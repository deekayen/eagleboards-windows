namespace EagleBoards.Core;

/// <summary>
/// The room chosen for the board being built, and whether the operator chose
/// it themselves (which decides if it may stay when it isn't the youth's
/// board type).
/// </summary>
public sealed record RoomChoice(string? RoomId, bool ChosenByOperator);

/// <summary>
/// Which room a proposed board goes in (SPEC.md D-24). Automatic assignment
/// uses only rooms set up for the youth's own board type; to use another,
/// the operator switches a room's type or chooses a room by hand.
/// </summary>
public static class RoomProposal
{
    /// <summary>
    /// The first free room set up for <paramref name="boardType"/>, or null
    /// when there is none: nothing is proposed in the other type's room.
    /// </summary>
    public static string? FirstFreeRoomOfType(string boardType, IEnumerable<RoomInfo> freeRooms) =>
        freeRooms.FirstOrDefault(r => r.BoardType == boardType)?.Id;

    /// <summary>
    /// The room for the board being built. The room already chosen stays
    /// while it's free, and while it is this youth's board type or the
    /// operator chose it themselves; otherwise it is
    /// <paramref name="proposedRoomId"/> and no longer counts as the
    /// operator's.
    /// </summary>
    /// <param name="mayKeepCurrentRoom">
    /// False when the current room was chosen for another youth, so it is
    /// proposed afresh whatever it is.
    /// </param>
    public static RoomChoice KeepOrChoose(string boardType, IEnumerable<RoomInfo> freeRooms, RoomChoice current,
        string? proposedRoomId, bool mayKeepCurrentRoom)
    {
        var currentRoom = freeRooms.FirstOrDefault(r => r.Id == current.RoomId);
        var keep = mayKeepCurrentRoom
            && currentRoom is not null
            && (current.ChosenByOperator || currentRoom.BoardType == boardType);
        return keep ? current : new RoomChoice(proposedRoomId, ChosenByOperator: false);
    }
}
