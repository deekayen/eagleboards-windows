using EagleBoards.Core.Records;

namespace EagleBoards.Core;

public enum IssueLevel
{
    /// <summary>Can't be seated like this.</summary>
    Block,

    /// <summary>Allowed, but the operator should know: seating says "Seat anyway".</summary>
    Warn,
}

public sealed record BoardIssue(IssueLevel Level, string Title, string Message);

/// <summary>
/// Everything wrong with a board the operator is putting together, all at
/// once, before they press Seat: the same rules <see cref="BoardService.SeatBoard"/>
/// enforces, plus the ones that are the operator's call (a member from the
/// youth's own unit, a larger than usual board, a room set up for the other
/// board type). The window lists these as it goes; the server still refuses
/// the hard ones, because the window is not the only way in.
/// </summary>
public static class BoardCheck
{
    public static IReadOnlyList<BoardIssue> Review(ScoutInfo scout, IReadOnlyList<AdultInfo> picked, string? chairId, RoomInfo? room)
    {
        var issues = new List<BoardIssue>();
        void Block(string title, string message) => issues.Add(new BoardIssue(IssueLevel.Block, title, message));
        void Warn(string title, string message) => issues.Add(new BoardIssue(IssueLevel.Warn, title, message));

        var type = scout.BoardType;
        if (type is not (BoardTypes.Final or BoardTypes.Project))
        {
            Block("No board type", "Set Final board or Project review for this youth on the Youth page.");
            return issues;
        }

        var kind = type == BoardTypes.Project ? "project review" : "board of review";
        var min = BoardRules.MinMembers(type);

        foreach (var a in picked)
        {
            if (a.Room == AdultRoom.Disabled)
            {
                Block("Gone home", $"**{a.First} {a.Last}** has gone home for tonight. Remove them, or mark them back on the Adults page.");
            }
            else if (!a.IsFree)
            {
                Block("Already on a board", $"**{a.First} {a.Last}** is on the board in room {a.Room}. Remove them.");
            }
            else if (a.RoleFor(type) == BoardRoles.Unavailable)
            {
                Block("Unavailable", $"**{a.First} {a.Last}** isn't available for a {kind}. Remove them.");
            }
        }

        if (picked.Count == 0)
        {
            Block("No members yet", $"Add at least {min} members.");
        }
        else
        {
            var candidates = picked.Select(a => new BoardCandidate(a.Id, a.Last, a.First, a.UnitName)).ToList();
            var conflicts = BoardRules.FindUnitConflicts(scout.UnitName, candidates);
            if (conflicts.Count > 0)
            {
                var names = string.Join(", ", conflicts.Select(c => $"{c.First} {c.Last}"));
                if (!BoardRules.HasNonUnitMember(scout.UnitName, candidates))
                {
                    Block("Everyone is from the youth's unit",
                        "A board needs at least one member from outside the unit (Guide to Advancement 8.0.3.0). Add someone from another unit.");
                }
                else
                {
                    Warn("Same unit",
                        $"{names} {(conflicts.Count == 1 ? "is" : "are")} in the youth's own unit. The council doesn't permit that; "
                        + "the national rule, which this board still meets, needs only one member from outside the unit.");
                }
            }

            var chairs = picked.Where(a => a.RoleFor(type) == BoardRoles.Chair).ToList();
            if (chairs.Count == 0)
            {
                Block("No chair", $"None of the members can chair a {kind}. Add a qualified chair, or make someone a chair on the Adults page.");
            }
            else if (chairId == null || chairs.All(c => c.Id != chairId))
            {
                Block("Choose a chair", "Pick which qualified member chairs the board.");
            }

            switch (BoardRules.CheckSize(type, picked.Count))
            {
                case SizeVerdict.TooFew:
                    Block("Too few members", $"A {kind} needs at least {min}. Add {min - picked.Count} more.");
                    break;
                case SizeVerdict.TooMany:
                    Block("Too many members", $"A {kind} can have at most {BoardRules.BoardMaxMembers} (Guide to Advancement 8.0.0.3). "
                        + $"Remove {picked.Count - BoardRules.BoardMaxMembers}.");
                    break;
                case SizeVerdict.OverPreferred:
                    Warn("Larger than usual", $"{picked.Count} members; {min} is enough for a {kind}.");
                    break;
            }
        }

        if (room == null)
        {
            Block("No room", "Choose a free room.");
        }
        else if (room.Scout is not ("" or "-"))
        {
            Block("Room in use", $"Room {room.Room} already has a board. Choose a free room.");
        }
        else if (room.BoardType != type)
        {
            Warn("Room type", $"Room {room.Room} is set up for {(room.BoardType == BoardTypes.Project ? "project reviews" : "final boards")}.");
        }

        return issues;
    }
}
