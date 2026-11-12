using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;

namespace GlassPlayer;

/// <summary>The pages: Home, Discover (search), genres, Library, playlists, SoundCloud sets and artists.</summary>
public partial class MainWindow
{
    static string Greeting()
    {
        int h = DateTime.Now.Hour;
        return h switch { >= 5 and < 12 => "Good morning", >= 12 and < 17 => "Good afternoon", >= 17 and < 22 => "Good evening", _ => "Good night" };
    }

    StackPanel Slot() => new();

    static string TotalTime(IEnumerable<Track> tracks)
    {
        long ms = tracks.Sum(t => (long)t.DurationMs);
        if (ms <= 0) return "";
        var ts = TimeSpan.FromMilliseconds(ms);
        return ts.TotalHours >= 1 ? $"{(int)ts.TotalHours} h {ts.Minutes} min" : $"{ts.Minutes} min";
    }

    IEnumerable<FrameworkElement> Cards(IList<Track> tracks, string name, int max = 20) =>
        tracks.Take(max).Select((t, i) => TrackCard(t, tracks, i, name));

    /// <summary>Async shelf: title + skeleton, then cards (hidden entirely if there's nothing to show).</summary>
    void AsyncShelf(Panel body, string title, string? subtitle, Func<Task<List<Track>>> load, string contextName, Action? seeAll = null)
    {
        var head = SectionTitle(title, subtitle, seeAll);
        var slot = Slot();
        body.Children.Add(head);
        body.Children.Add(slot);
        LoadInto(slot, "shelf", async () =>
        {
            var list = await load();
            if (list.Count == 0) { head.Visibility = Visibility.Collapsed; return new Border(); }
            return Shelf(Cards(list, contextName));
        });
    }

    // =====================================================================
    // Home
    // =====================================================================
    FrameworkElement BuildHome()
    {
        var page = NewPage();
        var b = page.Body;
        var name = ScApi.Account.UserName ?? cfg.LastUserName ?? PrettyUserName();
        b.Children.Add(PageHeader($"{Greeting()}{(string.IsNullOrWhiteSpace(name) ? "" : ", " + name)}", "Pick up where you left off, or find something new.", null, DateTime.Now.ToString("dddd, d MMMM")));

        var recent = Library.RecentlyPlayed();
        bool fresh = recent.Count == 0 && Library.Data.Favorites.Count == 0;

        if (fresh)
        {
            b.Children.Add(EmptyState("", "Let's find your music", "Search SoundCloud for any song, artist or playlist, or explore a genre below. Everything you play and like shows up here.", "Open Discover", () => Navigate("discover")));
            b.Children.Add(SectionTitle("Explore genres"));
            b.Children.Add(CardGrid(ScApi.Genres.Take(8).Select(g => GenreTile(g.Name, g.Color1, g.Color2, () => Navigate("genre", (g.Name, g.Tag))))));
            AsyncShelf(b, "Trending on SoundCloud", "What people are playing right now", () => ScApi.PopularAsync("all"), "Trending on SoundCloud");
            return page.Scroll;
        }

        // continue listening: songs you stopped in the middle, plus the last session
        var cont = Library.Data.Partial.Select(p => p.Track).ToList();
        if (queue.Current is { } cur && cont.All(c => Library.Key(c) != Library.Key(cur))) cont.Insert(0, cur);
        if (cont.Count > 0) { b.Children.Add(SectionTitle("Continue listening", "Starts where you stopped")); b.Children.Add(Shelf(Cards(cont, "Continue listening"))); }

        if (recent.Count > 0)
        {
            b.Children.Add(SectionTitle("Recently played", null, () => Navigate("library", "history")));
            b.Children.Add(Shelf(Cards(recent, "Recently played")));
        }

        var rotation = Library.Rotation();
        if (rotation.Count >= 3) { b.Children.Add(SectionTitle("Your rotation", "What you've had on repeat this month")); b.Children.Add(Shelf(Cards(rotation, "Your rotation"))); }

        // personal mixes built from the artists you play most
        var mixes = Library.TopArtists(6).Where(a => a != "This PC").ToList();
        if (mixes.Count > 0)
        {
            b.Children.Add(SectionTitle("Made for you", "Mixes around the artists you play most"));
            var cards = mixes.Select((a, i) =>
            {
                var seed = Library.Data.History.Select(h => h.Track).FirstOrDefault(t => t.Artist == a);
                var c1 = ScApi.Genres[(i * 5 + 1) % ScApi.Genres.Length];
                var grad = new LinearGradientBrush(ThemeDef.C(c1.Color1, Colors.SteelBlue), ThemeDef.C(c1.Color2, Colors.Navy), 45);
                return CollectionCard($"{a} Mix", "With similar artists", seed?.Art, () => _ = OpenMixAsync(a, seed), false, grad, "", null, () => _ = OpenMixAsync(a, seed, true));
            });
            b.Children.Add(Shelf(cards));
        }

        var seeds = rotation.Concat(recent).Where(t => t.Source == "SoundCloud").DistinctBy(Library.Key).ToList();
        if (seeds.Count > 0)
        {
            var s1 = seeds[0];
            AsyncShelf(b, "Recommended for you", $"Because you listen to {s1.Artist}", async () =>
            {
                var known = new HashSet<string>(Library.Data.History.Select(h => Library.Key(h.Track)));
                var list = new List<Track>();
                foreach (var s in seeds.Take(3))
                {
                    try { list.AddRange(await ((SoundCloud)providers["SoundCloud"]).RelatedAsync(s)); } catch { }
                }
                return list.Where(t => !known.Contains(Library.Key(t))).DistinctBy(Library.Key).Take(24).ToList();
            }, "Recommended for you");
        }

        if (Library.Data.Favorites.Count > 0)
        {
            var favs = Library.Data.Favorites.Select(f => f.Track).ToList();
            b.Children.Add(SectionTitle("Favorite tracks", null, () => Navigate("library", "favorites")));
            b.Children.Add(Shelf(Cards(favs, "Favorites")));
            var lastFav = favs.FirstOrDefault(t => t.Source == "SoundCloud");
            if (lastFav != null)
                AsyncShelf(b, "New discoveries", $"Fresh finds near \"{lastFav.Title}\"", async () =>
                {
                    var known = new HashSet<string>(Library.Data.History.Select(h => Library.Key(h.Track)).Concat(favs.Select(Library.Key)));
                    var list = await ((SoundCloud)providers["SoundCloud"]).RelatedAsync(lastFav);
                    return list.Where(t => !known.Contains(Library.Key(t))).Skip(2).Take(20).ToList();
                }, "New discoveries");
        }

        var added = Library.RecentlyAdded();
        if (added.Count > 0) { b.Children.Add(SectionTitle("Recently added", null, () => Navigate("library", "recent"))); b.Children.Add(Shelf(Cards(added, "Recently added"))); }

        if (Library.Data.Playlists.Count > 0)
        {
            b.Children.Add(SectionTitle("Your playlists", "Saved in LiquidCloud", () => Navigate("library", "playlists")));
            b.Children.Add(Shelf(Library.Data.Playlists.Select(PlaylistCard)));
        }

        var playlistSlot = Slot();
        var plHead = SectionTitle("Popular playlists", "On SoundCloud");
        b.Children.Add(plHead);
        b.Children.Add(playlistSlot);
        string q = mixes.FirstOrDefault() ?? "top hits";
        LoadInto(playlistSlot, "shelf", async () =>
        {
            var p = await ScApi.SearchPlaylistsAsync(q);
            if (p.Items.Count == 0) p = await ScApi.SearchPlaylistsAsync("top hits");
            if (p.Items.Count == 0) { plHead.Visibility = Visibility.Collapsed; return new Border(); }
            return Shelf(p.Items.Select(SetCard));
        });
        return page.Scroll;
    }

