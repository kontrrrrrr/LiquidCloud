using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;

namespace GlassPlayer;

/// <summary>Lyrics under the cover, and the full-panel lyrics view where the cover flies into the corner.</summary>
public partial class MainWindow
{
    List<LyricLine> lyrics = new();
    bool lyrSynced, expanded, artAnimating;
    int lyrIndex = -1;
    double artSize = 280;
    const double MiniArt = 76;

    void InitLyricsLayout()
    {
        MainArea.SizeChanged += (_, _) =>
        {
            artSize = Math.Clamp(MainArea.ActualHeight - 240, 140, 320);
            ArtSpacer.Width = ArtSpacer.Height = artSize;
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
        if (expanded) { PlaceArt(0, 0, MiniArt); return; }
        if (!IsLoaded) return;
        var p = ArtSpacer.TranslatePoint(new Point(0, 0), ArtLayer);
        PlaceArt(p.X, p.Y, artSize);
    }

    void PlaceArt(double x, double y, double size)
    {
        Canvas.SetLeft(ArtBox, x);
        Canvas.SetTop(ArtBox, y);
        ArtBox.Width = ArtBox.Height = size;
        ArtBox.CornerRadius = new CornerRadius(size > 120 ? 30 : 16);
    }

    void ExpandLyrics_Click(object s, RoutedEventArgs e) => SetExpanded(!expanded);

    void SetExpanded(bool on)
    {
        if (expanded == on || artAnimating) return;
        expanded = on;

        double fx = Canvas.GetLeft(ArtBox), fy = Canvas.GetTop(ArtBox), fs = ArtBox.Width;
        if (double.IsNaN(fx)) fx = 0;
        if (double.IsNaN(fy)) fy = 0;
        if (double.IsNaN(fs)) fs = artSize;
        Point to = on ? new Point(0, 0) : ArtSpacer.TranslatePoint(new Point(0, 0), ArtLayer);
        double ts = on ? MiniArt : artSize;

        artAnimating = true;
        ArtBox.CornerRadius = new CornerRadius(on ? 16 : 30);
        var dur = TimeSpan.FromMilliseconds(560 * AnimFactor);
        var ease = new CubicEase { EasingMode = EasingMode.EaseInOut };
        DoubleAnimation A(double a, double b) => new(a, b, dur) { EasingFunction = ease };

        var last = A(fs, ts);
        last.Completed += (_, _) =>
        {
            foreach (var p in new[] { Canvas.LeftProperty, Canvas.TopProperty, WidthProperty, HeightProperty })
                ArtBox.BeginAnimation(p, null);
            artAnimating = false;
            PositionArt();
        };
        ArtBox.BeginAnimation(Canvas.LeftProperty, A(fx, to.X));
        ArtBox.BeginAnimation(Canvas.TopProperty, A(fy, to.Y));
        ArtBox.BeginAnimation(WidthProperty, A(fs, ts));
        ArtBox.BeginAnimation(HeightProperty, last);

        NormalView.IsHitTestVisible = !on;
        ExpandedView.IsHitTestVisible = on;
        Anim(NormalView, OpacityProperty, on ? 0 : 1, on ? 260 : 520);
        Anim(ExpandedView, OpacityProperty, on ? 1 : 0, on ? 600 : 260);
        Anim(ExpandedT, TranslateTransform.YProperty, on ? 0 : 24, 600);

        if (on) Dispatcher.InvokeAsync(() => ScrollToLine(lyrIndex, true), DispatcherPriority.Loaded);
    }

    // ---- content ----
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
        Fade(LyricsStack, 0, 1, 500);
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
        if (expanded) ScrollToLine(i, false);
    }

    static void Highlight(TextBlock tb, bool on)
    {
        Anim(tb, OpacityProperty, on ? 1 : .35, 300);
        var st = (ScaleTransform)tb.RenderTransform;
        Anim(st, ScaleTransform.ScaleXProperty, on ? 1.05 : 1, 300);
        Anim(st, ScaleTransform.ScaleYProperty, on ? 1.05 : 1, 300);
    }

    void UpdatePreview()
    {
        string L(int k) => k >= 0 && k < lyrics.Count ? lyrics[k].Text : "";
        if (lyrics.Count == 0)
        {
            LyrPrev.Text = "";
            LyrCur.Text = LyricsStack.Children.Count > 0 && LyricsStack.Children[0] is TextBlock t0 && t0.Text.StartsWith("Loading") ? "" : "No lyrics";
            LyrNext.Text = "";
            LyrCur.BeginAnimation(OpacityProperty, null);
            LyrCur.Opacity = .5;
            return;
        }
        LyrCur.BeginAnimation(OpacityProperty, null);
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
        Fade(LyrCur, .3, 1, 300);
    }

    // ---- smooth auto-scroll in the big lyrics view ----
    double lyrTarget;
    bool lyrScrolling;

    void ScrollToLine(int i, bool instant)
    {
        double y = 0;
        if (i >= 0 && i < LyricsStack.Children.Count && LyricsStack.Children[i] is FrameworkElement el)
            y = el.TranslatePoint(new Point(0, 0), LyricsStack).Y + LyricsStack.Margin.Top + el.ActualHeight / 2 - LyricsScroll.ViewportHeight * 0.4;
        y = Math.Clamp(y, 0, Math.Max(0, LyricsScroll.ScrollableHeight));
        if (instant) { LyricsScroll.ScrollToVerticalOffset(y); return; }
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
