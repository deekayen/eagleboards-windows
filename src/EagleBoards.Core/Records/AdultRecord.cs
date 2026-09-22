namespace EagleBoards.Core.Records;

public sealed class AdultRecord : PersonRecord
{
    public static readonly IReadOnlyList<string> AllColumns =
    [
        "Type", "ID", "Last", "First", "Email", "Phone", "UnitType", "Unit", "UnitName",
        "ProjectReview", "FinalBoard", "RegTime", "Room", "Flags", "Sel", "BoardHistory",
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

    public AdultRecord Clone() => new(Fields);

    public static RecordFactory<AdultRecord> Factory { get; } = new(() => new AdultRecord(), AllColumns);
}
