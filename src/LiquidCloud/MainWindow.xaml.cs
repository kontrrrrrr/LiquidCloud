using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace LiquidCloud;

// LiquidCloud. Based on GlassPlayer by Jonzu00 (GPL-3.0); see README.md for what changed.
// This file: startup and shutdown, the window, and wiring the PlayerService / PlayerViewModel to the UI.
// Playback logic itself lives in Core/PlayerService.cs.
public partial class MainWindow : Window
{
    [DllImport("dwmapi.dll")] static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);
    [DllImport("psapi.dll")] static extern bool EmptyWorkingSet(IntPtr process);

    readonly AppSettings cfg = AppSettings.Load();
    readonly Dictionary<string, IProvider> providers = new();
    readonly AudioEngine engine;
    readonly RoutingOutput output;
    readonly AppResolver resolver;
    readonly PlayerService player;
    readonly PlayerViewModel vm;
    readonly DispatcherTimer timer = new() { Interval = TimeSpan.FromMilliseconds(250) };
    IProvider? provider;
    Spotify? spotify;
    bool settingsOpen;
    int sessionTicks;

    // short names used across the window's partial files
    Track? nowPlaying => player.Current;
    bool playing => player.IsPlaying;
    PlayQueue queue => player.Queue;

    public MainWindow()
    {
        ImageCache.Shared = ImageCache.CreateDefault(AppEnv.LocalDir, () => AppEnv.Offline);
        InitializeComponent();

        Library.Load();
        ThemeStore.Load();
        Motion.UserReduced = cfg.ReduceMotion;
        Motion.Speed = 1 / Math.Clamp(cfg.AnimSpeed, .3, 3);

        Bg.Run(Task.Run(Cache.Cleanup), "Cache", "Cleanup");
        Bg.Run(Task.Run(ImageCache.Shared.Trim), "Images", "Trim disk cache");
        Spotify.AutoLaunch = cfg.LaunchSpotify;
        foreach (IProvider p in new IProvider[] { new SoundCloud(), new YouTubeMusic(), new Spotify(), new LocalFiles() })
            providers[p.Name] = p;
        provider = providers["SoundCloud"];
        ScApi.Init((SoundCloud)providers["SoundCloud"]);
        spotify = (Spotify)providers["Spotify"];
        spotify.Web = new WebPlayer(Web, spotify);

        // playback: VLC for streams and files, Spotify Connect for Spotify; the service decides what plays when
        engine = new AudioEngine(Dispatcher, cfg.RememberVolume ? cfg.Volume : .8);
        output = new RoutingOutput(engine, new SpotifyOutput(spotify));
        resolver = new AppResolver(providers);
        player = new PlayerService(output, resolver)
        {
            AutoAdvance = cfg.Autoplay,
            AutoRadio = cfg.AutoRadio,
            Related = RelatedForAsync,
            Fallback = FallbackForAsync,
        };
        vm = new PlayerViewModel(player, Library.IsFavorite);
        WireCommands();
        Root.DataContext = vm;

        player.TrackChanged += OnTrackChanged;
        player.StateChanged += OnStateChanged;
        player.Started += OnStarted;
        player.Notice += msg => Toast(msg, "");
        player.RelatedAdded += n => Toast(Loc.T("Up next: {0} similar songs", n), "");
        vm.PropertyChanged += (_, e) => { if (e.PropertyName == nameof(PlayerViewModel.QueuePosition)) SyncQueueSelection(); };

        InitLook();
        InitLyricsLayout();
        InitShell();
        InitExtras();

        RestoreSession();
        if (AppEnv.Files.Count > 0) Dispatcher.InvokeAsync(() => AddLocal(AppEnv.Files), DispatcherPriority.Background);

        timer.Tick += (_, _) => Tick();
        timer.Start();

        Loaded += (_, _) =>
        {
            if (spotify is { SignedIn: true, Web: not null } && !AppEnv.Offline) Bg.Run(spotify.Web.StartAsync(), "Spotify", "Web player start");
            StartBlobs();
            ApplyActivity();
            ShowWelcomeOrApp();
            if (AppEnv.Smoke) Bg.Run(RunSmokeAsync(), "Smoke", "Self-check");
        };

        // drop audio files or folders onto the window
        AllowDrop = true;
        Drop += (_, e) => { if (e.Data.GetData(DataFormats.FileDrop) is string[] paths) AddLocal(paths); };

        // working in other windows for a while: hand unused memory back to Windows
        Activated += (_, _) => { idleTrim.Stop(); ApplyActivity(); };
        Deactivated += (_, _) => { ApplyActivity(); idleTrim.Stop(); idleTrim.Start(); };
        StateChanged += (_, _) => ApplyActivity();
    }

    void WireCommands()
    {
        // seek slider: hold → move freely, release → one jump (also when the thumb loses the mouse unexpectedly)
        Seek.PreviewMouseLeftButtonDown += (_, _) => vm.HoldSeek = true;
        Seek.PreviewMouseLeftButtonUp += (_, _) => vm.CommitSeek();
        Seek.LostMouseCapture += (_, _) => { if (vm.HoldSeek && Mouse.LeftButton == MouseButtonState.Released) vm.CommitSeek(); };
        vm.PlayPauseCommand = new RelayCommand(() => Bg.Run(TogglePlayAsync(), "Player", "Play/pause"));
        vm.NextCommand = new RelayCommand(() => Bg.Run(player.NextAsync(), "Player", "Next"));
        vm.PreviousCommand = new RelayCommand(() => Bg.Run(player.PreviousAsync(), "Player", "Previous"));
        vm.ShuffleCommand = new RelayCommand(ToggleShuffle);
        vm.RepeatCommand = new RelayCommand(CycleRepeat);
        vm.MuteCommand = new RelayCommand(ToggleMute);
        vm.LikeCommand = new RelayCommand(() => { if ((nowPlaying ?? queue.Current) is { } t) ToggleFavorite(t, LikeBtn); });
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
        RegisterMediaKeys(hwnd);
    }

    bool closing;
    protected override void OnClosing(System.ComponentModel.CancelEventArgs e)
    {
        if (!closing)
        {
            e.Cancel = true; // play the exit animation first, then close for real
            closing = true;
            if (sc != null) sc.Visibility = Visibility.Hidden;
            SaveSession();
            Motion.To(Root, OpacityProperty, 0, Motion.Base, Motion.Exit, () => Close());
            Motion.To(Ambient, OpacityProperty, 0, Motion.Base);
            return;
        }
        base.OnClosing(e);
    }

    protected override void OnClosed(EventArgs e)
    {
        timer.Stop();
        cfg.Volume = player.Muted ? Math.Max(.05, cfg.Volume) : player.Volume;
        cfg.Save();
        Library.Save();
        pageCts?.Cancel();
        DisposeSc();
        discord?.Dispose();
        engine.Dispose();
        Log.Info("App", "Closed");
        base.OnClosed(e);
        Environment.Exit(0); // make sure no background audio/web processes keep LiquidCloud alive
    }

    static string Fmt(long ms) => PlayerViewModel.Fmt(ms);

    // compatibility helpers used by the other partial files
    static void Anim(IAnimatable target, DependencyProperty prop, double to, int ms = Motion.Base, Action? done = null) => Motion.To(target, prop, to, ms, null, done);

    // ---------------- timer ----------------
    void Tick()
    {
        player.Tick();
        vm.Refresh(vm.HoldSeek || Seek.IsMouseCaptureWithin);
        var (pos, len) = player.Times;
        UpdateLyrics(pos);
        MaybePrepareNext(pos, len);
        UpdateDiscord(false);
        if (++sessionTicks % 60 == 0) SaveSession(); // every ~15 s
    }

    // ---------------- reacting to the player ----------------
    void OnTrackChanged()
    {
        var t = player.Current;
        if (t == null) return;
        Library.AddHistory(t);
        ShowTrackVisuals(t);
        ShowNoLyrics("Loading lyrics…");
        preparedFor = null;
        UpdateDiscord();
    }

    void ShowTrackVisuals(Track t)
    {
        Motion.FromTo(TitleText, OpacityProperty, 0, 1, Motion.Slow);
        Motion.FromTo(ArtistText, OpacityProperty, 0, 1, Motion.Slow + 120);
        Motion.FromTo(ArtBox, OpacityProperty, .25, 1, Motion.Slow + 80);
        SetArtUrl(t.Art);
        vm.RefreshFavorite();
        HighlightPlaying();
    }

    void OnStateChanged()
    {
        SetPlayingVisuals(player.IsPlaying);
        UpdateDiscord();
    }

    void OnStarted(Track t, string url)
    {
        if (!url.StartsWith("http", StringComparison.OrdinalIgnoreCase) && !url.StartsWith("spotify:", StringComparison.OrdinalIgnoreCase))
            Bg.Run(LoadFileExtrasAsync(url, t), "Lyrics", "Read file tags");
        else ShowNoLyrics(t.Source == "Spotify" ? "Lyrics are read from audio files, so they aren't available for Spotify." : "");
    }

    /// <summary>Lyrics and embedded cover straight from the song file.</summary>
    async Task LoadFileExtrasAsync(string path, Track t)
    {
        var (lines, synced, cover) = await Task.Run(() => LyricsReader.Read(path, 600));
        if (!ReferenceEquals(nowPlaying, t)) return;
        if (cover != null && t.Art == null) SetArt(cover);
        SetLyrics(lines, synced);
    }

    void SyncQueueSelection()
    {
        updatingList = true;
        List.SelectedIndex = vm.QueuePosition;
        updatingList = false;
        if (vm.QueuePosition >= 0 && QueueView.IsVisible && List.SelectedItem != null) List.ScrollIntoView(List.SelectedItem);
    }

    bool updatingList;

    void List_SelectionChanged(object s, SelectionChangedEventArgs e)
    {
        if (updatingList || List.SelectedIndex < 0 || List.SelectedIndex == queue.Position) return;
        SavePartialPosition();
        Bg.Run(player.JumpToAsync(List.SelectedIndex), "Player", "Jump in queue");
    }

    void ClearQueue_Click(object s, RoutedEventArgs e)
    {
        queue.ClearUpcoming();
        Toast(Loc.T("Queue cleared"), "");
    }

    // ---------------- actions other parts of the app use ----------------
    public void PlayContext(IList<Track> tracks, int index, string name)
    {
        name = Loc.T(name);
        SavePartialPosition();
        Bg.Run(player.PlayContextAsync(tracks, index, name), "Player", "Play list");
    }

    public void PlayShuffled(IList<Track> tracks, string name)
    {
        if (tracks.Count == 0) return;
        name = Loc.T(name);
        SavePartialPosition();
        Bg.Run(player.PlayContextAsync(tracks, Random.Shared.Next(tracks.Count), name, shuffle: true), "Player", "Shuffle play");
    }

    public void PlayNow(Track t)
    {
        SavePartialPosition();
        Bg.Run(player.PlayNowAsync(t), "Player", "Play now");
    }

    public void PlayNextInQueue(Track t)
    {
        if (queue.Count == 0) { PlayNow(t); return; }
        queue.PlayNext(t);
        Toast(Loc.T("Plays next"), "");
    }

    public void AddToQueue(IEnumerable<Track> tracks)
    {
        var list = tracks.ToList();
        if (list.Count == 0) return;
        if (queue.Count == 0) { PlayContext(list, 0, list.Count == 1 ? list[0].Title : Loc.T("Your queue")); return; }
        queue.Add(list);
        Toast(list.Count == 1 ? Loc.T("Added to queue") : Loc.T("Added {0} to the queue", Loc.Songs(list.Count)), "");
    }

    Task AdvanceAsync(bool auto) { SavePartialPosition(); return player.NextAsync(auto); }

    void Prev_Click(object s, RoutedEventArgs e) => Bg.Run(player.PreviousAsync(), "Player", "Previous");

    Task TogglePlayAsync()
    {
        long resume = player.Current == null ? Math.Max(Library.Data.Session.PositionMs, queue.Current != null ? Library.ResumePosition(queue.Current) : 0) : 0;
        return player.TogglePlayAsync(resume);
    }

    (long Pos, long Len) CurrentTimes() => player.Times;

    void SeekTo(long ms) { player.Seek(ms); lastDiscord = DateTime.MinValue; }
    void SeekBy(long deltaMs) { player.SeekBy(deltaMs); lastDiscord = DateTime.MinValue; }

    void ToggleMute() { vm.ToggleMute(); Motion.Pulse(MuteBtn, 1.15); }
    void NudgeVolume(double d) => vm.Volume = Math.Clamp(vm.Volume + d, 0, 1);

    void ToggleShuffle()
    {
        player.SetShuffle(!queue.Shuffle);
        vm.RefreshModes();
        Motion.Pulse(ShuffleBtn, 1.2);
        Toast(queue.Shuffle ? Loc.T("Shuffle on") : Loc.T("Shuffle off"), "");
    }

    void CycleRepeat()
    {
        var mode = player.CycleRepeat();
        vm.RefreshModes();
        Motion.Pulse(RepeatBtn, 1.2);
        Toast(mode switch { RepeatMode.All => Loc.T("Repeat all"), RepeatMode.One => Loc.T("Repeat this song"), _ => Loc.T("Repeat off") }, "");
        SaveSession();
    }

    void UpdateModeButtons() => vm.RefreshModes();
    void UpdateLikeButton() => vm.RefreshFavorite();

    // ---- cover art ----
    void SetArtUrl(string? url)
    {
        if (url == null) { SetArt(null); return; }
        var version = ++artVersion;
        SetArt(null);
        Bg.Run(LoadArtAsync(url, version), "Images", "Cover");
    }

    int artVersion;

    async Task LoadArtAsync(string url, int version)
    {
        var img = await ImageCache.Shared.GetAsync(url, 600, CancellationToken.None);
        if (version == artVersion && img != null) SetArt(img);
    }

    void SetArt(ImageSource? img)
    {
        ArtBrush.ImageSource = img;
        MiniArt.ImageSource = img;
        ArtPlaceholder.Visibility = img == null ? Visibility.Visible : Visibility.Collapsed;
        SetGlow(img as BitmapSource);
    }

    BitmapSource? glowArt;
    int glowVersion;

    /// <summary>The blurred cover behind everything. Made once per cover on a background thread (see SoftBlur).</summary>
    void SetGlow(BitmapSource? art)
    {
        glowArt = art;
        int v = ++glowVersion;
        if (art == null || !cfg.AmbientGlow) { Glow.Source = null; return; }
        double width = Math.Max(ActualWidth, 800);
        Bg.Run(Task.Run(() => SoftBlur.Make(art, 120, width)).ContinueWith(t =>
        {
            if (t.IsFaulted) { Log.Warn("Look", "Couldn't blur the cover", t.Exception?.GetBaseException()); return; }
            if (v != glowVersion || !t.IsCompletedSuccessfully) return;
            Glow.Source = t.Result;
            Motion.FromTo(Glow, OpacityProperty, 0, CurrentTheme.Glow, Motion.Slow * 3);
        }, TaskScheduler.FromCurrentSynchronizationContext()), "Look", "Cover glow");
    }

    void SetPlayingVisuals(bool on)
    {
        double scale = on ? 1.0 : 0.9; // cover swells while playing, settles when paused
        Motion.To(ArtScale, ScaleTransform.ScaleXProperty, scale, Motion.Morph, Motion.Spring);
        Motion.To(ArtScale, ScaleTransform.ScaleYProperty, scale, Motion.Morph, Motion.Spring);
        UpdateLiveBits();
    }

    bool ringOn;

    /// <summary>Soft pulsing ring around the play button: only while playing and LiquidCloud is in front, at 30 fps.</summary>
    void UpdatePlayRing(bool live)
    {
        bool want = playing && live && !Motion.Reduced && !miniMode;
        if (want == ringOn) return;
        ringOn = want;
        if (want)
        {
            var dur = TimeSpan.FromSeconds(1.9);
            var grow = new System.Windows.Media.Animation.DoubleAnimation(1, 1.42, dur) { RepeatBehavior = System.Windows.Media.Animation.RepeatBehavior.Forever, EasingFunction = Motion.Standard };
            var fade = new System.Windows.Media.Animation.DoubleAnimation(.6, 0, dur) { RepeatBehavior = System.Windows.Media.Animation.RepeatBehavior.Forever };
            System.Windows.Media.Animation.Timeline.SetDesiredFrameRate(grow, 30);
            System.Windows.Media.Animation.Timeline.SetDesiredFrameRate(fade, 30);
            PlayRingScale.BeginAnimation(ScaleTransform.ScaleXProperty, grow);
            PlayRingScale.BeginAnimation(ScaleTransform.ScaleYProperty, grow);
            PlayRing.BeginAnimation(OpacityProperty, fade);
        }
        else
        {
            PlayRingScale.BeginAnimation(ScaleTransform.ScaleXProperty, null);
            PlayRingScale.BeginAnimation(ScaleTransform.ScaleYProperty, null);
            PlayRing.BeginAnimation(OpacityProperty, null);
            PlayRing.Opacity = 0;
        }
    }

    // ---- favorites ----
    /// <summary>Favorites live in LiquidCloud; SoundCloud songs are also liked on SoundCloud when you're signed in.</summary>
    public void ToggleFavorite(Track t, UIElement? animate = null)
    {
        bool fav = Library.ToggleFavorite(t);
        if (animate != null) Motion.Pulse(animate, 1.35);
        vm.RefreshFavorite();
        string where = Loc.T("in LiquidCloud");
        if (t.Source == "SoundCloud" && ScApi.Account.SignedIn && cfg.LikeOnSoundCloud)
        {
            where = Loc.T("in LiquidCloud and on SoundCloud");
            Bg.Run(SyncLikeAsync(t, fav), "SoundCloud", "Like");
        }
        Toast(fav ? Loc.T("Added to favorites {0}", where) : Loc.T("Removed from favorites {0}", where), fav ? "" : "");
    }

    async Task SyncLikeAsync(Track t, bool like)
    {
        try { await ScApi.SetLikeAsync(t, like); }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Log.Warn("SoundCloud", "Like not saved on SoundCloud", ex);
            Toast(Loc.T("Saved in LiquidCloud only. SoundCloud: {0}", ex.Message), "");
        }
    }

    void AddCurrentToPlaylist_Click(object s, RoutedEventArgs e)
    {
        if ((nowPlaying ?? queue.Current) is not { } t) return;
        var menu = new ContextMenu();
        FillAddToPlaylist(menu.Items, new[] { t });
        OpenMenu(menu, (FrameworkElement)s);
    }

    // ---- session: remember the queue and where you were ----
    void SaveSession()
    {
        var (pos, _) = player.Times;
        Library.SaveSession(queue.Snapshot(nowPlaying != null ? pos : Library.Data.Session.PositionMs));
    }

    void RestoreSession()
    {
        var s = Library.Data.Session;
        if (s.Queue.Count == 0) { vm.ShowIdle(null); return; }
        queue.Restore(s);
        vm.RefreshModes();
        if (queue.Current is { } t)
        {
            vm.ShowIdle(t);
            SetArtUrl(t.Art);
            vm.ShowPosition(s.PositionMs, t.DurationMs);
            vm.Status = Loc.T("Press play to continue where you left off");
        }
    }

    void SavePartialPosition()
    {
        if (nowPlaying == null) return;
        var (pos, len) = player.Times;
        Library.SavePartial(nowPlaying, pos, len);
    }

    // ---- local files (drag & drop, "Open with", Library buttons) ----
    static readonly string[] AudioExt = { ".mp3", ".m4a", ".aac", ".wav", ".flac", ".ogg", ".opus", ".wma" };

    void AddLocal(IEnumerable<string> paths)
    {
        var files = new List<string>();
        foreach (var p in paths)
        {
            if (Directory.Exists(p))
            {
                try { files.AddRange(Directory.EnumerateFiles(p, "*.*", SearchOption.AllDirectories).Where(f => AudioExt.Contains(Path.GetExtension(f).ToLowerInvariant()))); }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { Log.Warn("Files", $"Couldn't read folder {p}", ex); }
            }
            else if (AudioExt.Contains(Path.GetExtension(p).ToLowerInvariant())) files.Add(p);
        }
        if (files.Count == 0) { Toast(Loc.T("No audio files found"), ""); return; }
        var tracks = files.Select(LocalFiles.ToTrack).ToList();
        ShowTrackListPage(Loc.T("From this PC"), Loc.T("Files from this PC"), tracks, "");
        PlayContext(tracks, 0, Loc.T("Files from this PC"));
    }

    void PickFiles()
    {
        var dlg = new Microsoft.Win32.OpenFileDialog { Multiselect = true, Filter = Loc.T("Audio files") + "|*.mp3;*.m4a;*.aac;*.wav;*.flac;*.ogg;*.opus;*.wma|" + Loc.T("All files") + "|*.*" };
        if (dlg.ShowDialog() == true) AddLocal(dlg.FileNames);
    }

    void PickFolder()
    {
        var dlg = new Microsoft.Win32.OpenFolderDialog { Multiselect = true };
        if (dlg.ShowDialog() == true) AddLocal(dlg.FolderNames);
    }

    static bool IsLink(string s) => Regex.IsMatch(s, @"^https?://\S+$", RegexOptions.IgnoreCase);

    async Task<List<Track>> ImportLinkAsync(string link, CancellationToken ct = default)
    {
        AppEnv.EnsureOnline();
        var sp = Regex.Match(link, "playlist[/:]([A-Za-z0-9]+)");
        if (link.Contains("spotify", StringComparison.OrdinalIgnoreCase))
        {
            if (!sp.Success) throw new InvalidOperationException(Loc.T("For Spotify, paste a playlist link."));
            if (!spotify!.Enabled) throw new InvalidOperationException(Loc.T("Spotify needs a Client ID first (see Settings)."));
            return await spotify.PlaylistAsync(sp.Groups[1].Value);
        }
        return await Importer.FromLinkAsync(link); // checks the link is a real YouTube / SoundCloud address
    }

    // ---- settings sheet ----
    void Settings_Click(object s, RoutedEventArgs e) => ToggleSettings(!settingsOpen);
    void CloseSettings_Click(object s, RoutedEventArgs e) => ToggleSettings(false);
    void Dim_Click(object s, MouseButtonEventArgs e) => ToggleSettings(false);

    void ToggleSettings(bool open)
    {
        settingsOpen = open;
        WebAirspace();
        if (open)
        {
            BuildSettings();
            Overlay.Visibility = Visibility.Visible;
            Motion.To(Dim, OpacityProperty, 1, Motion.Base);
            Motion.To(Sheet, OpacityProperty, 1, Motion.Base);
            Motion.To(SheetScale, ScaleTransform.ScaleXProperty, 1, Motion.Slow, Motion.Spring);
            Motion.To(SheetScale, ScaleTransform.ScaleYProperty, 1, Motion.Slow, Motion.Spring);
            Bg.Run(RefreshAccountsAsync(), "Settings", "Accounts");
            SettingsScroll.Focus();
        }
        else
        {
            Motion.To(Dim, OpacityProperty, 0, Motion.Base);
            Motion.To(Sheet, OpacityProperty, 0, Motion.Fast);
            Motion.To(SheetScale, ScaleTransform.ScaleXProperty, .96, Motion.Base);
            Motion.To(SheetScale, ScaleTransform.ScaleYProperty, .96, Motion.Base, null, () => { if (!settingsOpen) Overlay.Visibility = Visibility.Collapsed; });
        }
    }

    void SetAvatar(string? url, string initial)
    {
        AvatarInitial.Text = initial;
        AvatarBrush.ImageSource = null;
        AvatarInitial.Visibility = Visibility.Visible;
        if (url == null) return;
        Bg.Run(SetAvatarAsync(url), "Images", "Avatar");
    }

    async Task SetAvatarAsync(string url)
    {
        var img = await ImageCache.Shared.GetAsync(url, 96, CancellationToken.None);
        if (img == null) return;
        AvatarBrush.ImageSource = img;
        AvatarInitial.Visibility = Visibility.Collapsed;
    }

    async Task RefreshAccountsAsync()
    {
        SoundCloudDetail.Text = ScApi.Account.SignedIn
            ? Loc.T("SoundCloud: signed in as {0}. Your likes, feed and playlists are in Library.", ScApi.Account.UserName ?? Loc.T("you"))
            : Loc.T("Search works out of the box. Sign in to SoundCloud (bottom of the sidebar) for your likes, feed and playlists.");

        if (spotify == null || !spotify.Enabled)
        {
            SpotifyName.Text = Loc.T("Spotify");
            SpotifyDetail.Text = Loc.T("Add SPOTIFY_CLIENT_ID to config.json to turn it on.");
            SpotifyAction.Visibility = Visibility.Collapsed;
            SetAvatar(null, "S");
            return;
        }
        SpotifyAction.Visibility = Visibility.Visible;
        if (!spotify.SignedIn)
        {
            SpotifyName.Text = Loc.T("Not connected");
            SpotifyDetail.Text = Loc.T("Connect your Premium account to play full tracks.");
            SpotifyAction.Content = Loc.T("Connect");
            SetAvatar(null, "S");
            return;
        }

        SpotifyAction.Content = Loc.T("Sign out");
        SpotifyName.Text = Loc.T("Loading…");
        SpotifyDetail.Text = "";
        try
        {
            AppEnv.EnsureOnline();
            var pr = await spotify.ProfileAsync();
            if (pr == null)
            {
                SpotifyName.Text = Loc.T("Connected");
                SpotifyDetail.Text = Loc.T("Couldn't load your profile. Sign out and connect again.");
                SetAvatar(null, "S");
                return;
            }
            SpotifyName.Text = pr.Name;
            string plan = pr.Plan switch
            {
                "premium" => "Spotify Premium",
                null => Loc.T("Connected"),
                _ => Loc.T("Free plan. Playback needs Premium")
            };
            SpotifyDetail.Text = pr.Country != null ? $"{plan} ({pr.Country})" : plan;
            SetAvatar(pr.Image, pr.Name.Length > 0 ? pr.Name[..1].ToUpper() : "S");
        }
        catch (Exception ex)
        {
            Log.Warn("Spotify", "Profile", ex);
            SpotifyName.Text = Loc.T("Connected");
            SpotifyDetail.Text = ex.Message;
        }
    }

    async void SpotifyAction_Click(object s, RoutedEventArgs e) // event handler: errors handled inside
    {
        if (spotify == null) return;
        try
        {
            if (spotify.SignedIn)
            {
                if (output.OnSpotify) player.Stop();
                spotify.SignOut();
                await RefreshAccountsAsync();
                return;
            }
            SpotifyAction.IsEnabled = false;
            SpotifyDetail.Text = Loc.T("Approve LiquidCloud in your browser…");
            AppEnv.EnsureOnline();
            await spotify.ConnectAsync();
            await RefreshAccountsAsync();
        }
        catch (Exception ex)
        {
            Log.Warn("Spotify", "Connect", ex);
            SpotifyDetail.Text = ex.Message;
        }
        finally { SpotifyAction.IsEnabled = true; }
    }

    // ---- memory ----
    static void TrimMemory()
    {
        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();
        try { EmptyWorkingSet(Process.GetCurrentProcess().Handle); }
        catch (Exception ex) { Log.Warn("Memory", "Trim", ex); }
    }

    void Close_Click(object s, RoutedEventArgs e) => Close();
    void Min_Click(object s, RoutedEventArgs e) => WindowState = WindowState.Minimized;
    void Max_Click(object s, RoutedEventArgs e) => WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;
}
