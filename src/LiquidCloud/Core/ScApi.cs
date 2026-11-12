using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace LiquidCloud;

public record ScUser(string Id, string Name, string? Avatar, int Followers, int TrackCount, string? Url, string? City);

/// <summary>A SoundCloud playlist or album.</summary>
public record ScSet(string Id, string Title, string Owner, string OwnerId, string? Art, int Count, string? Url, bool IsAlbum);

public record Page<T>(List<T> Items, string? Next);

/// <summary>
/// Talks to SoundCloud's web API (the same one soundcloud.com uses). Read-only calls work without an account;
/// likes and playlist changes use your signed-in account. Unofficial: SoundCloud may change it at any time.
/// </summary>
static class ScApi
{
    const string Base = "https://api-v2.soundcloud.com";
    static readonly HttpClient Http = Make();
    static HttpClient Make()
    {
        var h = new HttpClient { Timeout = TimeSpan.FromSeconds(20) };
        h.DefaultRequestHeaders.UserAgent.ParseAdd("Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/124.0 Safari/537.36");
        h.DefaultRequestHeaders.Add("Origin", "https://soundcloud.com");
        h.DefaultRequestHeaders.Add("Referer", "https://soundcloud.com/");
        return h;
    }

    public static SoundCloud Provider = null!;
    public static ScAccount Account = null!;

    public static void Init(SoundCloud provider)
    {
        Provider = provider;
        Account = new ScAccount();
    }

    // ---- low level ----
    /// <summary>Cancellation for the page that's currently loading; set by the page loader so leaving a page stops its requests.</summary>
    public static readonly AsyncLocal<CancellationToken> Ambient = new();

    static readonly ConcurrentDictionary<string, (string Body, DateTime At)> Responses = new();
    static readonly ConcurrentDictionary<string, Lazy<Task<string>>> InFlight = new();
    static readonly TimeSpan CacheFor = TimeSpan.FromMinutes(3);

    /// <summary>
    /// GET with: offline check, a short response cache (the same page/search isn't fetched twice in a row),
    /// one shared request when several parts of the UI ask for the same thing, cancellation, retry with a fresh client id,
    /// and logging (secrets masked).
    /// </summary>
    public static async Task<JsonDocument> GetAsync(string path, bool auth = false, CancellationToken ct = default)
    {
        AppEnv.EnsureOnline();
        if (!ct.CanBeCanceled) ct = Ambient.Value;
        if (path.StartsWith(Base)) path = path[Base.Length..];
        bool withAuth = (auth || Account.SignedIn) && Account.Token != null;
        string key = (withAuth ? "a:" : "") + path;

        if (!auth && Responses.TryGetValue(key, out var hit) && DateTime.UtcNow - hit.At < CacheFor)
            return JsonDocument.Parse(hit.Body);

        var job = InFlight.GetOrAdd(key, _ => new Lazy<Task<string>>(() => FetchAsync(path, withAuth, auth)));
        string body;
        try { body = await job.Value.WaitAsync(ct); }
        finally { if (job.Value.IsCompleted) InFlight.TryRemove(key, out _); }

        if (!auth)
        {
            Responses[key] = (body, DateTime.UtcNow);
            if (Responses.Count > 400)
                foreach (var old in Responses.Where(r => DateTime.UtcNow - r.Value.At > CacheFor).Select(r => r.Key).ToList()) Responses.TryRemove(old, out _);
        }
        return JsonDocument.Parse(body);
    }

