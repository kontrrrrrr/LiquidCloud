using System.Net.Http;

namespace LiquidCloud.Tests;

public class ImageCacheTests : IDisposable
{
    readonly string dir = Path.Combine(TestSetup.Root, "images-" + Guid.NewGuid().ToString("N"));
    public void Dispose() { try { Directory.Delete(dir, true); } catch (IOException) { } }

    // a valid 1×1 PNG
    static readonly byte[] Png = Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mNk+M9QDwADhgGAWjR9awAAAABJRU5ErkJggg==");

    [Fact]
    public async Task Concurrent_requests_for_one_url_download_once()
    {
        var gate = new TaskCompletionSource<bool>();
        int fetches = 0;
        var cache = new ImageCache(dir, async (u, ct) => { Interlocked.Increment(ref fetches); await gate.Task; return Png; });
        var jobs = Enumerable.Range(0, 10).Select(_ => cache.GetBytesAsync("https://x/a.png", CancellationToken.None)).ToList();
        gate.SetResult(true);
        var results = await Task.WhenAll(jobs);
        Assert.Equal(1, fetches);
        Assert.All(results, r => Assert.Equal(Png, r));
    }

    [Fact]
    public async Task Second_run_reads_from_disk()
    {
        int fetches = 0;
        Func<string, CancellationToken, Task<byte[]>> f = (u, ct) => { fetches++; return Task.FromResult(Png); };
        await new ImageCache(dir, f).GetBytesAsync("https://x/b.png", CancellationToken.None);
        var again = await new ImageCache(dir, f).GetBytesAsync("https://x/b.png", CancellationToken.None); // a fresh start of the app
        Assert.Equal(1, fetches);
        Assert.Equal(Png, again);
        Assert.True(File.Exists(new ImageCache(dir, f).PathFor("https://x/b.png")));
    }

    [Fact]
    public async Task Offline_never_hits_network_but_uses_disk()
    {
        int fetches = 0;
        bool offline = false;
        var cache = new ImageCache(dir, (u, ct) => { fetches++; return Task.FromResult(Png); }, () => offline);
        await cache.GetBytesAsync("https://x/c.png", CancellationToken.None);
        offline = true;
        Assert.Null(await cache.GetBytesAsync("https://x/new.png", CancellationToken.None));
        Assert.Equal(Png, await cache.GetBytesAsync("https://x/c.png", CancellationToken.None));
        Assert.Equal(1, fetches);
    }

    [Fact]
    public async Task Failed_download_is_retried_next_time()
    {
        int calls = 0;
        var cache = new ImageCache(dir, (u, ct) =>
            ++calls == 1 ? Task.FromException<byte[]>(new HttpRequestException("down")) : Task.FromResult(Png));
        Assert.Null(await cache.GetBytesAsync("https://x/d.png", CancellationToken.None));
        Assert.Equal(Png, await cache.GetBytesAsync("https://x/d.png", CancellationToken.None));
        Assert.Equal(2, calls);
    }

    [Fact]
    public async Task Cancelled_caller_does_not_break_other_callers()
    {
        var gate = new TaskCompletionSource<bool>();
        var cache = new ImageCache(dir, async (u, ct) => { await gate.Task; return Png; });
        using var cts = new CancellationTokenSource();
        var cancelled = cache.GetBytesAsync("https://x/e.png", cts.Token);
        var waiting = cache.GetBytesAsync("https://x/e.png", CancellationToken.None);
        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => cancelled);
        gate.SetResult(true);
        Assert.Equal(Png, await waiting);
    }

    [Fact]
    public async Task Decodes_to_a_frozen_image()
    {
        var cache = new ImageCache(dir, (u, ct) => Task.FromResult(Png));
        var img = await cache.GetAsync("https://x/f.png", 64, CancellationToken.None);
        Assert.NotNull(img);
        Assert.True(img!.IsFrozen);
        Assert.Same(img, await cache.GetAsync("https://x/f.png", 64, CancellationToken.None)); // memory hit
    }

    [Fact]
    public async Task Garbage_bytes_give_null_not_a_crash()
    {
        var cache = new ImageCache(dir, (u, ct) => Task.FromResult(new byte[] { 1, 2, 3 }));
        Assert.Null(await cache.GetAsync("https://x/g.png", 64, CancellationToken.None));
    }

    [Fact]
    public async Task Trim_keeps_cache_under_limit_oldest_first()
    {
        var big = new byte[1000];
        var cache = new ImageCache(dir, (u, ct) => Task.FromResult(big), maxBytes: 5000);
        for (int i = 0; i < 10; i++)
        {
            await cache.GetBytesAsync($"https://x/{i}.png", CancellationToken.None);
            File.SetLastAccessTimeUtc(cache.PathFor($"https://x/{i}.png"), DateTime.UtcNow.AddMinutes(i - 100));
        }
        Assert.Equal(10_000, cache.SizeBytes);
        cache.Trim();
        Assert.True(cache.SizeBytes <= 4000);
        Assert.False(File.Exists(cache.PathFor("https://x/0.png")));
        Assert.True(File.Exists(cache.PathFor("https://x/9.png")));
        cache.Clear();
        Assert.Equal(0, cache.SizeBytes);
    }
}
