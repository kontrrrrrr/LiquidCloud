using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;

namespace GlassPlayer;

/// <summary>
/// Your own SoundCloud: likes, feed and playlists shown as normal lists in Glass.
/// After you sign in once in the SoundCloud panel, Glass keeps SoundCloud's login token encrypted for your
/// Windows user only (%AppData%\LiquidCloud\soundcloud-token.bin) and talks to the same API the website uses.
/// Unofficial: it may break if SoundCloud changes its website.
/// </summary>
class ScAccount
{
    static readonly HttpClient Http = Make();
    static HttpClient Make()
    {
        var h = new HttpClient();
        h.DefaultRequestHeaders.UserAgent.ParseAdd("Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/124.0 Safari/537.36");
        h.DefaultRequestHeaders.Add("Origin", "https://soundcloud.com");
        h.DefaultRequestHeaders.Add("Referer", "https://soundcloud.com/");
        return h;
    }

    static readonly string Store = Path.Combine(AppSettings.Dir, "soundcloud-token.bin");
    public const string PlaylistSource = "SoundCloud Playlist";

    readonly SoundCloud sc;
    long userId;
    public string? Token { get; private set; }
    public string? UserName { get; private set; }
    public string? Avatar { get; private set; }
    public bool SignedIn => Token != null;

    public ScAccount(SoundCloud sc)
    {
        this.sc = sc;
        try { Token = Encoding.UTF8.GetString(ProtectedData.Unprotect(File.ReadAllBytes(Store), null, DataProtectionScope.CurrentUser)); }
        catch { Token = null; }
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
        catch { }
    }

    public void SignOut()
    {
        Token = null; userId = 0; UserName = null; Avatar = null;
        try { File.Delete(Store); } catch { }
    }

    async Task<JsonDocument> GetAsync(string path)
    {
        if (Token == null) throw new Exception("Sign in to SoundCloud first.");
        if (path.StartsWith("https://api-v2.soundcloud.com")) path = path["https://api-v2.soundcloud.com".Length..];
        for (int attempt = 0; ; attempt++)
        {
            var cid = await sc.ClientIdAsync(attempt > 0);
            var url = "https://api-v2.soundcloud.com" + path + (path.Contains('?') ? "&" : "?") + "client_id=" + cid;
            using var req = new HttpRequestMessage(HttpMethod.Get, url);
            req.Headers.TryAddWithoutValidation("Authorization", "OAuth " + Token);
            using var res = await Http.SendAsync(req);
            if (res.StatusCode == HttpStatusCode.Unauthorized)
            {
                if (attempt == 0) continue; // maybe just an old client id
                SignOut();
                throw new Exception("Your SoundCloud sign-in expired. Sign in again.");
            }
            if (!res.IsSuccessStatusCode)
            {
                if (attempt == 0) continue;
                throw new Exception($"SoundCloud error ({(int)res.StatusCode}). Try again in a moment.");
            }
            return JsonDocument.Parse(await res.Content.ReadAsStringAsync());
        }
    }

    static string? Str(JsonElement e, string k) => e.TryGetProperty(k, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    public async Task LoadMeAsync()
    {
        using var d = await GetAsync("/me");
        var e = d.RootElement;
        userId = e.GetProperty("id").GetInt64();
        UserName = Str(e, "username") ?? "SoundCloud user";
        Avatar = Str(e, "avatar_url")?.Replace("-large", "-t200x200");
    }

    async Task<long> UidAsync()
    {
        if (userId == 0) await LoadMeAsync();
        return userId;
    }

    /// <summary>Follows SoundCloud's "next_href" pages until max items.</summary>
    async Task<List<T>> PagedAsync<T>(string path, Func<JsonElement, T?> pick, int max) where T : class
    {
        var list = new List<T>();
        string? next = path;
        while (next != null && list.Count < max)
        {
            using var d = await GetAsync(next);
            foreach (var item in d.RootElement.GetProperty("collection").EnumerateArray())
                if (pick(item) is { } x) list.Add(x);
            next = Str(d.RootElement, "next_href");
        }
        return list;
    }

    static JsonElement Child(JsonElement e, string k) => e.TryGetProperty(k, out var v) ? v : default;

    public async Task<List<Track>> LikesAsync()
    {
        var uid = await UidAsync();
        return await PagedAsync($"/users/{uid}/track_likes?limit=100", e => SoundCloud.ParseTrack(Child(e, "track")), 500);
    }

    public Task<List<Track>> FeedAsync()
        => PagedAsync("/stream?limit=50", e => (Str(e, "type") ?? "").StartsWith("track") ? SoundCloud.ParseTrack(Child(e, "track")) : null, 100);

    static Track? ToPlaylist(JsonElement p)
    {
        if (p.ValueKind != JsonValueKind.Object || !p.TryGetProperty("id", out var id)) return null;
        string? art = Str(p, "artwork_url")?.Replace("-large", "-t500x500");
        int count = p.TryGetProperty("track_count", out var tc) && tc.TryGetInt32(out var n) ? n : 0;
        string owner = p.TryGetProperty("user", out var u) ? Str(u, "username") ?? "" : "";
        return new Track(id.GetRawText(), Str(p, "title") ?? "Playlist", $"{count} songs · {owner}", art, null, PlaylistSource, Str(p, "permalink_url"));
    }

    public async Task<List<Track>> PlaylistsAsync()
    {
        var uid = await UidAsync();
        var own = await PagedAsync($"/users/{uid}/playlists_without_albums?limit=50", ToPlaylist, 200);
        List<Track> liked;
        try { liked = await PagedAsync($"/users/{uid}/playlist_likes?limit=50", e => ToPlaylist(Child(e, "playlist")), 200); }
        catch { liked = new(); }
        return own.Concat(liked.Where(l => own.All(o => o.Id != l.Id))).ToList();
    }

    public async Task<List<Track>> PlaylistTracksAsync(string id)
    {
        using var d = await GetAsync($"/playlists/{id}?representation=full");
        var items = d.RootElement.GetProperty("tracks").EnumerateArray().ToList();
        var result = new Track?[items.Count];
        var missing = new List<(int Index, string Id)>();
        for (int i = 0; i < items.Count; i++)
        {
            result[i] = SoundCloud.ParseTrack(items[i]);
            // big playlists only send ids for most songs; fetch those below
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
        return result.Where(x => x != null).Select(x => x!).ToList();
    }
}
