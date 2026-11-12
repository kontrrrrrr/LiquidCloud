using System;
using System.Globalization;
using System.Windows;

namespace LiquidCloud;

/// <summary>
/// Interface text. Every user-facing string goes through Loc.T("English text") or {DynamicResource ...} in XAML.
/// Translations live in Strings/ru.xaml (key = the English text, or an id for XAML); English is the built-in fallback.
/// Switching language swaps the dictionary at runtime; pages are rebuilt so their text follows.
/// </summary>
static class Loc
{
    public const string Russian = "ru", English = "en";
    public static string Language { get; private set; } = Russian;
    static ResourceDictionary? current;

    public static void Apply(string lang)
    {
        Language = lang == English ? English : Russian;
        var app = Application.Current;
        if (app == null) return;
        if (current != null) app.Resources.MergedDictionaries.Remove(current);
        current = new ResourceDictionary { Source = new Uri($"pack://application:,,,/LiquidCloud;component/Strings/{Language}.xaml", UriKind.Absolute) };
        app.Resources.MergedDictionaries.Add(current);
        CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo(Language == Russian ? "ru-RU" : "en-US");
    }

    public static CultureInfo Culture => CultureInfo.GetCultureInfo(Language == Russian ? "ru-RU" : "en-US");

    /// <summary>Translated text for an English source string (falls back to the English text).</summary>
    public static string T(string english)
    {
        if (current != null && current.Contains(english) && current[english] is string s) return s;
        return english;
    }

    public static string T(string english, params object?[] args)
    {
        try { return string.Format(Culture, T(english), args); }
        catch (FormatException) { return string.Format(CultureInfo.InvariantCulture, english, args); }
    }

    /// <summary>Russian plural forms: 1 песня, 2 песни, 5 песен.</summary>
    public static string Count(int n, string one, string few, string many, string englishOne, string englishMany)
    {
        if (Language == English) return $"{n} {(n == 1 ? englishOne : englishMany)}";
        int m10 = n % 10, m100 = n % 100;
        var word = m10 == 1 && m100 != 11 ? one : m10 is >= 2 and <= 4 && (m100 < 12 || m100 > 14) ? few : many;
        return $"{n} {word}";
    }

    public static string Songs(int n) => Count(n, "песня", "песни", "песен", "song", "songs");
}
