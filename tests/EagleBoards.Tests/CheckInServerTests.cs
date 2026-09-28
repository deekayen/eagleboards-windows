using System.Net;
using System.Text;
using EagleBoards.Core;
using EagleBoards.Core.Records;
using EagleBoards.Web;
using Microsoft.AspNetCore.Http;

namespace EagleBoards.Tests;

/// <summary>
/// What a check-in station on the network may and may not do. Requests are
/// fed straight to the dispatcher with a chosen remote address, so nothing
/// listens on a real interface (and no firewall prompt appears).
/// </summary>
[Collection(ClockCollection.Name)]
public class CheckInServerTests
{
    private static readonly IPAddress Station = IPAddress.Parse("192.168.1.50");

    private static async Task<(int Status, string Body)> Send(CheckInServer server, IPAddress from, string method, string pathAndQuery, string? form = null)
    {
        var context = new DefaultHttpContext();
        context.Connection.RemoteIpAddress = from;
        context.Request.Method = method;
        var q = pathAndQuery.IndexOf('?');
        context.Request.Path = q < 0 ? pathAndQuery : pathAndQuery[..q];
        context.Request.QueryString = q < 0 ? QueryString.Empty : new QueryString(pathAndQuery[q..]);
        if (form != null)
        {
            context.Request.ContentType = "application/x-www-form-urlencoded";
            context.Request.Body = new MemoryStream(Encoding.UTF8.GetBytes(form));
        }

        var body = new MemoryStream();
        context.Response.Body = body;
        await server.DispatchAsync(context);
        return (context.Response.StatusCode, Encoding.UTF8.GetString(body.ToArray()));
    }

    private static CheckInServer Server(Sandbox box) => new(box.Open(), new CheckInServerOptions());

    [Fact]
    public async Task AStationGetsTheSignInPages()
    {
        using var box = new Sandbox();
        var server = Server(box);
        Assert.Contains("Please sign in", (await Send(server, Station, "GET", "/")).Body, StringComparison.Ordinal);
        Assert.Contains("register-youth", (await Send(server, Station, "GET", "/youth_register")).Body, StringComparison.Ordinal);
        Assert.Contains("register-adult", (await Send(server, Station, "GET", "/adult_register")).Body, StringComparison.Ordinal);
        Assert.Equal(200, (await Send(server, Station, "GET", "/eb-data.js")).Status);
        Assert.Contains("ebSignInForm", (await Send(server, Station, "GET", "/checkin.js")).Body, StringComparison.Ordinal);
        Assert.Equal(200, (await Send(server, Station, "GET", "/checkin.css")).Status);
    }

    [Fact]
    public async Task TheSharedPagesLookUpAnEmailWithoutABirthdatePhoneOrEmail()
    {
        // SPEC.md D-18, D-7 and D-8: the lookup answers with the fields its
        // form fills in, matched trimmed and in any case, and nothing more.
        using var box = new Sandbox();
        var svc = box.Open();
        svc.SaveRow(DataTable.ScoutsScheduled, "inserted", "SCOUT:Aldridge:Alex:1001", new Dictionary<string, string>
        {
            ["Last"] = "Aldridge", ["First"] = "Alex", ["Email"] = "alex@example.org", ["DOB"] = "2010-04-01",
            ["Phone"] = "555-123-4567",
        });
        var server = new CheckInServer(svc, new CheckInServerOptions());

        var (status, body) = await Send(server, Station, "POST", "/api/youth-lookup", "email=%20ALEX%40example.org%20");
        Assert.Equal(200, status);
        Assert.Contains("\"First\":\"Alex\"", body, StringComparison.Ordinal);
        Assert.DoesNotContain("2010-04-01", body, StringComparison.Ordinal);
        Assert.DoesNotContain("Phone", body, StringComparison.Ordinal);
        Assert.DoesNotContain("555-123-4567", body, StringComparison.Ordinal);
        Assert.DoesNotContain("example.org", body, StringComparison.Ordinal);
        Assert.Equal("{}", (await Send(server, Station, "POST", "/api/youth-lookup", "email=NONE")).Body);
    }

