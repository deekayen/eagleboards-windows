using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.Runtime.CompilerServices;
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
/// grid's selection, scroll position and sort on every change.
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
    private string _result = "", _chair = "", _members = "", _notes = "", _lastUpdate = "";
    private int? _mins;

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

    public string Room { get => _room; private set { if (Set(ref _room, value)) Raise(nameof(RoomText)); } }

    /// <summary>Blank once the board is over; the file says "N/A".</summary>
    public string RoomText => Room == AdultRoom.Disabled ? "" : Room;

    public string Status
    {
        get => _status;
        private set
        {
            if (Set(ref _status, value))
            {
                Raise(nameof(StatusRank));
                Raise(nameof(MinsTip));
                Raise(nameof(StatusText));
            }
        }
    }

    /// <summary>The status in words; the file keeps the Java values ("InProgress").</summary>
    public string StatusText => Display.Status(Status);

    public int StatusRank => BoardStatus.Rank(Status);

    public string Leader { get => _leader; private set => Set(ref _leader, value); }

    public string Result { get => _result; private set { if (Set(ref _result, value)) Raise(nameof(ResultText)); } }

    public string ResultText => Display.Result(Result);

    public string BoardChair { get => _chair; private set => Set(ref _chair, value); }

    public string BoardChairId { get; private set; } = "";

    public string BoardMembers { get => _members; private set { if (Set(ref _members, value)) Raise(nameof(BoardMembersText)); } }

    /// <summary>A list in the file (comma- or ~-joined); shown as a readable list.</summary>
    public string BoardMembersText => Display.List(BoardMembers);

    public string Notes { get => _notes; private set => Set(ref _notes, value); }

    public ScoutInfo Info => new(Id, Last, First, UnitName, BoardType, Room, Status, Leader);

    public void Update(IReadOnlyDictionary<string, string> r)
    {
        RegNum = V(r, "RegNum");
        _lastUpdate = V(r, DataRecord.LastUpdateTimeField);
        Tick();
        Last = Display.Text(V(r, "Last"));
        First = Display.Text(V(r, "First"));
        UnitName = V(r, "UnitName");
        BoardType = V(r, "BoardType");
        Room = V(r, "Room");
        Status = V(r, "Status");
        Leader = Display.Text(V(r, "Leader"));
        Result = V(r, "Result");
        BoardChair = Display.Text(V(r, "BoardChair"));
        BoardChairId = V(r, "BoardChairID");
        BoardMembers = V(r, "BoardMembers");
        Notes = Display.Text(V(r, "Notes"));
        Raise(nameof(QueueGroup));
        Raise(nameof(QueueRank));
        Raise(nameof(SubLine));
        Raise(nameof(TimeText));
    }

    /// <summary>The queue's sections: who's next, who's in a room, who's done.</summary>
    public string QueueGroup => BoardStatus.IsWaiting(Status) ? "Waiting" : BoardStatus.IsActive(Status) ? "On a board" : "Finished";

    public int QueueRank => BoardStatus.IsWaiting(Status) ? 0 : BoardStatus.IsActive(Status) ? 1 : 2;

    /// <summary>"W9 · Troop 1409 · Final board", or the room once seated.</summary>
    public string SubLine => string.Join(" · ", new[] { RegNum, UnitLabel, BoardStatus.IsActive(Status) ? "Room " + Room : Display.BoardType(BoardType) }
        .Where(s => s.Length > 0));

    public string TimeText => Mins is { } m && !BoardStatus.IsFinished(Status) ? $"{m} min" : "";

    /// <summary>
    /// Recount the minutes since the last status change. Stamps are to the
    /// minute, so the count changes on the clock's minute; the window calls
    /// this then, with no need to reload anything.
    /// </summary>
    public void Tick()
    {
        var m = DataRecord.MinutesSince(_lastUpdate);
        Mins = m >= 0 ? m : null;
        Raise(nameof(TimeText));
    }
}

