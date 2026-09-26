namespace EagleBoards.Core.Records;

/// <summary>A room boards can be seated in. Its ID is "ROOM:" + the room number.</summary>
public sealed class RoomRecord : DataRecord
{
    public static readonly IReadOnlyList<string> AllColumns = ["Type", "ID", "Room", "BoardType", "Scout", "Leaders", "RegTime"];

    public RoomRecord()
        : this(null)
    {
    }

    public RoomRecord(IEnumerable<KeyValuePair<string, string>>? source)
        : base("ROOM", AllColumns, source)
    {
    }

    public static string IdFor(string room) => "ROOM:" + room;

    /// <summary>
    /// The room number or name shown on screen and matched against
    /// <c>ScoutRecord.Room</c>/<c>AdultRecord.Room</c>. The ID stays what it
    /// was assigned at creation even after this changes.
    /// </summary>
    public string Room
    {
        get => GetValue("Room");
        set => Put("Room", value);
    }

    public string BoardType => GetValue("BoardType");

    /// <summary>Full name of the scout whose board holds the room, or "".</summary>
    public string Scout
    {
        get => GetValue("Scout");
        set => Put("Scout", value);
    }

    /// <summary>Comma-separated full names of the board members in the room.</summary>
    public string Leaders
    {
        get => GetValue("Leaders");
        set => Put("Leaders", value);
    }

    public static RecordFactory<RoomRecord> Factory { get; } = new(() => new RoomRecord(), AllColumns);
}
