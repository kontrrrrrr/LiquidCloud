namespace LiquidCloud.Tests;

public class PlayQueueTests
{
    static PlayQueue Q(int n, int start = 0) { var q = new PlayQueue(); q.Set(T.Many(n), start, "test"); return q; }

    [Fact]
    public void Set_starts_at_requested_song()
    {
        var q = Q(5, 2);
        Assert.Equal("id2", q.Current!.Id);
        Assert.Equal(2, q.Index);
        Assert.Equal("test", q.Name);
    }

    [Fact]
    public void Next_walks_forward_and_stops_at_end_when_repeat_off()
    {
        var q = Q(3);
        Assert.Equal("id1", q.Next(false)!.Id);
        Assert.Equal("id2", q.Next(false)!.Id);
        Assert.Null(q.Next(false));
        Assert.Null(q.Next(true));
        Assert.Equal("id2", q.Current!.Id); // stays on the last song
    }

    [Fact]
    public void Repeat_all_wraps_around_both_ways()
    {
        var q = Q(3);
        q.Repeat = RepeatMode.All;
        q.Next(false); q.Next(false);
        Assert.Equal("id0", q.Next(true)!.Id);
        Assert.Equal("id2", q.Previous()!.Id);
    }

    [Fact]
    public void Repeat_one_replays_only_when_song_ends_by_itself()
    {
        var q = Q(3);
        q.Repeat = RepeatMode.One;
        Assert.Equal("id0", q.Next(true)!.Id);   // ended → same song
        Assert.Equal("id1", q.Next(false)!.Id);  // Next button → next song
        Assert.Equal("id1", q.PeekNext()!.Id);
    }

    [Fact]
    public void Previous_at_start_without_repeat_stays()
    {
        var q = Q(3);
        Assert.Equal("id0", q.Previous()!.Id);
    }

    [Fact]
    public void Empty_queue_is_safe()
    {
        var q = new PlayQueue();
        Assert.Null(q.Current);
        Assert.Null(q.Next(false));
        Assert.Null(q.Previous());
        Assert.Null(q.JumpTo(0));
        Assert.Null(q.PeekNext());
        q.RemoveAt(0);
        q.Move(0, 1);
        q.ClearUpcoming();
        Assert.Equal(0, q.Count);
    }

    [Fact]
    public void Shuffle_plays_every_song_exactly_once_per_cycle()
    {
        for (int run = 0; run < 20; run++)
        {
            var q = new PlayQueue();
            q.SetShuffle(true);
            q.Set(T.Many(25), 7, "s");
            Assert.Equal("id7", q.Current!.Id); // the song you clicked plays first
            var seen = new List<string> { q.Current.Id };
            while (q.Next(false) is { } t) seen.Add(t.Id);
            Assert.Equal(25, seen.Count);
            Assert.Equal(25, seen.Distinct().Count());
        }
    }

    [Fact]
    public void Shuffle_new_cycle_does_not_repeat_last_song_immediately()
    {
        for (int run = 0; run < 50; run++)
        {
            var q = new PlayQueue();
            q.SetShuffle(true);
            q.Set(T.Many(4), 0, "s");
            q.Repeat = RepeatMode.All;
            Track last = q.Current!;
            for (int i = 0; i < 3; i++) last = q.Next(false)!;
            var first = q.Next(true)!;
            Assert.NotEqual(last.Id, first.Id);
            var cycle = new List<string> { first.Id };
            for (int i = 0; i < 3; i++) cycle.Add(q.Next(false)!.Id);
            Assert.Equal(4, cycle.Distinct().Count());
        }
    }

    [Fact]
    public void Toggling_shuffle_keeps_the_current_song()
    {
        var q = Q(10, 4);
        q.SetShuffle(true);
        Assert.Equal("id4", q.Current!.Id);
        Assert.Equal(0, q.Position);
        q.Next(false);
        var cur = q.Current!.Id;
        q.SetShuffle(false);
        Assert.Equal(cur, q.Current!.Id);
        Assert.Equal(10, q.InPlayOrder().Count);
    }

