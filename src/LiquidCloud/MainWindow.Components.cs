using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;

namespace LiquidCloud;

/// <summary>Where a track row lives, so its menu can offer "Remove from this playlist" and reordering.</summary>
public record RowOrigin(string Kind, string Id, bool Editable);

/// <summary>
/// LiquidCloud's UI building blocks. Every page is assembled from these, so spacing, radii, type sizes,
/// hover/press states and motion are the same everywhere.
/// </summary>
public partial class MainWindow
{
    // spacing scale
    const double PagePad = 30, Gap = 16, CardW = 172;

    Brush Res(string key) => (Brush)FindResource(key);
    CornerRadius Radius(string key) => (CornerRadius)FindResource(key);

    /// <summary>Loads a picture into an ImageBrush through the image cache (async, decoded at display size).</summary>
    static void LoadBrush(ImageBrush brush, string? url, int px)
    {
        brush.ImageSource = null;
        if (string.IsNullOrEmpty(url)) return;
        Bg.Run(SetAsync(), "Images", "Brush");
        async Task SetAsync()
        {
            var img = await ImageCache.Shared.GetAsync(url, px, CancellationToken.None);
            if (img != null) brush.ImageSource = img;
        }
    }

    // text and icons follow the theme live (resource references), so theme previews recolor open pages too
    TextBlock Txt(string text, double size = 14, FontWeight? weight = null, string brush = "TextBrush", bool wrap = false)
    {
        var t = new TextBlock
        {
            Text = text, FontSize = size, FontWeight = weight ?? FontWeights.Normal,
            TextTrimming = TextTrimming.CharacterEllipsis, TextWrapping = wrap ? TextWrapping.Wrap : TextWrapping.NoWrap
        };
        t.SetResourceReference(TextBlock.ForegroundProperty, brush);
        return t;
    }

    TextBlock Glyph(string glyph, double size = 14, string brush = "TextBrush")
    {
        var t = new TextBlock
        {
            Text = glyph, FontFamily = (FontFamily)FindResource("Icons"), FontSize = size,
            VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Center
        };
        t.SetResourceReference(TextBlock.ForegroundProperty, brush);
        return t;
    }

    static T Dyn<T>(T el, DependencyProperty prop, string key) where T : FrameworkElement { el.SetResourceReference(prop, key); return el; }

    Button RoundButton(string glyph, string tip, Action click, double size = 34, double font = 14)
    {
        tip = Loc.T(tip);
        var b = new Button { Style = S("Round"), Content = glyph, Width = size, Height = size, FontSize = font, ToolTip = tip };
        b.Click += (_, e) => { e.Handled = true; click(); };
        return b;
    }

