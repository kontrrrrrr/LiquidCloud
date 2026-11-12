namespace LiquidCloud.Tests;

public class PlayerServiceTests
{
    DateTime clock = new(2026, 1, 1);
    readonly FakeOutput audio = new();
    readonly FakeResolver resolver = new();

    PlayerService Make()
    {
        var p = new PlayerService(audio, resolver, () => clock) { SkipDelay = TimeSpan.Zero, AutoRadio = false };
        return p;
    }

    /// <summary>Lets the song "play" long enough that Ended counts as a real end, not a failed start.</summary>
    void Elapse(int seconds = 60) => clock = clock.AddSeconds(seconds);

    [Fact]
    public async Task Play_context_opens_the_resolved_url()
    {
        var p = Make();
        await p.PlayContextAsync(T.Many(3), 1, "ctx");
        Assert.Equal(new[] { "url:id1" }, audio.Opened);
        Assert.Equal(PlayState.Playing, p.State);
        Assert.Equal("id1", p.Current!.Id);
        Assert.Equal("ctx", p.Queue.Name);
    }

    [Fact]
    public async Task A_slow_old_request_never_overrides_the_newer_one()
    {
        var p = Make();
        var slow = new TaskCompletionSource<bool>();
        resolver.Gates["id0"] = slow;
        var tracks = T.Many(3);
        p.Queue.Set(tracks, 0, "q");
        var first = p.PlayAsync(tracks[0]);   // still resolving…
        await p.PlayAsync(tracks[2]);         // user picked another song
        slow.SetResult(true);                 // the old answer arrives late
        await first;
        Assert.Equal(new[] { "url:id2" }, audio.Opened);
        Assert.Equal("id2", p.Current!.Id);
    }

    [Fact]
    public async Task Ended_moves_to_next_song()
    {
        var p = Make();
        await p.PlayContextAsync(T.Many(3), 0, "q");
        Elapse();
        audio.RaiseEnded();
        await WaitFor(() => audio.Opened.Count == 2);
        Assert.Equal("url:id1", audio.Opened[^1]);
    }

    [Fact]
    public async Task Repeat_one_replays_the_same_song_on_end()
    {
        var p = Make();
        await p.PlayContextAsync(T.Many(3), 0, "q");
        p.CycleRepeat(); p.CycleRepeat();
        Assert.Equal(RepeatMode.One, p.Queue.Repeat);
        Elapse();
        audio.RaiseEnded();
        await WaitFor(() => audio.Opened.Count == 2);
        Assert.Equal("url:id0", audio.Opened[^1]);
    }

    [Fact]
    public async Task End_of_queue_stops_with_a_notice()
    {
        var p = Make();
        string? notice = null;
        p.Notice += m => notice = m;
        await p.PlayContextAsync(T.Many(1), 0, "q");
        Elapse();
        audio.RaiseEnded();
        await WaitFor(() => p.State == PlayState.Idle);
        Assert.True(audio.Stopped);
        Assert.Equal("End of the queue", notice);
    }

    [Fact]
    public async Task Auto_radio_adds_similar_songs_without_duplicates()
    {
        var p = Make();
        p.AutoRadio = true;
        int added = 0;
        p.RelatedAdded += n => added = n;
        p.Related = (t, ct) => Task.FromResult<IList<Track>>(new List<Track> { T.Make(0), T.Make(10), T.Make(11) });
        await p.PlayContextAsync(T.Many(1), 0, "q");
        await p.NextAsync();
        Assert.Equal(2, added);              // id0 was already in the queue
        Assert.Equal("id10", p.Current!.Id);
        Assert.Equal(3, p.Queue.Count);
    }

    [Fact]
    public async Task Broken_track_is_skipped()
    {
        var p = Make();
        resolver.Broken.Add("id0");
        await p.PlayContextAsync(T.Many(3), 0, "q");
        await WaitFor(() => audio.Opened.Count == 1);
        Assert.Equal("url:id1", audio.Opened[0]);
    }

    [Fact]
    public async Task Unstreamable_track_is_skipped_with_message()
    {
        var p = Make();
        var notices = new List<string>();
        p.Notice += notices.Add;
        resolver.Unplayable.Add("id0");
        await p.PlayContextAsync(T.Many(2), 0, "q");
        await WaitFor(() => audio.Opened.Count == 1);
        Assert.Contains("This track can't be streamed.", notices);
    }

    [Fact]
    public async Task Gives_up_after_several_broken_songs_in_a_row()
    {
        var p = Make();
        for (int i = 0; i < 20; i++) resolver.Broken.Add("id" + i);
        await p.PlayContextAsync(T.Many(20), 0, "q");
        await WaitFor(() => p.State == PlayState.Failed && resolver.Calls >= 6);
        await Task.Delay(100);
        Assert.Equal(6, resolver.Calls); // the first one plus five skips, then it stops
        Assert.Empty(audio.Opened);
    }

