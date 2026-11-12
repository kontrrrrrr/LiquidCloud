using System.Windows;
using System.Windows.Media;

namespace LiquidCloud.Tests;

public class ThemeServiceTests
{
    /// <summary>WPF brushes are created on an STA thread, like in the app.</summary>
    static TResult Sta<TResult>(Func<TResult> f)
    {
        TResult r = default!;
        Exception? err = null;
        var th = new Thread(() => { try { r = f(); } catch (Exception e) { err = e; } });
        th.SetApartmentState(ApartmentState.STA);
        th.Start(); th.Join();
        if (err != null) throw new Exception("STA failure", err);
        return r;
    }

    static Color Col(object brush) => ((SolidColorBrush)brush).Color;

    [Fact]
    public void Every_built_in_theme_produces_all_tokens()
    {
        string[] keys = { "AccentBrush", "OnAccentBrush", "TextBrush", "TextDimBrush", "HoverBrush", "GlassFill", "GlassFillRaised",
                          "GlassEdge", "GlassRadius", "CardRadius", "ControlRadius", "GlassShadow", "SoftShadow", "ShadowBrush", "BgBaseBrush" };
        Sta(() =>
        {
            foreach (var th in ThemeStore.BuiltIns)
            {
                var (tokens, _) = ThemeService.Build(th, null);
                foreach (var k in keys) Assert.True(tokens.ContainsKey(k), $"{th.Id} misses {k}");
                foreach (var v in tokens.Values)
                    if (v is Freezable f) Assert.True(f.IsFrozen, $"{th.Id}: unfrozen token"); // app resources must be frozen (shared across threads)
            }
            return 0;
        });
    }

    [Fact]
    public void Text_on_accent_stays_readable()
    {
        Assert.Equal(Colors.White, ThemeService.OnAccent(Color.FromRgb(10, 61, 255)));       // dark blue → white text
        Assert.NotEqual(Colors.White, ThemeService.OnAccent(Color.FromRgb(230, 247, 255)));  // pale blue → dark text
        foreach (var th in ThemeStore.BuiltIns)
        {
            var on = ThemeService.OnAccent(th.AccentC);
            var diff = Math.Abs(Themes.Luminance(on) - Themes.Luminance(th.AccentC));
            Assert.True(diff > 0.3, $"{th.Id}: contrast {diff:0.00}");
        }
    }

    [Fact]
    public void Light_theme_uses_dark_ink()
    {
        var arctic = ThemeStore.Find("arctic");
        var deep = ThemeStore.Find("deep");
        Assert.True(ThemeService.IsLight(arctic));
        Assert.False(ThemeService.IsLight(deep));
        var (_, light) = Sta(() => ThemeService.Build(arctic, null));
        var (_, dark) = Sta(() => ThemeService.Build(deep, null));
        Assert.True(light.LightUi);
        Assert.True(Themes.Luminance(light.Ink) < .2);
        Assert.Equal(Colors.White, dark.Ink);
    }

    [Fact]
    public void Radius_tokens_follow_theme_and_are_clamped()
    {
        var th = ThemeStore.Find("liquid").Clone();
        th.Radius = 30;
        var (t, _) = Sta(() => ThemeService.Build(th, null));
        Assert.Equal(new CornerRadius(30), t["GlassRadius"]);
        Assert.Equal(new CornerRadius(15), t["ControlRadius"]);
        th.Radius = 500;
        (t, _) = Sta(() => ThemeService.Build(th, null));
        Assert.Equal(new CornerRadius(48), t["GlassRadius"]);
        th.Radius = -3;
        (t, _) = Sta(() => ThemeService.Build(th, null));
        Assert.Equal(new CornerRadius(4), t["CardRadius"]);
    }

    [Fact]
    public void Background_accent_overrides_theme_accent()
    {
        var th = ThemeStore.Find("liquid");
        var red = Color.FromRgb(220, 40, 40);
        var (t, c) = Sta(() => ThemeService.Build(th, red));
        Assert.Equal(red, c.Accent);
        Assert.Equal(red, Sta(() => Col(t["AccentBrush"])));
        var (t2, c2) = Sta(() => ThemeService.Build(th, null));
        Assert.Equal(th.AccentC, c2.Accent);
    }

    [Fact]
    public void Broken_colors_fall_back_instead_of_crashing()
    {
        var th = new ThemeDef { Accent = "#zzz", Text = "", Background = null! };
        Assert.Equal(Colors.DodgerBlue, th.AccentC);
        Assert.Equal(Colors.White, th.TextC);
        Assert.Equal(Colors.Black, th.BackgroundC);
        Sta(() => ThemeService.Build(th, null));
    }

    [Fact]
    public void Apply_writes_tokens_into_dictionary()
    {
        Sta(() =>
        {
            var d = new ResourceDictionary();
            var (t, _) = ThemeService.Build(ThemeStore.Find("aurora"), null);
            ThemeService.Apply(d, t);
            Assert.Equal(t.Count, d.Count);
            Assert.Same(t["AccentBrush"], d["AccentBrush"]);
            return 0;
        });
    }

    [Fact]
    public void Color_helpers_round_trip()
    {
        var c = Color.FromRgb(46, 107, 255);
        var (h, s, v) = Themes.ToHsv(c);
        var back = Themes.FromHsv(h, s, v);
        Assert.InRange(Math.Abs(back.R - c.R) + Math.Abs(back.G - c.G) + Math.Abs(back.B - c.B), 0, 3);
        Assert.Equal("#2E6BFF", Themes.Hex(c));
        Assert.InRange((int)Themes.WithAlpha(c, .5).A, 127, 128);
    }
}
