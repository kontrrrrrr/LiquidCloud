using System.Collections.Generic;
using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Threading;
using LibVLCSharp.Shared;

namespace LiquidCloud;

/// <summary>
/// Plays songs. Uses VLC when it is ready (plays any format) and falls back to Windows' built-in player
/// if VLC is still loading, fails to start, or doesn't produce sound. Problems are logged to %LocalAppData%\LiquidCloud\audio.log.
/// </summary>
class AudioEngine : IAudioOutput, IDisposable
{
    readonly Dispatcher ui;
    LibVLC? lib;
    LibVLCSharp.Shared.MediaPlayer? vlc;
    System.Windows.Media.MediaPlayer? wpf;
    volatile bool vlcReady, usingVlc, paused;
    string? current;
    volatile int openId;
    double volume;

    public event Action? Ended;
    public event Action<string>? Failed;

    public AudioEngine(Dispatcher ui, double volume)
    {
        this.ui = ui;
        this.volume = volume;
        Task.Run(() => // VLC can take a while to load the first time, so never block the window on it
        {
            try
            {
                Core.Initialize();
                var l = new LibVLC("--no-video", "--quiet", "--no-stats", "--no-osd", "--no-snapshot-preview", "--no-sub-autodetect-file", "--no-media-library");
                l.Log += (_, e) => { if (e.Level == LogLevel.Error) VlcError(e.Message); };
                var p = new LibVLCSharp.Shared.MediaPlayer(l);
                // VLC raises these on its own thread; remember which song they belong to, so a late "ended" from the
                // previous song can never skip the one you just picked
                p.EndReached += (_, _) => { int id = playingId; ui.InvokeAsync(() => { if (usingVlc && id == openId) Ended?.Invoke(); }); };
                p.EncounteredError += (_, _) => { int id = playingId; ui.InvokeAsync(() => { if (usingVlc && id == openId) Fallback("VLC reported an error"); }); };
                ui.InvokeAsync(() =>
                {
                    lib = l; vlc = p;
                    p.Volume = (int)Math.Round(this.volume * 100);
                    vlcReady = true;
                });
            }
            catch (Exception ex) { Log("VLC failed to start: " + ex); }
        });
    }

    // ---- VLC commands run one after another on a background thread ----
    // Stop() and opening a network stream block for up to seconds (VLC waits for its network thread);
    // doing that on the window's thread is what made switching songs stutter.
    readonly object chainGate = new();
    Task chain = Task.CompletedTask;
    volatile int playingId;          // the open request VLC is actually playing
    long queuedSeek = -1;            // only the latest seek matters

    void Post(Action work, string what)
    {
        lock (chainGate)
            chain = chain.ContinueWith(_ =>
            {
                try { work(); }
                catch (Exception ex) when (ex is not OutOfMemoryException) { LiquidCloud.Log.Warn("Audio", what + " failed", ex); }
            }, CancellationToken.None, TaskContinuationOptions.None, TaskScheduler.Default);
    }

    static void Log(string msg) => LiquidCloud.Log.Info("Audio", msg);

    // VLC repeats the same error many times a second (e.g. no sound device): log each distinct message at most once a minute
    static readonly Dictionary<string, DateTime> vlcSeen = new();
    static void VlcError(string msg)
    {
        lock (vlcSeen)
        {
            if (vlcSeen.TryGetValue(msg, out var last) && DateTime.UtcNow - last < TimeSpan.FromMinutes(1)) return;
            vlcSeen[msg] = DateTime.UtcNow;
        }
        LiquidCloud.Log.Warn("Audio", "VLC: " + msg);
    }

    /// <summary>Releases VLC (native memory and audio device) when the app closes.</summary>
    public void Dispose()
    {
        openId++;
        var p = vlc; var l = lib;
        vlc = null; lib = null; vlcReady = false;
        Post(() => { p?.Stop(); p?.Dispose(); l?.Dispose(); }, "Dispose");
        Task last;
        lock (chainGate) last = chain;
        if (!last.Wait(TimeSpan.FromSeconds(3))) Log("VLC took too long to close");
        wpf?.Close();
    }

