using EagleBoards.Core;
using EagleBoards.Core.Records;

namespace EagleBoards.Tests;

/// <summary>
/// The scheduler window lists a proposed board's problems as the operator
/// builds it. These pin which problems block seating and which only warn
/// (the operator's call), matching what BoardService.SeatBoard refuses.
/// </summary>
public class BoardCheckTests
{
    private static AdultInfo A(string id, string unit, string final = "Member", string project = "Member", string room = "") =>
        new(id, id, id, unit, room, final, project);

    private static readonly ScoutInfo Final = new("S1", "Aldridge", "Alex", "Troop1001", BoardTypes.Final, "", BoardStatus.Registered, "");

    private static readonly ScoutInfo Project = Final with { BoardType = BoardTypes.Project };

    private static readonly RoomInfo FreeFinal = new("ROOM:101", "101", BoardTypes.Final, "");

    private static readonly AdultInfo Chair = A("chair", "Troop2", final: "Chair", project: "Chair");

    private static IReadOnlyList<BoardIssue> Check(ScoutInfo scout, AdultInfo[] picked, string? chairId = "chair", RoomInfo? room = null) =>
        BoardCheck.Review(scout, picked, chairId, room ?? FreeFinal);

    private static string[] Titles(IEnumerable<BoardIssue> issues, IssueLevel level) => issues.Where(i => i.Level == level).Select(i => i.Title).ToArray();

    [Fact]
    public void AGoodBoardHasNoIssues() =>
        Assert.Empty(Check(Final, [Chair, A("m1", "Troop3"), A("m2", "Troop4")]));

    [Fact]
    public void AProjectReviewNeedsTwo() =>
        Assert.Empty(Check(Project, [Chair, A("m1", "Troop3")], room: FreeFinal with { BoardType = BoardTypes.Project }));

    [Fact]
    public void TooFewBlocks() =>
        Assert.Equal(["Too few members"], Titles(Check(Final, [Chair, A("m1", "Troop3")]), IssueLevel.Block));

    [Fact]
    public void SevenBlocks()
    {
        var picked = new[] { Chair }.Concat(Enumerable.Range(1, 6).Select(i => A("m" + i, "Troop" + (10 + i)))).ToArray();
        Assert.Equal(["Too many members"], Titles(Check(Final, picked), IssueLevel.Block));
    }

    [Fact]
    public void FourIsAllowedWithAWarning()
    {
        var issues = Check(Final, [Chair, A("m1", "Troop3"), A("m2", "Troop4"), A("m3", "Troop5")]);
        Assert.Empty(Titles(issues, IssueLevel.Block));
        Assert.Equal(["Larger than usual"], Titles(issues, IssueLevel.Warn));
    }

    [Fact]
    public void SameUnitWarnsWhileSomeoneIsFromOutside()
    {
        var issues = Check(Final, [Chair, A("m1", "Troop1001"), A("m2", "Troop4")]);
        Assert.Empty(Titles(issues, IssueLevel.Block));
        Assert.Equal(["Same unit"], Titles(issues, IssueLevel.Warn));
        Assert.Contains("m1 m1", issues[0].Message);
    }

    [Fact]
    public void AnAllSameUnitBoardBlocks()
    {
        var chair = A("chair", "Troop1001", final: "Chair");
        var issues = Check(Final, [chair, A("m1", "Troop1001"), A("m2", "Troop1001")]);
        Assert.Contains("Everyone is from the youth's unit", Titles(issues, IssueLevel.Block));
    }

    [Fact]
    public void NoQualifiedChairBlocks() =>
        Assert.Contains("No chair", Titles(Check(Final, [A("m0", "Troop2"), A("m1", "Troop3"), A("m2", "Troop4")], chairId: null), IssueLevel.Block));

    [Fact]
    public void TheChosenChairMustBeQualified() =>
        Assert.Equal(["Choose a chair"], Titles(Check(Final, [Chair, A("m1", "Troop3"), A("m2", "Troop4")], chairId: "m1"), IssueLevel.Block));

    [Theory]
    [InlineData("101", "Already on a board")]
    [InlineData(AdultRoom.Disabled, "Gone home")]
    public void BusyOrGoneHomeMembersBlock(string room, string title) =>
        Assert.Contains(title, Titles(Check(Final, [Chair, A("m1", "Troop3", room: room), A("m2", "Troop4")]), IssueLevel.Block));

    [Fact]
    public void AnUnavailableMemberBlocks() =>
        Assert.Contains("Unavailable", Titles(Check(Final, [Chair, A("m1", "Troop3", final: "Unavailable"), A("m2", "Troop4")]), IssueLevel.Block));

    [Fact]
    public void RoomMustBeChosenAndFree()
    {
        AdultInfo[] ok = [Chair, A("m1", "Troop3"), A("m2", "Troop4")];
        Assert.Equal(["No room"], Titles(BoardCheck.Review(Final, ok, "chair", null), IssueLevel.Block));
        Assert.Equal(["Room in use"], Titles(Check(Final, ok, room: FreeFinal with { Scout = "Someone Else" }), IssueLevel.Block));
    }

    [Fact]
    public void TheOtherBoardTypesRoomOnlyWarns()
    {
        var issues = Check(Final, [Chair, A("m1", "Troop3"), A("m2", "Troop4")], room: FreeFinal with { BoardType = BoardTypes.Project });
        Assert.Empty(Titles(issues, IssueLevel.Block));
        Assert.Equal(["Room type"], Titles(issues, IssueLevel.Warn));
    }

    [Fact]
    public void NoMembersSaysSo() =>
        Assert.Equal(["No members yet"], Titles(Check(Final, [], chairId: null), IssueLevel.Block));
}
