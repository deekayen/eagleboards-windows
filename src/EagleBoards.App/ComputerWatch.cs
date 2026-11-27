using System.Net;
using System.Net.NetworkInformation;
using EagleBoards.Web;
using Microsoft.Win32;

namespace EagleBoards.App;

/// <summary>
/// Changes to the computer that can cut the check-in stations off from the
/// website, written to the event's log: its network addresses changing, and
/// sleep and waking up.
/// </summary>
/// <remarks>
/// The website answers on one address, chosen at start. The 2026-11-17 log
/// showed the scheduler restarted on another network after 72 seconds, and
/// nothing about whether the laptop's addresses changed after that, so it
/// couldn't say whether a station had been able to reach it
/// (docs/decisions/0004). Started by the app only: the tests and the
/// snapshot tool never start <see cref="App"/>.
/// </remarks>
internal sealed class ComputerWatch : IDisposable
{
    private readonly Action<string> _log;
    private readonly IPAddress? _bound;
    private readonly Lock _lock = new();
    private string _last;

    public ComputerWatch(Action<string> log, IPAddress? bound)
    {
        _log = log;
        _bound = bound;
        _last = DescribeAddresses(LanAddresses.Find(), bound);
        NetworkChange.NetworkAddressChanged += OnAddressChanged;
        SystemEvents.PowerModeChanged += OnPowerModeChanged;
    }

    /// <summary>
    /// This computer's addresses and where the check-in website answers, in
    /// one line, saying so when the website's address is no longer one of
    /// them. Interface names and addresses only: nothing about a person.
    /// </summary>
    internal static string DescribeAddresses(IReadOnlyList<LanAddress> addresses, IPAddress? bound)
    {
        var list = addresses.Count == 0
            ? "none"
            : string.Join(", ", addresses.Select(a => $"{a.Address} ({a.InterfaceName})"));
        var website = bound switch
        {
            null => "the check-in website answers on all of them",
            _ when IPAddress.IsLoopback(bound) => $"the check-in website is on {bound}, this computer only",
            _ when addresses.Any(a => a.Address.Equals(bound)) => $"the check-in website is on {bound}",

            // The adapter went down or took another address: stations get no
            // answer at all until the scheduler is started on one it has.
            _ => $"the check-in website is on {bound}, which this computer no longer has, so stations can't reach it",
        };
        return $"network addresses: {list}; {website}";
    }

    public void Dispose()
    {
        NetworkChange.NetworkAddressChanged -= OnAddressChanged;
        SystemEvents.PowerModeChanged -= OnPowerModeChanged;
    }

    private void OnAddressChanged(object? sender, EventArgs e)
    {
        // Windows raises this several times for one change, on thread-pool
        // threads that can overlap. Reading, comparing, and writing in one
        // lock keeps the lines in the order the addresses were read, so the
        // last line is the computer's last state.
        lock (_lock)
        {
            var now = DescribeAddresses(LanAddresses.Find(), _bound);

            // Only a different picture is worth a line.
            if (now == _last)
            {
                return;
            }

            _last = now;
            _log("changed: " + now);
        }
    }

    private void OnPowerModeChanged(object? sender, PowerModeChangedEventArgs e)
    {
        // The app keeps Windows awake, but closing the lid or the power
        // button still puts it to sleep, and the website with it.
        switch (e.Mode)
        {
            case PowerModes.Suspend:
                _log("the computer is going to sleep: stations can't sign anyone in until it wakes");
                break;
            case PowerModes.Resume:
                _log("the computer woke up");
                break;
        }
    }
}
