using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Windows.Media;

namespace LiquidCloud;

/// <summary>A LiquidCloud theme. Colors are hex strings so themes can be saved, shared and edited.</summary>
public class ThemeDef
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Name { get; set; } = "My theme";
    public bool BuiltIn { get; set; }

    public string Primary { get; set; } = "#2E6BFF";     // main light blob
    public string Secondary { get; set; } = "#38D1FF";   // second light blob
    public string Accent { get; set; } = "#5AB4FF";      // buttons, sliders, highlights
    public string Text { get; set; } = "#FFFFFF";
    public string Glass { get; set; } = "#CFE6FF";       // tint of glass surfaces
    public string Background { get; set; } = "#060B1A";
    public string Gradient1 { get; set; } = "#0A1530";
    public string Gradient2 { get; set; } = "#03060F";
    public double GradientAngle { get; set; } = 135;

    public double GlassOpacity { get; set; } = 0.12;
    public double Blur { get; set; } = 0;                 // blur of everything behind the panels
    public double Glow { get; set; } = 0.6;               // ambient glow and light edges
    public double Radius { get; set; } = 24;
    public double Shadow { get; set; } = 0.32;

    public ThemeDef Clone(string? newName = null)
    {
        var c = (ThemeDef)MemberwiseClone();
        c.Id = Guid.NewGuid().ToString("N");
        c.BuiltIn = false;
        if (newName != null) c.Name = newName;
        return c;
    }

    public static Color C(string? hex, Color fallback)
    {
        if (string.IsNullOrWhiteSpace(hex)) return fallback;
        try { return (Color)ColorConverter.ConvertFromString(hex); }
        catch (FormatException) { return fallback; } // a hand-edited theme with a typo keeps working
    }

    public Color PrimaryC => C(Primary, Colors.RoyalBlue);
    public Color SecondaryC => C(Secondary, Colors.DeepSkyBlue);
    public Color AccentC => C(Accent, Colors.DodgerBlue);
    public Color TextC => C(Text, Colors.White);
    public Color GlassC => C(Glass, Colors.White);
    public Color BackgroundC => C(Background, Colors.Black);
    public Color Gradient1C => C(Gradient1, Colors.Black);
    public Color Gradient2C => C(Gradient2, Colors.Black);
}

/// <summary>Built-in themes plus your own (saved in %AppData%\LiquidCloud\themes.json). Exports as .lctheme files.</summary>
static class ThemeStore
{
    static readonly string FilePath = Path.Combine(AppSettings.Dir, "themes.json");
    public const string DefaultId = "liquid";

    static ThemeDef B(string id, string name, string p, string s, string a, string text, string glass, string bg, string g1, string g2,
                      double op = .12, double glow = .6, double radius = 24, double shadow = .32, double blur = 0) => new()
    {
        Id = id, Name = name, BuiltIn = true, Primary = p, Secondary = s, Accent = a, Text = text, Glass = glass,
        Background = bg, Gradient1 = g1, Gradient2 = g2, GlassOpacity = op, Glow = glow, Radius = radius, Shadow = shadow, Blur = blur
    };

    // cold blue / cyan / white first: the LiquidCloud look
    public static readonly ThemeDef[] BuiltIns =
    {
        B("liquid",   "Liquid",      "#2E6BFF", "#38D1FF", "#5AB4FF", "#FFFFFF", "#CFE6FF", "#050A18", "#0B1838", "#02050D"),
        B("glacier",  "Glacier",     "#7FD8FF", "#E6F7FF", "#8BE3FF", "#FFFFFF", "#E8F7FF", "#08131C", "#123247", "#050B12", .14, .7, 26),
        B("deep",     "Deep Ocean",  "#0A3DFF", "#00A3FF", "#2F8CFF", "#EAF2FF", "#9CC2FF", "#020615", "#06123A", "#010309", .10, .55, 22, .4),
        B("arctic",   "Arctic Glass","#B9DCFF", "#FFFFFF", "#3E8BFF", "#0E1A2B", "#FFFFFF", "#DCEAF7", "#EFF6FD", "#C9DDF0", .45, .35, 26, .18),
        B("midnight", "Midnight",    "#4B4DFF", "#1C7CFF", "#7A7CFF", "#FFFFFF", "#C8CCFF", "#05050F", "#11123A", "#03030A", .11, .55, 24),
        B("aurora",   "Aurora",      "#30D158", "#64D2FF", "#4DE0B0", "#FFFFFF", "#D2FFF0", "#04110E", "#0B2A2A", "#020807"),
        B("sakura",   "Sakura",      "#FF6FB5", "#BF5AF2", "#FF8DC7", "#FFFFFF", "#FFE0F0", "#12060E", "#2A0F24", "#080307"),
        B("ember",    "Ember",       "#FF453A", "#FFB01F", "#FF7A45", "#FFFFFF", "#FFE2D6", "#120605", "#2E0E0A", "#080202"),
        B("mono",     "Mono",        "#8E8E93", "#3A3A3C", "#E5E5EA", "#FFFFFF", "#FFFFFF", "#0A0A0B", "#1C1C1E", "#050505", .12, .4, 20),
    };

