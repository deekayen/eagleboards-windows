using System.Globalization;

namespace EagleBoards.Core.Records;

/// <summary>The single settings record, kept in config.properties.</summary>
public sealed class ConfigRecord : DataRecord
{
    public const string DefaultId = "DEFAULT";

    /// <summary>
    /// The status colour keys that followed these (RegisteredColor through
    /// PostponedHiColor) are retired (SPEC.md D-19): a file that still has
    /// them loads, since only these keys are read, and saving drops them.
    /// </summary>
    public static readonly IReadOnlyList<string> AllColumns =
    [
        "Type", "ID", "Name", "RefreshTimeSecs", "ConveneRedMins",
        "ProjectYellowMins", "ProjectRedMins", "FinalYellowMins", "FinalRedMins",
    ];

    /// <summary>Defaults, and where they come from, are documented on the Settings window.</summary>
    public static readonly IReadOnlyDictionary<string, string> Defaults = new Dictionary<string, string>
    {
        ["ID"] = DefaultId,
        ["Name"] = DefaultId,
        ["RefreshTimeSecs"] = "30",
        ["ConveneRedMins"] = "30",
        ["ProjectYellowMins"] = "25",
        ["ProjectRedMins"] = "40",
        ["FinalYellowMins"] = "30",
        ["FinalRedMins"] = "45",
    };

    public ConfigRecord()
        : this(null)
    {
    }

    public ConfigRecord(IEnumerable<KeyValuePair<string, string>>? source)
        : base("CONFIG", AllColumns, source)
    {
        // Only one config record ever exists; defaulting its identity means an
        // update that omits ID/Name still lands on it instead of adding a twin.
        foreach (var kv in Defaults)
        {
            if (GetValue(kv.Key).Length == 0)
            {
                SetValue(kv.Key, kv.Value);
            }
        }
    }

    public string Name => GetValue("Name");

    public int RefreshTimeSecs => Int("RefreshTimeSecs");

    /// <summary>Cap on the convening phase (Seated): red only, no yellow.</summary>
    public int ConveneRedMins => Int("ConveneRedMins");

    public int ProjectYellowMins => Int("ProjectYellowMins");

    public int ProjectRedMins => Int("ProjectRedMins");

    public int FinalYellowMins => Int("FinalYellowMins");

    public int FinalRedMins => Int("FinalRedMins");

    /// <summary>
    /// Integer setting, falling back to the default when blank, zero or
    /// unparsable (as the browser pages' <c>parseInt(..) || default</c> did).
    /// </summary>
    private int Int(string key)
    {
        if (int.TryParse(GetValue(key), NumberStyles.Integer, CultureInfo.InvariantCulture, out var v) && v != 0)
        {
            return v;
        }

        return int.Parse(Defaults[key], CultureInfo.InvariantCulture);
    }

    public void UpdateFrom(DataRecord other, IEnumerable<string> fields)
    {
        foreach (var field in fields)
        {
            var value = other.Get(field);
            if (!string.IsNullOrEmpty(value))
            {
                Put(field, value);
            }
        }
    }

    public static RecordFactory<ConfigRecord> Factory { get; } = new(() => new ConfigRecord(), AllColumns);
}
