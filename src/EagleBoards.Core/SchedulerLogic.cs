using System.Globalization;
using System.Text;
using EagleBoards.Core.Records;

namespace EagleBoards.Core;

/// <summary>
/// What the scheduler needs to know about an adult to pick or locate them.
/// FreeSince is when they last became free to volunteer (see
/// <see cref="SchedulerLogic.FreeSinceTimes"/>); blank sorts first. WoodBadge
/// is "Y" or blank, and Supporting the "|"-separated IDs of the scouts they
/// came to support, as said at sign-in.
/// </summary>
public sealed record AdultInfo(string Id, string Last, string First, string UnitName, string Room, string FinalBoard, string ProjectReview,
    string FreeSince = "", string WoodBadge = "", string Supporting = "")
{
    public string RoleFor(string boardType) => boardType == BoardTypes.Project ? ProjectReview : FinalBoard;

    /// <summary>Not on a board and not stood down for the night.</summary>
    public bool IsFree => Room is "" or "-";

    /// <summary>Said at sign-in they came to support this scout.</summary>
    public bool Supports(string scoutId) => Supporting.Split('|').Contains(scoutId);

    /// <summary>
    /// Came to serve on any board: not here for a particular scout, or
    /// counting tonight toward a Wood Badge ticket item (who is then a
    /// volunteer first, whoever else they came with).
    /// </summary>
    public bool CameForAnyBoard => WoodBadge == "Y" || Supporting.Length == 0;
}

public sealed record ScoutInfo(string Id, string Last, string First, string UnitName, string BoardType, string Room, string Status, string Leader);

public sealed record RoomInfo(string Id, string Room, string BoardType, string Scout);

/// <summary>
/// Someone found by name (<see cref="SchedulerLogic.FindPeople"/>): the room
/// they're in, or null and where they are instead, in words ("is waiting").
/// </summary>
public sealed record PersonPlace(string Id, string Name, bool IsYouth, string? Room, string Where);

/// <summary>An auto-selected board: who, where, and what couldn't be found.</summary>
public sealed record AutoSelection(IReadOnlyList<string> ChairIds, IReadOnlyList<string> MemberIds, string? RoomId, IReadOnlyList<string> Problems)
{
    public IEnumerable<string> AllAdultIds => ChairIds.Concat(MemberIds);
}

