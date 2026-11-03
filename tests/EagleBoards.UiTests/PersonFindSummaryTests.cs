using EagleBoards.App;
using EagleBoards.Core;
using EagleBoards.Core.Records;

namespace EagleBoards.UiTests;

/// <summary>
/// The words the Event page's find box shows and speaks (SPEC.md D-21), tried
/// without a window. The window tests cover the box itself.
/// </summary>
public class PersonFindSummaryTests
{
    private static ScoutInfo Youth(string last, string first, string status = BoardStatus.Registered, string room = "") =>
        new(last, last, first, "Troop1001", BoardTypes.Final, room, status, "");

    private static AdultInfo Adult(string last, string first, string room = "") =>
        new(last, last, first, "Troop2001", room, BoardRoles.Member, BoardRoles.Member);

    private static PersonFindSummary Find(string query, ScoutInfo[] youth, AdultInfo[]? adults = null, params string[] rooms) =>
        PersonFindSummary.For(query, youth, adults ?? [], rooms);

    [Fact]
    public void NothingTypedLeavesEveryRoomShowing()
    {
        var summary = Find("  ", [Youth("Blackwood", "Bartholomew")]);

        Assert.Equal(PersonFindSummary.Empty, summary);
        Assert.Null(summary.Rooms);
    }

    [Fact]
    public void SomeoneInARoomNarrowsToThatRoomAndIsSpokenNotShown()
    {
        var summary = Find("black", [Youth("Blackwood", "Bartholomew", BoardStatus.Seated, "101")]);

        Assert.Equal(["101"], summary.Rooms);
        Assert.Equal("", summary.Note);
        Assert.Equal("Bartholomew Blackwood is in room 101.", summary.Spoken);
    }

    [Fact]
    public void SomeoneInNoRoomIsSaidWhereTheyAreOnScreenAndAloud()
    {
        var summary = Find("Blackwood", [Youth("Blackwood", "Bartholomew")]);

        Assert.Empty(summary.Rooms!);
        Assert.Equal("Bartholomew Blackwood is waiting.", summary.Note);
        Assert.Equal(summary.Note, summary.Spoken);
    }

    [Fact]
    public void NoOneByThatNameSaysSo()
    {
        var summary = Find("Nobody", [Youth("Blackwood", "Bartholomew")]);

        Assert.Equal("No one by that name has signed in.", summary.Note);
    }

    [Fact]
    public void ARoomByThatNameIsFoundAndCounted()
    {
        var summary = Find("101", [], null, "101", "102", "101 Annex");

        Assert.Equal(["101", "101 Annex"], summary.Rooms!.Order());
        Assert.Equal("2 rooms by that name: 101, 101 Annex.", summary.Spoken);
        Assert.Equal("", summary.Note);
    }

    [Fact]
    public void MoreThanFourPeopleInNoRoomAreNamedFourAndCounted()
    {
        var waiting = new[] { "Able", "Baker", "Cole", "Dunn", "Eaton", "Ford" }.Select(last => Youth(last, "Pat")).ToArray();

        var summary = Find("Pat", waiting);

        Assert.EndsWith("Pat Dunn is waiting. And 2 more.", summary.Note, StringComparison.Ordinal);
        Assert.DoesNotContain("Eaton", summary.Note, StringComparison.Ordinal);
    }

    [Fact]
    public void RoomNamesMatchWithoutRegardToCase()
    {
        var summary = Find("annex", [], null, "101", "101 ANNEX");

        Assert.Equal(["101 ANNEX"], summary.Rooms);
    }

    [Fact]
    public void ARoomHoldingSomeoneAndARoomByThatNameAreBothFoundOnce()
    {
        var summary = Find("101", [Youth("Blackwood", "Bartholomew", BoardStatus.Seated, "101")], null, "101", "101 Annex");

        Assert.Equal(["101", "101 Annex"], summary.Rooms!.Order());
    }

    [Fact]
    public void MoreThanFourPeopleInRoomsAreNamedFourAndCounted()
    {
        var seated = new[] { "Able", "Baker", "Cole", "Dunn", "Eaton" }
            .Select((last, i) => Youth(last, "Pat", BoardStatus.Seated, (101 + i).ToString(System.Globalization.CultureInfo.InvariantCulture))).ToArray();

        var summary = Find("Pat", seated);

        Assert.Contains("Pat Dunn is in room 104. And 1 more in rooms.", summary.Spoken, StringComparison.Ordinal);
        Assert.DoesNotContain("Eaton", summary.Spoken, StringComparison.Ordinal);
        Assert.Equal("", summary.Note);
    }

    [Fact]
    public void AnAdultInARoomAndAYouthWaitingAreBothReported()
    {
        var summary = Find("an", [Youth("Anders", "Sam")], [Adult("Hanson", "Lee", "102")]);

        Assert.Equal(["102"], summary.Rooms);
        Assert.Equal("Sam Anders is waiting.", summary.Note);
        Assert.Equal("Lee Hanson is in room 102. Sam Anders is waiting.", summary.Spoken);
    }
}
