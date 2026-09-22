using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Windows.Media;
using EagleBoards.Core;
using EagleBoards.Core.Records;

namespace EagleBoards.App;

public abstract class Row : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;

    public string Id { get; protected init; } = "";

    protected bool Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value))
        {
            return false;
        }

        field = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
        return true;
    }

    protected void Raise(string name) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

    internal static string V(IReadOnlyDictionary<string, string> r, string key) => r.TryGetValue(key, out var v) ? v : "";
}

/// <summary>
/// Refresh a bound collection in place: update rows that are still there,
/// add new ones, drop gone ones. Replacing the collection would lose the
/// grid's selection, scroll position and sort every RefreshTimeSecs.
/// </summary>
public static class RowSync
{
    public static void Sync<T>(ObservableCollection<T> target, IReadOnlyList<IReadOnlyDictionary<string, string>> fresh,
        Func<IReadOnlyDictionary<string, string>, T> create, Action<T, IReadOnlyDictionary<string, string>> update)
        where T : Row
    {
        var byId = new Dictionary<string, IReadOnlyDictionary<string, string>>(StringComparer.Ordinal);
        foreach (var r in fresh)
        {
            byId[Row.V(r, "ID")] = r;
        }

        for (var i = target.Count - 1; i >= 0; i--)
        {
            if (!byId.ContainsKey(target[i].Id))
            {
                target.RemoveAt(i);
            }
        }

        var existing = target.ToDictionary(t => t.Id, StringComparer.Ordinal);
        foreach (var (id, r) in byId)
        {
            if (existing.TryGetValue(id, out var row))
            {
                update(row, r);
            }
            else
            {
                target.Add(create(r));
            }
        }
    }
}

/// <summary>A youth, as shown in the Youth and Boards panels.</summary>
public sealed class ScoutRow : Row
{
    private string _regNum = "", _last = "", _first = "", _unitName = "", _boardType = "", _room = "", _status = "", _leader = "";
    private string _result = "", _chair = "", _members = "", _notes = "";
    private int? _mins;
    private Brush _background = Brushes.White;
    private Brush _selectedBackground = Brushes.LightGray;

    public ScoutRow(IReadOnlyDictionary<string, string> r)
    {
        Id = V(r, "ID");
        Update(r);
    }

    public string RegNum { get => _regNum; private set { if (Set(ref _regNum, value)) Raise(nameof(RegNumSort)); } }

    /// <summary>Pre-registered first, walk-ins after, each in sign-in order.</summary>
    public string RegNumSort
    {
        get
        {
            var (rank, number) = SchedulerLogic.RegNumSortKey(RegNum);
            return rank.ToString(CultureInfo.InvariantCulture) + number.ToString("D6", CultureInfo.InvariantCulture);
        }
    }

    /// <summary>
    /// Minutes since the record last changed. Waiting time for a Registered
    /// youth; time on the board once seated -- the clock restarts at each
    /// status change, which is what the room-card timers run on.
    /// </summary>
    public int? Mins { get => _mins; private set { if (Set(ref _mins, value)) Raise(nameof(MinsTip)); } }

    public string MinsTip => Mins is not { } m ? "" : Status switch
    {
        BoardStatus.Seated => $"Board convening for {m} min",
        BoardStatus.InProgress => $"On a board for {m} min",
        BoardStatus.Completed or BoardStatus.Postponed => $"{m} min since finishing",
        _ => $"Waiting {m} min",
    };

    public string RegNumTip => RegNum.StartsWith('P')
        ? $"Pre-registered: matched a sign-up. #{RegNum[1..]} of the pre-registered to sign in."
        : RegNum.StartsWith('W') ? $"Walk-in: no pre-registration matched. #{RegNum[1..]} of the walk-ins to sign in." : "";

    public string Last { get => _last; private set => Set(ref _last, value); }

    public string First { get => _first; private set => Set(ref _first, value); }

    public string FullName => First + " " + Last;

    public string UnitName { get => _unitName; private set { if (Set(ref _unitName, value)) Raise(nameof(UnitLabel)); } }

