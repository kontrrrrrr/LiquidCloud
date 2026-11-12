using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace LiquidCloud;

/// <summary>
/// "--smoke": an automated walk through the real window (used by CI, with --offline so nothing touches the network).
/// It plays generated audio files, uses the transport, opens pages, tabs, themes, settings, the language switch,
/// the full-window and mini players, saves a screenshot of each step and writes results.txt.
/// Exit code: 0 = every check passed, 1 = a check failed, 2 = the run hung.
/// </summary>
public partial class MainWindow
{
    readonly List<string> smokeLines = new();
    int smokeFailures, shotNo;

    void Check(string what, bool ok, string? detail = null)
    {
        if (!ok) smokeFailures++;
        var line = $"{(ok ? "PASS" : "FAIL")}  {what}{(detail != null ? "  (" + detail + ")" : "")}";
        smokeLines.Add(line);
        Log.Info("Smoke", line);
    }

    async Task Settle(int ms = 700)
    {
        await Task.Delay(ms);
        await Dispatcher.InvokeAsync(() => { }, System.Windows.Threading.DispatcherPriority.ApplicationIdle);
    }

    void Shot(string name)
    {
        try
        {
            UpdateLayout();
            var root = (FrameworkElement)Content;
            int w = (int)Math.Max(1, root.ActualWidth), h = (int)Math.Max(1, root.ActualHeight);
            var bmp = new RenderTargetBitmap(w, h, 96, 96, PixelFormats.Pbgra32);
            bmp.Render(root);
            var enc = new PngBitmapEncoder();
            enc.Frames.Add(BitmapFrame.Create(bmp));
            Directory.CreateDirectory(AppEnv.SmokeOut);
            using var fs = File.Create(Path.Combine(AppEnv.SmokeOut, $"{++shotNo:00}-{name}.png"));
            enc.Save(fs);
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or ArgumentException or OutOfMemoryException)
        {
            Check("screenshot " + name, false, ex.Message);
        }
    }

    static IEnumerable<DependencyObject> Descendants(DependencyObject root)
    {
        int n = VisualTreeHelper.GetChildrenCount(root);
        for (int i = 0; i < n; i++)
        {
            var c = VisualTreeHelper.GetChild(root, i);
            yield return c;
            foreach (var d in Descendants(c)) yield return d;
        }
    }

    /// <summary>A short sine-wave WAV, so playback can be tested without shipping audio files.</summary>
    static string MakeWav(string dir, string name, double hz, double seconds)
    {
        const int rate = 22050;
        int n = (int)(rate * seconds);
        var path = Path.Combine(dir, name);
        using var bw = new BinaryWriter(File.Create(path));
        bw.Write(Encoding.ASCII.GetBytes("RIFF")); bw.Write(36 + n * 2); bw.Write(Encoding.ASCII.GetBytes("WAVEfmt "));
        bw.Write(16); bw.Write((short)1); bw.Write((short)1); bw.Write(rate); bw.Write(rate * 2); bw.Write((short)2); bw.Write((short)16);
        bw.Write(Encoding.ASCII.GetBytes("data")); bw.Write(n * 2);
        for (int i = 0; i < n; i++) bw.Write((short)(Math.Sin(2 * Math.PI * hz * i / rate) * 6000));
        return path;
    }

