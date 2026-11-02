using System.Collections.ObjectModel;
using System.Globalization;
using System.Runtime.CompilerServices;
using EagleBoards.Core;
using EagleBoards.Core.Records;

namespace EagleBoards.App;

/// <summary>
/// Refresh a bound collection in place: update rows that are still there,
/// add new ones, drop gone ones. Replacing the collection would lose the
/// grid's selection, scroll position and sort on every change.
/// </summary>
public static class RowSync
{
    public static void Sync<T>(ObservableCollection<T> target, IReadOnlyList<IReadOnlyDictionary<string, string>> fresh,
        Func<IReadOnlyDictionary<string, string>, T> create, Action<T, IReadOnlyDictionary<string, string>> update)
        where T : Row
    {
        var byId = new Dictionary<string, IReadOnlyDictionary<string, string>>(StringComparer.Ordinal);
        foreach (var r in fresh)
        {
            byId[Row.V(r, "ID")] = r;
        }

        for (var i = target.Count - 1; i >= 0; i--)
        {
            if (!byId.ContainsKey(target[i].Id))
            {
                target.RemoveAt(i);
            }
        }

        var existing = target.ToDictionary(t => t.Id, StringComparer.Ordinal);
        foreach (var (id, r) in byId)
        {
            if (existing.TryGetValue(id, out var row))
            {
                update(row, r);
            }
            else
            {
                target.Add(create(r));
            }
        }
    }
}
