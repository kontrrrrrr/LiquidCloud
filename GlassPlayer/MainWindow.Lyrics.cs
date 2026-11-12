using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;

namespace GlassPlayer;

/// <summary>The player panel: cover layout, the Playing / Lyrics / Up next tabs (cover glides into the corner), and lyrics.</summary>
public partial class MainWindow
{
    List<LyricLine> lyrics = new();
    bool lyrSynced, expanded, artAnimating;
    string playerTab = "now";
    int lyrIndex = -1;
    double artSize = 280;
    const double MiniArtSize = 72;

    void InitLyricsLayout()
    {
        MainArea.SizeChanged += (_, _) =>
        {
            double max = fullPlayer ? 520 : 320;
            artSize = Math.Clamp(Math.Min(MainArea.ActualHeight - 210, MainArea.ActualWidth - 20), 120, max);
            ArtSpacer.Width = ArtSpacer.Height = artSize;
            Bars.Width = Math.Min(artSize, 300);
            QueuePositionArt();
        };
        NormalView.SizeChanged += (_, _) => QueuePositionArt();
        Loaded += (_, _) => QueuePositionArt();
        ShowNoLyrics("");
    }

    void QueuePositionArt() => Dispatcher.InvokeAsync(PositionArt, DispatcherPriority.Loaded);

    void PositionArt()
    {
        if (artAnimating) return;
        if (expanded) { PlaceArt(0, 0, MiniArtSize); return; }
        if (!IsLoaded) return;
        var p = ArtSpacer.TranslatePoint(new Point(0, 0), ArtLayer);
        PlaceArt(p.X, p.Y, artSize);
    }

    void PlaceArt(double x, double y, double size)
    {
        Canvas.SetLeft(ArtBox, x);
        Canvas.SetTop(ArtBox, y);
        ArtBox.Width = ArtBox.Height = size;
        ArtBox.CornerRadius = new CornerRadius(size > 120 ? 28 : 16);
    }

    void PlayerTab_Click(object s, RoutedEventArgs e)
    {
        if (s is RadioButton { CommandParameter: string tab }) SetPlayerTab(tab);
    }

    void CollapsePlayer_Click(object s, RoutedEventArgs e) => SetPlayerTab("now");
    void LyricPreview_Click(object s, MouseButtonEventArgs e) => SetPlayerTab("lyrics");

    /// <summary>"now" shows the big cover; "lyrics" and "queue" move the cover into the corner and fill the panel.</summary>
    void SetPlayerTab(string tab)
    {
        bool wasExpanded = expanded;
        playerTab = tab;
        (tab switch { "lyrics" => TabLyrics, "queue" => TabQueue, _ => TabNow }).IsChecked = true;

        if (tab != "now")
        {
            bool showQueue = tab == "queue";
            if (wasExpanded)
            {
                // switching between lyrics and queue: cross-fade the content only
                var incoming = showQueue ? (UIElement)QueueView : LyricsScroll;
                LyricsScroll.Visibility = showQueue ? Visibility.Collapsed : Visibility.Visible;
                QueueView.Visibility = showQueue ? Visibility.Visible : Visibility.Collapsed;
                Motion.Enter(incoming, 0, 10, Motion.Base);
            }
            else
            {
                LyricsScroll.Visibility = showQueue ? Visibility.Collapsed : Visibility.Visible;
                QueueView.Visibility = showQueue ? Visibility.Visible : Visibility.Collapsed;
            }
            if (showQueue && List.SelectedItem != null) Dispatcher.InvokeAsync(() => List.ScrollIntoView(List.SelectedItem), DispatcherPriority.Loaded);
            if (!wasExpanded) SetExpanded(true);
            else if (!showQueue) Dispatcher.InvokeAsync(() => ScrollToLine(lyrIndex, true), DispatcherPriority.Loaded);
        }
        else if (wasExpanded) SetExpanded(false);
    }