    [Fact]
    public async Task Early_end_tries_fallback_once_then_skips()
    {
        var p = Make();
        int fallbacks = 0;
        p.Fallback = (t, ct) => { fallbacks++; return Task.FromResult<string?>("file:" + t.Id); };
        await p.PlayContextAsync(T.Many(3), 0, "q");
        audio.RaiseEnded();                       // "ended" right away = didn't really play
        await WaitFor(() => audio.Opened.Count == 2);
        Assert.Equal("file:id0", audio.Opened[1]);
        audio.RaiseEnded();                       // the fallback failed too → next song
        await WaitFor(() => audio.Opened.Count == 3);
        Assert.Equal("url:id1", audio.Opened[2]);
        Assert.Equal(1, fallbacks);
    }

    [Fact]
    public async Task Failure_event_uses_fallback()
    {
        var p = Make();
        p.Fallback = (t, ct) => Task.FromResult<string?>("file:" + t.Id);
        await p.PlayContextAsync(T.Many(2), 0, "q");
        audio.RaiseFailed("vlc error");
        await WaitFor(() => audio.Opened.Count == 2);
        Assert.Equal("file:id0", audio.Opened[1]);
        Assert.Equal(PlayState.Playing, p.State);
    }

    [Fact]
    public async Task Toggle_pauses_and_resumes()
    {
        var p = Make();
        await p.PlayContextAsync(T.Many(1), 0, "q");
        await p.TogglePlayAsync();
        Assert.True(audio.Paused);
        Assert.Equal(PlayState.Paused, p.State);
        await p.TogglePlayAsync();
        Assert.False(audio.Paused);
        Assert.Equal(PlayState.Playing, p.State);
    }

    [Fact]
    public async Task Toggle_with_restored_session_starts_at_saved_position()
    {
        var p = Make();
        p.Queue.Restore(new SessionState { Queue = T.Many(2), Index = 1 });
        await p.TogglePlayAsync(42_000);
        Assert.Equal("url:id1", audio.Opened.Single());
        Assert.Equal(42_000, p.Times.Position);   // waits for the media length
        audio.LengthMs = 180_000;
        p.Tick();
        Assert.Equal(42_000, audio.TimeMs);
    }

    [Fact]
    public async Task Previous_restarts_song_after_four_seconds()
    {
        var p = Make();
        await p.PlayContextAsync(T.Many(3), 1, "q");
        audio.TimeMs = 10_000;
        await p.PreviousAsync();
        Assert.Equal(0, audio.TimeMs);
        Assert.Equal("id1", p.Current!.Id);
        await p.PreviousAsync();
        Assert.Equal("id0", p.Current!.Id);
    }

    [Fact]
    public async Task Seek_is_clamped_to_the_song()
    {
        var p = Make();
        await p.PlayContextAsync(T.Many(1), 0, "q");
        audio.LengthMs = 100_000;
        p.Seek(500_000);
        Assert.Equal(99_500, audio.TimeMs);
        p.Seek(-5);
        Assert.Equal(0, audio.TimeMs);
        p.SeekBy(5_000);
        Assert.Equal(5_000, audio.TimeMs);
    }

    [Fact]
    public void Mute_remembers_volume()
    {
        var p = Make();
        p.Volume = 0.6;
        p.ToggleMute();
        Assert.True(p.Muted);
        Assert.Equal(0, audio.Volume);
        p.ToggleMute();
        Assert.False(p.Muted);
        Assert.Equal(0.6, audio.Volume, 3);
        p.Volume = 3;
        Assert.Equal(1, audio.Volume);
    }

    [Fact]
    public void Repeat_cycles_off_all_one_off()
    {
        var p = Make();
        Assert.Equal(RepeatMode.All, p.CycleRepeat());
        Assert.Equal(RepeatMode.One, p.CycleRepeat());
        Assert.Equal(RepeatMode.Off, p.CycleRepeat());
    }

    [Fact]
    public async Task Stop_cancels_a_pending_load()
    {
        var p = Make();
        var gate = new TaskCompletionSource<bool>();
        resolver.Gates["id0"] = gate;
        var load = p.PlayContextAsync(T.Many(2), 0, "q");
        p.Stop();
        gate.SetResult(true);
        await load;
        Assert.Empty(audio.Opened);
        Assert.Equal(PlayState.Idle, p.State);
    }

    [Fact]
    public async Task PlayNow_keeps_the_rest_of_the_queue()
    {
        var p = Make();
        await p.PlayContextAsync(T.Many(3), 0, "q");
        await p.PlayNowAsync(T.Make(50));
        Assert.Equal("id50", p.Current!.Id);
        await p.NextAsync();
        Assert.Equal("id1", p.Current!.Id);
    }

    static async Task WaitFor(Func<bool> cond, int ms = 3000)
    {
        var until = DateTime.UtcNow.AddMilliseconds(ms);
        while (!cond())
        {
            if (DateTime.UtcNow > until) throw new TimeoutException("condition not reached");
            await Task.Delay(10);
        }
    }
}
