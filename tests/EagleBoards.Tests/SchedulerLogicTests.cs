using EagleBoards.Core;
using EagleBoards.Core.Records;

namespace EagleBoards.Tests;

public class SchedulerLogicTests
{
    private static AdultInfo A(string id, string unit, string project, string final, string room = "") =>
        new(id, id, id, unit, room, final, project);

    private static readonly ScoutInfo FinalScout = new("S1", "Aldridge", "Alex", "Troop1001", BoardTypes.Final, "", BoardStatus.Registered, "Sam Smith");

    [Fact]
    public void AutoSelectProposesAChairTwoMembersAndARoom()
    {
        var adults = new[]
        {
            A("busy-chair", "Troop2", "Member", "Chair", room: "101"),
            A("same-unit-chair", "Troop1001", "Member", "Chair"),
            A("chair", "Troop3", "Member", "Chair"),
            A("unavailable", "Troop4", "Member", "Unavailable"),
            A("m1", "Troop5", "Member", "Member"),
            A("disabled", "Troop6", "Member", "Member", room: AdultRoom.Disabled),
            A("m2", "Troop7", "Member", "Member"),
            A("m3", "Troop8", "Member", "Member"),
        };
        var rooms = new[]
        {
            new RoomInfo("ROOM:200", "200", BoardTypes.Project, ""),
            new RoomInfo("ROOM:101", "101", BoardTypes.Final, "Someone Else"),
            new RoomInfo("ROOM:102", "102", BoardTypes.Final, ""),
        };

        var pick = SchedulerLogic.AutoSelect(FinalScout, adults, rooms);
        Assert.Equal(["chair"], pick.ChairIds);
        Assert.Equal(["m1", "m2"], pick.MemberIds);
        Assert.Equal("ROOM:102", pick.RoomId);
        Assert.Empty(pick.Problems);
    }

    [Fact]
    public void AutoSelectTopsUpFromSpareChairsAndSaysWhatIsMissing()
    {
        var adults = new[] { A("c1", "Troop2", "Member", "Chair"), A("c2", "Troop3", "Member", "Chair") };
        var pick = SchedulerLogic.AutoSelect(FinalScout, adults, []);
        Assert.Equal(["c1"], pick.ChairIds);
        Assert.Equal(["c2"], pick.MemberIds);
        Assert.Contains("Only 1 Final Members Available", pick.Problems);
        Assert.Contains("No Final Rooms Available", pick.Problems);
    }

    [Fact]
    public void AProjectReviewNeedsOneMemberBesideTheChair()
    {
        var scout = FinalScout with { BoardType = BoardTypes.Project };
        var adults = new[] { A("pc", "Troop2", "Chair", "Member"), A("m1", "Troop3", "Member", "Member"), A("m2", "Troop4", "Member", "Member") };
        var pick = SchedulerLogic.AutoSelect(scout, adults, [new RoomInfo("ROOM:200A", "200A", BoardTypes.Project, "")]);
        Assert.Equal(["pc", "m1"], pick.AllAdultIds);
    }

    [Fact]
    public void LocateFindsLeadersAndParents()
    {
        var adults = new[]
        {
            new AdultInfo("L", "Smith", "Sam", "Troop1001", "", "Member", "Member"),
            new AdultInfo("P", "Aldridge", "Pat", "Troop1001", "102", "Member", "Member"),
            new AdultInfo("X", "Jones", "Jo", "Troop9", "", "Member", "Member"),
        };
        var found = SchedulerLogic.Locate(FinalScout, adults, includeParents: true);
        Assert.Equal([("L", true), ("P", false)], found.Select(f => (f.Adult.Id, f.IsLeader)));
        Assert.Equal(["L"], SchedulerLogic.Locate(FinalScout, adults, includeParents: false).Select(f => f.Adult.Id));
    }

    [Theory]
    [InlineData(BoardStatus.Registered, ScoutActions.Seat | ScoutActions.Postpone | ScoutActions.Locate)]
    [InlineData(BoardStatus.Seated, ScoutActions.Start | ScoutActions.Reset | ScoutActions.Locate)]
    [InlineData(BoardStatus.InProgress, ScoutActions.Complete | ScoutActions.Reset | ScoutActions.Locate)]
    [InlineData(BoardStatus.Completed, ScoutActions.Locate)]
    [InlineData("", ScoutActions.None)]
    public void EachStatusOffersItsNextSteps(string status, ScoutActions expected) =>
        Assert.Equal(expected, SchedulerLogic.ActionsFor(status));