    async Task OpenMixAsync(string artist, Track? seed, bool playNow = false)
    {
        try
        {
            Toast($"Making your {artist} mix…", "");
            var mine = Library.Data.History.Select(h => h.Track).Where(t => t.Artist == artist).DistinctBy(Library.Key).Take(5).ToList();
            var list = new List<Track>(mine);
            if (seed?.Source == "SoundCloud") list.AddRange(await ((SoundCloud)providers["SoundCloud"]).RelatedAsync(seed));
            else if (seed?.Source == "YouTube Music") list.AddRange(await ((YouTubeMusic)providers["YouTube Music"]).RelatedAsync(seed));
            else if (provider is SoundCloud) list.AddRange((await ScApi.SearchTracksAsync(artist)).Items);
            list = list.DistinctBy(Library.Key).ToList();
            // interleave your songs with suggestions so the mix feels personal but fresh
            var rnd = new Random(artist.GetHashCode());
            var mixed = mine.Concat(list.Skip(mine.Count).OrderBy(_ => rnd.Next())).Take(40).ToList();
            if (playNow) PlayContext(mixed, 0, $"{artist} Mix");
            else ShowTrackListPage($"{artist} Mix", "Songs you play by this artist, mixed with similar music", mixed, "");
        }
        catch (Exception ex) { Toast(ex.Message, ""); }
    }

    FrameworkElement PlaylistCard(LocalPlaylist p)
    {
        var art = p.CoverPath ?? p.Tracks.Select(t => t.Track.Art).FirstOrDefault(a => a != null);
        var tracks = p.Tracks.Select(t => t.Track).ToList();
        return CollectionCard(p.Name, $"{p.Tracks.Count} songs · LiquidCloud", art, () => Navigate("playlist", p.Id), false, null, "",
            () => PlaylistMenu(p), tracks.Count > 0 ? () => PlayContext(tracks, 0, p.Name) : null);
    }

    FrameworkElement SetCard(ScSet s) =>
        CollectionCard(s.Title, $"{(s.IsAlbum ? "Album" : "Playlist")} · {s.Owner}", s.Art, () => Navigate("set", s), false, null, s.IsAlbum ? "" : "",
            () => SetMenu(s), () => _ = PlaySetAsync(s, false));

    FrameworkElement ArtistCard(ScUser u) =>
        CollectionCard(u.Name, u.Followers > 0 ? $"{Compact(u.Followers)} followers" : "Artist", u.Avatar, () => Navigate("artist", u), true, null, "");

    static string Compact(int n) => n >= 1_000_000 ? $"{n / 1_000_000.0:0.#}M" : n >= 1000 ? $"{n / 1000.0:0.#}K" : n.ToString();

    ContextMenu SetMenu(ScSet s)
    {
        var m = new ContextMenu();
        m.Items.Add(Mi("Play", "", () => _ = PlaySetAsync(s, false)));
        m.Items.Add(Mi("Shuffle play", "", () => _ = PlaySetAsync(s, true)));
        m.Items.Add(Mi("Save a copy in LiquidCloud", "", () => _ = CopySetAsync(s)));
        if (s.Url != null) m.Items.Add(Mi("Open on SoundCloud", "", () => OpenUrl(s.Url)));
        return m;
    }

    async Task PlaySetAsync(ScSet s, bool shuffle)
    {
        try
        {
            var (_, tracks) = await ScApi.SetAsync(s.Id);
            if (tracks.Count == 0) { Toast("Nothing playable in this playlist", ""); return; }
            if (shuffle) PlayShuffled(tracks, s.Title); else PlayContext(tracks, 0, s.Title);
        }
        catch (Exception ex) { Toast(ex.Message, ""); }
    }

    async Task CopySetAsync(ScSet s)
    {
        try
        {
            var (_, tracks) = await ScApi.SetAsync(s.Id);
            var p = Library.CreatePlaylist(s.Title, $"Copied from SoundCloud ({s.Owner})");
            Library.AddToPlaylist(p, tracks);
            Toast($"Saved a copy of \"{s.Title}\" in LiquidCloud ({tracks.Count} songs)", "");
        }
        catch (Exception ex) { Toast(ex.Message, ""); }
    }