    public static List<ThemeDef> User { get; private set; } = new();

    public static IEnumerable<ThemeDef> All => BuiltIns.Concat(User);

    public static ThemeDef Find(string? id) => All.FirstOrDefault(t => t.Id == id) ?? BuiltIns[0];

    public static void Load()
    {
        try { if (File.Exists(FilePath)) User = JsonSerializer.Deserialize<List<ThemeDef>>(File.ReadAllText(FilePath)) ?? new(); }
        catch (Exception ex) when (ex is JsonException or IOException)
        {
            Log.Error("Themes", "themes.json unreadable; your themes are kept in themes.json.broken", ex);
            try { File.Copy(FilePath, FilePath + ".broken", true); } catch (IOException) { }
            User = new();
        }
        foreach (var t in User) t.BuiltIn = false;
    }

    public static void Save()
    {
        try
        {
            Directory.CreateDirectory(AppSettings.Dir);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(User, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { Log.Warn("Themes", "Couldn't save themes", ex); }
    }

    /// <summary>Adds or replaces a user theme.</summary>
    public static void Put(ThemeDef t)
    {
        t.BuiltIn = false;
        int i = User.FindIndex(u => u.Id == t.Id);
        if (i >= 0) User[i] = t; else User.Add(t);
        Save();
    }

    public static void Delete(string id) { User.RemoveAll(u => u.Id == id); Save(); }

    public static void Export(ThemeDef t, string path) =>
        File.WriteAllText(path, JsonSerializer.Serialize(t, new JsonSerializerOptions { WriteIndented = true }));

    public static ThemeDef Import(string path)
    {
        var t = JsonSerializer.Deserialize<ThemeDef>(File.ReadAllText(path)) ?? throw new InvalidDataException(Loc.T("That file isn't a LiquidCloud theme."));
        // validate every color so a bad file can't break the look
        foreach (var hex in new[] { t.Primary, t.Secondary, t.Accent, t.Text, t.Glass, t.Background, t.Gradient1, t.Gradient2 })
            ColorConverter.ConvertFromString(hex);
        t.Id = Guid.NewGuid().ToString("N");
        t.BuiltIn = false;
        t.GlassOpacity = Math.Clamp(t.GlassOpacity, 0, 1);
        t.Radius = Math.Clamp(t.Radius, 0, 48);
        t.Blur = Math.Clamp(t.Blur, 0, 60);
        t.Glow = Math.Clamp(t.Glow, 0, 1);
        t.Shadow = Math.Clamp(t.Shadow, 0, 1);
        Put(t);
        return t;
    }
}

/// <summary>Color math helpers.</summary>
static class Themes
{
    public static double Luminance(Color c) => (0.2126 * c.R + 0.7152 * c.G + 0.0722 * c.B) / 255.0;

    public static string Hex(Color c) => $"#{c.R:X2}{c.G:X2}{c.B:X2}";

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
        s = Math.Clamp(s, 0, 1); v = Math.Clamp(v, 0, 1);
        double c = v * s, x = c * (1 - Math.Abs(h / 60 % 2 - 1)), m = v - c;
        (double r, double g, double b) = h switch
        {
            < 60 => (c, x, 0d), < 120 => (x, c, 0d), < 180 => (0d, c, x),
            < 240 => (0d, x, c), < 300 => (x, 0d, c), _ => (c, 0d, x)
        };
        return Color.FromRgb((byte)Math.Round((r + m) * 255), (byte)Math.Round((g + m) * 255), (byte)Math.Round((b + m) * 255));
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

    public static Color Mix(Color a, Color b, double t) => Color.FromRgb(
        (byte)(a.R + (b.R - a.R) * t), (byte)(a.G + (b.G - a.G) * t), (byte)(a.B + (b.B - a.B) * t));

    public static Color WithAlpha(Color c, double a) => Color.FromArgb((byte)Math.Clamp(a * 255, 0, 255), c.R, c.G, c.B);
}
