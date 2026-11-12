using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Windows.Media.Imaging;

namespace GlassPlayer;

public record LyricLine(int TimeMs, string Text);

/// <summary>
/// Reads lyrics and cover art straight from the audio file (ID3/MP4/FLAC tags),
/// or from a .lrc file with the same name next to it. Timed (LRC) lyrics follow the song.
/// </summary>
static class LyricsReader
{
    static readonly Regex Stamp = new(@"\[(\d{1,3}):(\d{1,2}(?:[.:]\d{1,3})?)\]", RegexOptions.Compiled);
    static readonly Regex WordStamp = new(@"<\d{1,3}:\d{1,2}(?:[.:]\d{1,3})?>", RegexOptions.Compiled);
    static readonly Regex Meta = new(@"^\[[a-zA-Z#]+:.*\]$", RegexOptions.Compiled);

    public static (List<LyricLine> Lines, bool Synced, BitmapSource? Cover) Read(string path, int coverPx)
    {
        string? text = null;
        BitmapSource? cover = null;
        try
        {
            var lrc = Path.ChangeExtension(path, ".lrc");
            if (File.Exists(lrc)) text = File.ReadAllText(lrc);
        }
        catch { }
        try
        {
            using var f = TagLib.File.Create(path);
            if (string.IsNullOrWhiteSpace(text)) text = f.Tag.Lyrics;
            var pic = f.Tag.Pictures?.FirstOrDefault();
            if (pic?.Data?.Data is { Length: > 0 } bytes) cover = Decode(bytes, coverPx);
        }
        catch { }
        var lines = Parse(text ?? "", out bool synced);
        return (lines, synced, cover);
    }

    static BitmapSource? Decode(byte[] bytes, int px)
    {
        try
        {
            var b = new BitmapImage();
            b.BeginInit();
            b.CacheOption = BitmapCacheOption.OnLoad;
            b.DecodePixelWidth = px; // never keep a full-size cover in memory
            b.StreamSource = new MemoryStream(bytes);
            b.EndInit();
            b.Freeze();
            return b;
        }
        catch { return null; }
    }

    public static List<LyricLine> Parse(string text, out bool synced)
    {
        var timed = new List<LyricLine>();
        var plain = new List<LyricLine>();
        foreach (var raw in text.Replace("\r", "").Split('\n'))
        {
            var ms = Stamp.Matches(raw);
            if (ms.Count > 0)
            {
                var body = WordStamp.Replace(Stamp.Replace(raw, ""), "").Trim();
                foreach (Match m in ms) timed.Add(new LyricLine(ToMs(m), body));
            }
            else if (!Meta.IsMatch(raw.Trim())) plain.Add(new LyricLine(0, raw.Trim()));
        }
        synced = timed.Count > 0;
        if (synced) return timed.OrderBy(l => l.TimeMs).ToList();
        while (plain.Count > 0 && plain[0].Text.Length == 0) plain.RemoveAt(0);
        while (plain.Count > 0 && plain[^1].Text.Length == 0) plain.RemoveAt(plain.Count - 1);
        return plain;
    }

    static int ToMs(Match m)
    {
        int min = int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture);
        double sec = double.Parse(m.Groups[2].Value.Replace(':', '.'), CultureInfo.InvariantCulture);
        return (int)((min * 60 + sec) * 1000);
    }
}

/// <summary>Loads small thumbnails for the song list so a long list doesn't eat memory.</summary>
public class ArtConverter : System.Windows.Data.IValueConverter
{
    public object? Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value is not string s || s.Length == 0) return null;
        try
        {
            var b = new BitmapImage();
            b.BeginInit();
            b.UriSource = new Uri(s);
            b.DecodePixelWidth = 92;
            b.EndInit();
            return b;
        }
        catch { return null; }
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => throw new NotSupportedException();
}
