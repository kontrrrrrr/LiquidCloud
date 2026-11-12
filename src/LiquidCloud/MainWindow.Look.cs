using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using XamlAnimatedGif;

namespace LiquidCloud;

/// <summary>The glass design system at runtime: applies a theme to every surface, plus backgrounds, particles and the settings sheet.</summary>
public partial class MainWindow
{
    Color? bgAccent;
    double bgLum;
    readonly List<AnimationClock> blobClocks = new();
    readonly DispatcherTimer saveTimer = new() { Interval = TimeSpan.FromMilliseconds(600) };
    bool lastLive = true;
    readonly DispatcherTimer idleTrim = new() { Interval = TimeSpan.FromSeconds(20) };
    bool idleTrimHooked;
    ThemeDef? previewTheme; // the theme editor shows changes live before you save
    string? activeThemeId;  // the theme in use right now (cfg.Theme is the default one used at startup)

    /// <summary>Theme tokens live in the application's resources so menus, tooltips and popups follow the theme too.</summary>
    static ResourceDictionary AppRes => Application.Current.Resources;

    ThemeDef CurrentTheme => previewTheme ?? ThemeStore.Find(activeThemeId ?? cfg.Theme);

    /// <summary>Level bars and the play-button pulse run only while music plays and LiquidCloud is in front.</summary>
    void UpdateLiveBits()
    {
        bool live = WindowState != WindowState.Minimized && (IsActive || !cfg.PauseWhenInactive);
        Bars.Running = playing && live && !expanded && !miniMode;
        UpdatePlayRing(live);
    }

    void InitLook()
    {
        if (!idleTrimHooked) { idleTrim.Tick += (_, _) => { idleTrim.Stop(); if (!IsActive) TrimMemory(); }; idleTrimHooked = true; }
        saveTimer.Tick += (_, _) => { saveTimer.Stop(); cfg.Save(); };
        ConfigureParticles();
        ApplyBackground();
    }

    void SaveSoon() { saveTimer.Stop(); saveTimer.Start(); }

    static SolidColorBrush Solid(Color c) { var b = new SolidColorBrush(c); b.Freeze(); return b; }
    static Color A(Color c, double alpha) => Themes.WithAlpha(c, alpha);

    static Brush Blob(Color c, double strength)
    {
        var b = new RadialGradientBrush(new GradientStopCollection
        {
            new GradientStop(A(c, .65 * strength + .1), 0),
            new GradientStop(A(c, 0), 1)
        });
        b.Freeze();
        return b;
    }

    static LinearGradientBrush Linear(Color a, Color b, double angle = 135)
    {
        double r = angle * Math.PI / 180;
        var start = new Point(.5 - Math.Cos(r) / 2, .5 - Math.Sin(r) / 2);
        var end = new Point(.5 + Math.Cos(r) / 2, .5 + Math.Sin(r) / 2);
        var g = new LinearGradientBrush(a, b, 0) { StartPoint = start, EndPoint = end };
        g.Freeze();
        return g;
    }

    /// <summary>Turns the active theme into the design tokens every control uses (colors, glass layers, radii, shadows).</summary>
    void ApplyLook()
    {
        var th = CurrentTheme;
        var (tokens, colors) = ThemeService.Build(th, cfg.AutoAccent ? bgAccent : null);
        ThemeService.Apply(AppRes, tokens);
        var accent = colors.Accent;
        var ink = colors.Ink;
        bool lightUi = colors.LightUi;

        // "blur" softens everything behind the glass (wallpaper and light blobs)
        bool hasBg = BgImage.Visibility == Visibility.Visible;
        BgBlurFx.Radius = Math.Max(cfg.BgBlur, th.Blur);
        Ambient.Effect = th.Blur > 0.5 ? new BlurEffect { Radius = th.Blur * 2, KernelType = KernelType.Gaussian } : null;
        double autoDim = hasBg ? Math.Clamp((bgLum - .45) * 1.2, 0, .5) : 0;
        BgDim.Opacity = hasBg ? Math.Max(cfg.BgDim, autoDim) : 0;

        Particles.SetColor(cfg.ParticleAccent ? accent : (lightUi ? A(ink, .6) : Colors.White));
        Bars.SetBrush(Solid(A(accent, .85)));
        if (!cfg.AmbientGlow) Glow.Source = null;
        else if (Glow.Source == null && ArtBrush.ImageSource != null) { Glow.Source = ArtBrush.ImageSource; Glow.Opacity = th.Glow; }
        else Glow.Opacity = Glow.Source != null ? th.Glow : 0;

        if (IsLoaded) { UpdateModeButtons(); UpdateLikeButton(); }
    }

