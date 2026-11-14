using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows.Input;

namespace LiquidCloud;

public abstract class ViewModelBase : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;

    protected bool Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return false;
        field = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
        return true;
    }

    protected void Raise([CallerMemberName] string? name = null) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}

/// <summary>ICommand from a delegate. Exceptions from async work are logged, never swallowed silently.</summary>
public sealed class RelayCommand : ICommand
{
    readonly Action<object?> run;
    readonly Func<object?, bool>? can;

    public RelayCommand(Action run, Func<bool>? can = null) : this(_ => run(), can == null ? null : _ => can()) { }
    public RelayCommand(Action<object?> run, Func<object?, bool>? can = null) { this.run = run; this.can = can; }

    public event EventHandler? CanExecuteChanged
    {
        add => CommandManager.RequerySuggested += value;
        remove => CommandManager.RequerySuggested -= value;
    }

    public bool CanExecute(object? p) => can?.Invoke(p) ?? true;

    public void Execute(object? p)
    {
        try { run(p); }
        catch (Exception ex) { Log.Error("Command", "Command failed", ex); }
    }
}

/// <summary>
/// Everything the player panel and the mini player show, bound in XAML. The window only wires commands to the
/// PlayerService and calls Refresh on its timer; no control is updated by hand.
/// </summary>
public sealed class PlayerViewModel : ViewModelBase
{
    readonly PlayerService player;
    readonly Func<Track, bool> isFavorite;

    public PlayerViewModel(PlayerService player, Func<Track, bool> isFavorite)
    {
        this.player = player;
        this.isFavorite = isFavorite;
        player.TrackChanged += OnTrack;
        player.StateChanged += OnState;
        player.Queue.Changed += OnQueue;
        OnTrack();
        OnState();
        OnQueue();
    }

    // ---- track info ----
    string title = "", artist = "", meta = "";
    string? art;

    public string Title { get => title; private set => Set(ref title, value); }
    public string Artist { get => artist; private set => Set(ref artist, value); }
    public string Meta { get => meta; private set => Set(ref meta, value); }
    public string? ArtUrl { get => art; private set => Set(ref art, value); }

    /// <summary>Shown before anything was played (or for a restored session).</summary>
    public void ShowIdle(Track? t)
    {
        if (t == null)
        {
            Title = Loc.T("Nothing playing");
            Artist = Loc.T("Find something in Discover");
            Meta = "";
            ArtUrl = null;
        }
        else Fill(t);
    }

    void Fill(Track t)
    {
        Title = t.Title;
        Artist = t.Artist;
        var bits = new List<string> { t.Source == "Local" ? Loc.T("On this PC") : t.Source };
        if (t.DurationMs > 0) bits.Add(t.DurationText);
        Meta = string.Join("  ·  ", bits);
        ArtUrl = t.Art;
        RefreshFavorite();
    }

    void OnTrack()
    {
        if (player.Current != null) Fill(player.Current);
        else ShowIdle(player.Queue.Current);
    }

    // ---- state ----
    bool isPlaying;
    string status = "";

    public bool IsPlaying { get => isPlaying; private set { if (Set(ref isPlaying, value)) Raise(nameof(PlayGlyph)); } }
    public string PlayGlyph => isPlaying ? "" : "";
    public string Status { get => status; set => Set(ref status, value); }

    void OnState()
    {
        IsPlaying = player.State is PlayState.Playing or PlayState.Loading;
        Status = player.State switch
        {
            PlayState.Loading => Loc.T("Loading…"),
            PlayState.Failed or PlayState.Idle => player.Message ?? "",
            _ => ""
        };
    }

    // ---- position ----
    double position, duration;
    string positionText = "0:00", durationText = "0:00";

    /// <summary>Bound two-way to the seek slider: the timer updates it, dragging the slider seeks.</summary>
    public double Position
    {
        get => position;
        set
        {
            if (Math.Abs(value - position) < 0.01) return;
            position = value;
            Raise();
            if (!refreshing)
            {
                if (HoldSeek) seekDirty = true;      // dragging: only move the thumb and the time label
                else player.Seek((long)(value * 1000));
            }
            PositionText = Fmt((long)(value * 1000));
        }
    }

    bool seekDirty;

    /// <summary>True while the mouse holds the seek slider. The song jumps once, when you let go
    /// (seeking a network stream on every pixel of a drag made it stutter or not seek at all).</summary>
    public bool HoldSeek { get; set; }

    /// <summary>Mouse released on the slider: jump to where the thumb is.</summary>
    public void CommitSeek()
    {
        HoldSeek = false;
        if (!seekDirty) return;
        seekDirty = false;
        player.Seek((long)(position * 1000));
    }

    public double Duration { get => duration; private set { if (Set(ref duration, Math.Max(value, 0.001))) Raise(nameof(Progress)); } }
    public double Progress => duration > 0.01 ? position / duration : 0;
    public string PositionText { get => positionText; private set => Set(ref positionText, value); }
    public string DurationText { get => durationText; private set => Set(ref durationText, value); }