    public void Open(string pathOrUrl)
    {
        current = pathOrUrl;
        paused = false;
        int id = ++openId;
        Interlocked.Exchange(ref queuedSeek, -1);
        wpf?.Stop();
        if (vlcReady && vlc != null && lib != null)
        {
            usingVlc = true;
            var p = vlc; var l = lib;
            bool http = pathOrUrl.StartsWith("http", StringComparison.OrdinalIgnoreCase);
            Post(() =>
            {
                if (id != openId) return; // you already picked another song: skip this one entirely
                p.Stop();
                using var media = new Media(l, pathOrUrl, http ? FromType.FromLocation : FromType.FromPath);
                if (http) media.AddOption(":network-caching=1500");
                playingId = id;
                if (!p.Play(media)) ui.InvokeAsync(() => { if (id == openId) Fallback("VLC refused to play"); });
            }, "Open");
            var t = new DispatcherTimer(DispatcherPriority.Normal, ui) { Interval = TimeSpan.FromSeconds(http ? 12 : 6) };
            t.Tick += (_, _) =>
            {
                t.Stop();
                if (id == openId && usingVlc && !paused && vlc != null && !vlc.IsPlaying && vlc.Time <= 0) Fallback("VLC did not start playing");
            };
            t.Start();
        }
        else PlayWpf(pathOrUrl);
    }

    void Fallback(string why)
    {
        Log("Using Windows player instead: " + why);
        openId++; // whatever VLC was doing no longer counts
        var p = vlc;
        Post(() => p?.Stop(), "Stop for fallback");
        if (current != null) PlayWpf(current);
    }

    void PlayWpf(string path)
    {
        usingVlc = false;
        if (wpf == null)
        {
            wpf = new System.Windows.Media.MediaPlayer();
            wpf.MediaEnded += (_, _) => { if (!usingVlc) Ended?.Invoke(); };
            wpf.MediaFailed += (_, e) => { Log("Windows player failed: " + e.ErrorException.Message); Failed?.Invoke(LiquidCloud.Loc.T("Couldn't play this track: {0}", e.ErrorException.Message)); };
        }
        wpf.Volume = volume;
        wpf.Open(new Uri(path));
        wpf.Play();
    }

    public void SetPaused(bool p)
    {
        paused = p;
        if (usingVlc) { var v = vlc; int id = openId; Post(() => { if (id == openId) v?.SetPause(p); }, "Pause"); }
        else if (p) wpf?.Pause();
        else wpf?.Play();
    }

    public void Stop()
    {
        openId++;
        paused = false;
        var p = vlc;
        Post(() => p?.Stop(), "Stop");
        wpf?.Stop();
    }

    /// <summary>True while VLC is still switching to the song that was asked for last.</summary>
    bool Switching => usingVlc && playingId != openId;

    public long TimeMs
    {
        get => usingVlc ? (Switching ? 0 : vlc?.Time ?? 0) : (long)(wpf?.Position.TotalMilliseconds ?? 0);
        set
        {
            if (usingVlc)
            {
                // many seeks in a row (dragging, holding an arrow key) collapse into one: the last one
                if (Interlocked.Exchange(ref queuedSeek, value) >= 0) return;
                var p = vlc; int id = openId;
                Post(() =>
                {
                    long ms = Interlocked.Exchange(ref queuedSeek, -1);
                    if (ms < 0 || id != openId || p == null) return;
                    if (!p.IsSeekable) Log("VLC says this stream isn't seekable; trying anyway");
                    p.Time = ms;
                }, "Seek");
            }
            else if (wpf != null) wpf.Position = TimeSpan.FromMilliseconds(value);
        }
    }

    public long LengthMs => usingVlc
        ? (Switching ? 0 : vlc?.Length ?? 0)
        : (wpf != null && wpf.NaturalDuration.HasTimeSpan ? (long)wpf.NaturalDuration.TimeSpan.TotalMilliseconds : 0);

    public double Volume
    {
        get => volume;
        set
        {
            volume = value;
            if (vlc != null) vlc.Volume = (int)Math.Round(value * 100);
            if (wpf != null) wpf.Volume = value;
        }
    }
}
