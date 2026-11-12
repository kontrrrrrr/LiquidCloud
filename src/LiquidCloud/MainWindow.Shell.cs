using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;

namespace LiquidCloud;

/// <summary>App shell: navigation between pages, sidebar, search box, toasts, dialogs, menus, keyboard, mini / full player and the intro.</summary>
public partial class MainWindow
{
    string currentPage = "";
    object? currentArg;
    readonly Stack<(string Page, object? Arg)> backStack = new();
    bool modalOpen, fullPlayer, miniMode;
    List<(ScSet Set, bool Mine)>? scSetsCache;

    void InitShell()
    {
        Library.Changed += what =>
        {
            if (what == "playlists") RenderSidebar();
            // cached pages showing library data are rebuilt next time they're opened
            if (what is "favorites" or "playlists" or "artists" or "searches") InvalidatePages("library", "home", "discover");
        };
        ScApi.Account.Changed += () => Dispatcher.Invoke(() =>
        {
            if (ScApi.Account.UserName != null && cfg.LastUserName != ScApi.Account.UserName) { cfg.LastUserName = ScApi.Account.UserName; SaveSoon(); }
            scSetsCache = null;
            RenderAccount();
            RenderSidebar();
        });
        RenderAccount();
        RenderSidebar();
        if (ScApi.Account.SignedIn && !AppEnv.Offline) Bg.Run(ScApi.Account.LoadMeAsync, "SoundCloud", "Load account");

        PreviewKeyDown += OnKey;
        MouseUp += (_, e) => { if (e.ChangedButton == MouseButton.XButton1) GoBack(); else if (e.ChangedButton == MouseButton.XButton2) GoForward(); };
        searchTimer.Tick += async (_, _) => { searchTimer.Stop(); await UpdateSuggestionsAsync(); };
        Navigate("home", null, false);
    }

    // =====================================================================
    // Navigation
    // =====================================================================
    void Nav_Click(object s, RoutedEventArgs e)
    {
        if (s is RadioButton { CommandParameter: string page }) Navigate(page);
    }

    void Back_Click(object s, RoutedEventArgs e) => GoBack();
    void Forward_Click(object s, RoutedEventArgs e) => GoForward();

    readonly Stack<(string Page, object? Arg)> forwardStack = new();

    void GoBack()
    {
        if (backStack.Count == 0) return;
        forwardStack.Push((currentPage, currentArg));
        var (p, a) = backStack.Pop();
        Show(p, a);
    }

    void GoForward()
    {
        if (forwardStack.Count == 0) return;
        backStack.Push((currentPage, currentArg));
        var (p, a) = forwardStack.Pop();
        Show(p, a);
    }

    // ---- page cache: going back shows the same page instance (data, covers and scroll position kept) ----
    sealed class PageEntry
    {
        public required string Key;
        public required FrameworkElement Element;
        public required List<(Track Track, TextBlock Title)> Rows;
        public required CancellationTokenSource Cts;
        public DateTime Built = DateTime.UtcNow;
    }

    readonly List<PageEntry> pageCache = new();
    readonly Dictionary<FrameworkElement, CancellationTokenSource> pageTokens = new();
    CancellationTokenSource? pageCts;
    static readonly HashSet<string> CacheablePages = new() { "home", "discover", "genre", "library", "set", "artist" };
    static readonly TimeSpan PageLifetime = TimeSpan.FromMinutes(5);
    const int MaxCachedPages = 8;

    static string PageKey(string page, object? arg) => page + "|" + arg switch
    {
        null => "",
        string s => s,
        ScSet set => "set:" + set.Id,
        ScUser u => "user:" + u.Id,
        Track t => "artist-of:" + t.Key,
        ValueTuple<string, string> g => g.Item2,
        _ => arg.GetHashCode().ToString()
    };

    /// <summary>Drops cached pages (all, or those whose key starts with a prefix), e.g. after the library changed.</summary>
    void InvalidatePages(params string[] pages)
    {
        foreach (var e in pageCache.Where(e => pages.Length == 0 || pages.Any(p => e.Key.StartsWith(p + "|"))).ToList())
        {
            if (PageHost.Children.Contains(e.Element)) continue; // the visible page stays until you leave it
            e.Cts.Cancel();
            pageCache.Remove(e);
        }
    }

    /// <summary>Opens a page (new history entry). Navigating to the page you're on rebuilds it with fresh data.</summary>
    public void Navigate(string page, object? arg = null, bool push = true)
    {
        bool same = page == currentPage && PageKey(page, arg) == PageKey(currentPage, currentArg);
        if (push && currentPage.Length > 0 && !same)
        {
            backStack.Push((currentPage, currentArg));
            forwardStack.Clear();
        }
        if (backStack.Count > 40) { var keep = backStack.Take(30).Reverse().ToList(); backStack.Clear(); foreach (var k in keep) backStack.Push(k); }
        if (same || !push) pageCache.RemoveAll(e => e.Key == PageKey(page, arg) && !PageHost.Children.Contains(e.Element));
        Show(page, arg, forceFresh: same);
    }

