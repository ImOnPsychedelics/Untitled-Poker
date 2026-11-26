using System.Net;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using BalatroPoker.Core.Models;
using BalatroPoker.Web;

var builder = WebApplication.CreateBuilder(args);
builder.Services.AddSingleton<WebRoomManager>();

string portEnv = Environment.GetEnvironmentVariable("PORT") ?? "5080";
if (args.Length == 0 && !builder.Configuration.GetSection("Urls").Exists())
{
    builder.WebHost.UseUrls($"http://0.0.0.0:{portEnv}");
}

var app = builder.Build();
app.UseDefaultFiles();
app.UseStaticFiles();
app.UseWebSockets(new WebSocketOptions { KeepAliveInterval = TimeSpan.FromSeconds(20) });

app.MapGet("/api/health", () => Results.Ok(new { status = "ok", service = "UntitledPoker.Web" }));
app.MapGet("/api/rooms", (WebRoomManager mgr) => Results.Ok(mgr.GetPublicRooms()));

app.Map("/ws", async (HttpContext ctx, WebRoomManager mgr) =>
{
    if (!ctx.WebSockets.IsWebSocketRequest)
    {
        ctx.Response.StatusCode = 400;
        await ctx.Response.WriteAsync("WebSocket connection required.");
        return;
    }

    using var ws = await ctx.WebSockets.AcceptWebSocketAsync();
    WebRoom? activeRoom = null;
    string? playerId = null;
    Task? writeTask = null;
    using var writeCts = new CancellationTokenSource();

    try
    {
        while (ws.State == WebSocketState.Open)
        {
            string? msg = await ReceiveTextAsync(ws, ctx.RequestAborted);
            if (msg is null) break;

            using var doc = JsonDocument.Parse(msg);
            var root = doc.RootElement;
            if (!root.TryGetProperty("type", out var typeProp)) continue;
            string type = typeProp.GetString() ?? string.Empty;

            if (activeRoom is null)
            {
                if (type == "create_room")
                {
                    string playerName = root.TryGetProperty("playerName", out var pn) ? pn.GetString() ?? "HOST" : "HOST";
                    string roomName = root.TryGetProperty("roomName", out var rn) ? rn.GetString() ?? $"{playerName}'S TABLE" : $"{playerName}'S TABLE";
                    bool isPrivate = root.TryGetProperty("isPrivate", out var pr) && pr.GetBoolean();
                    var mode = root.TryGetProperty("mode", out var mp) ? (GameMode)mp.GetInt32() : GameMode.TexasHoldem;
                    int initialBots = root.TryGetProperty("initialBots", out var ib) ? ib.GetInt32() : 0;

                    var room = mgr.CreateRoom(roomName, isPrivate, mode);
                    var (ok, err, pid, _) = room.JoinHuman(playerName, ws, makeHost: true, initialBots: initialBots);
                    if (!ok)
                    {
                        await SendErrorAsync(ws, err);
                        continue;
                    }

                    activeRoom = room;
                    playerId = pid;
                    var conn = room.GetClient(pid);
                    if (conn is not null)
                        writeTask = Task.Run(() => PumpOutboxAsync(conn, writeCts.Token));
                }
                else if (type == "join_room")
                {
                    string playerName = root.TryGetProperty("playerName", out var pn) ? pn.GetString() ?? "PLAYER" : "PLAYER";
                    string roomCode = root.TryGetProperty("roomCode", out var rc) ? rc.GetString() ?? "" : "";
                    var room = mgr.GetRoom(roomCode);
                    if (room is null)
                    {
                        await SendErrorAsync(ws, $"Room '{roomCode}' not found.");
                        continue;
                    }

                    var (ok, err, pid, _) = room.JoinHuman(playerName, ws, makeHost: false);
                    if (!ok)
                    {
                        await SendErrorAsync(ws, err);
                        continue;
                    }

                    activeRoom = room;
                    playerId = pid;
                    var conn = room.GetClient(pid);
                    if (conn is not null)
                        writeTask = Task.Run(() => PumpOutboxAsync(conn, writeCts.Token));
                }
            }
            else if (type == "leave_room")
            {
                if (playerId is not null)
                {
                    activeRoom.RemoveHuman(playerId);
                    mgr.CleanupIfEmpty(activeRoom.RoomCode);
                }
                activeRoom = null;
                playerId = null;
            }
            else if (playerId is not null)
            {
                activeRoom.HandleClientMessage(playerId, root);
            }
        }
    }
    catch
    {
        // Client disconnected
    }
    finally
    {
        writeCts.Cancel();
        if (activeRoom is not null && playerId is not null)
        {
            activeRoom.RemoveHuman(playerId);
            mgr.CleanupIfEmpty(activeRoom.RoomCode);
        }
        if (writeTask is not null)
        {
            try { await writeTask; } catch { }
        }
    }
});

string localIp = DetectLocalIp();
Console.WriteLine("=======================================================");
Console.WriteLine("  UNTITLED POKER - STANDALONE WEB SERVER");
Console.WriteLine($"  Local:   http://localhost:{portEnv}");
Console.WriteLine($"  Network: http://{localIp}:{portEnv}");
Console.WriteLine("  Share the URL (or tunnel via Cloudflare/ngrok/Tailscale)");
Console.WriteLine("  so friends can play Hold'em & Baccarat in any browser!");
Console.WriteLine("=======================================================");

app.Run();

static async Task PumpOutboxAsync(WebClientConnection conn, CancellationToken ct)
{
    try
    {
        await foreach (string json in conn.Outbox.Reader.ReadAllAsync(ct))
        {
            if (conn.Socket.State != WebSocketState.Open) break;
            byte[] bytes = Encoding.UTF8.GetBytes(json);
            await conn.Socket.SendAsync(bytes, WebSocketMessageType.Text, true, ct);
        }
    }
    catch { }
}

static async Task SendErrorAsync(WebSocket ws, string message)
{
    if (ws.State != WebSocketState.Open) return;
    string json = JsonSerializer.Serialize(new { type = "error", message });
    await ws.SendAsync(Encoding.UTF8.GetBytes(json), WebSocketMessageType.Text, true, CancellationToken.None);
}

static async Task<string?> ReceiveTextAsync(WebSocket ws, CancellationToken ct)
{
    byte[] buffer = new byte[8192];
    using var ms = new MemoryStream();
    while (true)
    {
        var result = await ws.ReceiveAsync(buffer, ct);
        if (result.MessageType == WebSocketMessageType.Close) return null;
        ms.Write(buffer, 0, result.Count);
        if (result.EndOfMessage)
            return Encoding.UTF8.GetString(ms.ToArray());
    }
}

static string DetectLocalIp()
{
    try
    {
        foreach (var ip in Dns.GetHostEntry(Dns.GetHostName()).AddressList)
        {
            if (ip.AddressFamily == AddressFamily.InterNetwork && !IPAddress.IsLoopback(ip))
                return ip.ToString();
        }
    }
    catch { }
    return "127.0.0.1";
}
