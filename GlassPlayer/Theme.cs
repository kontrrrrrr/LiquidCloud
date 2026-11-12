using System;
using System.Windows.Media;

namespace GlassPlayer;

public record ThemeDef(string Name, Color Accent, Color Blob1, Color Blob2, Color Tint);

static class Themes
{
    static Color C(string hex) => (Color)ColorConverter.ConvertFromString(hex);

    public static readonly ThemeDef[] All =
    {
        new("LiquidCloud",  C("#5E5CE6"), C("#5E5CE6"), C("#FF375F"), C("#1E1E28")),
        new("Aurora", C("#30D158"), C("#64D2FF"), C("#30D158"), C("#14221E")),
        new("Sunset", C("#FF9F0A"), C("#FF375F"), C("#FF9F0A"), C("#2A1A16")),
        new("Ocean",  C("#0A84FF"), C("#0A84FF"), C("#5AC8FA"), C("#121C2A")),
        new("Sakura", C("#FF6FB5"), C("#FF6FB5"), C("#BF5AF2"), C("#26182A")),
        new("Ember",  C("#FF453A"), C("#FF453A"), C("#FFD60A"), C("#2A1414")),
        new("Forest", C("#7CC36A"), C("#2E7D4F"), C("#9CCC65"), C("#16211A")),
        new("Mono",   C("#E5E5EA"), C("#8E8E93"), C("#3A3A3C"), C("#1C1C1E")),
    };

    public static double Luminance(Color c) => (0.2126 * c.R + 0.7152 * c.G + 0.0722 * c.B) / 255.0;

    public static (double H, double S, double V) ToHsv(Color c)
    {
        double r = c.R / 255.0, g = c.G / 255.0, b = c.B / 255.0;
        double max = Math.Max(r, Math.Max(g, b)), min = Math.Min(r, Math.Min(g, b)), d = max - min;
        double h = 0;
        if (d > 0)
        {
            if (max == r) h = 60 * (((g - b) / d) % 6);
            else if (max == g) h = 60 * ((b - r) / d + 2);
            else h = 60 * ((r - g) / d + 4);
        }
        if (h < 0) h += 360;
        return (h, max == 0 ? 0 : d / max, max);
    }

    public static Color FromHsv(double h, double s, double v)
    {
        h = (h % 360 + 360) % 360;
        double c = v * s, x = c * (1 - Math.Abs(h / 60 % 2 - 1)), m = v - c;
        (double r, double g, double b) = h switch
        {
            < 60 => (c, x, 0d), < 120 => (x, c, 0d), < 180 => (0d, c, x),
            < 240 => (0d, x, c), < 300 => (x, 0d, c), _ => (c, 0d, x)
        };
        return Color.FromRgb((byte)((r + m) * 255), (byte)((g + m) * 255), (byte)((b + m) * 255));
    }

    /// <summary>Makes a color usable as an accent: enough saturation and brightness.</summary>
    public static Color Vivid(Color c)
    {
        var (h, s, v) = ToHsv(c);
        return s < 0.12 ? FromHsv(h, s, Math.Max(v, 0.85)) : FromHsv(h, Math.Max(s, 0.5), Math.Max(v, 0.8));
    }

    public static Color Shift(Color c, double degrees)
    {
        var (h, s, v) = ToHsv(c);
        return FromHsv(h + degrees, s, v);
    }
}
