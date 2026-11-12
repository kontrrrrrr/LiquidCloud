using System;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace GlassPlayer;

/// <summary>
/// LiquidCloud's single motion system: every animation in the app uses these durations, curves and presets,
/// and only animates opacity and transforms (no layout work), so it stays smooth.
/// Respects Windows' "Show animations" setting and the in-app "Reduce motion" switch.
/// </summary>
static class Motion
{
    // ---- durations (ms) ----
    public const int Instant = 90;   // press feedback
    public const int Fast = 160;     // hover, small state changes
    public const int Base = 260;     // most transitions
    public const int Slow = 420;     // bigger surfaces, modals
    public const int Page = 380;     // page enter
    public const int Morph = 560;    // cover morphs, layout-like moves

    public static double Speed = 1;  // user "Animation speed" (1 = normal, 0.5 = twice as fast)
    public static bool UserReduced;  // in-app "Reduce motion"
    public static bool Reduced => UserReduced || !SystemParameters.ClientAreaAnimation;

    // ---- easing curves ----
    public static readonly IEasingFunction Standard = Freeze(new CubicEase { EasingMode = EasingMode.EaseOut });
    public static readonly IEasingFunction Emphasized = Freeze(new QuinticEase { EasingMode = EasingMode.EaseOut });
    public static readonly IEasingFunction InOut = Freeze(new CubicEase { EasingMode = EasingMode.EaseInOut });
    public static readonly IEasingFunction Exit = Freeze(new CubicEase { EasingMode = EasingMode.EaseIn });
    public static readonly IEasingFunction Spring = Freeze(new BackEase { EasingMode = EasingMode.EaseOut, Amplitude = 0.35 });

    static IEasingFunction Freeze(EasingFunctionBase e) { e.Freeze(); return e; }

    public static Duration D(int ms) => Reduced ? new Duration(TimeSpan.Zero) : new Duration(TimeSpan.FromMilliseconds(ms * Speed));

    static DoubleAnimation Make(double? from, double to, int ms, IEasingFunction? ease, int delay)
    {
        var a = new DoubleAnimation { To = to, Duration = D(ms), EasingFunction = ease ?? Standard };
        if (from.HasValue) a.From = from;
        if (delay > 0 && !Reduced) a.BeginTime = TimeSpan.FromMilliseconds(delay * Speed);
        return a;
    }

    public static void To(IAnimatable target, DependencyProperty prop, double to, int ms = Base, IEasingFunction? ease = null, Action? done = null, int delay = 0)
    {
        var a = Make(null, to, ms, ease, delay);
        if (done != null) a.Completed += (_, _) => done();
        target.BeginAnimation(prop, a);
    }

    public static void FromTo(IAnimatable target, DependencyProperty prop, double from, double to, int ms = Base, IEasingFunction? ease = null, Action? done = null, int delay = 0)
    {
        var a = Make(from, to, ms, ease, delay);
        if (done != null) a.Completed += (_, _) => done();
        target.BeginAnimation(prop, a);
    }

    public static void AnimateColor(SolidColorBrush brush, Color to, int ms = Base)
    {
        if (brush.IsFrozen) return;
        brush.BeginAnimation(SolidColorBrush.ColorProperty, new ColorAnimation(to, D(ms)) { EasingFunction = Standard });
    }

    // ---- transform helpers (one TransformGroup per element: scale + translate) ----
    public static (ScaleTransform Scale, TranslateTransform Move) Transforms(UIElement el)
    {
        if (el.RenderTransform is TransformGroup g && g.Children.Count == 2 && g.Children[0] is ScaleTransform s && g.Children[1] is TranslateTransform t)
            return (s, t);
        var sc = new ScaleTransform(1, 1);
        var tr = new TranslateTransform();
        el.RenderTransform = new TransformGroup { Children = { sc, tr } };
        el.RenderTransformOrigin = new Point(.5, .5);
        return (sc, tr);
    }

    // ---- presets ----
    /// <summary>Fade + rise into place. Used for pages, cards, rows, toasts.</summary>
    public static void Enter(UIElement el, int delay = 0, double dy = 14, int ms = Page)
    {
        var (_, mv) = Transforms(el);
        FromTo(el, UIElement.OpacityProperty, 0, 1, ms, Standard, null, delay);
        FromTo(mv, TranslateTransform.YProperty, dy, 0, ms + 80, Emphasized, null, delay);
    }