    static async Task<string> FetchAsync(string path, bool withAuth, bool authRequired)
    {
        for (int attempt = 0; ; attempt++)
        {
            var cid = await Provider.ClientIdAsync(attempt > 0);
            var url = Base + path + (path.Contains('?') ? "&" : "?") + "client_id=" + cid;
            using var req = new HttpRequestMessage(HttpMethod.Get, url);
            if (withAuth) req.Headers.TryAddWithoutValidation("Authorization", "OAuth " + Account.Token);
            using var res = await Http.SendAsync(req);
            if (res.IsSuccessStatusCode) return await res.Content.ReadAsStringAsync();

            Log.Warn("SoundCloud", $"GET {path} -> {(int)res.StatusCode} (attempt {attempt + 1})");
            if (res.StatusCode == HttpStatusCode.TooManyRequests && attempt == 0)
            {
                var wait = res.Headers.RetryAfter?.Delta ?? TimeSpan.FromSeconds(2);
                await Task.Delay(wait > TimeSpan.FromSeconds(5) ? TimeSpan.FromSeconds(5) : wait);
                continue;
            }
            if (res.StatusCode == HttpStatusCode.Unauthorized && authRequired && attempt > 0)
            {
                Account.SignOut();
                throw new InvalidOperationException(Loc.T("Your SoundCloud sign-in expired. Sign in again."));
            }
            if (attempt == 0 && res.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden) continue; // often just an old client id
            throw res.StatusCode switch
            {
                HttpStatusCode.NotFound => new Exception(Loc.T("SoundCloud doesn't have this anymore.")),
                HttpStatusCode.TooManyRequests => new Exception(Loc.T("SoundCloud asks to slow down. Try again in a minute.")),
                >= HttpStatusCode.InternalServerError => new Exception(Loc.T("SoundCloud is having problems right now. Try again later.")),
                _ => new Exception(Loc.T("SoundCloud error ({0}).", (int)res.StatusCode))
            };
        }
    }

    /// <summary>Forget cached answers (after you change something on SoundCloud).</summary>
    public static void ForgetCache() => Responses.Clear();

    /// <summary>Changes on your account (likes, playlists). Throws with SoundCloud's answer if it refuses.</summary>
    public static async Task<JsonDocument?> SendAsync(HttpMethod method, string path, object? body = null)
    {
        AppEnv.EnsureOnline();
        if (Account.Token == null) throw new InvalidOperationException(Loc.T("Sign in to SoundCloud first."));
        ForgetCache();
        var cid = await Provider.ClientIdAsync();
        var url = Base + path + (path.Contains('?') ? "&" : "?") + "client_id=" + cid;
        using var req = new HttpRequestMessage(method, url);
        req.Headers.TryAddWithoutValidation("Authorization", "OAuth " + Account.Token);
        if (body != null) req.Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json");
        using var res = await Http.SendAsync(req);
        var text = await res.Content.ReadAsStringAsync();
        if (!res.IsSuccessStatusCode) Log.Warn("SoundCloud", $"{method} {path} -> {(int)res.StatusCode}");
        if (res.StatusCode == HttpStatusCode.Unauthorized) throw new InvalidOperationException(Loc.T("SoundCloud didn't accept your sign-in. Sign in again."));
        if (!res.IsSuccessStatusCode) throw new InvalidOperationException(Loc.T("SoundCloud refused the change ({0}).", (int)res.StatusCode));
        try { return text.Length > 0 ? JsonDocument.Parse(text) : null; }
        catch (JsonException) { return null; } // some changes answer with an empty or non-JSON body
    }

