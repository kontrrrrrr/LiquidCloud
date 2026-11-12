using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Windows.Threading;

namespace LiquidCloud;

public class SavedTrack
{
    public Track Track { get; set; } = null!;
    public DateTime Added { get; set; } = DateTime.Now;
}

public class HistoryEntry
{
    public Track Track { get; set; } = null!;
    public DateTime At { get; set; } = DateTime.Now;
}

/// <summary>A song you stopped in the middle of (shown in "Continue listening").</summary>
public class PartialEntry
{
    public Track Track { get; set; } = null!;
    public long PositionMs { get; set; }
    public long LengthMs { get; set; }
    public DateTime At { get; set; } = DateTime.Now;
}

/// <summary>A playlist that lives only inside LiquidCloud (saved on this PC).</summary>
public class LocalPlaylist
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Name { get; set; } = "New playlist";
    public string Description { get; set; } = "";
    public string? CoverPath { get; set; }
    public List<SavedTrack> Tracks { get; set; } = new();
    public DateTime Created { get; set; } = DateTime.Now;
    public DateTime Updated { get; set; } = DateTime.Now;
}

public class LibraryData
{
    public List<SavedTrack> Favorites { get; set; } = new();
    public List<HistoryEntry> History { get; set; } = new();
    public List<PartialEntry> Partial { get; set; } = new();
    public List<LocalPlaylist> Playlists { get; set; } = new();
    public List<string> Searches { get; set; } = new();
    public List<string> FavoriteArtists { get; set; } = new();
    public SessionState Session { get; set; } = new();
}

/// <summary>
/// Everything you collect in LiquidCloud: favorites, history, local playlists, recent searches and the last session.
/// Stored in %AppData%\LiquidCloud\library.json. Changes are saved shortly after they happen.
/// </summary>
static class Library
{
    static readonly string FilePath = Path.Combine(AppSettings.Dir, "library.json");
    static DispatcherTimer? saveTimer;
    public static LibraryData Data { get; private set; } = new();

    /// <summary>Raised after any change with a short tag ("favorites", "history", "playlists", "searches").</summary>
    public static event Action<string>? Changed;

    public static string Key(Track t) => t.Source + ":" + t.Id;

    public static void Load()
    {
        try
        {
            if (File.Exists(FilePath)) Data = JsonSerializer.Deserialize<LibraryData>(File.ReadAllText(FilePath)) ?? new();
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            // a broken file shouldn't take the library down: keep a copy (so nothing is lost) and start fresh
            Log.Error("Library", "library.json unreadable; a copy was kept as library.json.broken", ex);
            try { File.Copy(FilePath, FilePath + ".broken", true); }
            catch (IOException copyEx) { Log.Error("Library", "Couldn't keep a copy of the damaged library", copyEx); }
            Data = new();
        }
        Data.Favorites.RemoveAll(f => f.Track == null);
        Data.History.RemoveAll(h => h.Track == null);
        Data.Partial.RemoveAll(p => p.Track == null);
        foreach (var p in Data.Playlists) p.Tracks.RemoveAll(t => t.Track == null);
    }

    public static void Save()
    {
        try
        {
            Directory.CreateDirectory(AppSettings.Dir);
            var tmp = FilePath + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(Data));
            File.Move(tmp, FilePath, true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { Log.Warn("Library", "Couldn't save the library (will retry on the next change)", ex); }
    }

    static void Touch(string what)
    {
        if (saveTimer == null)
        {
            saveTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(800) };
            saveTimer.Tick += (_, _) => { saveTimer!.Stop(); Save(); };
        }
        saveTimer.Stop();
        saveTimer.Start();
        Changed?.Invoke(what);
    }

    // ---- favorites ----
    public static bool IsFavorite(Track t) { var k = Key(t); return Data.Favorites.Any(f => Key(f.Track) == k); }

    /// <summary>Returns true if the song is now a favorite.</summary>
    public static bool ToggleFavorite(Track t)
    {
        var k = Key(t);
        int i = Data.Favorites.FindIndex(f => Key(f.Track) == k);
        if (i >= 0) Data.Favorites.RemoveAt(i);
        else Data.Favorites.Insert(0, new SavedTrack { Track = t });
        Touch("favorites");
        return i < 0;
    }

    public static bool IsFavoriteArtist(string name) => Data.FavoriteArtists.Contains(name, StringComparer.OrdinalIgnoreCase);

    public static bool ToggleFavoriteArtist(string name)
    {
        bool had = Data.FavoriteArtists.RemoveAll(a => a.Equals(name, StringComparison.OrdinalIgnoreCase)) > 0;
        if (!had) Data.FavoriteArtists.Insert(0, name);
        Touch("artists");
        return !had;
    }

    // ---- history ----
    public static void AddHistory(Track t)
    {
        Data.History.Insert(0, new HistoryEntry { Track = t });
        if (Data.History.Count > 600) Data.History.RemoveRange(600, Data.History.Count - 600);
        Touch("history");
    }

