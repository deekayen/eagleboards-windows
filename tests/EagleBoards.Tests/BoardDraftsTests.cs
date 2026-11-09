using EagleBoards.Core;

namespace EagleBoards.Tests;

/// <summary>
/// The boards an operator changed, one per waiting youth (SPEC.md D-12,
/// amended 2026-11-08, eagleboards-shared#22), without a window. That the
/// window keeps and restores them as youth are opened is covered by the
/// window tests.
/// </summary>
public class BoardDraftsTests
{
    private static BoardDraft DraftOf(params string[] adultIds) =>
        new(adultIds, ChairId: adultIds.FirstOrDefault(), new RoomChoice("ROOM:101", ChosenByOperator: true), Proposal: null);

    [Fact]
    public void EachYouthKeepsTheirOwnDraft()
    {
        var drafts = new BoardDrafts();
        drafts.Keep("SCOUT:1", DraftOf("ADULT:1", "ADULT:2"));
        drafts.Keep("SCOUT:2", DraftOf("ADULT:3"));

        Assert.Equal(["ADULT:1", "ADULT:2"], drafts.Take("SCOUT:1")!.AdultIds);
        Assert.Equal(["ADULT:3"], drafts.Take("SCOUT:2")!.AdultIds);
    }

    [Fact]
    public void ADraftIsHandedBackOnceBecauseTheOpenBoardIsWhereItLives()
    {
        var drafts = new BoardDrafts();
        drafts.Keep("SCOUT:1", DraftOf("ADULT:1"));

        Assert.NotNull(drafts.Take("SCOUT:1"));
        Assert.Null(drafts.Take("SCOUT:1"));
        Assert.False(drafts.Has("SCOUT:1"));
    }

    [Fact]
    public void ADraftForAYouthWhoIsNotWaitingAnyMoreIsDropped()
    {
        var drafts = new BoardDrafts();
        drafts.Keep("SCOUT:1", DraftOf("ADULT:1"));
        drafts.Keep("SCOUT:2", DraftOf("ADULT:2"));

        drafts.ForgetAllExcept(new HashSet<string> { "SCOUT:2" });

        Assert.False(drafts.Has("SCOUT:1"));
        Assert.True(drafts.Has("SCOUT:2"));
    }

    [Fact]
    public void ForgetEndsOneYouthsDraftAndLeavesTheOthers()
    {
        var drafts = new BoardDrafts();
        drafts.Keep("SCOUT:1", DraftOf("ADULT:1"));
        drafts.Keep("SCOUT:2", DraftOf("ADULT:2"));

        drafts.Forget("SCOUT:1");

        Assert.False(drafts.Has("SCOUT:1"));
        Assert.True(drafts.Has("SCOUT:2"));
    }
}
