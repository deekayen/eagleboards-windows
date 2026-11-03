using System.Globalization;
using System.Runtime.CompilerServices;
using EagleBoards.Core;
using EagleBoards.Core.Records;

namespace EagleBoards.App;

/// <summary>An adult as the Event page knows them: the source of the builder's rows.</summary>
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

    /// <summary>"Y" when the event counts toward a Wood Badge ticket item.</summary>
    public string WoodBadge { get => _woodBadge; private set { if (Set(ref _woodBadge, value)) Raise(nameof(IsWoodBadge)); } }

    /// <summary>Shows the Wood Badge mark (SPEC.md D-20).</summary>
    public bool IsWoodBadge => WoodBadge == "Y";

    /// <summary>IDs of the scouts this adult introduces, "|"-separated.</summary>
    public string Supporting { get => _supporting; private set => Set(ref _supporting, value); }

    public bool IsDisabled => Room == AdultRoom.Disabled;

    /// <summary>On a board now.</summary>
    public bool IsBusy => Room.Length > 0 && !IsDisabled;

    /// <summary>"Gone home" instead of the file's "N/A".</summary>
    public string RoomText => IsDisabled ? "Gone home" : Room;

    /// <summary>Someone on a board or gone home can't be ticked for another.</summary>
    public bool CanPick => Room.Length == 0;

    public string? PickTip => IsDisabled ? "Gone home. Mark them back on the Adults page first."
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