    Button ActionButton(string text, Action click, bool accent = false, string? glyph = null, bool translate = true)
    {
        if (translate) text = Loc.T(text);
        object content = text;
        if (glyph != null)
        {
            var sp = new StackPanel { Orientation = Orientation.Horizontal };
            sp.Children.Add(new TextBlock { Text = glyph, FontFamily = (FontFamily)FindResource("Icons"), FontSize = 13, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 8, 0) });
            sp.Children.Add(new TextBlock { Text = text, VerticalAlignment = VerticalAlignment.Center });
            content = sp;
        }
        var b = new Button { Style = S(accent ? "AccentAction" : "Action"), Content = content, Margin = new Thickness(0, 0, 8, 0) };
        b.Click += (_, _) => click();
        Motion.Pressable(b);
        return b;
    }

    RadioButton Chip(string text, bool active, Action click, string group)
    {
        text = Loc.T(text);
        var r = new RadioButton { Style = S("Pill"), Content = text, GroupName = group, IsChecked = active, Margin = new Thickness(0, 0, 4, 0) };
        r.Click += (_, _) => click();
        return r;
    }

    Border ChipBar(params UIElement[] chips)
    {
        var sp = new StackPanel { Orientation = Orientation.Horizontal };
        foreach (var c in chips) sp.Children.Add(c);
        return Dyn(new Border { CornerRadius = Radius("ControlRadius"), Padding = new Thickness(3), Child = sp, HorizontalAlignment = HorizontalAlignment.Left }, Border.BackgroundProperty, "FieldBrush");
    }

    // ---- page scaffold ----
    sealed class PageCtx
    {
        public ScrollViewer Scroll = null!;
        public StackPanel Body = null!;
        public Func<Task>? NearEnd;
        public bool LoadingMore;
    }

    PageCtx NewPage()
    {
        var body = new StackPanel { Margin = new Thickness(PagePad, 24, PagePad, 40) };
        var sv = new ScrollViewer { VerticalScrollBarVisibility = ScrollBarVisibility.Hidden, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled, Content = body, Focusable = false };
        var ctx = new PageCtx { Scroll = sv, Body = body };
        SmoothScroll(sv, false);
        sv.ScrollChanged += async (_, _) =>
        {
            if (ctx.NearEnd == null || ctx.LoadingMore || sv.ScrollableHeight - sv.VerticalOffset > 700) return;
            ctx.LoadingMore = true;
            try { await ctx.NearEnd(); }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                if (ex is not OfflineException) Log.Warn("Pages", "Loading more failed", ex);
                Toast(FriendlyError(ex), "\uE7BA");
            }
            ctx.LoadingMore = false;
        };
        return ctx;
    }

    FrameworkElement PageHeader(string title, string? subtitle = null, UIElement? right = null, string? eyebrow = null)
    {
        title = Loc.T(title);
        if (subtitle != null) subtitle = Loc.T(subtitle);
        if (eyebrow != null) eyebrow = Loc.T(eyebrow);
        var g = new Grid { Margin = new Thickness(0, 0, 0, 18) };
        var sp = new StackPanel();
        if (eyebrow != null) sp.Children.Add(Txt(eyebrow.ToUpperInvariant(), 11, FontWeights.SemiBold, "AccentBrush"));
        var t = Txt(title, 32, FontWeights.SemiBold);
        t.FontFamily = new FontFamily("Segoe UI Variable Display, Segoe UI");
        sp.Children.Add(t);
        if (!string.IsNullOrEmpty(subtitle)) { var st = Txt(subtitle!, 14, null, "TextDimBrush", true); st.Margin = new Thickness(0, 4, 0, 0); sp.Children.Add(st); }
        g.Children.Add(sp);
        if (right != null)
        {
            if (right is FrameworkElement fe) { fe.HorizontalAlignment = HorizontalAlignment.Right; fe.VerticalAlignment = VerticalAlignment.Bottom; }
            g.Children.Add(right);
            sp.Margin = new Thickness(0, 0, 260, 0);
        }
        return g;
    }

    FrameworkElement SectionTitle(string title, string? subtitle = null, Action? seeAll = null)
    {
        title = Loc.T(title);
        if (subtitle != null) subtitle = Loc.T(subtitle);
        var g = new Grid { Margin = new Thickness(0, 26, 0, 12) };
        var sp = new StackPanel();
        sp.Children.Add(Txt(title, 19, FontWeights.SemiBold));
        if (subtitle != null) sp.Children.Add(Txt(subtitle, 12, null, "TextDimBrush"));
        g.Children.Add(sp);
        if (seeAll != null)
        {
            var b = new Button { Style = S("Action"), Content = Loc.T("See all"), HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Center };
            b.Click += (_, _) => seeAll();
            g.Children.Add(b);
        }
        return g;
    }

    // ---- shelves (horizontal rows of cards) ----
    FrameworkElement Shelf(IEnumerable<FrameworkElement> cards)
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal };
        int i = 0;
        foreach (var c in cards)
        {
            c.Margin = new Thickness(0, 0, Gap, 0);
            row.Children.Add(c);
            Motion.Enter(c, Math.Min(i++, 10) * 40, 16);
        }
        var sv = new ScrollViewer
        {
            HorizontalScrollBarVisibility = ScrollBarVisibility.Hidden, VerticalScrollBarVisibility = ScrollBarVisibility.Disabled,
            Content = row, Focusable = false, Padding = new Thickness(0, 6, 0, 10), Margin = new Thickness(-6, -6, -6, 0)
        };
        row.Margin = new Thickness(6, 0, 6, 0);
        SmoothScroll(sv, true);

        // arrows appear on hover
        var g = new Grid();
        g.Children.Add(sv);
        var left = RoundButton("", "Scroll left", () => ScrollShelf(sv, -1), 36, 12);
        var right = RoundButton("", "Scroll right", () => ScrollShelf(sv, 1), 36, 12);
        foreach (var b in new[] { left, right })
        {
            b.Background = Res("MenuBrush");
            b.VerticalAlignment = VerticalAlignment.Center;
            b.Opacity = 0;
            b.Margin = new Thickness(0, 0, 0, 50);
            g.Children.Add(b);
        }
        left.HorizontalAlignment = HorizontalAlignment.Left;
        right.HorizontalAlignment = HorizontalAlignment.Right;
        g.MouseEnter += (_, _) => { Motion.To(left, OpacityProperty, sv.HorizontalOffset > 1 ? 1 : 0, Motion.Fast); Motion.To(right, OpacityProperty, sv.ScrollableWidth - sv.HorizontalOffset > 1 ? 1 : 0, Motion.Fast); };
        g.MouseLeave += (_, _) => { Motion.To(left, OpacityProperty, 0, Motion.Base); Motion.To(right, OpacityProperty, 0, Motion.Base); };
        sv.ScrollChanged += (_, _) =>
        {
            if (!g.IsMouseOver) return;
            Motion.To(left, OpacityProperty, sv.HorizontalOffset > 1 ? 1 : 0, Motion.Fast);
            Motion.To(right, OpacityProperty, sv.ScrollableWidth - sv.HorizontalOffset > 1 ? 1 : 0, Motion.Fast);
        };
        return g;
    }

    void ScrollShelf(ScrollViewer sv, int dir)
    {
        if (scrollers.TryGetValue(sv, out var s)) s.ScrollBy(dir * Math.Max(300, sv.ViewportWidth * .8));
    }

    WrapPanel CardGrid(IEnumerable<FrameworkElement> cards)
    {
        var wp = new WrapPanel { Margin = new Thickness(-2, 0, 0, 0) };
        int i = 0;
        foreach (var c in cards)
        {
            c.Margin = new Thickness(0, 0, Gap, Gap + 4);
            wp.Children.Add(c);
            Motion.Enter(c, Math.Min(i++, 14) * 30, 14);
        }
        return wp;
    }

    // ---- cards ----
    Border Cover(string? art, double size, CornerRadius radius, string glyph = "", Brush? fallback = null, bool round = false)
    {
        var holder = new Border
        {
            Width = size, Height = size, CornerRadius = round ? new CornerRadius(size / 2) : radius,
            Background = fallback ?? Res("AccentSoftBrush"), ClipToBounds = true
        };
        var g = new Grid();
        g.Children.Add(Glyph(glyph, size * .28, "TextDimBrush"));
        if (!string.IsNullOrEmpty(art))
        {
            var pic = new Border { CornerRadius = holder.CornerRadius };
            ImageLoader.SetSize(pic, (int)(size * 1.6));
            ImageLoader.SetUrl(pic, art);
            g.Children.Add(pic);
        }
        // soft glass sheen over every cover keeps them part of the same visual system
        g.Children.Add(new Border { CornerRadius = holder.CornerRadius, Background = Res("GlassSheen"), BorderBrush = new SolidColorBrush(Color.FromArgb(0x22, 255, 255, 255)), BorderThickness = new Thickness(1) });
        holder.Child = g;
        return holder;
    }

    FrameworkElement TrackCard(Track t, IList<Track> context, int index, string contextName)
    {
        var card = new StackPanel { Width = CardW, Cursor = Cursors.Hand, Background = Brushes.Transparent };
        var coverGrid = new Grid();
        var cover = Cover(t.Art, CardW, Radius("CardRadius"));
        coverGrid.Children.Add(cover);

        // play button rises in on hover
        var play = new Border
        {
            Width = 44, Height = 44, CornerRadius = new CornerRadius(22), Background = Res("AccentBrush"),
            HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Bottom, Margin = new Thickness(0, 0, 10, 10),
            Opacity = 0, Child = Glyph("", 16, "OnAccentBrush")
        };
        var (_, playMove) = Motion.Transforms(play);
        playMove.Y = 8;
        coverGrid.Children.Add(play);
        card.Children.Add(coverGrid);

        var title = Txt(t.Title, 14, FontWeights.SemiBold);
        title.Margin = new Thickness(2, 10, 2, 0);
        card.Children.Add(title);
        var artist = Txt(t.Artist, 12, null, "TextDimBrush");
        artist.Margin = new Thickness(2, 2, 2, 0);
        card.Children.Add(artist);
        RegisterLive(t, title);

        card.MouseEnter += (_, _) => { Motion.To(play, OpacityProperty, 1, Motion.Fast); Motion.To(playMove, TranslateTransform.YProperty, 0, Motion.Base, Motion.Emphasized); };
        card.MouseLeave += (_, _) => { Motion.To(play, OpacityProperty, 0, Motion.Base); Motion.To(playMove, TranslateTransform.YProperty, 8, Motion.Base); };
        Motion.Hoverable(card, 1.025, -4);
        card.MouseLeftButtonUp += (_, _) => PlayContext(context, index, contextName);
        card.MouseRightButtonUp += (_, e) => { e.Handled = true; OpenMenu(TrackMenu(t, context, index, contextName, null), card); };
        MakeDraggable(card, t);
        card.ToolTip = $"{t.Title}\n{t.Artist}";
        return card;
    }

    FrameworkElement CollectionCard(string title, string subtitle, string? art, Action open, bool round = false, Brush? fallback = null,
                                    string glyph = "", Func<ContextMenu?>? menu = null, Action? play = null)
    {
        var card = new StackPanel { Width = CardW, Cursor = Cursors.Hand, Background = Brushes.Transparent };
        var coverGrid = new Grid();
        var cover = Cover(art, CardW, Radius("CardRadius"), glyph, fallback, round);
        coverGrid.Children.Add(cover);
        if (play != null)
        {
            var pb = new Border
            {
                Width = 44, Height = 44, CornerRadius = new CornerRadius(22), Background = Res("AccentBrush"),
                HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Bottom, Margin = new Thickness(0, 0, 10, 10),
                Opacity = 0, Child = Glyph("", 16, "OnAccentBrush"), Cursor = Cursors.Hand, ToolTip = Loc.T("Play")
            };
            pb.MouseLeftButtonUp += (_, e) => { e.Handled = true; play(); };
            coverGrid.Children.Add(pb);
            card.MouseEnter += (_, _) => Motion.To(pb, OpacityProperty, 1, Motion.Fast);
            card.MouseLeave += (_, _) => Motion.To(pb, OpacityProperty, 0, Motion.Base);
        }
        card.Children.Add(coverGrid);
        var t = Txt(title, 14, FontWeights.SemiBold);
        t.Margin = new Thickness(2, 10, 2, 0);
        if (round) t.TextAlignment = TextAlignment.Center;
        card.Children.Add(t);
        var st = Txt(subtitle, 12, null, "TextDimBrush");
        st.Margin = new Thickness(2, 2, 2, 0);
        if (round) st.TextAlignment = TextAlignment.Center;
        card.Children.Add(st);
        Motion.Hoverable(card, 1.025, -4);
        card.MouseLeftButtonUp += (_, _) => open();
        if (menu != null) card.MouseRightButtonUp += (_, e) => { e.Handled = true; if (menu() is { } m) OpenMenu(m, card); };
        return card;
    }

    /// <summary>Big colorful tile for genres and categories.</summary>
    FrameworkElement GenreTile(string name, string c1, string c2, Action open)
    {
        name = Loc.T(name);
        var a = ThemeDef.C(c1, Colors.SteelBlue);
        var b = ThemeDef.C(c2, Colors.Navy);
        var bg = new LinearGradientBrush(a, b, 45);
        bg.Freeze();
        var tile = new Border
        {
            Width = 200, Height = 104, CornerRadius = Radius("CardRadius"), Background = bg, Cursor = Cursors.Hand, ClipToBounds = true
        };
        var g = new Grid();
        g.Children.Add(new Border { Background = Res("GlassSheen"), CornerRadius = tile.CornerRadius });
        var t = new TextBlock { Text = name, FontSize = 17, FontWeight = FontWeights.SemiBold, Foreground = Brushes.White, Margin = new Thickness(16, 14, 16, 14), TextWrapping = TextWrapping.Wrap, VerticalAlignment = VerticalAlignment.Bottom };
        g.Children.Add(t);
        var ring = new System.Windows.Shapes.Ellipse { Width = 90, Height = 90, Fill = new SolidColorBrush(Color.FromArgb(0x30, 255, 255, 255)), HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(0, -30, -24, 0) };
        g.Children.Add(ring);
        tile.Child = g;
        Motion.Hoverable(tile, 1.04, -3);
        tile.MouseLeftButtonUp += (_, _) => open();
        return tile;
    }

    // ---- track rows ----
    List<(Track Track, TextBlock Title)> liveRows = new();

    void RegisterLive(Track t, TextBlock title)
    {
        liveRows.Add((t, title));
        if (nowPlaying != null && Library.Key(nowPlaying) == Library.Key(t)) title.Foreground = Res("AccentBrush");
    }

    /// <summary>Marks the playing song in whatever page is open.</summary>
    void HighlightPlaying()
    {
        var key = nowPlaying != null ? Library.Key(nowPlaying) : null;
        var accent = Res("AccentBrush");
        var text = Res("TextBrush");
        foreach (var (t, title) in liveRows) title.Foreground = key != null && Library.Key(t) == key ? accent : text;
    }

    FrameworkElement TrackRow(Track t, IList<Track> context, int index, string contextName, RowOrigin? origin, bool showIndex = true)
    {
        var row = new Grid { Height = 58, Cursor = Cursors.Hand, Background = Brushes.Transparent };
        var hover = Dyn(new Border { CornerRadius = Radius("ControlRadius"), Opacity = 0 }, Border.BackgroundProperty, "HoverBrush");
        row.Children.Add(hover);

        var g = new Grid { Margin = new Thickness(8, 0, 6, 0) };
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(showIndex ? 34 : 0) });
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(44) });
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star), MaxWidth = 220 });
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(56) });
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var num = Txt((index + 1).ToString(), 13, null, "TextFaintBrush");
        num.VerticalAlignment = VerticalAlignment.Center;
        num.HorizontalAlignment = HorizontalAlignment.Center;
        var playIcon = Glyph("", 13);
        playIcon.Opacity = 0;
        var idx = new Grid();
        idx.Children.Add(num);
        idx.Children.Add(playIcon);
        g.Children.Add(idx);

        var cover = Cover(t.Art, 44, new CornerRadius(10));
        Grid.SetColumn(cover, 1);
        g.Children.Add(cover);

        var info = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(12, 0, 12, 0) };
        var title = Txt(t.Title, 14, FontWeights.SemiBold);
        info.Children.Add(title);
        var artist = Txt(t.Artist, 12, null, "TextDimBrush");
        info.Children.Add(artist);
        Grid.SetColumn(info, 2);
        g.Children.Add(info);
        RegisterLive(t, title);

        var src = Txt(t.Source == "Local" ? Loc.T("This PC") : t.Source, 12, null, "TextFaintBrush");
        src.VerticalAlignment = VerticalAlignment.Center;
        Grid.SetColumn(src, 3);
        g.Children.Add(src);

        var dur = Txt(t.DurationText, 12, null, "TextDimBrush");
        dur.VerticalAlignment = VerticalAlignment.Center;
        dur.HorizontalAlignment = HorizontalAlignment.Right;
        dur.Margin = new Thickness(0, 0, 10, 0);
        Grid.SetColumn(dur, 4);
        g.Children.Add(dur);

        var actions = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        bool fav = Library.IsFavorite(t);
        Button heart = null!;
        heart = RoundButton(fav ? "" : "", fav ? "Remove from favorites" : "Add to favorites", () =>
        {
            ToggleFavorite(t, heart);
            bool now = Library.IsFavorite(t);
            heart.Content = now ? "" : "";
            heart.Foreground = now ? Res("AccentBrush") : Res("TextBrush");
            heart.Opacity = 1;
        }, 32, 13);
        if (fav) heart.Foreground = Res("AccentBrush");
        heart.Opacity = fav ? 1 : 0;
        var more = RoundButton("", "More", () => { }, 32, 13);
        more.Opacity = 0;
        more.Click += (_, _) => OpenMenu(TrackMenu(t, context, index, contextName, origin), more);
        actions.Children.Add(heart);
        actions.Children.Add(more);
        Grid.SetColumn(actions, 5);
        g.Children.Add(actions);

        row.Children.Add(g);

        row.MouseEnter += (_, _) =>
        {
            Motion.To(hover, OpacityProperty, 1, Motion.Fast);
            Motion.To(num, OpacityProperty, 0, Motion.Fast); Motion.To(playIcon, OpacityProperty, 1, Motion.Fast);
            Motion.To(more, OpacityProperty, 1, Motion.Fast); Motion.To(heart, OpacityProperty, 1, Motion.Fast);
        };
        row.MouseLeave += (_, _) =>
        {
            Motion.To(hover, OpacityProperty, 0, Motion.Base);
            Motion.To(num, OpacityProperty, 1, Motion.Base); Motion.To(playIcon, OpacityProperty, 0, Motion.Base);
            Motion.To(more, OpacityProperty, 0, Motion.Base); Motion.To(heart, OpacityProperty, Library.IsFavorite(t) ? 1 : 0, Motion.Base);
        };
        row.MouseLeftButtonUp += (_, e) => { if (e.OriginalSource is DependencyObject d && FindParent<Button>(d) != null) return; PlayContext(context, index, contextName); };
        row.MouseRightButtonUp += (_, e) => { e.Handled = true; OpenMenu(TrackMenu(t, context, index, contextName, origin), row); };
        MakeDraggable(row, t, origin is { Editable: true } ? index : -1);
        return row;
    }

    static T? FindParent<T>(DependencyObject d) where T : DependencyObject
    {
        while (d != null)
        {
            if (d is T t) return t;
            d = VisualTreeHelper.GetParent(d);
        }
        return null;
    }

    /// <summary>
    /// A track list that renders in chunks as you scroll (long likes lists stay light).
    /// When onMove is given, rows can be dragged to reorder.
    /// </summary>
    StackPanel TrackList(PageCtx page, IList<Track> tracks, string contextName, RowOrigin? origin = null, Action<int, int>? onMove = null)
    {
        var list = new StackPanel();
        int rendered = 0;
        void RenderMore()
        {
            int end = Math.Min(tracks.Count, rendered + 60);
            for (int i = rendered; i < end; i++)
            {
                var r = TrackRow(tracks[i], tracks, i, contextName, origin);
                if (onMove != null) MakeReorderTarget(r, i, onMove);
                list.Children.Add(r);
                if (i - rendered < 16) Motion.Enter(r, (i - rendered) * 22, 10, Motion.Base + 60);
            }
            rendered = end;
        }
        RenderMore();
        var previous = page.NearEnd;
        page.NearEnd = async () =>
        {
            if (rendered < tracks.Count) { RenderMore(); return; }
            if (previous != null) await previous();
        };
        return list;
    }

    // ---- drag & drop: drag songs onto sidebar playlists, or reorder within an editable list ----
    Point dragStart;
    const string TrackFormat = "LiquidCloud.Track";
    const string IndexFormat = "LiquidCloud.Index";

    void MakeDraggable(FrameworkElement el, Track t, int index = -1)
    {
        el.PreviewMouseLeftButtonDown += (_, e) => dragStart = e.GetPosition(this);
        el.PreviewMouseMove += (_, e) =>
        {
            if (e.LeftButton != MouseButtonState.Pressed) return;
            var p = e.GetPosition(this);
            if (Math.Abs(p.X - dragStart.X) < 8 && Math.Abs(p.Y - dragStart.Y) < 8) return;
            var data = new DataObject();
            data.SetData(TrackFormat, t);
            if (index >= 0) data.SetData(IndexFormat, index);
            Motion.To(el, OpacityProperty, .5, Motion.Fast);
            DragDrop.DoDragDrop(el, data, DragDropEffects.Copy | DragDropEffects.Move);
            Motion.To(el, OpacityProperty, 1, Motion.Base);
        };
    }

    void MakeReorderTarget(FrameworkElement row, int index, Action<int, int> onMove)
    {
        row.AllowDrop = true;
        var line = new Border { Height = 2, Background = Res("AccentBrush"), VerticalAlignment = VerticalAlignment.Top, Opacity = 0, IsHitTestVisible = false };
        if (row is Grid g) g.Children.Add(line);
        row.DragOver += (_, e) =>
        {
            if (!e.Data.GetDataPresent(IndexFormat)) return;
            e.Effects = DragDropEffects.Move;
            line.Opacity = 1;
            e.Handled = true;
        };
        row.DragLeave += (_, _) => line.Opacity = 0;
        row.Drop += (_, e) =>
        {
            line.Opacity = 0;
            if (e.Data.GetData(IndexFormat) is int from && from != index) { e.Handled = true; onMove(from, index); }
        };
    }

    // ---- states ----
    FrameworkElement Skeleton(string kind, int count = 6)
    {
        var sp = new StackPanel();
        var brush = Res("SkeletonBrush");
        if (kind == "shelf")
        {
            var row = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 6, 0, 0) };
            for (int i = 0; i < count; i++)
            {
                var c = new StackPanel { Width = CardW, Margin = new Thickness(0, 0, Gap, 0) };
                c.Children.Add(new Border { Height = CardW, CornerRadius = Radius("CardRadius"), Background = brush });
                c.Children.Add(new Border { Height = 12, Width = 120, CornerRadius = new CornerRadius(6), Background = brush, Margin = new Thickness(2, 12, 0, 0), HorizontalAlignment = HorizontalAlignment.Left });
                c.Children.Add(new Border { Height = 10, Width = 80, CornerRadius = new CornerRadius(5), Background = brush, Margin = new Thickness(2, 8, 0, 0), HorizontalAlignment = HorizontalAlignment.Left });
                row.Children.Add(c);
            }
            sp.Children.Add(row);
        }
        else
        {
            for (int i = 0; i < count; i++)
            {
                var g = new Grid { Height = 58 };
                g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(56) });
                g.ColumnDefinitions.Add(new ColumnDefinition());
                g.Children.Add(new Border { Width = 44, Height = 44, CornerRadius = new CornerRadius(10), Background = brush, Margin = new Thickness(8, 0, 0, 0) });
                var lines = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(12, 0, 0, 0) };
                lines.Children.Add(new Border { Height = 12, Width = 180 + (i * 37 % 90), CornerRadius = new CornerRadius(6), Background = brush, HorizontalAlignment = HorizontalAlignment.Left });
                lines.Children.Add(new Border { Height = 10, Width = 110 + (i * 23 % 60), CornerRadius = new CornerRadius(5), Background = brush, Margin = new Thickness(0, 8, 0, 0), HorizontalAlignment = HorizontalAlignment.Left });
                Grid.SetColumn(lines, 1);
                g.Children.Add(lines);
                sp.Children.Add(g);
            }
        }
        return sp;
    }

    /// <summary>Skeletons gently pulse while content loads (the pulse stops when the skeleton is replaced).</summary>
    static void Shimmer(UIElement el)
    {
        if (Motion.Reduced) return;
        var a = new DoubleAnimation(.45, 1, TimeSpan.FromSeconds(.9)) { AutoReverse = true, RepeatBehavior = RepeatBehavior.Forever, EasingFunction = Motion.InOut };
        Timeline.SetDesiredFrameRate(a, 24);
        el.BeginAnimation(UIElement.OpacityProperty, a);
    }

    FrameworkElement EmptyState(string glyph, string title, string text, string? actionText = null, Action? action = null)
    {
        title = Loc.T(title);
        text = Loc.T(text);
        var sp = new StackPanel { HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 40, 0, 40), MaxWidth = 420 };
        var circle = new Border { Width = 72, Height = 72, CornerRadius = new CornerRadius(36), Background = Res("AccentSoftBrush"), Child = Glyph(glyph, 28, "AccentBrush") };
        sp.Children.Add(circle);
        var t = Txt(title, 18, FontWeights.SemiBold);
        t.HorizontalAlignment = HorizontalAlignment.Center; t.Margin = new Thickness(0, 16, 0, 4);
        sp.Children.Add(t);
        var d = Txt(text, 13, null, "TextDimBrush", true);
        d.TextAlignment = TextAlignment.Center;
        sp.Children.Add(d);
        if (actionText != null && action != null)
        {
            var b = ActionButton(actionText, action, true);
            b.HorizontalAlignment = HorizontalAlignment.Center;
            b.Margin = new Thickness(0, 18, 0, 0);
            sp.Children.Add(b);
        }
        Motion.Enter(sp, 0, 12);
        return sp;
    }

    FrameworkElement ErrorState(string message, Action retry) =>
        EmptyState("", "Something went wrong", message, "Try again", retry);

    /// <summary>Shows a skeleton, runs the loader, then swaps in the result (or a friendly error with Retry).</summary>
    void LoadInto(Panel slot, string skeleton, Func<Task<FrameworkElement>> build, int skeletonCount = 6)
    {
        slot.Children.Clear();
        var sk = Skeleton(skeleton, skeletonCount);
        Shimmer(sk);
        slot.Children.Add(sk);
        var token = pageCts?.Token ?? CancellationToken.None;
        Bg.Run(Run(), "Pages", "Load section");

        async Task Run()
        {
            // every SoundCloud request made while building this section is cancelled if the page is closed
            ScApi.Ambient.Value = token;
            try
            {
                var el = await build();
                if (token.IsCancellationRequested) return;
                slot.Children.Clear();
                slot.Children.Add(el);
                Motion.FromTo(el, OpacityProperty, 0, 1, Motion.Base);
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { }
            catch (Exception ex)
            {
                if (token.IsCancellationRequested) return;
                if (ex is not OfflineException) Log.Warn("Pages", "Section failed to load", ex);
                slot.Children.Clear();
                slot.Children.Add(ex is OfflineException
                    ? EmptyState("\uF384", Loc.T("No connection"), ex.Message, Loc.T("Try again"), () => LoadInto(slot, skeleton, build, skeletonCount))
                    : ErrorState(FriendlyError(ex), () => LoadInto(slot, skeleton, build, skeletonCount)));
            }
        }
    }

    /// <summary>A message people can act on, instead of raw exception text.</summary>
    static string FriendlyError(Exception ex) => ex switch
    {
        OfflineException o => o.Message,
        TimeoutException or TaskCanceledException => Loc.T("SoundCloud is taking too long to answer. Try again in a moment."),
        System.Net.Http.HttpRequestException => Loc.T("Couldn't reach SoundCloud. Check your connection and try again."),
        _ => ex.Message
    };

    // ---- smooth scrolling for every scroll area ----
    sealed class Scroller
    {
        readonly ScrollViewer sv;
        readonly bool horizontal;
        double target;
        bool running;
        public Scroller(ScrollViewer sv, bool horizontal) { this.sv = sv; this.horizontal = horizontal; }

        double Offset => horizontal ? sv.HorizontalOffset : sv.VerticalOffset;
        double Max => horizontal ? sv.ScrollableWidth : sv.ScrollableHeight;
        void Set(double v) { if (horizontal) sv.ScrollToHorizontalOffset(v); else sv.ScrollToVerticalOffset(v); }

        public void ScrollBy(double delta)
        {
            if (!running) target = Offset;
            target = Math.Clamp(target + delta, 0, Max);
            if (Motion.Reduced) { Set(target); return; }
            if (!running) { running = true; CompositionTarget.Rendering += Step; }
        }

        void Step(object? s, EventArgs e)
        {
            double diff = target - Offset;
            if (Math.Abs(diff) < 0.5) { Set(target); CompositionTarget.Rendering -= Step; running = false; return; }
            Set(Offset + diff * 0.18);
        }
    }

    readonly System.Runtime.CompilerServices.ConditionalWeakTable<ScrollViewer, Scroller> scrollers = new();

    void SmoothScroll(ScrollViewer sv, bool horizontal)
    {
        var s = new Scroller(sv, horizontal);
        scrollers.AddOrUpdate(sv, s);
        sv.PreviewMouseWheel += (_, e) =>
        {
            bool shift = Keyboard.Modifiers.HasFlag(ModifierKeys.Shift);
            if (horizontal != shift) return; // vertical scrollers ignore Shift+wheel; shelves only take Shift+wheel
            e.Handled = true;
            s.ScrollBy(-e.Delta * 0.9);
        };
    }

    // the queue list in the player panel
    ScrollViewer? listScroll;
    void List_PreviewMouseWheel(object s, MouseWheelEventArgs e)
    {
        listScroll ??= FindChild<ScrollViewer>(List);
        if (listScroll == null) return;
        if (!scrollers.TryGetValue(listScroll, out var sr)) { sr = new Scroller(listScroll, false); scrollers.AddOrUpdate(listScroll, sr); }
        e.Handled = true;
        sr.ScrollBy(-e.Delta * 0.9);
    }

    static T? FindChild<T>(DependencyObject d) where T : DependencyObject
    {
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(d); i++)
        {
            var c = VisualTreeHelper.GetChild(d, i);
            if (c is T t) return t;
            var r = FindChild<T>(c);
            if (r != null) return r;
        }
        return null;
    }
}
