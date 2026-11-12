using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace LiquidCloud;

/// <summary>Something that can play a resolved stream / file / Spotify URI.</summary>
public interface IAudioOutput
{
    /// <summary>The song reached its end.</summary>
    event Action? Ended;
    /// <summary>Playback couldn't start or broke off.</summary>
    event Action<string>? Failed;
    void Open(string url);
    void Stop();
    void SetPaused(bool paused);
    long TimeMs { get; set; }
    long LengthMs { get; }
    double Volume { get; set; }
}

/// <summary>Turns a track into something IAudioOutput can play (stream URL, file path, Spotify URI).</summary>
public interface ITrackResolver
{
    Task<string?> ResolveAsync(Track track, CancellationToken ct);
}

public enum PlayState { Idle, Loading, Playing, Paused, Failed }

/// <summary>
/// All playback logic, independent of the window: the queue, play/pause, next/previous, shuffle/repeat, volume/mute,
/// "keep playing similar songs", skipping broken tracks, and protection against stale results when you switch songs quickly
/// (every play request gets a version; an older request that finishes late is ignored and its network work is cancelled).
/// </summary>
public sealed class PlayerService
{
    readonly IAudioOutput audio;
    readonly ITrackResolver resolver;
    readonly Func<DateTime> now;

    int version;
    CancellationTokenSource? playCts;
    DateTime startedAt;
    bool triedFallback;
    long pendingSeekMs;
    int failuresInARow;
    double volumeBeforeMute = 0.8;

    public PlayerService(IAudioOutput audio, ITrackResolver resolver, Func<DateTime>? clock = null)
    {
        this.audio = audio;
        this.resolver = resolver;
        now = clock ?? (() => DateTime.UtcNow);
        audio.Ended += OnEnded;
        audio.Failed += msg => Fire(HandleFailureAsync(msg), "playback failure");
    }

    public PlayQueue Queue { get; } = new();
    public Track? Current { get; private set; }
    public PlayState State { get; private set; }
    /// <summary>Last problem shown to the user (null when everything's fine).</summary>
    public string? Message { get; private set; }
    public bool Muted { get; private set; }

    /// <summary>Move on when a song ends (off = stop after each song).</summary>
    public bool AutoAdvance { get; set; } = true;
    /// <summary>When the queue runs out, ask Related for similar songs and keep going.</summary>
    public bool AutoRadio { get; set; } = true;
    public Func<Track, CancellationToken, Task<IList<Track>>>? Related { get; set; }
    /// <summary>Another way to get a song when its stream fails (e.g. download instead of stream). Tried once per song.</summary>
    public Func<Track, CancellationToken, Task<string?>>? Fallback { get; set; }
    /// <summary>Pause before skipping a song that can't be played (0 in tests).</summary>
    public TimeSpan SkipDelay { get; set; } = TimeSpan.FromMilliseconds(900);

    public event Action? TrackChanged;
    public event Action? StateChanged;
    /// <summary>A track started playing from this URL / path (used for lyrics and covers from local files).</summary>
    public event Action<Track, string>? Started;
    /// <summary>Something the user should know (a skipped song, end of queue…).</summary>
    public event Action<string>? Notice;
    public event Action<int>? RelatedAdded;

    public bool IsPlaying => State == PlayState.Playing;

    public (long Position, long Length) Times
    {
        get
        {
            if (Current == null) return (0, 0);
            long len = audio.LengthMs > 0 ? audio.LengthMs : Current.DurationMs;
            return (Math.Max(0, pendingSeekMs > 0 ? pendingSeekMs : audio.TimeMs), len);
        }
    }

    void SetState(PlayState s, string? message = null)
    {
        State = s;
        Message = message;
        StateChanged?.Invoke();
    }

    static void Fire(Task t, string what) => t.ContinueWith(x =>
    {
        var ex = x.Exception?.GetBaseException();
        if (ex is not OperationCanceledException) Log.Warn("Player", what + " failed", ex);
    }, TaskContinuationOptions.OnlyOnFaulted);

