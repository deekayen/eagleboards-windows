using System.Globalization;
using System.Runtime.CompilerServices;
using EagleBoards.Core;

namespace EagleBoards.App;

/// <summary>A room, as its card in the Event page's Rooms pane.</summary>
public sealed class RoomCard : Row
{
    private string _room = "", _boardType = "", _scout = "", _leaders = "", _timer = "";
    private int? _mins;
    private TimerState _timerState;
    private bool _isSelected;

    public RoomCard(IReadOnlyDictionary<string, string> r)
    {
        Id = V(r, "ID");
        Update(r);
    }

    // A rename or a type switch keeps the room's ID, so the card is updated
    // in place: its name must follow, or it still says the old room.
    public string Room { get => _room; private set { if (Set(ref _room, value)) Raise(nameof(AccessibleName)); } }

    public string BoardType
    {
        get => _boardType;
        private set
        {
            if (Set(ref _boardType, value))
            {
                Raise(nameof(BoardTypeText));
                Raise(nameof(AccessibleName));
            }
        }
    }

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
                Raise(nameof(AccessibleName));
            }
        }
    }

    public string LeadersText => Display.List(Leaders);

    /// <summary>The members one per line, for the room card.</summary>
    public string LeadersLines => Display.Lines(Leaders);

    public bool IsFree => Scout is "" or "-";

    /// <summary>"12 min" while a board holds the room, on the timer badge, where space is tight.</summary>
    public string TimerText { get => _timer; set => Set(ref _timer, value); }

    /// <summary>The minutes the timer counts, set beside <see cref="TimerText"/>; null while the room is free.</summary>
    public int? Mins { get => _mins; set { if (Set(ref _mins, value)) Raise(nameof(AccessibleName)); } }

    /// <summary>
    /// What a screen reader says for the card: what it shows, the minutes in
    /// words ("min" can be read as "minimum") and the members, which is how
    /// people find which room someone is in.
    /// </summary>
    public string AccessibleName => IsFree ? $"Room {Room}, {BoardTypeText}, free"
        : string.Join(", ", new[]
        {
            $"Room {Room}", BoardTypeText, Scout, Mins is { } m ? Display.Minutes(m) : "",
            TimerState == TimerState.Overdue ? "overdue" : TimerState == TimerState.Warning ? "running long" : "",
            LeadersText.Length > 0 ? "members " + LeadersText : "",
        }.Where(s => s.Length > 0));

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