    bool refreshing;

    /// <summary>Called by the window's timer. Skips the position while you're dragging the slider.</summary>
    public void Refresh(bool userIsSeeking)
    {
        var (pos, len) = player.Times;
        refreshing = true;
        Duration = len / 1000.0;
        DurationText = Fmt(len);
        if (!userIsSeeking)
        {
            position = pos / 1000.0;
            Raise(nameof(Position));
            Raise(nameof(Progress));
            PositionText = Fmt(pos);
        }
        refreshing = false;
    }

    /// <summary>Shows a saved position (restored session) without seeking.</summary>
    public void ShowPosition(long posMs, long lenMs)
    {
        refreshing = true;
        Duration = lenMs / 1000.0;
        DurationText = Fmt(lenMs);
        position = posMs / 1000.0;
        Raise(nameof(Position));
        Raise(nameof(Progress));
        PositionText = Fmt(posMs);
        refreshing = false;
    }

    public static string Fmt(long ms)
    {
        var t = TimeSpan.FromMilliseconds(Math.Max(0, ms));
        return t.TotalHours >= 1 ? $"{(int)t.TotalHours}:{t.Minutes:00}:{t.Seconds:00}" : $"{(int)t.TotalMinutes}:{t.Seconds:00}";
    }

    // ---- volume ----
    public double Volume
    {
        get => player.Volume;
        set { player.Volume = value; Raise(); Raise(nameof(VolumeGlyph)); Raise(nameof(Muted)); }
    }

    public bool Muted => player.Muted;
    public string VolumeGlyph => player.Muted || player.Volume <= 0.001 ? "" : player.Volume < .35 ? "" : player.Volume < .7 ? "" : "";

    public void ToggleMute()
    {
        player.ToggleMute();
        Raise(nameof(Volume)); Raise(nameof(VolumeGlyph)); Raise(nameof(Muted));
    }

    // ---- shuffle / repeat ----
    public bool Shuffle => player.Queue.Shuffle;
    public RepeatMode Repeat => player.Queue.Repeat;
    public bool RepeatOn => player.Queue.Repeat != RepeatMode.Off;
    public bool RepeatOne => player.Queue.Repeat == RepeatMode.One;
    public string RepeatGlyph => player.Queue.Repeat == RepeatMode.One ? "" : "";
    public string RepeatTip => player.Queue.Repeat switch
    {
        RepeatMode.All => Loc.T("Repeat: all (R)"),
        RepeatMode.One => Loc.T("Repeat: this song (R)"),
        _ => Loc.T("Repeat: off (R)")
    };

    public void RefreshModes()
    {
        Raise(nameof(Shuffle)); Raise(nameof(Repeat)); Raise(nameof(RepeatOn)); Raise(nameof(RepeatOne));
        Raise(nameof(RepeatGlyph)); Raise(nameof(RepeatTip));
    }

    // ---- favorite ----
    bool fav;
    public bool IsFavorite { get => fav; private set { if (Set(ref fav, value)) { Raise(nameof(LikeGlyph)); Raise(nameof(LikeTip)); } } }
    public string LikeGlyph => fav ? "" : "";
    public string LikeTip => fav ? Loc.T("Remove from favorites (L)") : Loc.T("Add to favorites (L)");
    public void RefreshFavorite() => IsFavorite = (player.Current ?? player.Queue.Current) is { } t && isFavorite(t);

    // ---- queue ----
    public ObservableCollection<Track> QueueItems { get; } = new();
    int queuePosition = -1;
    string queueName = "";
    public int QueuePosition { get => queuePosition; private set => Set(ref queuePosition, value); }
    public string QueueName { get => queueName; private set => Set(ref queueName, value); }

    void OnQueue()
    {
        var items = player.Queue.InPlayOrder();
        // replace only what changed so the list doesn't flicker or lose its scroll position
        int i = 0;
        for (; i < items.Count; i++)
        {
            if (i < QueueItems.Count) { if (!ReferenceEquals(QueueItems[i], items[i])) QueueItems[i] = items[i]; }
            else QueueItems.Add(items[i]);
        }
        while (QueueItems.Count > items.Count) QueueItems.RemoveAt(QueueItems.Count - 1);
        QueuePosition = player.Queue.Position;
        QueueName = string.IsNullOrEmpty(player.Queue.Name) ? Loc.T("Your queue") : player.Queue.Name;
        RefreshModes();
    }

    /// <summary>The interface language changed: re-read every text the view model produces.</summary>
    public void Relocalize()
    {
        OnTrack();
        OnState();
        OnQueue();
        Raise(nameof(LikeTip));
    }

    // ---- commands (wired by the window) ----
    public ICommand? PlayPauseCommand { get; set; }
    public ICommand? NextCommand { get; set; }
    public ICommand? PreviousCommand { get; set; }
    public ICommand? ShuffleCommand { get; set; }
    public ICommand? RepeatCommand { get; set; }
    public ICommand? MuteCommand { get; set; }
    public ICommand? LikeCommand { get; set; }
}
