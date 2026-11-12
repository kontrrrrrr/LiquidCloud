using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace LiquidCloud;

/// <summary>Faster starts (warm-up + preparing the next song), "keep playing similar songs", stream fallback and Discord status.</summary>
public partial class MainWindow
{
    DiscordPresence? discord;
    DateTime lastDiscord;
    string? preparedFor;
    CancellationTokenSource? prepareCts;

    void InitExtras()
    {
        if (cfg.DiscordOn && !AppEnv.Smoke) discord = new DiscordPresence(Config.Get("DISCORD_CLIENT_ID"));
        if (AppEnv.Offline) return;

        // warm up in the background so the first song doesn't wait for this
        var scProv = (SoundCloud)providers["SoundCloud"];
        Bg.Run(async () =>
        {
            await scProv.ClientIdAsync();
            await Dlp.EnsureAsync();
        }, "Startup", "Warm-up");
    }

    /// <summary>About 25 s before the end, resolve the next song in the background so it starts instantly.</summary>
    void MaybePrepareNext(long pos, long len)
    {
        if (len <= 0 || len - pos > 25000 || nowPlaying == null || preparedFor == nowPlaying.Key || !playing) return;
        preparedFor = nowPlaying.Key;
        if (queue.PeekNext() is not { } next) return;
        prepareCts?.Cancel();
        prepareCts = new CancellationTokenSource();
        Bg.Run(resolver.PrepareAsync(next, prepareCts.Token), "Player", "Prepare next song");
    }

    /// <summary>Songs to add when the queue runs out (SoundCloud / YouTube suggestions).</summary>
    async Task<IList<Track>> RelatedForAsync(Track seed, CancellationToken ct)
    {
        if (!cfg.AutoRadio || !cfg.Autoplay || AppEnv.Offline) return Array.Empty<Track>();
        return seed.Source switch
        {
            "SoundCloud" => await ((SoundCloud)providers["SoundCloud"]).RelatedAsync(seed).WaitAsync(ct),
            "YouTube Music" => await ((YouTubeMusic)providers["YouTube Music"]).RelatedAsync(seed).WaitAsync(ct),
            _ => Array.Empty<Track>()
        };
    }

    /// <summary>If a YouTube stream is blocked, download the song once instead.</summary>
    async Task<string?> FallbackForAsync(Track t, CancellationToken ct)
    {
        if (t.Source != "YouTube Music" || AppEnv.Offline) return null;
        return await ((YouTubeMusic)providers["YouTube Music"]).DownloadAsync(t).WaitAsync(ct);
    }

    // ---- Discord ----
    void UpdateDiscord(bool force = true)
    {
        if (discord is not { Enabled: true }) return;
        if (!force && (DateTime.UtcNow - lastDiscord).TotalSeconds < 15) return; // Discord allows about one update per 15 s
        lastDiscord = DateTime.UtcNow;
        var (pos, len) = player.Times;
        discord.Update(nowPlaying, playing, pos, len);
    }

    void SetDiscord(bool on)
    {
        cfg.DiscordOn = on;
        if (on && discord == null) { discord = new DiscordPresence(Config.Get("DISCORD_CLIENT_ID")); UpdateDiscord(); }
        else if (!on) { discord?.Dispose(); discord = null; }
    }
}
