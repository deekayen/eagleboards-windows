using EagleBoards.Core;
using EagleBoards.Core.Import;
using EagleBoards.Core.Records;
using EagleBoards.Core.Storage;

namespace EagleBoards.Tests;

/// <summary>
/// Behaviour the HTTP evening test (scripts/test-board-evening.sh) doesn't
/// reach, mostly the places this port deliberately differs from the Java one.
/// </summary>
[Collection(ClockCollection.Name)]
public class BoardServiceTests
{
    private static string Status(BoardService s, string scoutId) =>
        s.Snapshot(DataTable.Scouts).Single(r => r["ID"] == scoutId)["Status"];

    private static Dictionary<string, string> AdultRow(BoardService s, string id) =>
        s.Snapshot(DataTable.Adults).Single(r => r["ID"] == id);

    [Fact]
    public void WalkInsAndPreRegisteredAreNumberedSeparately()
    {
        using var box = new Sandbox();
        var s = box.Open();
        s.SaveRow(DataTable.ScoutsScheduled, "inserted", "SCOUT:Bram:Beau:1002", new Dictionary<string, string> { ["Email"] = "beau@example.org" });

        s.RegisterScout(Seed.Scout("Aldridge", "Alex", "1001", "Final"));
        s.RegisterScout(Seed.Scout("Bram", "Beau", "1002", "Final", "beau@example.org"));
        s.RegisterScout(Seed.Scout("Carrington", "Cora", "1003", "Project"));

        var regNums = s.Snapshot(DataTable.Scouts).ToDictionary(r => r["Last"], r => r["RegNum"]);
        Assert.Equal("W1", regNums["Aldridge"]);
        Assert.Equal("P1", regNums["Bram"]);
        Assert.Equal("W2", regNums["Carrington"]);
    }

    [Fact]
    public void SubmittingTheSignInFormTwiceKeepsYourPlace()
    {
        using var box = new Sandbox();
        var s = box.Open();
        s.RegisterScout(Seed.Scout("Aldridge", "Alex", "1001", "Final"));
        s.RegisterScout(Seed.Scout("Bram", "Beau", "1002", "Final"));
        s.RegisterScout(Seed.Scout("Aldridge", "Alex", "1001", "Final"));

        var aldridge = s.Snapshot(DataTable.Scouts).Single(r => r["Last"] == "Aldridge");
        Assert.Equal("W1", aldridge["RegNum"]);
        Assert.Equal(2, s.Snapshot(DataTable.Scouts).Count);
    }

    [Fact]
    public void ABlankEmailDoesNotMatchABlankPreRegistration()
    {
        using var box = new Sandbox();
        var s = box.Open();
        s.SaveRow(DataTable.ScoutsScheduled, "inserted", "SCOUT:Other:Otto:9", new Dictionary<string, string> { ["Email"] = "" });
        s.RegisterScout(Seed.Scout("Aldridge", "Alex", "1001", "Final"));
        Assert.Equal("W1", s.Snapshot(DataTable.Scouts).Single()["RegNum"]);
    }

    [Fact]
    public void AnAdultSignInIsRecordedInTheHistory()
    {
        using var box = new Sandbox();
        var s = box.Open();
        s.RegisterAdult(Seed.Adult("Able", "Ann", "2001", "Chair", "Member"));
        var history = Assert.Single(s.Snapshot(DataTable.AdultHistory));
        Assert.Equal("ADULT:Able:Ann:2001", history["ID"]);
        Assert.Matches(@"^\(\d{4}-\d{2}-\d{2}\)$", history["BoardHistory"]);
        Assert.Equal("W", AdultRow(s, "ADULT:Able:Ann:2001")["Flags"]);

        // Next event night: a new tonight's list, the same history.
        var s2 = BoardService.Open(new EventOptions { DataDirectory = Path.Combine(box.Root, "night2"), AdultHistoryPath = box.HistoryPath, ConfigPath = box.ConfigPath });
        s2.RegisterAdult(Seed.Adult("Able", "Ann", "2001", "Chair", "Chair"));
        Assert.Equal("P", AdultRow(s2, "ADULT:Able:Ann:2001")["Flags"]);
        var again = Assert.Single(s2.Snapshot(DataTable.AdultHistory));
        Assert.Equal("Chair", again["FinalBoard"]);
    }

    private static (BoardService Service, string Scout, string Chair, string M1, string M2) SeatableEvening(Sandbox box)
    {
        var s = box.Open();
        s.AddRoom("101", BoardTypes.Final);
        s.RegisterScout(Seed.Scout("Aldridge", "Alex", "1001", BoardTypes.Final));
        s.RegisterAdult(Seed.Adult("Able", "Ann", "2001", "Member", "Chair"));
        s.RegisterAdult(Seed.Adult("Baker", "Bob", "2002", "Member", "Member"));
        s.RegisterAdult(Seed.Adult("Cole", "Cat", "2003", "Member", "Member"));
        return (s, Seed.ScoutId("Aldridge", "Alex", "1001"), Seed.AdultId("Able", "Ann", "2001"),
            Seed.AdultId("Baker", "Bob", "2002"), Seed.AdultId("Cole", "Cat", "2003"));
    }

    [Fact]
    public void OnePersonListedThreeTimesIsNotABoardOfThree()
    {
        using var box = new Sandbox();
        var (s, scout, chair, _, _) = SeatableEvening(box);
        var result = s.SeatBoard("ROOM:101", scout, chair, $"{chair},{chair},{chair}");
        Assert.False(result.Ok);
        Assert.Contains("more than once", result.Message, StringComparison.Ordinal);
        Assert.Equal(BoardStatus.Registered, Status(s, scout));
    }

