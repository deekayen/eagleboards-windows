using EagleBoards.Core.Records;
using EagleBoards.Core.Storage;

namespace EagleBoards.Core.Import;

/// <summary>
/// Reads the district website's pre-registration CSV (the <c>-p</c> option):
/// a header row naming the columns, quoted fields allowed. Ported from the
/// Java <c>DataFileConverter</c> and its two subclasses.
/// </summary>
internal abstract class PreRegConverter<T>(DataRecordFile<T> file, Action<string> trace)
    where T : PersonRecord
{
    private static readonly string[] ExpectedColumns = ["Email", "First Name", "Last Name", "Scouts Contact Number", "Scoutmasters Name", "Unit Number"];

    protected DataRecordFile<T> File { get; } = file;

    /// <summary>Per-column converters by the file's column heading.</summary>
    protected abstract IReadOnlyDictionary<string, Func<DataRecord, string, bool>> Converters { get; }

    /// <summary>File headings that map onto a differently named record column.</summary>
    protected abstract IReadOnlyDictionary<string, string> ColumnMap { get; }

    public void Convert(string path)
    {
        using var reader = new StringReader(TextFiles.Read(path));
        string[]? header = null;
        var lineNo = 0;
        while (reader.ReadLine() is { } line)
        {
            if (header == null)
            {
                header = ParseLine(line);
                foreach (var expected in ExpectedColumns)
                {
                    if (!header.Contains(expected))
                    {
                        throw new InvalidDataException($"error: '{Path.GetFullPath(path)}'. Invalid file format: expected column: {expected}");
                    }
                }

                continue;
            }

            lineNo++;
            var values = ParseLine(line);
            var record = File.CreateNew();
            var ok = true;
            var why = "";
            for (var i = 0; i < header.Length && ok; i++)
            {
                var value = i < values.Length ? values[i] : "";
                if (Converters.TryGetValue(header[i], out var convert))
                {
                    ok = convert(record, value);
                    why = $"invalid col '{header[i]}' with value = '{value}'";
                }
                else if (FieldName(header[i]) is { } field)
                {
                    record.SetValue(field, value);
                }
            }

            if (ok)
            {
                record.PostLoadUpdate();
                Accept(record);
            }
            else
            {
                trace($" rejected [{Path.GetFileName(path)}:{lineNo}]:{why} ({line})");
            }
        }
    }

    protected virtual void Accept(T record)
    {
        if (File.Get(record.Id) == null)
        {
            File.Add(record, false);
        }
    }

    private string? FieldName(string heading)
    {
        heading = heading.Trim();
        var name = ColumnMap.TryGetValue(heading, out var mapped) ? mapped : heading;
        return File.Columns.FirstOrDefault(c => string.Equals(c, name, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>Split on commas, honouring "double" or 'single' quoted fields.</summary>
    internal static string[] ParseLine(string line)
    {
        var fields = new List<string>();
        var current = new System.Text.StringBuilder();
        char? quote = null;
        foreach (var c in line)
        {
            if (quote != null)
            {
                if (c == quote)
                {
                    quote = null;
                }
                else
                {
                    current.Append(c);
                }
            }
            else if (c == ',')
            {
                fields.Add(current.ToString());
                current.Clear();
            }
            else if (c is '"' or '\'')
            {
                quote = c;
            }
            else
            {
                current.Append(c);
            }
        }

        fields.Add(current.ToString());
        return fields.ToArray();
    }
}

internal sealed class PreRegScoutConverter(DataRecordFile<ScoutRecord> file, Action<string> trace)
    : PreRegConverter<ScoutRecord>(file, trace)
{
    protected override IReadOnlyDictionary<string, Func<DataRecord, string, bool>> Converters { get; } =
        new Dictionary<string, Func<DataRecord, string, bool>>
        {
            ["First Name"] = FieldConverters.FirstName,
            ["Last Name"] = FieldConverters.LastName,
            // Not "Scouts Contact Number": a youth's phone number isn't kept
            // (SPEC.md D-8). The adults' converter still reads it.
            ["request_status"] = (_, v) => FieldConverters.NotCancelled(v),
            ["Item"] = FieldConverters.ScoutBoardType,
            ["Unit Number"] = FieldConverters.Unit,
            ["Scoutmasters Name"] = FieldConverters.Leader,
            ["Life-to-Eagle Coach"] = FieldConverters.Leader,
        };

    protected override IReadOnlyDictionary<string, string> ColumnMap { get; } = new Dictionary<string, string>
    {
        ["Email"] = "Email",
        ["id"] = "",
    };

    /// <summary>
    /// A file with a birthdate or a phone column doesn't put either on file
    /// for a youth (SPEC.md D-7, D-8).
    /// </summary>
    protected override void Accept(ScoutRecord record)
    {
        record.SetValue(ScoutRecord.DobField, "");
        record.SetValue(ScoutRecord.PhoneField, "");
        base.Accept(record);
    }
}

internal sealed class PreRegAdultConverter(DataRecordFile<AdultRecord> file, Action<string> trace)
    : PreRegConverter<AdultRecord>(file, trace)
{
    protected override IReadOnlyDictionary<string, Func<DataRecord, string, bool>> Converters { get; } =
        new Dictionary<string, Func<DataRecord, string, bool>>
        {
            ["name"] = FieldConverters.FullName,
            ["phone"] = FieldConverters.Phone,
            ["request_status"] = (_, v) => FieldConverters.NotCancelled(v),
            ["Item"] = FieldConverters.AdultSlot,
            ["UnitNumber"] = FieldConverters.Unit,
            ["Unit Number"] = FieldConverters.Unit,
            ["First Name"] = FieldConverters.FirstName,
            ["Last Name"] = FieldConverters.LastName,
            ["Scouts Contact Number"] = FieldConverters.Phone,
        };

    protected override IReadOnlyDictionary<string, string> ColumnMap { get; } = new Dictionary<string, string>
    {
        ["email"] = "Email",
        ["Unit Type"] = "UnitType",
        ["Unit Number"] = "Unit",
        ["id"] = "",
    };

    /// <summary>
    /// Known by ID: refresh phone and email. Otherwise by email: one match gets
    /// its phone refreshed, none means a new adult, several means ambiguous and
    /// the row is left alone.
    /// </summary>
    protected override void Accept(AdultRecord record)
    {
        var known = File.Get(record.Id);
        if (known != null)
        {
            known.UpdateFrom(record, ["Phone", "Email"]);
            return;
        }

        var byEmail = File.Where("Email", record.Email);
        if (byEmail.Count == 0)
        {
            File.Add(record, false);
        }
        else if (byEmail.Count == 1)
        {
            byEmail[0].UpdateFrom(record, ["Phone"]);
        }
    }
}
