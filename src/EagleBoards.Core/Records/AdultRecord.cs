namespace EagleBoards.Core.Records;

public sealed class AdultRecord : PersonRecord
{
    public static readonly IReadOnlyList<string> AllColumns =
    [
        "Type", "ID", "Last", "First", "Email", "Phone", "UnitType", "Unit", "UnitName",
        "ProjectReview", "FinalBoard", "RegTime", "Room", "Flags", "Sel", "BoardHistory",

        // Per event, set at sign-in and never carried into the adult history.
        // Appended, as in the Java version, so older files still line up.
        "WoodBadge", "Supporting",
    ];

    public AdultRecord()
        : this(null)
    {
    }

    public AdultRecord(IEnumerable<KeyValuePair<string, string>>? source)
        : base("ADULT", AllColumns, source)
    {
        UpdateFields(true);
    }

    /// <summary>Chair, Member or Unavailable, for final boards of review.</summary>
    public string FinalBoardRole => GetValue("FinalBoard");

    /// <summary>Chair, Member or Unavailable, for project proposal reviews.</summary>
    public string ProjectReviewRole => GetValue("ProjectReview");

    /// <summary>This adult's role for a board of the given type.</summary>
    public string RoleFor(string boardType) => boardType == BoardTypes.Project ? ProjectReviewRole : FinalBoardRole;

    public string BoardHistory
    {
        get => GetValue("BoardHistory");
        set => SetValue("BoardHistory", value);
    }

    /// <summary>The operator's checkbox on the scheduler: "1" when picked for the next board.</summary>
    public string Sel
    {
        get => GetValue("Sel");
        set => SetValue("Sel", value);
    }

    /// <summary>"Y" when this event counts toward a Wood Badge ticket item.</summary>
    public string WoodBadge
    {
        get => GetValue("WoodBadge");
        set => SetValue("WoodBadge", value);
    }

    /// <summary>
    /// IDs of the scouts this adult introduces to their board (their Scoutmaster, say; SPEC.md D-23),
    /// separated by "|" because the data files turn commas into "~".
    /// </summary>
    public string Supporting
    {
        get => GetValue("Supporting");
        set => SetValue("Supporting", value);
    }

    /// <summary>The scouts this adult introduces.</summary>
    public IReadOnlyList<string> SupportingIds => Supporting.Split('|', StringSplitOptions.RemoveEmptyEntries);

    public AdultRecord Clone() => new(Fields);

    public static RecordFactory<AdultRecord> Factory { get; } = new(() => new AdultRecord(), AllColumns);
}
