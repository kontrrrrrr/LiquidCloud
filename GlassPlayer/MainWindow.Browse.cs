using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using System.Windows;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;

namespace GlassPlayer;

/// <summary>
/// The real soundcloud.com inside Glass. You sign in on SoundCloud's own page (Glass never sees your password;
/// the login cookie stays in %LocalAppData%\LiquidCloud\soundcloud-web). Press play on any song and it plays in Glass.
/// </summary>
public partial class MainWindow
{
    WebView2? sc;
    bool browsing;
    string lastCaught = "";
    DateTime lastCaughtAt;

    static readonly string ScData = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "LiquidCloud", "soundcloud-web");
    const string ScHome = "https://soundcloud.com/discover";

    double BrowseWidth() => Math.Max(350, ContentGrid.ActualWidth - 16 - 380);

    async void Browse_Click(object s, RoutedEventArgs e) => await SetBrowsingAsync(!browsing);

    async Task SetBrowsingAsync(bool on)
    {
        if (on == browsing) return;
        browsing = on;
        BrowseBtn.Content = on ? "Back to list" : "Browse SoundCloud";
        if (expanded) SetExpanded(false);

        double target = on ? BrowseWidth() : 350;
        Anim(LeftPanel, FrameworkElement.WidthProperty, target, 480, () =>
        {
            LeftPanel.BeginAnimation(FrameworkElement.WidthProperty, null);
            LeftPanel.Width = browsing ? BrowseWidth() : 350;
        });

        BrowseView.Visibility = on ? Visibility.Visible : Visibility.Collapsed;
        TopArea.Visibility = List.Visibility = on ? Visibility.Collapsed : Visibility.Visible;

        if (on)
        {
            try { await EnsureScAsync(); }
            catch (Exception ex)
            {
                Status.Text = "Couldn't open SoundCloud: " + ex.Message + " (Microsoft Edge WebView2 Runtime is needed.)";
            }
        }
        else if (!cfg.KeepScLoaded) DisposeSc();
        else if (sc != null) sc.Visibility = Visibility.Hidden;
        ApplyActivity();
    }

    async Task EnsureScAsync()
    {
        if (sc != null) { sc.Visibility = Visibility.Visible; return; }
        var view = new WebView2();
        sc = view;
        WebHost.Child = view;
        Directory.CreateDirectory(ScData);
        var env = await CoreWebView2Environment.CreateAsync(null, ScData);
        await view.EnsureCoreWebView2Async(env);
        if (sc != view) return; // closed while starting
        var core = view.CoreWebView2;
        core.Settings.IsStatusBarEnabled = false;
        core.Settings.AreDevToolsEnabled = false;
        core.Settings.IsGeneralAutofillEnabled = false;
        core.Settings.IsPasswordAutosaveEnabled = false;

        // ordinary SoundCloud links stay inside the panel; sign-in popups (Google, Apple, Facebook) open as their own window
        core.NewWindowRequested += (_, e) =>
        {
            if (Uri.TryCreate(e.Uri, UriKind.Absolute, out var u) && u.Host.EndsWith("soundcloud.com") && !u.Host.StartsWith("secure."))
            {
                e.Handled = true;
                core.Navigate(e.Uri);
            }
        };
        core.SourceChanged += (_, _) => ScUrl.Text = Pretty(core.Source);
        core.NavigationCompleted += async (_, _) => await TryGrabTokenAsync();
        core.IsDocumentPlayingAudioChanged += async (_, _) =>
        {
            if (cfg.CatchScPlay && core.IsDocumentPlayingAudio) await CatchScPlayAsync();
        };
        core.Navigate(ScHome);
    }

    static string Pretty(string url) => url.Replace("https://", "").Split('?')[0];

    void DisposeSc()
    {
        if (sc == null) return;
        var v = sc;
        sc = null;
        WebHost.Child = null;
        try { v.Dispose(); } catch { }
    }

    /// <summary>WebView2 is drawn by a separate window, so it must be hidden while our overlays are open.</summary>
    void WebAirspace()
    {
        if (sc == null) return;
        sc.Visibility = browsing && !settingsOpen && !importOpen ? Visibility.Visible : Visibility.Hidden;
    }

    // Pause SoundCloud's own player and read which song was started.
    const string PauseSc = "(function(){var b=document.querySelector('.playControl.playing');if(b)b.click();document.querySelectorAll('audio,video').forEach(function(m){try{m.pause()}catch(e){}});return true;})()";
    const string CurrentSc = "(function(){var a=document.querySelector('.playbackSoundBadge__titleLink');return a?a.href:'';})()";

    async Task CatchScPlayAsync()
    {
        var core = sc?.CoreWebView2;
        if (core == null) return;
        string url;
        try
        {
            await core.ExecuteScriptAsync(PauseSc);
            await Task.Delay(350); // let SoundCloud update its "now playing" badge
            url = JsonSerializer.Deserialize<string>(await core.ExecuteScriptAsync(CurrentSc)) ?? "";
        }
        catch { return; }
        if (url.Length == 0) return;
        url = url.Split('?')[0];
        if (url == lastCaught && (DateTime.UtcNow - lastCaughtAt).TotalSeconds < 5) return;
        lastCaught = url;
        lastCaughtAt = DateTime.UtcNow;
        await PlayLinkAsync(url);
    }

    async Task PlayLinkAsync(string url)
    {
        Status.Text = "Loading from SoundCloud…";
        try
        {
            var tracks = await Importer.FromLinkAsync(url);
            if (tracks.Count == 0) { Status.Text = "Nothing playable on this page. Open a song, playlist or profile."; return; }
            if (tracks.Count == 1)
            {
                var cur = (List.ItemsSource as IEnumerable<Track>)?.ToList() ?? new List<Track>();
                cur.RemoveAll(x => x.Id == tracks[0].Id);
                cur.Insert(0, tracks[0]);
                List.ItemsSource = cur;
            }
            else List.ItemsSource = tracks;
            List.SelectedIndex = 0; // starts playback
            Status.Text = tracks.Count > 1 ? $"Loaded {tracks.Count} songs from this page" : "";
        }
        catch (Exception ex) { Status.Text = ex.Message; }
    }

    async void ScPlayPage_Click(object s, RoutedEventArgs e)
    {
        var src = sc?.CoreWebView2?.Source;
        if (src == null || !src.Contains("soundcloud.com")) return;
        await PlayLinkAsync(src.Split('?')[0]);
    }

    void ScBack_Click(object s, RoutedEventArgs e) { if (sc?.CoreWebView2?.CanGoBack == true) sc.CoreWebView2.GoBack(); }
    void ScForward_Click(object s, RoutedEventArgs e) { if (sc?.CoreWebView2?.CanGoForward == true) sc.CoreWebView2.GoForward(); }
    void ScReload_Click(object s, RoutedEventArgs e) => sc?.CoreWebView2?.Reload();
    void ScHome_Click(object s, RoutedEventArgs e) => sc?.CoreWebView2?.Navigate(ScHome);

    void SignOutSc()
    {
        if (sc?.CoreWebView2 != null)
        {
            sc.CoreWebView2.CookieManager.DeleteAllCookies();
            sc.CoreWebView2.Navigate(ScHome);
        }
        else
        {
            try { if (Directory.Exists(ScData)) Directory.Delete(ScData, true); } catch { }
        }
        scAcc?.SignOut();
        UpdateScChips();
        Status.Text = "Signed out of SoundCloud.";
    }
}
