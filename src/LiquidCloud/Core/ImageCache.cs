using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;

namespace LiquidCloud;

/// <summary>
/// Covers and avatars: memory cache (decoded, per display size) → disk cache (%LocalAppData%\LiquidCloud\images) → network.
/// The same URL is only ever downloaded once at a time, the disk cache is kept under a size limit (oldest first),
/// and images are decoded at the size they're shown.
/// </summary>
public sealed class ImageCache
{
    public static ImageCache Shared { get; set; } = null!;

    readonly string dir;
    readonly Func<string, CancellationToken, Task<byte[]>> fetch;
    readonly Func<bool> offline;
    readonly long maxBytes;
    readonly ConcurrentDictionary<string, Lazy<Task<byte[]?>>> inflight = new();
    readonly Dictionary<string, WeakReference<ImageSource>> memory = new();
    readonly object memGate = new();

    public int Downloads; // for tests and the log

    public ImageCache(string dir, Func<string, CancellationToken, Task<byte[]>> fetch, Func<bool>? offline = null, long maxBytes = 300L * 1024 * 1024)
    {
        this.dir = dir;
        this.fetch = fetch;
        this.offline = offline ?? (() => false);
        this.maxBytes = maxBytes;
    }

    static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(20) };

    /// <summary>The app's cache, downloading over HTTPS.</summary>
    public static ImageCache CreateDefault(string localDir, Func<bool> offline) =>
        new(Path.Combine(localDir, "images"), (url, ct) => Http.GetByteArrayAsync(url, ct), offline);

    public string PathFor(string url) => System.IO.Path.Combine(dir, Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(url)))[..32] + ".img");

    /// <summary>Raw image bytes from disk or network (one download per URL even if many rows ask at once).</summary>
    public async Task<byte[]?> GetBytesAsync(string url, CancellationToken ct)
    {
        if (!url.StartsWith("http", StringComparison.OrdinalIgnoreCase))
            return File.Exists(url) ? await File.ReadAllBytesAsync(url, ct) : null; // a local cover file

        var path = PathFor(url);
        if (File.Exists(path))
        {
            try
            {
                File.SetLastAccessTimeUtc(path, DateTime.UtcNow);
                return await File.ReadAllBytesAsync(path, ct);
            }
            catch (IOException) { /* being written right now: fall through to the shared download */ }
        }
        if (offline()) return null;

        var job = inflight.GetOrAdd(url, u => new Lazy<Task<byte[]?>>(() => DownloadSharedAsync(u, path)));
        return await job.Value.WaitAsync(ct);
    }

    /// <summary>One shared download per URL; forgotten as soon as it finishes (also when every caller gave up waiting).</summary>
    async Task<byte[]?> DownloadSharedAsync(string url, string path)
    {
        try { return await DownloadAsync(url, path); }
        finally { inflight.TryRemove(url, out _); }
    }

    async Task<byte[]?> DownloadAsync(string url, string path)
    {
        try
        {
            Interlocked.Increment(ref Downloads);
            // not tied to one caller's cancellation: other rows may be waiting for the same picture
            var bytes = await fetch(url, CancellationToken.None);
            Directory.CreateDirectory(dir);
            var tmp = path + "." + Guid.NewGuid().ToString("N")[..8] + ".tmp";
            await File.WriteAllBytesAsync(tmp, bytes);
            File.Move(tmp, path, true);
            return bytes;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or IOException)
        {
            Log.Warn("Images", "Download failed: " + url, ex);
            return null;
        }
    }

    /// <summary>A frozen image decoded at the given width, or null if it can't be loaded.</summary>
    public async Task<ImageSource?> GetAsync(string url, int px, CancellationToken ct)
    {
        var key = url + "|" + px;
        lock (memGate)
            if (memory.TryGetValue(key, out var w) && w.TryGetTarget(out var hit)) return hit;

        var bytes = await GetBytesAsync(url, ct);
        if (bytes == null || bytes.Length == 0) return null;
        var img = await Task.Run(() => Decode(bytes, px), ct);
        if (img == null) return null;
        lock (memGate)
        {
            memory[key] = new WeakReference<ImageSource>(img);
            if (memory.Count > 2000)
                foreach (var dead in memory.Where(kv => !kv.Value.TryGetTarget(out _)).Select(kv => kv.Key).ToList()) memory.Remove(dead);
        }
        return img;
    }

    static ImageSource? Decode(byte[] bytes, int px)
    {
        try
        {
            var b = new BitmapImage();
            b.BeginInit();
            b.CacheOption = BitmapCacheOption.OnLoad;
            b.CreateOptions = BitmapCreateOptions.IgnoreColorProfile;
            b.DecodePixelWidth = px;
            b.StreamSource = new MemoryStream(bytes);
            b.EndInit();
            b.Freeze();
            return b;
        }
        catch (Exception ex) when (ex is NotSupportedException or FileFormatException or InvalidOperationException or ArgumentException)
        {
            Log.Warn("Images", "Couldn't decode an image", ex);
            return null;
        }
    }

    public long SizeBytes => Directory.Exists(dir) ? new DirectoryInfo(dir).GetFiles("*.img").Sum(f => f.Length) : 0;

    /// <summary>Keeps the disk cache under its limit by deleting the least recently used pictures.</summary>
    public void Trim()
    {
        if (!Directory.Exists(dir)) return;
        var files = new DirectoryInfo(dir).GetFiles("*.img").OrderBy(f => f.LastAccessTimeUtc).ToList();
        long total = files.Sum(f => f.Length);
        foreach (var f in files)
        {
            if (total <= maxBytes * 8 / 10) break;
            try { total -= f.Length; f.Delete(); }
            catch (IOException) { }
        }
        foreach (var t in new DirectoryInfo(dir).GetFiles("*.tmp").Where(f => f.LastWriteTimeUtc < DateTime.UtcNow.AddHours(-1)))
            try { t.Delete(); } catch (IOException) { }
    }

    public void Clear()
    {
        lock (memGate) memory.Clear();
        if (!Directory.Exists(dir)) return;
        foreach (var f in new DirectoryInfo(dir).GetFiles())
            try { f.Delete(); } catch (IOException) { }
    }
}

