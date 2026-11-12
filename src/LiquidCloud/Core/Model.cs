using System.Collections.Generic;

namespace LiquidCloud;

/// <summary>A song from any source. Source is "SoundCloud", "YouTube Music", "Spotify" or "Local".</summary>
public record Track(string Id, string Title, string Artist, string? Art, string? Preview, string Source, string? OpenUri, int DurationMs = 0)
{
    public string DurationText => DurationMs > 0 ? $"{DurationMs / 60000}:{DurationMs / 1000 % 60:00}" : "";

    /// <summary>Identity across sources (the same id can exist on two services).</summary>
    public string Key => Source + ":" + Id;
}

/// <summary>The queue and position saved between launches.</summary>
public class SessionState
{
    public List<Track> Queue { get; set; } = new();
    public int Index { get; set; } = -1;
    public long PositionMs { get; set; }
    public bool Shuffle { get; set; }
    public int Repeat { get; set; }
    public string QueueName { get; set; } = "";
}
