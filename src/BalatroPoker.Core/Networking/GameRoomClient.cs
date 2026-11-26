using System.Net.Sockets;
using BalatroPoker.Core.Models;

namespace BalatroPoker.Core.Networking;

public sealed class GameRoomClient : IDisposable
{
    private readonly SemaphoreSlim _sendLock = new(1, 1);
    private TcpClient? _tcp;
    private NetworkStream? _stream;
    private CancellationTokenSource? _cts;
    private volatile TableSnapshot? _latestSnapshot;

    public string PlayerId { get; private set; } = string.Empty;
    public int SeatIndex { get; private set; } = -1;
    public bool IsConnected => _tcp?.Connected == true;
    public string StatusMessage { get; private set; } = string.Empty;
    public TableSnapshot? LatestSnapshot => _latestSnapshot;

    public async Task<(bool Ok, string Error)> ConnectAsync(string host, int port, string playerName, CancellationToken ct = default)
    {
        Disconnect();
        try
        {
            _tcp = new TcpClient { NoDelay = true };
            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeoutCts.CancelAfter(TimeSpan.FromSeconds(5));
            await _tcp.ConnectAsync(host, port, timeoutCts.Token).ConfigureAwait(false);
            _stream = _tcp.GetStream();

            await PacketFraming.SendAsync(_stream, new NetworkPacket
            {
                Type = MessageType.JoinRequest,
                PlayerName = playerName
            }, timeoutCts.Token).ConfigureAwait(false);

            var reply = await PacketFraming.ReceiveAsync(_stream, timeoutCts.Token).ConfigureAwait(false);
            if (reply is null)
            {
                Disconnect();
                return (false, "Host closed connection.");
            }
            if (reply.Type == MessageType.JoinRejected)
            {
                string reason = reply.Text ?? "Join rejected.";
                Disconnect();
                return (false, reason);
            }
            if (reply.Type != MessageType.JoinAccepted)
            {
                Disconnect();
                return (false, "Unexpected handshake response.");
            }

            PlayerId = reply.PlayerId ?? string.Empty;
            SeatIndex = reply.SeatIndex;
            StatusMessage = $"Connected to {reply.Text ?? host}";

            _cts = new CancellationTokenSource();
            _ = Task.Run(() => ReceiveLoopAsync(_cts.Token));
            return (true, string.Empty);
        }
        catch (Exception ex)
        {
            Disconnect();
            return (false, ex.Message);
        }
    }

    public Task SendHoldemActionAsync(HoldemActionType action, int raiseToAmount = 0) =>
        SendAsync(new NetworkPacket
        {
            Type = MessageType.HoldemAction,
            HoldemAction = action,
            Amount = raiseToAmount
        });

    public Task SendBaccaratBetAsync(BaccaratBetType betType, int amount) =>
        SendAsync(new NetworkPacket
        {
            Type = MessageType.BaccaratBet,
            BaccaratBetType = betType,
            Amount = amount
        });

    public Task SendBaccaratClearAsync() =>
        SendAsync(new NetworkPacket { Type = MessageType.BaccaratClear });

    public Task SendBaccaratLockAsync() =>
        SendAsync(new NetworkPacket { Type = MessageType.BaccaratLock });

    public Task SendGutsDecisionAsync(GutsDecision choice) =>
        SendAsync(new NetworkPacket { Type = MessageType.GutsDecision, GutsChoice = choice });

    public Task SendBlackjackBetAsync(int amount) =>
        SendAsync(new NetworkPacket { Type = MessageType.BlackjackBet, Amount = amount });

    public Task SendBlackjackClearAsync() =>
        SendAsync(new NetworkPacket { Type = MessageType.BlackjackClear });

    public Task SendBlackjackLockAsync() =>
        SendAsync(new NetworkPacket { Type = MessageType.BlackjackLock });

    public Task SendBlackjackActionAsync(BlackjackActionType action) =>
        SendAsync(new NetworkPacket { Type = MessageType.BlackjackAction, BlackjackAction = action });

    public Task SendRebuyAsync() =>
        SendAsync(new NetworkPacket { Type = MessageType.RebuyRequest });

    public Task SendChatAsync(string text, int pitch = 64) =>
        SendAsync(new NetworkPacket { Type = MessageType.ChatMessage, Text = text, Amount = Math.Clamp(pitch, 25, 130) });

    private async Task SendAsync(NetworkPacket packet)
    {
        if (_stream is null || !IsConnected) return;
        await _sendLock.WaitAsync().ConfigureAwait(false);
        try
        {
            await PacketFraming.SendAsync(_stream, packet).ConfigureAwait(false);
        }
        catch
        {
            StatusMessage = "Disconnected from host.";
        }
        finally
        {
            _sendLock.Release();
        }
    }

    private async Task ReceiveLoopAsync(CancellationToken ct)
    {
        try
        {
            while (!ct.IsCancellationRequested && _stream is not null)
            {
                var pkt = await PacketFraming.ReceiveAsync(_stream, ct).ConfigureAwait(false);
                if (pkt is null) break;
                if (pkt.Type == MessageType.TableSnapshot && pkt.Snapshot is not null)
                {
                    var prev = _latestSnapshot;
                    if (prev is null || pkt.Snapshot.StateVersion >= prev.StateVersion)
                        _latestSnapshot = pkt.Snapshot;
                }
            }
        }
        catch
        {
            // Connection dropped
        }
        finally
        {
            StatusMessage = "Disconnected from host.";
        }
    }

    public void Disconnect()
    {
        try { _cts?.Cancel(); } catch { }
        try { _stream?.Close(); _stream?.Dispose(); } catch { }
        try { _tcp?.Close(); _tcp?.Dispose(); } catch { }
        _stream = null;
        _tcp = null;
        _cts?.Dispose();
        _cts = null;
    }

    public void Dispose() => Disconnect();
}
