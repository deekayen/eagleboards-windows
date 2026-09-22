namespace EagleBoards.Core.Records;

/// <summary>Makes empty records of one type and names its columns.</summary>
public sealed class RecordFactory<T>(Func<T> create, IReadOnlyList<string> columns)
    where T : DataRecord
{
    public T Create() => create();

    public IReadOnlyList<string> Columns { get; } = columns;
}
