using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using FlipPix.Remote.Contracts;

namespace FlipPix.Remote.Host;

/// <summary>
/// Answers a phone's "is FlipPix here?" broadcast on the Wi-Fi, so pairing starts from a list of
/// computers rather than an IP address typed on a phone keyboard. The answer names the computer and
/// the port; it grants nothing, since every real request still needs a paired token.
/// </summary>
public sealed class DiscoveryResponder : IDisposable
{
    private readonly Func<HelloDto> _hello;
    private readonly Action<string> _log;
    private UdpClient? _udp;
    private CancellationTokenSource? _cts;

    public DiscoveryResponder(Func<HelloDto> hello, Action<string> log)
    {
        _hello = hello;
        _log = log;
    }

    public void Start()
    {
        Stop();
        try
        {
            var udp = new UdpClient(AddressFamily.InterNetwork);
            udp.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
            udp.Client.Bind(new IPEndPoint(IPAddress.Any, RemoteApi.DiscoveryPort));
            _udp = udp;
            _cts = new CancellationTokenSource();
            _ = ListenAsync(udp, _cts.Token);
        }
        catch (SocketException ex)
        {
            // Not fatal: the phone can still be given the address by hand.
            _log($"Discovery is off (UDP {RemoteApi.DiscoveryPort}: {ex.Message}). Phones can still connect by address.");
        }
    }

    private async Task ListenAsync(UdpClient udp, CancellationToken ct)
    {
        var probe = Encoding.ASCII.GetBytes(RemoteApi.DiscoveryProbe);
        while (!ct.IsCancellationRequested)
        {
            try
            {
                var got = await udp.ReceiveAsync(ct);
                if (!got.Buffer.AsSpan().SequenceEqual(probe)) continue;
                var reply = JsonSerializer.SerializeToUtf8Bytes(_hello(), new JsonSerializerOptions(JsonSerializerDefaults.Web));
                await udp.SendAsync(reply, got.RemoteEndPoint, ct);
            }
            catch (OperationCanceledException) { return; }
            catch (ObjectDisposedException) { return; }
            catch (SocketException) { /* one bad datagram (or an ICMP echo of one) doesn't stop listening */ }
        }
    }

    public void Stop()
    {
        _cts?.Cancel();
        _udp?.Dispose();
        _udp = null;
        _cts = null;
    }

    public void Dispose() => Stop();
}
