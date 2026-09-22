namespace EagleBoards.Core;

public enum SizeVerdict
{
    /// <summary>Seat it.</summary>
    Ok,

    /// <summary>Below the minimum: refused.</summary>
    TooFew,

    /// <summary>Above the maximum: refused, not overridable.</summary>
    TooMany,

    /// <summary>Legal but larger than the working size: ask the operator to confirm.</summary>
    OverPreferred,
}

/// <summary>A board member as the composition rules see them.</summary>
public sealed record BoardCandidate(string Id, string Last, string First, string UnitName);

/// <summary>
/// Board composition rules, kept pure so they can be unit-tested with no UI
/// and no files. The scheduler window uses them to explain and (where
/// allowed) offer an override; <see cref="BoardService.SeatBoard"/> enforces
/// the hard ones again, because the window is not the only way in.
/// </summary>
public static class BoardRules
{
    /// <summary>
    /// Guide to Advancement 8.0.0.3: a board of review has "no fewer than
    /// three and no more than six members". Three is the district's working
    /// size, so four to six asks for confirmation and seven is refused.
    /// </summary>
    public const int BoardMinMembers = 3;

    public const int BoardMaxMembers = 6;

    /// <summary>
    /// A project proposal review is not a board of review (GTA 9.0.2.4); this
    /// district runs it with two, under the same ceiling of six.
    /// </summary>
    public const int ProjectMinMembers = 2;

    public static SizeVerdict CheckBoardSize(int count) => Verdict(count, BoardMinMembers);

    public static SizeVerdict CheckProjectSize(int count) => Verdict(count, ProjectMinMembers);

    public static SizeVerdict CheckSize(string boardType, int count) =>
        boardType == Records.BoardTypes.Project ? CheckProjectSize(count) : CheckBoardSize(count);

    public static int MinMembers(string boardType) =>
        boardType == Records.BoardTypes.Project ? ProjectMinMembers : BoardMinMembers;

    private static SizeVerdict Verdict(int count, int min)
    {
        if (count < min)
        {
            return SizeVerdict.TooFew;
        }

        if (count > BoardMaxMembers)
        {
            return SizeVerdict.TooMany;
        }

        return count > min ? SizeVerdict.OverPreferred : SizeVerdict.Ok;
    }

    /// <summary>
    /// The members who share the scout's unit -- every one of them, not just
    /// the first, so a stacked board is fixed in one pass. A blank unit on
    /// either side is unknown, not a match. Comparison is exact: UnitName is
    /// already normalized ("Troop1234"), so a case difference is a real one.
    /// </summary>
    public static IReadOnlyList<BoardCandidate> FindUnitConflicts(string scoutUnitName, IEnumerable<BoardCandidate> members)
    {
        if (string.IsNullOrEmpty(scoutUnitName))
        {
            return [];
        }

        return members.Where(m => !string.IsNullOrEmpty(m.UnitName) && m.UnitName == scoutUnitName).ToList();
    }

    /// <summary>
    /// The council's rule (no adults from the scout's unit) may be overridden,
    /// but only down to the national floor, GTA 8.0.3.0 #2: at least one
    /// member not affiliated with the unit. A blank unit counts as outside.
    /// </summary>
    public static bool HasNonUnitMember(string scoutUnitName, IEnumerable<BoardCandidate> members)
    {
        if (string.IsNullOrEmpty(scoutUnitName))
        {
            return true;
        }

        return members.Any(m => string.IsNullOrEmpty(m.UnitName) || m.UnitName != scoutUnitName);
    }
}
