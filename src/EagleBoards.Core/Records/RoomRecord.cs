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

    public string Room => GetValue("Room");

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