    public string UnitLabel => SchedulerLogic.UnitLabel(UnitName);

    public string BoardType { get => _boardType; private set => Set(ref _boardType, value); }

    public string Room { get => _room; private set => Set(ref _room, value); }

    public string Status { get => _status; private set { if (Set(ref _status, value)) { Raise(nameof(StatusRank)); Raise(nameof(MinsTip)); } } }

    public int StatusRank => BoardStatus.Rank(Status);

    public string Leader { get => _leader; private set => Set(ref _leader, value); }

    public string Result { get => _result; private set => Set(ref _result, value); }

    public string BoardChair { get => _chair; private set => Set(ref _chair, value); }

    public string BoardMembers { get => _members; private set { if (Set(ref _members, value)) Raise(nameof(BoardMembersText)); } }

    /// <summary>Stored comma-joined with no spaces (the file format); shown readable.</summary>
    public string BoardMembersText => BoardMembers.Replace(",", ", ", StringComparison.Ordinal);

    public string Notes { get => _notes; private set => Set(ref _notes, value); }

    public Brush Background { get => _background; set => Set(ref _background, value); }

    public Brush SelectedBackground { get => _selectedBackground; set => Set(ref _selectedBackground, value); }

    public ScoutInfo Info => new(Id, Last, First, UnitName, BoardType, Room, Status, Leader);

    public void Update(IReadOnlyDictionary<string, string> r)
    {
        RegNum = V(r, "RegNum");
        Mins = int.TryParse(V(r, DataRecord.MinsSinceLastUpdateField), out var m) ? m : null;
        Last = V(r, "Last");
        First = V(r, "First");
        UnitName = V(r, "UnitName");
        BoardType = V(r, "BoardType");
        Room = V(r, "Room");
        Status = V(r, "Status");
        Leader = V(r, "Leader");
        Result = V(r, "Result");
        BoardChair = V(r, "BoardChair");
        BoardMembers = V(r, "BoardMembers");
        Notes = V(r, "Notes");
    }

    public void ApplyColors(ConfigRecord config)
    {
        Background = Brushes2.FromHex(config.ColorFor(Status, false), Brushes.White);
        SelectedBackground = Brushes2.FromHex(config.ColorFor(Status, true), Brushes.LightGray);
    }
}

/// <summary>An adult in the Adult Board Members panel.</summary>
public sealed class AdultRow : Row
{
    private string _last = "", _first = "", _unitName = "", _room = "", _final = "", _project = "";
    private bool _sel, _highlighted;

    public AdultRow(IReadOnlyDictionary<string, string> r)
    {
        Id = V(r, "ID");
        Update(r);
    }

    /// <summary>Set by the window to persist a checkbox change.</summary>
    public Action<AdultRow, bool>? OnSelChanged { get; set; }

    /// <summary>Picked by the operator for the next board.</summary>
    public bool Sel
    {
        get => _sel;
        set
        {
            if (Set(ref _sel, value))
            {
                Raise(nameof(SelSort));
                OnSelChanged?.Invoke(this, value);
            }
        }
    }

    public int SelSort => Sel ? 1 : 0;

    /// <summary>
    /// Sitting on the board whose room is in focus. A highlight, not a pick:
    /// looking at a seated board must not disturb the operator's picks for
    /// the next one.
    /// </summary>
    public bool IsHighlighted { get => _highlighted; set => Set(ref _highlighted, value); }

    internal void SetSelQuietly(bool value)
    {
        if (Set(ref _sel, value, nameof(Sel)))
        {
            Raise(nameof(SelSort));
        }
    }

    public string Last { get => _last; private set => Set(ref _last, value); }

    public string First { get => _first; private set => Set(ref _first, value); }

    public string FullName => First + " " + Last;

    public string UnitName { get => _unitName; private set { if (Set(ref _unitName, value)) Raise(nameof(UnitLabel)); } }

    public string UnitLabel => SchedulerLogic.UnitLabel(UnitName);