    [Fact]
    public void ConveningGoesStraightToRedAndTheInterviewHasYellowThenRed()
    {
        var config = new ConfigRecord();
        Assert.Equal(TimerState.Okay, SchedulerLogic.TimerFor(BoardStatus.Seated, BoardTypes.Final, 29, config));
        Assert.Equal(TimerState.Overdue, SchedulerLogic.TimerFor(BoardStatus.Seated, BoardTypes.Final, 30, config));
        Assert.Equal(TimerState.Okay, SchedulerLogic.TimerFor(BoardStatus.InProgress, BoardTypes.Final, 29, config));
        Assert.Equal(TimerState.Warning, SchedulerLogic.TimerFor(BoardStatus.InProgress, BoardTypes.Final, 30, config));
        Assert.Equal(TimerState.Overdue, SchedulerLogic.TimerFor(BoardStatus.InProgress, BoardTypes.Final, 45, config));
        Assert.Equal(TimerState.Warning, SchedulerLogic.TimerFor(BoardStatus.InProgress, BoardTypes.Project, 25, config));
        Assert.Equal(TimerState.None, SchedulerLogic.TimerFor(BoardStatus.Registered, BoardTypes.Final, 99, config));
    }

    [Fact]
    public void PreRegisteredSortAheadOfWalkInsAndNumbersSortNumerically()
    {
        var sorted = new[] { "W10", "P2", "W9", "", "P10", "W1" }.OrderBy(SchedulerLogic.RegNumSortKey).ToArray();
        Assert.Equal(["P2", "P10", "W1", "W9", "W10", ""], sorted);
    }

    [Fact]
    public void TheYouthListStacksWaitingThenOnABoardByRoomThenFinishedNewestFirst()
    {
        // SPEC.md O-3 (amended): one list, three groups, each in its own order.
        var youth = new (string Name, string Status, string RegNum, string Room, string Updated)[]
        {
            ("finished early", BoardStatus.Completed, "P1", AdultRoom.Disabled, "2026-09-27_18:05-0400"),
            ("in 200A", BoardStatus.InProgress, "W1", "200A", "2026-09-27_19:00-0400"),
            ("walk-in", BoardStatus.Registered, "W2", "", "2026-09-27_18:00-0400"),
            ("postponed late", BoardStatus.Postponed, "W3", "", "2026-09-27_19:40-0400"),
            ("in 102", BoardStatus.Seated, "P4", "102", "2026-09-27_19:30-0400"),
            ("finished, another zone", BoardStatus.Completed, "P5", AdultRoom.Disabled, "2026-09-27_23:20+0000"),
            ("pre-registered", BoardStatus.Registered, "P10", "", "2026-09-27_19:50-0400"),
            ("in 20", BoardStatus.Seated, "P6", "20", "2026-09-27_19:45-0400"),
            ("unreadable", BoardStatus.Completed, "P7", AdultRoom.Disabled, ""),
            ("in 200b", BoardStatus.InProgress, "P8", "200b", "2026-09-27_18:30-0400"),
        };

        var order = youth.OrderBy(y => SchedulerLogic.QueueSortKey(y.Status, y.RegNum, y.Room, y.Updated), StringComparer.Ordinal)
            .Select(y => y.Name).ToArray();

        Assert.Equal(
        [
            "pre-registered", "walk-in",
            "in 20", "in 102", "in 200A", "in 200b",
            "postponed late", "finished, another zone", "finished early", "unreadable",
        ], order);
    }

    [Theory]
    [InlineData("Troop2", "T2")]
    [InlineData("Crew1776", "C1776")]
    [InlineData("District", "District")]
    [InlineData("Troop", "Troop")]
    [InlineData("", "")]
    public void NumberedUnitsAreAbbreviatedForDisplayOnly(string unitName, string label) =>
        Assert.Equal(label, SchedulerLogic.UnitLabel(unitName));

    [Fact]
    public void FillKeepsTheOperatorsChoiceAndAddsAChairAndOneMember()
    {
        // The operator wants m9 (a member from another unit) on the board.
        var adults = new[] { A("m9", "Troop9", "Member", "Member"), A("chair", "Troop3", "Member", "Chair"), A("m1", "Troop5", "Member", "Member"), A("m2", "Troop7", "Member", "Member") };
        var fill = SchedulerLogic.FillBoard(FinalScout, adults, ["m9"]);
        Assert.Equal(["chair"], fill.ChairIds);
        Assert.Equal(["m1"], fill.MemberIds);
        Assert.Empty(fill.Problems);
    }

