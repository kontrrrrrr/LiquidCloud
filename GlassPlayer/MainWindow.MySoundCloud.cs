using System;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;

namespace GlassPlayer;

/// <summary>Likes / Feed / Playlists tabs for your own SoundCloud account.</summary>
public partial class MainWindow
{
    ScAccount? scAcc;
    readonly DispatcherTimer signInPoll = new() { Interval = TimeSpan.FromSeconds(2) };

    void InitMySoundCloud()
    {
        scAcc = new ScAccount((SoundCloud)providers["SoundCloud"]);
        signInPoll.Tick += async (_, _) =>
        {
            if (!browsing || scAcc!.SignedIn) { signInPoll.Stop(); return; }
            await TryGrabTokenAsync();
        };
        UpdateScChips();
        if (scAcc.SignedIn) Loaded += async (_, _) => { try { await scAcc.LoadMeAsync(); } catch { } UpdateScChips(); };
    }

    void UpdateScChips()
    {
        if (scAcc == null) return;
        ScChips.Visibility = provider is SoundCloud ? Visibility.Visible : Visibility.Collapsed;
        ScTabs.Visibility = scAcc.SignedIn ? Visibility.Visible : Visibility.Collapsed;
        ScSignIn.Visibility = scAcc.SignedIn ? Visibility.Collapsed : Visibility.Visible;
    }

    async void ScTab_Click(object s, RoutedEventArgs e)
    {
        if (s is RadioButton { Tag: string kind }) await ShowMyScAsync(kind);
    }

    async Task ShowMyScAsync(string kind)
    {
        if (scAcc == null) return;
        if (browsing) await SetBrowsingAsync(false);
        Status.Text = $"Loading your {kind.ToLowerInvariant()}…";
        try
        {
            var list = kind switch
            {
                "Feed" => await scAcc.FeedAsync(),
                "Playlists" => await scAcc.PlaylistsAsync(),
                _ => await scAcc.LikesAsync()
            };
            ShowTracks(list, list.Count == 0 ? "Nothing here yet." : kind == "Playlists" ? $"{list.Count} playlists. Pick one to open it." : $"{list.Count} songs");
        }
        catch (Exception ex)
        {
            Status.Text = ex.Message;
            UpdateScChips();
        }
    }

    async Task OpenScPlaylistAsync(Track p)
    {
        if (scAcc == null) return;
        Status.Text = $"Opening {p.Title}…";
        try
        {
            var tracks = await scAcc.PlaylistTracksAsync(p.Id);
            ShowTracks(tracks, $"{p.Title} · {tracks.Count} songs");
        }
        catch (Exception ex) { Status.Text = ex.Message; UpdateScChips(); }
    }

    async void ScSignIn_Click(object s, RoutedEventArgs e)
    {
        await SetBrowsingAsync(true);
        sc?.CoreWebView2?.Navigate("https://soundcloud.com/signin");
        Status.Text = "Sign in on the SoundCloud page. LiquidCloud picks it up automatically.";
        signInPoll.Start();
    }

    /// <summary>Takes SoundCloud's login token from the panel's cookies once you've signed in.</summary>
    async Task TryGrabTokenAsync()
    {
        var core = sc?.CoreWebView2;
        if (core == null || scAcc == null) return;
        string? token;
        try
        {
            var cookies = await core.CookieManager.GetCookiesAsync("https://soundcloud.com");
            token = cookies.FirstOrDefault(c => c.Name == "oauth_token")?.Value;
        }
        catch { return; }
        if (string.IsNullOrEmpty(token) || token == scAcc.Token) return;

        bool wasSignedIn = scAcc.SignedIn;
        scAcc.Save(token);
        try { await scAcc.LoadMeAsync(); } catch { }
        UpdateScChips();
        if (!wasSignedIn)
        {
            signInPoll.Stop();
            Status.Text = $"Signed in to SoundCloud as {scAcc.UserName}";
            if (provider is not SoundCloud) SoundCloudBtn.IsChecked = true;
            await ShowMyScAsync("Likes");
        }
    }
}
