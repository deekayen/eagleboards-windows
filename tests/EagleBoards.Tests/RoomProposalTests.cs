using EagleBoards.Core;
using EagleBoards.Core.Records;

namespace EagleBoards.Tests;

/// <summary>
/// Which room a proposed board goes in (SPEC.md D-24), without a window: a
/// project proposal is never proposed in a final board's room, the bug of
/// the 2026-10-27 event. That the window passes only these two methods' answers
/// to the builder is covered by the window tests.
/// </summary>
public class RoomProposalTests
{
    private static readonly RoomInfo FinalRoom = new("ROOM:101", "101", BoardTypes.Final, "");

    private static readonly RoomInfo ProjectRoom = new("ROOM:102", "102", BoardTypes.Project, "");

    private static readonly RoomInfo[] BothFree = [FinalRoom, ProjectRoom];

    private static RoomChoice Proposed(string? roomId) => new(roomId, ChosenByOperator: false);

    [Fact]
    public void NoRoomIsProposedInTheOtherTypesRoom()
    {
        Assert.Null(RoomProposal.FirstFreeRoomOfType(BoardTypes.Project, [FinalRoom]));
    }

    [Fact]
    public void TheFirstFreeRoomOfTheYouthsTypeIsProposed()
    {
        var secondProjectRoom = new RoomInfo("ROOM:103", "103", BoardTypes.Project, "");

        Assert.Equal("ROOM:102", RoomProposal.FirstFreeRoomOfType(BoardTypes.Project, [FinalRoom, ProjectRoom, secondProjectRoom]));
    }

    [Fact]
    public void ARoomOfTheYouthsTypeStaysWhileItIsFree()
    {
        var kept = RoomProposal.KeepOrChoose(BoardTypes.Project, BothFree, Proposed("ROOM:102"), "ROOM:101", mayKeepCurrentRoom: true);

        Assert.Equal(Proposed("ROOM:102"), kept);
    }

    [Fact]
    public void AProposedRoomSwitchedToTheOtherTypeIsWithdrawn()
    {
        var withdrawn = RoomProposal.KeepOrChoose(BoardTypes.Project, BothFree, Proposed("ROOM:101"), "ROOM:102", mayKeepCurrentRoom: true);

        Assert.Equal(Proposed("ROOM:102"), withdrawn);
    }

    [Fact]
    public void WithdrawingLeavesNoRoomWhenNoneOfTheRightTypeIsFree()
    {
        var withdrawn = RoomProposal.KeepOrChoose(BoardTypes.Project, [FinalRoom], Proposed("ROOM:101"), null, mayKeepCurrentRoom: true);

        Assert.Equal(Proposed(null), withdrawn);
    }

    [Fact]
    public void ARoomTheOperatorChoseStaysEvenInTheOtherType()
    {
        var chosen = new RoomChoice("ROOM:101", ChosenByOperator: true);

        var kept = RoomProposal.KeepOrChoose(BoardTypes.Project, BothFree, chosen, "ROOM:102", mayKeepCurrentRoom: true);

        Assert.Equal(chosen, kept);
    }

    [Fact]
    public void ARoomChosenForAnotherYouthIsProposedAfreshWhoeverChoseIt()
    {
        var chosen = new RoomChoice("ROOM:101", ChosenByOperator: true);

        var afresh = RoomProposal.KeepOrChoose(BoardTypes.Project, BothFree, chosen, "ROOM:102", mayKeepCurrentRoom: false);

        Assert.Equal(Proposed("ROOM:102"), afresh);
    }

    [Fact]
    public void ARoomThatHasBeenTakenIsNotKept()
    {
        var taken = new RoomChoice("ROOM:102", ChosenByOperator: true);

        var replaced = RoomProposal.KeepOrChoose(BoardTypes.Project, [FinalRoom], taken, null, mayKeepCurrentRoom: true);

        Assert.Equal(Proposed(null), replaced);
    }

    [Fact]
    public void WithNoRoomChosenYetTheProposedOneIsTaken()
    {
        var first = RoomProposal.KeepOrChoose(BoardTypes.Final, BothFree, Proposed(null), "ROOM:101", mayKeepCurrentRoom: true);

        Assert.Equal(Proposed("ROOM:101"), first);
    }
}
