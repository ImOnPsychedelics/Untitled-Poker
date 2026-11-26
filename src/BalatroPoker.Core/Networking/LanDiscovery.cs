using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using BalatroPoker.Core.Models;

namespace BalatroPoker.Core.Networking;

public sealed class LanDiscoveryService : IDisposable
{
    private readonly ConcurrentDictionary<string, LanRoomAdvertisement> _discovered = new();
    private CancellationTokenSource? _cts;
    private UdpClient? _listenClient;

    public IReadOnlyList<LanRoomAdvertisement> GetRooms()
    {
        DateTime cutoff = DateTime.UtcNow.AddSeconds(-6);
        foreach (var kv in _discovered)
        {
            if (kv.Value.LastSeenUtc < cutoff)
                _discovered.TryRemove(kv.Key, out _);
        }
        return _discovered.Values.OrderBy(r => r.RoomName).ToList();
    }

    public void StartListening(int discoveryPort = PacketFraming.DefaultDiscoveryPort)
    {
        Stop();
        _cts = new CancellationTokenSource();
        var ct = _cts.Token;

        _ = Task.Run(async () =>
        {
            try
            {
                _listenClient = new UdpClient();
                _listenClient.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
                _listenClient.Client.Bind(new IPEndPoint(IPAddress.Any, discoveryPort));

                while (!ct.IsCancellationRequested)
                {
                    var result = await _listenClient.ReceiveAsync(ct).ConfigureAwait(false);
                    var adv = JsonSerializer.Deserialize<LanRoomAdvertisement>(result.Buffer, PacketFraming.JsonOptions);
                    if (adv is null) continue;
                    adv.HostIp = result.RemoteEndPoint.Address.ToString();
                    adv.LastSeenUtc = DateTime.UtcNow;
                    string key = $"{adv.HostIp}:{adv.Port}";
                    _discovered[key] = adv;
                }
            }
            catch
            {
                // Ignore socket shutdown / restricted sandbox UDP errors
            }
        }, ct);
    }

    public static async Task BroadcastOnceAsync(LanRoomAdvertisement adv, int discoveryPort = PacketFraming.DefaultDiscoveryPort)
    {
        try
        {
            using var udp = new UdpClient { EnableBroadcast = true };
            byte[] data = JsonSerializer.SerializeToUtf8Bytes(adv, PacketFraming.JsonOptions);
            await udp.SendAsync(data, data.Length, new IPEndPoint(IPAddress.Broadcast, discoveryPort)).ConfigureAwait(false);
        }
        catch
        {
            // Ignore broadcast errors on isolated interfaces
        }
    }

    public void Stop()
    {
        try { _cts?.Cancel(); } catch { }
        try { _listenClient?.Close(); _listenClient?.Dispose(); } catch { }
        _listenClient = null;
        _cts?.Dispose();
        _cts = null;
    }

    public void Dispose() => Stop();
}
