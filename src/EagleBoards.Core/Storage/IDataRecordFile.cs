using EagleBoards.Core.Records;

namespace EagleBoards.Core.Storage;

/// <summary>A data file seen without its record type, for the generic grid and edit paths.</summary>
public interface IDataRecordFile
{
    string FilePath { get; }

    IReadOnlyList<string> Columns { get; }

    IEnumerable<DataRecord> AllRecords { get; }

    DataRecord? Find(string? id);

    IEnumerable<DataRecord> FindWhere(string field, string value);

    DataRecord AddNewRecord(string id);

    void Remove(string id);

    void Store();
}