    void Show(string page, object? arg, bool forceFresh = false)
    {
        currentPage = page;
        currentArg = arg;
        BackBtn.IsEnabled = backStack.Count > 0;
        ForwardBtn.IsEnabled = forwardStack.Count > 0;
        if (fullPlayer) SetFullPlayer(false);

        var nav = page switch
        {
            "home" => NavHome, "discover" or "genre" or "artist" or "set" => NavDiscover,
            "library" or "playlist" or "tracks" => NavLibrary, "web" => NavWeb, "themes" => NavThemes, _ => null
        };
        if (nav != null) nav.IsChecked = true;
        RenderSidebarSelection();

        if (page == "web") { Bg.Run(SetBrowsingAsync(true), "Web", "Open SoundCloud web"); return; }
        if (browsing) Bg.Run(SetBrowsingAsync(false), "Web", "Close SoundCloud web");
        if (page != "themes" && (previewTheme != null || draft != null)) { previewTheme = null; draft = null; ApplyLook(); }

        var key = PageKey(page, arg);
        var entry = forceFresh ? null : pageCache.FirstOrDefault(e => e.Key == key && DateTime.UtcNow - e.Built < PageLifetime);
        if (entry != null)
        {
            pageCache.Remove(entry);
            pageCache.Add(entry); // most recently used last
            liveRows = entry.Rows;
            pageCts = entry.Cts;
            HighlightPlaying();
        }
        else
        {
            liveRows = new();
            pageCts = new CancellationTokenSource();
            FrameworkElement built = page switch
            {
                "home" => BuildHome(),
                "discover" => BuildDiscover(arg as string),
                "genre" => BuildGenre(((string Name, string Tag))arg!),
                "library" => BuildLibrary(arg as string ?? "favorites"),
                "playlist" => BuildLocalPlaylist((string)arg!),
                "set" => BuildScSet((ScSet)arg!),
                "artist" => BuildArtist(arg!),
                "tracks" => BuildTrackListPage((TrackListArgs)arg!),
                "themes" => BuildThemes(),
                _ => BuildHome()
            };
            built.Tag = key;
            pageTokens[built] = pageCts;
            entry = new PageEntry { Key = key, Element = built, Rows = liveRows, Cts = pageCts };
            if (CacheablePages.Contains(page))
            {
                pageCache.RemoveAll(e => e.Key == key);
                pageCache.Add(entry);
                // keep at most a few pages; anything older is cancelled (its network requests stop) and released
                while (pageCache.Count > MaxCachedPages) { pageCache[0].Cts.Cancel(); pageCache.RemoveAt(0); }
            }
        }

        var el = entry.Element;
        foreach (var o in PageHost.Children.OfType<FrameworkElement>().Where(o => o != el).ToList())
        {
            o.IsHitTestVisible = false;
            Motion.Leave(o, () =>
            {
                PageHost.Children.Remove(o);
                // a page that isn't kept in the cache stops its network work once it's gone
                if (!pageCache.Any(e => e.Element == o) && pageTokens.Remove(o, out var oldCts)) oldCts.Cancel();
            }, -6, Motion.Fast);
        }
        el.IsHitTestVisible = true;
        if (!PageHost.Children.Contains(el)) PageHost.Children.Add(el);
        Motion.Enter(el, 40, 18, Motion.Page);
    }

    record TrackListArgs(string Title, string Subtitle, List<Track> Tracks, string Glyph);

    void ShowTrackListPage(string title, string subtitle, List<Track> tracks, string glyph) =>
        Navigate("tracks", new TrackListArgs(Loc.T(title), Loc.T(subtitle), tracks, glyph));

    // =====================================================================
    // Sidebar
    // =====================================================================
    readonly List<(RadioButton Item, string Kind, string Id)> sidebarItems = new();

    void RenderSidebar()
    {
        SidebarPlaylists.Children.Clear();
        sidebarItems.Clear();
        foreach (var p in Library.Data.Playlists)
        {
            var id = p.Id;
            var item = SidebarItem(p.Name, Loc.Songs(p.Tracks.Count) + " · LiquidCloud", "", () => Navigate("playlist", id));
            item.ContextMenu = PlaylistMenu(p);
            // drop songs onto a playlist to add them
            item.AllowDrop = true;
            item.DragOver += (_, e) => { if (e.Data.GetDataPresent(TrackFormat)) { e.Effects = DragDropEffects.Copy; e.Handled = true; item.IsChecked = true; } };
            item.DragLeave += (_, _) => RenderSidebarSelection();
            item.Drop += (_, e) =>
            {
                if (e.Data.GetData(TrackFormat) is Track t) AddToLocalPlaylist(p, new[] { t }, item);
                RenderSidebarSelection();
            };
            sidebarItems.Add((item, "playlist", id));
            SidebarPlaylists.Children.Add(item);
        }
        if (Library.Data.Playlists.Count == 0)
        {
            var hint = Txt(Loc.T("Create a playlist with +, or drag songs here later."), 12, null, "TextFaintBrush", true);
            hint.Margin = new Thickness(12, 4, 12, 8);
            SidebarPlaylists.Children.Add(hint);
        }

        if (ScApi.Account.SignedIn)
        {
            var head = Txt(Loc.T("ON SOUNDCLOUD"), 11, FontWeights.SemiBold, "TextFaintBrush");
            head.Margin = new Thickness(12, 16, 0, 6);
            SidebarPlaylists.Children.Add(head);
            foreach (var (glyph, name, tab) in new[] { ("", Loc.T("Likes"), "likes"), ("", Loc.T("Feed"), "feed"), ("", Loc.T("SoundCloud playlists"), "scsets") })
            {
                var t = tab;
                var item = SidebarItem(name, "", glyph, () => Navigate("library", t));
                sidebarItems.Add((item, "library", tab));
                SidebarPlaylists.Children.Add(item);
            }
        }
        RenderSidebarSelection();
    }

    RadioButton SidebarItem(string name, string tip, string glyph, Action open)
    {
        var r = new RadioButton { Style = S("Nav"), GroupName = "side", Tag = glyph, Content = name, ToolTip = tip.Length > 0 ? tip : null };
        r.Click += (_, _) => open();
        return r;
    }

    void RenderSidebarSelection()
    {
        foreach (var (item, kind, id) in sidebarItems)
            item.IsChecked = (kind == "playlist" && currentPage == "playlist" && Equals(currentArg, id))
                          || (kind == "library" && currentPage == "library" && Equals(currentArg, id));
    }