    // ---- background image / gif ----
    void ApplyBackground()
    {
        bgAccent = null;
        bgLum = 0;
        AnimationBehavior.SetSourceUri(BgImage, null);
        BgImage.Source = null;
        var path = cfg.Background;
        if (path == null || !File.Exists(path))
        {
            BgImage.Visibility = Visibility.Collapsed;
            ApplyLook();
            return;
        }
        try
        {
            var uri = new Uri(path);
            if (Path.GetExtension(path).Equals(".gif", StringComparison.OrdinalIgnoreCase))
            {
                AnimationBehavior.SetRepeatBehavior(BgImage, RepeatBehavior.Forever);
                AnimationBehavior.SetSourceUri(BgImage, uri); // decodes frames as it plays instead of holding the whole gif in memory
            }
            else
            {
                var img = new BitmapImage();
                img.BeginInit();
                img.UriSource = uri;
                img.CacheOption = BitmapCacheOption.OnLoad; // don't lock the file
                img.DecodePixelWidth = (int)Math.Min(SystemParameters.PrimaryScreenWidth * 1.25, 3840);
                img.EndInit();
                img.Freeze();
                BgImage.Source = img;
            }
            BgImage.Visibility = Visibility.Visible;
            (bgAccent, bgLum) = Analyze(uri);
        }
        catch (Exception ex)
        {
            BgImage.Visibility = Visibility.Collapsed;
            Toast(Loc.T("Couldn't load the background: {0}", ex.Message), "");
        }
        ApplyLook();
    }

    /// <summary>Finds the main color and brightness of an image (from a tiny 24px copy).</summary>
    static (Color, double) Analyze(Uri uri)
    {
        var dec = BitmapDecoder.Create(uri, BitmapCreateOptions.IgnoreColorProfile, BitmapCacheOption.OnLoad);
        BitmapSource f = dec.Frames[0];
        var small = new TransformedBitmap(f, new ScaleTransform(24.0 / f.PixelWidth, 24.0 / f.PixelHeight));
        var conv = new FormatConvertedBitmap(small, PixelFormats.Bgra32, null, 0);
        int w = conv.PixelWidth, h = conv.PixelHeight;
        var px = new byte[w * h * 4];
        conv.CopyPixels(px, w * 4, 0);
        double r = 0, g = 0, b = 0, wt = 0, lum = 0;
        int n = w * h;
        for (int i = 0; i < px.Length; i += 4)
        {
            double B = px[i], G = px[i + 1], R = px[i + 2];
            double mx = Math.Max(R, Math.Max(G, B)), mn = Math.Min(R, Math.Min(G, B));
            double sat = mx == 0 ? 0 : (mx - mn) / mx;
            double weight = 0.05 + sat * sat * (mx / 255.0); // colorful pixels count more than grey ones
            r += R * weight; g += G * weight; b += B * weight; wt += weight;
            lum += (0.2126 * R + 0.7152 * G + 0.0722 * B) / 255.0;
        }
        var c = Color.FromRgb((byte)(r / wt), (byte)(g / wt), (byte)(b / wt));
        return (Themes.Vivid(c), lum / Math.Max(n, 1));
    }

    void ChooseBackground()
    {
        var dlg = new Microsoft.Win32.OpenFileDialog { Filter = Loc.T("Images and GIFs") + "|*.png;*.jpg;*.jpeg;*.gif" };
        if (dlg.ShowDialog() != true) return;
        try
        {
            Directory.CreateDirectory(AppSettings.Dir);
            var dest = Path.Combine(AppSettings.Dir, $"background-{DateTime.Now.Ticks}{Path.GetExtension(dlg.FileName).ToLowerInvariant()}");
            File.Copy(dlg.FileName, dest, true);
            var old = cfg.Background;
            cfg.Background = dest;
            ApplyBackground();
            DeleteLater(old);
            cfg.Save();
            BuildSettings();
        }
        catch (Exception ex) { Toast(Loc.T("Couldn't use that image: {0}", ex.Message), ""); }
    }

