using System.Globalization;
using EagleBoards.Core.Records;
using EagleBoards.Core.Storage;

namespace EagleBoards.Core;

/// <summary>
/// A proposal approved at an earlier event (SPEC.md D-22), with only what
/// the Approved proposals page shows: never a birthdate, phone number or
/// email. Its ID is the event's date and the youth's ID, since the same
/// youth can come up at more than one event.
/// </summary>
public sealed class ApprovalRecord : DataRecord
{
    public static readonly IReadOnlyList<string> AllColumns = ["Type", "ID", "Event", "Last", "First", "UnitType", "Unit", "BoardChair", "BoardMembers", "Notes"];

    /// <summary>What is copied from the youth's record, as the file holds it.</summary>
    internal static readonly string[] Copied = ["Last", "First", "UnitType", "Unit", "BoardChair", "BoardMembers", "Notes"];

    public ApprovalRecord(IEnumerable<KeyValuePair<string, string>> source)
        : base("APPROVAL", AllColumns, source)
    {
    }
}

/// <summary>
/// What <see cref="EarlierEvents.ReadApprovedProposals"/> found: the
/// approvals, the dates of the earlier events read (oldest first), and the
/// folders whose youth couldn't be read.
/// </summary>
public sealed record ApprovedProposals(IReadOnlyList<ApprovalRecord> Approvals, IReadOnlyList<string> Events, IReadOnlyList<string> Unreadable)
{
    /// <summary>How many earlier events were read, and from which date to which.</summary>
    public string About => Events.Count switch
    {
        0 => "No earlier events in this data folder.",
        1 => $"Read from 1 earlier event, {Events[0]}.",
        _ => $"Read from {Events.Count} earlier events, {Events[0]} to {Events[^1]}.",
    };
}

/// <summary>
/// The events held in the data folder beside this one. Read only: nothing
/// in an earlier event's folder is ever written.
/// </summary>
public static class EarlierEvents
{
    /// <summary>
    /// SPEC.md D-22: whose project proposal was approved at an earlier event,
    /// when, and by whom, for a youth who comes to a board of review without
    /// the signed page. Reads every dated folder (<c>YYYY-MM-DD</c>) beside
    /// <paramref name="eventFolder"/> dated before this event, however long
    /// ago: a project can take more than a year. The event's date is its
    /// folder's name, or today if that isn't a date. From each, the
    /// <c>scouts.csv</c> rows whose board was a Project review and whose
    /// result was Approved. A folder with no <c>scouts.csv</c> held no event;
    /// one that can't be read is named in <see cref="ApprovedProposals.Unreadable"/>,
    /// and the rest are still read.
    /// </summary>
    public static ApprovedProposals ReadApprovedProposals(string eventFolder, DateTimeOffset now)
    {
        var approvals = new List<ApprovalRecord>();
        var events = new List<string>();
        var unreadable = new List<string>();
        var full = Path.GetFullPath(eventFolder).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var eventDate = DateOf(Path.GetFileName(full)) ?? DateOnly.FromDateTime(now.Date);
        if (Path.GetDirectoryName(full) is not { } dataFolder || !Directory.Exists(dataFolder))
        {
            return new ApprovedProposals(approvals, events, unreadable);
        }

        var earlier = Directory.EnumerateDirectories(dataFolder)
            .Select(Path.GetFileName)
            .OfType<string>()
            .Where(name => DateOf(name) is { } date && date < eventDate)
            .Order(StringComparer.Ordinal);
        foreach (var name in earlier)
        {
            var path = Path.Combine(dataFolder, name, "scouts.csv");
            if (!File.Exists(path))
            {
                continue;
            }

            try
            {
                var found = new List<ApprovalRecord>();
                foreach (var youth in DataRecordFile<ScoutRecord>.ReadOnly(path, ScoutRecord.Factory).Where(r => r.GetValue("BoardType") == BoardTypes.Project && r.GetValue("Result") == BoardResults.Approved))
                {
                    var fields = ApprovalRecord.Copied.ToDictionary(f => f, youth.GetValue, StringComparer.Ordinal);
                    fields["ID"] = name + ":" + youth.Id;
                    fields["Event"] = name;
                    found.Add(new ApprovalRecord(fields));
                }

                approvals.AddRange(found);
                events.Add(name);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or FormatException or ArgumentException or IndexOutOfRangeException)
            {
                unreadable.Add(name);
            }
        }

        return new ApprovedProposals(approvals, events, unreadable);
    }

    /// <summary>The date a folder is named by, or null if it isn't named <c>YYYY-MM-DD</c>.</summary>
    private static DateOnly? DateOf(string name) =>
        name.Length == 10 && DateOnly.TryParseExact(name, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var date) ? date : null;
}
