using System.Collections.Concurrent;

namespace LiquidCloud.Tests;

static class T
{
    public static Track Make(int i, string src = "test") => new($"id{i}", $"Song {i}", "Artist", null, null, src, null, 180_000);
    public static List<Track> Many(int n) => Enumerable.Range(0, n).Select(i => Make(i)).ToList();
}

/// <summary>Plays nothing; records what the player asked for and lets a test raise Ended / Failed.</summary>
sealed class FakeOutput : IAudioOutput
{
    public event Action? Ended;
    public event Action<string>? Failed;
    public readonly List<string> Opened = new();
    public bool Paused, Stopped;
    public long TimeMs { get; set; }
    public long LengthMs { get; set; }
    public double Volume { get; set; } = 0.8;
    public void Open(string url) { Opened.Add(url); Stopped = false; Paused = false; TimeMs = 0; }
    public void Stop() => Stopped = true;
    public void SetPaused(bool p) => Paused = p;
    public void RaiseEnded() => Ended?.Invoke();
    public void RaiseFailed(string m) => Failed?.Invoke(m);
}

/// <summary>Resolves "testN" to "url:idN". Tracks can be made slow (wait for a gate) or broken.</summary>
sealed class FakeResolver : ITrackResolver
{
    public readonly ConcurrentDictionary<string, TaskCompletionSource<bool>> Gates = new();
    public readonly HashSet<string> Broken = new();
    public readonly HashSet<string> Unplayable = new();
    public int Calls;

    public async Task<string?> ResolveAsync(Track track, CancellationToken ct)
    {
        Interlocked.Increment(ref Calls);
        if (Gates.TryGetValue(track.Id, out var gate)) await gate.Task.WaitAsync(ct);
        if (Broken.Contains(track.Id)) throw new InvalidOperationException("broken " + track.Id);
        if (Unplayable.Contains(track.Id)) return null;
        return "url:" + track.Id;
    }
}