    // =====================================================================
    // Discover + search
    // =====================================================================
    FrameworkElement BuildDiscover(string? query)
    {
        var page = NewPage();
        var b = page.Body;
        var sources = providers.Values.Where(p => p is not LocalFiles).ToList();

        UIElement SourceBar() => ChipBar(sources.Select(p => (UIElement)Chip(p.Name, p == provider, () =>
        {
            provider = p;
            Navigate("discover", query, false);
        }, "src")).ToArray());

        if (string.IsNullOrWhiteSpace(query))
        {
            b.Children.Add(PageHeader("Discover", "Search the whole of SoundCloud, or browse by genre.", SourceBar()));

            if (Library.Data.Searches.Count > 0)
            {
                var head = SectionTitle("Recent searches");
                b.Children.Add(head);
                var wrap = new WrapPanel();
                foreach (var s in Library.Data.Searches)
                {
                    var q = s;
                    var chip = ActionButton(q, () => { SearchBox.Text = q; RunSearch(q); }, false, "");
                    chip.Margin = new Thickness(0, 0, 8, 8);
                    chip.ContextMenu = new ContextMenu();
                    chip.ContextMenu.Items.Add(Mi("Remove", "", () => { Library.RemoveSearch(q); Navigate("discover", null, false); }));
                    wrap.Children.Add(chip);
                }
                var clear = ActionButton("Clear", () => { Library.ClearSearches(); Navigate("discover", null, false); }, false, "");
                clear.Margin = new Thickness(0, 0, 8, 8);
                wrap.Children.Add(clear);
                b.Children.Add(wrap);
            }

            b.Children.Add(SectionTitle("Genres & moods"));
            b.Children.Add(CardGrid(ScApi.Genres.Select(g => GenreTile(g.Name, g.Color1, g.Color2, () => Navigate("genre", (g.Name, g.Tag))))));

            AsyncShelf(b, "Trending on SoundCloud", "Popular right now", () => ScApi.PopularAsync("all"), "Trending on SoundCloud");
            AsyncShelf(b, "New this week", "Fresh uploads in electronic music", () => ScApi.NewReleasesAsync("electronic"), "New this week");
            AsyncShelf(b, "New in hip-hop", "Uploaded in the last week", () => ScApi.NewReleasesAsync("hip-hop"), "New in hip-hop");

            var artistsHead = SectionTitle("Popular artists");
            var artistSlot = Slot();
            b.Children.Add(artistsHead);
            b.Children.Add(artistSlot);
            LoadInto(artistSlot, "shelf", async () =>
            {
                var users = new List<ScUser>();
                foreach (var g in new[] { "electronic", "hip hop", "pop" })
                {
                    try { users.AddRange((await ScApi.SearchUsersAsync(g)).Items); } catch { }
                }
                var top = users.DistinctBy(u => u.Id).OrderByDescending(u => u.Followers).Take(16).ToList();
                if (top.Count == 0) { artistsHead.Visibility = Visibility.Collapsed; return new Border(); }
                return Shelf(top.Select(ArtistCard));
            });

            var recSeed = Library.RecentlyPlayed().FirstOrDefault(t => t.Source == "SoundCloud");
            if (recSeed != null)
                AsyncShelf(b, "Recommended", $"Based on \"{recSeed.Title}\"", () => ((SoundCloud)providers["SoundCloud"]).RelatedAsync(recSeed), "Recommended");
            return page.Scroll;
        }

        // ---- results ----
        b.Children.Add(PageHeader($"“{query}”", provider is SoundCloud ? "Results from SoundCloud" : $"Results from {provider!.Name}", SourceBar(), "Search"));
        var content = Slot();

        if (provider is not SoundCloud)
        {
            b.Children.Add(content);
            var prov = provider!;
            LoadInto(content, "rows", async () =>
            {
                var list = await prov.SearchAsync(query);
                if (list.Count == 0) return EmptyState("", "No results", $"{prov.Name} found nothing for “{query}”. Try other words.");
                var sp = new StackPanel();
                sp.Children.Add(TrackList(page, list, $"Search: {query}"));
                return sp;
            }, 8);
            return page.Scroll;
        }

        string tab = "top";
        var tabs = ChipBar(
            Chip("Top", true, () => Show("top"), "res"),
            Chip("Tracks", false, () => Show("tracks"), "res"),
            Chip("Artists", false, () => Show("artists"), "res"),
            Chip("Playlists", false, () => Show("playlists"), "res"),
            Chip("Albums", false, () => Show("albums"), "res"));
        tabs.Margin = new Thickness(0, 0, 0, 14);
        b.Children.Add(tabs);
        b.Children.Add(content);

        void Show(string t)
        {
            tab = t;
            page.NearEnd = null;
            page.Scroll.ScrollToTop();
            switch (t)
            {
                case "tracks": ShowTracksTab(); break;
                case "artists": ShowGrid(n => ScApi.SearchUsersAsync(query, n), ArtistCard, "No artists found"); break;
                case "playlists": ShowGrid(n => ScApi.SearchPlaylistsAsync(query, n), SetCard, "No playlists found"); break;
                case "albums": ShowGrid(n => ScApi.SearchAlbumsAsync(query, n), SetCard, "No albums found"); break;
                default: ShowTop(); break;
            }
        }

        void ShowTop()
        {
            LoadInto(content, "rows", async () =>
            {
                var tracksTask = ScApi.SearchTracksAsync(query);
                var usersTask = ScApi.SearchUsersAsync(query);
                var setsTask = ScApi.SearchPlaylistsAsync(query);
                var tracks = (await tracksTask).Items;
                List<ScUser> users = new(); List<ScSet> sets = new();
                try { users = (await usersTask).Items; } catch { }
                try { sets = (await setsTask).Items; } catch { }
                if (tracks.Count == 0 && users.Count == 0 && sets.Count == 0)
                    return EmptyState("", "No results", $"SoundCloud found nothing for “{query}”. Check the spelling or try fewer words.");

                var sp = new StackPanel();
                if (tracks.Count > 0)
                {
                    // top result + best tracks side by side
                    var g = new Grid();
                    g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(300) });
                    g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(24) });
                    g.ColumnDefinitions.Add(new ColumnDefinition());
                    g.Children.Add(TopResult(tracks[0], tracks));
                    var rows = new StackPanel();
                    rows.Children.Add(Txt("Songs", 19, FontWeights.SemiBold));
                    for (int i = 0; i < Math.Min(5, tracks.Count); i++) rows.Children.Add(TrackRow(tracks[i], tracks, i, $"Search: {query}", null, false));
                    Grid.SetColumn(rows, 2);
                    g.Children.Add(rows);
                    sp.Children.Add(g);
                }
                if (users.Count > 0) { sp.Children.Add(SectionTitle("Artists", null, () => Show("artists"))); sp.Children.Add(Shelf(users.Take(12).Select(ArtistCard))); }
                if (sets.Count > 0) { sp.Children.Add(SectionTitle("Playlists", null, () => Show("playlists"))); sp.Children.Add(Shelf(sets.Take(12).Select(SetCard))); }
                if (tracks.Count > 5) { sp.Children.Add(SectionTitle("More songs", null, () => Show("tracks"))); sp.Children.Add(Shelf(Cards(tracks.Skip(5).ToList(), $"Search: {query}"))); }
                return sp;
            });
        }

        void ShowTracksTab()
        {
            LoadInto(content, "rows", async () =>
            {
                var first = await ScApi.SearchTracksAsync(query);
                if (first.Items.Count == 0) return EmptyState("", "No songs found", $"Nothing on SoundCloud matches “{query}”.");
                var all = new List<Track>(first.Items);
                var list = new StackPanel();
                void Add(IEnumerable<Track> items)
                {
                    foreach (var t in items)
                    {
                        int i = all.IndexOf(t);
                        var r = TrackRow(t, all, i, $"Search: {query}", null);
                        list.Children.Add(r);
                        Motion.Enter(r, Math.Min(list.Children.Count % 30, 12) * 20, 10, Motion.Base);
                    }
                }
                Add(first.Items);
                string? next = first.Next;
                page.NearEnd = async () =>
                {
                    if (next == null || tab != "tracks") return;
                    var more = await ScApi.SearchTracksAsync(query, next);
                    next = more.Items.Count > 0 ? more.Next : null;
                    var fresh = more.Items.Where(t => all.All(a => a.Id != t.Id)).ToList();
                    all.AddRange(fresh);
                    Add(fresh);
                };
                return list;
            }, 10);
        }

        void ShowGrid<T>(Func<string?, Task<Page<T>>> load, Func<T, FrameworkElement> card, string empty) where T : class
        {
            LoadInto(content, "shelf", async () =>
            {
                var first = await load(null);
                if (first.Items.Count == 0) return EmptyState("", empty, $"Nothing on SoundCloud matches “{query}”.");
                var grid = CardGrid(first.Items.Select(card));
                string? next = first.Next;
                string myTab = tab;
                page.NearEnd = async () =>
                {
                    if (next == null || tab != myTab) return;
                    var more = await load(next);
                    next = more.Items.Count > 0 ? more.Next : null;
                    foreach (var c in more.Items.Select(card)) { c.Margin = new Thickness(0, 0, Gap, Gap + 4); grid.Children.Add(c); Motion.Enter(c, 0, 12); }
                };
                return grid;
            });
        }

        ShowTop();
        return page.Scroll;
    }

    FrameworkElement TopResult(Track t, IList<Track> context)
    {
        var card = new Border { Style = S("GlassRaised"), Padding = new Thickness(20), Cursor = Cursors.Hand };
        var sp = new StackPanel();
        sp.Children.Add(Txt("Top result", 19, FontWeights.SemiBold));
        var cover = Cover(t.Art, 120, Radius("CardRadius"));
        cover.Margin = new Thickness(0, 14, 0, 14);
        cover.HorizontalAlignment = HorizontalAlignment.Left;
        sp.Children.Add(cover);
        var title = Txt(t.Title, 22, FontWeights.SemiBold);
        sp.Children.Add(title);
        sp.Children.Add(Txt($"{t.Artist}  ·  {t.DurationText}", 13, null, "TextDimBrush"));
        var play = ActionButton("Play", () => PlayContext(context, 0, t.Title), true, "");
        play.HorizontalAlignment = HorizontalAlignment.Left;
        play.Margin = new Thickness(0, 14, 0, 0);
        sp.Children.Add(play);
        card.Child = sp;
        RegisterLive(t, title);
        Motion.Hoverable(card, 1.015, -2);
        card.MouseRightButtonUp += (_, e) => { e.Handled = true; OpenMenu(TrackMenu(t, context, 0, t.Title, null), card); };
        return card;
    }

    FrameworkElement BuildGenre((string Name, string Tag) g)
    {
        var page = NewPage();
        var b = page.Body;
        var info = ScApi.Genres.FirstOrDefault(x => x.Tag == g.Tag);
        b.Children.Add(PageHeader(g.Name, "Real SoundCloud results for this genre", ActionButton("Play popular", () => _ = PlayPopularAsync(g.Tag, g.Name), true, ""), "Genre"));

        var popularSlot = Slot();
        b.Children.Add(SectionTitle("Popular"));
        b.Children.Add(popularSlot);
        LoadInto(popularSlot, "rows", async () =>
        {
            var list = await ScApi.PopularAsync(g.Tag);
            if (list.Count == 0) return EmptyState("", "Nothing here right now", "SoundCloud didn't return songs for this genre. Try again later.");
            var sp = new StackPanel();
            sp.Children.Add(TrackList(page, list.Take(15).ToList(), g.Name));
            return sp;
        }, 8);

        AsyncShelf(b, "New this week", "Uploaded in the last 7 days", () => ScApi.NewReleasesAsync(g.Tag), $"New in {g.Name}");

        var plHead = SectionTitle("Playlists");
        var plSlot = Slot();
        b.Children.Add(plHead);
        b.Children.Add(plSlot);
        LoadInto(plSlot, "shelf", async () =>
        {
            var p = await ScApi.SearchPlaylistsAsync(g.Tag);
            if (p.Items.Count == 0) { plHead.Visibility = Visibility.Collapsed; return new Border(); }
            return Shelf(p.Items.Select(SetCard));
        });
        return page.Scroll;
    }

    async Task PlayPopularAsync(string tag, string name)
    {
        try { var list = await ScApi.PopularAsync(tag); PlayContext(list, 0, $"Popular in {name}"); }
        catch (Exception ex) { Toast(ex.Message, ""); }
    }

    // =====================================================================
    // Library
    // =====================================================================
    FrameworkElement BuildLibrary(string tab)
    {
        var page = NewPage();
        var b = page.Body;
        b.Children.Add(PageHeader("Library", "Everything you've saved, played and collected.", ActionButton("Add music", () => ToggleImport(true), false, "")));

        var chips = new List<UIElement>
        {
            Chip("Favorites", tab == "favorites", () => Navigate("library", "favorites", false), "lib"),
            Chip("Playlists", tab == "playlists", () => Navigate("library", "playlists", false), "lib"),
            Chip("History", tab == "history", () => Navigate("library", "history", false), "lib"),
            Chip("Recently added", tab == "recent", () => Navigate("library", "recent", false), "lib"),
            Chip("Artists", tab == "artists", () => Navigate("library", "artists", false), "lib"),
        };
        if (ScApi.Account.SignedIn)
        {
            chips.Add(Chip("SC Likes", tab == "likes", () => Navigate("library", "likes", false), "lib"));
            chips.Add(Chip("SC Feed", tab == "feed", () => Navigate("library", "feed", false), "lib"));
            chips.Add(Chip("SC Playlists", tab == "scsets", () => Navigate("library", "scsets", false), "lib"));
        }
        var bar = ChipBar(chips.ToArray());
        bar.Margin = new Thickness(0, 0, 0, 16);
        b.Children.Add(bar);
        var content = Slot();
        b.Children.Add(content);

        switch (tab)
        {
            case "favorites":
            {
                var favs = Library.Data.Favorites.Select(f => f.Track).ToList();
                if (favs.Count == 0) { content.Children.Add(EmptyState("", "No favorites yet", "Tap the heart on any song to keep it here.", "Find music", () => Navigate("discover"))); break; }
                content.Children.Add(ListActions(favs, "Favorites"));
                content.Children.Add(TrackList(page, favs, "Favorites", new RowOrigin("favorites", "", false)));
                break;
            }
            case "history":
            {
                var hist = Library.Data.History;
                if (hist.Count == 0) { content.Children.Add(EmptyState("", "Nothing played yet", "Songs you play show up here.")); break; }
                var clear = ActionButton("Clear history", async () =>
                {
                    if (await ShowModal("Clear listening history?", Txt("This removes your history and \"Continue listening\". Favorites and playlists stay.", 13, null, "TextDimBrush", true), "Clear", "Cancel", true))
                    { Library.ClearHistory(); Navigate("library", "history", false); }
                }, false, "");
                clear.HorizontalAlignment = HorizontalAlignment.Left;
                clear.Margin = new Thickness(0, 0, 0, 12);
                content.Children.Add(clear);
                foreach (var day in hist.Take(400).GroupBy(h => h.At.Date))
                {
                    var label = day.Key == DateTime.Today ? "Today" : day.Key == DateTime.Today.AddDays(-1) ? "Yesterday" : day.Key.ToString("dddd, d MMMM");
                    content.Children.Add(SectionTitle(label));
                    var tracks = day.Select(h => h.Track).ToList();
                    for (int i = 0; i < tracks.Count; i++) content.Children.Add(TrackRow(tracks[i], tracks, i, $"History: {label}", null, false));
                }
                break;
            }
            case "recent":
            {
                var list = Library.RecentlyAdded(200);
                if (list.Count == 0) { content.Children.Add(EmptyState("", "Nothing added yet", "Songs you favorite or add to playlists appear here, newest first.")); break; }
                content.Children.Add(ListActions(list, "Recently added"));
                content.Children.Add(TrackList(page, list, "Recently added"));
                break;
            }
            case "playlists":
            {
                var cards = new List<FrameworkElement>();
                var create = CollectionCard("New playlist", "Saved in LiquidCloud", null, () => _ = NewPlaylistAsync(), false, null, "");
                cards.Add(create);
                cards.AddRange(Library.Data.Playlists.Select(PlaylistCard));
                content.Children.Add(CardGrid(cards));
                if (Library.Data.Playlists.Count == 0) content.Children.Add(Txt("Tip: drag any song onto a playlist in the sidebar to add it.", 12, null, "TextFaintBrush"));
                break;
            }
            case "artists":
            {
                var favArtists = Library.Data.FavoriteArtists;
                var played = Library.TopArtists(24).Where(a => !favArtists.Contains(a)).ToList();
                if (favArtists.Count == 0 && played.Count == 0) { content.Children.Add(EmptyState("", "No artists yet", "Artists you favorite or play a lot appear here.")); break; }
                if (favArtists.Count > 0) { content.Children.Add(SectionTitle("Favorite artists")); content.Children.Add(ArtistNameGrid(favArtists)); }
                if (played.Count > 0) { content.Children.Add(SectionTitle("You play a lot")); content.Children.Add(ArtistNameGrid(played)); }
                break;
            }
            case "likes":
            case "feed":
            {
                if (!ScApi.Account.SignedIn) { content.Children.Add(SignInPrompt()); break; }
                bool likes = tab == "likes";
                LoadInto(content, "rows", async () =>
                {
                    var list = likes ? await ScApi.MyLikesAsync() : await ScApi.MyFeedAsync();
                    if (list.Count == 0) return EmptyState(likes ? "" : "", likes ? "No likes yet" : "Your feed is empty", likes ? "Songs you like on SoundCloud show up here." : "Follow artists on SoundCloud to fill your feed.");
                    var sp = new StackPanel();
                    sp.Children.Add(ListActions(list, likes ? "Your SoundCloud likes" : "Your SoundCloud feed"));
                    sp.Children.Add(TrackList(page, list, likes ? "Your SoundCloud likes" : "Your SoundCloud feed"));
                    return sp;
                }, 10);
                break;
            }
            case "scsets":
            {
                if (!ScApi.Account.SignedIn) { content.Children.Add(SignInPrompt()); break; }
                LoadInto(content, "shelf", async () =>
                {
                    var sets = await ScApi.MySetsAsync();
                    scSetsCache = sets;
                    if (sets.Count == 0) return EmptyState("", "No SoundCloud playlists", "Create one from any song's menu: Add to playlist → New SoundCloud playlist.");
                    var sp = new StackPanel();
                    var mine = sets.Where(s => s.Mine).Select(s => s.Set).ToList();
                    var liked = sets.Where(s => !s.Mine).Select(s => s.Set).ToList();
                    if (mine.Count > 0) { sp.Children.Add(SectionTitle("Yours", "Changes you make here save to SoundCloud")); sp.Children.Add(CardGrid(mine.Select(SetCard))); }
                    if (liked.Count > 0) { sp.Children.Add(SectionTitle("Liked playlists", "Read-only")); sp.Children.Add(CardGrid(liked.Select(SetCard))); }
                    return sp;
                });
                break;
            }
        }
        return page.Scroll;
    }

    FrameworkElement SignInPrompt() =>
        EmptyState("", "Sign in to SoundCloud", "See your likes, feed and playlists here, and save changes to your SoundCloud playlists.", "Sign in", () => _ = StartScSignInAsync());

    WrapPanel ArtistNameGrid(IEnumerable<string> names) => CardGrid(names.Select(a =>
    {
        var seed = Library.Data.History.Select(h => h.Track).FirstOrDefault(t => t.Artist == a);
        return CollectionCard(a, Library.IsFavoriteArtist(a) ? "Favorite artist" : "Artist", seed?.Art, () =>
        {
            if (seed?.Source == "SoundCloud") Navigate("artist", seed);
            else { SearchBox.Text = a; RunSearch(a); }
        }, true, null, "");
    }));

    FrameworkElement ListActions(List<Track> tracks, string name)
    {
        var sp = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 12) };
        sp.Children.Add(ActionButton("Play", () => PlayContext(tracks, 0, name), true, ""));
        sp.Children.Add(ActionButton("Shuffle", () => PlayShuffled(tracks, name), false, ""));
        sp.Children.Add(ActionButton("Add to queue", () => AddToQueue(tracks), false, ""));
        var addAll = ActionButton("Add all to playlist", () => { }, false, "");
        addAll.Click += (_, _) => { var m = new ContextMenu(); FillAddToPlaylist(m.Items, tracks); OpenMenu(m, addAll); };
        sp.Children.Add(addAll);
        sp.Children.Add(new TextBlock { Text = $"{tracks.Count} songs" + (TotalTime(tracks) is { Length: > 0 } tt ? " · " + tt : ""), Foreground = Res("TextDimBrush"), FontSize = 12, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(8, 0, 0, 0) });
        return sp;
    }

    // =====================================================================
    // Collection pages (local playlist, SoundCloud set, artist, generic list)
    // =====================================================================
    FrameworkElement CollectionHeader(UIElement art, string eyebrow, string title, string description, string meta, params UIElement[] actions)
    {
        var g = new Grid { Margin = new Thickness(0, 0, 0, 22) };
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        g.ColumnDefinitions.Add(new ColumnDefinition());
        g.Children.Add(art);
        var sp = new StackPanel { Margin = new Thickness(24, 0, 0, 0), VerticalAlignment = VerticalAlignment.Bottom };
        sp.Children.Add(Txt(eyebrow.ToUpperInvariant(), 11, FontWeights.SemiBold, "AccentBrush"));
        var t = Txt(title, 34, FontWeights.SemiBold);
        t.FontFamily = new FontFamily("Segoe UI Variable Display, Segoe UI");
        t.TextWrapping = TextWrapping.Wrap;
        t.MaxHeight = 96;
        sp.Children.Add(t);
        if (description.Length > 0) { var d = Txt(description, 13, null, "TextDimBrush", true); d.MaxHeight = 60; d.Margin = new Thickness(0, 4, 0, 0); sp.Children.Add(d); }
        if (meta.Length > 0) { var m = Txt(meta, 12, null, "TextFaintBrush"); m.Margin = new Thickness(0, 6, 0, 0); sp.Children.Add(m); }
        var row = new WrapPanel { Margin = new Thickness(0, 14, 0, 0) };
        foreach (var a in actions) row.Children.Add(a);
        sp.Children.Add(row);
        Grid.SetColumn(sp, 1);
        g.Children.Add(sp);
        return g;
    }

    /// <summary>2×2 mosaic of the first covers, or a single cover / custom image.</summary>
    FrameworkElement Mosaic(IList<string?> arts, double size, string? custom)
    {
        var r = Radius("CardRadius");
        if (custom != null || arts.Count(a => a != null) < 4)
        {
            var c = Cover(custom ?? arts.FirstOrDefault(a => a != null), size, r, "");
            c.Effect = (System.Windows.Media.Effects.Effect)FindResource("GlassShadow");
            return c;
        }
        var g = new Grid { Width = size, Height = size };
        var clip = new Border { CornerRadius = r, ClipToBounds = true, Effect = (System.Windows.Media.Effects.Effect)FindResource("GlassShadow") };
        var u = new UniformGrid { Rows = 2, Columns = 2 };
        foreach (var a in arts.Where(a => a != null).Take(4))
            u.Children.Add(new Border { Background = Img(a, (int)size) is { } img ? new ImageBrush(img) { Stretch = Stretch.UniformToFill } : Res("AccentSoftBrush") });
        clip.Child = u;
        clip.OpacityMask = new VisualBrush(new Border { Width = size, Height = size, CornerRadius = r, Background = Brushes.Black });
        g.Children.Add(clip);
        return g;
    }

    FrameworkElement BuildLocalPlaylist(string id)
    {
        var page = NewPage();
        var b = page.Body;
        var p = Library.FindPlaylist(id);
        if (p == null) { b.Children.Add(EmptyState("", "Playlist not found", "It may have been deleted.")); return page.Scroll; }
        var tracks = p.Tracks.Select(s => s.Track).ToList();
        var meta = $"{tracks.Count} songs" + (TotalTime(tracks) is { Length: > 0 } tt ? " · " + tt : "") + $" · updated {p.Updated:d MMM yyyy}";
        b.Children.Add(CollectionHeader(Mosaic(tracks.Select(t => t.Art).ToList(), 190, p.CoverPath), "Playlist · saved in LiquidCloud", p.Name, p.Description, meta,
            ActionButton("Play", () => PlayContext(tracks, 0, p.Name), true, ""),
            ActionButton("Shuffle", () => PlayShuffled(tracks, p.Name), false, ""),
            ActionButton("Repeat", () => { queue.Repeat = RepeatMode.All; UpdateModeButtons(); PlayContext(tracks, 0, p.Name); Toast("Playing on repeat", ""); }, false, ""),
            ActionButton("Edit", () => _ = EditPlaylistAsync(p), false, ""),
            ActionButton("Delete", () => _ = DeletePlaylistAsync(p), false, "")));

        if (tracks.Count == 0)
        {
            b.Children.Add(EmptyState("", "This playlist is empty", "Add songs from any song's menu (Add to playlist), or drag songs onto it in the sidebar.", "Find music", () => Navigate("discover")));
            return page.Scroll;
        }
        b.Children.Add(Txt("Drag songs to reorder.", 12, null, "TextFaintBrush"));
        b.Children.Add(new Border { Height = 8 });
        b.Children.Add(TrackList(page, tracks, p.Name, new RowOrigin("local", p.Id, true), (from, to) =>
        {
            Library.MoveInPlaylist(p, from, to);
            Navigate("playlist", p.Id, false);
        }));
        return page.Scroll;
    }

    async Task EditPlaylistAsync(LocalPlaylist p)
    {
        var form = new StackPanel();
        form.Children.Add(Txt("Name", 12, null, "TextDimBrush"));
        var name = Field("Playlist name", p.Name);
        form.Children.Add(name);
        form.Children.Add(Txt("Description", 12, null, "TextDimBrush"));
        var desc = Field("Add a description (optional)", p.Description, true);
        form.Children.Add(desc);
        string? cover = p.CoverPath;
        var coverLabel = Txt(cover != null ? "Custom cover: " + Path.GetFileName(cover) : "Cover: made from the songs", 12, null, "TextDimBrush");
        var coverRow = new WrapPanel { Margin = new Thickness(0, 4, 0, 0) };
        coverRow.Children.Add(ActionButton("Choose cover…", () =>
        {
            var dlg = new Microsoft.Win32.OpenFileDialog { Filter = "Images|*.png;*.jpg;*.jpeg" };
            if (dlg.ShowDialog() != true) return;
            try
            {
                var dir = Path.Combine(AppSettings.Dir, "covers");
                Directory.CreateDirectory(dir);
                var dest = Path.Combine(dir, $"{p.Id}-{DateTime.Now.Ticks}{Path.GetExtension(dlg.FileName).ToLowerInvariant()}");
                File.Copy(dlg.FileName, dest, true);
                cover = dest;
                coverLabel.Text = "Custom cover: " + Path.GetFileName(dlg.FileName);
            }
            catch (Exception ex) { coverLabel.Text = "Couldn't use that image: " + ex.Message; }
        }, false, ""));
        coverRow.Children.Add(ActionButton("Remove cover", () => { cover = null; coverLabel.Text = "Cover: made from the songs"; }));
        form.Children.Add(coverRow);
        coverLabel.Margin = new Thickness(0, 6, 0, 0);
        form.Children.Add(coverLabel);

        if (!await ShowModal("Edit playlist", form, "Save")) return;
        Library.UpdatePlaylist(p, name.Text, desc.Text, cover);
        Toast("Playlist saved in LiquidCloud", "");
        if (currentPage == "playlist") Navigate("playlist", p.Id, false);
    }

    async Task DeletePlaylistAsync(LocalPlaylist p)
    {
        if (!await ShowModal($"Delete \"{p.Name}\"?", Txt("The playlist is removed from LiquidCloud. The songs themselves stay where they are.", 13, null, "TextDimBrush", true), "Delete", "Cancel", true)) return;
        Library.DeletePlaylist(p);
        Toast($"Deleted \"{p.Name}\"", "");
        if (currentPage == "playlist" && Equals(currentArg, p.Id)) Navigate("library", "playlists", false);
    }

    FrameworkElement BuildScSet(ScSet s)
    {
        var page = NewPage();
        var b = page.Body;
        bool mine = ScApi.Account.SignedIn && scSetsCache?.Any(x => x.Mine && x.Set.Id == s.Id) == true;
        var content = Slot();
        var art = Cover(s.Art, 190, Radius("CardRadius"), s.IsAlbum ? "" : "");
        art.Effect = (System.Windows.Media.Effects.Effect)FindResource("GlassShadow");
        var where = mine ? "Yours on SoundCloud · changes save to SoundCloud" : $"{(s.IsAlbum ? "Album" : "Playlist")} on SoundCloud · by {s.Owner}";
        var actions = new List<UIElement>
        {
            ActionButton("Play", () => _ = PlaySetAsync(s, false), true, ""),
            ActionButton("Shuffle", () => _ = PlaySetAsync(s, true), false, ""),
            ActionButton("Save a copy in LiquidCloud", () => _ = CopySetAsync(s), false, ""),
        };
        if (s.Url != null) actions.Add(ActionButton("Open on SoundCloud", () => OpenUrl(s.Url), false, ""));
        b.Children.Add(CollectionHeader(art, s.IsAlbum ? "Album" : "Playlist", s.Title, where, s.Count > 0 ? $"{s.Count} songs" : "", actions.ToArray()));
        b.Children.Add(content);
        LoadInto(content, "rows", async () =>
        {
            var (_, tracks) = await ScApi.SetAsync(s.Id);
            if (tracks.Count == 0) return EmptyState("", "Nothing playable here", "This playlist is empty, or its songs can't be streamed outside SoundCloud.");
            var sp = new StackPanel();
            if (s.Count > tracks.Count) sp.Children.Add(Txt($"{s.Count - tracks.Count} songs can't be played outside SoundCloud (Go+ or region-locked) and are hidden. They stay in the playlist.", 12, null, "TextFaintBrush", true));
            sp.Children.Add(TrackList(page, tracks, s.Title, mine ? new RowOrigin("sc", s.Id, true) : null));
            return sp;
        }, 10);
        return page.Scroll;
    }

    FrameworkElement BuildArtist(object arg)
    {
        var page = NewPage();
        var b = page.Body;
        var headSlot = Slot();
        var body = Slot();
        b.Children.Add(headSlot);
        b.Children.Add(body);
        LoadInto(headSlot, "rows", async () =>
        {
            ScUser? u = arg switch
            {
                ScUser x => x,
                Track t => await ScApi.ArtistOfAsync(t),
                _ => null
            };
            if (u == null) return EmptyState("", "Artist not found", "SoundCloud doesn't have a profile for this artist.");
            var top = await ScApi.UserTopTracksAsync(u.Id);
            var avatar = Cover(u.Avatar, 180, Radius("CardRadius"), "", null, true);
            avatar.Effect = (System.Windows.Media.Effects.Effect)FindResource("GlassShadow");
            var meta = string.Join(" · ", new[] { u.Followers > 0 ? $"{Compact(u.Followers)} followers" : null, u.TrackCount > 0 ? $"{u.TrackCount} tracks" : null, u.City }.Where(x => !string.IsNullOrEmpty(x)));
            Button favBtn = null!;
            favBtn = ActionButton(Library.IsFavoriteArtist(u.Name) ? "Favorite artist ✓" : "Add to favorite artists", () =>
            {
                bool on = Library.ToggleFavoriteArtist(u.Name);
                favBtn.Content = on ? "Favorite artist ✓" : "Add to favorite artists";
                Motion.Pulse(favBtn, 1.06);
                Toast(on ? $"{u.Name} is now a favorite artist" : $"Removed {u.Name} from favorite artists", on ? "" : "");
            }, false);
            var acts = new List<UIElement> { ActionButton("Play", () => PlayContext(top, 0, $"{u.Name}: popular"), true, ""), favBtn };
            if (u.Url != null) acts.Add(ActionButton("Open on SoundCloud", () => OpenUrl(u.Url), false, ""));
            LoadArtistBody(page, body, u, top);
            return CollectionHeader(avatar, "Artist", u.Name, "", meta, acts.ToArray());
        }, 2);
        return page.Scroll;
    }

    void LoadArtistBody(PageCtx page, StackPanel body, ScUser u, List<Track> top)
    {
        body.Children.Clear();
        if (top.Count > 0)
        {
            body.Children.Add(SectionTitle("Popular"));
            for (int i = 0; i < Math.Min(10, top.Count); i++) body.Children.Add(TrackRow(top[i], top, i, $"{u.Name}: popular", null));
        }
        var setsHead = SectionTitle("Albums & playlists");
        var setsSlot = Slot();
        body.Children.Add(setsHead);
        body.Children.Add(setsSlot);
        LoadInto(setsSlot, "shelf", async () =>
        {
            var sets = await ScApi.UserSetsAsync(u.Id);
            if (sets.Count == 0) { setsHead.Visibility = Visibility.Collapsed; return new Border(); }
            return Shelf(sets.Select(SetCard));
        });

        body.Children.Add(SectionTitle("All tracks"));
        var tracksSlot = Slot();
        body.Children.Add(tracksSlot);
        LoadInto(tracksSlot, "rows", async () =>
        {
            var first = await ScApi.UserTracksAsync(u.Id);
            var all = new List<Track>(first.Items);
            var list = new StackPanel();
            foreach (var t in all) list.Children.Add(TrackRow(t, all, all.IndexOf(t), u.Name, null));
            string? next = first.Next;
            page.NearEnd = async () =>
            {
                if (next == null) return;
                var more = await ScApi.UserTracksAsync(u.Id, next);
                next = more.Items.Count > 0 ? more.Next : null;
                foreach (var t in more.Items.Where(t => all.All(a => a.Id != t.Id)))
                {
                    all.Add(t);
                    var r = TrackRow(t, all, all.Count - 1, u.Name, null);
                    list.Children.Add(r);
                    Motion.Enter(r, 0, 10, Motion.Base);
                }
            };
            if (all.Count == 0) return EmptyState("", "No tracks", "This artist hasn't uploaded any public tracks.");
            return list;
        }, 6);
    }

    FrameworkElement BuildTrackListPage(TrackListArgs a)
    {
        var page = NewPage();
        var b = page.Body;
        var art = Mosaic(a.Tracks.Select(t => t.Art).ToList(), 170, null);
        b.Children.Add(CollectionHeader(art, "Collection", a.Title, a.Subtitle, $"{a.Tracks.Count} songs" + (TotalTime(a.Tracks) is { Length: > 0 } tt ? " · " + tt : "")));
        if (a.Tracks.Count == 0) { b.Children.Add(EmptyState(a.Glyph, "Nothing here", "No songs to show.")); return page.Scroll; }
        b.Children.Add(ListActions(a.Tracks, a.Title));
        b.Children.Add(TrackList(page, a.Tracks, a.Title));
        return page.Scroll;
    }
}