    public string Room
    {
        get => _room;
        private set
        {
            if (Set(ref _room, value))
            {
                Raise(nameof(CanPick));
                Raise(nameof(PickTip));
                Raise(nameof(Foreground));
            }
        }
    }

    public string FinalBoard { get => _final; private set => Set(ref _final, value); }

    public string ProjectReview { get => _project; private set => Set(ref _project, value); }

    public bool IsDisabled => Room == AdultRoom.Disabled;

    /// <summary>Someone on a board or gone home can't be ticked for another.</summary>
    public bool CanPick => Room.Length == 0;

    public string? PickTip => IsDisabled ? "Disabled for tonight -- enable them first"
        : Room.Length > 0 ? "Already seated on the board in room " + Room : null;

    /// <summary>Red when on a board, grey when gone home.</summary>
    public Brush Foreground => IsDisabled ? Brushes.Gray : Room.Length > 0 ? Brushes.Red : Brushes.Black;

    public AdultInfo Info => new(Id, Last, First, UnitName, Room, FinalBoard, ProjectReview);

    public void Update(IReadOnlyDictionary<string, string> r)
    {
        SetSelQuietly(V(r, "Sel") is "1" or "true");
        Last = V(r, "Last");
        First = V(r, "First");
        UnitName = V(r, "UnitName");
        Room = V(r, "Room");
        FinalBoard = V(r, "FinalBoard");
        ProjectReview = V(r, "ProjectReview");
    }
}

/// <summary>A card in the Available Rooms strip.</summary>
public sealed class RoomCard : Row
{
    private string _room = "", _boardType = "", _scout = "", _leaders = "", _timer = "";
    private TimerState _timerState;
    private bool _isSelected;

    public RoomCard(IReadOnlyDictionary<string, string> r)
    {
        Id = V(r, "ID");
        Update(r);
    }

    public string Room { get => _room; private set => Set(ref _room, value); }

    public string BoardType { get => _boardType; private set => Set(ref _boardType, value); }

    public string Scout { get => _scout; private set { if (Set(ref _scout, value)) Raise(nameof(IsFree)); } }

    public string Leaders { get => _leaders; private set { if (Set(ref _leaders, value)) Raise(nameof(LeadersText)); } }

    public string LeadersText => Leaders.Replace(",", ", ", StringComparison.Ordinal);

    public bool IsFree => Scout is "" or "-";

    /// <summary>"[12m]" while a board holds the room.</summary>
    public string TimerText { get => _timer; set => Set(ref _timer, value); }

    public TimerState TimerState
    {
        get => _timerState;
        set
        {
            if (Set(ref _timerState, value))
            {
                Raise(nameof(TimerBrush));
                Raise(nameof(ShowWarning));
            }
        }
    }

    public Brush TimerBrush => TimerState switch
    {
        TimerState.Warning => Brushes.DarkOrange,
        TimerState.Overdue => Brushes.Red,
        _ => Brushes.Black,
    };

    public bool ShowWarning => TimerState is TimerState.Warning or TimerState.Overdue;

    public bool IsSelected { get => _isSelected; set => Set(ref _isSelected, value); }

    public RoomInfo Info => new(Id, Room, BoardType, Scout);

    public void Update(IReadOnlyDictionary<string, string> r)
    {
        Room = V(r, "Room");
        BoardType = V(r, "BoardType");
        Scout = V(r, "Scout");
        Leaders = V(r, "Leaders");
    }
}

internal static class Brushes2
{
    private static readonly Dictionary<string, Brush> Cache = new(StringComparer.OrdinalIgnoreCase);

    public static Brush FromHex(string hex, Brush fallback)
    {
        if (string.IsNullOrWhiteSpace(hex))
        {
            return fallback;
        }

        if (Cache.TryGetValue(hex, out var cached))
        {
            return cached;
        }

        try
        {
            var brush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex));
            brush.Freeze();
            Cache[hex] = brush;
            return brush;
        }
        catch (Exception ex) when (ex is FormatException or NotSupportedException)
        {
            return fallback;
        }
    }
}
