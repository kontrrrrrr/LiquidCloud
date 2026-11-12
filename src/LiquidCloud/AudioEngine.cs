using System.Collections.Generic;
using System;
using System.IO;
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
    bool vlcReady, usingVlc, paused;
    string? current;
    int openId;
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
                p.EndReached += (_, _) => ui.InvokeAsync(() => { if (usingVlc) Ended?.Invoke(); });
                p.EncounteredError += (_, _) => ui.InvokeAsync(() => { if (usingVlc) Fallback("VLC reported an error"); });
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
        try { vlc?.Stop(); vlc?.Dispose(); lib?.Dispose(); }
        catch (Exception ex) { LiquidCloud.Log.Warn("Audio", "Dispose", ex); }
        vlc = null; lib = null;
        wpf?.Close();
    }

    public void Open(string pathOrUrl)
    {
        current = pathOrUrl;
        paused = false;
        int id = ++openId;
        StopCurrent();
        if (vlcReady && vlc != null && lib != null)
        {
            usingVlc = true;
            var type = pathOrUrl.StartsWith("http", StringComparison.OrdinalIgnoreCase) ? FromType.FromLocation : FromType.FromPath;
            using var media = new Media(lib, pathOrUrl, type);
            if (!vlc.Play(media)) { Fallback("VLC refused to play"); return; }
            var t = new DispatcherTimer(DispatcherPriority.Normal, ui) { Interval = TimeSpan.FromSeconds(pathOrUrl.StartsWith("http", StringComparison.OrdinalIgnoreCase) ? 12 : 6) };
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
        try { vlc?.Stop(); }
        catch (Exception ex) { Log("VLC stop failed: " + ex.Message); }
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

    void StopCurrent()
    {
        try { vlc?.Stop(); }
        catch (Exception ex) { Log("Stop failed: " + ex.Message); }
        wpf?.Stop();
    }

    public void SetPaused(bool p)
    {
        paused = p;
        if (usingVlc) vlc?.SetPause(p);
        else if (p) wpf?.Pause();
        else wpf?.Play();
    }

    public void Stop() { openId++; paused = false; StopCurrent(); }

    public long TimeMs
    {
        get => usingVlc ? (vlc?.Time ?? 0) : (long)(wpf?.Position.TotalMilliseconds ?? 0);
        set
        {
            if (usingVlc) { if (vlc != null) vlc.Time = value; }
            else if (wpf != null) wpf.Position = TimeSpan.FromMilliseconds(value);
        }
    }

    public long LengthMs => usingVlc
        ? (vlc?.Length ?? 0)
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