    [Fact]
    public void AnUnavailableAdultCannotBeSeatedEvenAfterTheChair()
    {
        using var box = new Sandbox();
        var (s, scout, chair, m1, _) = SeatableEvening(box);
        s.RegisterAdult(Seed.Adult("Dunn", "Dee", "2004", "Member", "Unavailable"));
        var result = s.SeatBoard("ROOM:101", scout, chair, $"{chair},{m1},{Seed.AdultId("Dunn", "Dee", "2004")}");
        Assert.False(result.Ok);
        Assert.Contains("Unavailable", result.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void SeatingUsesUpTheOperatorsPicks()
    {
        using var box = new Sandbox();
        var (s, scout, chair, m1, m2) = SeatableEvening(box);
        foreach (var id in new[] { chair, m1, m2 })
        {
            s.SaveRow(DataTable.Adults, "updated", id, new Dictionary<string, string> { ["Sel"] = "1" });
        }

        Assert.True(s.SeatBoard("ROOM:101", scout, chair, $"{chair},{m1},{m2}").Ok);
        Assert.All(new[] { chair, m1, m2 }, id => Assert.Equal("0", AdultRow(s, id)["Sel"]));
        Assert.All(new[] { chair, m1, m2 }, id => Assert.Equal("101", AdultRow(s, id)["Room"]));
        Assert.Equal(BoardStatus.Seated, Status(s, scout));
    }

    [Fact]
    public void AResultCannotBeRecordedWhileTheBoardIsStillConvening()
    {
        using var box = new Sandbox();
        var (s, scout, chair, m1, m2) = SeatableEvening(box);
        Assert.True(s.SeatBoard("ROOM:101", scout, chair, $"{chair},{m1},{m2}").Ok);
        Assert.False(s.CompleteBoard(scout, BoardResults.Approved, "").Ok);
        Assert.True(s.StartReview(scout).Ok);
        Assert.True(s.CompleteBoard(scout, BoardResults.Approved, "fine").Ok);
        Assert.Equal(BoardStatus.Completed, Status(s, scout));
        Assert.Equal("", AdultRow(s, chair)["Room"]);
    }

    [Fact]
    public void AMemberCannotChairAndTheChairMustSit()
    {
        using var box = new Sandbox();
        var (s, scout, chair, m1, m2) = SeatableEvening(box);
        Assert.Contains("not qualified", s.SeatBoard("ROOM:101", scout, m1, $"{chair},{m1},{m2}").Message, StringComparison.Ordinal);
        s.RegisterAdult(Seed.Adult("Dunn", "Dee", "2004", "Member", "Member"));
        Assert.Contains("not one of the board members",
            s.SeatBoard("ROOM:101", scout, chair, $"{m1},{m2},{Seed.AdultId("Dunn", "Dee", "2004")}").Message, StringComparison.Ordinal);
    }

    [Fact]
    public void RoomsCannotBeDuplicatedOrRemovedWhileInUse()
    {
        using var box = new Sandbox();
        var (s, scout, chair, m1, m2) = SeatableEvening(box);
        Assert.False(s.AddRoom("101", BoardTypes.Final).Ok);
        Assert.False(s.AddRoom("  ", BoardTypes.Final).Ok);
        Assert.True(s.SeatBoard("ROOM:101", scout, chair, $"{chair},{m1},{m2}").Ok);
        Assert.False(s.RemoveRoom("ROOM:101").Ok);
    }

    [Fact]
    public void ChangedIsRaisedForWhatChanged()
    {
        using var box = new Sandbox();
        var s = box.Open();
        var seen = new List<DataTable>();
        s.Changed += (_, e) => seen.AddRange(e.Tables);
        s.RegisterScout(Seed.Scout("Aldridge", "Alex", "1001", "Final"));
        Assert.Equal([DataTable.Scouts], seen);
    }

    [Fact]
    public void SignUpGeniusAdultsGetTheirRealId()
    {
        using var box = new Sandbox();
        var history = new DataRecordFile<AdultRecord>(box.HistoryPath, AdultRecord.Factory);
        var scheduled = new DataRecordFile<ScoutRecord>(Path.Combine(box.Root, "sched.csv"), ScoutRecord.Factory);
        var month = DataRecord.Clock().ToString("yyyy-MM", System.Globalization.CultureInfo.InvariantCulture);
        var sug = new SignUpGenius(new HttpClient(), "unused", _ => { }, _ => { });

        var summary = sug.Merge(
        [
            new SignUpEntry(month + "-15", "ann", "able", "Adult volunteer", "ann@example.org", ["Troop 2001", "5551234567"]),
            new SignUpEntry(month + "-15", "bob", "baker", "Adult volunteer", "bob@example.org", ["Crew 2002", "(555) 765-4321"]),
            new SignUpEntry(month + "-15", "alex", "aldridge", "Eagle board of review", "alex@example.org", ["Troop 1001", "555.111.2222", "sam smith"]),
            new SignUpEntry("1999-01-01", "old", "entry", "Adult", "old@example.org", []),
        ], history, scheduled);

        Assert.Equal((2, 1), (summary.AdultsAdded, summary.ScoutsAdded));
        Assert.Equal(["ADULT:Able:Ann:2001", "ADULT:Baker:Bob:2002"], history.Records.Select(r => r.Id));
        Assert.Equal("555-765-4321", history.Records[1].Phone);
        Assert.Equal("Crew", history.Records[1].UnitType);
        var scout = Assert.Single(scheduled.Records);
        Assert.Equal(("SCOUT:Aldridge:Alex:1001", BoardTypes.Final, "Sam Smith"), (scout.Id, scout.BoardType, scout.Leader));
    }
}