    void RenderAccount()
    {
        var a = ScApi.Account;
        if (a.SignedIn)
        {
            AccountName.Text = a.UserName ?? "SoundCloud";
            AccountHint.Text = Loc.T("Signed in to SoundCloud");
            LoadBrush(AccountAvatar, a.Avatar, 80);
            AccountInitial.Visibility = a.Avatar == null ? Visibility.Visible : Visibility.Collapsed;
        }
        else
        {
            AccountName.Text = Loc.T("Sign in to SoundCloud");
            AccountHint.Text = Loc.T("Likes, feed and playlists");
            AccountAvatar.ImageSource = null;
            AccountInitial.Visibility = Visibility.Visible;
        }
    }

    void AccountBox_Click(object s, MouseButtonEventArgs e)
    {
        if (ScApi.Account.SignedIn)
        {
            var m = new ContextMenu();
            m.Items.Add(Mi("Your likes", "", () => Navigate("library", "likes")));
            m.Items.Add(Mi("Your feed", "", () => Navigate("library", "feed")));
            m.Items.Add(Mi("Your SoundCloud playlists", "", () => Navigate("library", "scsets")));
            m.Items.Add(new Separator());
            m.Items.Add(Mi("Sign out of SoundCloud", "", SignOutSc));
            OpenMenu(m, AccountBox);
        }
        else Bg.Run(StartScSignInAsync(), "SoundCloud", "Sign in");
    }

    // =====================================================================
    // Search box + suggestions
    // =====================================================================
    readonly DispatcherTimer searchTimer = new() { Interval = TimeSpan.FromMilliseconds(260) };
    int suggestVersion;

    void Search_TextChanged(object s, TextChangedEventArgs e) { searchTimer.Stop(); searchTimer.Start(); }

    void Search_Focus(object s, KeyboardFocusChangedEventArgs e) { Bg.Run(UpdateSuggestionsAsync(), "UI", "UpdateSuggestionsAsync"); }