/// <summary>
/// An adult found for a scout: one who said at sign-in they came to support
/// them (<paramref name="IsSupporting"/>), else a leader guessed from the
/// scout's Leader field, else a parent.
/// </summary>
public sealed record LocatedAdult(AdultInfo Adult, bool IsLeader, bool IsSupporting = false);

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
    /// Propose a board for a waiting scout: one qualified chair, the
    /// working-size complement of members (two for a Final board, one for a
    /// project review), none from the scout's unit, and the first empty room
    /// of the right type.
    /// </summary>
    /// <remarks>
    /// It used to take the first qualified chair and the first adults whose
    /// role for the board type was Member, in sign-in order. A Final board's
    /// Member is often a project chair, so the first Final board of the night
    /// could take both project chairs and leave every project review without
    /// one; and it ignored the troops of the scouts still waiting.
    ///
    /// Now every legal board is considered and the one chosen is, in order:
    /// the one leaving the most of the <paramref name="waiting"/> scouts (the
    /// OTHER waiting scouts, in queue order) able to get a full board at once
    /// from the adults left over, so chairs and troop conflicts both count;
    /// then the one using up the fewest chair qualifications, so member-only
    /// adults fill member seats and a single-type chair is used before one who
    /// can chair either; then the one whose adults could serve the fewest
    /// other waiting scouts; then volunteers who came for any board (not linked
    /// to a scout, or Wood Badge), so they are not the ones left idle; then the
    /// adults who have waited longest to volunteer since they were last free,
    /// and sign-in order within a minute. With no full board to be had
    /// it proposes what it can, in the same preference order, and says what
    /// is short.
    ///
    /// The same algorithm is proposeBoard in the Java version's process_seat.js
    /// and BoardSuggestion in the Mac version, and all three run the cases in
    /// eagleboards-shared/cases (SPEC.md D-5; SharedCaseTests here).
    /// </remarks>
    public static AutoSelection AutoSelect(ScoutInfo scout, IReadOnlyList<AdultInfo> adults, IEnumerable<RoomInfo> rooms,
        IReadOnlyList<ScoutInfo>? waiting = null)
    {
        IReadOnlyList<ScoutInfo> queue = waiting ?? [];
        var problems = new List<string>();
        var need = MembersBesideChair(scout.BoardType);

        var pool = RankFreeAdults(adults, queue);
        var chairs = pool.Where(c => CanChairFor(c.Adult, scout)).ToList();
        var sitters = pool.Where(c => CanSitFor(c.Adult, scout)).ToList();

        List<Candidate>? best = null;
        var bestScore = (Seatable: -1, ChairsKept: 0, Flexibility: 0);
        var triedChairs = new HashSet<string>();
        foreach (var chair in chairs)
        {
            if (!triedChairs.Add(Profile(chair.Adult)))
            {
                continue;
            }

            var others = sitters.Where(c => c.Adult.Id != chair.Adult.Id).ToList();
            var combo = new List<Candidate>();

            void Visit(int start)
            {
                if (combo.Count == need)
                {
                    var board = new List<Candidate> { chair };
                    board.AddRange(combo);
                    var taken = board.Select(c => c.Adult.Id).ToHashSet();
                    var score = (CountSeatable(pool.Where(c => !taken.Contains(c.Adult.Id)).ToList(), queue),
                        -board.Sum(c => c.Chairs), -board.Sum(c => c.Useful));
                    if (best == null || score.CompareTo(bestScore) > 0)
                    {
                        bestScore = score;
                        best = board;
                    }

                    return;
                }

                // Adults from the same unit with the same roles are
                // interchangeable here, so only the first is tried in each
                // seat: the same answer from a far smaller search.
                var tried = new HashSet<string>();
                for (var n = start; n < others.Count; n++)
                {
                    if (!tried.Add(Profile(others[n].Adult)))
                    {
                        continue;
                    }

                    combo.Add(others[n]);
                    Visit(n + 1);
                    combo.RemoveAt(combo.Count - 1);
                }
            }

            Visit(0);
        }

        List<string> chairIds;
        List<string> memberIds;
        if (best != null)
        {
            chairIds = [best[0].Adult.Id];
            memberIds = best.Skip(1).Select(c => c.Adult.Id).ToList();
        }
        else
        {
            // No full board: what there is, best first, and what is short.
            chairIds = chairs.Take(1).Select(c => c.Adult.Id).ToList();
            if (chairIds.Count == 0)
            {
                problems.Add($"No {scout.BoardType} Chairs Available.");
            }

            memberIds = sitters.Where(c => !chairIds.Contains(c.Adult.Id)).Take(need).Select(c => c.Adult.Id).ToList();
            if (memberIds.Count < need)
            {
                problems.Add($"Only {memberIds.Count} {scout.BoardType} Members Available");
            }
        }

        var room = rooms.FirstOrDefault(r => r.Scout is "" or "-" && r.BoardType == scout.BoardType);
        if (room == null)
        {
            problems.Add($"No {scout.BoardType} Rooms Available");
        }

        return new AutoSelection(chairIds, memberIds, room?.Id, problems);
    }

    /// <summary>
    /// When each adult last became free to volunteer, for the waited-longest
    /// tie-break: when they signed in, or when the last board they sat on was
    /// completed, whichever is later. Nothing stores the second, so it is read
    /// from the Completed scouts, whose LastUpdateTime is when the result was
    /// recorded and whose member list names who sat. A reset board never
    /// happened and keeps no member list, so the adult's earlier wait stands.
    /// </summary>
    /// <remarks>
    /// Times are the records' <c>yyyy-MM-dd_HH:mm±hhmm</c> stamps, which sort
    /// ordinally within one event night. The member list is comma-joined and
    /// read back from the CSV with '~'; an ID whose name had a comma also holds
    /// a '~', so each whole ID is looked for between separators rather than
    /// splitting the list. The same helper is freeSinceTimes in the Java
    /// version and BoardSuggestion.freeSinceTimes on the Mac.
    /// </remarks>
    public static Dictionary<string, string> FreeSinceTimes(
        IEnumerable<(string Id, string RegTime)> adults,
        IEnumerable<(string Status, string MemberIds, string LastUpdate)> scouts)
    {
        var boards = scouts
            .Where(s => s.Status == BoardStatus.Completed && s.MemberIds.Length > 0 && s.LastUpdate.Length > 0)
            .Select(s => (List: "~" + s.MemberIds.Replace(',', '~') + "~", s.LastUpdate))
            .ToList();
        var since = new Dictionary<string, string>();
        foreach (var (id, regTime) in adults)
        {
            var latest = regTime;
            var needle = "~" + id + "~";
            foreach (var (list, lastUpdate) in boards)
            {
                if (list.Contains(needle, StringComparison.Ordinal) && string.CompareOrdinal(lastUpdate, latest) > 0)
                {
                    latest = lastUpdate;
                }
            }

            since[id] = latest;
        }

        return since;
    }

    private sealed record Candidate(AdultInfo Adult, int Chairs, int Useful, string Since, int Order);

    private static string Profile(AdultInfo a) => a.UnitName + "|" + a.FinalBoard + "|" + a.ProjectReview;

    /// <summary>Same test as <see cref="BoardRules.FindUnitConflicts"/>: a blank unit on either side is no match.</summary>
    private static bool SharesUnit(AdultInfo a, ScoutInfo s) =>
        !string.IsNullOrEmpty(s.UnitName) && !string.IsNullOrEmpty(a.UnitName) && a.UnitName == s.UnitName;

    private static bool CanSitFor(AdultInfo a, ScoutInfo s) =>
        a.RoleFor(s.BoardType) is BoardRoles.Chair or BoardRoles.Member && !SharesUnit(a, s);

    private static bool CanChairFor(AdultInfo a, ScoutInfo s) =>
        a.RoleFor(s.BoardType) == BoardRoles.Chair && !SharesUnit(a, s);

    private static int ChairQualifications(AdultInfo a) =>
        (a.FinalBoard == BoardRoles.Chair ? 1 : 0) + (a.ProjectReview == BoardRoles.Chair ? 1 : 0);

    /// <summary>Members beside the chair at the district's working size.</summary>
    private static int MembersBesideChair(string boardType) => BoardRules.MinMembers(boardType) - 1;

    /// <summary>
    /// How many of <paramref name="waiting"/>, in queue order, can each still
    /// get a full board at once from <paramref name="pool"/> (sorted by preference).
    /// </summary>
    private static int CountSeatable(List<Candidate> pool, IReadOnlyList<ScoutInfo> waiting)
    {
        var used = new HashSet<string>();
        var seated = 0;
        foreach (var t in waiting)
        {
            var chair = pool.FirstOrDefault(c => !used.Contains(c.Adult.Id) && CanChairFor(c.Adult, t));
            if (chair == null)
            {
                continue;
            }

            var need = MembersBesideChair(t.BoardType);
            var members = pool.Where(c => !used.Contains(c.Adult.Id) && c != chair && CanSitFor(c.Adult, t)).Take(need).ToList();
            if (members.Count == need)
            {
                used.Add(chair.Adult.Id);
                used.UnionWith(members.Select(c => c.Adult.Id));
                seated++;
            }
        }

        return seated;
    }

    /// <summary>
    /// Complete a board around the adults the operator has already chosen:
    /// a qualified chair if none of them can chair, then members up to the
    /// minimum, taken in <see cref="AutoSelect"/>'s order of preference
    /// (<see cref="RankFreeAdults"/>). Returns only the adults to add; the
    /// operator's choices are never replaced.
    /// </summary>
    public static AutoSelection FillBoard(ScoutInfo scout, IReadOnlyList<AdultInfo> adults, IReadOnlyCollection<string> pickedIds,
        IReadOnlyList<ScoutInfo>? waiting = null)
    {
        var problems = new List<string>();
        var pool = RankFreeAdults(adults, waiting ?? []).Where(c => !pickedIds.Contains(c.Adult.Id)).ToList();
        var picked = adults.Where(a => pickedIds.Contains(a.Id)).ToList();

        var chairs = new List<string>();
        if (picked.All(a => a.RoleFor(scout.BoardType) != BoardRoles.Chair))
        {
            if (pool.FirstOrDefault(c => CanChairFor(c.Adult, scout)) is { } chair)
            {
                chairs.Add(chair.Adult.Id);
            }
            else
            {
                problems.Add($"No {scout.BoardType} Chairs Available.");
            }
        }

        var wanted = Math.Max(0, BoardRules.MinMembers(scout.BoardType) - pickedIds.Count - chairs.Count);
        var members = pool.Where(c => !chairs.Contains(c.Adult.Id) && CanSitFor(c.Adult, scout))
            .Take(wanted).Select(c => c.Adult.Id).ToList();
        if (members.Count < wanted)
        {
            problems.Add($"Only {members.Count} {scout.BoardType} Members Available");
        }

        return new AutoSelection(chairs, members, null, problems);
    }

    /// <summary>
    /// The free adults in order of preference for a seat: fewest chair
    /// qualifications first (keep chairs for boards to come), then those who
    /// could serve the fewest other <paramref name="queue"/> scouts, then
    /// volunteers who came for any board, then the longest since last free,
    /// then sign-in order.
    /// </summary>
    private static List<Candidate> RankFreeAdults(IReadOnlyList<AdultInfo> adults, IReadOnlyList<ScoutInfo> queue) =>
        adults
            .Select((a, order) => new Candidate(a, ChairQualifications(a), queue.Count(w => CanSitFor(a, w)), a.FreeSince, order))
            .Where(c => c.Adult.IsFree)
            .OrderBy(c => c.Chairs).ThenBy(c => c.Useful).ThenBy(c => c.Adult.CameForAnyBoard ? 0 : 1)
            .ThenBy(c => c.Since, StringComparer.Ordinal).ThenBy(c => c.Order)
            .ToList();

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

        // Those who linked themselves to this scout at sign-in come first and
        // are not guessed at again.
        var supporting = SupportingAdults(scout.Id, adults);
        return supporting.Select(a => new LocatedAdult(a, true, IsSupporting: true))
            .Concat(leaders.Where(l => !supporting.Contains(l)).Select(a => new LocatedAdult(a, true)))
            .Concat(parents.Where(p => !leaders.Contains(p) && !supporting.Contains(p)).Select(a => new LocatedAdult(a, false)))
            .ToList();
    }

    /// <summary>
    /// The adults who said at sign-in that they came to support this scout --
    /// often their Scoutmaster, who may be on another board and has to be
    /// fetched to introduce them when their review starts.
    /// </summary>
    public static List<AdultInfo> SupportingAdults(string scoutId, IEnumerable<AdultInfo> adults) =>
        adults.Where(a => a.Supports(scoutId)).ToList();

    /// <summary>
    /// An adult's Supporting list ("|"-separated scout IDs) with one scout
    /// linked or unlinked. Order is kept and a scout is never listed twice.
    /// The same as withSupportLink in the Java version's process_seat.js.
    /// </summary>
    public static string WithSupportLink(string supporting, string scoutId, bool linked)
    {
        var ids = supporting.Split('|').Where(id => id.Length > 0 && id != scoutId).ToList();
        if (linked)
        {
            ids.Add(scoutId);
        }

        return string.Join('|', ids);
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

    /// <summary>
    /// Everyone signed in whose name has <paramref name="query"/> in it,
    /// ignoring case, youth then adults, each by last name: the room they're
    /// in, or where they are instead (SPEC.md D-21). An empty query finds no one.
    /// </summary>
    public static IReadOnlyList<PersonPlace> FindPeople(string query, IEnumerable<ScoutInfo> youth, IEnumerable<AdultInfo> adults)
    {
        query = query.Trim();
        if (query.Length == 0)
        {
            return [];
        }

        bool Named(string first, string last) => $"{first} {last}".Contains(query, StringComparison.OrdinalIgnoreCase);

        var found = youth.Where(s => Named(s.First, s.Last))
            .OrderBy(s => s.Last, StringComparer.OrdinalIgnoreCase).ThenBy(s => s.First, StringComparer.OrdinalIgnoreCase)
            .Select(s => BoardStatus.IsActive(s.Status)
                ? new PersonPlace(s.Id, $"{s.First} {s.Last}", true, s.Room, "is in room " + s.Room)
                : new PersonPlace(s.Id, $"{s.First} {s.Last}", true, null, s.Status switch
                {
                    BoardStatus.Completed => "has finished",
                    BoardStatus.Postponed => "was postponed",
                    _ => "is waiting",
                }))
            .ToList();
        found.AddRange(adults.Where(a => Named(a.First, a.Last))
            .OrderBy(a => a.Last, StringComparer.OrdinalIgnoreCase).ThenBy(a => a.First, StringComparer.OrdinalIgnoreCase)
            .Select(a => a.Room == AdultRoom.Disabled ? new PersonPlace(a.Id, $"{a.First} {a.Last}", false, null, "has gone home")
                : a.IsFree ? new PersonPlace(a.Id, $"{a.First} {a.Last}", false, null, "isn't on a board")
                : new PersonPlace(a.Id, $"{a.First} {a.Last}", false, a.Room, "is in room " + a.Room)));
        return found;
    }

    /// <summary>
    /// Where a youth sits in the stacked Youth list (SPEC.md O-3): Waiting in
    /// sign-in order, then On a board by room, then Finished with the most
    /// recent first. Compare the keys ordinally.
    /// </summary>
    public static string QueueSortKey(string status, string regNum, string room, string lastUpdateTime)
    {
        if (BoardStatus.IsWaiting(status))
        {
            var (rank, number) = RegNumSortKey(regNum);
            return string.Create(CultureInfo.InvariantCulture, $"0{rank}{number:D10}");
        }

        if (BoardStatus.IsActive(status))
        {
            return "1" + RoomSortKey(room);
        }

        // Newest first; a stamp that can't be read goes last.
        var ticks = DataRecord.ParseTime(lastUpdateTime)?.UtcTicks ?? 0;
        return string.Create(CultureInfo.InvariantCulture, $"2{long.MaxValue - ticks:D19}");
    }

    /// <summary>
    /// Rooms as people read them: the numbers in a name compared as numbers,
    /// so 2 comes before 10 and 102 before 200A, and letters regardless of case.
    /// </summary>
    public static string RoomSortKey(string room)
    {
        var key = new StringBuilder(room.Length + 16);
        for (var i = 0; i < room.Length;)
        {
            if (!char.IsAsciiDigit(room[i]))
            {
                key.Append(char.ToUpperInvariant(room[i++]));
                continue;
            }

            var start = i;
            while (i < room.Length && char.IsAsciiDigit(room[i]))
            {
                i++;
            }

            key.Append(room[start..i].TrimStart('0').PadLeft(10, '0'));
        }

        return key.ToString();
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