    void SetExpanded(bool on)
    {
        if (expanded == on) return;
        if (artAnimating)
        {
            // finish the running morph immediately before starting the next one
            foreach (var p in new[] { Canvas.LeftProperty, Canvas.TopProperty, WidthProperty, HeightProperty }) ArtBox.BeginAnimation(p, null);
            artAnimating = false;
        }
        expanded = on;

        double fx = Canvas.GetLeft(ArtBox), fy = Canvas.GetTop(ArtBox), fs = ArtBox.Width;
        if (double.IsNaN(fx)) fx = 0;
        if (double.IsNaN(fy)) fy = 0;
        if (double.IsNaN(fs)) fs = artSize;
        Point to = on ? new Point(0, 0) : ArtSpacer.TranslatePoint(new Point(0, 0), ArtLayer);
        double ts = on ? MiniArtSize : artSize;

        artAnimating = true;
        ArtBox.CornerRadius = new CornerRadius(on ? 16 : 28);
        int ms = Motion.Morph;
        Motion.FromTo(ArtBox, Canvas.LeftProperty, fx, to.X, ms, Motion.InOut);
        Motion.FromTo(ArtBox, Canvas.TopProperty, fy, to.Y, ms, Motion.InOut);
        Motion.FromTo(ArtBox, WidthProperty, fs, ts, ms, Motion.InOut);
        Motion.FromTo(ArtBox, HeightProperty, fs, ts, ms, Motion.InOut, () =>
        {
            foreach (var p in new[] { Canvas.LeftProperty, Canvas.TopProperty, WidthProperty, HeightProperty })
                ArtBox.BeginAnimation(p, null);
            artAnimating = false;
            PositionArt();
        });

        NormalView.IsHitTestVisible = !on;
        Bars.Running = playing && !on && IsActive; // level bars are hidden behind the lyrics / queue
        ExpandedView.IsHitTestVisible = on;
        Motion.To(NormalView, OpacityProperty, on ? 0 : 1, on ? Motion.Fast + 60 : Motion.Slow);
        Motion.To(ExpandedView, OpacityProperty, on ? 1 : 0, on ? Motion.Slow + 120 : Motion.Fast + 60);
        Motion.To(ExpandedT, TranslateTransform.YProperty, on ? 0 : 24, Motion.Slow + 120, Motion.Emphasized);

        if (on && playerTab == "lyrics") Dispatcher.InvokeAsync(() => ScrollToLine(lyrIndex, true), DispatcherPriority.Loaded);
    }

    // ---- lyrics content ----
    void ShowNoLyrics(string msg)
    {
        lyrics = new();
        lyrSynced = false;
        lyrIndex = -1;
        LyricsStack.Children.Clear();
        LyricsStack.Children.Add(MakeLine(msg.Length > 0 ? msg : "No lyrics for this song.", -1, .5));
        UpdatePreview();
    }

    void SetLyrics(List<LyricLine> lines, bool synced)
    {
        if (lines.Count == 0)
        {
            ShowNoLyrics("No lyrics in this file. Add them to the song's tags, or put a .lrc file with the same name next to it.");
            return;
        }
        lyrics = lines;
        lyrSynced = synced;
        lyrIndex = -1;
        LyricsStack.Children.Clear();
        for (int i = 0; i < lines.Count; i++)
            LyricsStack.Children.Add(MakeLine(lines[i].Text, i, synced ? .35 : .9));
        UpdatePreview();
        Motion.FromTo(LyricsStack, OpacityProperty, 0, 1, Motion.Slow);
        LyricsScroll.ScrollToTop();
    }

    TextBlock MakeLine(string text, int i, double opacity)
    {
        var tb = new TextBlock
        {
            Text = text.Length == 0 ? " " : text,
            FontSize = cfg.LyricsSize,
            FontWeight = FontWeights.SemiBold,
            TextWrapping = TextWrapping.Wrap,
            Opacity = opacity,
            Margin = new Thickness(0, 7, 0, 7),
            RenderTransformOrigin = new Point(0, .5),
            RenderTransform = new ScaleTransform(1, 1)
        };
        if (i >= 0 && lyrSynced)
        {
            int ms = lyrics[i].TimeMs;
            tb.Cursor = Cursors.Hand;
            tb.MouseLeftButtonUp += (_, _) => SeekTo(ms); // click a line to jump there
        }
        return tb;
    }

