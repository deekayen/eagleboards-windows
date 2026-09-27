using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace EagleBoards.Core.Records;

/// <summary>
/// One row of a data file: a bag of string fields plus the ordered list of
/// columns that get written to disk.
///
/// Ported from the Java <c>shkc.core.DataRecord</c>, which extended
/// <c>HashMap&lt;String,String&gt;</c>. The semantics are kept on purpose,
/// including the odd ones, because the CSV files on disk are shared with the
/// Java version and with every event night already recorded:
/// <list type="bullet">
/// <item>A field that is not a column can still be held (e.g. ShortName), it
/// just never reaches the file.</item>
/// <item>Commas in a value are written as <c>~</c> and line breaks as <c>+</c>;
/// there is no quoting.</item>
/// <item>Times are <c>yyyy-MM-dd_HH:mm</c> plus a <c>+hhmm</c> offset, the
/// format Java's <c>SimpleDateFormat("yyyy-MM-dd_HH:mmZ")</c> writes.</item>
/// </list>
/// </summary>
public abstract partial class DataRecord
{
    public const string TypeField = "Type";
    public const string IdField = "ID";
    public const string RegTimeField = "RegTime";
    public const string LastUpdateTimeField = "LastUpdateTime";

    /// <summary>Computed on read: whole minutes since LastUpdateTime.</summary>
    public const string MinsSinceLastUpdateField = "MinsSinceLastUpdate";

    /// <summary>Computed on read: the HH:mm part of RegTime.</summary>
    public const string RegTimeHmField = "RegTimeHM";

    /// <summary>
    /// Source of "now". Tests replace it to exercise the timers without
    /// waiting; everything else leaves it alone.
    /// </summary>
    public static Func<DateTimeOffset> Clock { get; set; } = () => DateTimeOffset.Now;

    private readonly Dictionary<string, string> _values = new(StringComparer.Ordinal);

    protected DataRecord(string type, IReadOnlyList<string> columns, IEnumerable<KeyValuePair<string, string>>? source)
    {
        Columns = columns;
        _values[TypeField] = type;

        if (source != null)
        {
            foreach (var kv in source)
            {
                if (kv.Value != null)
                {
                    _values[kv.Key] = kv.Value;
                }
            }
        }

        // Always stamped, even over a copied value: a clone is a new record.
        var now = FormatTime(Clock());
        _values[LastUpdateTimeField] = now;
        _values[RegTimeField] = now;

        foreach (var column in Columns)
        {
            _values.TryAdd(column, "");
        }
    }

    public IReadOnlyList<string> Columns { get; }

    /// <summary>Every field held, columns and extras alike.</summary>
    public IReadOnlyDictionary<string, string> Fields => _values;

    public string Id => GetValue(IdField);

    public string Type => GetValue(TypeField);

    public string RegTime => GetValue(RegTimeField);

    public string LastUpdateTime => GetValue(LastUpdateTimeField);

    /// <summary>The raw stored value, or null when the field was never set.</summary>
    public string? Get(string field) => _values.TryGetValue(field, out var v) ? v : null;

    public void Put(string field, string value) => _values[field] = value;

    /// <summary>Stored value with computed fields resolved; never null.</summary>
    public string GetValue(string field)
    {
        if (field == MinsSinceLastUpdateField)
        {
            return MinsSinceLastUpdate;
        }

        if (field == RegTimeHmField)
        {
            return RegTimeHm;
        }

        return _values.TryGetValue(field, out var v) ? v : "";
    }

    public void SetValue(string field, string? value) => _values[field] = value ?? "";

    public bool IsColumn(string field) => Columns.Contains(field);

    public string MinsSinceLastUpdate
    {
        get
        {
            var mins = MinutesSince(LastUpdateTime);
            return mins < 0 ? "" : mins.ToString(CultureInfo.InvariantCulture);
        }
    }

    public string RegTimeHm => RegTime.Length >= 16 ? RegTime.Substring(11, 5) : "";

    protected virtual void SetDefaults()
    {
    }

    /// <summary>
    /// Recompute derived fields. <paramref name="touch"/> also stamps
    /// LastUpdateTime, which is what restarts the room-card timers.
    /// </summary>
    public virtual void UpdateFields(bool touch)
    {
        if (touch)
        {
            SetValue(LastUpdateTimeField, FormatTime(Clock()));
        }
    }

    /// <summary>
    /// Runs after a row is read from disk. Times older than a day (or
    /// unreadable) are reset, so a room carried over from a previous night
    /// doesn't show a timer in the thousands of minutes.
    /// </summary>
    public virtual void PostLoadUpdate()
    {
        var mins = MinutesSince(LastUpdateTime);
        if (mins < 0 || mins > 1440)
        {
            SetValue(LastUpdateTimeField, FormatTime(Clock()));
        }

        mins = MinutesSince(RegTime);
        if (mins < 0 || mins > 1440)
        {
            SetValue(RegTimeField, FormatTime(Clock()));
        }
    }

    /// <summary>
    /// Fill from one CSV line, positionally against <paramref name="header"/>.
    /// Values for anything that isn't one of this record's columns are
    /// dropped; the last header column takes the rest of the line.
    /// </summary>
    public void FromCsv(string line, char delimiter, IReadOnlyList<string> header)
    {
        var start = 0;
        for (var i = 0; i < header.Count; i++)
        {
            var end = line.IndexOf(delimiter, start);
            if (end < 0)
            {
                if (IsColumn(header[i]))
                {
                    _values[header[i]] = line.Substring(start);
                }

                break;
            }

            if (IsColumn(header[i]))
            {
                _values[header[i]] = line.Substring(start, end - start);
            }

            start = end + 1;
        }

        SetDefaults();
        UpdateFields(false);
        PostLoadUpdate();
    }

