using EagleBoards.Core;

namespace EagleBoards.App;

/// <summary>
/// What the Event page's find box shows for what was typed (SPEC.md D-21).
/// Reads no window state, so the wording can be tested without a window.
/// </summary>
/// <param name="Rooms">The rooms to narrow to, or null when nothing is typed and every room shows.</param>
/// <param name="Note">Shown beside the box: where anyone in no room is, or that no one by that name has signed in.</param>
/// <param name="Spoken">What a screen reader says: whom the rooms hold, then the note.</param>
internal sealed record PersonFindSummary(IReadOnlySet<string>? Rooms, string Note, string Spoken)
{
    /// <summary>The most people named in one sentence; the rest are counted.</summary>
    private const int NamedAtMost = 4;

    /// <summary>The find box with nothing typed in it.</summary>
    public static PersonFindSummary Empty { get; } = new(null, "", "");

    /// <summary>
    /// What to show and say for <paramref name="query"/>: the rooms holding a
    /// youth or an adult by that name, plus rooms named like it, and a note
    /// for anyone in no room.
    /// </summary>
    public static PersonFindSummary For(string query, IEnumerable<ScoutInfo> youth, IEnumerable<AdultInfo> adults,
        IEnumerable<string> roomNames)
    {
        query = query.Trim();
        if (query.Length == 0)
        {
            // An empty box narrows nothing (D-21); without this every room
            // would match "" and the cards would all be called found.
            return Empty;
        }

        var people = SchedulerLogic.FindPeople(query, youth, adults);
        var inRooms = people.Where(p => p.Room != null).ToList();
        var elsewhere = people.Where(p => p.Room == null).ToList();
        var named = roomNames.Where(r => r.Contains(query, StringComparison.OrdinalIgnoreCase)).ToList();
        var rooms = inRooms.Select(p => p.Room!).Concat(named).ToHashSet(StringComparer.Ordinal);

        var note = rooms.Count == 0 && elsewhere.Count == 0 ? "No one by that name has signed in."
            : string.Join(" ", elsewhere.Take(NamedAtMost).Select(p => $"{p.Name} {p.Where}."))
              + (elsewhere.Count > NamedAtMost ? $" And {elsewhere.Count - NamedAtMost} more." : "");
        var holds = string.Join(" ", inRooms.Take(NamedAtMost).Select(p => $"{p.Name} {p.Where}."))
            + (inRooms.Count > NamedAtMost ? $" And {inRooms.Count - NamedAtMost} more in rooms." : "")
            + (named.Count == 0 ? "" : $" {Display.Count(named.Count, "room", "rooms")} by that name: {string.Join(", ", named)}.");
        return new PersonFindSummary(rooms, note, (holds.Trim() + " " + note).Trim());
    }
}
