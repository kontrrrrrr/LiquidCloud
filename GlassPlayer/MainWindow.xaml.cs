using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace GlassPlayer;

// LiquidCloud. Based on GlassPlayer by Jonzu00 (GPL-3.0); see README.md for what changed.
// This file: startup, the window itself, playback through the global queue, and the transport controls.
public partial class MainWindow : Window
{
    [DllImport("dwmapi.dll")] static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);
    [DllImport("psapi.dll")] static extern bool EmptyWorkingSet(IntPtr process);

    readonly AppSettings cfg = AppSettings.Load();
    readonly PlayQueue queue = new();
    AudioEngine? engine;
    long fallbackMs;
    readonly DispatcherTimer timer = new() { Interval = TimeSpan.FromMilliseconds(250) };
    readonly DispatcherTimer seekDebounce = new() { Interval = TimeSpan.FromMilliseconds(300) };
    readonly DispatcherTimer volDebounce = new() { Interval = TimeSpan.FromMilliseconds(300) };
    readonly Dictionary<string, IProvider> providers = new();
    IProvider? provider;
    Spotify? spotify;
    bool updating, playing, advanced, polling, settingsOpen, importOpen, updatingList;
    long pendingResumeMs;
    double volumeBeforeMute = .8;
    int sessionTicks;

    // Spotify remote state: non-null while a Spotify track is the one playing
    Spotify? activeSp;
    Track? nowPlaying;
    int rProgress, rDuration;
    DateTime rStamp, lastPoll, playStart;
    bool rPlaying;

    public MainWindow()
    {
        InitializeComponent();

        Library.Load();
        ThemeStore.Load();
        Motion.UserReduced = cfg.ReduceMotion;
        Motion.Speed = 1 / Math.Clamp(cfg.AnimSpeed, .3, 3);

        _ = Task.Run(Cache.Cleanup);
        Spotify.AutoLaunch = cfg.LaunchSpotify;
        foreach (IProvider p in new IProvider[] { new SoundCloud(), new YouTubeMusic(), new Spotify(), new LocalFiles() })
            providers[p.Name] = p;
        provider = providers["SoundCloud"];
        ScApi.Init((SoundCloud)providers["SoundCloud"]);
        spotify = (Spotify)providers["Spotify"];
        spotify.Web = new WebPlayer(Web, spotify);

        if (cfg.RememberVolume) Volume.Value = cfg.Volume;

        InitLook();
        InitLyricsLayout();
        InitShell();
        InitExtras();
        StartEngine();

        queue.Changed += OnQueueChanged;
        RestoreSession();

        seekDebounce.Tick += async (_, _) => { seekDebounce.Stop(); await Safe(() => activeSp?.SeekAsync((int)(Seek.Value * 1000)) ?? Task.CompletedTask); };
        volDebounce.Tick += async (_, _) => { volDebounce.Stop(); await Safe(() => activeSp?.VolumeAsync((int)(Volume.Value * 100)) ?? Task.CompletedTask); };
        timer.Tick += async (_, _) => await TickAsync();
        timer.Start();

        Loaded += (_, _) =>
        {
            if (spotify is { SignedIn: true, Web: not null }) _ = spotify.Web.StartAsync();
            StartBlobs();
            ApplyActivity();
            ShowWelcomeOrApp();
        };

        // drop audio files or folders onto the window
        AllowDrop = true;
        Drop += (_, e) => { if (e.Data.GetData(DataFormats.FileDrop) is string[] paths) AddLocal(paths); };

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
        cfg.Volume = muted ? volumeBeforeMute : Volume.Value;
        cfg.Save();
        Library.Save();
        DisposeSc();
        discord?.Dispose();
        base.OnClosed(e);
        Environment.Exit(0); // make sure no background audio/web processes keep LiquidCloud alive
    }

    static string Fmt(TimeSpan t) => t.TotalHours >= 1 ? $"{(int)t.TotalHours}:{t.Minutes:00}:{t.Seconds:00}" : $"{(int)t.TotalMinutes}:{t.Seconds:00}";
    static string Fmt(long ms) => Fmt(TimeSpan.FromMilliseconds(Math.Max(0, ms)));

    async Task Safe(Func<Task> action)
    {
        try { await action(); } catch (Exception ex) { Status.Text = ex.Message; }
    }

    // kept for older call sites: everything goes through the Motion system
    static void Anim(IAnimatable target, DependencyProperty prop, double to, int ms = Motion.Base, Action? done = null) => Motion.To(target, prop, to, ms, null, done);
    static void Fade(UIElement el, double from, double to, int ms) => Motion.FromTo(el, OpacityProperty, from, to, ms);

    void StartEngine()
    {
        var eng = new AudioEngine(Dispatcher, Volume.Value);
        eng.Ended += async () =>
        {
            if (activeSp != null) return;
            if ((DateTime.UtcNow - playStart).TotalSeconds < 3)
            {
                if (await TryDownloadInsteadAsync()) return;
                Status.Text = "Couldn't play this track. Skipping…";
                await Task.Delay(800);
            }
            await AdvanceAsync(true);
        };
        eng.Failed += async msg =>
        {
            if (await TryDownloadInsteadAsync()) return;
            Status.Text = msg; SetPlaying(false);
        };
        engine = eng;
    }

    async Task TickAsync()
    {
        if (activeSp != null) { await RemoteTickAsync(activeSp); return; }
        if (engine == null) return;
        long len = engine.LengthMs > 0 ? engine.LengthMs : fallbackMs;
        if (len <= 0) return;
        long pos = Math.Max(0, engine.TimeMs);
        if (pendingResumeMs > 0 && engine.LengthMs > 0 && pos < 2000)
        {
            engine.TimeMs = pendingResumeMs; // continue where you left off
            pendingResumeMs = 0;
        }
        updating = true;
        Seek.Maximum = len / 1000.0;
        if (!Seek.IsMouseCaptureWithin) Seek.Value = pos / 1000.0;
        Cur.Text = Fmt(pos);
        Dur.Text = Fmt(len);
        MiniProgress.Value = len > 0 ? (double)pos / len : 0;
        updating = false;
        UpdateLyrics(pos);
        MaybePreloadNext(pos, len);
        UpdateDiscord(false);
        if (++sessionTicks % 60 == 0) SaveSession(); // every ~15 s
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
        Cur.Text = Fmt(pos);
        Dur.Text = Fmt(rDuration);
        MiniProgress.Value = (double)pos / rDuration;
        updating = false;
        if (rPlaying && pos >= rDuration - 1500 && !advanced) { advanced = true; await AdvanceAsync(true); }
        UpdateDiscord(false);
    }

    // =====================================================================
    // Playing music: everything goes through the global queue
    // =====================================================================

    /// <summary>Plays a list starting at one song (the list becomes the queue).</summary>
    public void PlayContext(IList<Track> tracks, int index, string name)
    {
        if (tracks.Count == 0) return;
        index = Math.Clamp(index, 0, tracks.Count - 1);
        queue.Set(tracks, index, name);
        _ = PlayAsync(queue.Current!);
    }

    public void PlayShuffled(IList<Track> tracks, string name)
    {
        if (tracks.Count == 0) return;
        queue.SetShuffle(true);
        queue.Set(tracks, new Random().Next(tracks.Count), name);
        UpdateModeButtons();
        _ = PlayAsync(queue.Current!);
    }

    public void PlayNextInQueue(Track t)
    {
        if (queue.Count == 0) { PlayContext(new List<Track> { t }, 0, t.Title); return; }
        queue.PlayNext(t);
        Toast("Plays next", "");
    }

    public void AddToQueue(IEnumerable<Track> tracks)
    {
        var list = tracks.ToList();
        if (list.Count == 0) return;
        if (queue.Count == 0) { PlayContext(list, 0, list.Count == 1 ? list[0].Title : "Your queue"); return; }
        queue.Add(list);
        Toast(list.Count == 1 ? "Added to queue" : $"Added {list.Count} songs to the queue", "");
    }

    /// <summary>Next song after the current one ends (auto) or on the Next button.</summary>
    async Task AdvanceAsync(bool auto)
    {
        SavePartialPosition();
        var next = queue.Next(auto);
        if (next == null)
        {
            // end of the queue: keep going with suggested songs, like SoundCloud does
            if (cfg.Autoplay && cfg.AutoRadio && queue.Current is { } last && last.Source is "SoundCloud" or "YouTube Music")
            {
                Status.Text = "Finding similar songs…";
                if (await AppendRelatedAsync(last)) next = queue.Next(false);
                Status.Text = "";
            }
            if (next == null)
            {
                if (auto) { SetPlaying(false); UpdateDiscord(); Status.Text = "End of the queue"; }
                return;
            }
        }
        if (auto && !cfg.Autoplay && queue.Repeat != RepeatMode.One) { SetPlaying(false); return; }
        await PlayAsync(next);
    }

    async void Next_Click(object s, RoutedEventArgs e) => await AdvanceAsync(false);

    async void Prev_Click(object s, RoutedEventArgs e)
    {
        // like most players: restart the song if it's been playing a few seconds, otherwise go back
        var (pos, _) = CurrentTimes();
        if (pos > 4000 && nowPlaying != null) { SeekTo(0); return; }
        SavePartialPosition();
        var prev = queue.Previous();
        if (prev != null) await PlayAsync(prev);
    }

    void OnQueueChanged()
    {
        updatingList = true;
        var items = queue.InPlayOrder();
        List.ItemsSource = items;
        List.SelectedIndex = queue.Position;
        updatingList = false;
        QueueName.Text = string.IsNullOrEmpty(queue.Name) ? "Your queue" : queue.Name;
        if (queue.Position >= 0 && QueueView.IsVisible) List.ScrollIntoView(List.SelectedItem);
    }

    void List_SelectionChanged(object s, SelectionChangedEventArgs e)
    {
        if (updatingList || List.SelectedIndex < 0 || List.SelectedIndex == queue.Position) return;
        SavePartialPosition();
        if (queue.JumpTo(List.SelectedIndex) is { } t) _ = PlayAsync(t);
    }

    void ClearQueue_Click(object s, RoutedEventArgs e)
    {
        queue.ClearUpcoming();
        Toast("Queue cleared", "");
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
        MiniArt.ImageSource = img;
        ArtPlaceholder.Visibility = img == null ? Visibility.Visible : Visibility.Collapsed;
        Glow.Source = cfg.AmbientGlow ? img : null;
        if (img != null && cfg.AmbientGlow) Motion.FromTo(Glow, OpacityProperty, 0, CurrentTheme.Glow, Motion.Slow * 3);
    }

    void ShowTrackInfo(Track t)
    {
        TitleText.Text = ExpTitle.Text = MiniTitle.Text = t.Title;
        ArtistText.Text = ExpArtist.Text = MiniArtist.Text = t.Artist;
        var bits = new List<string> { t.Source == "Local" ? "On this PC" : t.Source };
        if (t.DurationMs > 0) bits.Add(t.DurationText);
        MetaText.Text = string.Join("  ·  ", bits);
        Motion.FromTo(TitleText, OpacityProperty, 0, 1, Motion.Slow);
        Motion.FromTo(ArtistText, OpacityProperty, 0, 1, Motion.Slow + 120);
        Motion.FromTo(ArtBox, OpacityProperty, .25, 1, Motion.Slow + 80);
        SetArtUrl(t.Art);
        UpdateLikeButton();
        HighlightPlaying();
    }

    public async Task PlayAsync(Track t)
    {
        nowPlaying = t; advanced = false; retriedDownload = false;
        fallbackMs = t.DurationMs;
        pendingResumeMs = Library.ResumePosition(t);
        updating = true; Seek.Maximum = 1; Seek.Value = 0; Cur.Text = "0:00"; Dur.Text = t.DurationMs > 0 ? t.DurationText : "0:00"; updating = false;
        ShowTrackInfo(t);
        ShowNoLyrics("Loading lyrics…");
        Library.AddHistory(t);
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
                if (nowPlaying != t) return; // you picked something else meanwhile
                if (url == null) { Status.Text = "This track can't be streamed. Skipping…"; await Task.Delay(900); if (nowPlaying == t) await AdvanceAsync(true); return; }
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
        PlayBtn.Content = MiniPlay.Content = on ? "" : "";
        double scale = on ? 1.0 : 0.9; // cover swells while playing, settles when paused
        Motion.To(ArtScale, ScaleTransform.ScaleXProperty, scale, Motion.Morph, Motion.Spring);
        Motion.To(ArtScale, ScaleTransform.ScaleYProperty, scale, Motion.Morph, Motion.Spring);
        Bars.Running = on;

        // soft pulsing ring around the play button while music plays
        if (on && !Motion.Reduced)
        {
            var dur = TimeSpan.FromSeconds(1.9);
            var grow = new DoubleAnimation(1, 1.42, dur) { RepeatBehavior = RepeatBehavior.Forever, EasingFunction = Motion.Standard };
            PlayRingScale.BeginAnimation(ScaleTransform.ScaleXProperty, grow);
            PlayRingScale.BeginAnimation(ScaleTransform.ScaleYProperty, grow);
            PlayRing.BeginAnimation(OpacityProperty, new DoubleAnimation(.6, 0, dur) { RepeatBehavior = RepeatBehavior.Forever });
        }
        else
        {
            PlayRingScale.BeginAnimation(ScaleTransform.ScaleXProperty, null);
            PlayRingScale.BeginAnimation(ScaleTransform.ScaleYProperty, null);
            PlayRing.BeginAnimation(OpacityProperty, null);
            PlayRing.Opacity = 0;
        }
    }

    async void PlayPause_Click(object s, RoutedEventArgs e) => await TogglePlayAsync();

    async Task TogglePlayAsync()
    {
        if (nowPlaying == null)
        {
            // restored session: start the remembered song where you left it
            if (queue.Current is { } c) { pendingResumeMs = Math.Max(pendingResumeMs, Library.Data.Session.PositionMs); await PlayAsync(c); }
            return;
        }
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

    void SeekTo(long ms)
    {
        if (activeSp != null) { rProgress = (int)ms; rStamp = DateTime.UtcNow; seekDebounce.Stop(); seekDebounce.Start(); }
        else if (engine != null) engine.TimeMs = ms;
        lastDiscord = DateTime.MinValue;
    }

    void SeekBy(long deltaMs)
    {
        var (pos, len) = CurrentTimes();
        SeekTo(Math.Clamp(pos + deltaMs, 0, Math.Max(0, len - 500)));
    }

    // ---- volume / mute ----
    bool muted;

    void Volume_ValueChanged(object s, RoutedPropertyChangedEventArgs<double> e)
    {
        if (engine != null) engine.Volume = e.NewValue;
        if (activeSp != null) { volDebounce.Stop(); volDebounce.Start(); }
        if (muted && e.NewValue > 0) { muted = false; }
        MuteBtn.Content = e.NewValue <= 0.001 ? "" : e.NewValue < .35 ? "" : e.NewValue < .7 ? "" : "";
    }

    void Mute_Click(object s, RoutedEventArgs e) => ToggleMute();

    void ToggleMute()
    {
        if (!muted) { volumeBeforeMute = Math.Max(Volume.Value, .05); muted = true; Volume.Value = 0; }
        else { muted = false; Volume.Value = volumeBeforeMute; }
        Motion.Pulse(MuteBtn, 1.15);
    }

    void NudgeVolume(double d) { muted = false; Volume.Value = Math.Clamp(Volume.Value + d, 0, 1); }

    // ---- shuffle / repeat ----
    void Shuffle_Click(object s, RoutedEventArgs e) => ToggleShuffle();

    void ToggleShuffle()
    {
        queue.SetShuffle(!queue.Shuffle);
        UpdateModeButtons();
        Motion.Pulse(ShuffleBtn, 1.2);
        Toast(queue.Shuffle ? "Shuffle on" : "Shuffle off", "");
    }

    void Repeat_Click(object s, RoutedEventArgs e) => CycleRepeat();

    void CycleRepeat()
    {
        queue.Repeat = queue.Repeat switch { RepeatMode.Off => RepeatMode.All, RepeatMode.All => RepeatMode.One, _ => RepeatMode.Off };
        UpdateModeButtons();
        Motion.Pulse(RepeatBtn, 1.2);
        Toast(queue.Repeat switch { RepeatMode.All => "Repeat all", RepeatMode.One => "Repeat this song", _ => "Repeat off" }, "");
        SaveSession();
    }

    void UpdateModeButtons()
    {
        var accent = (Brush)FindResource("AccentBrush");
        var text = (Brush)FindResource("TextBrush");
        ShuffleBtn.Foreground = queue.Shuffle ? accent : text;
        Motion.To(ShuffleBtn, OpacityProperty, queue.Shuffle ? 1 : .55, Motion.Fast);
        bool rep = queue.Repeat != RepeatMode.Off;
        RepeatBtn.Foreground = rep ? accent : text;
        RepeatBtn.Content = queue.Repeat == RepeatMode.One ? "" : "";
        RepeatBtn.ToolTip = queue.Repeat switch { RepeatMode.All => "Repeat: all (R)", RepeatMode.One => "Repeat: this song (R)", _ => "Repeat: off (R)" };
        RepeatOneBadge.Visibility = queue.Repeat == RepeatMode.One ? Visibility.Visible : Visibility.Collapsed;
        Motion.To(RepeatBtn, OpacityProperty, rep ? 1 : .55, Motion.Fast);
    }

    // ---- favorites ----
    void Like_Click(object s, RoutedEventArgs e) { if (nowPlaying != null) ToggleFavorite(nowPlaying, LikeBtn); }

    /// <summary>Favorites live in LiquidCloud; SoundCloud songs are also liked on SoundCloud when you're signed in.</summary>
    public void ToggleFavorite(Track t, UIElement? animate = null)
    {
        bool fav = Library.ToggleFavorite(t);
        if (animate != null) Motion.Pulse(animate, 1.35);
        UpdateLikeButton();
        string where = "in LiquidCloud";
        if (t.Source == "SoundCloud" && ScApi.Account.SignedIn && cfg.LikeOnSoundCloud)
        {
            where = "in LiquidCloud and on SoundCloud";
            _ = Task.Run(async () =>
            {
                try { await ScApi.SetLikeAsync(t, fav); }
                catch (Exception ex) { Dispatcher.Invoke(() => Toast("Saved in LiquidCloud only. SoundCloud: " + ex.Message, "")); }
            });
        }
        Toast(fav ? $"Added to favorites {where}" : $"Removed from favorites {where}", fav ? "" : "");
    }

    void UpdateLikeButton()
    {
        bool fav = nowPlaying != null && Library.IsFavorite(nowPlaying);
        LikeBtn.Content = fav ? "" : "";
        LikeBtn.Foreground = fav ? (Brush)FindResource("AccentBrush") : (Brush)FindResource("TextBrush");
        LikeBtn.ToolTip = fav ? "Remove from favorites (L)" : "Add to favorites (L)";
    }

    void AddCurrentToPlaylist_Click(object s, RoutedEventArgs e)
    {
        if (nowPlaying == null) return;
        var menu = new ContextMenu();
        FillAddToPlaylist(menu.Items, new[] { nowPlaying });
        OpenMenu(menu, (FrameworkElement)s);
    }

    // ---- session: remember the queue and where you were ----
    void SaveSession()
    {
        var (pos, _) = CurrentTimes();
        Library.SaveSession(queue.Snapshot(nowPlaying != null ? pos : Library.Data.Session.PositionMs));
    }

    void RestoreSession()
    {
        var s = Library.Data.Session;
        if (s.Queue.Count == 0) { UpdateModeButtons(); return; }
        queue.Restore(s);
        UpdateModeButtons();
        if (queue.Current is { } t)
        {
            ShowTrackInfo(t);
            if (s.PositionMs > 0)
            {
                Cur.Text = Fmt(s.PositionMs);
                if (t.DurationMs > 0) { Seek.Maximum = t.DurationMs / 1000.0; updating = true; Seek.Value = s.PositionMs / 1000.0; updating = false; Dur.Text = t.DurationText; }
            }
            Status.Text = "Press play to continue where you left off";
        }
    }

    void SavePartialPosition()
    {
        if (nowPlaying == null) return;
        var (pos, len) = CurrentTimes();
        Library.SavePartial(nowPlaying, pos, len);
    }

    // ---- import (links, files, folders) ----
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
            Motion.PopIn(ImportCard);
            ImportBox.Focus();
        }
        else Motion.PopOut(ImportCard, () => { if (!importOpen) ImportOverlay.Visibility = Visibility.Collapsed; });
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
        if (files.Count == 0) { Toast("No audio files found", ""); return; }
        var tracks = files.Select(LocalFiles.ToTrack).ToList();
        ShowTrackListPage("From this PC", $"{tracks.Count} files", tracks, "");
        PlayContext(tracks, 0, "Files from this PC");
    }

    void ImportFiles_Click(object s, RoutedEventArgs e)
    {
        var dlg = new Microsoft.Win32.OpenFileDialog { Multiselect = true, Filter = "Audio files|*.mp3;*.m4a;*.aac;*.wav;*.flac;*.ogg;*.opus;*.wma|All files|*.*" };
        if (dlg.ShowDialog() != true) return;
        ToggleImport(false);
        AddLocal(dlg.FileNames);
    }

    void ImportFolder_Click(object s, RoutedEventArgs e)
    {
        var dlg = new Microsoft.Win32.OpenFolderDialog { Multiselect = true };
        if (dlg.ShowDialog() != true) return;
        ToggleImport(false);
        AddLocal(dlg.FolderNames);
    }

    async void ImportGo_Click(object s, RoutedEventArgs e)
    {
        var link = ImportBox.Text.Trim();
        if (link.Length == 0) return;
        ImportGo.IsEnabled = false;
        ImportStatus.Text = "Importing…";
        try
        {
            var tracks = await ImportLinkAsync(link);
            if (tracks.Count == 0) ImportStatus.Text = "Nothing found at that link.";
            else
            {
                ImportBox.Text = "";
                ToggleImport(false);
                ShowTrackListPage("Imported", $"{tracks.Count} songs", tracks, "");
            }
        }
        catch (Exception ex) { ImportStatus.Text = ex.Message; }
        ImportGo.IsEnabled = true;
    }

    static bool IsLink(string s) => Regex.IsMatch(s, @"^https?://\S+$", RegexOptions.IgnoreCase);

    async Task<List<Track>> ImportLinkAsync(string link)
    {
        var sp = Regex.Match(link, "playlist[/:]([A-Za-z0-9]+)");
        if (link.Contains("spotify", StringComparison.OrdinalIgnoreCase))
        {
            if (!sp.Success) throw new Exception("For Spotify, paste a playlist link.");
            if (!spotify!.Enabled) throw new Exception("Spotify needs a Client ID first (see Settings).");
            return await spotify.PlaylistAsync(sp.Groups[1].Value);
        }
        if (Regex.IsMatch(link, "youtube\\.com|youtu\\.be|soundcloud\\.com", RegexOptions.IgnoreCase))
            return await Importer.FromLinkAsync(link);
        throw new Exception("Paste a link from SoundCloud, YouTube, YouTube Music or Spotify.");
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
            BuildSettings();
            Overlay.Visibility = Visibility.Visible;
            Motion.To(Dim, OpacityProperty, 1, Motion.Base);
            Motion.To(Sheet, OpacityProperty, 1, Motion.Base);
            Motion.To(SheetScale, ScaleTransform.ScaleXProperty, 1, Motion.Slow, Motion.Spring);
            Motion.To(SheetScale, ScaleTransform.ScaleYProperty, 1, Motion.Slow, Motion.Spring);
            await RefreshAccountsAsync();
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
        SoundCloudDetail.Text = ScApi.Account.SignedIn
            ? $"SoundCloud: signed in as {ScApi.Account.UserName ?? "you"}. Your likes, feed and playlists are in Library."
            : "Search works out of the box. Sign in to SoundCloud (bottom of the sidebar) for your likes, feed and playlists.";

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
