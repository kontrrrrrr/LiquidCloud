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
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using XamlAnimatedGif;

namespace GlassPlayer;

/// <summary>Themes, custom backgrounds, particles, the settings menu and pausing effects in the background.</summary>
public partial class MainWindow
{
    Color? bgAccent;
    double bgLum;
    readonly List<AnimationClock> blobClocks = new();
    readonly DispatcherTimer saveTimer = new() { Interval = TimeSpan.FromMilliseconds(600) };
    bool lastLive = true;

    ThemeDef CurrentTheme => Themes.All.FirstOrDefault(t => t.Name == cfg.Theme) ?? Themes.All[0];

    void InitLook()
    {
        saveTimer.Tick += (_, _) => { saveTimer.Stop(); cfg.Save(); };
        ConfigureParticles();
        ApplyBackground();
        BuildSettings();
        ContentGrid.SizeChanged += (_, _) => { if (browsing && !LeftPanel.HasAnimatedProperties) LeftPanel.Width = BrowseWidth(); };
    }

    void SaveSoon() { saveTimer.Stop(); saveTimer.Start(); }

    static SolidColorBrush Solid(Color c) { var b = new SolidColorBrush(c); b.Freeze(); return b; }

    static Brush Blob(Color c)
    {
        var b = new RadialGradientBrush(new GradientStopCollection
        {
            new GradientStop(Color.FromArgb(0xA0, c.R, c.G, c.B), 0),
            new GradientStop(Color.FromArgb(0x00, c.R, c.G, c.B), 1)
        });
        b.Freeze();
        return b;
    }

    /// <summary>Pushes the current theme / background colors into every button, slider and panel.</summary>
    void ApplyLook()
    {
        var th = CurrentTheme;
        bool fromBg = cfg.AutoAccent && bgAccent != null;
        var accent = fromBg ? bgAccent!.Value : th.Accent;

        Resources["AccentBrush"] = Solid(accent);
        Resources["AccentSoftBrush"] = Solid(Color.FromArgb(0x59, accent.R, accent.G, accent.B));
        Resources["OnAccentBrush"] = Solid(Themes.Luminance(accent) > .62 ? Color.FromRgb(18, 18, 22) : Colors.White);
        Resources["Blob1Brush"] = Blob(fromBg ? accent : th.Blob1);
        Resources["Blob2Brush"] = Blob(fromBg ? Themes.Shift(accent, 40) : th.Blob2);

        byte a1 = (byte)Math.Clamp(cfg.GlassOpacity * 255, 0, 255);
        byte a2 = (byte)Math.Clamp(cfg.GlassOpacity * 0.35 * 255, 0, 255);
        // the glass picks up a hint of the accent so panels match the wallpaper
        Color tint1 = Mix(Colors.White, accent, fromBg ? .18 : .06);
        var fill = new LinearGradientBrush(Color.FromArgb(a1, tint1.R, tint1.G, tint1.B), Color.FromArgb(a2, 255, 255, 255), new Point(0, 0), new Point(1, 1));
        fill.Freeze();
        Resources["GlassFill"] = fill;
        Resources["GlassRadius"] = new CornerRadius(cfg.Radius);

        var tint = fromBg ? Mix(th.Tint, Color.FromRgb((byte)(accent.R / 6), (byte)(accent.G / 6), (byte)(accent.B / 6)), .6) : th.Tint;
        Resources["SheetBrush"] = Solid(Color.FromArgb(0xEA, tint.R, tint.G, tint.B));

        bool hasBg = BgImage.Visibility == Visibility.Visible;
        // bright wallpapers get darkened automatically so white text and buttons stay readable
        double autoDim = hasBg ? Math.Clamp((bgLum - .45) * 1.2, 0, .5) : 0;
        BgDim.Opacity = hasBg ? Math.Max(cfg.BgDim, autoDim) : 0;
        BgBlurFx.Radius = cfg.BgBlur;
        Ambient.Visibility = hasBg ? Visibility.Collapsed : Visibility.Visible;

        Particles.SetColor(cfg.ParticleAccent ? accent : Colors.White);
        AnimFactor = 1 / Math.Clamp(cfg.AnimSpeed, .3, 3);
        if (!cfg.AmbientGlow) Glow.Source = null;
        else if (Glow.Source == null && ArtBrush.ImageSource != null) { Glow.Source = ArtBrush.ImageSource; Glow.Opacity = .6; }
    }

