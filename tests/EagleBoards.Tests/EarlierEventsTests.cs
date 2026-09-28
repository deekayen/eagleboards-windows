using EagleBoards.Core;
using EagleBoards.Core.Records;
using EagleBoards.Core.Storage;

namespace EagleBoards.Tests;

/// <summary>SPEC.md D-22: proposals approved at earlier events, read from their folders.</summary>
public class EarlierEventsTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 27, 19, 0, 0, TimeSpan.FromHours(-4));

    [Fact]
    public void OnlyProjectApprovalsFromEarlierEventsAreRead()
    {
        using var box = new Sandbox();
        var eventFolder = Path.Combine(box.Root, "2026-09-27");
        Plant(box, "2025-03-11", Youth("Ashby", "Ava", "3101", BoardTypes.Project, BoardResults.Approved));
        Plant(box, "2026-08-25",
            Youth("Byrne", "Bo", "3102", BoardTypes.Project, BoardResults.Approved),
            Youth("Cole", "Cy", "3103", BoardTypes.Project, BoardResults.NotApproved),
            Youth("Dent", "Di", "3104", BoardTypes.Final, BoardResults.Approved),
            Youth("Eyre", "Ed", "3105", BoardTypes.Project, ""));
        Plant(box, "2026-09-27", Youth("Tonight", "Tia", "3106", BoardTypes.Project, BoardResults.Approved));
        Plant(box, "2026-10-25", Youth("Later", "Lu", "3107", BoardTypes.Project, BoardResults.Approved));
        Plant(box, "run", Youth("Undated", "Una", "3108", BoardTypes.Project, BoardResults.Approved));
        Directory.CreateDirectory(Path.Combine(box.Root, "2026-01-10"));
        var before = File.ReadAllText(Path.Combine(box.Root, "2026-08-25", "scouts.csv"));

        var read = EarlierEvents.ReadApprovedProposals(eventFolder, Now);

        // More than a year back is in; the event's own date, a later one and a
        // folder not named by a date are out, and so is one with no youth file.
        Assert.Equal(["2025-03-11", "2026-08-25"], read.Events);
        Assert.Equal("Read from 2 earlier events, 2025-03-11 to 2026-08-25.", read.About);
        Assert.Equal(["Ashby", "Byrne"], read.Approvals.Select(a => a.GetValue("Last")));
        var byrne = read.Approvals[1];
        Assert.Equal("2026-08-25", byrne.GetValue("Event"));
        Assert.Equal("2026-08-25|SCOUT:Byrne:Bo:3102", byrne.Id);
        Assert.Equal("Chair Person", byrne.GetValue("BoardChair"));
        Assert.Equal("Well planned~ and safe", byrne.GetValue("Notes"));

        // Never a birthdate, phone number or email, though the files hold them.
        Assert.All(read.Approvals, a => Assert.DoesNotContain(a.Fields.Keys, k => k is "DOB" or "Phone" or "Email"));
        Assert.Empty(read.Unreadable);

        // Nothing in an earlier folder is written.
        Assert.Equal(before, File.ReadAllText(Path.Combine(box.Root, "2026-08-25", "scouts.csv")));
    }

    [Fact]
    public void AFolderThatCantBeReadIsNamedAndTheRestAreStillRead()
    {
        using var box = new Sandbox();
        Plant(box, "2026-06-01", Youth("Frost", "Fay", "3201", BoardTypes.Project, BoardResults.Approved));
        Plant(box, "2026-07-01", Youth("Gale", "Gus", "3202", BoardTypes.Project, BoardResults.Approved));
        using (File.Open(Path.Combine(box.Root, "2026-06-01", "scouts.csv"), FileMode.Open, FileAccess.Read, FileShare.None))
        {
            var read = EarlierEvents.ReadApprovedProposals(Path.Combine(box.Root, "2026-09-27"), Now);
            Assert.Equal(["2026-06-01"], read.Unreadable.Select(u => u.Event));
            Assert.Equal(["2026-07-01"], read.Events);
            Assert.Equal("Gale", Assert.Single(read.Approvals).GetValue("Last"));
        }
    }

    [Fact]
    public void AYouthFileThatIsntAFileIsNamedAndApprovalsAreByLastNameAcrossEvents()
    {
        // Event test section 28 plants a folder whose scouts.csv is a folder.
        using var box = new Sandbox();
        Plant(box, "2019-05-28", Youth("Quill", "Ada", "3701", BoardTypes.Project, BoardResults.Approved));
        Plant(box, "2025-08-26", Youth("Brook", "Ben", "3702", BoardTypes.Project, BoardResults.Approved));
        Directory.CreateDirectory(Path.Combine(box.Root, "2025-07-22", "scouts.csv"));

        var read = EarlierEvents.ReadApprovedProposals(Path.Combine(box.Root, "2026-09-27"), Now);
        Assert.Equal(["Brook", "Quill"], read.Approvals.Select(a => a.GetValue("Last")));
        Assert.Equal(["2019-05-28", "2025-08-26"], read.Events);
        Assert.Equal("2025-07-22", Assert.Single(read.Unreadable).Event);
    }

    [Fact]
    public void AnEventInAFolderNotNamedByADateIsTodays()
    {
        using var box = new Sandbox();
        Plant(box, "2026-09-26", Youth("Hale", "Hal", "3301", BoardTypes.Project, BoardResults.Approved));
        Plant(box, "2026-09-27", Youth("Ives", "Ida", "3302", BoardTypes.Project, BoardResults.Approved));

        var read = EarlierEvents.ReadApprovedProposals(box.DataDir, Now);
        Assert.Equal("Hale", Assert.Single(read.Approvals).GetValue("Last"));

        var none = EarlierEvents.ReadApprovedProposals(Path.Combine(box.Root, "2026-09-26"), Now);
        Assert.Empty(none.Approvals);
        Assert.Equal("No earlier events in this data folder.", none.About);
    }

    private static ScoutRecord Youth(string last, string first, string unit, string boardType, string result) => new(new Dictionary<string, string>
    {
        ["ID"] = $"SCOUT:{last}:{first}:{unit}",
        ["Last"] = last,
        ["First"] = first,
        ["UnitType"] = "Troop",
        ["Unit"] = unit,
        ["Email"] = $"{first.ToLowerInvariant()}@example.org",
        ["Phone"] = "555-0100",
        ["DOB"] = "2010-04-01",
        ["BoardType"] = boardType,
        ["Status"] = BoardStatus.Completed,
        ["Result"] = result,
        ["BoardChair"] = "Chair Person",
        ["BoardMembers"] = "Chair Person,Member One,Member Two",
        ["Notes"] = "Well planned, and safe",
    });

    private static void Plant(Sandbox box, string folder, params ScoutRecord[] youth)
    {
        Directory.CreateDirectory(Path.Combine(box.Root, folder));
        var file = new DataRecordFile<ScoutRecord>(Path.Combine(box.Root, folder, "scouts.csv"), ScoutRecord.Factory);
        foreach (var y in youth)
        {
            file.Add(y, false);
        }

        file.Store();
    }
}
