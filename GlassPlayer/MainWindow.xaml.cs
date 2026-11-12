using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace GlassPlayer;

// Based on GlassPlayer by Jonzu00 (GPL-3.0). Modified: themes, backgrounds, particles, lyrics,
// SoundCloud browser, memory savings. See README.md.
public partial class MainWindow : Window
{
    [DllImport("dwmapi.dll")] static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);
    [DllImport("psapi.dll")] static extern bool EmptyWorkingSet(IntPtr process);

    readonly AppSettings cfg = AppSettings.Load();
    AudioEngine? engine;
    long fallbackMs;
    bool importOpen;
    readonly DispatcherTimer timer = new() { Interval = TimeSpan.FromMilliseconds(250) };
    readonly DispatcherTimer seekDebounce = new() { Interval = TimeSpan.FromMilliseconds(300) };
    readonly DispatcherTimer volDebounce = new() { Interval = TimeSpan.FromMilliseconds(300) };
    readonly Dictionary<string, IProvider> providers = new();
    IProvider? provider;
    Spotify? spotify;
    bool updating, playing, advanced, polling, settingsOpen, updatingList;

    // Spotify remote state: non-null while a Spotify track is the one playing
    Spotify? activeSp;
    Track? nowPlaying;
    int rProgress, rDuration;
    DateTime rStamp, lastPoll, playStart;
    bool rPlaying;

    public MainWindow()
    {
        InitializeComponent();

        _ = Task.Run(Cache.Cleanup);
        Spotify.AutoLaunch = cfg.LaunchSpotify;
        foreach (IProvider p in new IProvider[] { new YouTubeMusic(), new SoundCloud(), new Spotify(), new LocalFiles() })
            providers[p.Name] = p;
        spotify = (Spotify)providers["Spotify"];
        spotify.Web = new WebPlayer(Web, spotify);
        InitMySoundCloud();
        InitExtras();
        Loaded += (_, _) =>
        {
            if (spotify is { SignedIn: true, Web: not null }) _ = spotify.Web.StartAsync();
            StartBlobs();
            ApplyActivity();
        };

        if (cfg.RememberVolume) Volume.Value = cfg.Volume;
        SoundCloudBtn.IsChecked = true; // works with no keys

        InitLook();
        InitLyricsLayout();
        StartEngine();

        seekDebounce.Tick += async (_, _) => { seekDebounce.Stop(); await Safe(() => activeSp?.SeekAsync((int)(Seek.Value * 1000)) ?? Task.CompletedTask); };
        volDebounce.Tick += async (_, _) => { volDebounce.Stop(); await Safe(() => activeSp?.VolumeAsync((int)(Volume.Value * 100)) ?? Task.CompletedTask); };

        timer.Tick += async (_, _) =>
        {
            if (activeSp != null) { await RemoteTickAsync(activeSp); return; }
            if (engine == null) return;
            long len = engine.LengthMs > 0 ? engine.LengthMs : fallbackMs;
            if (len <= 0) return;
            long pos = Math.Max(0, engine.TimeMs);
            updating = true;
            Seek.Maximum = len / 1000.0;
            if (!Seek.IsMouseCaptureWithin) Seek.Value = pos / 1000.0;
            Cur.Text = Fmt(TimeSpan.FromMilliseconds(pos));
            Dur.Text = Fmt(TimeSpan.FromMilliseconds(len));
            updating = false;
            UpdateLyrics(pos);
            MaybePreloadNext(pos, len);
            UpdateDiscord(false);
        };
        timer.Start();

        PreviewKeyDown += (_, e) =>
        {
            if (e.Key != Key.Escape) return;
            if (importOpen) ToggleImport(false);
            else if (settingsOpen) ToggleSettings(false);
            else if (expanded) SetExpanded(false);
        };

        // drop audio files or folders onto the window
        AllowDrop = true;
        Drop += (_, e) => { if (e.Data.GetData(DataFormats.FileDrop) is string[] paths) AddLocal(paths); };

        // pause effects while Glass is in the background / minimized
        Activated += (_, _) => ApplyActivity();
        Deactivated += (_, _) => ApplyActivity();
        StateChanged += (_, _) => ApplyActivity();
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        var hwnd = new WindowInteropHelper(this).Handle;
        HwndSource.FromHwnd(hwnd).CompositionTarget.BackgroundColor = Colors.Transparent;
        int dark = 1, round = 2, acrylic = 3;
        DwmSetWindowAttribute(hwnd, 20, ref dark, 4);    // immersive dark mode
        DwmSetWindowAttribute(hwnd, 33, ref round, 4);   // rounded window corners
        DwmSetWindowAttribute(hwnd, 38, ref acrylic, 4); // acrylic backdrop (Windows 11 22H2+)
    }

    bool closing;
    protected override void OnClosing(System.ComponentModel.CancelEventArgs e)
    {
        if (!closing)
        {
            e.Cancel = true; // play the exit animation first, then close for real
            closing = true;
            if (sc != null) sc.Visibility = Visibility.Hidden;
            Anim(Root, OpacityProperty, 0, 240);
            Anim(Ambient, OpacityProperty, 0, 240);
            Anim(Overlay, OpacityProperty, 0, 240);
            Anim(RootScale, ScaleTransform.ScaleXProperty, .95, 260);
            Anim(RootScale, ScaleTransform.ScaleYProperty, .95, 260);
            var t = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(280) };
            t.Tick += (_, _) => { t.Stop(); Close(); };
            t.Start();
            return;
        }
        base.OnClosing(e);
    }

    protected override void OnClosed(EventArgs e)
    {
        cfg.Volume = Volume.Value;
        cfg.Save();
        DisposeSc();
        discord?.Dispose();
        base.OnClosed(e);
        Environment.Exit(0); // make sure no background audio/web processes keep Glass alive
    }

    static string Fmt(TimeSpan t) => $"{(int)t.TotalMinutes}:{t.Seconds:00}";

    async Task Safe(Func<Task> action)
    {
        try { await action(); } catch (Exception ex) { Status.Text = ex.Message; }
    }

    void StartEngine()
    {
        var eng = new AudioEngine(Dispatcher, Volume.Value);
        eng.Ended += async () =>
        {
            if (activeSp != null) return;
            if ((DateTime.UtcNow - playStart).TotalSeconds < 3)
            {
                if (await TryDownloadInsteadAsync()) return;
                Status.Text = "Couldn't play this track. Try another one."; SetPlaying(false); return;
            }
            if (cfg.Autoplay) Step(1); else { SetPlaying(false); UpdateDiscord(); }
        };
        eng.Failed += async msg =>
        {
            if (await TryDownloadInsteadAsync()) return;
            Status.Text = msg; SetPlaying(false);
        };
        engine = eng;
    }

    // ---- animation helpers (AnimFactor comes from the "Animation speed" setting) ----
    static double AnimFactor = 1;

    static void Anim(IAnimatable target, DependencyProperty prop, double to, int ms = 350, Action? done = null)
    {
        var a = new DoubleAnimation(to, TimeSpan.FromMilliseconds(ms * AnimFactor)) { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } };
        if (done != null) a.Completed += (_, _) => done();
        target.BeginAnimation(prop, a);
    }

    static void Fade(UIElement el, double from, double to, int ms)
        => el.BeginAnimation(OpacityProperty, new DoubleAnimation(from, to, TimeSpan.FromMilliseconds(ms * AnimFactor)) { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } });

    async Task AnimateResultsAsync()
    {
        await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.Loaded);
        var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
        for (int i = 0; i < Math.Min(List.Items.Count, 14); i++)
        {
            if (List.ItemContainerGenerator.ContainerFromIndex(i) is not ListBoxItem c) continue;
            var tt = new TranslateTransform(0, 18);
            c.RenderTransform = tt;
            var begin = TimeSpan.FromMilliseconds(i * 45 * AnimFactor);
            c.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(320 * AnimFactor)) { BeginTime = begin, EasingFunction = ease });
            tt.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(18, 0, TimeSpan.FromMilliseconds(420 * AnimFactor)) { BeginTime = begin, EasingFunction = ease });
        }
    }

    // ---- smooth wheel scrolling for the song list ----
    ScrollViewer? listScroll;
    double scrollTarget;
    bool scrolling;

    static ScrollViewer? FindScroll(DependencyObject d)
    {
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(d); i++)
        {
            var c = VisualTreeHelper.GetChild(d, i);
            if (c is ScrollViewer sv) return sv;
            var r = FindScroll(c);
            if (r != null) return r;
        }
        return null;
    }

    void List_PreviewMouseWheel(object s, MouseWheelEventArgs e)
    {
        listScroll ??= FindScroll(List);
        if (listScroll == null) return;
        e.Handled = true;
        if (!scrolling) scrollTarget = listScroll.VerticalOffset;
        scrollTarget = Math.Clamp(scrollTarget - e.Delta * 0.9, 0, listScroll.ScrollableHeight);
        if (!scrolling) { scrolling = true; CompositionTarget.Rendering += ScrollStep; }
    }

    void ScrollStep(object? s, EventArgs e)
    {
        var sv = listScroll!;
        double diff = scrollTarget - sv.VerticalOffset;
        if (Math.Abs(diff) < 0.5)
        {
            sv.ScrollToVerticalOffset(scrollTarget);
            CompositionTarget.Rendering -= ScrollStep;
            scrolling = false;
            return;
        }
        sv.ScrollToVerticalOffset(sv.VerticalOffset + diff * 0.18);
    }

    // ---- Spotify progress: poll the API about every 1.5s and interpolate in between ----
    async Task RemoteTickAsync(Spotify sp)
    {
        var now = DateTime.UtcNow;
        if (!polling && (now - lastPoll).TotalMilliseconds > 1500)
        {
            polling = true; lastPoll = now;
            try
            {
                if (await sp.StateAsync() is { } st && (st.Uri == nowPlaying?.OpenUri || (DateTime.UtcNow - playStart).TotalSeconds > 3))
                {
                    rPlaying = st.Playing; rProgress = st.Progress; rDuration = st.Duration; rStamp = DateTime.UtcNow;
                    SetPlaying(st.Playing);
                }
            }
            catch (Exception ex) { Status.Text = ex.Message; }
            polling = false;
        }
        if (rDuration <= 0) return;
        int pos = rPlaying ? Math.Min(rProgress + (int)(DateTime.UtcNow - rStamp).TotalMilliseconds, rDuration) : rProgress;
        updating = true;
        Seek.Maximum = rDuration / 1000.0;
        if (!Seek.IsMouseCaptureWithin) Seek.Value = pos / 1000.0;
        Cur.Text = Fmt(TimeSpan.FromMilliseconds(pos));
        Dur.Text = Fmt(TimeSpan.FromMilliseconds(rDuration));
        updating = false;
        if (rPlaying && pos >= rDuration - 1500 && !advanced) { advanced = true; if (cfg.Autoplay) Step(1); }
        UpdateDiscord(false);
    }

    // ---- search ----
    void Provider_Checked(object s, RoutedEventArgs e)
    {
        if (s is RadioButton { Tag: string name } && providers.TryGetValue(name, out var p)) provider = p;
        UpdateScChips();
        if (SearchBox.Text.Length > 0) _ = SearchAsync();
    }

    void Search_KeyDown(object s, KeyEventArgs e) { if (e.Key == Key.Enter) _ = SearchAsync(); }

    async Task SearchAsync()
    {
        if (provider == null || string.IsNullOrWhiteSpace(SearchBox.Text)) return;
        if (browsing) await SetBrowsingAsync(false);
        Status.Text = provider is Spotify { SignedIn: false } ? "Approve LiquidCloud in your browser to connect Spotify…" : "Searching…";
        try
        {
            var results = await provider.SearchAsync(SearchBox.Text.Trim());
            List.ItemsSource = results;
            Status.Text = results.Count == 0 ? "No results. Try different words." : "";
            _ = AnimateResultsAsync();
        }
        catch (Exception ex) { Status.Text = "Search failed: " + ex.Message; }
    }

    void List_SelectionChanged(object s, SelectionChangedEventArgs e)
    {
        if (updatingList || List.SelectedItem is not Track t) return;
        if (t.Source == ScAccount.PlaylistSource) { _ = OpenScPlaylistAsync(t); return; } // open the playlist instead of playing it
        _ = PlayAsync(t);
    }

    // ---- cover art ----
    void SetArtUrl(string? url)
    {
        if (url == null) { SetArt(null); return; }
        try
        {
            var img = new BitmapImage();
            img.BeginInit();
            img.UriSource = new Uri(url);
            img.DecodePixelWidth = 600; // keeps big covers from eating memory
            img.EndInit();
            SetArt(img);
        }
        catch { SetArt(null); }
    }

    void SetArt(ImageSource? img)
    {
        ArtBrush.ImageSource = img;
        ArtPlaceholder.Visibility = img == null ? Visibility.Visible : Visibility.Collapsed;
        Glow.Source = cfg.AmbientGlow ? img : null;
        if (img != null && cfg.AmbientGlow) Fade(Glow, 0, .6, 1000);
    }

    // ---- playback ----
    async Task PlayAsync(Track t)
    {
        nowPlaying = t; advanced = false; retriedDownload = false;
        fallbackMs = t.DurationMs;
        updating = true; Seek.Maximum = 1; Seek.Value = 0; Cur.Text = "0:00"; Dur.Text = t.DurationMs > 0 ? Fmt(TimeSpan.FromMilliseconds(t.DurationMs)) : "0:00"; updating = false;
        TitleText.Text = ExpTitle.Text = t.Title;
        ArtistText.Text = ExpArtist.Text = t.Artist;
        Fade(TitleText, 0, 1, 450); Fade(ArtistText, 0, 1, 600); Fade(ArtBox, .25, 1, 550);
        SetArtUrl(t.Art);
        ShowNoLyrics("Loading lyrics…");
        try
        {
            Status.Text = "";
            if (t.Source == "Spotify" && providers["Spotify"] is Spotify sp)
            {
                engine?.Stop();
                Status.Text = "Starting Spotify…";
                var device = await sp.PlayAsync(t.OpenUri!);
                playStart = DateTime.UtcNow;
                activeSp = sp; rPlaying = true; rProgress = 0; rDuration = 0; rStamp = DateTime.UtcNow; lastPoll = DateTime.MinValue;
                SetPlaying(true);
                UpdateDiscord();
                Status.Text = $"Playing on {device}";
                ShowNoLyrics("Lyrics are read from audio files, so they aren't available for Spotify.");
                await Safe(() => sp.VolumeAsync((int)(Volume.Value * 100)));
            }
            else
            {
                if (activeSp != null) { var old = activeSp; activeSp = null; await Safe(old.PauseAsync); }
                Status.Text = "Loading…";
                var url = TakePreloaded(t) ?? await providers[t.Source].ResolveAsync(t);
                if (nowPlaying != t) return; // user picked something else meanwhile
                if (url == null) { Status.Text = "This track can't be streamed."; return; }
                engine!.Open(url);
                playStart = DateTime.UtcNow;
                SetPlaying(true);
                UpdateDiscord();
                Status.Text = "";
                if (!url.StartsWith("http", StringComparison.OrdinalIgnoreCase)) _ = LoadFileExtrasAsync(url, t);
                else ShowNoLyrics("");
            }
        }
        catch (Exception ex) { Status.Text = ex.Message; SetPlaying(false); }
    }

    /// <summary>Lyrics and embedded cover straight from the song file.</summary>
    async Task LoadFileExtrasAsync(string path, Track t)
    {
        var (lines, synced, cover) = await Task.Run(() => LyricsReader.Read(path, 600));
        if (nowPlaying != t) return;
        if (cover != null && t.Art == null) SetArt(cover);
        SetLyrics(lines, synced);
    }

    void SetPlaying(bool on)
    {
        playing = on;
        PlayBtn.Content = on ? "" : "";
        double scale = on ? 1.0 : 0.88; // cover art swells while playing, settles when paused
        Anim(ArtScale, ScaleTransform.ScaleXProperty, scale, 500);
        Anim(ArtScale, ScaleTransform.ScaleYProperty, scale, 500);

        // soft pulsing ring around the play button while music plays
        if (on)
        {
            var dur = TimeSpan.FromSeconds(1.8);
            var grow = new DoubleAnimation(1, 1.45, dur) { RepeatBehavior = RepeatBehavior.Forever, EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } };
            PlayRingScale.BeginAnimation(ScaleTransform.ScaleXProperty, grow);
            PlayRingScale.BeginAnimation(ScaleTransform.ScaleYProperty, grow);
            PlayRing.BeginAnimation(OpacityProperty, new DoubleAnimation(.7, 0, dur) { RepeatBehavior = RepeatBehavior.Forever });
        }
        else
        {
            PlayRingScale.BeginAnimation(ScaleTransform.ScaleXProperty, null);
            PlayRingScale.BeginAnimation(ScaleTransform.ScaleYProperty, null);
            PlayRing.BeginAnimation(OpacityProperty, null);
            PlayRing.Opacity = 0;
        }
    }

    async void PlayPause_Click(object s, RoutedEventArgs e)
    {
        if (List.SelectedItem == null) return;
        bool resume = !playing;
        SetPlaying(resume);
        if (activeSp != null)
        {
            rPlaying = resume; rStamp = DateTime.UtcNow; lastPoll = DateTime.UtcNow;
            var sp = activeSp;
            await Safe(() => resume ? sp.ResumeAsync() : sp.PauseAsync());
        }
        else engine?.SetPaused(!resume);
        UpdateDiscord();
    }

    async void Step(int d)
    {
        int n = List.Items.Count;
        if (n == 0) return;
        int i = List.SelectedIndex;
        // end of the list: keep going with suggested songs, like SoundCloud does
        if (d > 0 && i == n - 1 && cfg.AutoRadio && List.SelectedItem is Track last && last.Source is "SoundCloud" or "YouTube Music")
        {
            Status.Text = "Finding similar songs…";
            if (await AppendRelatedAsync(last, true)) return;
            Status.Text = "";
        }
        List.SelectedIndex = ((i + d) % n + n) % n;
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
    void Next_Click(object s, RoutedEventArgs e) => Step(1);
    void Prev_Click(object s, RoutedEventArgs e) => Step(-1);

    void Seek_ValueChanged(object s, RoutedPropertyChangedEventArgs<double> e)
    {
        if (updating) return;
        if (activeSp != null)
        {
            rProgress = (int)(e.NewValue * 1000); rStamp = DateTime.UtcNow; lastPoll = DateTime.UtcNow;
            seekDebounce.Stop(); seekDebounce.Start();
        }
        else if (engine != null) engine.TimeMs = (long)(e.NewValue * 1000);
        lastDiscord = DateTime.MinValue; // refresh Discord's progress bar on the next tick
    }

    void SeekTo(int ms)
    {
        if (activeSp == null && engine != null) engine.TimeMs = ms;
    }

    void Volume_ValueChanged(object s, RoutedPropertyChangedEventArgs<double> e)
    {
        if (engine != null) engine.Volume = e.NewValue;
        if (activeSp != null) { volDebounce.Stop(); volDebounce.Start(); }
    }

    // ---- import ----
    void ShowTracks(List<Track> tracks, string message)
    {
        List.ItemsSource = tracks;
        Status.Text = message;
        listScroll?.ScrollToTop();
        _ = AnimateResultsAsync();
    }

    void Import_Click(object s, RoutedEventArgs e) => ToggleImport(true);
    void ImportDim_Click(object s, MouseButtonEventArgs e) => ToggleImport(false);
    void Import_KeyDown(object s, KeyEventArgs e) { if (e.Key == Key.Enter) ImportGo_Click(s, e); }

    void ToggleImport(bool open)
    {
        importOpen = open;
        WebAirspace();
        if (open)
        {
            ImportOverlay.Visibility = Visibility.Visible;
            ImportStatus.Text = "";
            Anim(ImportCard, OpacityProperty, 1, 300);
            Anim(ImportScale, ScaleTransform.ScaleXProperty, 1, 380);
            Anim(ImportScale, ScaleTransform.ScaleYProperty, 1, 380);
            ImportBox.Focus();
        }
        else
        {
            Anim(ImportCard, OpacityProperty, 0, 200);
            Anim(ImportScale, ScaleTransform.ScaleXProperty, .94, 220);
            Anim(ImportScale, ScaleTransform.ScaleYProperty, .94, 220, () => { if (!importOpen) ImportOverlay.Visibility = Visibility.Collapsed; });
        }
    }

    static readonly string[] AudioExt = { ".mp3", ".m4a", ".aac", ".wav", ".flac", ".ogg", ".opus", ".wma" };

    void AddLocal(IEnumerable<string> paths)
    {
        var files = new List<string>();
        foreach (var p in paths)
        {
            if (Directory.Exists(p))
            {
                try { files.AddRange(Directory.EnumerateFiles(p, "*.*", SearchOption.AllDirectories).Where(f => AudioExt.Contains(Path.GetExtension(f).ToLowerInvariant()))); }
                catch { }
            }
            else if (AudioExt.Contains(Path.GetExtension(p).ToLowerInvariant())) files.Add(p);
        }
        if (files.Count == 0) { Status.Text = "No audio files found."; return; }
        if (browsing) _ = SetBrowsingAsync(false);
        var tracks = files.Select(LocalFiles.ToTrack).ToList();
        ShowTracks(tracks, $"Imported {tracks.Count} file{(tracks.Count == 1 ? "" : "s")}");
    }

    void ImportFiles_Click(object s, RoutedEventArgs e)
    {
        var dlg = new Microsoft.Win32.OpenFileDialog { Multiselect = true, Filter = "Audio files|*.mp3;*.m4a;*.aac;*.wav;*.flac;*.ogg;*.opus;*.wma|All files|*.*" };
        if (dlg.ShowDialog() != true) return;
        AddLocal(dlg.FileNames);
        ToggleImport(false);
    }

    void ImportFolder_Click(object s, RoutedEventArgs e)
    {
        var dlg = new Microsoft.Win32.OpenFolderDialog { Multiselect = true };
        if (dlg.ShowDialog() != true) return;
        AddLocal(dlg.FolderNames);
        ToggleImport(false);
    }

    async void ImportGo_Click(object s, RoutedEventArgs e)
    {
        var link = ImportBox.Text.Trim();
        if (link.Length == 0) return;
        ImportGo.IsEnabled = false;
        ImportStatus.Text = "Importing…";
        try
        {
            List<Track> tracks;
            var sp = Regex.Match(link, "playlist[/:]([A-Za-z0-9]+)");
            if (link.Contains("spotify", StringComparison.OrdinalIgnoreCase))
            {
                if (!sp.Success) throw new Exception("For Spotify, paste a playlist link.");
                if (!spotify!.Enabled) throw new Exception("Spotify needs a Client ID first (see Settings).");
                tracks = await spotify.PlaylistAsync(sp.Groups[1].Value);
            }
            else if (Regex.IsMatch(link, "youtube\\.com|youtu\\.be|soundcloud\\.com", RegexOptions.IgnoreCase))
                tracks = await Importer.FromLinkAsync(link);
            else
                throw new Exception("Paste a link from YouTube, YouTube Music, SoundCloud or Spotify.");

            if (tracks.Count == 0) ImportStatus.Text = "Nothing found at that link.";
            else
            {
                if (browsing) await SetBrowsingAsync(false);
                ShowTracks(tracks, $"Imported {tracks.Count} song{(tracks.Count == 1 ? "" : "s")}");
                ImportBox.Text = "";
                ToggleImport(false);
            }
        }
        catch (Exception ex) { ImportStatus.Text = ex.Message; }
        ImportGo.IsEnabled = true;
    }

    // ---- settings sheet ----
    void Settings_Click(object s, RoutedEventArgs e) => ToggleSettings(!settingsOpen);
    void CloseSettings_Click(object s, RoutedEventArgs e) => ToggleSettings(false);
    void Dim_Click(object s, MouseButtonEventArgs e) => ToggleSettings(false);

    async void ToggleSettings(bool open)
    {
        settingsOpen = open;
        WebAirspace();
        if (open)
        {
            Overlay.Visibility = Visibility.Visible;
            Anim(Dim, OpacityProperty, 1, 300);
            Anim(Sheet, OpacityProperty, 1, 350);
            Anim(SheetScale, ScaleTransform.ScaleXProperty, 1, 450);
            Anim(SheetScale, ScaleTransform.ScaleYProperty, 1, 450);
            await RefreshAccountsAsync();
        }
        else
        {
            Anim(Dim, OpacityProperty, 0, 250);
            Anim(Sheet, OpacityProperty, 0, 250);
            Anim(SheetScale, ScaleTransform.ScaleXProperty, .94, 280);
            Anim(SheetScale, ScaleTransform.ScaleYProperty, .94, 280, () => { if (!settingsOpen) Overlay.Visibility = Visibility.Collapsed; });
        }
    }

    void SetAvatar(string? url, string initial)
    {
        AvatarInitial.Text = initial;
        if (url != null)
        {
            try { AvatarBrush.ImageSource = new BitmapImage(new Uri(url)); AvatarInitial.Visibility = Visibility.Collapsed; return; }
            catch { }
        }
        AvatarBrush.ImageSource = null;
        AvatarInitial.Visibility = Visibility.Visible;
    }

    async Task RefreshAccountsAsync()
    {
        SoundCloudDetail.Text = scAcc is { SignedIn: true }
            ? $"SoundCloud: signed in as {scAcc.UserName ?? "you"}. Your likes, feed and playlists are under the SoundCloud tab."
            : "Search works out of the box. Sign in to SoundCloud (SoundCloud tab) to see your likes, feed and playlists.";

        if (spotify == null || !spotify.Enabled)
        {
            SpotifyName.Text = "Spotify";
            SpotifyDetail.Text = "Add SPOTIFY_CLIENT_ID to config.json to turn it on.";
            SpotifyAction.Visibility = Visibility.Collapsed;
            SetAvatar(null, "S");
            return;
        }
        SpotifyAction.Visibility = Visibility.Visible;
        if (!spotify.SignedIn)
        {
            SpotifyName.Text = "Not connected";
            SpotifyDetail.Text = "Connect your Premium account to play full tracks.";
            SpotifyAction.Content = "Connect";
            SetAvatar(null, "S");
            return;
        }

        SpotifyAction.Content = "Sign out";
        SpotifyName.Text = "Loading…";
        SpotifyDetail.Text = "";
        try
        {
            var pr = await spotify.ProfileAsync();
            if (pr == null)
            {
                SpotifyName.Text = "Connected";
                SpotifyDetail.Text = "Couldn't load your profile. Sign out and connect again.";
                SetAvatar(null, "S");
                return;
            }
            SpotifyName.Text = pr.Name;
            string plan = pr.Plan switch
            {
                "premium" => "Spotify Premium",
                null => "Connected",
                _ => "Free plan. Playback needs Premium"
            };
            SpotifyDetail.Text = pr.Country != null ? $"{plan} ({pr.Country})" : plan;
            SetAvatar(pr.Image, pr.Name.Length > 0 ? pr.Name[..1].ToUpper() : "S");
        }
        catch (Exception ex) { SpotifyName.Text = "Connected"; SpotifyDetail.Text = ex.Message; }
    }

    async void SpotifyAction_Click(object s, RoutedEventArgs e)
    {
        if (spotify == null) return;
        if (spotify.SignedIn)
        {
            if (activeSp != null) { await Safe(spotify.PauseAsync); activeSp = null; SetPlaying(false); }
            spotify.SignOut();
            await RefreshAccountsAsync();
            return;
        }
        SpotifyAction.IsEnabled = false;
        SpotifyDetail.Text = "Approve LiquidCloud in your browser…";
        try { await spotify.ConnectAsync(); }
        catch (Exception ex) { SpotifyDetail.Text = ex.Message; SpotifyAction.IsEnabled = true; return; }
        SpotifyAction.IsEnabled = true;
        await RefreshAccountsAsync();
    }

    // ---- memory ----
    static void TrimMemory()
    {
        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();
        try { EmptyWorkingSet(Process.GetCurrentProcess().Handle); } catch { }
    }

    void Close_Click(object s, RoutedEventArgs e) => Close();
    void Min_Click(object s, RoutedEventArgs e) => WindowState = WindowState.Minimized;
    void Max_Click(object s, RoutedEventArgs e) => WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;
}