    public static void ClearHistory() { Data.History.Clear(); Data.Partial.Clear(); Touch("history"); }

    public static List<Track> RecentlyPlayed(int n = 20) =>
        Data.History.Select(h => h.Track).DistinctBy(Key).Take(n).ToList();

    /// <summary>Songs you've played most in the last 30 days.</summary>
    public static List<Track> Rotation(int n = 16)
    {
        var since = DateTime.Now.AddDays(-30);
        return Data.History.Where(h => h.At >= since)
            .GroupBy(h => Key(h.Track))
            .Where(g => g.Count() >= 2)
            .OrderByDescending(g => g.Count()).ThenByDescending(g => g.Max(x => x.At))
            .Take(n).Select(g => g.First().Track).ToList();
    }

    public static List<string> TopArtists(int n = 6) =>
        Data.History.Take(300).GroupBy(h => h.Track.Artist).OrderByDescending(g => g.Count()).Take(n).Select(g => g.Key).ToList();

    // ---- continue listening ----
    public static void SavePartial(Track t, long pos, long len)
    {
        var k = Key(t);
        Data.Partial.RemoveAll(p => Key(p.Track) == k);
        if (len > 60_000 && pos > 20_000 && pos < len - 20_000)
        {
            Data.Partial.Insert(0, new PartialEntry { Track = t, PositionMs = pos, LengthMs = len });
            if (Data.Partial.Count > 12) Data.Partial.RemoveRange(12, Data.Partial.Count - 12);
        }
        Touch("partial");
    }

    public static long ResumePosition(Track t)
    {
        var k = Key(t);
        return Data.Partial.FirstOrDefault(p => Key(p.Track) == k)?.PositionMs ?? 0;
    }

    // ---- recently added (favorites + local playlists) ----
    public static List<Track> RecentlyAdded(int n = 30) =>
        Data.Favorites.Concat(Data.Playlists.SelectMany(p => p.Tracks))
            .OrderByDescending(s => s.Added).Select(s => s.Track).DistinctBy(Key).Take(n).ToList();

    // ---- searches ----
    public static void AddSearch(string q)
    {
        q = q.Trim();
        if (q.Length == 0) return;
        Data.Searches.RemoveAll(s => s.Equals(q, StringComparison.OrdinalIgnoreCase));
        Data.Searches.Insert(0, q);
        if (Data.Searches.Count > 14) Data.Searches.RemoveRange(14, Data.Searches.Count - 14);
        Touch("searches");
    }

    public static void RemoveSearch(string q) { Data.Searches.Remove(q); Touch("searches"); }
    public static void ClearSearches() { Data.Searches.Clear(); Touch("searches"); }

    // ---- local playlists ----
    public static LocalPlaylist CreatePlaylist(string name, string description = "")
    {
        var p = new LocalPlaylist { Name = string.IsNullOrWhiteSpace(name) ? Loc.T("New playlist") : name.Trim(), Description = description.Trim() };
        Data.Playlists.Insert(0, p);
        Touch("playlists");
        return p;
    }

    public static LocalPlaylist? FindPlaylist(string id) => Data.Playlists.FirstOrDefault(p => p.Id == id);

    public static void DeletePlaylist(LocalPlaylist p) { Data.Playlists.Remove(p); Touch("playlists"); }

    public static void UpdatePlaylist(LocalPlaylist p, string name, string description, string? cover)
    {
        p.Name = string.IsNullOrWhiteSpace(name) ? p.Name : name.Trim();
        p.Description = description.Trim();
        p.CoverPath = cover;
        p.Updated = DateTime.Now;
        Touch("playlists");
    }

    /// <summary>Adds songs that aren't in the playlist yet. Returns how many were added.</summary>
    public static int AddToPlaylist(LocalPlaylist p, IEnumerable<Track> tracks)
    {
        var have = new HashSet<string>(p.Tracks.Select(s => Key(s.Track)));
        int n = 0;
        foreach (var t in tracks)
            if (have.Add(Key(t))) { p.Tracks.Add(new SavedTrack { Track = t }); n++; }
        if (n > 0) { p.Updated = DateTime.Now; Touch("playlists"); }
        return n;
    }

    public static void RemoveFromPlaylist(LocalPlaylist p, int index)
    {
        if (index < 0 || index >= p.Tracks.Count) return;
        p.Tracks.RemoveAt(index);
        p.Updated = DateTime.Now;
        Touch("playlists");
    }

    public static void MoveInPlaylist(LocalPlaylist p, int from, int to)
    {
        if (from < 0 || from >= p.Tracks.Count || to < 0 || to >= p.Tracks.Count || from == to) return;
        var s = p.Tracks[from];
        p.Tracks.RemoveAt(from);
        p.Tracks.Insert(to, s);
        p.Updated = DateTime.Now;
        Touch("playlists");
    }

    public static void SaveSession(SessionState s) { Data.Session = s; Touch("session"); }
}