    public static void Leave(UIElement el, Action? done = null, double dy = -8, int ms = Fast)
    {
        var (_, mv) = Transforms(el);
        To(el, UIElement.OpacityProperty, 0, ms, Exit, done);
        To(mv, TranslateTransform.YProperty, dy, ms, Exit);
    }

    /// <summary>Scale-in for popups and modals.</summary>
    public static void PopIn(UIElement el, double from = .94, int ms = Slow)
    {
        var (sc, _) = Transforms(el);
        FromTo(el, UIElement.OpacityProperty, 0, 1, ms - 120, Standard);
        FromTo(sc, ScaleTransform.ScaleXProperty, from, 1, ms, Spring);
        FromTo(sc, ScaleTransform.ScaleYProperty, from, 1, ms, Spring);
    }

    public static void PopOut(UIElement el, Action? done = null, double to = .96, int ms = Fast + 40)
    {
        var (sc, _) = Transforms(el);
        To(el, UIElement.OpacityProperty, 0, ms, Exit, done);
        To(sc, ScaleTransform.ScaleXProperty, to, ms, Exit);
        To(sc, ScaleTransform.ScaleYProperty, to, ms, Exit);
    }

    /// <summary>Quick "boing" for likes and adds.</summary>
    public static void Pulse(UIElement el, double peak = 1.28)
    {
        if (Reduced) return;
        var (sc, _) = Transforms(el);
        var a = new DoubleAnimationUsingKeyFrames { Duration = D(Slow) };
        a.KeyFrames.Add(new EasingDoubleKeyFrame(peak, KeyTime.FromPercent(.35), Standard));
        a.KeyFrames.Add(new EasingDoubleKeyFrame(1, KeyTime.FromPercent(1), Spring));
        sc.BeginAnimation(ScaleTransform.ScaleXProperty, a);
        sc.BeginAnimation(ScaleTransform.ScaleYProperty, a);
    }

    /// <summary>Hover: slight lift and grow; press: slight shrink. Gives cards a spatial feel.</summary>
    public static void Hoverable(FrameworkElement el, double grow = 1.03, double lift = -3)
    {
        var (sc, mv) = Transforms(el);
        el.MouseEnter += (_, _) =>
        {
            To(sc, ScaleTransform.ScaleXProperty, grow, Fast); To(sc, ScaleTransform.ScaleYProperty, grow, Fast);
            To(mv, TranslateTransform.YProperty, lift, Fast);
        };
        el.MouseLeave += (_, _) =>
        {
            To(sc, ScaleTransform.ScaleXProperty, 1, Base); To(sc, ScaleTransform.ScaleYProperty, 1, Base);
            To(mv, TranslateTransform.YProperty, 0, Base);
        };
        el.PreviewMouseLeftButtonDown += (_, _) => { To(sc, ScaleTransform.ScaleXProperty, grow - .05, Instant); To(sc, ScaleTransform.ScaleYProperty, grow - .05, Instant); };
        el.PreviewMouseLeftButtonUp += (_, _) => { To(sc, ScaleTransform.ScaleXProperty, el.IsMouseOver ? grow : 1, Fast, Spring); To(sc, ScaleTransform.ScaleYProperty, el.IsMouseOver ? grow : 1, Fast, Spring); };
    }

    /// <summary>Press feedback only (buttons).</summary>
    public static void Pressable(FrameworkElement el)
    {
        var (sc, _) = Transforms(el);
        el.PreviewMouseLeftButtonDown += (_, _) => { To(sc, ScaleTransform.ScaleXProperty, .92, Instant); To(sc, ScaleTransform.ScaleYProperty, .92, Instant); };
        void Up() { To(sc, ScaleTransform.ScaleXProperty, 1, Base, Spring); To(sc, ScaleTransform.ScaleYProperty, 1, Base, Spring); }
        el.PreviewMouseLeftButtonUp += (_, _) => Up();
        el.MouseLeave += (_, _) => Up();
    }
}