    static Color Mix(Color a, Color b, double t) => Color.FromRgb(
        (byte)(a.R + (b.R - a.R) * t), (byte)(a.G + (b.G - a.G) * t), (byte)(a.B + (b.B - a.B) * t));

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
                // XamlAnimatedGif decodes frames as it plays instead of holding the whole gif in memory
                AnimationBehavior.SetRepeatBehavior(BgImage, RepeatBehavior.Forever);
                AnimationBehavior.SetSourceUri(BgImage, uri);
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
            Status.Text = "Couldn't load the background: " + ex.Message;
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
        var dlg = new Microsoft.Win32.OpenFileDialog { Filter = "Images and GIFs|*.png;*.jpg;*.jpeg;*.gif" };
        if (dlg.ShowDialog() != true) return;
        try
        {
            // keep our own copy so the background survives if the original file is moved
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
        catch (Exception ex) { Status.Text = "Couldn't use that image: " + ex.Message; }
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
        t.Tick += (_, _) => { t.Stop(); try { File.Delete(path); } catch { } };
        t.Start();
    }

    // ---- moving color blobs ----
    void StartBlobs()
    {
        if (blobClocks.Count > 0) return;
        void Run(TranslateTransform target, DependencyProperty prop, double to, double seconds)
        {
            var a = new DoubleAnimation(to, TimeSpan.FromSeconds(seconds))
            {
                AutoReverse = true,
                RepeatBehavior = RepeatBehavior.Forever,
                EasingFunction = new SineEase { EasingMode = EasingMode.EaseInOut }
            };
            var clock = a.CreateClock();
            target.ApplyAnimationClock(prop, clock);
            blobClocks.Add(clock);
        }
        Run(Blob1T, TranslateTransform.XProperty, 110, 9);
        Run(Blob1T, TranslateTransform.YProperty, 80, 12);
        Run(Blob2T, TranslateTransform.XProperty, -120, 11);
        Run(Blob2T, TranslateTransform.YProperty, -70, 8);
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
    /// Effects only run while Glass is on screen and (by default) focused. When you work in other
    /// windows, particles, gif and blobs stop, and when minimized, memory is handed back to Windows.
    /// </summary>
    void ApplyActivity()
    {
        bool visible = WindowState != WindowState.Minimized;
        bool live = visible && (IsActive || !cfg.PauseWhenInactive);

        Particles.Running = live && cfg.ParticlesOn;

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

        if (!visible) TrimMemory();
    }

    // =====================================================================
    // Settings menu (built in code so it's easy to add more options)
    // =====================================================================
    Style S(string key) => (Style)FindResource(key);

    void BuildSettings()
    {
        var h = SettingsHost;
        h.Children.Clear();

        Section("Theme");
        var wrap = new WrapPanel { Margin = new Thickness(0, 6, 0, 0) };
        foreach (var th in Themes.All)
        {
            bool sel = th.Name == cfg.Theme;
            var grad = new LinearGradientBrush(th.Blob1, th.Blob2, 45); grad.Freeze();
            var sw = new Border
            {
                Width = 58, Height = 58, CornerRadius = new CornerRadius(18), Background = grad, Cursor = Cursors.Hand,
                BorderBrush = sel ? Brushes.White : new SolidColorBrush(Color.FromArgb(0x30, 255, 255, 255)),
                BorderThickness = new Thickness(sel ? 2.5 : 1),
                RenderTransformOrigin = new Point(.5, .5), RenderTransform = new ScaleTransform(1, 1)
            };
            sw.MouseEnter += (_, _) => { Anim((ScaleTransform)sw.RenderTransform, ScaleTransform.ScaleXProperty, 1.08, 180); Anim((ScaleTransform)sw.RenderTransform, ScaleTransform.ScaleYProperty, 1.08, 180); };
            sw.MouseLeave += (_, _) => { Anim((ScaleTransform)sw.RenderTransform, ScaleTransform.ScaleXProperty, 1, 220); Anim((ScaleTransform)sw.RenderTransform, ScaleTransform.ScaleYProperty, 1, 220); };
            var name = th.Name;
            sw.MouseLeftButtonUp += (_, _) => { cfg.Theme = name; ApplyLook(); BuildSettings(); SaveSoon(); };
            var cell = new StackPanel { Margin = new Thickness(0, 0, 12, 10) };
            cell.Children.Add(sw);
            cell.Children.Add(new TextBlock { Text = name, FontSize = 11, Opacity = sel ? 1 : .6, HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 4, 0, 0) });
            wrap.Children.Add(cell);
        }
        h.Children.Add(wrap);
        Toggle("Match colors to the background", "Buttons, sliders and panels take their color from your wallpaper", cfg.AutoAccent, v => { cfg.AutoAccent = v; ApplyLook(); });

        Section("Background");
        Buttons(("Choose image or GIF…", ChooseBackground), ("Remove", RemoveBackground));
        Note(cfg.Background != null ? "Using: " + Path.GetFileName(cfg.Background) : "PNG, JPG, JPEG or animated GIF.");
        SliderRow("Darken", 0, .85, cfg.BgDim, v => $"{v:P0}", v => { cfg.BgDim = v; ApplyLook(); });
        SliderRow("Blur", 0, 40, cfg.BgBlur, v => $"{v:0}", v => { cfg.BgBlur = v; ApplyLook(); });

        Section("Glass panels");
        SliderRow("Panel opacity", .03, .5, cfg.GlassOpacity, v => $"{v:P0}", v => { cfg.GlassOpacity = v; ApplyLook(); });
        SliderRow("Corner roundness", 6, 40, cfg.Radius, v => $"{v:0}", v => { cfg.Radius = v; ApplyLook(); });
        Toggle("Ambient glow", "Blur the cover art into the background", cfg.AmbientGlow, v => { cfg.AmbientGlow = v; ApplyLook(); if (v) Anim(Glow, OpacityProperty, .6, 600); });

        Section("Particles");
        Toggle("Show particles", "", cfg.ParticlesOn, v => { cfg.ParticlesOn = v; ConfigureParticles(); });
        Choice("Style", new[] { "Dots", "Snow", "Stars", "Bubbles" }, cfg.ParticleShape, v => { cfg.ParticleShape = v; ConfigureParticles(); });
        SliderRow("Amount", 5, 300, cfg.ParticleCount, v => $"{v:0}", v => { cfg.ParticleCount = (int)v; ConfigureParticles(); });
        SliderRow("Size", 1, 8, cfg.ParticleSize, v => $"{v:0.0}", v => { cfg.ParticleSize = v; ConfigureParticles(); });
        SliderRow("Speed", .2, 3, cfg.ParticleSpeed, v => $"{v:0.0}×", v => { cfg.ParticleSpeed = v; ConfigureParticles(); });
        Toggle("Use accent color", "Otherwise particles are white", cfg.ParticleAccent, v => { cfg.ParticleAccent = v; ApplyLook(); });

        Section("Lyrics");
        Note("Lyrics are read from the song file itself (its tags), or from a .lrc file with the same name next to it. Timed .lrc lyrics follow the music.");
        SliderRow("Text size", 14, 36, cfg.LyricsSize, v => $"{v:0}", v => { cfg.LyricsSize = v; ApplyLyricsSize(); });

        Section("Animations");
        SliderRow("Animation speed", .5, 2, cfg.AnimSpeed, v => $"{v:0.0}×", v => { cfg.AnimSpeed = v; ApplyLook(); });

        Section("Playback");
        Toggle("Autoplay next track", "Move on to the next result when a song ends", cfg.Autoplay, v => cfg.Autoplay = v);
        Toggle("Open Spotify when needed", "Start the Spotify app if it isn't running", cfg.LaunchSpotify, v => { cfg.LaunchSpotify = v; Spotify.AutoLaunch = v; });
        Toggle("Remember volume", "Start at the level you left it on", cfg.RememberVolume, v => cfg.RememberVolume = v);
        Toggle("Keep playing similar songs", "When the list ends, continue with songs SoundCloud / YouTube suggests", cfg.AutoRadio, v => cfg.AutoRadio = v);

        Section("Discord");
        Toggle("Show what I'm listening to", "Song, artist, cover, progress and session time in your Discord status", cfg.DiscordOn, SetDiscord);
        Note(Config.Get("DISCORD_CLIENT_ID") == null
            ? "Add DISCORD_CLIENT_ID to config.json first (see README), then restart LiquidCloud."
            : "Discord desktop app must be running. Your status shows \"Listening to LiquidCloud\".");

        Section("SoundCloud");
        Toggle("Play in LiquidCloud when I press play on SoundCloud", "Songs you start in the SoundCloud browser play in LiquidCloud instead", cfg.CatchScPlay, v => cfg.CatchScPlay = v);
        Toggle("Keep SoundCloud loaded", "Opens faster, but uses more memory while you're not browsing", cfg.KeepScLoaded, v => { cfg.KeepScLoaded = v; if (!v && !browsing) DisposeSc(); });
        Buttons(("Sign out of SoundCloud", SignOutSc));

        Section("Performance");
        Toggle("Pause effects when LiquidCloud isn't focused", "Particles, GIF and glow stop while you use other windows. Music keeps playing", cfg.PauseWhenInactive, v => { cfg.PauseWhenInactive = v; ApplyActivity(); });
        Note("When LiquidCloud is minimized it gives its memory back to Windows.");
    }