    public static string? Str(JsonElement e, string k) => e.ValueKind == JsonValueKind.Object && e.TryGetProperty(k, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
    static int Int(JsonElement e, string k) => e.ValueKind == JsonValueKind.Object && e.TryGetProperty(k, out var v) && v.TryGetInt32(out var n) ? n : 0;
    static JsonElement Child(JsonElement e, string k) => e.ValueKind == JsonValueKind.Object && e.TryGetProperty(k, out var v) ? v : default;
    static string Q(string s) => Uri.EscapeDataString(s);

    static string? Big(string? art) => art?.Replace("-large", "-t500x500");

    public static ScUser? ParseUser(JsonElement u)
    {
        if (u.ValueKind != JsonValueKind.Object || !u.TryGetProperty("id", out var id)) return null;
        return new ScUser(id.GetRawText(), Str(u, "username") ?? Loc.T("Artist"), Str(u, "avatar_url")?.Replace("-large", "-t300x300"),
            Int(u, "followers_count"), Int(u, "track_count"), Str(u, "permalink_url"), Str(u, "city"));
    }

    public static ScSet? ParseSet(JsonElement p)
    {
        if (p.ValueKind != JsonValueKind.Object || !p.TryGetProperty("id", out var id)) return null;
        var user = Child(p, "user");
        string? art = Str(p, "artwork_url");
        if (art == null && p.TryGetProperty("tracks", out var tr) && tr.ValueKind == JsonValueKind.Array)
            art = tr.EnumerateArray().Select(t => Str(t, "artwork_url")).FirstOrDefault(a => a != null);
        art ??= Str(user, "avatar_url");
        bool album = p.TryGetProperty("is_album", out var ia) && ia.ValueKind == JsonValueKind.True;
        return new ScSet(id.GetRawText(), Str(p, "title") ?? "Playlist", Str(user, "username") ?? "", user.ValueKind == JsonValueKind.Object && user.TryGetProperty("id", out var uid) ? uid.GetRawText() : "",
            Big(art), Int(p, "track_count"), Str(p, "permalink_url"), album);
    }

    static async Task<Page<T>> PageAsync<T>(string path, Func<JsonElement, T?> pick, bool auth = false) where T : class
    {
        using var d = await GetAsync(path, auth);
        var list = new List<T>();
        foreach (var item in d.RootElement.GetProperty("collection").EnumerateArray())
            if (pick(item) is { } x) list.Add(x);
        return new Page<T>(list, Str(d.RootElement, "next_href"));
    }

    static async Task<List<T>> AllPagesAsync<T>(string path, Func<JsonElement, T?> pick, int max, bool auth = false) where T : class
    {
        var all = new List<T>();
        string? next = path;
        while (next != null && all.Count < max)
        {
            var p = await PageAsync(next, pick, auth);
            all.AddRange(p.Items);
            next = p.Items.Count == 0 ? null : p.Next;
        }
        return all;
    }

    // ---- search ----
    public static Task<Page<Track>> SearchTracksAsync(string q, string? next = null, string extra = "") =>
        PageAsync(next ?? $"/search/tracks?q={Q(q)}&limit=30{extra}", SoundCloud.ParseTrack);

    public static Task<Page<ScUser>> SearchUsersAsync(string q, string? next = null) =>
        PageAsync(next ?? $"/search/users?q={Q(q)}&limit=24", ParseUser);

    public static Task<Page<ScSet>> SearchPlaylistsAsync(string q, string? next = null) =>
        PageAsync(next ?? $"/search/playlists_without_albums?q={Q(q)}&limit=24", ParseSet);

    public static Task<Page<ScSet>> SearchAlbumsAsync(string q, string? next = null) =>
        PageAsync(next ?? $"/search/albums?q={Q(q)}&limit=24", ParseSet);

    /// <summary>Search suggestions while typing.</summary>
    public static async Task<List<string>> SuggestAsync(string q)
    {
        using var d = await GetAsync($"/search/queries?q={Q(q)}&limit=8");
        return d.RootElement.GetProperty("collection").EnumerateArray()
            .Select(e => Str(e, "output") ?? Str(e, "query")).Where(s => !string.IsNullOrWhiteSpace(s)).Select(s => s!).Distinct().ToList();
    }

    // ---- discovery ----
    public static readonly (string Name, string Tag, string Color1, string Color2)[] Genres =
    {
        ("Hip-hop & Rap", "hip-hop", "#FF6A3D", "#8B1E3F"), ("Electronic", "electronic", "#38D1FF", "#2E3BFF"),
        ("House", "house", "#7A5CFF", "#00C2FF"), ("Lo-fi", "lofi", "#9DB4FF", "#35467A"),
        ("Pop", "pop", "#FF6FB5", "#7B3FE4"), ("R&B & Soul", "r&b", "#C86BFF", "#3A1C71"),
        ("Rock", "rock", "#FF453A", "#3A0A0A"), ("Techno", "techno", "#00E0B8", "#0A2A3A"),
        ("Drum & Bass", "drum & bass", "#FFD60A", "#C2410C"), ("Phonk", "phonk", "#B026FF", "#1A0033"),
        ("Ambient", "ambient", "#8BE3FF", "#123247"), ("Indie", "indie", "#FFB01F", "#5B3A00"),
        ("Jazz", "jazz", "#E8B86D", "#3D2B12"), ("Classical", "classical", "#E6F7FF", "#4A5A6A"),
        ("Dubstep", "dubstep", "#30D158", "#0A3D1A"), ("Trap", "trap", "#FF2D55", "#2D0A12"),
    };

    /// <summary>
    /// Popular tracks for a genre. Tries SoundCloud's charts first; if they aren't available,
    /// falls back to a real SoundCloud search for that genre.
    /// </summary>
    public static async Task<List<Track>> PopularAsync(string tag)
    {
        try
        {
            var chartGenre = Q("soundcloud:genres:" + (tag == "all" ? "all-music" : tag.Replace(" & ", "").Replace("&", "").Replace(" ", "")));
            var p = await PageAsync($"/charts?kind=trending&genre={chartGenre}&limit=30", e => SoundCloud.ParseTrack(Child(e, "track")));
            if (p.Items.Count > 0) return p.Items;
        }
        catch (Exception ex) when (ex is not OperationCanceledException and not OfflineException)
        {
            // SoundCloud's charts aren't always available; a real genre search is the fallback
            Log.Info("SoundCloud", $"Charts unavailable for {tag}, using genre search: {ex.Message}");
        }
        var q = tag == "all" ? "music" : tag;
        var s = await SearchTracksAsync(q, null, $"&filter.genre_or_tag={Q(q)}");
        if (s.Items.Count == 0) s = await SearchTracksAsync(q);
        return s.Items.OrderByDescending(t => t.DurationMs > 0).ToList();
    }

    /// <summary>Tracks uploaded in the last week for a genre (real search with SoundCloud's date filter).</summary>
    public static async Task<List<Track>> NewReleasesAsync(string tag)
    {
        var p = await SearchTracksAsync(tag, null, $"&filter.created_at=last_week&filter.genre_or_tag={Q(tag)}");
        if (p.Items.Count == 0) p = await SearchTracksAsync(tag, null, "&filter.created_at=last_week");
        return p.Items;
    }

    public static async Task<List<Track>> RelatedAsync(string trackId) =>
        (await PageAsync($"/tracks/{trackId}/related?limit=30", SoundCloud.ParseTrack)).Items;

    // ---- artists ----
    public static async Task<ScUser?> UserAsync(string id)
    {
        using var d = await GetAsync($"/users/{id}");
        return ParseUser(d.RootElement);
    }

    public static async Task<ScUser?> ResolveUserAsync(string permalinkUrl)
    {
        using var d = await GetAsync($"/resolve?url={Q(permalinkUrl)}");
        return ParseUser(d.RootElement);
    }

    public static Task<Page<Track>> UserTracksAsync(string id, string? next = null) =>
        PageAsync(next ?? $"/users/{id}/tracks?limit=30", SoundCloud.ParseTrack);

    public static async Task<List<Track>> UserTopTracksAsync(string id) =>
        (await PageAsync($"/users/{id}/toptracks?limit=10", SoundCloud.ParseTrack)).Items;

    public static async Task<List<ScSet>> UserSetsAsync(string id)
    {
        var sets = new List<ScSet>();
        try { sets.AddRange((await PageAsync($"/users/{id}/albums?limit=20", ParseSet)).Items); }
        catch (Exception ex) when (ex is not OperationCanceledException and not OfflineException) { Log.Info("SoundCloud", "Albums unavailable: " + ex.Message); }
        try { sets.AddRange((await PageAsync($"/users/{id}/playlists_without_albums?limit=20", ParseSet)).Items); }
        catch (Exception ex) when (ex is not OperationCanceledException and not OfflineException) { Log.Info("SoundCloud", "Playlists unavailable: " + ex.Message); }
        return sets;
    }

    /// <summary>Finds the SoundCloud artist behind a track (for the artist page).</summary>
    public static async Task<ScUser?> ArtistOfAsync(Track t)
    {
        if (t.Source != "SoundCloud") return null;
        using var d = await GetAsync(long.TryParse(t.Id, out _) ? $"/tracks/{t.Id}" : $"/resolve?url={Q(t.OpenUri ?? "")}");
        return ParseUser(Child(d.RootElement, "user"));
    }

    // ---- playlists ----
    public static async Task<(ScSet? Set, List<Track> Tracks)> SetAsync(string id)
    {
        using var d = await GetAsync($"/playlists/{id}?representation=full");
        var set = ParseSet(d.RootElement);
        var items = d.RootElement.GetProperty("tracks").EnumerateArray().ToList();
        var result = new Track?[items.Count];
        var missing = new List<(int Index, string Id)>();
        for (int i = 0; i < items.Count; i++)
        {
            result[i] = SoundCloud.ParseTrack(items[i]);
            // big playlists only send ids for most songs; fetch those in batches
            if (result[i] == null && !items[i].TryGetProperty("title", out _) && items[i].TryGetProperty("id", out var tid))
                missing.Add((i, tid.GetRawText()));
        }
        foreach (var chunk in missing.Chunk(50))
        {
            using var t = await GetAsync("/tracks?ids=" + string.Join(",", chunk.Select(c => c.Id)));
            var byId = new Dictionary<string, Track>();
            foreach (var e in t.RootElement.EnumerateArray())
                if (SoundCloud.ParseTrack(e) is { } tr) byId[tr.Id] = tr;
            foreach (var c in chunk)
                if (byId.TryGetValue(c.Id, out var tr)) result[c.Index] = tr;
        }
        return (set, result.Where(x => x != null).Select(x => x!).ToList());
    }

    /// <summary>All track ids of a playlist in order (including ones that can't be played here), for safe editing.</summary>
    public static async Task<List<long>> SetTrackIdsAsync(string id)
    {
        // read the raw id of every entry, so songs that can't be played here are never dropped from your playlist
        using var d = await GetAsync($"/playlists/{id}", true);
        return d.RootElement.GetProperty("tracks").EnumerateArray()
            .Select(t => t.ValueKind == JsonValueKind.Number ? t.GetInt64() : t.GetProperty("id").GetInt64()).ToList();
    }

    // ---- your account ----
    public static async Task<List<Track>> MyLikesAsync()
    {
        var uid = await Account.UserIdAsync();
        return await AllPagesAsync($"/users/{uid}/track_likes?limit=100", e => SoundCloud.ParseTrack(Child(e, "track")), 500, true);
    }

    public static async Task<List<Track>> MyFeedAsync() =>
        await AllPagesAsync("/stream?limit=50", e => (Str(e, "type") ?? "").StartsWith("track") ? SoundCloud.ParseTrack(Child(e, "track")) : null, 100, true);

    /// <summary>Your playlists (editable) and playlists you liked (read-only).</summary>
    public static async Task<List<(ScSet Set, bool Mine)>> MySetsAsync()
    {
        var uid = await Account.UserIdAsync();
        var own = await AllPagesAsync($"/users/{uid}/playlists_without_albums?limit=50", ParseSet, 200, true);
        List<ScSet> liked;
        try { liked = await AllPagesAsync($"/users/{uid}/playlist_likes?limit=50", e => ParseSet(Child(e, "playlist")), 200, true); }
        catch (Exception ex) when (ex is not OperationCanceledException and not OfflineException)
        {
            Log.Info("SoundCloud", "Liked playlists unavailable: " + ex.Message);
            liked = new();
        }
        return own.Select(s => (s, true)).Concat(liked.Where(l => own.All(o => o.Id != l.Id)).Select(s => (s, false))).ToList();
    }

    public static async Task SetLikeAsync(Track t, bool like)
    {
        var uid = await Account.UserIdAsync();
        await SendAsync(like ? HttpMethod.Put : HttpMethod.Delete, $"/users/{uid}/track_likes/{t.Id}");
    }

    /// <summary>Replaces the track list of one of your SoundCloud playlists.</summary>
    public static Task SetPlaylistTracksAsync(string setId, IEnumerable<long> trackIds) =>
        SendAsync(HttpMethod.Put, $"/playlists/{setId}", new { playlist = new { tracks = trackIds.ToArray() } });

    public static async Task AddToScPlaylistAsync(string setId, IEnumerable<Track> tracks)
    {
        var ids = await SetTrackIdsAsync(setId);
        foreach (var t in tracks)
            if (long.TryParse(t.Id, out var id) && !ids.Contains(id)) ids.Add(id);
        await SetPlaylistTracksAsync(setId, ids);
    }

    public static async Task RemoveFromScPlaylistAsync(string setId, Track t)
    {
        var ids = await SetTrackIdsAsync(setId);
        if (long.TryParse(t.Id, out var id)) ids.Remove(id);
        await SetPlaylistTracksAsync(setId, ids);
    }

    public static async Task<ScSet?> CreateScPlaylistAsync(string title, IEnumerable<Track> tracks)
    {
        var ids = tracks.Select(t => long.TryParse(t.Id, out var id) ? id : 0).Where(id => id > 0).ToArray();
        using var d = await SendAsync(HttpMethod.Post, "/playlists", new { playlist = new { title, sharing = "private", tracks = ids } });
        return d == null ? null : ParseSet(d.RootElement);
    }
}

/// <summary>
/// Your SoundCloud sign-in. After you sign in once in the SoundCloud panel, LiquidCloud keeps SoundCloud's login token
/// encrypted for your Windows user only (%AppData%\LiquidCloud\soundcloud-token.bin).
/// </summary>
class ScAccount
{
    static readonly string Store = Path.Combine(AppSettings.Dir, "soundcloud-token.bin");
    long userId;
    public string? Token { get; private set; }
    public string? UserName { get; private set; }
    public string? Avatar { get; private set; }
    public bool SignedIn => Token != null;
    public event Action? Changed;

