namespace EagleBoards.Core.Records;

public sealed class ScoutRecord : PersonRecord
{
    public static readonly IReadOnlyList<string> AllColumns =
    [
        "Type", "ID", "RegNum", "Last", "First", "Email", "Phone", "UnitType", "Unit", "UnitName",
        "DOB", "BoardType", "Leader", "RegTime", "LastUpdateTime", "Flags", "Room", "Status", "Result",
        "BoardChair", "BoardChairID", "BoardMembers", "BoardMembersIDs", "Notes",
    ];

    public ScoutRecord()
        : this(null)
    {
    }

    public ScoutRecord(IEnumerable<KeyValuePair<string, string>>? source)
        : base("SCOUT", AllColumns, source)
    {
        SetDefaults();
        UpdateFields(true);
    }

    public string RegNum
    {
        get => GetValue("RegNum");
        set => SetValue("RegNum", value);
    }

    /// <summary>
    /// The birthdate column. It stays in the file so folders still move
    /// between versions (SPEC.md D-1), but nothing is written to it, shown
    /// from it or sent from it (D-7). Values already on file are carried
    /// through untouched (O-5).
    /// </summary>
    public const string DobField = "DOB";

    /// <summary>
    /// The phone number column. Like the birthdate it stays in the file
    /// (SPEC.md D-1), but a youth's number is no longer taken at sign-in,
    /// imported, shown, pre-filled or sent (D-8); one already on file is
    /// carried through untouched. Adults' numbers are not affected.
    /// </summary>
    public const string PhoneField = "Phone";

    public string Leader => GetValue("Leader");

    /// <summary>"Final" (board of review) or "Project" (proposal review).</summary>
    public string BoardType => GetValue("BoardType");

    public string Status
    {
        get => GetValue("Status");
        set => SetValue("Status", value);
    }

    public string Result
    {
        get => GetValue("Result");
        set => SetValue("Result", value);
    }

    public string Notes
    {
        get => GetValue("Notes");
        set => SetValue("Notes", value);
    }

    public string BoardChair
    {
        get => GetValue("BoardChair");
        set => SetValue("BoardChair", value);
    }

    public string BoardChairId
    {
        get => GetValue("BoardChairID");
        set => SetValue("BoardChairID", value);
    }

    public string BoardMembers
    {
        get => GetValue("BoardMembers");
        set => SetValue("BoardMembers", value);
    }

    public string BoardMemberIds
    {
        get => GetValue("BoardMembersIDs");
        set => SetValue("BoardMembersIDs", value);
    }

    protected override void SetDefaults()
    {
        if (string.IsNullOrWhiteSpace(Get("Status")))
        {
            Put("Status", "");
        }
    }

    public ScoutRecord Clone() => new(Fields);

    public static RecordFactory<ScoutRecord> Factory { get; } = new(() => new ScoutRecord(), AllColumns);
}
