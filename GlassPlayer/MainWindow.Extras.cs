using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace GlassPlayer;

/// <summary>Faster starts (warm-up + preparing the next song), endless autoplay and Discord status.</summary>
public partial class MainWindow
{
    DiscordPresence? discord;
    DateTime lastDiscord;
    readonly Dictionary<string, (string Url, DateTime At)> preloaded = new();
    string? preloadedFor;
    bool fetchingMore, retriedDownload;

    void InitExtras()
    {
        if (cfg.DiscordOn) discord = new DiscordPresence(Config.Get("DISCORD_CLIENT_ID"));

        // warm up in the background so the first song doesn't wait for this
        var scProv = (SoundCloud)providers["SoundCloud"];
        _ = Task.Run(async () =>
        {
            try { await scProv.ClientIdAsync(); } catch { }
            try { await Dlp.EnsureAsync(); } catch { }
        });
    }

    static string PreKey(Track t) => t.Source + ":" + t.Id;

    /// <summary>A stream URL prepared shortly before it's needed (they expire, so only recent ones are used).</summary>
    string? TakePreloaded(Track t)
    {
        if (preloaded.Remove(PreKey(t), out var p) && (DateTime.UtcNow - p.At).TotalMinutes < 4) return p.Url;
        return null;
    }

    /// <summary>Called every tick: about 25 s before the end, prepare the next song so it starts instantly.</summary>
    void MaybePreloadNext(long pos, long len)
    {
        if (len <= 0 || len - pos > 25000 || nowPlaying == null || preloadedFor == nowPlaying.Id) return;
        preloadedFor = nowPlaying.Id;
        var next = queue.PeekNext();
        if (next == null)
        {
            if (cfg.AutoRadio && cfg.Autoplay && queue.Repeat == RepeatMode.Off) _ = AppendRelatedAsync(nowPlaying);
            return;
        }
        _ = PreloadAsync(next);
    }

    async Task PreloadAsync(Track next)
    {
        if (next.Source is "Spotify" or "Local") return;
        if (!providers.TryGetValue(next.Source, out var p)) return;
        try
        {
            var url = await p.ResolveAsync(next);
            if (url != null) preloaded[PreKey(next)] = (url, DateTime.UtcNow);
        }
        catch { }
    }

    /// <summary>Like SoundCloud: when the queue runs out, add suggested songs to it.</summary>
    async Task<bool> AppendRelatedAsync(Track seed)
    {
        if (fetchingMore) return false;
        fetchingMore = true;
        try
        {
            List<Track> more = seed.Source switch
            {
                "SoundCloud" => await ((SoundCloud)providers["SoundCloud"]).RelatedAsync(seed),
                "YouTube Music" => await ((YouTubeMusic)providers["YouTube Music"]).RelatedAsync(seed),
                _ => new List<Track>()
            };
            var known = new HashSet<string>(queue.Items.Select(PreKey));
            more = more.Where(x => known.Add(PreKey(x))).ToList();
            if (more.Count == 0) return false;
            queue.Add(more);
            Toast($"Up next: {more.Count} similar songs", "");
            if (queue.PeekNext() is { } n) _ = PreloadAsync(n);
            return true;
        }
        catch { return false; }
        finally { fetchingMore = false; }
    }

    /// <summary>If a YouTube stream is blocked, fall back to downloading the song once.</summary>
    async Task<bool> TryDownloadInsteadAsync()
    {
        if (nowPlaying is not { Source: "YouTube Music" } t || retriedDownload) return false;
        retriedDownload = true;
        Status.Text = "Stream didn't start, downloading instead…";
        try
        {
            var file = await ((YouTubeMusic)providers["YouTube Music"]).DownloadAsync(t);
            if (file == null || nowPlaying != t) return false;
            engine!.Open(file);
            playStart = DateTime.UtcNow;
            SetPlaying(true);
            Status.Text = "";
            return true;
        }
        catch (Exception ex) { Status.Text = ex.Message; return false; }
    }

    // ---- Discord ----
    (long Pos, long Len) CurrentTimes()
    {
        if (activeSp != null)
        {
            long p = rPlaying ? Math.Min(rProgress + (long)(DateTime.UtcNow - rStamp).TotalMilliseconds, rDuration) : rProgress;
            return (p, rDuration);
        }
        if (engine == null || nowPlaying == null) return (0, fallbackMs);
        return (Math.Max(0, engine.TimeMs), engine.LengthMs > 0 ? engine.LengthMs : fallbackMs);
    }

    void UpdateDiscord(bool force = true)
    {
        if (discord is not { Enabled: true }) return;
        if (!force && (DateTime.UtcNow - lastDiscord).TotalSeconds < 15) return; // Discord allows about one update per 15 s
        lastDiscord = DateTime.UtcNow;
        var (pos, len) = CurrentTimes();
        discord.Update(nowPlaying, playing, pos, len);
    }

    void SetDiscord(bool on)
    {
        cfg.DiscordOn = on;
        if (on && discord == null) { discord = new DiscordPresence(Config.Get("DISCORD_CLIENT_ID")); UpdateDiscord(); }
        else if (!on) { discord?.Dispose(); discord = null; }
    }
}