    void Search_LostFocus(object s, KeyboardFocusChangedEventArgs e)
    {
        // close a moment later so a click on a suggestion still lands
        var t = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(180) };
        t.Tick += (_, _) => { t.Stop(); if (!SearchBox.IsKeyboardFocused) SuggestPopup.IsOpen = false; };
        t.Start();
    }

    void Search_KeyDown(object s, KeyEventArgs e)
    {
        if (e.Key == Key.Enter) { RunSearch(SearchBox.Text); e.Handled = true; }
        else if (e.Key == Key.Escape) { SuggestPopup.IsOpen = false; Keyboard.ClearFocus(); FocusManager.SetFocusedElement(this, this); e.Handled = true; }
        else if (e.Key == Key.Down && SuggestPopup.IsOpen && SuggestHost.Children.OfType<Button>().FirstOrDefault() is { } first) { first.Focus(); e.Handled = true; }
    }

    void RunSearch(string q) => Bg.Run(RunSearchAsync(q), "Search", "Run search");

    async Task RunSearchAsync(string q)
    {
        q = q.Trim();
        SuggestPopup.IsOpen = false;
        if (q.Length == 0) return;
        if (IsLink(q))
        {
            // pasting a link plays/imports it right away
            SearchBox.Text = "";
            try
            {
                Toast("Opening link…", "");
                var tracks = await ImportLinkAsync(q);
                if (tracks.Count == 1) PlayNow(tracks[0]);
                else if (tracks.Count > 1) { ShowTrackListPage("From a link", Loc.Songs(tracks.Count), tracks, ""); PlayContext(tracks, 0, "From a link"); }
                else Toast("Nothing found at that link", "");
            }
            catch (Exception ex) { Toast(ex.Message, ""); }
            return;
        }
        Library.AddSearch(q);
        Navigate("discover", q);
    }

    async Task UpdateSuggestionsAsync()
    {
        if (!SearchBox.IsKeyboardFocused) return;
        int version = ++suggestVersion;
        var q = SearchBox.Text.Trim();
        var items = new List<(string Text, string Glyph, bool History)>();
        items.AddRange(Library.Data.Searches.Where(s => q.Length == 0 || s.Contains(q, StringComparison.OrdinalIgnoreCase)).Take(q.Length == 0 ? 8 : 4).Select(s => (s, "", true)));
        if (q.Length >= 2 && !IsLink(q) && provider is SoundCloud)
        {
            try
            {
                var more = await ScApi.SuggestAsync(q);
                if (version != suggestVersion) return;
                items.AddRange(more.Where(m => items.All(i => !i.Text.Equals(m, StringComparison.OrdinalIgnoreCase))).Take(7).Select(m => (m, "", false)));
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // suggestions are a nice-to-have: without them the box still shows your recent searches
                if (ex is not OfflineException) Log.Info("Search", "No suggestions: " + ex.Message);
            }
        }
        if (version != suggestVersion) return;
        SuggestHost.Children.Clear();
        if (items.Count == 0) { SuggestPopup.IsOpen = false; return; }
        if (q.Length == 0) { var h = Txt(Loc.T("RECENT SEARCHES"), 11, FontWeights.SemiBold, "TextFaintBrush"); h.Margin = new Thickness(10, 6, 0, 6); SuggestHost.Children.Add(h); }
        foreach (var (text, glyph, history) in items)
        {
            var b = new Button { Style = S("SuggestRow"), Tag = glyph, Content = text };
            var value = text;
            b.Click += (_, _) => { SearchBox.Text = value; RunSearch(value); };
            if (history)
            {
                var rm = new MenuItem { Header = Loc.T("Remove from history") };
                rm.Click += (_, _) => { Library.RemoveSearch(value); Bg.Run(UpdateSuggestionsAsync(), "UI", "UpdateSuggestionsAsync"); };
                b.ContextMenu = new ContextMenu { Items = { rm } };
            }
            SuggestHost.Children.Add(b);
        }
        SuggestPopup.IsOpen = true;
    }

    // =====================================================================
    // Toasts
    // =====================================================================
    public void Toast(string text, string glyph = "")
    {
        text = Loc.T(text);
        var row = new StackPanel { Orientation = Orientation.Horizontal };
        row.Children.Add(new TextBlock { Text = glyph, FontFamily = (FontFamily)FindResource("Icons"), Foreground = Res("AccentBrush"), FontSize = 14, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 10, 0) });
        row.Children.Add(new TextBlock { Text = text, Foreground = Res("TextBrush"), FontSize = 13, VerticalAlignment = VerticalAlignment.Center, TextWrapping = TextWrapping.Wrap, MaxWidth = 520 });
        var pill = new Border
        {
            Background = Res("MenuBrush"), BorderBrush = Res("GlassEdge"), BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(18), Padding = new Thickness(16, 10, 18, 10), Margin = new Thickness(0, 6, 0, 0),
            Child = row, HorizontalAlignment = HorizontalAlignment.Center, Effect = (System.Windows.Media.Effects.Effect)FindResource("SoftShadow")
        };
        while (ToastHost.Children.Count >= 3) ToastHost.Children.RemoveAt(0);
        ToastHost.Children.Add(pill);
        Motion.Enter(pill, 0, 16, Motion.Base + 80);
        var t = new DispatcherTimer { Interval = TimeSpan.FromSeconds(Math.Clamp(text.Length / 18.0, 2.4, 6)) };
        t.Tick += (_, _) => { t.Stop(); Motion.Leave(pill, () => ToastHost.Children.Remove(pill), 10, Motion.Base); };
        t.Start();
    }

    // =====================================================================
    // Modal dialogs
    // =====================================================================
    TaskCompletionSource<bool>? modalResult;

    Task<bool> ShowModal(string title, UIElement? body, string ok, string cancel = "Cancel", bool danger = false)
    {
        title = Loc.T(title); ok = Loc.T(ok); cancel = Loc.T(cancel);
        modalResult?.TrySetResult(false);
        var tcs = new TaskCompletionSource<bool>();
        modalResult = tcs;
        modalOpen = true;
        WebAirspace();
        ModalTitle.Text = title;
        ModalBody.Content = body;
        ModalBody.Visibility = body == null ? Visibility.Collapsed : Visibility.Visible;
        ModalButtons.Children.Clear();
        if (cancel.Length > 0) ModalButtons.Children.Add(ActionButton(cancel, () => CloseModal(false)));
        var okBtn = ActionButton(ok, () => CloseModal(true), true);
        okBtn.Margin = new Thickness(0);
        okBtn.IsDefault = true;
        if (danger) okBtn.Background = new SolidColorBrush(Color.FromRgb(0xE5, 0x48, 0x4D));
        ModalButtons.Children.Add(okBtn);
        ModalOverlay.Visibility = Visibility.Visible;
        Motion.FromTo(ModalDim, OpacityProperty, 0, 1, Motion.Base);
        Motion.PopIn(ModalCard);
        Dispatcher.InvokeAsync(() => { if (body is FrameworkElement fe && FindFocusable(fe) is { } f) f.Focus(); else okBtn.Focus(); }, DispatcherPriority.Input);
        return tcs.Task;
    }

    static UIElement? FindFocusable(DependencyObject d)
    {
        if (d is TextBox tb) return tb;
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(d); i++)
            if (FindFocusable(VisualTreeHelper.GetChild(d, i)) is { } r) return r;
        if (d is Panel p) foreach (var c in p.Children) if (c is TextBox t) return t;
        return null;
    }

    void CloseModal(bool ok)
    {
        if (!modalOpen) return;
        modalOpen = false;
        var tcs = modalResult;
        modalResult = null;
        Motion.To(ModalDim, OpacityProperty, 0, Motion.Base);
        Motion.PopOut(ModalCard, () => { if (!modalOpen) ModalOverlay.Visibility = Visibility.Collapsed; WebAirspace(); });
        tcs?.TrySetResult(ok);
    }

    void ModalDim_Click(object s, MouseButtonEventArgs e) => CloseModal(false);

    TextBox Field(string placeholder, string text = "", bool multiline = false)
    {
        placeholder = Loc.T(placeholder);
        var tb = new TextBox { Style = S("Field"), Tag = placeholder, Text = text, Margin = new Thickness(0, 0, 0, 10) };
        if (multiline) { tb.AcceptsReturn = true; tb.TextWrapping = TextWrapping.Wrap; tb.MinHeight = 70; tb.VerticalContentAlignment = VerticalAlignment.Top; }
        tb.KeyDown += (_, e) => { if (e.Key == Key.Enter && !multiline) { e.Handled = true; CloseModal(true); } };
        return tb;
    }

    async Task<string?> Prompt(string title, string placeholder, string initial = "", string ok = "Save")
    {
        title = Loc.T(title);
        var tb = Field(placeholder, initial);
        tb.SelectAll();
        return await ShowModal(title, tb, ok) ? tb.Text.Trim() : null;
    }

    // =====================================================================
    // Context menus
    // =====================================================================
    MenuItem Mi(string header, string glyph, Action click, bool enabled = true, bool translate = true)
    {
        if (translate) header = Loc.T(header);
        var m = new MenuItem { Header = header, Icon = new TextBlock { Text = glyph }, IsEnabled = enabled };
        m.Click += (_, e) => { e.Handled = true; click(); };
        return m;
    }

    void OpenMenu(ContextMenu menu, FrameworkElement target)
    {
        menu.PlacementTarget = target;
        menu.Placement = System.Windows.Controls.Primitives.PlacementMode.MousePoint;
        menu.Opened += (_, _) => Motion.PopIn(menu, .95, Motion.Base);
        menu.IsOpen = true;
    }

    /// <summary>The menu for any song, anywhere in the app.</summary>
    ContextMenu TrackMenu(Track t, IList<Track> context, int index, string contextName, RowOrigin? origin)
    {
        var m = new ContextMenu();
        m.Items.Add(Mi("Play", "", () => PlayContext(context, index, contextName)));
        m.Items.Add(Mi("Play next", "", () => PlayNextInQueue(t)));
        m.Items.Add(Mi("Add to queue", "", () => AddToQueue(new[] { t })));
        m.Items.Add(new Separator());
        bool fav = Library.IsFavorite(t);
        m.Items.Add(Mi(fav ? "Remove from favorites" : "Add to favorites", fav ? "" : "", () => ToggleFavorite(t)));
        var add = new MenuItem { Header = Loc.T("Add to playlist"), Icon = new TextBlock { Text = "" } };
        FillAddToPlaylist(add.Items, new[] { t });
        m.Items.Add(add);
        if (origin is { Editable: true })
        {
            m.Items.Add(Mi(origin.Kind == "sc" ? "Remove from this SoundCloud playlist" : "Remove from this playlist", "", () => Bg.Run(RemoveFromOriginAsync(t, index, origin), "UI", "RemoveFromOriginAsync")));
        }
        m.Items.Add(new Separator());
        if (t.Source == "SoundCloud") m.Items.Add(Mi("Go to artist", "", () => Navigate("artist", t)));
        else m.Items.Add(Mi(Loc.T("More from {0}", t.Artist), "", () => { SearchBox.Text = t.Artist; RunSearch(t.Artist); }, true, false));
        if (t.Source is "SoundCloud" or "YouTube Music")
            m.Items.Add(Mi("Similar songs", "", () => Bg.Run(ShowSimilarAsync(t), "UI", "ShowSimilarAsync")));
        if (t.OpenUri != null && t.OpenUri.StartsWith("http"))
        {
            m.Items.Add(Mi(Loc.T("Open on {0}", t.Source), "", () => OpenUrl(t.OpenUri)));
            m.Items.Add(Mi("Copy link", "", () => CopyLink(t.OpenUri)));
        }
        return m;
    }

    void CopyLink(string url)
    {
        try { Clipboard.SetText(url); Toast(Loc.T("Link copied"), "\uE8C8"); }
        catch (System.Runtime.InteropServices.COMException ex) { Log.Warn("Shell", "Clipboard busy", ex); Toast(Loc.T("Couldn't copy: another app is using the clipboard"), "\uE7BA"); }
    }

    static void OpenUrl(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var u) || (u.Scheme != Uri.UriSchemeHttps && u.Scheme != Uri.UriSchemeHttp)) return; // only web links, never files or programs
        try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(u.AbsoluteUri) { UseShellExecute = true }); }
        catch (System.ComponentModel.Win32Exception ex) { Log.Warn("Shell", "Couldn't open the browser", ex); }
    }

    /// <summary>Lists where songs can be added, clearly split into LiquidCloud playlists and SoundCloud playlists.</summary>
    void FillAddToPlaylist(ItemCollection items, IList<Track> tracks)
    {
        items.Add(Mi("New LiquidCloud playlist…", "", async () =>
        {
            var name = await Prompt("New playlist", "Playlist name", tracks.Count == 1 ? tracks[0].Title : "");
            if (name == null) return;
            var p = Library.CreatePlaylist(name);
            AddToLocalPlaylist(p, tracks, null);
        }));
        if (Library.Data.Playlists.Count > 0)
        {
            items.Add(new Separator());
            items.Add(Mi("SAVED IN LIQUIDCLOUD", "", () => { }, false));
            foreach (var p in Library.Data.Playlists)
            {
                var pl = p;
                items.Add(Mi(p.Name, "", () => AddToLocalPlaylist(pl, tracks, null)));
            }
        }
        if (ScApi.Account.SignedIn && tracks.All(t => t.Source == "SoundCloud"))
        {
            items.Add(new Separator());
            items.Add(Mi("ON YOUR SOUNDCLOUD", "", () => { }, false));
            items.Add(Mi("New SoundCloud playlist…", "", async () =>
            {
                var name = await Prompt("New SoundCloud playlist", "Playlist name (it will be private)", "", "Create on SoundCloud");
                if (name == null) return;
                try
                {
                    var set = await ScApi.CreateScPlaylistAsync(name, tracks);
                    scSetsCache = null;
                    Toast(Loc.T("Created \"{0}\" on SoundCloud", name), "");
                }
                catch (Exception ex) { Toast(Loc.T("Not saved. {0}", ex.Message), ""); }
            }));
            if (scSetsCache == null)
            {
                items.Add(Mi("Loading your SoundCloud playlists…", "", () => { }, false));
                Bg.Run(LoadScSetsAsync(), "UI", "LoadScSetsAsync");
            }
            else
                foreach (var (set, mine) in scSetsCache.Where(x => x.Mine))
                {
                    var target = set;
                    items.Add(Mi(set.Title, "", () => Bg.Run(AddToScSetAsync(target, tracks), "UI", "AddToScSetAsync")));
                }
        }
    }

    async Task LoadScSetsAsync()
    {
        try { scSetsCache = await ScApi.MySetsAsync(); }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Log.Warn("SoundCloud", "Couldn't load your playlists for the menu", ex);
            scSetsCache = new();
        }
    }

    void AddToLocalPlaylist(LocalPlaylist p, IList<Track> tracks, UIElement? animate)
    {
        int n = Library.AddToPlaylist(p, tracks);
        if (animate != null) Motion.Pulse(animate, 1.08);
        Toast(n == 0 ? Loc.T("Already in \"{0}\"", p.Name) : Loc.T("Added {0} to \"{1}\" · saved in LiquidCloud", Loc.Songs(n), p.Name), n == 0 ? "" : "");
    }

    async Task AddToScSetAsync(ScSet set, IList<Track> tracks)
    {
        Toast(Loc.T("Saving to \"{0}\" on SoundCloud…", set.Title), "");
        try
        {
            await ScApi.AddToScPlaylistAsync(set.Id, tracks);
            Toast(Loc.T("Added to \"{0}\" · saved on SoundCloud", set.Title), "");
        }
        catch (Exception ex) { Toast(Loc.T("Not saved to SoundCloud. {0}", ex.Message), ""); }
    }

    async Task RemoveFromOriginAsync(Track t, int index, RowOrigin origin)
    {
        if (origin.Kind == "local" && Library.FindPlaylist(origin.Id) is { } p)
        {
            int i = p.Tracks.FindIndex(s => Library.Key(s.Track) == Library.Key(t));
            Library.RemoveFromPlaylist(p, i >= 0 ? i : index);
            Toast(Loc.T("Removed from \"{0}\" · saved in LiquidCloud", p.Name), "");
            if (currentPage == "playlist") Navigate("playlist", p.Id, false);
        }
        else if (origin.Kind == "sc")
        {
            try
            {
                await ScApi.RemoveFromScPlaylistAsync(origin.Id, t);
                Toast("Removed · saved on SoundCloud", "");
                if (currentPage == "set" && currentArg is ScSet s) Navigate("set", s, false);
            }
            catch (Exception ex) { Toast(Loc.T("Not changed on SoundCloud. {0}", ex.Message), ""); }
        }
    }

    ContextMenu PlaylistMenu(LocalPlaylist p)
    {
        var m = new ContextMenu();
        m.Items.Add(Mi("Play", "", () => PlayContext(p.Tracks.Select(s => s.Track).ToList(), 0, p.Name)));
        m.Items.Add(Mi("Shuffle play", "", () => PlayShuffled(p.Tracks.Select(s => s.Track).ToList(), p.Name)));
        m.Items.Add(Mi("Add to queue", "", () => AddToQueue(p.Tracks.Select(s => s.Track))));
        m.Items.Add(new Separator());
        m.Items.Add(Mi("Edit details…", "", () => Bg.Run(EditPlaylistAsync(p), "UI", "EditPlaylistAsync")));
        m.Items.Add(Mi("Delete playlist…", "", () => Bg.Run(DeletePlaylistAsync(p), "UI", "DeletePlaylistAsync")));
        return m;
    }

    void NewPlaylist_Click(object s, RoutedEventArgs e) => Bg.Run(NewPlaylistAsync(), "UI", "NewPlaylistAsync");

    async Task NewPlaylistAsync()
    {
        var name = await Prompt("New playlist", "Playlist name", "", "Create");
        if (name == null) return;
        var p = Library.CreatePlaylist(name);
        Toast(Loc.T("Created \"{0}\" · saved in LiquidCloud", p.Name), "");
        Navigate("playlist", p.Id);
    }

    // =====================================================================
    // Queue list: drag to reorder, right-click menu
    // =====================================================================
    int queueDragFrom = -1;

    int QueueIndexAt(Point p)
    {
        var hit = List.InputHitTest(p) as DependencyObject;
        var item = hit != null ? FindParent<ListBoxItem>(hit) : null;
        return item != null ? List.ItemContainerGenerator.IndexFromContainer(item) : -1;
    }

    void Queue_MouseDown(object s, MouseButtonEventArgs e)
    {
        dragStart = e.GetPosition(this);
        queueDragFrom = QueueIndexAt(e.GetPosition(List));
    }

    void Queue_MouseMove(object s, MouseEventArgs e)
    {
        if (e.LeftButton != MouseButtonState.Pressed || queueDragFrom < 0) return;
        var p = e.GetPosition(this);
        if (Math.Abs(p.Y - dragStart.Y) < 8) return;
        int from = queueDragFrom;
        queueDragFrom = -1;
        DragDrop.DoDragDrop(List, new DataObject("LiquidCloud.QueueIndex", from), DragDropEffects.Move);
    }

    void Queue_Drop(object s, DragEventArgs e)
    {
        if (e.Data.GetData("LiquidCloud.QueueIndex") is not int from) return;
        int to = QueueIndexAt(e.GetPosition(List));
        if (to < 0) to = List.Items.Count - 1;
        queue.Move(from, to);
    }

    void Queue_RightClick(object s, MouseButtonEventArgs e)
    {
        int i = QueueIndexAt(e.GetPosition(List));
        if (i < 0) return;
        var items = queue.InPlayOrder();
        var t = items[i];
        var m = TrackMenu(t, items, i, queue.Name, null);
        if (i != queue.Position) m.Items.Insert(3, Mi("Remove from queue", "", () => queue.RemoveAt(i)));
        OpenMenu(m, List);
    }

    // =====================================================================
    // Keyboard and media keys
    // =====================================================================
    void OnKey(object s, KeyEventArgs e)
    {
        var mods = Keyboard.Modifiers;
        bool typing = Keyboard.FocusedElement is TextBox;

        if (e.Key == Key.Escape)
        {
            if (modalOpen) CloseModal(false);
            else if (settingsOpen) ToggleSettings(false);
            else if (miniMode) SetMini(false);
            else if (fullPlayer) SetFullPlayer(false);
            else if (expanded) SetPlayerTab("now");
            else return;
            e.Handled = true;
            return;
        }
        if (mods == ModifierKeys.Control && e.Key is Key.K or Key.F) { SearchBox.Focus(); SearchBox.SelectAll(); e.Handled = true; return; }
        if (mods == ModifierKeys.Control && e.Key == Key.M) { SetMini(!miniMode); e.Handled = true; return; }
        if (mods == ModifierKeys.Alt && e.SystemKey == Key.Left) { GoBack(); e.Handled = true; return; }
        if (mods == ModifierKeys.Alt && e.SystemKey == Key.Right) { GoForward(); e.Handled = true; return; }
        if (e.Key == Key.F11) { SetFullPlayer(!fullPlayer); e.Handled = true; return; }
        if (e.Key == Key.MediaPlayPause) { Bg.Run(TogglePlayAsync(), "Keys", "Play/pause"); e.Handled = true; return; }
        if (e.Key == Key.MediaNextTrack) { Bg.Run(AdvanceAsync(false), "Keys", "Next"); e.Handled = true; return; }
        if (e.Key == Key.MediaPreviousTrack) { Bg.Run(player.PreviousAsync(), "Keys", "Previous"); e.Handled = true; return; }
        if (typing || modalOpen) return;

        switch (e.Key)
        {
            case Key.Space: Bg.Run(TogglePlayAsync(), "Keys", "Play/pause"); break;
            case Key.Right when mods == ModifierKeys.Control: Bg.Run(AdvanceAsync(false), "Keys", "Next"); break;
            case Key.Left when mods == ModifierKeys.Control: Bg.Run(player.PreviousAsync(), "Keys", "Previous"); break;
            case Key.Right when mods == ModifierKeys.Shift: SeekBy(10_000); break;
            case Key.Left when mods == ModifierKeys.Shift: SeekBy(-10_000); break;
            case Key.Up when mods == ModifierKeys.Control: NudgeVolume(.05); break;
            case Key.Down when mods == ModifierKeys.Control: NudgeVolume(-.05); break;
            case Key.M when mods == ModifierKeys.None: ToggleMute(); break;
            case Key.S when mods == ModifierKeys.None: ToggleShuffle(); break;
            case Key.R when mods == ModifierKeys.None: CycleRepeat(); break;
            case Key.L when mods == ModifierKeys.None: if (nowPlaying != null) ToggleFavorite(nowPlaying, LikeBtn); break;
            case Key.Q when mods == ModifierKeys.None: SetPlayerTab(playerTab == "queue" ? "now" : "queue"); break;
            default: return;
        }
        e.Handled = true;
    }

    [DllImport("user32.dll")] static extern bool RegisterHotKey(IntPtr hWnd, int id, uint modifiers, uint vk);
    const int WM_HOTKEY = 0x0312;

    /// <summary>Play/pause, next and previous media keys work even while LiquidCloud is in the background.</summary>
    void RegisterMediaKeys(IntPtr hwnd)
    {
        // another app (e.g. Spotify) may already own a media key; then it still works while LiquidCloud is focused
        bool all = RegisterHotKey(hwnd, 0x4C01, 0, 0xB3) & RegisterHotKey(hwnd, 0x4C02, 0, 0xB0) & RegisterHotKey(hwnd, 0x4C03, 0, 0xB1);
        if (!all) Log.Info("Keys", "Some media keys are taken by another app");
        HwndSource.FromHwnd(hwnd)?.AddHook((IntPtr h, int msg, IntPtr w, IntPtr l, ref bool handled) =>
        {
            if (msg != WM_HOTKEY) return IntPtr.Zero;
            switch (w.ToInt32())
            {
                case 0x4C01: Bg.Run(TogglePlayAsync(), "Keys", "Play/pause"); handled = true; break;
                case 0x4C02: Bg.Run(AdvanceAsync(false), "Keys", "Next"); handled = true; break;
                case 0x4C03: Bg.Run(player.PreviousAsync(), "Keys", "Previous"); handled = true; break;
            }
            return IntPtr.Zero;
        });
    }

    // =====================================================================
    // Mini player and full-window player
    // =====================================================================
    Rect normalBounds;
    WindowState normalState;

    void Mini_Click(object s, RoutedEventArgs e) => SetMini(!miniMode);

    void SetMini(bool on)
    {
        if (on == miniMode) return;
        miniMode = on;
        if (on)
        {
            normalState = WindowState;
            WindowState = WindowState.Normal;
            normalBounds = new Rect(Left, Top, Width, Height);
            TitleBar.Visibility = Body.Visibility = Visibility.Collapsed;
            MiniBar.Visibility = Visibility.Visible;
            MinWidth = 380; MinHeight = 100;
            Width = 440; Height = 108;
            var area = SystemParameters.WorkArea;
            Left = area.Right - Width - 24;
            Top = area.Bottom - Height - 24;
            Topmost = true;
            ResizeMode = ResizeMode.NoResize;
            SetCaption(108);
            Particles.Visibility = Visibility.Collapsed;
            Motion.PopIn(MiniBar, .9);
        }
        else
        {
            Topmost = false;
            ResizeMode = ResizeMode.CanResize;
            SetCaption(52);
            MiniBar.Visibility = Visibility.Collapsed;
            TitleBar.Visibility = Body.Visibility = Visibility.Visible;
            MinWidth = 1040; MinHeight = 660;
            Left = normalBounds.Left; Top = normalBounds.Top; Width = normalBounds.Width; Height = normalBounds.Height;
            WindowState = normalState;
            Particles.Visibility = cfg.ParticlesOn ? Visibility.Visible : Visibility.Collapsed;
            Motion.Enter(Body, 0, 12);
        }
        ApplyActivity();
    }

    /// <summary>The draggable title area: the whole mini player, or the 52 px title bar.</summary>
    void SetCaption(double height) => System.Windows.Shell.WindowChrome.SetWindowChrome(this, new System.Windows.Shell.WindowChrome
    {
        CaptionHeight = height, GlassFrameThickness = new Thickness(-1), ResizeBorderThickness = new Thickness(height > 60 ? 0 : 6),
        CornerRadius = new CornerRadius(0), UseAeroCaptionButtons = false
    });

    void Full_Click(object s, RoutedEventArgs e) => SetFullPlayer(!fullPlayer);

    /// <summary>The player takes the whole window: big cover, lyrics and queue tabs.</summary>
    void SetFullPlayer(bool on)
    {
        if (on == fullPlayer) return;
        fullPlayer = on;
        Sidebar.Visibility = CenterPanel.Visibility = on ? Visibility.Collapsed : Visibility.Visible;
        SideCol.Width = new GridLength(on ? 0 : 236);
        GapCol1.Width = GapCol2.Width = new GridLength(on ? 0 : 12);
        CenterCol.Width = new GridLength(on ? 0 : 1, on ? GridUnitType.Pixel : GridUnitType.Star);
        NowCol.Width = on ? new GridLength(1, GridUnitType.Star) : new GridLength(380);
        NowPanel.Padding = on ? new Thickness(60, 26, 60, 30) : new Thickness(26, 18, 26, 20);
        Controls.MaxWidth = on ? 760 : double.PositiveInfinity; // full window: controls stay a comfortable width, centered
        FullBtn.Content = on ? "" : "";
        FullBtn.ToolTip = Loc.T(on ? "Exit full-window player (F11)" : "Full-window player (F11)");
        if (on && browsing && sc != null) sc.Visibility = Visibility.Hidden;
        else WebAirspace();
        Motion.Enter(NowPanel, 0, 10, Motion.Slow);
        QueuePositionArt();
    }

    // =====================================================================
    // Welcome intro
    // =====================================================================
    bool welcomeDone;

    void ShowWelcomeOrApp()
    {
        if (!cfg.ShowWelcome || Motion.Reduced)
        {
            Motion.FromTo(Root, OpacityProperty, 0, 1, Motion.Slow);
            Motion.Enter(Body, 60, 16, Motion.Slow);
            return;
        }
        var name = ScApi.Account.UserName ?? cfg.LastUserName ?? PrettyUserName();
        int h = DateTime.Now.Hour;
        string part = h switch { >= 5 and < 12 => "morning", >= 12 and < 17 => "afternoon", >= 17 and < 22 => "evening", _ => "night" };
        WelcomeHi.Text = string.IsNullOrWhiteSpace(name) ? Loc.T("Hi there.") : Loc.T("Hi, {0}.", name);
        WelcomeLine.Text = Loc.T("Nice " + part + ", isn't it?");
        Welcome.Visibility = Visibility.Visible;

        // slow drifting lights
        void Drift(TranslateTransform t, double x, double y, double s)
        {
            t.BeginAnimation(TranslateTransform.XProperty, new DoubleAnimation(0, x, TimeSpan.FromSeconds(s)) { AutoReverse = true, RepeatBehavior = RepeatBehavior.Forever, EasingFunction = Motion.InOut });
            t.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(0, y, TimeSpan.FromSeconds(s * 1.3)) { AutoReverse = true, RepeatBehavior = RepeatBehavior.Forever, EasingFunction = Motion.InOut });
        }
        Drift(WLight1, 140, 90, 6);
        Drift(WLight2, -120, -70, 7);
        Drift(WLight3, -90, 60, 5);

        Motion.FromTo(WelcomeLights, OpacityProperty, 0, 1, 1200);
        Motion.PopIn(WelcomeGlass, .92, 900);
        Motion.Enter(WelcomeIcon, 200, 10, 700);
        Motion.Enter(WelcomeBrand, 420, 14, 800);
        Motion.Enter(WelcomeHi, 1100, 14, 800);
        Motion.Enter(WelcomeLine, 1600, 14, 800);

        var t = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(3600) };
        t.Tick += (_, _) => { t.Stop(); EndWelcome(); };
        t.Start();
    }

    static string PrettyUserName()
    {
        var n = Environment.UserName ?? "";
        return n.Length == 0 ? "" : char.ToUpper(n[0]) + n[1..];
    }

    void Welcome_Click(object s, MouseButtonEventArgs e) => EndWelcome();

    void EndWelcome()
    {
        if (welcomeDone) return;
        welcomeDone = true;
        Motion.To(Welcome, OpacityProperty, 0, 700, Motion.InOut, () =>
        {
            Welcome.Visibility = Visibility.Collapsed;
            foreach (var t in new[] { WLight1, WLight2, WLight3 }) { t.BeginAnimation(TranslateTransform.XProperty, null); t.BeginAnimation(TranslateTransform.YProperty, null); }
        });
        Motion.FromTo(Root, OpacityProperty, 0, 1, 700, null, null, 150);
        Motion.Enter(Body, 220, 22, 800);
    }

    void Artist_Click(object s, MouseButtonEventArgs e)
    {
        if ((nowPlaying ?? queue.Current) is not { } t) return;
        if (t.Source == "SoundCloud") Navigate("artist", t);
        else { SearchBox.Text = t.Artist; RunSearch(t.Artist); }
    }

    async Task ShowSimilarAsync(Track t)
    {
        try
        {
            Toast("Finding similar songs…", "");
            var list = t.Source == "SoundCloud"
                ? await ((SoundCloud)providers["SoundCloud"]).RelatedAsync(t)
                : await ((YouTubeMusic)providers["YouTube Music"]).RelatedAsync(t);
            ShowTrackListPage(Loc.T("Like \"{0}\"", t.Title), Loc.T("Songs similar to {0} by {1}", t.Title, t.Artist), list, "");
        }
        catch (Exception ex) { Toast(ex.Message, ""); }
    }
}