    [Fact]
    public void FillAddsNoChairWhenTheOperatorChoseOne()
    {
        var adults = new[] { A("c9", "Troop9", "Member", "Chair"), A("chair", "Troop3", "Member", "Chair"), A("m1", "Troop5", "Member", "Member"), A("m2", "Troop7", "Member", "Member") };
        var fill = SchedulerLogic.FillBoard(FinalScout, adults, ["c9"]);
        Assert.Empty(fill.ChairIds);
        Assert.Equal(["m1", "m2"], fill.MemberIds);
    }

    [Fact]
    public void FillOnAFullBoardAddsNobody()
    {
        var adults = new[] { A("c", "Troop2", "Member", "Chair"), A("a", "Troop3", "Member", "Member"), A("b", "Troop4", "Member", "Member"), A("m", "Troop5", "Member", "Member") };
        var fill = SchedulerLogic.FillBoard(FinalScout, adults, ["c", "a", "b"]);
        Assert.Empty(fill.AllAdultIds);
    }

    [Fact]
    public void FillSaysWhatIsMissing()
    {
        var fill = SchedulerLogic.FillBoard(FinalScout, [A("m9", "Troop9", "Member", "Member")], ["m9"]);
        Assert.Contains("No Final Chairs Available.", fill.Problems);
        Assert.Contains("Only 0 Final Members Available", fill.Problems);
    }

    // ------------------------------------------------------------------
    // Auto-select weighing the whole waiting line. The same cases, in the
    // same order, are in the Java version's scripts/test-seat-conflicts.js
    // and the Mac version's BoardRulesTests; keep all three in step.
    // Arguments are in the Java test's order: final role, then project.
    // ------------------------------------------------------------------

    private static AdultInfo Pool(string id, string unit, string final, string project, string room = "") =>
        new(id, id, id, unit, room, final, project);

    private static ScoutInfo Queue(string id, string unit, string boardType) =>
        new(id, id, id, unit, boardType, "", BoardStatus.Registered, "");

    private static AutoSelection Propose(ScoutInfo scout, AdultInfo[] adults, params ScoutInfo[] waiting) =>
        SchedulerLogic.AutoSelect(scout, adults, [new RoomInfo("ROOM:1", "1", scout.BoardType, "")], waiting);

    [Fact]
    public void MemberOnlyAdultsFillTheMemberSeatsNotAProjectChair()
    {
        var pick = Propose(Queue("S", "Troop1001", BoardTypes.Final),
        [
            Pool("FC", "Troop9001", "Chair", "Member"),
            Pool("PC", "Troop9002", "Member", "Chair"),
            Pool("M1", "Troop9003", "Member", "Member"),
            Pool("M2", "Troop9004", "Member", "Member"),
        ]);
        Assert.Equal(["FC"], pick.ChairIds);
        Assert.Equal(["M1", "M2"], pick.MemberIds);
        Assert.Empty(pick.Problems);
    }

    [Fact]
    public void AChairOfOneKindIsUsedBeforeAChairOfBoth()
    {
        var pick = Propose(Queue("S", "Troop1001", BoardTypes.Final),
        [
            Pool("BOTH", "Troop9001", "Chair", "Chair"),
            Pool("FC", "Troop9002", "Chair", "Member"),
            Pool("M1", "Troop9003", "Member", "Member"),
            Pool("M2", "Troop9004", "Member", "Member"),
        ]);
        Assert.Equal(["FC"], pick.ChairIds);
    }

    [Fact]
    public void WhenMemberOnlyAdultsRunOutAChairCapableAdultFillsTheSeat()
    {
        var pick = Propose(Queue("S", "Troop1001", BoardTypes.Final),
        [
            Pool("FC", "Troop9001", "Chair", "Member"),
            Pool("PC", "Troop9002", "Member", "Chair"),
            Pool("M1", "Troop9003", "Member", "Member"),
        ]);
        Assert.Equal(["FC", "M1", "PC"], pick.AllAdultIds);
        Assert.Empty(pick.Problems);
    }

    [Fact]
    public void AnAdultWhoCannotServeTheNextScoutsTroopIsUsedHere()
    {
        var pick = Propose(Queue("S", "Troop1001", BoardTypes.Project),
        [
            Pool("P1", "Troop3001", "Member", "Chair"),
            Pool("P2", "Troop3002", "Member", "Chair"),
            Pool("B", "Troop4001", "Member", "Member"),
            Pool("A", "Troop2001", "Member", "Member"),
        ], Queue("T", "Troop2001", BoardTypes.Project));
        Assert.Equal(["P1", "A"], pick.AllAdultIds);
    }

