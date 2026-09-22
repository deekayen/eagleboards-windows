using System.Text;
using EagleBoards.Core.Records;

namespace EagleBoards.Core.Storage;

/// <summary>
/// One data file and the records in it, kept in file order plus an index by
/// ID. The whole file is rewritten on every <see cref="Store"/>.
///
/// Format is chosen by extension, as in the Java version: ".properties" is a
/// single record as key=value lines (the settings), anything else is a CSV
/// with a header row. Not thread-safe; <see cref="BoardService"/> serializes
/// every access under its lock.
/// </summary>
public sealed class DataRecordFile<T> : IDataRecordFile
    where T : DataRecord
{
    private readonly RecordFactory<T> _factory;
    private readonly List<T> _records = [];
    private readonly Dictionary<string, T> _byId = new(StringComparer.Ordinal);

    public DataRecordFile(string path, RecordFactory<T> factory)
    {
        FilePath = Path.GetFullPath(path);
        _factory = factory;
        if (File.Exists(FilePath))
        {
            Load();
        }
        else
        {
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
            File.WriteAllBytes(FilePath, []);
        }
    }

    public string FilePath { get; }

    public IReadOnlyList<string> Columns => _factory.Columns;

    public IReadOnlyList<T> Records => _records;

    private bool IsProperties => FilePath.EndsWith(".properties", StringComparison.OrdinalIgnoreCase);

    public T CreateNew() => _factory.Create();

    public T AddNew(string id, bool store)
    {
        var record = _factory.Create();
        record.Put(DataRecord.IdField, id);
        Add(record, store);
        return record;
    }

    public void Add(T record, bool store)
    {
        _records.Add(record);
        _byId[record.Id] = record;
        if (store)
        {
            Store();
        }
    }

    public T? Get(string? id) => id != null && _byId.TryGetValue(id, out var r) ? r : null;

    /// <summary>Every record whose raw <paramref name="field"/> equals <paramref name="value"/>.</summary>
    public List<T> Where(string field, string value) => _records.Where(r => r.Get(field) == value).ToList();

    public void Remove(string id)
    {
        if (_byId.Remove(id, out var record))
        {
            _records.Remove(record);
        }
    }

    public void ClearAll()
    {
        _records.Clear();
        _byId.Clear();
    }

    IEnumerable<DataRecord> IDataRecordFile.AllRecords => _records;

    DataRecord? IDataRecordFile.Find(string? id) => Get(id);

    IEnumerable<DataRecord> IDataRecordFile.FindWhere(string field, string value) => Where(field, value);

    DataRecord IDataRecordFile.AddNewRecord(string id) => AddNew(id, false);

    public void Store()
    {
        var sb = new StringBuilder();
        if (IsProperties)
        {
            sb.Append("# Review Board Scheduler configuration\n");
            sb.Append("# Edit the values after each '='. Lines starting with # are comments.\n\n");
            foreach (var record in _records)
            {
                foreach (var column in Columns)
                {
                    sb.Append(column).Append('=').Append(record.GetValue(column)).Append('\n');
                }
            }
        }
        else
        {
            sb.AppendJoin(',', Columns).Append('\n');
            foreach (var record in _records)
            {
                record.ToCsv(sb, ',');
                sb.Append('\n');
            }
        }

        TextFiles.WriteAtomically(FilePath, sb.ToString());
    }

    public void Load()
    {
        var text = TextFiles.Read(FilePath);
        if (IsProperties)
        {
            var properties = JavaProperties.Parse(text);
            var record = _factory.Create();
            foreach (var column in Columns)
            {
                // Blank values are skipped so they can't clobber a default.
                if (properties.TryGetValue(column, out var value) && value.Length > 0)
                {
                    record.Put(column, value);
                }
            }

            Add(record, false);
            return;
        }

        using var reader = new StringReader(text);
        string[]? header = null;
        while (reader.ReadLine() is { } line)
        {
            if (header == null)
            {
                // StringTokenizer semantics: empty tokens are skipped.
                header = line.Split(',', StringSplitOptions.RemoveEmptyEntries);
                continue;
            }

            var record = _factory.Create();
            record.FromCsv(line, ',', header);
            Add(record, false);
        }
    }
}