    void Section(string title) => SettingsHost.Children.Add(new TextBlock { Text = title, Style = S("Section") });

    void Note(string text) => SettingsHost.Children.Add(new TextBlock { Text = text, FontSize = 12, Opacity = .55, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 6, 0, 0) });

    void Toggle(string title, string desc, bool value, Action<bool> set)
    {
        var g = new Grid { Margin = new Thickness(0, 12, 0, 0) };
        var sp = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 64, 0) };
        sp.Children.Add(new TextBlock { Text = title, FontSize = 14 });
        if (desc.Length > 0) sp.Children.Add(new TextBlock { Text = desc, FontSize = 12, Opacity = .55, TextWrapping = TextWrapping.Wrap });
        var tb = new ToggleButton { Style = S("Switch"), IsChecked = value, HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Center };
        tb.Click += (_, _) => { set(tb.IsChecked == true); SaveSoon(); };
        g.Children.Add(sp);
        g.Children.Add(tb);
        SettingsHost.Children.Add(g);
    }

    void SliderRow(string title, double min, double max, double value, Func<double, string> fmt, Action<double> set)
    {
        var sp = new StackPanel { Margin = new Thickness(0, 12, 0, 0) };
        var head = new Grid();
        var val = new TextBlock { Text = fmt(value), FontSize = 12, Opacity = .6, HorizontalAlignment = HorizontalAlignment.Right };
        head.Children.Add(new TextBlock { Text = title, FontSize = 14 });
        head.Children.Add(val);
        var sl = new Slider { Style = S("Seek"), Minimum = min, Maximum = max, Value = Math.Clamp(value, min, max), IsMoveToPointEnabled = true, Margin = new Thickness(0, 4, 0, 0) };
        sl.ValueChanged += (_, e) => { val.Text = fmt(e.NewValue); set(e.NewValue); SaveSoon(); };
        sp.Children.Add(head);
        sp.Children.Add(sl);
        SettingsHost.Children.Add(sp);
    }

    void Choice(string title, string[] options, string value, Action<string> set)
    {
        var g = new Grid { Margin = new Thickness(0, 12, 0, 0) };
        g.Children.Add(new TextBlock { Text = title, FontSize = 14, VerticalAlignment = VerticalAlignment.Center });
        var box = new Border
        {
            CornerRadius = new CornerRadius(18), Background = new SolidColorBrush(Color.FromArgb(0x14, 255, 255, 255)), Padding = new Thickness(3),
            HorizontalAlignment = HorizontalAlignment.Right
        };
        var row = new StackPanel { Orientation = Orientation.Horizontal };
        var group = "g" + Guid.NewGuid().ToString("N");
        foreach (var o in options)
        {
            var rb = new RadioButton { Style = S("Pill"), Content = o, GroupName = group, IsChecked = o == value };
            var opt = o;
            rb.Checked += (_, _) => { set(opt); SaveSoon(); };
            row.Children.Add(rb);
        }
        box.Child = row;
        g.Children.Add(box);
        SettingsHost.Children.Add(g);
    }

    void Buttons(params (string Text, Action Click)[] items)
    {
        var row = new WrapPanel { Margin = new Thickness(0, 10, 0, 0) };
        foreach (var (text, click) in items)
        {
            var b = new Button { Style = S("Action"), Content = text, Margin = new Thickness(0, 0, 8, 0) };
            b.Click += (_, _) => { click(); };
            row.Children.Add(b);
        }
        SettingsHost.Children.Add(row);
    }
}