    [Fact]
    public void AScoutStillWaitingWhoCanBeSeatedOutranksAChairKeptForLater()
    {
        var pick = Propose(Queue("S", "Troop1001", BoardTypes.Final),
        [
            Pool("FC1", "Troop3001", "Chair", "Member"),
            Pool("FC2", "Troop3002", "Chair", "Member"),
            Pool("M", "Troop3003", "Member", "Member"),
            Pool("Y", "Troop2001", "Member", "Chair"),
            Pool("Z", "Troop2001", "Member", "Member"),
            Pool("W", "Troop3004", "Member", "Member"),
        ], Queue("T", "Troop2001", BoardTypes.Final));
        Assert.Equal(["FC1", "Z", "Y"], pick.AllAdultIds);
    }

    [Fact]
    public void AutoSelectNeverProposesSomeoneWhoCannotSit()
    {
        var pick = Propose(Queue("S", "Troop1001", BoardTypes.Final),
        [
            Pool("SAME", "Troop1001", "Chair", "Chair"),
            Pool("BUSY", "Troop9001", "Chair", "Chair", "101"),
            Pool("GONE", "Troop9002", "Chair", "Chair", AdultRoom.Disabled),
            Pool("NOPE", "Troop9003", "Unavailable", "Member"),
            Pool("FC", "Troop9004", "Chair", "Member"),
            Pool("M1", "Troop9005", "Member", "Member"),
            Pool("M2", "Troop9006", "Member", "Member"),
        ]);
        Assert.Equal(["FC", "M1", "M2"], pick.AllAdultIds);
    }

    [Fact]
    public void WithNoChairItStillProposesTheMembersAndSaysSo()
    {
        var pick = Propose(Queue("S", "Troop1001", BoardTypes.Final),
            [Pool("M1", "Troop9001", "Member", "Member"), Pool("M2", "Troop9002", "Member", "Member")]);
        Assert.Empty(pick.ChairIds);
        Assert.Equal(["M1", "M2"], pick.MemberIds);
        Assert.Equal(["No Final Chairs Available."], pick.Problems);
    }

    [Fact]
    public void WithTooFewMembersItProposesWhatThereIsAndSaysSo()
    {
        var pick = Propose(Queue("S", "Troop1001", BoardTypes.Final),
            [Pool("FC", "Troop9001", "Chair", "Member"), Pool("M1", "Troop9002", "Member", "Member")]);
        Assert.Equal(["FC", "M1"], pick.AllAdultIds);
        Assert.Equal(["Only 1 Final Members Available"], pick.Problems);
    }

    /// <summary>
    /// The event test's shape: 9 Final and 5 Project scouts, 30 adults, five
    /// of whom chair anything. Proposing boards down the queue must reach the
    /// chair cap of five at once; sign-in order stalled at three, because the
    /// first Final board took both project chairs as its members.
    /// </summary>
    [Fact]
    public void AWholeEventSeatsFiveBoardsAtOnce()
    {
        var adults = new List<AdultInfo>
        {
            Pool("FC1", "Troop2001", "Chair", "Chair"),
            Pool("FC2", "Troop2002", "Chair", "Member"),
            Pool("FC3", "Troop2003", "Chair", "Member"),
            Pool("PC1", "Troop2004", "Member", "Chair"),
            Pool("PC2", "Troop2005", "Member", "Chair"),
        };
        for (var n = 6; n <= 25; n++)
        {
            adults.Add(Pool("M" + n, "Troop" + (2000 + n), "Member", "Member"));
        }

        adults.Add(Pool("U26", "Troop2026", "Member", "Unavailable"));
        adults.Add(Pool("U27", "Troop2027", "Member", "Unavailable"));
        adults.Add(Pool("U28", "Troop1001", "Unavailable", "Member"));
        adults.Add(Pool("U29", "Troop1002", "Unavailable", "Member"));
        adults.Add(Pool("U30", "Troop1003", "Unavailable", "Member"));

        var queue = Enumerable.Range(1, 14)
            .Select(q => Queue("S" + q, "Troop" + (1000 + q), q <= 9 ? BoardTypes.Final : BoardTypes.Project))
            .ToList();
        var seated = new HashSet<string>();
        var boards = new Dictionary<string, int> { [BoardTypes.Final] = 0, [BoardTypes.Project] = 0 };
        for (var s = 0; s < queue.Count; s++)
        {
            var stillWaiting = queue.Where(t => t != queue[s] && !seated.Contains(t.Id)).ToArray();
            var pick = Propose(queue[s], adults.ToArray(), stillWaiting);
            if (pick.Problems.Count == 0)
            {
                seated.Add(queue[s].Id);
                boards[queue[s].BoardType]++;
                var room = "R" + s;
                adults = adults.Select(a => pick.AllAdultIds.Contains(a.Id) ? a with { Room = room } : a).ToList();
            }
        }

        Assert.Equal((3, 2), (boards[BoardTypes.Final], boards[BoardTypes.Project]));
    }