/// <summary>
/// An adult in the board builder, seen from one youth's board: picked
/// members (with the chair choice) and the eligible adults to add.
/// </summary>
public sealed class PickRow : Row
{
    private bool _isChair;

    public PickRow(AdultRow adult, string boardType, string scoutUnit, Action<PickRow>? chairChosen = null)
    {
        Adult = adult;
        Id = adult.Id;
        var role = adult.Info.RoleFor(boardType);
        CanChair = role == BoardRoles.Chair;
        SameUnit = scoutUnit.Length > 0 && adult.UnitName == scoutUnit;
        Detail = string.Join(" · ", new[] { adult.UnitLabel, role, SameUnit ? "same unit as the youth" : "" }.Where(s => s.Length > 0));
        ChairChosen = chairChosen;
    }

    public AdultRow Adult { get; }

    public string Name => Adult.FullName;

    public string Detail { get; }

    public bool CanChair { get; }

    public bool SameUnit { get; }

    private Action<PickRow>? ChairChosen { get; }

    public bool IsChair
    {
        get => _isChair;
        set
        {
            if (Set(ref _isChair, value) && value)
            {
                ChairChosen?.Invoke(this);
            }
        }
    }

    internal void SetChairQuietly(bool value) => Set(ref _isChair, value, nameof(IsChair));
}

