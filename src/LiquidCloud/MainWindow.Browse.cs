using System;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Threading;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;

namespace LiquidCloud;

/// <summary>
/// The real soundcloud.com as a page inside LiquidCloud. You sign in on SoundCloud's own page (LiquidCloud never sees
/// your password; the login cookie stays in %LocalAppData%\LiquidCloud\soundcloud-web). Press play on any song and it plays here.
/// </summary>
public partial class MainWindow
{
    WebView2? sc;
    bool browsing;
    string lastCaught = "";
    DateTime lastCaughtAt;
    readonly DispatcherTimer signInPoll = new() { Interval = TimeSpan.FromSeconds(2) };
    string? scPendingUrl; // where to go once the web view has started

    static readonly string ScData = Path.Combine(AppEnv.LocalDir, "soundcloud-web");
    const string ScHome = "https://soundcloud.com/discover";

    /// <summary>Shows or hides the SoundCloud web page in the center panel.</summary>
    async Task SetBrowsingAsync(bool on, string? url = null)
    {
        browsing = on;
        BrowseView.Visibility = on ? Visibility.Visible : Visibility.Collapsed;
        PageHost.Visibility = on ? Visibility.Collapsed : Visibility.Visible;
        if (on)
        {
            Motion.Enter(BrowseView, 0, 10);
            try
            {
                if (url != null)
                {
                    if (sc?.CoreWebView2 != null) sc.CoreWebView2.Navigate(url);
                    else scPendingUrl = url;
                }
                await EnsureScAsync();
            }
            catch (Exception ex)
            {
                Toast(Loc.T("Couldn't open SoundCloud: {0} (Microsoft Edge WebView2 Runtime is needed.)", ex.Message), "");
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

        // ordinary SoundCloud links stay inside the page; sign-in popups (Google, Apple, Facebook) open as their own window
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
        core.Navigate(scPendingUrl ?? ScHome);
        scPendingUrl = null;
    }

    static string Pretty(string url) => url.Replace("https://", "").Split('?')[0];

    void DisposeSc()
    {
        if (sc == null) return;
        var v = sc;
        sc = null;
        WebHost.Child = null;
        try { v.Dispose(); }
        catch (Exception ex) when (ex is InvalidOperationException or ObjectDisposedException) { Log.Info("Web", "WebView dispose: " + ex.Message); }
    }

    /// <summary>WebView2 is drawn by a separate window, so it must be hidden while our overlays are open.</summary>
    void WebAirspace()
    {
        if (sc == null) return;
        sc.Visibility = browsing && !settingsOpen && !modalOpen ? Visibility.Visible : Visibility.Hidden;
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
        catch (Exception ex) when (ex is InvalidOperationException or JsonException or COMException)
        {
            Log.Info("Web", "Couldn't read the song playing on the page: " + ex.Message);
            return;
        }
        if (url.Length == 0) return;
        url = url.Split('?')[0];
        if (url == lastCaught && (DateTime.UtcNow - lastCaughtAt).TotalSeconds < 5) return;
        lastCaught = url;
        lastCaughtAt = DateTime.UtcNow;
        await PlayLinkAsync(url);
    }

    async Task PlayLinkAsync(string url)
    {
        Status.Text = Loc.T("Loading from SoundCloud…");
        try
        {
            var tracks = await Importer.FromLinkAsync(url);
            if (tracks.Count == 0) { Toast("Nothing playable on this page. Open a song, playlist or profile.", ""); return; }
            if (tracks.Count == 1) PlayNow(tracks[0]);
            else PlayContext(tracks, 0, Pretty(url));
            Status.Text = "";
        }
        catch (Exception ex) { Status.Text = ex.Message; }
    }

    void ScPlayPage_Click(object s, RoutedEventArgs e)
    {
        var src = sc?.CoreWebView2?.Source;
        if (src == null || !src.Contains("soundcloud.com")) return;
        Bg.Run(PlayLinkAsync(src.Split('?')[0]), "Web", "Play page");
    }

    void ScBack_Click(object s, RoutedEventArgs e) { if (sc?.CoreWebView2?.CanGoBack == true) sc.CoreWebView2.GoBack(); }
    void ScForward_Click(object s, RoutedEventArgs e) { if (sc?.CoreWebView2?.CanGoForward == true) sc.CoreWebView2.GoForward(); }
    void ScReload_Click(object s, RoutedEventArgs e) => sc?.CoreWebView2?.Reload();
    void ScHome_Click(object s, RoutedEventArgs e) => sc?.CoreWebView2?.Navigate(ScHome);

    // ---- SoundCloud sign-in ----
    async Task StartScSignInAsync()
    {
        const string signIn = "https://soundcloud.com/signin";
        if (sc?.CoreWebView2 != null) sc.CoreWebView2.Navigate(signIn);
        else scPendingUrl = signIn;
        Navigate("web");
        await Task.CompletedTask;
        Toast("Sign in on the SoundCloud page. LiquidCloud picks it up automatically.", "");
        signInPoll.Tick -= SignInPollTick;
        signInPoll.Tick += SignInPollTick;
        signInPoll.Start();
    }

    async void SignInPollTick(object? s, EventArgs e)
    {
        if (!browsing || ScApi.Account.SignedIn) { signInPoll.Stop(); return; }
        await TryGrabTokenAsync();
    }

    /// <summary>Takes SoundCloud's login token from the page's cookies once you've signed in.</summary>
    async Task TryGrabTokenAsync()
    {
        var core = sc?.CoreWebView2;
        if (core == null) return;
        string? token;
        try
        {
            var cookies = await core.CookieManager.GetCookiesAsync("https://soundcloud.com");
            token = cookies.FirstOrDefault(c => c.Name == "oauth_token")?.Value;
        }
        catch (Exception ex) when (ex is InvalidOperationException or COMException)
        {
            Log.Info("Web", "Cookies not readable yet: " + ex.Message);
            return;
        }
        if (string.IsNullOrEmpty(token) || token == ScApi.Account.Token) return;

        bool wasSignedIn = ScApi.Account.SignedIn;
        ScApi.Account.Save(token);
        try { await ScApi.Account.LoadMeAsync(); }
        catch (Exception ex) when (ex is not OperationCanceledException) { Log.Warn("SoundCloud", "Signed in, but the profile didn't load", ex); }
        if (!wasSignedIn)
        {
            signInPoll.Stop();
            Toast(Loc.T("Signed in to SoundCloud as {0}", ScApi.Account.UserName), "");
            Navigate("library", "likes");
        }
    }

    void SignOutSc()
    {
        if (sc?.CoreWebView2 != null)
        {
            sc.CoreWebView2.CookieManager.DeleteAllCookies();
            sc.CoreWebView2.Navigate(ScHome);
        }
        else
        {
            try { if (Directory.Exists(ScData)) Directory.Delete(ScData, true); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { Log.Warn("Web", "Couldn't clear SoundCloud web data", ex); }
        }
        ScApi.Account.SignOut();
        Toast("Signed out of SoundCloud", "");
    }
}