    [Fact]
    public async Task AnAdultIsStillPreFilledWithTheirPhoneNumber()
    {
        // SPEC.md D-8 is about youth only.
        using var box = new Sandbox();
        var svc = box.Open();
        svc.RegisterAdult(new Dictionary<string, string>(Seed.Adult("Able", "Ann", "2001", "Member", "Chair")) { ["Phone"] = "555-765-4321" });
        var server = new CheckInServer(svc, new CheckInServerOptions());

        var (status, body) = await Send(server, Station, "POST", "/api/adult-lookup", "email=ann%40example.org");
        Assert.Equal(200, status);
        Assert.Contains("\"Phone\":\"555-765-4321\"", body, StringComparison.Ordinal);
        Assert.Contains("555-765-4321", (await Send(server, Station, "GET", "/adult-autofill?Email=ann@example.org&fmt=json")).Body, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("/scheduler")]
    [InlineData("/admin")]
    [InlineData("/configure")]
    public async Task TheOldBrowserAdminPagesAreGone(string path)
    {
        using var box = new Sandbox();
        Assert.Equal(404, (await Send(Server(box), IPAddress.Loopback, "GET", path)).Status);
    }

    [Fact]
    public async Task AStationCanSignIn()
    {
        using var box = new Sandbox();
        var server = Server(box);
        Assert.Equal(200, (await Send(server, Station, "POST", "/register-youth", "Last=Aldridge&First=Alex&UnitType=Troop&Unit=1001&BoardType=Final")).Status);
        Assert.Equal(200, (await Send(server, Station, "POST", "/register-adult", "Last=Able&First=Ann&UnitType=Troop&Unit=2001&FinalBoard=Chair&ProjectReview=Member")).Status);
        var list = await Send(server, Station, "GET", "/youth-cells?cols=RegTimeHM,Last,First,UnitType,Unit");
        Assert.Equal(200, list.Status);
        Assert.Contains("<cell>Aldridge</cell>", list.Body, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("POST", "/seat-board", "RoomID=ROOM:1&ScoutID=x&ChairID=y&MemberIDs=y")]
    [InlineData("POST", "/complete-board", "ScoutID=x&Result=Approved")]
    [InlineData("POST", "/reset-board", "ScoutID=x")]
    [InlineData("POST", "/restore-board", null)]
    [InlineData("POST", "/room-update", "!nativeeditor_status=inserted&gr_id=ROOM:9&Room=9")]
    [InlineData("POST", "/adult-update", "!nativeeditor_status=updated&gr_id=x&Room=N/A")]
    [InlineData("POST", "/update-config", "RefreshTimeSecs=1")]
    [InlineData("GET", "/adult-history-cells", null)]
    [InlineData("GET", "/room-cells", null)]
    public async Task AStationCannotRunTheEvent(string method, string path, string? form)
    {
        using var box = new Sandbox();
        Assert.Equal(403, (await Send(Server(box), Station, method, path, form)).Status);
    }

    [Theory]
    [InlineData("/youth-cells?cols=Last,Phone")]
    [InlineData("/youth-cells?cols=Last,First&data=DOB")]
    [InlineData("/youth-cells?cols=Last,First&fmt=csv")]
    [InlineData("/youth-cells")]
    [InlineData("/adult-cells?cols=Last,Email")]
    public async Task AStationSeesNamesAndUnitsNotContactDetails(string pathAndQuery)
    {
        using var box = new Sandbox();
        Assert.Equal(403, (await Send(Server(box), Station, "GET", pathAndQuery)).Status);
    }

    [Fact]
    public async Task TheAdminComputerKeepsTheWholeContract()
    {
        using var box = new Sandbox();
        var server = Server(box);
        Assert.Equal(200, (await Send(server, IPAddress.Loopback, "GET", "/youth-cells?cols=Last,Phone&fmt=csv")).Status);
        Assert.Equal(200, (await Send(server, IPAddress.IPv6Loopback, "GET", "/room-cells")).Status);
        Assert.Equal(200, (await Send(server, IPAddress.Parse("::ffff:127.0.0.1"), "GET", "/room-cells")).Status);
    }

    [Fact]
    public async Task ARefusedBoardActionIs409WithTheReason()
    {
        using var box = new Sandbox();
        var (status, body) = await Send(Server(box), IPAddress.Loopback, "POST", "/inprogress-board", "ScoutID=nobody");
        Assert.Equal(409, status);
        Assert.StartsWith("ERROR: Invalid Scout ID", body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ReturningYouthArePreFilledWithoutABirthdateOrPhoneNumber()
    {
        // SPEC.md D-7, D-8 and O-5: one already on file stays there, but the
        // sign-in page is never sent it.
        using var box = new Sandbox();
        var svc = box.Open();
        svc.SaveRow(DataTable.ScoutsScheduled, "inserted", "SCOUT:Aldridge:Alex:1001", new Dictionary<string, string>
        {
            ["Last"] = "Aldridge", ["First"] = "Alex", ["Email"] = "alex@example.org", ["DOB"] = "2010-04-01",
            ["Phone"] = "555-123-4567",
        });
        var server = new CheckInServer(svc, new CheckInServerOptions());

        foreach (var query in new[] { "/youth-autofill?Email=alex@example.org", "/youth-autofill?Email=alex@example.org&fmt=json" })
        {
            var (status, body) = await Send(server, Station, "GET", query);
            Assert.Equal(200, status);
            Assert.Contains("Aldridge", body, StringComparison.Ordinal);
            Assert.DoesNotContain("DOB", body, StringComparison.Ordinal);
            Assert.DoesNotContain("2010-04-01", body, StringComparison.Ordinal);
            Assert.DoesNotContain("Phone", body, StringComparison.Ordinal);
            Assert.DoesNotContain("555-123-4567", body, StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task AYouthPhoneNumberIsNeitherKeptAtSignInNorServed()
    {
        // SPEC.md D-8 and D-2: a page cached from before D-8 still sends
        // Phone, and it is thrown away. One already on file stays there, but
        // no youth table sends it, even to the admin computer; an adult's
        // still goes there.
        using var box = new Sandbox();
        var svc = box.Open();
        var server = new CheckInServer(svc, new CheckInServerOptions());
        Assert.Equal(200, (await Send(server, Station, "POST", "/register-youth",
            "Last=Aldridge&First=Alex&UnitType=Troop&Unit=1001&BoardType=Final&Phone=5551234567")).Status);
        Assert.Equal("", svc.Snapshot(DataTable.Scouts).Single()["Phone"]);

        svc.SaveRow(DataTable.Scouts, "updated", "SCOUT:Aldridge:Alex:1001", new Dictionary<string, string> { ["Phone"] = "555-123-4567" });
        svc.SaveRow(DataTable.ScoutsScheduled, "inserted", "SCOUT:Bram:Beau:1002", new Dictionary<string, string>
        {
            ["Last"] = "Bram", ["First"] = "Beau", ["Phone"] = "555-222-3333",
        });
        foreach (var query in new[]
        {
            "/youth-cells?cols=Last,Phone", "/youth-cells?cols=Last&data=Phone", "/youth-cells?cols=Last,Phone&fmt=data",
            "/youth-cells?cols=Last,Phone&fmt=csv&filename=Report.csv", "/youth-cells",
            "/youth-scheduled-cells?cols=Last,Phone", "/youth-scheduled-cells?fmt=csv",
        })
        {
            var (status, body) = await Send(server, IPAddress.Loopback, "GET", query);
            Assert.Equal(200, status);
            Assert.DoesNotContain("555-123-4567", body, StringComparison.Ordinal);
            Assert.DoesNotContain("555-222-3333", body, StringComparison.Ordinal);
        }

        Assert.Equal("555-123-4567", box.Open().Snapshot(DataTable.Scouts).Single()["Phone"]);
        var csv = (await Send(server, IPAddress.Loopback, "GET", "/youth-cells?cols=Last,Phone,First&fmt=csv")).Body;
        Assert.Contains("Last,Phone,First\nAldridge,,Alex\n", csv, StringComparison.Ordinal);

        await Send(server, Station, "POST", "/register-adult",
            "Last=Able&First=Ann&UnitType=Troop&Unit=2001&FinalBoard=Chair&ProjectReview=Member&Phone=555-765-4321");
        Assert.Contains("<cell>555-765-4321</cell>",
            (await Send(server, IPAddress.Loopback, "GET", "/adult-cells?cols=Last,Phone")).Body, StringComparison.Ordinal);
        Assert.Contains("<cell>555-765-4321</cell>",
            (await Send(server, IPAddress.Loopback, "GET", "/adult-history-cells?cols=Last,Phone")).Body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AFilterOnAWithheldColumnMatchesNothing()
    {
        // SPEC.md D-7 and D-8: the rows a filter kept would say whose birthdate
        // or number it is, so a filter on a youth's DOB or Phone keeps none.
        // One on a column that is served, or on an adult's Phone, still works.
        using var box = new Sandbox();
        var svc = box.Open();
        svc.RegisterScout(Seed.Scout("Aldridge", "Alex", "1001", "Final"));
        svc.SaveRow(DataTable.Scouts, "updated", "SCOUT:Aldridge:Alex:1001", new Dictionary<string, string>
        {
            ["Phone"] = "555-123-4567", ["DOB"] = "2010-04-01",
        });
        svc.SaveRow(DataTable.ScoutsScheduled, "inserted", "SCOUT:Bram:Beau:1002", new Dictionary<string, string>
        {
            ["Last"] = "Bram", ["First"] = "Beau", ["Phone"] = "555-222-3333", ["DOB"] = "2011-05-02",
        });
        svc.RegisterAdult(new Dictionary<string, string>(Seed.Adult("Able", "Ann", "2001", "Member", "Chair")) { ["Phone"] = "555-765-4321" });
        var server = new CheckInServer(svc, new CheckInServerOptions());

        foreach (var query in new[]
        {
            "/youth-cells?cols=Last&filter=Phone~555-123-4567", "/youth-cells?cols=Last&filter=DOB~2010-04-01",
            "/youth-cells?cols=Last&filter=Phone~555-123-4567&fmt=csv", "/youth-cells?cols=Last&filter=Phone~555-123-4567&fmt=data",
            "/youth-scheduled-cells?cols=Last&filter=Phone~555-222-3333", "/youth-scheduled-cells?cols=Last&filter=DOB~2011-05-02",
        })
        {
            var (status, body) = await Send(server, IPAddress.Loopback, "GET", query);
            Assert.Equal(200, status);
            Assert.DoesNotContain("Aldridge", body, StringComparison.Ordinal);
            Assert.DoesNotContain("Bram", body, StringComparison.Ordinal);
        }

        Assert.Contains("<cell>Aldridge</cell>",
            (await Send(server, IPAddress.Loopback, "GET", "/youth-cells?cols=Last&filter=Last~Aldridge")).Body, StringComparison.Ordinal);
        Assert.Contains("<cell>Able</cell>",
            (await Send(server, IPAddress.Loopback, "GET", "/adult-cells?cols=Last&filter=Phone~555-765-4321")).Body, StringComparison.Ordinal);
        Assert.Contains("<cell>Able</cell>",
            (await Send(server, IPAddress.Loopback, "GET", "/adult-history-cells?cols=Last&filter=Phone~555-765-4321")).Body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AStationListsTheScoutsAnAdultMaySupportByNameAndUnitOnly()
    {
        using var box = new Sandbox();
        var server = Server(box);
        await Send(server, Station, "POST", "/register-youth",
            "Last=Aldridge&First=Alex&UnitType=Troop&Unit=1001&BoardType=Final&Email=alex@example.org&Phone=5551234567");
        var (status, body) = await Send(server, Station, "GET", "/scout-choices");
        Assert.Equal(200, status);
        Assert.Contains("<row id=\"SCOUT:Aldridge:Alex:1001\"><cell>Alex</cell><cell>Aldridge</cell><cell>Troop1001</cell></row>",
            body, StringComparison.Ordinal);
        Assert.DoesNotContain("example.org", body, StringComparison.Ordinal);
        Assert.DoesNotContain("5551234567", body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ApprovedProposalsAreServedWithoutABirthdatePhoneOrEmailWhateverIsAsked()
    {
        // SPEC.md D-22: the Approved proposals columns only, on this machine only.
        using var box = new Sandbox();
        var server = Server(box);
        var earlier = new Core.Storage.DataRecordFile<ScoutRecord>(Path.Combine(box.Root, "2020-01-14", "scouts.csv"), ScoutRecord.Factory);
        earlier.Add(new ScoutRecord(new Dictionary<string, string>
        {
            ["ID"] = "SCOUT:Ashby:Ava:3101", ["Last"] = "Ashby", ["First"] = "Ava", ["UnitType"] = "Troop", ["Unit"] = "3101",
            ["Email"] = "ava@example.org", ["Phone"] = "5551234567", ["DOB"] = "2010-04-01",
            ["BoardType"] = BoardTypes.Project, ["Result"] = BoardResults.Approved, ["BoardChair"] = "Chair Person", ["Notes"] = "Trail steps",
        }), false);
        earlier.Store();

        var (status, body) = await Send(server, IPAddress.Loopback, "GET", "/approved-proposals-cells?cols=Event,Last,First,Unit,BoardChair,Notes,DOB,Phone,Email");
        Assert.Equal(200, status);
        Assert.Contains("<rows read=\"1\" from=\"2020-01-14\" to=\"2020-01-14\" unreadable=\"\">", body, StringComparison.Ordinal);
        Assert.Contains("<row id=\"2020-01-14|SCOUT:Ashby:Ava:3101\"><cell>2020-01-14</cell><cell>Ashby</cell><cell>Ava</cell><cell>3101</cell><cell>Chair Person</cell><cell>Trail steps</cell><cell></cell><cell></cell><cell></cell></row>",
            body, StringComparison.Ordinal);
        Assert.DoesNotContain("example.org", body, StringComparison.Ordinal);
        Assert.Equal(403, (await Send(server, Station, "GET", "/approved-proposals-cells")).Status);
    }
}
