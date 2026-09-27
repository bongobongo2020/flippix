using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using FlipPix.Remote.Contracts;

namespace FlipPix.Mobile.Services;

/// <summary>A FlipPix desktop that answered on the Wi-Fi.</summary>
public sealed record FoundComputer(string Name, string Url)
{
    public string Address => Url.Replace("http://", "", StringComparison.Ordinal);
}

/// <summary>
/// Finds FlipPix desktops on the same network: one broadcast, and every desktop with the phone
/// remote on answers with its name and port. Sent to the limited broadcast address and to each
/// interface's own subnet broadcast, because some routers drop one or the other.
/// </summary>
public static class Discovery
{
    public static async Task<IReadOnlyList<FoundComputer>> FindAsync(TimeSpan listenFor, CancellationToken ct = default)
    {
        // A blocking receive with a socket timeout, on a worker thread. Cancelling an async UDP receive
        // never fired on Android (the search sat on "Looking" for good), and a hard deadline on top
        // means nothing here can hold the screen.
        var search = Task.Run(() => Listen(listenFor), ct);
        var deadline = Task.Delay(listenFor + TimeSpan.FromSeconds(2), ct);
        if (await Task.WhenAny(search, deadline) != search)
        {
            ct.ThrowIfCancellationRequested();
            return Array.Empty<FoundComputer>();
        }
        ct.ThrowIfCancellationRequested();
        return await search;
    }

    private static IReadOnlyList<FoundComputer> Listen(TimeSpan listenFor)
    {
        var found = new Dictionary<string, FoundComputer>();
        using var udp = new UdpClient(AddressFamily.InterNetwork) { EnableBroadcast = true };
        udp.Client.Bind(new IPEndPoint(IPAddress.Any, 0));
        var probe = Encoding.ASCII.GetBytes(RemoteApi.DiscoveryProbe);
        foreach (var target in Targets())
        {
            try { udp.Send(probe, probe.Length, new IPEndPoint(target, RemoteApi.DiscoveryPort)); }
            catch (SocketException) { /* that network doesn't allow broadcast; the others may */ }
        }

        var until = DateTime.UtcNow + listenFor;
        while (true)
        {
            var left = until - DateTime.UtcNow;
            if (left <= TimeSpan.Zero) break;
            udp.Client.ReceiveTimeout = Math.Max(1, (int)left.TotalMilliseconds);
            try
            {
                var from = new IPEndPoint(IPAddress.Any, 0);
                var buffer = udp.Receive(ref from);
                var hello = JsonSerializer.Deserialize<HelloDto>(buffer, RemoteClient.Json);
                if (hello?.App != RemoteApi.AppName || hello.Port <= 0) continue;
                var url = $"http://{from.Address}:{hello.Port}";
                found[url] = new FoundComputer(string.IsNullOrWhiteSpace(hello.Name) ? from.Address.ToString() : hello.Name, url);
            }
            catch (SocketException ex) when (ex.SocketErrorCode == SocketError.TimedOut) { break; }
            catch (SocketException) { /* an ICMP echo of a probe, say; keep listening */ }
            catch (JsonException) { /* a stray datagram */ }
        }

        // A computer with several network adapters (Wi-Fi plus Hyper-V, WSL or a VPN) can answer from
        // more than one address. Keep one per computer: the one on this phone's own network.
        var local = LocalNetworks();
        return found.Values
            .GroupBy(f => f.Name, StringComparer.OrdinalIgnoreCase)
            .Select(g => g.OrderByDescending(f => OnLocalNetwork(f.Url, local)).First())
            .OrderBy(f => f.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static bool OnLocalNetwork(string url, IReadOnlyList<(byte[] Ip, byte[] Mask)> local)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var u) || !IPAddress.TryParse(u.Host, out var ip)) return false;
        var b = ip.GetAddressBytes();
        return b.Length == 4 && local.Any(n => Enumerable.Range(0, 4).All(i => (b[i] & n.Mask[i]) == (n.Ip[i] & n.Mask[i])));
    }

    private static IReadOnlyList<(byte[] Ip, byte[] Mask)> LocalNetworks()
    {
        var list = new List<(byte[], byte[])>();
        try
        {
            foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (nic.OperationalStatus != OperationalStatus.Up) continue;
                foreach (var a in nic.GetIPProperties().UnicastAddresses)
                    if (a.Address.AddressFamily == AddressFamily.InterNetwork && a.IPv4Mask is { } mask && !IPAddress.IsLoopback(a.Address))
                        list.Add((a.Address.GetAddressBytes(), mask.GetAddressBytes()));
            }
        }
        catch (Exception) { /* no interface list: the first answer wins */ }
        return list;
    }

    private static IEnumerable<IPAddress> Targets()
    {
        var targets = new HashSet<IPAddress> { IPAddress.Broadcast };
        try
        {
            foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (nic.OperationalStatus != OperationalStatus.Up || nic.NetworkInterfaceType == NetworkInterfaceType.Loopback) continue;
                foreach (var a in nic.GetIPProperties().UnicastAddresses)
                {
                    if (a.Address.AddressFamily != AddressFamily.InterNetwork) continue;
                    var mask = a.IPv4Mask;
                    if (mask == null || mask.Equals(IPAddress.Any)) continue;
                    var ip = a.Address.GetAddressBytes();
                    var m = mask.GetAddressBytes();
                    var b = new byte[4];
                    for (var i = 0; i < 4; i++) b[i] = (byte)(ip[i] | ~m[i]);
                    targets.Add(new IPAddress(b));
                }
            }
        }
        catch (Exception) { /* interface listing isn't available everywhere; the plain broadcast still goes */ }
        return targets;
    }
}