/// <summary>An adult on the People page and the source of the builder's rows.</summary>
public sealed class AdultRow : Row
{
    private string _last = "", _first = "", _unitName = "", _room = "", _final = "", _project = "", _woodBadge = "", _supporting = "";
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
                Raise(nameof(RoomText));
                Raise(nameof(IsDisabled));
                Raise(nameof(IsBusy));
            }
        }
    }

    public string FinalBoard { get => _final; private set => Set(ref _final, value); }

    public string ProjectReview { get => _project; private set => Set(ref _project, value); }

    /// <summary>"Y" when tonight counts toward a Wood Badge ticket item.</summary>
    public string WoodBadge { get => _woodBadge; private set { if (Set(ref _woodBadge, value)) Raise(nameof(WoodBadgeMark)); } }

    /// <summary>What the WB column shows.</summary>
    public string WoodBadgeMark => WoodBadge == "Y" ? "\u2713" : "";

    /// <summary>IDs of the scouts this adult came to support, "|"-separated.</summary>
    public string Supporting { get => _supporting; private set => Set(ref _supporting, value); }

    private string _supportingNames = "";

    /// <summary>Who they came to support, by name; set by the window, which knows the youth.</summary>
    public string SupportingNames { get => _supportingNames; set => Set(ref _supportingNames, value); }

    public bool IsDisabled => Room == AdultRoom.Disabled;

    /// <summary>On a board now.</summary>
    public bool IsBusy => Room.Length > 0 && !IsDisabled;

    /// <summary>"Gone home" rather than the file's "N/A".</summary>
    public string RoomText => IsDisabled ? "Gone home" : Room;

    /// <summary>Someone on a board or gone home can't be ticked for another.</summary>
    public bool CanPick => Room.Length == 0;

    public string? PickTip => IsDisabled ? "Gone home. Mark them back first."
        : Room.Length > 0 ? "On the board in room " + Room : null;

    public AdultInfo Info => new(Id, Last, First, UnitName, Room, FinalBoard, ProjectReview, WoodBadge: WoodBadge, Supporting: Supporting);

    public void Update(IReadOnlyDictionary<string, string> r)
    {
        SetSelQuietly(V(r, "Sel") is "1" or "true");
        Last = Display.Text(V(r, "Last"));
        First = Display.Text(V(r, "First"));
        UnitName = V(r, "UnitName");
        Room = V(r, "Room");
        FinalBoard = V(r, "FinalBoard");
        ProjectReview = V(r, "ProjectReview");
        WoodBadge = V(r, "WoodBadge");
        Supporting = V(r, "Supporting");
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

    public string BoardType { get => _boardType; private set { if (Set(ref _boardType, value)) Raise(nameof(BoardTypeText)); } }

    public string BoardTypeText => Display.BoardType(BoardType);

    public string Scout
    {
        get => _scout;
        private set
        {
            if (Set(ref _scout, value))
            {
                Raise(nameof(IsFree));
                Raise(nameof(ScoutText));
                Raise(nameof(AccessibleName));
            }
        }
    }

    public string ScoutText => IsFree ? "Free" : Scout;

    public string Leaders
    {
        get => _leaders;
        private set
        {
            if (Set(ref _leaders, value))
            {
                Raise(nameof(LeadersText));
                Raise(nameof(LeadersLines));
            }
        }
    }

    public string LeadersText => Display.List(Leaders);

    /// <summary>The members one per line, for the room card.</summary>
    public string LeadersLines => Display.Lines(Leaders);

    public bool IsFree => Scout is "" or "-";

    /// <summary>"12 min" while a board holds the room.</summary>
    public string TimerText { get => _timer; set { if (Set(ref _timer, value)) Raise(nameof(AccessibleName)); } }

    /// <summary>What a screen reader says for the card.</summary>
    public string AccessibleName => $"Room {Room}, {BoardTypeText}, "
        + (IsFree ? "free" : $"{Scout}, {TimerText}{(TimerState == TimerState.Overdue ? ", overdue" : TimerState == TimerState.Warning ? ", running long" : "")}");

    public TimerState TimerState
    {
        get => _timerState;
        set
        {
            if (Set(ref _timerState, value))
            {
                Raise(nameof(HasTimerTip));
                Raise(nameof(TimerTip));
                Raise(nameof(AccessibleName));
            }
        }
    }

    public string? TimerTip => TimerState switch
    {
        TimerState.Warning => "Running long: past the caution time in Settings",
        TimerState.Overdue => "Overdue: past the overdue time in Settings",
        _ => null,
    };

    /// <summary>Running long or overdue: the card's tooltip says which, in words.</summary>
    public bool HasTimerTip => TimerState is TimerState.Warning or TimerState.Overdue;

    public bool IsSelected { get => _isSelected; set => Set(ref _isSelected, value); }

    public RoomInfo Info => new(Id, Room, BoardType, Scout);

    public void Update(IReadOnlyDictionary<string, string> r)
    {
        Room = V(r, "Room");
        BoardType = V(r, "BoardType");
        Scout = Display.Text(V(r, "Scout"));
        Leaders = V(r, "Leaders");
    }
}

/// <summary>
/// Words for the values the data files store in their Java form. Display
/// only: the files keep "InProgress", "NotApproved", "N/A", and the Java
/// escapes ("," written as "~").
/// </summary>
public static class Display
{
    /// <summary>
    /// The file format writes a comma in a value as "~" and never turns it back
    /// (the Java version's format, kept so both can read the files). Undo it
    /// for display; IDs and the files themselves are left alone.
    /// </summary>
    public static string Text(string value) => value.Replace('~', ',');

    /// <summary>The same list, one name per line.</summary>
    public static string Lines(string value) =>
        string.Join(Environment.NewLine, value.Split([',', '~'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));

    /// <summary>A list of names, joined with "," in memory or "~" once saved, as "A, B, C".</summary>
    public static string List(string value) =>
        string.Join(", ", value.Split([',', '~'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));

    public static string Status(string status) => status switch
    {
        BoardStatus.Registered => "Waiting",
        BoardStatus.InProgress => "In review",
        _ => status,
    };

    public static string Result(string result) => result == BoardResults.NotApproved ? "Not approved" : result;

    public static string BoardType(string type) => type switch
    {
        BoardTypes.Final => "Final board",
        BoardTypes.Project => "Project review",
        _ => type,
    };
}
