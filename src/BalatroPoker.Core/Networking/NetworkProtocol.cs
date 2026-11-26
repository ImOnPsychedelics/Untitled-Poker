using System.Buffers.Binary;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using BalatroPoker.Core.Models;

namespace BalatroPoker.Core.Networking;

public enum MessageType
{
    JoinRequest = 0,
    JoinAccepted = 1,
    JoinRejected = 2,
    TableSnapshot = 3,
    HoldemAction = 4,
    BaccaratBet = 5,
    BaccaratClear = 6,
    BaccaratLock = 7,
    ReadyToggle = 8,
    RebuyRequest = 9,
    ChatMessage = 10,
    Leave = 11,
    GutsDecision = 12,
    BlackjackBet = 13,
    BlackjackClear = 14,
    BlackjackLock = 15,
    BlackjackAction = 16
}

public sealed class NetworkPacket
{
    public MessageType Type { get; set; }
    public string? PlayerName { get; set; }
    public string? PlayerId { get; set; }
    public int SeatIndex { get; set; } = -1;
    public string? Text { get; set; }
    public HoldemActionType HoldemAction { get; set; }
    public int Amount { get; set; }
    public BaccaratBetType BaccaratBetType { get; set; }
    public GutsDecision GutsChoice { get; set; }
    public BlackjackActionType BlackjackAction { get; set; }
    public TableSnapshot? Snapshot { get; set; }
}

public static class PacketFraming
{
    public const int DefaultGamePort = 7777;
    public const int DefaultDiscoveryPort = 7778;
    private const int MaxPacketBytes = 2 * 1024 * 1024;

    public static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        WriteIndented = false
    };

    public static async Task SendAsync(NetworkStream stream, NetworkPacket packet, CancellationToken ct = default)
    {
        byte[] payload = JsonSerializer.SerializeToUtf8Bytes(packet, JsonOptions);
        byte[] frame = new byte[4 + payload.Length];
        BinaryPrimitives.WriteInt32BigEndian(frame.AsSpan(0, 4), payload.Length);
        Buffer.BlockCopy(payload, 0, frame, 4, payload.Length);
        await stream.WriteAsync(frame, ct).ConfigureAwait(false);
        await stream.FlushAsync(ct).ConfigureAwait(false);
    }

    public static async Task<NetworkPacket?> ReceiveAsync(NetworkStream stream, CancellationToken ct = default)
    {
        byte[] lenBuf = new byte[4];
        if (!await ReadExactAsync(stream, lenBuf, ct).ConfigureAwait(false))
            return null;

        int length = BinaryPrimitives.ReadInt32BigEndian(lenBuf);
        if (length <= 0 || length > MaxPacketBytes)
            return null;

        byte[] payload = new byte[length];
        if (!await ReadExactAsync(stream, payload, ct).ConfigureAwait(false))
            return null;

        return JsonSerializer.Deserialize<NetworkPacket>(payload, JsonOptions);
    }

    private static async Task<bool> ReadExactAsync(NetworkStream stream, byte[] buffer, CancellationToken ct)
    {
        int offset = 0;
        while (offset < buffer.Length)
        {
            int read = await stream.ReadAsync(buffer.AsMemory(offset, buffer.Length - offset), ct).ConfigureAwait(false);
            if (read == 0) return false;
            offset += read;
        }
        return true;
    }
}

public sealed class LanRoomAdvertisement
{
    public string RoomName { get; set; } = string.Empty;
    public string HostIp { get; set; } = string.Empty;
    public int Port { get; set; } = PacketFraming.DefaultGamePort;
    public GameMode Mode { get; set; }
    public int PlayerCount { get; set; }
    public int MaxPlayers { get; set; } = 10;
    public DateTime LastSeenUtc { get; set; } = DateTime.UtcNow;
}