    [Fact]
    public void PlayNext_inserts_right_after_current()
    {
        var q = Q(3, 1);
        q.PlayNext(T.Make(99));
        Assert.Equal("id99", q.PeekNext()!.Id);
        Assert.Equal("id1", q.Current!.Id);
        Assert.Equal("id99", q.Next(false)!.Id);
        Assert.Equal("id2", q.Next(false)!.Id);
    }

    [Fact]
    public void PlayNext_with_shuffle_still_plays_next()
    {
        var q = new PlayQueue();
        q.SetShuffle(true);
        q.Set(T.Many(8), 3, "s");
        q.PlayNext(T.Make(99));
        Assert.Equal("id3", q.Current!.Id);
        Assert.Equal("id99", q.Next(false)!.Id);
        Assert.Equal(9, q.InPlayOrder().Select(t => t.Id).Distinct().Count());
    }

    [Fact]
    public void Add_appends_without_changing_current()
    {
        var q = Q(2);
        q.Add(new[] { T.Make(5), T.Make(6) });
        Assert.Equal(4, q.Count);
        Assert.Equal("id0", q.Current!.Id);
        Assert.Equal(new[] { "id0", "id1", "id5", "id6" }, q.InPlayOrder().Select(t => t.Id));
    }

    [Fact]
    public void Remove_before_current_keeps_current()
    {
        var q = Q(5, 3);
        q.RemoveAt(1);
        Assert.Equal("id3", q.Current!.Id);
        Assert.Equal("id4", q.Next(false)!.Id);
    }

    [Fact]
    public void Remove_current_continues_with_following_song()
    {
        var q = Q(5, 2);
        q.RemoveAt(2);
        Assert.Equal("id3", q.Next(false)!.Id);
    }

    [Fact]
    public void Move_reorders_and_tracks_current()
    {
        var q = Q(4, 1);
        q.Move(3, 0);
        Assert.Equal(new[] { "id3", "id0", "id1", "id2" }, q.InPlayOrder().Select(t => t.Id));
        Assert.Equal("id1", q.Current!.Id);
        Assert.Equal("id2", q.Next(false)!.Id);
        q.Move(-1, 2); q.Move(0, 99); // ignored
        Assert.Equal(4, q.Count);
    }

    [Fact]
    public void ClearUpcoming_keeps_only_current()
    {
        var q = Q(5, 2);
        q.ClearUpcoming();
        Assert.Equal(1, q.Count);
        Assert.Equal("id2", q.Current!.Id);
        Assert.Null(q.Next(false));
    }

    [Fact]
    public void Snapshot_and_restore_round_trip()
    {
        var q = Q(6, 4);
        q.SetShuffle(true);
        q.Repeat = RepeatMode.One;
        var s = q.Snapshot(12_345);
        var r = new PlayQueue();
        r.Restore(s);
        Assert.Equal("id4", r.Current!.Id);
        Assert.True(r.Shuffle);
        Assert.Equal(RepeatMode.One, r.Repeat);
        Assert.Equal(12_345, s.PositionMs);
        Assert.Equal(6, r.Count);
    }

    [Fact]
    public void Restore_clamps_bad_saved_values()
    {
        var r = new PlayQueue();
        r.Restore(new SessionState { Queue = T.Many(2), Index = 50, Repeat = 9, QueueName = "x" });
        Assert.Equal("id1", r.Current!.Id);
        Assert.Equal(RepeatMode.One, r.Repeat);
    }

    [Fact]
    public void Changed_fires_on_edits()
    {
        var q = Q(3);
        int n = 0;
        q.Changed += () => n++;
        q.Next(false); q.Add(new[] { T.Make(9) }); q.RemoveAt(2); q.SetShuffle(true);
        Assert.Equal(4, n);
    }
}
