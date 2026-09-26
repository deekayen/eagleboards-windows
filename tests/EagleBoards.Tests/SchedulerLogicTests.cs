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
}
