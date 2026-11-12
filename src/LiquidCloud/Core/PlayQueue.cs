using System;
using System.Collections.Generic;
using System.Linq;

namespace LiquidCloud;

public enum RepeatMode { Off, All, One }

/// <summary>
/// The global play queue. "Items" keeps the order songs were added; "order" is the actual play order
/// (the same as Items, or a shuffled copy). Shuffle plays every song once per cycle before anything repeats.
/// </summary>
public class PlayQueue
{
    readonly List<Track> items = new();
    List<int> order = new();
    int pos = -1;
    readonly Random rnd = new();

    public string Name { get; private set; } = "";
    public bool Shuffle { get; private set; }
    public RepeatMode Repeat { get; set; }
    public event Action? Changed;

    public IReadOnlyList<Track> Items => items;
    public int Count => items.Count;
    /// <summary>Index into Items of the current song.</summary>
    public int Index => pos >= 0 && pos < order.Count ? order[pos] : -1;
    /// <summary>Position of the current song in play order.</summary>
    public int Position => pos;
    public Track? Current => Index >= 0 ? items[Index] : null;

    /// <summary>The queue in the order it will play.</summary>
    public List<Track> InPlayOrder() => order.Select(i => items[i]).ToList();

    public Track? PeekNext()
    {
        if (Repeat == RepeatMode.One) return Current;
        if (pos + 1 < order.Count) return items[order[pos + 1]];
        return Repeat == RepeatMode.All && order.Count > 0 && !Shuffle ? items[order[0]] : null;
    }

    public void Set(IEnumerable<Track> tracks, int start, string name)
    {
        items.Clear();
        items.AddRange(tracks);
        Name = name;
        BuildOrder(Math.Clamp(start, -1, items.Count - 1));
        Changed?.Invoke();
    }

    void BuildOrder(int current)
    {
        order = Enumerable.Range(0, items.Count).ToList();
        if (Shuffle)
        {
            if (current >= 0) order.Remove(current);
            ShuffleList(order);
            if (current >= 0) order.Insert(0, current);
            pos = current >= 0 ? 0 : -1;
        }
        else pos = current;
    }

    void ShuffleList(List<int> list, int from = 0)
    {
        for (int i = list.Count - 1; i > from; i--)
        {
            int j = rnd.Next(from, i + 1);
            (list[i], list[j]) = (list[j], list[i]);
        }
    }

    public void SetShuffle(bool on)
    {
        if (on == Shuffle) return;
        int cur = Index;
        Shuffle = on;
        BuildOrder(cur);
        Changed?.Invoke();
    }

    /// <summary>
    /// Moves to the next song. auto = the previous song ended by itself (then Repeat One replays it).
    /// Returns null at the end of the queue when Repeat is Off.
    /// </summary>
    public Track? Next(bool auto)
    {
        if (items.Count == 0) return null;
        if (auto && Repeat == RepeatMode.One) return Current;
        if (pos + 1 < order.Count) { pos++; Changed?.Invoke(); return Current; }
        if (Repeat == RepeatMode.Off) return null;
        // new cycle: with shuffle, a fresh order that doesn't start with the song that just played
        if (Shuffle && order.Count > 1)
        {
            int last = order[pos];
            ShuffleList(order);
            if (order[0] == last) (order[0], order[^1]) = (order[^1], order[0]);
        }
        pos = 0;
        Changed?.Invoke();
        return Current;
    }

    public Track? Previous()
    {
        if (items.Count == 0) return null;
        if (pos > 0) pos--;
        else if (Repeat == RepeatMode.All) pos = order.Count - 1;
        Changed?.Invoke();
        return Current;
    }

    /// <summary>Jump to a position in play order (what the queue list shows).</summary>
    public Track? JumpTo(int playOrderPosition)
    {
        if (playOrderPosition < 0 || playOrderPosition >= order.Count) return null;
        pos = playOrderPosition;
        Changed?.Invoke();
        return Current;
    }

    /// <summary>Puts a song right after the current one.</summary>
    public void PlayNext(Track t)
    {
        int at = Index + 1;
        items.Insert(at, t);
        for (int k = 0; k < order.Count; k++) if (order[k] >= at) order[k]++;
        order.Insert(pos + 1, at);
        if (pos < 0) pos = -1;
        Changed?.Invoke();
    }

    public void Add(IEnumerable<Track> tracks)
    {
        foreach (var t in tracks)
        {
            items.Add(t);
            int idx = items.Count - 1;
            if (Shuffle && order.Count > pos + 1) order.Insert(rnd.Next(pos + 1, order.Count + 1), idx);
            else order.Add(idx);
        }
        Changed?.Invoke();
    }

    /// <summary>Removes the song at a play-order position.</summary>
    public void RemoveAt(int playOrderPosition)
    {
        if (playOrderPosition < 0 || playOrderPosition >= order.Count) return;
        int itemIndex = order[playOrderPosition];
        items.RemoveAt(itemIndex);
        order.RemoveAt(playOrderPosition);
        for (int k = 0; k < order.Count; k++) if (order[k] > itemIndex) order[k]--;
        if (playOrderPosition <= pos) pos--; // removing the current song: Next continues with what came after it
        Changed?.Invoke();
    }

    /// <summary>Reorders by play-order positions (drag and drop in the queue).</summary>
    public void Move(int from, int to)
    {
        if (from == to || from < 0 || to < 0 || from >= order.Count || to >= order.Count) return;
        int cur = pos >= 0 ? order[pos] : -1;
        int v = order[from];
        order.RemoveAt(from);
        order.Insert(to, v);
        if (!Shuffle)
        {
            // keep Items in the same order as the play order when not shuffling
            var newItems = order.Select(i => items[i]).ToList();
            int newCur = cur >= 0 ? order.IndexOf(cur) : -1;
            items.Clear(); items.AddRange(newItems);
            order = Enumerable.Range(0, items.Count).ToList();
            pos = newCur;
        }
        else pos = cur >= 0 ? order.IndexOf(cur) : -1;
        Changed?.Invoke();
    }

    public void ClearUpcoming()
    {
        var cur = Current;
        items.Clear();
        order.Clear();
        pos = -1;
        if (cur != null) { items.Add(cur); order.Add(0); pos = 0; }
        Changed?.Invoke();
    }

    public SessionState Snapshot(long positionMs) => new()
    {
        Queue = items.Take(400).ToList(),
        Index = Index < 400 ? Index : -1,
        PositionMs = positionMs,
        Shuffle = Shuffle,
        Repeat = (int)Repeat,
        QueueName = Name
    };

    public void Restore(SessionState s)
    {
        Shuffle = s.Shuffle;
        Repeat = (RepeatMode)Math.Clamp(s.Repeat, 0, 2);
        items.Clear();
        items.AddRange(s.Queue);
        Name = s.QueueName;
        BuildOrder(Math.Clamp(s.Index, -1, items.Count - 1));
        Changed?.Invoke();
    }
}