    void RemoveBackground()
    {
        var old = cfg.Background;
        cfg.Background = null;
        ApplyBackground();
        DeleteLater(old);
        cfg.Save();
        BuildSettings();
    }

    void DeleteLater(string? path)
    {
        if (path == null || !path.StartsWith(AppSettings.Dir, StringComparison.OrdinalIgnoreCase)) return;
        var t = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
        t.Tick += (_, _) => { t.Stop(); try { File.Delete(path); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { Log.Info("Look", "Old background still in use, left in place: " + ex.Message); } };
        t.Start();
    }

    // ---- slow moving light blobs ----
    void StartBlobs()
    {
        if (blobClocks.Count > 0 || Motion.Reduced) return;
        void Run(TranslateTransform target, DependencyProperty prop, double to, double seconds)
        {
            var a = new DoubleAnimation(to, TimeSpan.FromSeconds(seconds))
            {
                AutoReverse = true,
                RepeatBehavior = RepeatBehavior.Forever,
                EasingFunction = new SineEase { EasingMode = EasingMode.EaseInOut }
            };
            Timeline.SetDesiredFrameRate(a, 20); // slow drift: 20 fps looks the same and costs a third
            var clock = a.CreateClock();
            target.ApplyAnimationClock(prop, clock);
            blobClocks.Add(clock);
        }
        Run(Blob1T, TranslateTransform.XProperty, 120, 13);
        Run(Blob1T, TranslateTransform.YProperty, 90, 17);
        Run(Blob2T, TranslateTransform.XProperty, -130, 15);
        Run(Blob2T, TranslateTransform.YProperty, -80, 11);
        if (!lastLive) foreach (var c in blobClocks) c.Controller?.Pause();
    }

    void ConfigureParticles()
    {
        Particles.Configure(cfg.ParticleCount, cfg.ParticleSize, cfg.ParticleSpeed, cfg.ParticleShape);
        Particles.Visibility = cfg.ParticlesOn ? Visibility.Visible : Visibility.Collapsed;
        lastLive = !lastLive; // force ApplyActivity to re-evaluate
        ApplyActivity();
    }

    /// <summary>
    /// Effects only run while LiquidCloud is on screen and (by default) focused. When you work in other windows,
    /// particles, gif, light blobs and level bars stop; when minimized, memory is handed back to Windows.
    /// </summary>
    void ApplyActivity()
    {
        bool visible = WindowState != WindowState.Minimized;
        bool live = visible && (IsActive || !cfg.PauseWhenInactive);

        Particles.Running = live && cfg.ParticlesOn && !Motion.Reduced;
        UpdateLiveBits();

        if (live != lastLive)
        {
            lastLive = live;
            foreach (var c in blobClocks) { if (live) c.Controller?.Resume(); else c.Controller?.Pause(); }
            var gif = AnimationBehavior.GetAnimator(BgImage);
            if (gif != null) { if (live) gif.Play(); else gif.Pause(); }
        }

        if (sc?.CoreWebView2 != null)
            sc.CoreWebView2.MemoryUsageTargetLevel = visible && browsing
                ? Microsoft.Web.WebView2.Core.CoreWebView2MemoryUsageTargetLevel.Normal
                : Microsoft.Web.WebView2.Core.CoreWebView2MemoryUsageTargetLevel.Low;

        if (!visible) { SaveSession(); TrimMemory(); }
    }

    // =====================================================================
    // Settings sheet (built in code so options are easy to add)
    // =====================================================================
    Style S(string key) => (Style)FindResource(key);
    Panel settingsTarget = null!;

    void BuildSettings()
    {
        settingsTarget = SettingsHost;
        var h = SettingsHost;
        h.Children.Clear();

        Section("LANGUAGE");
        Choice("Interface language", new[] { "Русский", "English" }, Loc.Language == Loc.English ? "English" : "Русский",
            v => SetLanguage(v == "English" ? Loc.English : Loc.Russian));

        Section("APPEARANCE");
        var th = CurrentTheme;
        var row = new Grid { Margin = new Thickness(0, 6, 0, 0) };
        row.Children.Add(new TextBlock { Text = Loc.T("Theme: {0}", th.Name), FontSize = 14, VerticalAlignment = VerticalAlignment.Center });
        var open = new Button { Style = S("AccentAction"), Content = Loc.T("Open theme studio"), HorizontalAlignment = HorizontalAlignment.Right };
        open.Click += (_, _) => { ToggleSettings(false); Navigate("themes"); };
        row.Children.Add(open);
        h.Children.Add(row);
        Toggle("Match accent to the background picture", "Buttons and sliders take their color from your wallpaper", cfg.AutoAccent, v => { cfg.AutoAccent = v; ApplyLook(); });

        Section("BACKGROUND PICTURE");
        Buttons(("Choose image or GIF…", ChooseBackground), ("Remove", RemoveBackground));
        Note(cfg.Background != null ? Loc.T("Using: {0}", Path.GetFileName(cfg.Background)) : Loc.T("PNG, JPG, JPEG or animated GIF."));
        SliderRow("Darken", 0, .85, cfg.BgDim, v => $"{v:P0}", v => { cfg.BgDim = v; ApplyLook(); });
        SliderRow("Blur", 0, 40, cfg.BgBlur, v => $"{v:0}", v => { cfg.BgBlur = v; ApplyLook(); });
        Toggle("Ambient glow", "Blur the cover art into the background", cfg.AmbientGlow, v => { cfg.AmbientGlow = v; ApplyLook(); });

        Section("PARTICLES");
        Toggle("Show particles", "", cfg.ParticlesOn, v => { cfg.ParticlesOn = v; ConfigureParticles(); });
        Choice("Style", new[] { "Dots", "Snow", "Stars", "Bubbles" }, cfg.ParticleShape, v => { cfg.ParticleShape = v; ConfigureParticles(); });
        SliderRow("Amount", 5, 300, cfg.ParticleCount, v => $"{v:0}", v => { cfg.ParticleCount = (int)v; ConfigureParticles(); });
        SliderRow("Size", 1, 8, cfg.ParticleSize, v => $"{v:0.0}", v => { cfg.ParticleSize = v; ConfigureParticles(); });
        SliderRow("Speed", .2, 3, cfg.ParticleSpeed, v => $"{v:0.0}×", v => { cfg.ParticleSpeed = v; ConfigureParticles(); });
        Toggle("Use accent color", "Otherwise particles are white", cfg.ParticleAccent, v => { cfg.ParticleAccent = v; ApplyLook(); });

        Section("MOTION");
        SliderRow("Animation speed", .5, 2, cfg.AnimSpeed, v => $"{v:0.0}×", v => { cfg.AnimSpeed = v; Motion.Speed = 1 / Math.Clamp(v, .3, 3); });
        Toggle("Reduce motion", "Turns off movement-heavy animations (also follows Windows' animation setting)", cfg.ReduceMotion, v => { cfg.ReduceMotion = v; Motion.UserReduced = v; ApplyActivity(); });
        Toggle("Welcome screen at start", "\"Hi, …\" intro when LiquidCloud opens", cfg.ShowWelcome, v => cfg.ShowWelcome = v);

        Section("PLAYBACK");
        Toggle("Autoplay next track", "Move on to the next song when one ends", cfg.Autoplay, v => { cfg.Autoplay = v; player.AutoAdvance = v; });
        Toggle("Keep playing similar songs", "When the queue ends, continue with songs SoundCloud / YouTube suggests", cfg.AutoRadio, v => { cfg.AutoRadio = v; player.AutoRadio = v; });
        Toggle("Remember volume", "Start at the level you left it on", cfg.RememberVolume, v => cfg.RememberVolume = v);
        Toggle("Open Spotify when needed", "Start the Spotify app if it isn't running", cfg.LaunchSpotify, v => { cfg.LaunchSpotify = v; Spotify.AutoLaunch = v; });

        Section("LYRICS");
        Note("Lyrics are read from the song file itself (its tags), or from a .lrc file with the same name next to it. Timed .lrc lyrics follow the music.");
        SliderRow("Text size", 14, 36, cfg.LyricsSize, v => $"{v:0}", v => { cfg.LyricsSize = v; ApplyLyricsSize(); });

        Section("SOUNDCLOUD");
        Toggle("Like on SoundCloud too", "Adding a SoundCloud song to favorites also likes it on your SoundCloud account", cfg.LikeOnSoundCloud, v => cfg.LikeOnSoundCloud = v);
        Toggle("Play in LiquidCloud when I press play on SoundCloud web", "Songs you start in the SoundCloud web page play here instead", cfg.CatchScPlay, v => cfg.CatchScPlay = v);
        Toggle("Keep SoundCloud web loaded", "Opens faster, but uses more memory while you're not on that page", cfg.KeepScLoaded, v => { cfg.KeepScLoaded = v; if (!v && !browsing) DisposeSc(); });
        Buttons(("Sign out of SoundCloud", SignOutSc));

        Section("DISCORD");
        Toggle("Show what I'm listening to", "Song, artist, cover, progress and session time in your Discord status", cfg.DiscordOn, SetDiscord);
        Note(Config.Get("DISCORD_CLIENT_ID") == null
            ? "Add DISCORD_CLIENT_ID to config.json first (see README), then restart LiquidCloud."
            : "Discord desktop app must be running. Your status shows \"LiquidCloud\".");

        Section("LIBRARY");
        Buttons(("Add files from this PC…", () => { ToggleSettings(false); PickFiles(); }),
                ("Add a folder…", () => { ToggleSettings(false); PickFolder(); }),
                ("Clear listening history", () => { Library.ClearHistory(); Toast("History cleared", ""); }),
                ("Clear search history", () => { Library.ClearSearches(); Toast("Search history cleared", ""); }));

        Section("PERFORMANCE");
        Toggle("Pause effects when LiquidCloud isn't focused", "Particles, GIF, light and level bars stop while you use other windows. Music keeps playing", cfg.PauseWhenInactive, v => { cfg.PauseWhenInactive = v; ApplyActivity(); });
        Note("When LiquidCloud is minimized it gives its memory back to Windows.");

        Section("DIAGNOSTICS");
        Note(Loc.T("Covers and avatars are kept on disk so they load instantly offline: {0} MB now.", (ImageCache.Shared.SizeBytes / 1048576.0).ToString("0.#", Loc.Culture)));
        Buttons(("Open log folder", OpenLogFolder),
                ("Clear image cache", () => { ImageCache.Shared.Clear(); Toast("Image cache cleared", ""); BuildSettings(); }));

        Section("KEYBOARD");
        Note("Space play/pause · Ctrl+→ / Ctrl+← next / previous · Shift+→ / Shift+← seek 10 s · Ctrl+↑ / Ctrl+↓ volume · M mute · S shuffle · R repeat · L favorite · Q up next · Ctrl+K search · Ctrl+M mini player · F11 full player · Alt+← back. Media keys work even when LiquidCloud is in the background.");
    }

    void OpenLogFolder()
    {
        try
        {
            Directory.CreateDirectory(Log.FolderPath);
            var psi = new System.Diagnostics.ProcessStartInfo("explorer.exe") { UseShellExecute = false };
            psi.ArgumentList.Add(Log.FolderPath);
            System.Diagnostics.Process.Start(psi);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception)
        {
            Log.Warn("Settings", "Couldn't open the log folder", ex);
            Toast(ex.Message, "");
        }
    }

    /// <summary>Switches the interface language right away (no restart): text resources swap, pages and settings rebuild.</summary>
    void SetLanguage(string lang)
    {
        if (lang == Loc.Language) return;
        cfg.Language = lang;
        cfg.Save();
        Loc.Apply(lang);
        Log.Info("Settings", "Language: " + lang);
        vm.Relocalize();
        FullBtn.ToolTip = Loc.T(fullPlayer ? "Exit full-window player (F11)" : "Full-window player (F11)");
        RenderSidebar();
        RenderAccount();
        if (lyrics.Count == 0) ShowNoLyrics(noLyricsText);
        InvalidatePages();
        if (currentPage.Length > 0 && currentPage != "web") Navigate(currentPage, currentArg, false);
        if (settingsOpen) BuildSettings();
        Bg.Run(RefreshAccountsAsync(), "Settings", "Accounts text");
    }

    void Section(string title) => settingsTarget.Children.Add(new TextBlock { Text = Loc.T(title), Style = S("Section") });

    void Note(string text) => settingsTarget.Children.Add(new TextBlock { Text = Loc.T(text), FontSize = 12, Foreground = (Brush)FindResource("TextDimBrush"), TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 6, 0, 0) });

