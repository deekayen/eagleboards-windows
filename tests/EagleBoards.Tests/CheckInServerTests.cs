using System.Net;
using System.Text;
using EagleBoards.Core;
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
    public async Task TheSharedPagesLookUpAnEmailWithoutABirthdateOrAnEmail()
    {
        // SPEC.md D-18 and D-7: the lookup answers with the fields its form
        // fills in, matched trimmed and in any case, and nothing more.
        using var box = new Sandbox();
        var svc = box.Open();
        svc.SaveRow(DataTable.ScoutsScheduled, "inserted", "SCOUT:Aldridge:Alex:1001", new Dictionary<string, string>
        {
            ["Last"] = "Aldridge", ["First"] = "Alex", ["Email"] = "alex@example.org", ["DOB"] = "2010-04-01",
        });
        var server = new CheckInServer(svc, new CheckInServerOptions());

        var (status, body) = await Send(server, Station, "POST", "/api/youth-lookup", "email=%20ALEX%40example.org%20");
        Assert.Equal(200, status);
        Assert.Contains("\"First\":\"Alex\"", body, StringComparison.Ordinal);
        Assert.DoesNotContain("2010-04-01", body, StringComparison.Ordinal);
        Assert.DoesNotContain("example.org", body, StringComparison.Ordinal);
        Assert.Equal("{}", (await Send(server, Station, "POST", "/api/youth-lookup", "email=NONE")).Body);
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
    public async Task AStationCannotRunTheEvening(string method, string path, string? form)
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
    public async Task ReturningYouthArePreFilledWithoutABirthdate()
    {
        // SPEC.md D-7 and O-5: one already on file stays there, but the
        // sign-in page is never sent it.
        using var box = new Sandbox();
        var svc = box.Open();
        svc.SaveRow(DataTable.ScoutsScheduled, "inserted", "SCOUT:Aldridge:Alex:1001", new Dictionary<string, string>
        {
            ["Last"] = "Aldridge", ["First"] = "Alex", ["Email"] = "alex@example.org", ["DOB"] = "2010-04-01",
        });
        var server = new CheckInServer(svc, new CheckInServerOptions());

        foreach (var query in new[] { "/youth-autofill?Email=alex@example.org", "/youth-autofill?Email=alex@example.org&fmt=json" })
        {
            var (status, body) = await Send(server, Station, "GET", query);
            Assert.Equal(200, status);
            Assert.Contains("Aldridge", body, StringComparison.Ordinal);
            Assert.DoesNotContain("DOB", body, StringComparison.Ordinal);
            Assert.DoesNotContain("2010-04-01", body, StringComparison.Ordinal);
        }
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
}
