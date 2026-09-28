using EagleBoards.Core;
using EagleBoards.Core.Records;

namespace EagleBoards.Tests;

/// <summary>
/// What only Windows' scheduler has. Which adults auto-select and fill the
/// rest propose, free-since and the Supporting link are the cases all three
/// versions share (<see cref="SharedCaseTests"/>); a case for those goes in
/// eagleboards-shared, not here.
/// </summary>
public class SchedulerLogicTests
{
    private static readonly ScoutInfo FinalScout = new("S1", "Aldridge", "Alex", "Troop1001", BoardTypes.Final, "", BoardStatus.Registered, "Sam Smith");

    /// <summary>An adult in the pool, final role then project, as the shared cases write them.</summary>
    private static AdultInfo Pool(string id, string unit, string final, string project) =>
        new(id, id, id, unit, "", final, project);

    private static readonly AdultInfo[] FullBoard =
        [Pool("chair", "Troop3", "Chair", "Member"), Pool("m1", "Troop5", "Member", "Member"), Pool("m2", "Troop7", "Member", "Member")];

    [Fact]
    public void AutoSelectTakesTheFirstFreeRoomOfTheYouthsKind()
    {
        var rooms = new[]
        {
            new RoomInfo("ROOM:200", "200", BoardTypes.Project, ""),
            new RoomInfo("ROOM:101", "101", BoardTypes.Final, "Someone Else"),
            new RoomInfo("ROOM:102", "102", BoardTypes.Final, ""),
        };

        var pick = SchedulerLogic.AutoSelect(FinalScout, FullBoard, rooms);
        Assert.Equal("ROOM:102", pick.RoomId);
        Assert.Empty(pick.Problems);
    }

    [Fact]
    public void WithNoFreeRoomOfItsKindAutoSelectStillProposesTheBoardAndSaysSo()
    {
        var rooms = new[]
        {
            new RoomInfo("ROOM:200", "200", BoardTypes.Project, ""),
            new RoomInfo("ROOM:101", "101", BoardTypes.Final, "Someone Else"),
        };

        var pick = SchedulerLogic.AutoSelect(FinalScout, FullBoard, rooms);
        Assert.Null(pick.RoomId);
        Assert.Equal(["chair", "m1", "m2"], pick.AllAdultIds);
        Assert.Equal(["No Final Rooms Available"], pick.Problems);
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
    public void FindingAPersonSaysWhichRoomTheyAreInOrWhereTheyAreInstead()
    {
        // SPEC.md D-21: a youth or an adult, by any part of their name.
        ScoutInfo Youth(string first, string last, string status, string room = "") =>
            new(last, last, first, "Troop1", BoardTypes.Final, room, status, "");
        AdultInfo Adult(string first, string last, string room = "") =>
            new(last, last, first, "Troop2", room, BoardRoles.Member, BoardRoles.Member);

        var youth = new[]
        {
            Youth("Arthur", "Eldred", BoardStatus.InProgress, "101"),
            Youth("Bill", "Amend", BoardStatus.Registered),
            Youth("Peter", "Agre", BoardStatus.Completed, AdultRoom.Disabled),
            Youth("Rob", "Corddry", BoardStatus.Postponed),
        };
        var adults = new[]
        {
            Adult("Neil", "Armstrong", "101"),
            Adult("Jim", "Lovell"),
            Adult("Charles", "Duke", AdultRoom.Disabled),
        };

        string Say(string query) => string.Join("; ",
            SchedulerLogic.FindPeople(query, youth, adults).Select(p => $"{p.Name} {p.Where}{(p.Room != null ? " [" + p.Room + "]" : "")}"));

        // Youth first, then adults, each by last name.
        Assert.Equal("Arthur Eldred is in room 101 [101]; Neil Armstrong is in room 101 [101]; Charles Duke has gone home", Say("ar"));
        Assert.Equal("Neil Armstrong is in room 101 [101]", Say("neil arm"));
        Assert.Equal("Bill Amend is waiting", Say("bill"));
        Assert.Equal("Peter Agre has finished", Say("AGRE"));
        Assert.Equal("Rob Corddry was postponed", Say("rob c"));
        Assert.Equal("Jim Lovell isn't on a board", Say("lovell"));
        Assert.Equal("Charles Duke has gone home", Say("Duke"));
        Assert.Empty(SchedulerLogic.FindPeople("  ", youth, adults));
        Assert.Empty(SchedulerLogic.FindPeople("Spielberg", youth, adults));
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
}