    // ------------------------------------------------------------------
    // The adults who have waited longest to volunteer go first. The same
    // cases are in the Java and Mac versions.
    // ------------------------------------------------------------------

    [Fact]
    public void FreeSinceIsSignInOrWhenTheirLastBoardCompleted()
    {
        var since = SchedulerLogic.FreeSinceTimes(
        [
            ("ADULT:Able:Ann:1", "2026-09-24_19:00-0400"),
            ("ADULT:Baker:Bo:2", "2026-09-24_19:10-0400"),
            ("ADULT:Cole:Cy:3", "2026-09-24_19:05-0400"),
            ("ADULT:Whitmore~ Jr.:Lysander:4", "2026-09-24_19:00-0400"),
            ("ADULT:Lee:Al:1", "2026-09-24_19:00-0400"),
        ],
        [
            (BoardStatus.Completed, "ADULT:Able:Ann:1,ADULT:Other:Oz:9", "2026-09-24_19:40-0400"),
            // As it reads back after a restart: the CSV stored the commas as '~'.
            (BoardStatus.Completed, "ADULT:X:X:9~ADULT:Whitmore~ Jr.:Lysander:4~ADULT:Lee:Al:12", "2026-09-24_19:50-0400"),
            (BoardStatus.Registered, "", "2026-09-24_20:00-0400"),   // a reset board
            (BoardStatus.Seated, "ADULT:Cole:Cy:3", "2026-09-24_20:05-0400"),
        ]);
        Assert.Equal("2026-09-24_19:40-0400", since["ADULT:Able:Ann:1"]);   // came off a completed board
        Assert.Equal("2026-09-24_19:10-0400", since["ADULT:Baker:Bo:2"]);   // has not sat
        Assert.Equal("2026-09-24_19:05-0400", since["ADULT:Cole:Cy:3"]);    // a running board does not count
        Assert.Equal("2026-09-24_19:50-0400", since["ADULT:Whitmore~ Jr.:Lysander:4"]);
        Assert.Equal("2026-09-24_19:00-0400", since["ADULT:Lee:Al:1"]);     // not mistaken for ADULT:Lee:Al:12
    }

    private static AdultInfo Waited(AdultInfo adult, string freeSince) => adult with { FreeSince = freeSince };

    [Fact]
    public void AmongEqualsThoseWhoHaveWaitedLongestAreProposed()
    {
        var pick = Propose(Queue("S", "Troop1001", BoardTypes.Final),
        [
            Waited(Pool("FC", "Troop9001", "Chair", "Member"), "2026-09-24_19:00-0400"),
            Waited(Pool("M1", "Troop9002", "Member", "Member"), "2026-09-24_19:40-0400"),
            Waited(Pool("M2", "Troop9003", "Member", "Member"), "2026-09-24_19:10-0400"),
            Waited(Pool("M3", "Troop9004", "Member", "Member"), "2026-09-24_19:20-0400"),
        ]);
        Assert.Equal(["M2", "M3"], pick.MemberIds);
    }

    [Fact]
    public void WaitingLongestDoesNotOutrankKeepingAChairFree()
    {
        var pick = Propose(Queue("S", "Troop1001", BoardTypes.Final),
        [
            Waited(Pool("FC", "Troop9001", "Chair", "Member"), "2026-09-24_19:00-0400"),
            Waited(Pool("PC", "Troop9002", "Member", "Chair"), "2026-09-24_18:30-0400"),
            Waited(Pool("M1", "Troop9003", "Member", "Member"), "2026-09-24_19:30-0400"),
            Waited(Pool("M2", "Troop9004", "Member", "Member"), "2026-09-24_19:35-0400"),
        ]);
        Assert.Equal(["M1", "M2"], pick.MemberIds);
    }

