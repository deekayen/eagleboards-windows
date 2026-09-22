using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;

namespace EagleBoards.Web;

/// <summary>An IPv4 address on this machine that check-in stations might reach.</summary>
public sealed record LanAddress(IPAddress Address, string InterfaceName, bool HasGateway, bool LooksVirtual)
{
    public override string ToString() => $"{Address} ({InterfaceName})";
}

public static class LanAddresses
{
    private static readonly string[] VirtualHints = ["virtual", "hyper-v", "vmware", "virtualbox", "wsl", "vethernet", "docker", "bluetooth", "loopback", "tap-", "tunnel"];

    /// <summary>
    /// Every up, non-loopback IPv4 address, most likely venue network first:
    /// one with a default gateway on a physical adapter. Hyper-V, WSL and VPN
    /// adapters are listed last rather than hidden, in case one is the right
    /// answer on the night.
    /// </summary>
    public static List<LanAddress> Find()
    {
        var found = new List<LanAddress>();
        NetworkInterface[] interfaces;
        try
        {
            interfaces = NetworkInterface.GetAllNetworkInterfaces();
        }
        catch (NetworkInformationException)
        {
            return found;
        }

        foreach (var nic in interfaces)
        {
            if (nic.OperationalStatus != OperationalStatus.Up || nic.NetworkInterfaceType == NetworkInterfaceType.Loopback)
            {
                continue;
            }

            IPInterfaceProperties props;
            try
            {
                props = nic.GetIPProperties();
            }
            catch (NetworkInformationException)
            {
                continue;
            }

            var hasGateway = props.GatewayAddresses.Any(g => g.Address.AddressFamily == AddressFamily.InterNetwork
                && !g.Address.Equals(IPAddress.Any));
            var label = nic.Name + " " + nic.Description;
            var looksVirtual = VirtualHints.Any(h => label.Contains(h, StringComparison.OrdinalIgnoreCase));
            foreach (var unicast in props.UnicastAddresses)
            {
                var address = unicast.Address;
                if (address.AddressFamily == AddressFamily.InterNetwork && !IPAddress.IsLoopback(address)
                    && !address.ToString().StartsWith("169.254.", StringComparison.Ordinal))
                {
                    found.Add(new LanAddress(address, nic.Name, hasGateway, looksVirtual));
                }
            }
        }

        return found
            .OrderBy(a => a.LooksVirtual)
            .ThenByDescending(a => a.HasGateway)
            .ToList();
    }

    /// <summary>
    /// The address to serve on for a <c>-bind</c> prefix such as "192.168.",
    /// or null when nothing matches.
    /// </summary>
    public static LanAddress? MatchPrefix(string prefix)
    {
        // "-bind 127.0.0.1" keeps a test or practice run off the network
        // entirely (and clear of the Windows Firewall prompt).
        if ("127.0.0.1".StartsWith(prefix, StringComparison.Ordinal) && prefix.Length >= 4)
        {
            return new LanAddress(IPAddress.Loopback, "loopback", HasGateway: false, LooksVirtual: false);
        }

        return Find().FirstOrDefault(a => a.Address.ToString().StartsWith(prefix, StringComparison.Ordinal));
    }
}