    void Toggle(string title, string desc, bool value, Action<bool> set)
    {
        title = Loc.T(title); desc = Loc.T(desc);
        var g = new Grid { Margin = new Thickness(0, 12, 0, 0) };
        var sp = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 64, 0) };
        sp.Children.Add(new TextBlock { Text = title, FontSize = 14 });
        if (desc.Length > 0) sp.Children.Add(new TextBlock { Text = desc, FontSize = 12, Foreground = (Brush)FindResource("TextDimBrush"), TextWrapping = TextWrapping.Wrap });
        var tb = new ToggleButton { Style = S("Switch"), IsChecked = value, HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Center };
        tb.Click += (_, _) => { set(tb.IsChecked == true); SaveSoon(); };
        g.Children.Add(sp);
        g.Children.Add(tb);
        settingsTarget.Children.Add(g);
    }

    Slider SliderRow(string title, double min, double max, double value, Func<double, string> fmt, Action<double> set)
    {
        title = Loc.T(title);
        var sp = new StackPanel { Margin = new Thickness(0, 12, 0, 0) };
        var head = new Grid();
        var val = new TextBlock { Text = fmt(value), FontSize = 12, Foreground = (Brush)FindResource("TextDimBrush"), HorizontalAlignment = HorizontalAlignment.Right };
        head.Children.Add(new TextBlock { Text = title, FontSize = 14 });
        head.Children.Add(val);
        var sl = new Slider { Style = S("Seek"), Minimum = min, Maximum = max, Value = Math.Clamp(value, min, max), IsMoveToPointEnabled = true, Margin = new Thickness(0, 4, 0, 0) };
        sl.ValueChanged += (_, e) => { val.Text = fmt(e.NewValue); set(e.NewValue); SaveSoon(); };
        sp.Children.Add(head);
        sp.Children.Add(sl);
        settingsTarget.Children.Add(sp);
        return sl;
    }

    void Choice(string title, string[] options, string value, Action<string> set)
    {
        title = Loc.T(title);
        var g = new Grid { Margin = new Thickness(0, 12, 0, 0) };
        g.Children.Add(new TextBlock { Text = title, FontSize = 14, VerticalAlignment = VerticalAlignment.Center });
        var box = new Border { CornerRadius = (CornerRadius)FindResource("ControlRadius"), Background = (Brush)FindResource("FieldBrush"), Padding = new Thickness(3), HorizontalAlignment = HorizontalAlignment.Right };
        var rowPanel = new StackPanel { Orientation = Orientation.Horizontal };
        var group = "g" + Guid.NewGuid().ToString("N");
        foreach (var o in options)
        {
            var rb = new RadioButton { Style = S("Pill"), Content = Loc.T(o), GroupName = group, IsChecked = o == value };
            var opt = o;
            rb.Checked += (_, _) => { set(opt); SaveSoon(); };
            rowPanel.Children.Add(rb);
        }
        box.Child = rowPanel;
        g.Children.Add(box);
        settingsTarget.Children.Add(g);
    }

    void Buttons(params (string Text, Action Click)[] items)
    {
        var rowPanel = new WrapPanel { Margin = new Thickness(0, 10, 0, 0) };
        foreach (var (text, click) in items)
        {
            var b = new Button { Style = S("Action"), Content = Loc.T(text), Margin = new Thickness(0, 0, 8, 8) };
            b.Click += (_, _) => click();
            rowPanel.Children.Add(b);
        }
        settingsTarget.Children.Add(rowPanel);
    }
}