    // ---------------- playing ----------------

    /// <summary>The list becomes the queue, starting at index.</summary>
    public Task PlayContextAsync(IList<Track> tracks, int index, string name, bool shuffle = false)
    {
        if (tracks.Count == 0) return Task.CompletedTask;
        failuresInARow = 0;
        if (shuffle) Queue.SetShuffle(true);
        Queue.Set(tracks, Math.Clamp(index, 0, tracks.Count - 1), name);
        return PlayAsync(Queue.Current!);
    }

    /// <summary>Play one song right now and keep the rest of the queue after it.</summary>
    public Task PlayNowAsync(Track t)
    {
        if (Queue.Count == 0) return PlayContextAsync(new List<Track> { t }, 0, t.Title);
        Queue.PlayNext(t);
        var n = Queue.Next(false);
        return n != null ? PlayAsync(n) : Task.CompletedTask;
    }

    /// <summary>Starts a track. If another request comes in meanwhile, this one is cancelled and ignored.</summary>
    public async Task PlayAsync(Track t, long startAtMs = 0)
    {
        int v = ++version;
        playCts?.Cancel();
        playCts?.Dispose();
        playCts = new CancellationTokenSource();
        var ct = playCts.Token;

        Current = t;
        triedFallback = false;
        pendingSeekMs = startAtMs;
        SetState(PlayState.Loading);
        TrackChanged?.Invoke();

        string? url;
        try { url = await resolver.ResolveAsync(t, ct); }
        catch (OperationCanceledException) { return; }
        catch (Exception ex)
        {
            if (v != version) return;
            Log.Warn("Player", $"Couldn't resolve \"{t.Title}\" ({t.Source})", ex);
            await SkipAfterErrorAsync(v, ex.Message);
            return;
        }
        if (v != version) return; // you picked another song while this one was loading
        if (url == null) { await SkipAfterErrorAsync(v, Loc.T("This track can't be streamed.")); return; }
        StartUrl(t, url);
    }

    void StartUrl(Track t, string url)
    {
        audio.Open(url);
        startedAt = now();
        SetState(PlayState.Playing);
        Started?.Invoke(t, url);
    }

    /// <summary>Call regularly (the UI timer does): applies a pending "continue where you left off" seek once the media is ready.</summary>
    public void Tick()
    {
        if (pendingSeekMs > 0 && State == PlayState.Playing && audio.LengthMs > 0)
        {
            audio.TimeMs = Math.Min(pendingSeekMs, Math.Max(0, audio.LengthMs - 1000));
            pendingSeekMs = 0;
        }
    }

    async Task SkipAfterErrorAsync(int v, string message)
    {
        SetState(PlayState.Failed, message);
        Notice?.Invoke(message);
        if (!AutoAdvance || ++failuresInARow > 5) return; // don't spin through a whole broken playlist
        if (SkipDelay > TimeSpan.Zero) await Task.Delay(SkipDelay);
        if (v != version || Queue.PeekNext() == null) return;
        await NextAsync(true);
    }

    void OnEnded()
    {
        if (State != PlayState.Playing) return;
        if (now() - startedAt < TimeSpan.FromSeconds(3))
        {
            // "ended" right after starting means the stream didn't really play
            Fire(HandleFailureAsync(Loc.T("Couldn't play this track.")), "early end");
            return;
        }
        failuresInARow = 0;
        if (!AutoAdvance && Queue.Repeat != RepeatMode.One) { SetState(PlayState.Idle); return; }
        Fire(NextAsync(true), "auto next");
    }

    async Task HandleFailureAsync(string message)
    {
        int v = version;
        var t = Current;
        if (t != null && Fallback != null && !triedFallback)
        {
            triedFallback = true;
            Notice?.Invoke(Loc.T("Stream didn't start, trying another way…"));
            try
            {
                var url = await Fallback(t, playCts?.Token ?? CancellationToken.None);
                if (v == version && url != null) { StartUrl(t, url); return; }
            }
            catch (OperationCanceledException) { return; }
            catch (Exception ex) { Log.Warn("Player", "Fallback failed", ex); message = ex.Message; }
        }
        if (v == version) await SkipAfterErrorAsync(v, message);
    }