    // ------------------------------------------------------------------
    // Volunteers who came for any board go before a scout's own leaders.
    // The same cases are in the Java and Mac versions.
    // ------------------------------------------------------------------

    [Fact]
    public void AnUnattachedVolunteerIsProposedBeforeAScoutsLeaderWhoHasWaitedLonger()
    {
        var pick = Propose(Queue("S", "Troop1001", BoardTypes.Final),
        [
            Pool("FC", "Troop9001", "Chair", "Member"),
            Pool("LEAD", "Troop9002", "Member", "Member") with { Supporting = "SCOUT:Other:Oli:3001", FreeSince = "2026-09-24_18:00-0400" },
            Pool("V1", "Troop9003", "Member", "Member") with { FreeSince = "2026-09-24_19:30-0400" },
            Pool("V2", "Troop9004", "Member", "Member") with { FreeSince = "2026-09-24_19:40-0400" },
        ]);
        Assert.Equal(["V1", "V2"], pick.MemberIds);
    }

    [Fact]
    public void AWoodBadgeVolunteerCountsAsHereForAnyBoard()
    {
        var pick = Propose(Queue("S", "Troop1001", BoardTypes.Final),
        [
            Pool("FC", "Troop9001", "Chair", "Member"),
            Pool("LEAD", "Troop9002", "Member", "Member") with { Supporting = "SCOUT:Other:Oli:3001", FreeSince = "2026-09-24_18:00-0400" },
            Pool("WB", "Troop9003", "Member", "Member") with { Supporting = "SCOUT:Other:Oli:3001", WoodBadge = "Y", FreeSince = "2026-09-24_19:00-0400" },
            Pool("V", "Troop9004", "Member", "Member") with { FreeSince = "2026-09-24_19:30-0400" },
        ]);
        Assert.Equal(["WB", "V"], pick.MemberIds);
    }

    [Fact]
    public void ComingForAnyBoardDoesNotOutrankKeepingAChairFree()
    {
        var pick = Propose(Queue("S", "Troop1001", BoardTypes.Final),
        [
            Pool("FC", "Troop9001", "Chair", "Member"),
            Pool("PC", "Troop9002", "Member", "Chair"),
            Pool("L1", "Troop9003", "Member", "Member") with { Supporting = "SCOUT:A:A:1" },
            Pool("L2", "Troop9004", "Member", "Member") with { Supporting = "SCOUT:B:B:2" },
        ]);
        Assert.Equal(["L1", "L2"], pick.MemberIds);
    }

    [Fact]
    public void LocateNamesTheAdultsWhoCameToSupportTheScoutFirst()
    {
        var adults = new[]
        {
            new AdultInfo("L", "Smith", "Sam", "Troop1001", "", "Member", "Member"),
            new AdultInfo("SM", "Jones", "Jo", "Troop1001", "104", "Member", "Member", Supporting: "SCOUT:X|S1"),
        };
        var found = SchedulerLogic.Locate(FinalScout, adults, includeParents: true);
        Assert.Equal([("SM", true), ("L", false)], found.Select(f => (f.Adult.Id, f.IsSupporting)));
        Assert.Equal(["SM"], SchedulerLogic.SupportingAdults("S1", adults).Select(a => a.Id));
    }

    [Theory]
    [InlineData("", "SCOUT:A:A:1", true, "SCOUT:A:A:1")]
    [InlineData("SCOUT:A:A:1", "SCOUT:B:B:2", true, "SCOUT:A:A:1|SCOUT:B:B:2")]
    [InlineData("SCOUT:A:A:1|SCOUT:B:B:2", "SCOUT:A:A:1", true, "SCOUT:B:B:2|SCOUT:A:A:1")]
    [InlineData("SCOUT:A:A:1|SCOUT:B:B:2", "SCOUT:A:A:1", false, "SCOUT:B:B:2")]
    [InlineData("SCOUT:A:A:1", "SCOUT:A:A:1", false, "")]
    [InlineData("SCOUT:Doe~ Jr.:Jan:1", "SCOUT:B:B:2", true, "SCOUT:Doe~ Jr.:Jan:1|SCOUT:B:B:2")]
    public void LinkingAnAdultToAScoutEditsTheirSupportingList(string supporting, string scoutId, bool linked, string expected) =>
        Assert.Equal(expected, SchedulerLogic.WithSupportLink(supporting, scoutId, linked));
}