    public void ToCsv(StringBuilder sb, char delimiter) => ToCsv(sb, delimiter, Columns);

    public void ToCsv(StringBuilder sb, char delimiter, IEnumerable<string> columns)
    {
        var first = true;
        foreach (var column in columns)
        {
            if (!first)
            {
                sb.Append(delimiter);
            }

            first = false;
            sb.Append(GetValue(column).Replace(delimiter, '~').Replace('\n', '+').Replace('\r', '+'));
        }
    }

    /// <summary>
    /// The server's pseudo-JSON: unquoted keys, <c>"</c> in values written as
    /// <c>~</c>. The check-in pages parse it with a regex.
    /// </summary>
    public void ToLooseJson(StringBuilder sb) => ToLooseJson(sb, Columns);

    public void ToLooseJson(StringBuilder sb, IEnumerable<string> columns)
    {
        var first = true;
        sb.Append('{');
        foreach (var column in columns)
        {
            if (!first)
            {
                sb.Append(',');
            }

            first = false;
            var value = GetValue(column).Replace('"', '~').Replace('\n', '+').Replace('\r', '+');
            sb.Append(column).Append(": \"").Append(value).Append('"');
        }

        sb.Append('}');
    }

    /// <summary>A grid row: <c>&lt;row id=".."&gt;&lt;cell&gt;..&lt;/cell&gt;...&lt;/row&gt;</c>.</summary>
    public void ToCells(StringBuilder sb, IEnumerable<string> columns, IEnumerable<string>? userData)
    {
        sb.Append("<row id=\"");
        AppendEscaped(sb, Id);
        sb.Append("\">");
        foreach (var column in columns)
        {
            sb.Append("<cell>");
            AppendEscaped(sb, GetValue(column));
            sb.Append("</cell>");
        }

        if (userData != null)
        {
            foreach (var name in userData)
            {
                sb.Append("<userdata name=\"").Append(name).Append("\">");
                AppendEscaped(sb, GetValue(name));
                sb.Append("</userdata>");
            }
        }

        sb.Append("</row>");
    }

    /// <summary>A data-view item: <c>&lt;item id=".."&gt;&lt;Col&gt;..&lt;/Col&gt;...&lt;/item&gt;</c>.</summary>
    public void ToDataView(StringBuilder sb, IEnumerable<string> columns, IEnumerable<string>? userData)
    {
        sb.Append("<item id=\"");
        AppendEscaped(sb, Id);
        sb.Append("\">");
        foreach (var column in columns.Concat(userData ?? []))
        {
            sb.Append('<').Append(column).Append('>');
            AppendEscaped(sb, GetValue(column));
            sb.Append("</").Append(column).Append('>');
        }

        sb.Append("</item>");
    }

    /// <summary>
    /// A copy of the named fields, computed ones resolved, for handing to a
    /// UI thread that must not touch the live record.
    /// </summary>
    public Dictionary<string, string> Snapshot(IEnumerable<string>? extra = null)
    {
        var copy = new Dictionary<string, string>(_values, StringComparer.Ordinal)
        {
            [MinsSinceLastUpdateField] = MinsSinceLastUpdate,
            [RegTimeHmField] = RegTimeHm,
        };
        foreach (var field in extra ?? [])
        {
            copy[field] = GetValue(field);
        }

        return copy;
    }

    public override string ToString()
    {
        var sb = new StringBuilder();
        ToCsv(sb, ',');
        return sb.ToString();
    }

    /// <summary>XML-escape, with a line break read as a space (as the Java did).</summary>
    public static void AppendEscaped(StringBuilder sb, string value)
    {
        foreach (var c in value)
        {
            switch (c)
            {
                case '\n': sb.Append(' '); break;
                case '"': sb.Append("&quot;"); break;
                case '&': sb.Append("&amp;"); break;
                case '\'': sb.Append("&apos;"); break;
                case '<': sb.Append("&lt;"); break;
                case '>': sb.Append("&gt;"); break;
                default: sb.Append(c); break;
            }
        }
    }

    public static string FormatTime(DateTimeOffset t)
    {
        var offset = t.Offset;
        var sign = offset < TimeSpan.Zero ? '-' : '+';
        offset = offset.Duration();
        return t.ToString("yyyy-MM-dd_HH:mm", CultureInfo.InvariantCulture)
            + sign + offset.Hours.ToString("00", CultureInfo.InvariantCulture)
            + offset.Minutes.ToString("00", CultureInfo.InvariantCulture);
    }

    public static DateTimeOffset? ParseTime(string value)
    {
        var m = TimePattern().Match(value);
        if (!m.Success)
        {
            return null;
        }

        try
        {
            int N(int g) => int.Parse(m.Groups[g].Value, CultureInfo.InvariantCulture);
            var offset = new TimeSpan(N(7), N(8), 0);
            if (m.Groups[6].Value == "-")
            {
                offset = -offset;
            }

            return new DateTimeOffset(N(1), N(2), N(3), N(4), N(5), 0, offset);
        }
        catch (ArgumentException)
        {
            return null;
        }
    }

    /// <summary>Whole minutes since the stamp, or -1 when it can't be read.</summary>
    public static int MinutesSince(string stamp)
    {
        var then = ParseTime(stamp);
        if (then == null)
        {
            return -1;
        }

        // Truncated toward zero, as Java's integer division did, so a stamp a
        // few seconds in the future (clock skew) reads 0 rather than unreadable.
        return (int)(Clock() - then.Value).TotalMinutes;
    }

    [GeneratedRegex(@"^(\d{4})-(\d{2})-(\d{2})_(\d{2}):(\d{2})([+-])(\d{2})(\d{2})")]
    private static partial Regex TimePattern();
}