    // ---------------- transport ----------------

    /// <summary>Next song. auto = the previous one ended by itself.</summary>
    public async Task NextAsync(bool auto = false)
    {
        if (!auto) failuresInARow = 0;
        var next = Queue.Next(auto);
        if (next == null && AutoRadio && Related != null && Queue.Current is { } last)
        {
            try
            {
                var more = await Related(last, playCts?.Token ?? CancellationToken.None);
                var known = new HashSet<string>(Queue.Items.Select(x => x.Key));
                var fresh = more.Where(x => known.Add(x.Key)).ToList();
                if (fresh.Count > 0)
                {
                    Queue.Add(fresh);
                    RelatedAdded?.Invoke(fresh.Count);
                    next = Queue.Next(false);
                }
            }
            catch (OperationCanceledException) { return; }
            catch (Exception ex) { Log.Warn("Player", "Couldn't load similar songs", ex); }
        }
        if (next == null)
        {
            if (auto)
            {
                audio.Stop();
                SetState(PlayState.Idle, Loc.T("End of the queue"));
                Notice?.Invoke(Loc.T("End of the queue"));
            }
            return;
        }
        await PlayAsync(next);
    }

    /// <summary>Restarts the song if it's been playing a few seconds, otherwise goes to the previous one.</summary>
    public async Task PreviousAsync()
    {
        if (Current != null && State is PlayState.Playing or PlayState.Paused && audio.TimeMs > 4000)
        {
            audio.TimeMs = 0;
            return;
        }
        var prev = Queue.Previous();
        if (prev != null) await PlayAsync(prev);
    }

    public async Task JumpToAsync(int playOrderPosition)
    {
        failuresInARow = 0;
        var t = Queue.JumpTo(playOrderPosition);
        if (t != null) await PlayAsync(t);
    }

    /// <summary>Play/pause. With nothing loaded yet (restored session), starts the current queue song.</summary>
    public async Task TogglePlayAsync(long resumeAtMs = 0)
    {
        switch (State)
        {
            case PlayState.Playing: audio.SetPaused(true); SetState(PlayState.Paused); break;
            case PlayState.Paused: audio.SetPaused(false); SetState(PlayState.Playing); break;
            case PlayState.Loading: break;
            default:
                if (Queue.Current is { } c) await PlayAsync(c, resumeAtMs);
                break;
        }
    }

    public void Seek(long ms)
    {
        if (Current == null) return;
        var (_, len) = Times;
        pendingSeekMs = 0;
        audio.TimeMs = Math.Clamp(ms, 0, len > 0 ? Math.Max(0, len - 500) : long.MaxValue);
    }

    public void SeekBy(long deltaMs) => Seek(Times.Position + deltaMs);

    public double Volume
    {
        get => audio.Volume;
        set
        {
            audio.Volume = Math.Clamp(value, 0, 1);
            if (value > 0) Muted = false;
        }
    }

    public void ToggleMute()
    {
        if (!Muted) { volumeBeforeMute = Math.Max(audio.Volume, 0.05); audio.Volume = 0; Muted = true; }
        else { audio.Volume = volumeBeforeMute; Muted = false; }
    }

    public void SetShuffle(bool on) => Queue.SetShuffle(on);

    public RepeatMode CycleRepeat()
    {
        Queue.Repeat = Queue.Repeat switch { RepeatMode.Off => RepeatMode.All, RepeatMode.All => RepeatMode.One, _ => RepeatMode.Off };
        return Queue.Repeat;
    }

    public void Stop()
    {
        version++;
        playCts?.Cancel();
        audio.Stop();
        SetState(PlayState.Idle);
    }
}
