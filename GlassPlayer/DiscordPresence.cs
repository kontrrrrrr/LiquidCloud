using System;
using DiscordRPC;

namespace GlassPlayer;

/// <summary>
/// "Listening to LiquidCloud" in your Discord profile: song, artist, cover, a live 0:07 / 3:27 bar
/// and how long you've been enjoying the app. Needs DISCORD_CLIENT_ID in config.json (see README).
/// </summary>
sealed class DiscordPresence : IDisposable
{
    readonly DiscordRpcClient? client;
    readonly DateTime started = DateTime.UtcNow;

    public DiscordPresence(string? appId)
    {
        if (string.IsNullOrWhiteSpace(appId)) return;
        try
        {
            client = new DiscordRpcClient(appId.Trim());
            client.Initialize();
        }
        catch { client = null; }
    }

    public bool Enabled => client != null;

    static string Fit(string s)
    {
        s = (s ?? "").Trim();
        if (s.Length < 2) s = s.PadRight(2, '.'); // Discord rejects texts shorter than 2 characters
        return s.Length > 127 ? s[..126] + "…" : s;
    }

    public string Enjoying
    {
        get
        {
            var e = DateTime.UtcNow - started;
            return $"enjoying for {(int)e.TotalHours:00}:{e.Minutes:00}:{e.Seconds:00}";
        }
    }

    public void Update(Track? t, bool playing, long posMs, long durMs)
    {
        if (client == null) return;
        try
        {
            if (t == null) { client.ClearPresence(); return; }
            string art = t.Art != null && t.Art.StartsWith("http") && t.Art.Length <= 256 ? t.Art : "logo";
            var p = new RichPresence
            {
                Details = Fit(t.Title),
                State = Fit(playing ? "by " + t.Artist : "Paused · " + t.Artist),
                Assets = new Assets
                {
                    LargeImageKey = art,
                    LargeImageText = Fit($"{Time(posMs)} / {Time(durMs)} · {Enjoying}"),
                    SmallImageKey = "logo",
                    SmallImageText = Fit("LiquidCloud · " + Enjoying)
                }
            };
            if (playing && durMs > 0)
            {
                // Discord draws the live 0:07 / 3:27 progress bar from these two times
                var start = DateTime.UtcNow - TimeSpan.FromMilliseconds(posMs);
                p.Timestamps = new Timestamps(start, start + TimeSpan.FromMilliseconds(durMs));
            }
            SetListening(p);
            client.SetPresence(p);
        }
        catch { }
    }

    /// <summary>
    /// "Listening to" instead of "Playing". Newer versions of the Discord library have this property, older ones don't,
    /// so it's set only if it exists (otherwise Discord shows "Playing LiquidCloud").
    /// </summary>
    static void SetListening(RichPresence p)
    {
        try
        {
            var prop = p.GetType().GetProperty("Type");
            if (prop != null && prop.PropertyType.IsEnum && Enum.TryParse(prop.PropertyType, "Listening", out var v))
                prop.SetValue(p, v);
        }
        catch { }
    }

    static string Time(long ms) => ms <= 0 ? "0:00" : $"{ms / 60000}:{ms / 1000 % 60:00}";

    public void Dispose()
    {
        try { client?.ClearPresence(); client?.Dispose(); } catch { }
    }
}
