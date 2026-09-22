using EagleBoards.Core;

namespace EagleBoards.Tests;

/// <summary>
/// Board composition rules. Ported case for case from the Java project's
/// scripts/test-seat-conflicts.js, which pinned the same rules in
/// process_seat.js. The failure that matters is a silent one: a same-unit
/// adult who is never reported still ends up sitting on the board.
/// </summary>
public class BoardRulesTests
{
    private static BoardCandidate Adult(string first, string last, string unit) => new("ADULT:" + last + ":" + first, last, first, unit);

    private static string[] Names(IEnumerable<BoardCandidate> conflicts) => conflicts.Select(c => c.First + " " + c.Last).ToArray();

    // ---- same-unit board member detection ----

    [Fact]
    public void AdultInTheScoutsUnitIsFlagged() =>
        Assert.Equal(["Robin Sameunit"], Names(BoardRules.FindUnitConflicts("Troop1234", [Adult("Robin", "Sameunit", "Troop1234")])));

    [Fact]
    public void BoardWithNoUnitOverlapIsClean() =>
        Assert.Empty(BoardRules.FindUnitConflicts("Troop1234",
            [Adult("Jordan", "Otherunit", "Troop5678"), Adult("Casey", "Otherunit", "Troop9999"), Adult("Sam", "Otherunit", "Pack0042")]));

    [Fact]
    public void EverySameUnitMemberIsReportedNotJustTheFirst() =>
        Assert.Equal(["Robin Sameunit", "Alex Sameunit"], Names(BoardRules.FindUnitConflicts("Troop1234",
            [Adult("Robin", "Sameunit", "Troop1234"), Adult("Alex", "Sameunit", "Troop1234"), Adult("Jordan", "Otherunit", "Troop5678")])));

    [Fact]
    public void OnlyTheOverlappingMembersAreReturnedInOrder() =>
        Assert.Equal(["Robin Sameunit"], Names(BoardRules.FindUnitConflicts("Troop1234",
            [Adult("Jordan", "Otherunit", "Troop5678"), Adult("Robin", "Sameunit", "Troop1234"), Adult("Casey", "Otherunit", "Troop9999")])));

    [Fact]
    public void AnEntireSameUnitBoardIsFullyReported() =>
        Assert.Equal(["Robin Sameunit", "Alex Sameunit", "Jordan Sameunit"], Names(BoardRules.FindUnitConflicts("Troop1234",
            [Adult("Robin", "Sameunit", "Troop1234"), Adult("Alex", "Sameunit", "Troop1234"), Adult("Jordan", "Sameunit", "Troop1234")])));

    // ---- unit-name edge cases ----

    [Fact]
    public void AUnitThatIsAPrefixOfAnotherDoesNotMatch() =>
        Assert.Empty(BoardRules.FindUnitConflicts("Troop12", [Adult("Robin", "Sameunit", "Troop123")]));

    [Fact]
    public void SameNumberDifferentUnitTypeIsNotAMatch() =>
        Assert.Empty(BoardRules.FindUnitConflicts("Troop1234", [Adult("Robin", "Otherunit", "Pack1234")]));

    [Theory]
    [InlineData("", "")]
    [InlineData("Troop1234", "")]
    [InlineData("", "Troop5678")]
    public void BlankUnitsAreUnknownNotTheSame(string scoutUnit, string memberUnit) =>
        Assert.Empty(BoardRules.FindUnitConflicts(scoutUnit, [Adult("Robin", "Nounit", memberUnit)]));

    [Fact]
    public void DifferingCaseIsNotTreatedAsTheSameUnit() =>
        Assert.Empty(BoardRules.FindUnitConflicts("Troop1234", [Adult("Robin", "Sameunit", "TROOP1234")]));

    [Fact]
    public void NoMembersSelectedYieldsNoConflicts() =>
        Assert.Empty(BoardRules.FindUnitConflicts("Troop1234", []));

    // ---- national floor: at least one member from outside the unit ----

    [Fact]
    public void ABoardEntirelyFromTheScoutsUnitHasNoOutsideMember() =>
        Assert.False(BoardRules.HasNonUnitMember("Troop1234",
            [Adult("Robin", "Sameunit", "Troop1234"), Adult("Alex", "Sameunit", "Troop1234"), Adult("Jordan", "Sameunit", "Troop1234")]));

    [Fact]
    public void OneOutsideMemberIsEnoughToMeetTheNationalFloor() =>
        Assert.True(BoardRules.HasNonUnitMember("Troop1234",
            [Adult("Robin", "Sameunit", "Troop1234"), Adult("Alex", "Sameunit", "Troop1234"), Adult("Jordan", "Otherunit", "Troop5678")]));

    [Fact]
    public void AFullyCrossUnitBoardMeetsTheFloor() =>
        Assert.True(BoardRules.HasNonUnitMember("Troop1234", [Adult("Jordan", "Otherunit", "Troop5678"), Adult("Casey", "Otherunit", "Troop9999")]));

    [Fact]
    public void ABlankMemberUnitCountsAsOutside() =>
        Assert.True(BoardRules.HasNonUnitMember("Troop1234", [Adult("Robin", "Sameunit", "Troop1234"), Adult("Pat", "Nounit", "")]));

    [Fact]
    public void AnUnknownScoutUnitNeverBlocksTheBypass() =>
        Assert.True(BoardRules.HasNonUnitMember("", [Adult("Robin", "Sameunit", "Troop1234")]));

    [Fact]
    public void SameNumberDifferentUnitTypeCountsAsOutside() =>
        Assert.True(BoardRules.HasNonUnitMember("Troop1234", [Adult("Robin", "Otherunit", "Pack1234")]));

    // ---- board size (GTA 8.0.0.3: three to six members) ----

    [Theory]
    [InlineData(0, SizeVerdict.TooFew)]
    [InlineData(1, SizeVerdict.TooFew)]
    [InlineData(2, SizeVerdict.TooFew)]
    [InlineData(3, SizeVerdict.Ok)]
    [InlineData(4, SizeVerdict.OverPreferred)]
    [InlineData(5, SizeVerdict.OverPreferred)]
    [InlineData(6, SizeVerdict.OverPreferred)]
    [InlineData(7, SizeVerdict.TooMany)]
    [InlineData(20, SizeVerdict.TooMany)]
    public void BoardOfReviewSize(int count, SizeVerdict expected) => Assert.Equal(expected, BoardRules.CheckBoardSize(count));

    // ---- project review size (two to six members) ----

    [Theory]
    [InlineData(0, SizeVerdict.TooFew)]
    [InlineData(1, SizeVerdict.TooFew)]
    [InlineData(2, SizeVerdict.Ok)]
    [InlineData(3, SizeVerdict.OverPreferred)]
    [InlineData(6, SizeVerdict.OverPreferred)]
    [InlineData(7, SizeVerdict.TooMany)]
    [InlineData(20, SizeVerdict.TooMany)]
    public void ProjectReviewSize(int count, SizeVerdict expected) => Assert.Equal(expected, BoardRules.CheckProjectSize(count));

    [Fact]
    public void TheTwoRulesDifferOnlyAtTheFloor()
    {
        Assert.Equal((SizeVerdict.Ok, SizeVerdict.OverPreferred), (BoardRules.CheckBoardSize(3), BoardRules.CheckProjectSize(3)));
        Assert.Equal((SizeVerdict.TooFew, SizeVerdict.Ok), (BoardRules.CheckBoardSize(2), BoardRules.CheckProjectSize(2)));
    }
}