/// <summary>
/// XAML helper: ImageLoader.Url="{Binding Art}" on a Border (fills its background), an Image or an Ellipse loads the
/// picture through the cache asynchronously. If the element is reused for another item (virtualized lists), a late
/// answer for the old URL is ignored.
/// </summary>
public static class ImageLoader
{
    public static readonly DependencyProperty UrlProperty = DependencyProperty.RegisterAttached(
        "Url", typeof(string), typeof(ImageLoader), new PropertyMetadata(null, OnUrl));

    public static readonly DependencyProperty SizeProperty = DependencyProperty.RegisterAttached(
        "Size", typeof(int), typeof(ImageLoader), new PropertyMetadata(160));

    public static string? GetUrl(DependencyObject d) => (string?)d.GetValue(UrlProperty);
    public static void SetUrl(DependencyObject d, string? v) => d.SetValue(UrlProperty, v);
    public static int GetSize(DependencyObject d) => (int)d.GetValue(SizeProperty);
    public static void SetSize(DependencyObject d, int v) => d.SetValue(SizeProperty, v);

    static void OnUrl(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        Apply(d, null);
        if (e.NewValue is not string url || url.Length == 0 || ImageCache.Shared == null) return;
        _ = LoadAsync(d, url);
    }

    static async Task LoadAsync(DependencyObject d, string url)
    {
        try
        {
            var img = await ImageCache.Shared.GetAsync(url, GetSize(d), CancellationToken.None);
            if (img != null && GetUrl(d) == url) Apply(d, img);
        }
        catch (Exception ex) { Log.Warn("Images", "Load " + url, ex); }
    }

    static void Apply(DependencyObject d, ImageSource? img)
    {
        var brush = img == null ? null : new ImageBrush(img) { Stretch = Stretch.UniformToFill };
        switch (d)
        {
            case Image i: i.Source = img; break;
            case Border b: b.Background = brush; break;
            case Shape s: s.Fill = brush; break;
        }
    }
}