    async Task RunSmokeAsync()
    {
        // never hang CI: give up after 4 minutes
        Bg.Run(Task.Delay(TimeSpan.FromMinutes(4)).ContinueWith(_ => { Log.Error("Smoke", "Timed out"); Environment.Exit(2); }), "Smoke", "Watchdog");
        try
        {
            await Settle(1500);
            Check("window shown", IsVisible && ActualWidth > 500, $"{ActualWidth}x{ActualHeight}");
            Check("offline mode", AppEnv.Offline);
            Shot("home");

            // ---- pages offline: each must show its 'no connection' state instead of crashing ----
            Navigate("discover"); await Settle(1200); Shot("discover-offline");
            Check("discover page", currentPage == "discover");
            SearchBox.Text = "test"; RunSearch("test"); await Settle(1200); Shot("search-offline");
            Navigate("library"); await Settle(); Shot("library");
            Check("library page", currentPage == "library");

            // ---- back / forward with the page cache ----
            Navigate("home"); await Settle(400);
            Navigate("library"); await Settle(400);
            var libPage = PageHost.Children.OfType<FrameworkElement>().LastOrDefault();
            Navigate("themes"); await Settle(400);
            GoBack(); await Settle(600);
            Check("back goes to the previous page", currentPage == "library", currentPage);
            Check("back reuses the cached page", PageHost.Children.OfType<FrameworkElement>().LastOrDefault() == libPage);
            GoForward(); await Settle(600);
            Check("forward", currentPage == "themes", currentPage);

            // ---- local playback ----
            var dir = Path.Combine(AppEnv.SmokeOut, "audio");
            Directory.CreateDirectory(dir);
            var files = new[] { MakeWav(dir, "a-low.wav", 220, 8), MakeWav(dir, "b-mid.wav", 440, 8), MakeWav(dir, "c-high.wav", 880, 8) };
            AddLocal(files);
            await Settle(2500);
            Check("local file starts", player.Current?.Title != null && player.State == PlayState.Playing, $"{player.State} {player.Message}");
            Check("queue has 3 songs", player.Queue.Count == 3 && vm.QueueItems.Count == 3, $"{player.Queue.Count}/{vm.QueueItems.Count}");
            Check("now playing title bound", vm.Title == player.Current?.Title, vm.Title);
            Shot("playing");

            await Settle(1500);
            var (pos, len) = player.Times;
            Check("position moves", pos > 0, $"{pos}/{len} ms");

            var first = player.Current?.Id;
            await player.NextAsync(); await Settle(1200);
            Check("next", player.Current?.Id != first && player.Queue.Position == 1, player.Current?.Title);
            await player.PreviousAsync(); await Settle(1200);
            Check("previous", player.Current?.Id == first, player.Current?.Title);

            await player.TogglePlayAsync(); await Settle(300);
            Check("pause", player.State == PlayState.Paused && vm.PlayGlyph != null);
            await player.TogglePlayAsync(); await Settle(300);
            Check("resume", player.State == PlayState.Playing);

            ToggleShuffle(); await Settle(300);
            Check("shuffle on", player.Queue.Shuffle && vm.Shuffle && player.Current?.Id == first);
            ToggleShuffle();
            CycleRepeat(); Check("repeat all", vm.Repeat == RepeatMode.All);
            CycleRepeat(); Check("repeat one", vm.Repeat == RepeatMode.One && vm.RepeatOne);
            CycleRepeat(); Check("repeat off", vm.Repeat == RepeatMode.Off);

            vm.Volume = 0.3; Check("volume", Math.Abs(player.Volume - 0.3) < .01);
            ToggleMute(); Check("mute", player.Muted && vm.Muted);
            ToggleMute(); Check("unmute", !player.Muted);

            // ---- keyboard: Enter on a focused track row plays it ----
            var rowEl = Descendants(PageHost).OfType<FrameworkElement>()
                .FirstOrDefault(e => System.Windows.Automation.AutomationProperties.GetName(e).StartsWith("c-high"));
            Check("track rows have accessible names", rowEl != null);
            if (rowEl != null)
            {
                Check("track row takes keyboard focus", rowEl.Focus());
                rowEl.RaiseEvent(new System.Windows.Input.KeyEventArgs(System.Windows.Input.Keyboard.PrimaryDevice, PresentationSource.FromVisual(this)!, 0, System.Windows.Input.Key.Enter)
                    { RoutedEvent = System.Windows.Input.Keyboard.KeyDownEvent });
                await Settle(1500);
                Check("Enter plays the focused row", player.Current?.Title == "c-high", player.Current?.Title);
            }

            // ---- player tabs ----
            SetPlayerTab("lyrics"); await Settle(); Shot("tab-lyrics");
            SetPlayerTab("queue"); await Settle(); Shot("tab-queue");
            Check("queue tab lists songs", List.Items.Count == 3, List.Items.Count.ToString());
            SetPlayerTab("now"); await Settle();

            // ---- themes ----
            foreach (var id in new[] { "arctic", "ember", ThemeStore.DefaultId })
            {
                activeThemeId = id; ApplyLook(); await Settle(500);
                Shot("theme-" + id);
            }
            Check("themes applied", Application.Current.Resources["AccentBrush"] is SolidColorBrush);

            // ---- settings + language ----
            ToggleSettings(true); await Settle(900); Shot("settings");
            Check("settings open", Overlay.Visibility == Visibility.Visible);
            SetLanguage(Loc.English); await Settle(900); Shot("settings-english");
            Check("english strings", Loc.T("Settings") == "Settings" && (string)FindResource("T_Next") == "Next");
            SetLanguage(Loc.Russian); await Settle(900);
            Check("russian strings", Loc.T("Settings") == "Настройки" && (string)FindResource("T_Next") == "Следующий трек");
            ToggleSettings(false); await Settle(600);

            // ---- full-window and mini player ----
            SetFullPlayer(true); await Settle(900); Shot("full-player");
            Check("full player", fullPlayer && Sidebar.Visibility == Visibility.Collapsed);
            SetFullPlayer(false); await Settle(600);
            SetMini(true); await Settle(900); Shot("mini-player");
            Check("mini player", miniMode && MiniBar.Visibility == Visibility.Visible && ActualHeight < 200, $"{ActualWidth}x{ActualHeight}");
            SetMini(false); await Settle(900);
            Check("back from mini", !miniMode && Body.Visibility == Visibility.Visible && ActualWidth > 900, $"{ActualWidth}x{ActualHeight}");
            Shot("restored");

            // ---- session saved ----
            SaveSession();
            Check("session remembered", Library.Data.Session.Queue.Count == 3, Library.Data.Session.Queue.Count.ToString());
        }
        catch (Exception ex)
        {
            Check("self-check ran to the end", false, ex.GetType().Name + ": " + ex.Message);
            Log.Error("Smoke", "Self-check crashed", ex);
        }

        smokeLines.Add($"{smokeLines.Count(l => l.StartsWith("PASS"))} passed, {smokeFailures} failed");
        Directory.CreateDirectory(AppEnv.SmokeOut);
        File.WriteAllLines(Path.Combine(AppEnv.SmokeOut, "results.txt"), smokeLines);
        foreach (var l in smokeLines) Console.WriteLine(l);
        player.Stop();
        Environment.Exit(smokeFailures == 0 ? 0 : 1);
    }
}
