using System.Text;
using EagleBoards.Core.Records;
using EagleBoards.Core.Storage;

namespace EagleBoards.Tests;

/// <summary>
/// The files on disk are shared with the Java version and with every event
/// already recorded, so the format is pinned here.
/// </summary>
[Collection(ClockCollection.Name)]
public class StorageTests
{
    [Fact]
    public void CommasAndLineBreaksAreFlattenedNotQuoted()
    {
        using var box = new Sandbox();
        var path = Path.Combine(box.Root, "rooms.csv");
        var file = new DataRecordFile<RoomRecord>(path, RoomRecord.Factory);
        var room = file.AddNew("ROOM:101", false);
        room.Put("Room", "101");
        room.Leaders = "Ann Able,Bob Baker\nCarl";
        file.Store();

        var lines = File.ReadAllText(path).Split('\n');
        Assert.Equal("Type,ID,Room,BoardType,Scout,Leaders,RegTime", lines[0]);
        Assert.StartsWith("ROOM,ROOM:101,101,,,Ann Able~Bob Baker+Carl,", lines[1]);
    }

    [Fact]
    public void AFileWrittenByTheJavaVersionLoads()
    {
        using var box = new Sandbox();
        var path = Path.Combine(box.Root, "scouts.csv");
        File.WriteAllText(path,
            string.Join(',', ScoutRecord.AllColumns) + "\n"
            + "SCOUT,SCOUT:Aldridge:Alexander:1001,W1,Aldridge,Alexander,s1@example.org,,Troop,1001,T1001,,Final,,"
            + "2026-09-22_18:05-0500,2026-09-22_18:05-0500,,,Registered,,,,,,\n");

        var file = new DataRecordFile<ScoutRecord>(path, ScoutRecord.Factory);
        var scout = Assert.Single(file.Records);
        Assert.Equal("SCOUT:Aldridge:Alexander:1001", scout.Id);
        Assert.Equal("Registered", scout.Status);
        Assert.Equal("Final", scout.BoardType);

        // UnitName is always recomputed from the type and number, which also
        // upgrades the old one-letter form.
        Assert.Equal("Troop1001", scout.UnitName);
    }

    [Fact]
    public void AnOldAnsiAdultHistoryKeepsItsAccents()
    {
        using var box = new Sandbox();
        var path = Path.Combine(box.Root, "history.csv");
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        var ansi = Encoding.GetEncoding(1252);
        File.WriteAllBytes(path, ansi.GetBytes(string.Join(',', AdultRecord.AllColumns) + "\nADULT,ADULT:Peña:José:12,Peña,José,,,Troop,12,,Member,Member,,,,,\n"));

        var file = new DataRecordFile<AdultRecord>(path, AdultRecord.Factory);
        Assert.Equal("José", Assert.Single(file.Records).First);

        // And it is written back as UTF-8 from then on.
        file.Store();
        Assert.Contains("Peña", File.ReadAllText(path, Encoding.UTF8), StringComparison.Ordinal);
    }

    [Fact]
    public void AnExcelByteOrderMarkIsNotPartOfTheFirstColumn()
    {
        using var box = new Sandbox();
        var path = Path.Combine(box.Root, "history.csv");
        File.WriteAllText(path, string.Join(',', AdultRecord.AllColumns) + "\nADULT,ADULT:Able:Ann:1,Able,Ann,,,Troop,1,,Chair,Chair,,,,,\n",
            new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));

        var file = new DataRecordFile<AdultRecord>(path, AdultRecord.Factory);
        Assert.Equal("ADULT", Assert.Single(file.Records).Type);
    }

    [Fact]
    public void StoreLeavesNoTemporaryFileBehind()
    {
        using var box = new Sandbox();
        var path = Path.Combine(box.Root, "rooms.csv");
        var file = new DataRecordFile<RoomRecord>(path, RoomRecord.Factory);
        file.AddNew("ROOM:1", true);
        Assert.False(File.Exists(path + ".tmp"));
    }

    [Fact]
    public void JavaPropertiesSyntaxIsUnderstood()
    {
        var parsed = JavaProperties.Parse("""
            # comment
            ! also a comment
            RefreshTimeSecs = 15
            FinalRedMins:50
            Name  DEFAULT
            ConveneRedMins=3\
              5
            Escaped=a\tbA
            """);
        Assert.Equal("15", parsed["RefreshTimeSecs"]);
        Assert.Equal("50", parsed["FinalRedMins"]);
        Assert.Equal("DEFAULT", parsed["Name"]);
        Assert.Equal("35", parsed["ConveneRedMins"]);
        Assert.Equal("a\tbA", parsed["Escaped"]);
        Assert.False(parsed.ContainsKey("# comment"));
    }

    [Fact]
    public void ConfigFillsInDefaultsForAnythingMissing()
    {
        using var box = new Sandbox();
        File.WriteAllText(box.ConfigPath, "FinalRedMins=50\nRefreshTimeSecs=\n");
        var file = new DataRecordFile<ConfigRecord>(box.ConfigPath, ConfigRecord.Factory);
        var config = Assert.Single(file.Records);
        Assert.Equal(50, config.FinalRedMins);
        Assert.Equal(30, config.RefreshTimeSecs);
        Assert.Equal("DEFAULT", config.Id);
    }

    [Fact]
    public void RetiredColourKeysLoadAndAreDroppedOnSave()
    {
        // SPEC.md D-19: an older config.properties with the status colours
        // still loads, and the next save leaves them out.
        using var box = new Sandbox();
        File.WriteAllText(box.ConfigPath, "Type=CONFIG\nID=DEFAULT\nName=DEFAULT\nFinalRedMins=50\n"
            + "RegisteredColor=#ffcccc\nInProgressHiColor=#66ff66\n");
        var file = new DataRecordFile<ConfigRecord>(box.ConfigPath, ConfigRecord.Factory);
        var config = Assert.Single(file.Records);
        Assert.Equal(50, config.FinalRedMins);
        Assert.DoesNotContain(ConfigRecord.AllColumns, c => c.EndsWith("Color", StringComparison.Ordinal));

        file.Store();
        var saved = File.ReadAllText(box.ConfigPath);
        Assert.Contains("FinalRedMins=50\n", saved);
        Assert.DoesNotContain("Color", saved);
    }

    [Fact]
    public void TimesUseJavasFormatAndCountWholeMinutes()
    {
        var saved = DataRecord.Clock;
        try
        {
            var now = new DateTimeOffset(2026, 9, 22, 19, 30, 45, TimeSpan.FromHours(-5));
            DataRecord.Clock = () => now;
            Assert.Equal("2026-09-22_19:30-0500", DataRecord.FormatTime(now));
            Assert.Equal(12, DataRecord.MinutesSince("2026-09-22_19:18-0500"));
            Assert.Equal(0, DataRecord.MinutesSince("2026-09-22_19:31-0500"));
            Assert.Equal(-1, DataRecord.MinutesSince("not a time"));
        }
        finally
        {
            DataRecord.Clock = saved;
        }
    }
}