    public ScAccount()
    {
        if (!File.Exists(Store)) return;
        try { Token = Encoding.UTF8.GetString(ProtectedData.Unprotect(File.ReadAllBytes(Store), null, DataProtectionScope.CurrentUser)); }
        catch (Exception ex) when (ex is CryptographicException or IOException)
        {
            Log.Warn("SoundCloud", "Saved sign-in can't be read; sign in again", ex);
            Token = null;
        }
    }

    public void Save(string token)
    {
        Token = token;
        userId = 0;
        try
        {
            Directory.CreateDirectory(AppSettings.Dir);
            File.WriteAllBytes(Store, ProtectedData.Protect(Encoding.UTF8.GetBytes(token), null, DataProtectionScope.CurrentUser));
        }
        catch (Exception ex) when (ex is CryptographicException or IOException or UnauthorizedAccessException)
        {
            Log.Warn("SoundCloud", "Couldn't save the sign-in (you'll stay signed in until you close LiquidCloud)", ex);
        }
        Changed?.Invoke();
    }

    public void SignOut()
    {
        Token = null; userId = 0; UserName = null; Avatar = null;
        try { File.Delete(Store); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { Log.Warn("SoundCloud", "Couldn't delete the saved sign-in", ex); }
        Changed?.Invoke();
    }

    public async Task LoadMeAsync()
    {
        using var d = await ScApi.GetAsync("/me", true);
        var e = d.RootElement;
        userId = e.GetProperty("id").GetInt64();
        UserName = ScApi.Str(e, "username") ?? Loc.T("SoundCloud user");
        Avatar = ScApi.Str(e, "avatar_url")?.Replace("-large", "-t200x200");
        Changed?.Invoke();
    }

    public async Task<long> UserIdAsync()
    {
        if (Token == null) throw new InvalidOperationException(Loc.T("Sign in to SoundCloud first."));
        if (userId == 0) await LoadMeAsync();
        return userId;
    }
}
