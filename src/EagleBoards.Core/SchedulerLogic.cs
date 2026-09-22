using EagleBoards.Core.Records;

namespace EagleBoards.Core;

/// <summary>What the scheduler needs to know about an adult to pick or locate them.</summary>
public sealed record AdultInfo(string Id, string Last, string First, string UnitName, string Room, string FinalBoard, string ProjectReview)
{
    public string RoleFor(string boardType) => boardType == BoardTypes.Project ? ProjectReview : FinalBoard;

    /// <summary>Not on a board and not stood down for the night.</summary>
    public bool IsFree => Room is "" or "-";
}

public sealed record ScoutInfo(string Id, string Last, string First, string UnitName, string BoardType, string Room, string Status, string Leader);

public sealed record RoomInfo(string Id, string Room, string BoardType, string Scout);

/// <summary>An auto-selected board: who, where, and what couldn't be found.</summary>
public sealed record AutoSelection(IReadOnlyList<string> ChairIds, IReadOnlyList<string> MemberIds, string? RoomId, IReadOnlyList<string> Problems)
{
    public IEnumerable<string> AllAdultIds => ChairIds.Concat(MemberIds);
}

public sealed record LocatedAdult(AdultInfo Adult, bool IsLeader);

/// <summary>Toolbar actions on the Youth panel.</summary>
[Flags]
public enum ScoutActions
{
    None = 0,
    Seat = 1,
    Start = 2,
    Complete = 4,
    Locate = 8,
    Reset = 16,
    Postpone = 32,
}

/// <summary>
/// The scheduler's decision-making, separated from the window so it can be
/// tested: which actions a status allows, which adults and room to propose
/// for a waiting scout, and where a scout's leaders and parents are.
/// Ported from scheduler.html, scheduler_scout_grid.js and scheduler_adult_grid.js.
/// </summary>
public static class SchedulerLogic
{
    public static ScoutActions ActionsFor(string status) => status switch
    {
        BoardStatus.Registered => ScoutActions.Seat | ScoutActions.Postpone | ScoutActions.Locate,

        // Legacy records only; nothing sets Verified any more, but a carried-over scout must not be stuck.
        BoardStatus.Verified => ScoutActions.Seat | ScoutActions.Reset | ScoutActions.Postpone | ScoutActions.Locate,

        // Convening: the members have the room, the scout is outside. Complete is withheld.
        BoardStatus.Seated => ScoutActions.Start | ScoutActions.Reset | ScoutActions.Locate,
        BoardStatus.InProgress => ScoutActions.Complete | ScoutActions.Reset | ScoutActions.Locate,
        BoardStatus.Completed or BoardStatus.Postponed => ScoutActions.Locate,
        _ => ScoutActions.None,
    };

    /// <summary>
    /// Up to <paramref name="count"/> free adults whose role for the board
    /// type is one of <paramref name="roles"/>, skipping the scout's own unit
    /// and anyone in <paramref name="omit"/>. Adults are taken in list order.
    /// </summary>
    public static List<string> FindBoardMembers(IEnumerable<AdultInfo> adults, string scoutUnitName, string boardType,
        IReadOnlyCollection<string> roles, int count, IReadOnlyCollection<string> omit)
    {
        var picked = new List<string>();
        foreach (var a in adults)
        {
            if (picked.Count >= count)
            {
                break;
            }

            if (omit.Contains(a.Id) || !a.IsFree || a.UnitName == scoutUnitName)
            {
                continue;
            }

            if (roles.Contains(a.RoleFor(boardType)))
            {
                picked.Add(a.Id);
            }
        }

        return picked;
    }

    /// <summary>
    /// Propose a board for a waiting scout: one qualified chair, then the
    /// working-size complement of members (two for a Final board, one for a
    /// project review), topped up from spare chairs if members run short,
    /// and the first empty room of the right type.
    /// </summary>
    public static AutoSelection AutoSelect(ScoutInfo scout, IReadOnlyList<AdultInfo> adults, IEnumerable<RoomInfo> rooms)
    {
        var problems = new List<string>();
        var chairs = FindBoardMembers(adults, scout.UnitName, scout.BoardType, [BoardRoles.Chair], 1, []);
        if (chairs.Count == 0)
        {
            problems.Add($"No {scout.BoardType} Chairs Available.");
        }

        var wanted = BoardRules.MinMembers(scout.BoardType) - 1;
        var members = FindBoardMembers(adults, scout.UnitName, scout.BoardType, [BoardRoles.Member], wanted, chairs);
        if (members.Count < wanted)
        {
            members.AddRange(FindBoardMembers(adults, scout.UnitName, scout.BoardType,
                [BoardRoles.Member, BoardRoles.Chair], wanted - members.Count, chairs.Concat(members).ToList()));
            if (members.Count < wanted)
            {
                problems.Add($"Only {members.Count} {scout.BoardType} Members Available");
            }
        }

        var room = rooms.FirstOrDefault(r => r.Scout is "" or "-" && r.BoardType == scout.BoardType);
        if (room == null)
        {
            problems.Add($"No {scout.BoardType} Rooms Available");
        }

        return new AutoSelection(chairs, members, room?.Id, problems);
    }

