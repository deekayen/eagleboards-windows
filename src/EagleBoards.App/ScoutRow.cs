using System.ComponentModel;
using System.Globalization;
using EagleBoards.Core;
using EagleBoards.Core.Records;

namespace EagleBoards.App;

/// <summary>A youth, as shown in the Event page's queue and details pane.</summary>
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

    /// <summary>
    /// Order in the Youth list: Waiting by sign-in number, On a board by room,
    /// Finished the most recent first (SPEC.md O-3). Compared ordinally.
    /// </summary>
    public string QueueSort => SchedulerLogic.QueueSortKey(Status, RegNum, Room, _lastUpdate);

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

    /// <summary>What the minutes count, in words: the queue shows "12 min" for width, a tooltip has room.</summary>
    public string MinsTip => Mins is not { } m ? "" : Status switch
    {
        BoardStatus.Seated => $"Board convening for {Display.Minutes(m)}",
        BoardStatus.InProgress => $"In review for {Display.Minutes(m)}",
        BoardStatus.Completed or BoardStatus.Postponed => $"{Display.Minutes(m)} since finishing",
        _ => $"Waiting {Display.Minutes(m)}",
    };

    /// <summary>What the sign-in number means, in words: "#3" is read aloud as "number sign 3".</summary>
    public string RegNumTip => RegNum.Length < 2 || !int.TryParse(RegNum[1..], NumberStyles.None, CultureInfo.InvariantCulture, out var n) ? ""
        : RegNum[0] == 'P' ? $"Pre-registered: matched a sign-up. The {Display.Ordinal(n)} pre-registered youth to sign in."
        : RegNum[0] == 'W' ? $"Walk-in: no pre-registration matched. The {Display.Ordinal(n)} walk-in to sign in." : "";

    /// <summary>
    /// The queue item's tooltip, on the item so it shows on keyboard focus as
    /// well as on a hover over its text: what the minutes and the sign-in
    /// number mean. Null when there's nothing to say, for no empty tooltip.
    /// </summary>
    public string? QueueTip => string.Join(" ", new[] { MinsTip.Length > 0 ? MinsTip + "." : "", RegNumTip }.Where(s => s.Length > 0)) is { Length: > 0 } tip ? tip : null;

    /// <summary>
    /// The queue item's status for a screen reader (its ItemStatus, after the
    /// name): where the youth is and for how long, in words, since the
    /// waiting group shows no status pill.
    /// </summary>
    public string QueueStatus => Status switch
    {
        BoardStatus.Seated => Mins is { } m ? $"Seated, convening for {Display.Minutes(m)}" : StatusText,
        BoardStatus.InProgress => Mins is { } m ? $"In review for {Display.Minutes(m)}" : StatusText,
        _ when BoardStatus.IsWaiting(Status) => Mins is { } m ? $"Waiting {Display.Minutes(m)}" : StatusText,
        _ => StatusText,
    };

    /// <summary>The queue item's help text: the second line in commas, then what the sign-in number means.</summary>
    public string QueueHelp => string.Join(". ", new[] { SubLineSpoken, RegNumTip }.Where(s => s.Length > 0));

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
                Raise(nameof(QueueStatus));
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
        Raise(nameof(QueueSort));
        Raise(nameof(SubLine));
        Raise(nameof(SubLineSpoken));
        Raise(nameof(QueueHelp));
        Raise(nameof(QueueTip));
        Raise(nameof(QueueStatus));
        Raise(nameof(TimeText));
    }

    /// <summary>The queue's sections, in order: who's next, who's in a room, who's done.</summary>
    public static readonly string[] QueueGroups = ["Waiting", "On a board", "Finished"];

    public string QueueGroup => QueueGroups[QueueRank];

    public int QueueRank => BoardStatus.IsWaiting(Status) ? 0 : BoardStatus.IsActive(Status) ? 1 : 2;

    /// <summary>"W9 · Troop 1409 · Final board", or the room once seated. The unit in full: "T1409" is read as a code.</summary>
    public string SubLine => string.Join(" · ", new[] { RegNum, Display.Unit(UnitName), BoardStatus.IsActive(Status) ? "Room " + Room : Display.BoardType(BoardType) }
        .Where(s => s.Length > 0));

    /// <summary>The same in commas, as it's read aloud: a screen reader says the dot, or nothing.</summary>
    public string SubLineSpoken => Display.Spoken(SubLine);

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
        Raise(nameof(QueueTip));
        Raise(nameof(QueueStatus));
    }
}
