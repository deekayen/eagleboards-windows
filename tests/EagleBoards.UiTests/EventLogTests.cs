using System.Diagnostics;
using System.IO;
using System.Net;
using EagleBoards.App;
using EagleBoards.Web;

namespace EagleBoards.UiTests;

/// <summary>
/// What the event's log says while the scheduler runs, for diagnosing a
/// problem after the event (docs/decisions/0004). Counts, times, and
/// addresses, never a name.
/// </summary>
public class EventLogTests
{
    [Fact]
    public void ADoorSignInIsInTheLogWithoutTheName()
    {
        OffScreen.Run(() =>
        {
            using var e = new TestEvent();
            _ = e.Window;
            e.AdultAtTheDoor("Quillfeather", "Odessa", "4100", "Member");
            OffScreen.Pump();

            // The session holds the log open for writing while it runs.
            using var file = new FileStream(Path.Combine(e.Plan.DataDirectory, "eagleboards.log"), FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            var log = new StreamReader(file).ReadToEnd();
            Assert.Matches(@"POST /register-adult from 127\.0\.0\.1: 200, \d+ ms", log);
            Assert.Contains("adult signed in, new to the scheduler; 1 adult at this event", log, StringComparison.Ordinal);
            Assert.Contains("network addresses: ", log, StringComparison.Ordinal);
            Assert.DoesNotContain("Quillfeather", log, StringComparison.Ordinal);
        });
    }

    [Fact]
    public void AChangeShownLateIsLoggedAndOneShownAtOnceIsNot()
    {
        var lines = new List<string>();
        var responsiveness = new ResponsivenessLog(lines.Add);
        var made = Stopwatch.GetTimestamp();

        responsiveness.ChangeMade(made);
        responsiveness.ChangesShown(responsiveness.TakeUnshown(), made + Stopwatch.Frequency / 10);
        Assert.Empty(lines);

        // The oldest change not yet shown counts: a second one doesn't restart the clock.
        responsiveness.ChangeMade(made);
        responsiveness.ChangeMade(made + Stopwatch.Frequency * 60);
        var since = responsiveness.TakeUnshown();

        // A change made while the window reads is measured on the next round.
        responsiveness.ChangeMade(made + Stopwatch.Frequency * 70);
        responsiveness.ChangesShown(since, made + Stopwatch.Frequency * 75);
        Assert.Equal(["the window took 75.0 s to show a change"], lines);
        responsiveness.ChangesShown(responsiveness.TakeUnshown(), made + Stopwatch.Frequency * 72);
        Assert.Equal("the window took 2.0 s to show a change", lines[^1]);

        // Nothing waiting, nothing to say.
        responsiveness.ChangesShown(responsiveness.TakeUnshown(), made + Stopwatch.Frequency * 90);
        Assert.Equal(2, lines.Count);
    }

    [Fact]
    public void ASlowRefreshAndALateMinuteAreLogged()
    {
        var lines = new List<string>();
        var responsiveness = new ResponsivenessLog(lines.Add);
        responsiveness.RefreshTook(TimeSpan.FromMilliseconds(200));
        responsiveness.MinuteTicked(TimeSpan.FromSeconds(2));
        Assert.Empty(lines);

        responsiveness.RefreshTook(TimeSpan.FromSeconds(4.25));
        responsiveness.MinuteTicked(TimeSpan.FromSeconds(47));
        Assert.Equal(
        [
            "the window's refresh, with the board proposal, took 4.3 s",
            "the window was held up for 47.0 s (its minute update ran late)",
        ], lines);
    }

    [Fact]
    public void TheEventsSizeIsLoggedEveryFiveMinutes()
    {
        var lines = new List<string>();
        var responsiveness = new ResponsivenessLog(lines.Add);
        var start = new DateTime(2026, 11, 17, 19, 0, 0);
        for (var minute = 0; minute <= 10; minute++)
        {
            // The minute timer runs a few milliseconds early or late.
            var jitter = TimeSpan.FromMilliseconds(minute % 2 == 0 ? -40 : 40);
            responsiveness.Summary(start.AddMinutes(minute) + jitter, () => $"at minute {minute}");
        }

        Assert.Equal(["event: at minute 0", "event: at minute 5", "event: at minute 10"], lines);
    }

    [Fact]
    public void TheNetworkLineSaysWhenTheWebsitesAddressIsGone()
    {
        var wifi = new LanAddress(IPAddress.Parse("10.201.161.203"), "Wi-Fi", HasGateway: true, LooksVirtual: false);
        var wired = new LanAddress(IPAddress.Parse("192.168.1.153"), "Ethernet", HasGateway: true, LooksVirtual: false);

        Assert.Equal("network addresses: 10.201.161.203 (Wi-Fi), 192.168.1.153 (Ethernet); the check-in website is on 192.168.1.153",
            ComputerWatch.DescribeAddresses([wifi, wired], wired.Address));
        Assert.Equal("network addresses: 10.201.161.203 (Wi-Fi); the check-in website is on 192.168.1.153, which this computer no longer has, so stations can't reach it",
            ComputerWatch.DescribeAddresses([wifi], wired.Address));
        Assert.Equal("network addresses: none; the check-in website answers on all of them",
            ComputerWatch.DescribeAddresses([], null));
        Assert.Equal("network addresses: none; the check-in website is on 127.0.0.1, this computer only",
            ComputerWatch.DescribeAddresses([], IPAddress.Loopback));
    }
}