    /// <summary>
    /// Find a scout's leaders (an adult whose last name appears in the
    /// scout's Leader field, and who shares the unit or whose first name also
    /// appears) and parents (same unit and same last name). Once a leader is
    /// found, parents are only included when <paramref name="includeParents"/>.
    /// </summary>
    public static List<LocatedAdult> Locate(ScoutInfo scout, IEnumerable<AdultInfo> adults, bool includeParents)
    {
        var lastLower = scout.Last.ToLowerInvariant();
        var leaderLower = scout.Leader.ToLowerInvariant();
        var leaders = new List<AdultInfo>();
        var parents = new List<AdultInfo>();

        foreach (var a in adults)
        {
            var aLast = a.Last.ToLowerInvariant();
            var aFirst = a.First.ToLowerInvariant();
            var found = false;
            if (leaderLower.Contains(aLast, StringComparison.Ordinal))
            {
                if (scout.UnitName == a.UnitName)
                {
                    leaders.Add(a);
                    found = true;
                }
                else if (leaderLower.Contains(aFirst, StringComparison.Ordinal))
                {
                    leaders.Add(a);
                }
            }

            if (!found && a.UnitName == scout.UnitName && lastLower == aLast)
            {
                parents.Add(a);
            }
        }

        if (leaders.Count > 0 && !includeParents)
        {
            parents.Clear();
        }

        return leaders.Select(a => new LocatedAdult(a, true))
            .Concat(parents.Where(p => !leaders.Contains(p)).Select(a => new LocatedAdult(a, false)))
            .ToList();
    }

    /// <summary>
    /// Room-card timer state for an active board, from the minutes since its
    /// last status change. Seated (convening) has one cap and goes straight
    /// to red; InProgress has board-type specific yellow and red.
    /// </summary>
    public static TimerState TimerFor(string status, string boardType, int minutes, ConfigRecord config)
    {
        int yellow, red;
        switch (status)
        {
            case BoardStatus.Seated:
                yellow = red = config.ConveneRedMins;
                break;
            case BoardStatus.InProgress:
                yellow = boardType == BoardTypes.Final ? config.FinalYellowMins : config.ProjectYellowMins;
                red = boardType == BoardTypes.Final ? config.FinalRedMins : config.ProjectRedMins;
                break;
            default:
                return TimerState.None;
        }

        if (minutes < 0)
        {
            return TimerState.None;
        }

        if (red > 0 && minutes >= red)
        {
            return TimerState.Overdue;
        }

        return minutes >= yellow ? TimerState.Warning : TimerState.Okay;
    }

    /// <summary>
    /// Sort key for the "#" column: pre-registered ("P") before walk-ins
    /// ("W") before anything else, then numerically, so W10 follows W9.
    /// </summary>
    public static (int Rank, int Number) RegNumSortKey(string regNum)
    {
        var rank = regNum.StartsWith('P') ? 0 : regNum.StartsWith('W') ? 1 : 2;
        var digits = new string(regNum.SkipWhile(char.IsAsciiLetter).TakeWhile(char.IsAsciiDigit).ToArray());
        return (rank, int.TryParse(digits, out var n) ? n : 0);
    }

    /// <summary>"Troop2" → "T2" for the narrow Unit columns; unnumbered types keep the whole word.</summary>
    public static string UnitLabel(string unitName)
    {
        if (unitName.Length == 0 || !char.IsLetter(unitName[0]))
        {
            return unitName;
        }

        var i = 0;
        while (i < unitName.Length && char.IsAsciiLetter(unitName[i]))
        {
            i++;
        }

        var number = unitName.Substring(i);
        return number.Length > 0 && number.All(char.IsAsciiDigit) ? unitName[0] + number : unitName;
    }
}

public enum TimerState
{
    None,
    Okay,
    Warning,
    Overdue,
}