    void ApplyLyricsSize()
    {
        foreach (var c in LyricsStack.Children.OfType<TextBlock>()) c.FontSize = cfg.LyricsSize;
    }

    void UpdateLyrics(long pos)
    {
        if (!lyrSynced || lyrics.Count == 0) return;
        int i = -1;
        for (int k = 0; k < lyrics.Count; k++)
        {
            if (lyrics[k].TimeMs <= pos + 150) i = k;
            else break;
        }
        if (i == lyrIndex) return;
        if (lyrIndex >= 0 && lyrIndex < LyricsStack.Children.Count) Highlight((TextBlock)LyricsStack.Children[lyrIndex], false);
        lyrIndex = i;
        if (i >= 0 && i < LyricsStack.Children.Count) Highlight((TextBlock)LyricsStack.Children[i], true);
        UpdatePreview();
        if (expanded && playerTab == "lyrics") ScrollToLine(i, false);
    }

    static void Highlight(TextBlock tb, bool on)
    {
        Motion.To(tb, OpacityProperty, on ? 1 : .35, Motion.Base);
        var st = (ScaleTransform)tb.RenderTransform;
        Motion.To(st, ScaleTransform.ScaleXProperty, on ? 1.05 : 1, Motion.Base);
        Motion.To(st, ScaleTransform.ScaleYProperty, on ? 1.05 : 1, Motion.Base);
    }

    void UpdatePreview()
    {
        string L(int k) => k >= 0 && k < lyrics.Count ? lyrics[k].Text : "";
        LyrCur.BeginAnimation(OpacityProperty, null);
        if (lyrics.Count == 0)
        {
            LyrPrev.Text = LyrNext.Text = "";
            LyrCur.Text = "";
            LyricPreview.Visibility = Visibility.Collapsed;
            return;
        }
        LyricPreview.Visibility = Visibility.Visible;
        LyrCur.Opacity = 1;
        if (!lyrSynced)
        {
            var ne = lyrics.Where(l => l.Text.Trim().Length > 0).Select(l => l.Text).ToList();
            LyrPrev.Text = "";
            LyrCur.Text = ne.ElementAtOrDefault(0) ?? "";
            LyrNext.Text = ne.ElementAtOrDefault(1) ?? "";
            return;
        }
        LyrPrev.Text = L(lyrIndex - 1);
        LyrCur.Text = lyrIndex < 0 ? "♪" : L(lyrIndex);
        LyrNext.Text = L(lyrIndex + 1);
        Motion.FromTo(LyrCur, OpacityProperty, .3, 1, Motion.Base);
    }

    // ---- smooth auto-scroll in the lyrics view ----
    double lyrTarget;
    bool lyrScrolling;

    void ScrollToLine(int i, bool instant)
    {
        double y = 0;
        if (i >= 0 && i < LyricsStack.Children.Count && LyricsStack.Children[i] is FrameworkElement el)
            y = el.TranslatePoint(new Point(0, 0), LyricsStack).Y + LyricsStack.Margin.Top + el.ActualHeight / 2 - LyricsScroll.ViewportHeight * 0.4;
        y = Math.Clamp(y, 0, Math.Max(0, LyricsScroll.ScrollableHeight));
        if (instant || Motion.Reduced) { LyricsScroll.ScrollToVerticalOffset(y); return; }
        lyrTarget = y;
        if (!lyrScrolling) { lyrScrolling = true; CompositionTarget.Rendering += LyrScrollStep; }
    }

    void LyrScrollStep(object? s, EventArgs e)
    {
        double diff = lyrTarget - LyricsScroll.VerticalOffset;
        if (Math.Abs(diff) < 0.5)
        {
            LyricsScroll.ScrollToVerticalOffset(lyrTarget);
            CompositionTarget.Rendering -= LyrScrollStep;
            lyrScrolling = false;
            return;
        }
        LyricsScroll.ScrollToVerticalOffset(LyricsScroll.VerticalOffset + diff * 0.12);
    }
}
