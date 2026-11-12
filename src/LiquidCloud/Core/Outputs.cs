using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Threading;

namespace LiquidCloud;

/// <summary>Spotify Connect as an audio output: commands go to your Spotify device, progress is polled and interpolated.</summary>
sealed class SpotifyOutput : IAudioOutput
{
    readonly Spotify sp;
    readonly DispatcherTimer poll = new() { Interval = TimeSpan.FromMilliseconds(1500) };
    readonly DispatcherTimer volumeDebounce = new() { Interval = TimeSpan.FromMilliseconds(300) };
    string? uri;
    bool playing, endedFired, polling;
    int progress, duration;
    DateTime stamp;
    double volume = .8;

    public event Action? Ended;
    public event Action<string>? Failed;
    public string? Device { get; private set; }

    public SpotifyOutput(Spotify sp)
    {
        this.sp = sp;
        poll.Tick += async (_, _) => await PollAsync();
        volumeDebounce.Tick += async (_, _) =>
        {
            volumeDebounce.Stop();
            await Safe(() => sp.VolumeAsync((int)(volume * 100)), "volume");
        };
    }

    async Task Safe(Func<Task> a, string what)
    {
        try { await a(); }
        catch (Exception ex) { Log.Warn("Spotify", what, ex); }
    }

    public async void Open(string url) // event-like entry point: errors are reported through Failed, never thrown
    {
        uri = url;
        playing = true; endedFired = false; progress = 0; duration = 0; stamp = DateTime.UtcNow;
        try
        {
            Device = await sp.PlayAsync(url);
            poll.Start();
            await Safe(() => sp.VolumeAsync((int)(volume * 100)), "volume");
        }
        catch (Exception ex)
        {
            Log.Warn("Spotify", "Play failed", ex);
            playing = false;
            Failed?.Invoke(ex.Message);
        }
    }

    async Task PollAsync()
    {
        if (polling || uri == null) return;
        polling = true;
        try
        {
            if (await sp.StateAsync() is { } st && st.Uri == uri)
            {
                playing = st.Playing; progress = st.Progress; duration = st.Duration; stamp = DateTime.UtcNow;
            }
            if (playing && duration > 0 && TimeMs >= duration - 1500 && !endedFired)
            {
                endedFired = true;
                Ended?.Invoke();
            }
        }
        catch (Exception ex) { Log.Warn("Spotify", "State poll", ex); }
        finally { polling = false; }
    }

    public void Stop()
    {
        poll.Stop();
        if (uri != null && playing) Bg.Run(Safe(sp.PauseAsync, "pause"), "Spotify", "pause");
        uri = null; playing = false;
    }

    public void SetPaused(bool paused)
    {
        progress = (int)TimeMs; stamp = DateTime.UtcNow; playing = !paused;
        Bg.Run(Safe(() => paused ? sp.PauseAsync() : sp.ResumeAsync(), paused ? "pause" : "resume"), "Spotify", "pause/resume");
    }

    public long TimeMs
    {
        get => playing ? Math.Min(progress + (long)(DateTime.UtcNow - stamp).TotalMilliseconds, duration > 0 ? duration : long.MaxValue) : progress;
        set
        {
            progress = (int)value; stamp = DateTime.UtcNow; endedFired = false;
            Bg.Run(Safe(() => sp.SeekAsync((int)value), "seek"), "Spotify", "seek");
        }
    }

    public long LengthMs => duration;

    public double Volume
    {
        get => volume;
        set { volume = value; volumeDebounce.Stop(); volumeDebounce.Start(); }
    }
}

/// <summary>Sends Spotify URIs to Spotify and everything else (streams, files) to the built-in engine.</summary>
sealed class RoutingOutput : IAudioOutput
{
    readonly IAudioOutput local;
    readonly IAudioOutput? spotify;
    IAudioOutput active;

    public event Action? Ended;
    public event Action<string>? Failed;

    public RoutingOutput(IAudioOutput local, IAudioOutput? spotify)
    {
        this.local = local;
        this.spotify = spotify;
        active = local;
        local.Ended += () => { if (active == local) Ended?.Invoke(); };
        local.Failed += m => { if (active == local) Failed?.Invoke(m); };
        if (spotify != null)
        {
            spotify.Ended += () => { if (active == spotify) Ended?.Invoke(); };
            spotify.Failed += m => { if (active == spotify) Failed?.Invoke(m); };
        }
    }

    public bool OnSpotify => active == spotify;

    public void Open(string url)
    {
        var target = url.StartsWith("spotify:", StringComparison.OrdinalIgnoreCase) && spotify != null ? spotify : local;
        if (target != active) active.Stop();
        active = target;
        active.Open(url);
    }

    public void Stop() => active.Stop();
    public void SetPaused(bool paused) => active.SetPaused(paused);
    public long TimeMs { get => active.TimeMs; set => active.TimeMs = value; }
    public long LengthMs => active.LengthMs;

    public double Volume
    {
        get => active.Volume;
        set { local.Volume = value; if (spotify != null) spotify.Volume = value; }
    }
}

/// <summary>
/// Finds a playable URL for a track: songs prepared in advance are used first; otherwise the track's service is asked.
/// Respects offline mode and cancellation (switching songs cancels the previous lookup).
/// </summary>
sealed class AppResolver : ITrackResolver
{
    readonly IReadOnlyDictionary<string, IProvider> providers;
    readonly Dictionary<string, (string Url, DateTime At)> prepared = new();
    readonly object gate = new();

    public AppResolver(IReadOnlyDictionary<string, IProvider> providers) => this.providers = providers;

    public async Task<string?> ResolveAsync(Track t, CancellationToken ct)
    {
        if (t.Source == "Spotify") return t.OpenUri;
        lock (gate)
        {
            if (prepared.Remove(t.Key, out var p) && (DateTime.UtcNow - p.At).TotalMinutes < 4) return p.Url;
        }
        if (t.Source != "Local") AppEnv.EnsureOnline();
        if (!providers.TryGetValue(t.Source, out var prov)) throw new InvalidOperationException(Loc.T("Unknown source: {0}", t.Source));
        return await prov.ResolveAsync(t).WaitAsync(TimeSpan.FromSeconds(75), ct);
    }

    /// <summary>Resolve the next song in the background so it starts instantly.</summary>
    public async Task PrepareAsync(Track t, CancellationToken ct)
    {
        if (t.Source is "Spotify" or "Local" || AppEnv.Offline || !providers.TryGetValue(t.Source, out var prov)) return;
        var url = await prov.ResolveAsync(t).WaitAsync(TimeSpan.FromSeconds(75), ct);
        if (url != null) lock (gate) prepared[t.Key] = (url, DateTime.UtcNow);
    }
}
