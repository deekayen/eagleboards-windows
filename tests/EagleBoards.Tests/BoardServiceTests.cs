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

    private static (BoardService Service, string Scout, string Chair, string M1, string M2) UnderReview(Sandbox box)
    {
        var (s, scout, chair, m1, m2) = SeatableEvening(box);
        Assert.True(s.SeatBoard("ROOM:101", scout, chair, $"{chair},{m1},{m2}").Ok);
        Assert.True(s.StartReview(scout).Ok);
        return (s, scout, chair, m1, m2);
    }

    [Theory]
    [InlineData("Maybe")]
    [InlineData("Approvd")]
    [InlineData("approved")]
    [InlineData("")]
    [InlineData("Postponed")] // a scout sent away before any board; never a board's decision
    public void OnlyTheBoardsThreeDecisionsAreResults(string result)
    {
        using var box = new Sandbox();
        var (s, scout, chair, _, _) = UnderReview(box);
        Assert.False(s.CompleteBoard(scout, result, "").Ok);
        Assert.Equal(BoardStatus.InProgress, Status(s, scout));
        Assert.Equal("101", AdultRow(s, chair)["Room"]);
    }

    [Theory]
    [InlineData(BoardResults.Approved)]
    [InlineData(BoardResults.Adjourned)]
    [InlineData(BoardResults.NotApproved)]
    public void EachOfTheBoardsDecisionsIsRecorded(string result)
    {
        using var box = new Sandbox();
        var (s, scout, _, _, _) = UnderReview(box);
        Assert.True(s.CompleteBoard(scout, result, "").Ok);
        Assert.Equal(result, s.Snapshot(DataTable.Scouts).Single(r => r["ID"] == scout)["Result"]);
    }

    [Fact]
    public void ABoardWhoseRoomWasRenamedStillCompletesAndFreesTheRoom()
    {
        using var box = new Sandbox();
        var (s, scout, chair, m1, m2) = UnderReview(box);
        s.SaveRow(DataTable.Rooms, "updated", "ROOM:101", new Dictionary<string, string> { ["Room"] = "101 Annex" });
        Assert.True(s.CompleteBoard(scout, BoardResults.Approved, "").Ok);
        Assert.All(new[] { chair, m1, m2 }, id => Assert.Equal("", AdultRow(s, id)["Room"]));
        Assert.Equal("", s.Snapshot(DataTable.Rooms).Single(r => r["ID"] == "ROOM:101")["Scout"]);
    }

    [Fact]
    public void ABoardWhoseRoomWasDeletedStillCompletes()
    {
        using var box = new Sandbox();
        var (s, scout, chair, m1, m2) = UnderReview(box);
        s.SaveRow(DataTable.Rooms, "deleted", "ROOM:101", new Dictionary<string, string>());
        Assert.True(s.CompleteBoard(scout, BoardResults.Approved, "").Ok);
        Assert.All(new[] { chair, m1, m2 }, id => Assert.Equal("", AdultRow(s, id)["Room"]));
    }

    [Fact]
    public void ResettingABoardWhoseRoomWasDeletedFreesItsAdults()
    {
        using var box = new Sandbox();
        var (s, scout, chair, m1, m2) = UnderReview(box);
        s.SaveRow(DataTable.Rooms, "deleted", "ROOM:101", new Dictionary<string, string>());
        Assert.True(s.ResetBoard(scout).Ok);
        Assert.All(new[] { chair, m1, m2 }, id => Assert.Equal("", AdultRow(s, id)["Room"]));
    }

    [Fact]
    public void AResultOnTheWrongScoutCanBeUndoneOnTheAdminWindow()
    {
        using var box = new Sandbox();
        var (s, scout, chair, m1, m2) = UnderReview(box);
        Assert.True(s.CompleteBoard(scout, BoardResults.Approved, "").Ok);

        // What the Admin window writes: one field per edit.
        foreach (var (field, value) in new[] { ("Status", BoardStatus.Registered), ("Result", ""), ("BoardChair", ""), ("BoardMembers", "") })
        {
            Assert.Equal("updated", s.SaveRow(DataTable.Scouts, "updated", scout, new Dictionary<string, string> { [field] = value }));
        }

        // Complete left Room "N/A"; that must not keep them from their real board.
        Assert.True(s.SeatBoard("ROOM:101", scout, chair, $"{chair},{m1},{m2}").Ok);
        Assert.Equal(BoardStatus.Seated, Status(s, scout));
    }

    [Fact]
    public void AnAdultWithACommaInTheirNameCanBeSeated()
    {
        using var box = new Sandbox();
        var (s, scout, chair, m1, _) = SeatableEvening(box);
        s.RegisterAdult(Seed.Adult("Whitmore, Jr.", "Lysander", "2031", "Member", "Member"));
        var junior = "ADULT:Whitmore~ Jr.:Lysander:2031";
        Assert.Equal("Lysander", AdultRow(s, junior)["First"]);
        Assert.True(s.SeatBoard("ROOM:101", scout, chair, $"{chair},{m1},{junior}").Ok);
        Assert.Equal("101", AdultRow(s, junior)["Room"]);
    }

    /// <summary>A board of three in room 101 (chair A1, members A2 and A3), in review, and three free adults.</summary>
    private static (BoardService S, string Scout) BoardInReview(Sandbox box)
    {
        var s = box.Open();
        s.AddRoom("101", BoardTypes.Final);
        s.RegisterScout(Seed.Scout("Aldridge", "Alex", "1001", "Final"));
        foreach (var (last, final) in new[] { ("A1", "Chair"), ("A2", "Member"), ("A3", "Member"), ("B1", "Member"), ("B2", "Chair"), ("B3", "Unavailable") })
        {
            s.RegisterAdult(Seed.Adult(last, "X", "2" + last, "Member", final));
        }

        var scout = Seed.ScoutId("Aldridge", "Alex", "1001");
        string A(string last) => Seed.AdultId(last, "X", "2" + last);
        Assert.True(s.SeatBoard("ROOM:101", scout, A("A1"), $"{A("A1")},{A("A2")},{A("A3")}").Ok);
        Assert.True(s.StartReview(scout).Ok);
        return (s, scout);
    }

    private static string Id(string last) => Seed.AdultId(last, "X", "2" + last);

    [Fact]
    public void AMemberCanBeSwappedMidBoard()
    {
        using var box = new Sandbox();
        var (s, scout) = BoardInReview(box);

        var r = s.ChangeBoardMembers(scout, Id("A1"), $"{Id("A1")},{Id("B1")},{Id("A3")}");
        Assert.True(r.Ok, r.Message);

        Assert.Equal("", AdultRow(s, Id("A2"))["Room"]);
        Assert.Equal("101", AdultRow(s, Id("B1"))["Room"]);
        var row = s.Snapshot(DataTable.Scouts).Single(x => x["ID"] == scout);
        Assert.Equal(BoardStatus.InProgress, row["Status"]);
        Assert.Contains("X B1", row["BoardMembers"]);
        Assert.DoesNotContain("X A2", row["BoardMembers"]);
        Assert.Contains("X B1", s.Snapshot(DataTable.Rooms).Single()["Leaders"]);
    }

    [Fact]
    public void TheChairCanBeHandedToAnotherQualifiedMember()
    {
        using var box = new Sandbox();
        var (s, scout) = BoardInReview(box);
        Assert.True(s.ChangeBoardMembers(scout, Id("B2"), $"{Id("B2")},{Id("A2")},{Id("A3")}").Ok);
        Assert.Equal("X B2", s.Snapshot(DataTable.Scouts).Single(x => x["ID"] == scout)["BoardChair"]);
        Assert.Equal("", AdultRow(s, Id("A1"))["Room"]);
    }

    [Theory]
    [InlineData("B3", "Unavailable")]
    [InlineData("A2", "more than once")]
    public void ChangingMembersKeepsTheSeatingRules(string extra, string refusal)
    {
        using var box = new Sandbox();
        var (s, scout) = BoardInReview(box);
        var r = s.ChangeBoardMembers(scout, Id("A1"), $"{Id("A1")},{Id("A2")},{Id(extra)}");
        Assert.False(r.Ok);
        Assert.Contains(refusal, r.Message);
        Assert.Equal("101", AdultRow(s, Id("A2"))["Room"]);
    }

    [Fact]
    public void ChangingMembersRefusesAnUnqualifiedChairOrTooFew()
    {
        using var box = new Sandbox();
        var (s, scout) = BoardInReview(box);
        Assert.Contains("not qualified", s.ChangeBoardMembers(scout, Id("A2"), $"{Id("A1")},{Id("A2")},{Id("A3")}").Message);
        Assert.Contains("Only 2", s.ChangeBoardMembers(scout, Id("A1"), $"{Id("A1")},{Id("A2")}").Message);
    }

    [Fact]
    public void AMemberBusyOnAnotherBoardCantJoin()
    {
        using var box = new Sandbox();
        var (s, scout) = BoardInReview(box);
        s.AddRoom("102", BoardTypes.Final);
        s.RegisterAdult(Seed.Adult("C1", "X", "2C1", "Member", "Member"));
        s.RegisterScout(Seed.Scout("Bram", "Beau", "1002", "Final"));
        Assert.True(s.SeatBoard("ROOM:102", Seed.ScoutId("Bram", "Beau", "1002"), Id("B2"), $"{Id("B2")},{Id("B1")},{Id("C1")}").Ok);

        var r = s.ChangeBoardMembers(scout, Id("A1"), $"{Id("A1")},{Id("A2")},{Id("B1")}");
        Assert.False(r.Ok);
        Assert.Contains("room 102", r.Message);
        Assert.Equal("101", AdultRow(s, Id("A3"))["Room"]);
    }

    [Fact]
    public void OnlyASeatedOrRunningBoardCanBeChanged()
    {
        using var box = new Sandbox();
        var s = box.Open();
        s.RegisterScout(Seed.Scout("Aldridge", "Alex", "1001", "Final"));
        Assert.Contains("no board", s.ChangeBoardMembers(Seed.ScoutId("Aldridge", "Alex", "1001"), "x", "x").Message);
    }

    private static Dictionary<string, string> HistoryRow(BoardService s, string id) =>
        s.Snapshot(DataTable.AdultHistory).Single(r => r["ID"] == id);

    [Fact]
    public void WoodBadgeAndWhomTheyCameToSupportAreForTonightOnly()
    {
        using var box = new Sandbox();
        var s = box.Open();
        var form = Seed.Adult("Hargrove", "Ines", "3401", "Member", "Member");
        form["WoodBadge"] = "Y";
        form["Supporting"] = "SCOUT:Galloway:Tobias:3401|SCOUT:Other:Oli:3402";
        s.RegisterAdult(form);
        var id = Seed.AdultId("Hargrove", "Ines", "3401");
        Assert.Equal("Y", AdultRow(s, id)["WoodBadge"]);
        Assert.Equal("SCOUT:Galloway:Tobias:3401|SCOUT:Other:Oli:3402", AdultRow(s, id)["Supporting"]);
        Assert.Equal(("", ""), (HistoryRow(s, id)["WoodBadge"], HistoryRow(s, id)["Supporting"]));

        form["WoodBadge"] = "yes";   // anything but Y is no
        form["Supporting"] = "";
        s.RegisterAdult(form);
        Assert.Equal(("", ""), (AdultRow(s, id)["WoodBadge"], AdultRow(s, id)["Supporting"]));
    }

    [Fact]
    public void ScoutChoicesAreRsvpsAndWalkInsWhoseEveningIsNotOver()
    {
        using var box = new Sandbox();
        var s = box.Open();
        s.SaveRow(DataTable.ScoutsScheduled, "inserted", "SCOUT:Rsvp:Only:3999",
            new Dictionary<string, string> { ["Last"] = "Rsvp", ["First"] = "Only", ["UnitType"] = "Troop", ["Unit"] = "3999" });
        s.RegisterScout(Seed.Scout("Bram", "Beau", "1002", BoardTypes.Final));
        s.RegisterScout(Seed.Scout("Carrington", "Cormac", "1003", BoardTypes.Final));
        Assert.True(s.PostponeBoard(Seed.ScoutId("Carrington", "Cormac", "1003")).Ok);
        Assert.Equal(["SCOUT:Bram:Beau:1002", "SCOUT:Rsvp:Only:3999"], s.ScoutChoices().Select(c => c.Id));
    }

    [Fact]
    public void AnOperatorLinksAnAdultToAScoutAfterBothSignedIn()
    {
        using var box = new Sandbox();
        var (s, scout, _, m1, _) = SeatableEvening(box);
        Assert.True(s.SetSupporting(m1, scout, linked: true).Ok);
        Assert.Equal(scout, AdultRow(s, m1)["Supporting"]);
        Assert.True(s.SetSupporting(m1, scout, linked: true).Ok);   // pressing it twice links once
        Assert.Equal(scout, AdultRow(s, m1)["Supporting"]);
        Assert.True(s.SetSupporting(m1, scout, linked: false).Ok);
        Assert.Equal("", AdultRow(s, m1)["Supporting"]);

        Assert.False(s.SetSupporting("ADULT:Nobody:Here:0", scout, linked: true).Ok);
        Assert.False(s.SetSupporting(m1, "SCOUT:Nobody:Here:0", linked: true).Ok);
        Assert.True(s.SetSupporting(m1, "SCOUT:Nobody:Here:0", linked: false).Ok);   // clearing a stale link is fine
    }
}
