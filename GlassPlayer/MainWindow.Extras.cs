using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace GlassPlayer;

/// <summary>Faster starts (warm-up + preloading the next song), endless autoplay and Discord status.</summary>
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

    /// <summary>A stream URL resolved shortly before it's needed (they expire, so only recent ones are used).</summary>
    string? TakePreloaded(Track t)
    {
        if (preloaded.Remove(PreKey(t), out var p) && (DateTime.UtcNow - p.At).TotalMinutes < 4) return p.Url;
        return null;
    }

    /// <summary>Called every tick: ~25 s before the end, resolve the next song so it starts instantly.</summary>
    void MaybePreloadNext(long pos, long len)
    {
        if (len <= 0 || len - pos > 25000 || nowPlaying == null || preloadedFor == nowPlaying.Id) return;
        preloadedFor = nowPlaying.Id;
        int i = List.SelectedIndex;
        if (i < 0) return;
        if (i + 1 >= List.Items.Count)
        {
            if (cfg.AutoRadio && cfg.Autoplay) _ = AppendRelatedAsync(nowPlaying, false);
            return;
        }
        if (List.Items[i + 1] is Track next) _ = PreloadAsync(next);
    }

    async Task PreloadAsync(Track next)
    {
        if (next.Source is "Spotify" or "Local" || next.Source == ScAccount.PlaylistSource) return;
        if (!providers.TryGetValue(next.Source, out var p)) return;
        try
        {
            var url = await p.ResolveAsync(next);
            if (url != null) preloaded[PreKey(next)] = (url, DateTime.UtcNow);
        }
        catch { }
    }

    /// <summary>Like SoundCloud: when the list runs out, keep going with suggested songs.</summary>
    async Task<bool> AppendRelatedAsync(Track seed, bool playNow)
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
            var cur = (List.ItemsSource as IEnumerable<Track>)?.ToList() ?? new List<Track>();
            var known = new HashSet<string>(cur.Select(x => x.Id));
            more = more.Where(x => known.Add(x.Id)).ToList();
            if (more.Count == 0) return false;
            int sel = List.SelectedIndex;
            if (sel < 0) return false;
            cur.AddRange(more);
            updatingList = true;
            List.ItemsSource = cur;
            List.SelectedIndex = sel; // keep the current song selected without restarting it
            updatingList = false;
            Status.Text = $"Up next: {more.Count} similar songs";
            if (playNow) List.SelectedIndex = sel + 1;
            else if (List.Items[sel + 1] is Track n) _ = PreloadAsync(n);
            return true;
        }
        catch { return false; }
        finally { fetchingMore = false; }
    }

    // ---- Discord ----
    (long Pos, long Len) CurrentTimes()
    {
        if (activeSp != null)
        {
            long p = rPlaying ? Math.Min(rProgress + (long)(DateTime.UtcNow - rStamp).TotalMilliseconds, rDuration) : rProgress;
            return (p, rDuration);
        }
        if (engine == null) return (0, fallbackMs);
        return (Math.Max(0, engine.TimeMs), engine.LengthMs > 0 ? engine.LengthMs : fallbackMs);
    }

    void UpdateDiscord(bool force = true)
    {
        if (discord is not { Enabled: true }) return;
        if (!force && (DateTime.UtcNow - lastDiscord).TotalSeconds < 15) return; // Discord allows ~1 update / 15 s
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
