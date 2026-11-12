using System;
using System.IO;
using System.Text.Json;

namespace GlassPlayer;

class AppSettings
{
    // playback
    public bool Autoplay { get; set; } = true;
    public bool LaunchSpotify { get; set; } = true;
    public bool RememberVolume { get; set; } = true;
    public bool AutoRadio { get; set; } = true;       // keep playing suggested songs when the list ends
    public bool DiscordOn { get; set; } = true;
    public double Volume { get; set; } = 0.8;

    // look
    public string Theme { get; set; } = ThemeStore.DefaultId; // id of the active theme
    public bool AutoAccent { get; set; } = false;     // take button colors from the background image
    public string? Background { get; set; }           // png / jpg / jpeg / gif copied into the app folder
    public double BgDim { get; set; } = 0.25;
    public double BgBlur { get; set; } = 0;
    public bool AmbientGlow { get; set; } = true;
    public double AnimSpeed { get; set; } = 1.0;
    public bool ReduceMotion { get; set; } = false;
    public bool ShowWelcome { get; set; } = true;
    public bool LikeOnSoundCloud { get; set; } = true;  // favorites of SoundCloud songs also like them on SoundCloud
    public string? LastUserName { get; set; }          // shown on the welcome screen

    // particles
    public bool ParticlesOn { get; set; } = true;
    public string ParticleShape { get; set; } = "Dots";
    public int ParticleCount { get; set; } = 40;
    public double ParticleSize { get; set; } = 2.5;
    public double ParticleSpeed { get; set; } = 1.0;
    public bool ParticleAccent { get; set; } = false;

    // lyrics
    public double LyricsSize { get; set; } = 22;

    // SoundCloud browser
    public bool CatchScPlay { get; set; } = true;
    public bool KeepScLoaded { get; set; } = false;

    // performance
    public bool PauseWhenInactive { get; set; } = true;

    public static readonly string Dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "LiquidCloud");
    static readonly string File_ = Path.Combine(Dir, "settings.json");

    public static AppSettings Load()
    {
        try { return JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(File_)) ?? new(); }
        catch { return new(); }
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(Dir);
            File.WriteAllText(File_, JsonSerializer.Serialize(this));
        }
        catch { }
    }
}
