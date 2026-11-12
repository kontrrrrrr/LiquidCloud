using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Effects;

namespace LiquidCloud;

/// <summary>The colors a theme resolves to, besides the resource tokens (used for things that aren't resources).</summary>
public sealed record ThemeColors(Color Accent, Color Ink, bool LightUi);

/// <summary>
/// Turns a theme into the design tokens every control uses. Pure: it only computes values, so it can be tested;
/// the window applies the result to the app's resources (which recolors menus, popups and open pages too).
/// </summary>
public static class ThemeService
{
    static SolidColorBrush Solid(Color c) { var b = new SolidColorBrush(c); b.Freeze(); return b; }
    static Color A(Color c, double alpha) => Themes.WithAlpha(c, alpha);

    static Brush Blob(Color c, double strength)
    {
        var b = new RadialGradientBrush(new GradientStopCollection { new GradientStop(A(c, .65 * strength + .1), 0), new GradientStop(A(c, 0), 1) });
        b.Freeze();
        return b;
    }

    public static LinearGradientBrush Linear(Color a, Color b, double angle = 135)
    {
        double r = angle * Math.PI / 180;
        var g = new LinearGradientBrush(a, b, 0)
        {
            StartPoint = new Point(.5 - Math.Cos(r) / 2, .5 - Math.Sin(r) / 2),
            EndPoint = new Point(.5 + Math.Cos(r) / 2, .5 + Math.Sin(r) / 2)
        };
        g.Freeze();
        return g;
    }

    /// <summary>Is this a light theme (dark text on light glass)?</summary>
    public static bool IsLight(ThemeDef th) => Themes.Luminance(th.BackgroundC) > .55;

    /// <summary>Text/icon color placed on top of the accent: dark on light accents, white on dark ones.</summary>
    public static Color OnAccent(Color accent) => Themes.Luminance(accent) > .58 ? Color.FromRgb(10, 18, 34) : Colors.White;

    public static (Dictionary<string, object> Tokens, ThemeColors Colors) Build(ThemeDef th, Color? backgroundAccent)
    {
        var accent = backgroundAccent ?? th.AccentC;
        bool fromBg = backgroundAccent != null;
        var text = th.TextC;
        bool lightUi = IsLight(th);
        var ink = lightUi ? Color.FromRgb(10, 20, 40) : Colors.White; // overlays: dark on light themes, white on dark ones
        var glass = th.GlassC;
        double op = Math.Clamp(th.GlassOpacity, 0, 1);
        var bgc = th.BackgroundC;
        var t = new Dictionary<string, object>
        {
            ["AccentBrush"] = Solid(accent),
            ["AccentSoftBrush"] = Solid(A(accent, .26)),
            ["OnAccentBrush"] = Solid(OnAccent(accent)),
            ["TextBrush"] = Solid(text),
            ["TextDimBrush"] = Solid(A(text, .66)),
            ["TextFaintBrush"] = Solid(A(text, .40)),
            ["HoverBrush"] = Solid(A(ink, lightUi ? .06 : .07)),
            ["PressBrush"] = Solid(A(ink, lightUi ? .10 : .13)),
            ["FieldBrush"] = Solid(A(ink, lightUi ? .05 : .06)),
            ["LineBrush"] = Solid(A(ink, lightUi ? .14 : .13)),
            ["SkeletonBrush"] = Solid(A(ink, .09)),
            ["SheetBrush"] = Solid(A(Themes.Mix(bgc, glass, .06), .94)),
            ["MenuBrush"] = Solid(A(Themes.Mix(bgc, glass, .10), .97)),
            ["BgBaseBrush"] = Linear(th.Gradient1C, th.Gradient2C, th.GradientAngle),
            ["Blob1Brush"] = Blob(fromBg ? accent : th.PrimaryC, th.Glow),
            ["Blob2Brush"] = Blob(fromBg ? Themes.Shift(accent, 40) : th.SecondaryC, th.Glow),
        };

        // glass: level 1 (panels) and level 2 (cards, popups), each with a light top-left edge
        var tint = fromBg ? Themes.Mix(glass, accent, .25) : glass;
        t["GlassFill"] = Linear(A(tint, op), A(tint, op * .3));
        t["GlassFillRaised"] = Linear(A(tint, Math.Min(1, op * 1.6 + .03)), A(tint, op * .55));
        double edge = .25 + .5 * Math.Clamp(th.Glow, 0, 1);
        var edgeBrush = new LinearGradientBrush(new GradientStopCollection
        {
            new GradientStop(A(lightUi ? Colors.White : Themes.Mix(Colors.White, accent, .15), edge), 0),
            new GradientStop(A(Colors.White, .05), .45),
            new GradientStop(A(Colors.White, edge * .5), 1)
        }, new Point(0, 0), new Point(1, 1));
        edgeBrush.Freeze();
        t["GlassEdge"] = edgeBrush;

        double r = Math.Clamp(th.Radius, 0, 48);
        t["GlassRadius"] = new CornerRadius(r);
        t["CardRadius"] = new CornerRadius(Math.Max(4, r * .66));
        t["ControlRadius"] = new CornerRadius(Math.Max(4, r * .5));

        double sh = Math.Clamp(th.Shadow, 0, 1);
        var shadow = new DropShadowEffect { BlurRadius = 44, ShadowDepth = 12, Direction = 270, Opacity = sh };
        shadow.Freeze();
        t["GlassShadow"] = shadow;
        var soft = new DropShadowEffect { BlurRadius = 26, ShadowDepth = 6, Direction = 270, Opacity = sh * .85 };
        soft.Freeze();
        t["SoftShadow"] = soft;
        t["ShadowBrush"] = Solid(A(Colors.Black, sh * (lightUi ? .45 : .85)));
        return (t, new ThemeColors(accent, ink, lightUi));
    }

    /// <summary>Writes the tokens into a resource dictionary (the app's). DynamicResource users update immediately.</summary>
    public static void Apply(ResourceDictionary target, Dictionary<string, object> tokens)
    {
        foreach (var (k, v) in tokens) target[k] = v;
    }
}
