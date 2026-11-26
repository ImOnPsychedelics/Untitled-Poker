using System.Collections.Concurrent;
using System.Security.Cryptography;
using BalatroPoker.Core.Models;

namespace BalatroPoker.Web;

public sealed class WebRoomManager : IDisposable
{
    private const string CodeChars = "ABCDEFGHJKLMNPQRSTUVWXYZ23456789";
    private readonly ConcurrentDictionary<string, WebRoom> _rooms = new(StringComparer.OrdinalIgnoreCase);
    private readonly Timer _botTimer;

    public WebRoomManager()
    {
        _botTimer = new Timer(_ => TickAll(), null, 350, 350);
    }

    public WebRoom CreateRoom(string roomName, bool isPrivate, GameMode mode, int startingChips = 1000)
    {
        string code = GenerateCode();
        var room = new WebRoom(code, roomName, isPrivate, mode, startingChips);
        _rooms[code] = room;
        return room;
    }

    public WebRoom? GetRoom(string roomCode)
    {
        if (string.IsNullOrWhiteSpace(roomCode)) return null;
        return _rooms.TryGetValue(roomCode.Trim().ToUpperInvariant(), out var room) ? room : null;
    }

    public IReadOnlyList<WebRoomSummary> GetPublicRooms()
    {
        return _rooms.Values
            .Where(r => !r.IsPrivate && r.HasHumans)
            .Select(r => r.GetSummary())
            .OrderByDescending(r => r.PlayerCount)
            .ToList();
    }

    public void CleanupIfEmpty(string roomCode)
    {
        if (_rooms.TryGetValue(roomCode, out var room) && !room.HasHumans)
        {
            _rooms.TryRemove(roomCode, out _);
        }
    }

    private void TickAll()
    {
        foreach (var kv in _rooms)
        {
            var room = kv.Value;
            if (!room.HasHumans && (DateTime.UtcNow - room.LastActivityUtc).TotalSeconds > 30)
            {
                _rooms.TryRemove(kv.Key, out _);
                continue;
            }
            try { room.TickBots(); } catch { }
        }
    }

    private string GenerateCode()
    {
        Span<char> buf = stackalloc char[5];
        for (int attempt = 0; attempt < 20; attempt++)
        {
            for (int i = 0; i < 5; i++)
                buf[i] = CodeChars[RandomNumberGenerator.GetInt32(CodeChars.Length)];
            string code = new(buf);
            if (!_rooms.ContainsKey(code))
                return code;
        }
        return Guid.NewGuid().ToString("N")[..5].ToUpperInvariant();
    }

    public void Dispose() => _botTimer.Dispose();
}
