using System.Collections.Concurrent;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using BalatroPoker.Core.Baccarat;
using BalatroPoker.Core.Blackjack;
using BalatroPoker.Core.Cards;
using BalatroPoker.Core.Guts;
using BalatroPoker.Core.Holdem;
using BalatroPoker.Core.Models;
using BalatroPoker.Core.Networking;

namespace BalatroPoker.Web;

public sealed class WebRoomSummary
{
    public string RoomCode { get; set; } = string.Empty;
    public string RoomName { get; set; } = string.Empty;
    public GameMode Mode { get; set; }
    public RoomPhase Phase { get; set; }
    public int PlayerCount { get; set; }
    public int HumanCount { get; set; }
    public int MaxPlayers { get; set; } = 10;
}

public sealed class WebClientConnection
{
    public string PlayerId { get; init; } = string.Empty;
    public WebSocket Socket { get; init; } = null!;
    public Channel<string> Outbox { get; } = Channel.CreateUnbounded<string>(
        new UnboundedChannelOptions { SingleReader = true });
}

public sealed class WebRoom
{
    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true
    };

    private static readonly string[] BotNames =
    {
        "JIMBO", "GROS MICHEL", "CAVENDISH", "BLUEPRINT", "BRAINSTORM",
        "PERKEO", "CANIO", "TRIBOULET", "YORICK", "CHICOT"
    };

    private readonly object _lock = new();
    private readonly List<HoldemPlayer> _players = new();
    private readonly List<string> _actionLog = new();
    private readonly ConcurrentDictionary<string, WebClientConnection> _clients = new();
    private readonly Random _rng = new();

    private long _stateVersion;
    private long _chatSeq;
    private int _botCounter;
    private DateTime _nextBotActionUtc = DateTime.MinValue;

    public string RoomCode { get; }
    public bool IsPrivate { get; }
    public GameConfig Config { get; } = new();
    public RoomPhase Phase { get; private set; } = RoomPhase.Lobby;
    public HoldemEngine Holdem { get; }
    public BaccaratEngine Baccarat { get; }
    public GutsEngine Guts { get; }
    public BlackjackEngine Blackjack { get; }
    public DateTime LastActivityUtc { get; private set; } = DateTime.UtcNow;

    public WebRoom(string roomCode, string roomName, bool isPrivate, GameMode mode, int startingChips = 1000)
    {
        RoomCode = roomCode;
        IsPrivate = isPrivate;
        Config.RoomName = string.IsNullOrWhiteSpace(roomName) ? $"ROOM {roomCode}" : roomName.Trim().ToUpperInvariant();
        if (Config.RoomName.Length > 24) Config.RoomName = Config.RoomName[..24];
        Config.Mode = mode;
        Config.StartingChips = Math.Clamp(startingChips, 100, 100_000);
        Config.MaxPlayers = 10;

        Holdem = new HoldemEngine(AddLog);
        Baccarat = new BaccaratEngine(AddLog);
        Guts = new GutsEngine(AddLog);
        Blackjack = new BlackjackEngine(AddLog);
    }

    public bool HasHumans
    {
        get { lock (_lock) return _players.Any(p => !p.IsBot && p.IsConnected); }
    }

    public WebRoomSummary GetSummary()
    {
        lock (_lock)
        {
            return new WebRoomSummary
            {
                RoomCode = RoomCode,
                RoomName = Config.RoomName,
                Mode = Config.Mode,
                Phase = Phase,
                PlayerCount = _players.Count,
                HumanCount = _players.Count(p => !p.IsBot),
                MaxPlayers = Config.MaxPlayers
            };
        }
    }

    public (bool Ok, string Error, string PlayerId, int SeatIndex) JoinHuman(
        string playerName,
        WebSocket socket,
        bool makeHost,
        int initialBots = 0)
    {
        string playerId = Guid.NewGuid().ToString("N")[..12];
        string cleanName = string.IsNullOrWhiteSpace(playerName) ? "PLAYER" : playerName.Trim().ToUpperInvariant();
        if (cleanName.Length > 14) cleanName = cleanName[..14];

        int seat;
        WebClientConnection conn;
        lock (_lock)
        {
            seat = FindOpenSeat();
            if (seat == -1)
                return (false, $"Room is full ({Config.MaxPlayers}/{Config.MaxPlayers}).", string.Empty, -1);

            bool isHost = makeHost || _players.All(p => !p.IsHost);
            _players.Add(new HoldemPlayer
            {
                SeatIndex = seat,
                PlayerId = playerId,
                Name = cleanName,
                IsHost = isHost,
                IsBot = false,
                IsConnected = true,
                IsReady = true,
                Chips = Config.StartingChips,
                SittingOut = Phase == RoomPhase.InGame && Config.Mode == GameMode.TexasHoldem
            });

            AddLog($"{cleanName} joined seat #{seat + 1}.");

            if (makeHost && initialBots > 0)
            {
                SetBotCountUnsafe(initialBots);
            }

            conn = new WebClientConnection { PlayerId = playerId, Socket = socket };
            _clients[playerId] = conn;
            LastActivityUtc = DateTime.UtcNow;
        }

        // Send joined ack first, then broadcast updated snapshot to everyone
        SendJsonToClient(conn, new
        {
            type = "joined",
            roomCode = RoomCode,
            roomName = Config.RoomName,
            isPrivate = IsPrivate,
            playerId,
            seatIndex = seat
        });

        BroadcastSnapshots();
        return (true, string.Empty, playerId, seat);
    }

    public WebClientConnection? GetClient(string playerId) =>
        _clients.TryGetValue(playerId, out var c) ? c : null;

    public void RemoveHuman(string playerId)
    {
        if (_clients.TryRemove(playerId, out var conn))
        {
            conn.Outbox.Writer.TryComplete();
        }

        lock (_lock)
        {
            var p = _players.FirstOrDefault(x => x.PlayerId == playerId);
            if (p is null) return;

            AddLog($"{p.Name} left the room.");
            bool wasHost = p.IsHost;

            if (Phase == RoomPhase.InGame && Config.Mode == GameMode.TexasHoldem && Holdem.CurrentTurnSeat == p.SeatIndex)
            {
                Holdem.ApplyAction(_players, p.SeatIndex, HoldemActionType.Fold, 0, Config);
            }

            _players.Remove(p);

            if (wasHost)
            {
                var nextHost = _players.FirstOrDefault(x => !x.IsBot && x.IsConnected);
                if (nextHost is not null)
                {
                    nextHost.IsHost = true;
                    AddLog($"{nextHost.Name} is now the Host.");
                }
            }
            LastActivityUtc = DateTime.UtcNow;
        }

        BroadcastSnapshots();
    }

    public void HandleClientMessage(string playerId, JsonElement root)
    {
        if (!root.TryGetProperty("type", out var typeProp)) return;
        string msgType = typeProp.GetString() ?? string.Empty;

        bool changed = false;
        lock (_lock)
        {
            LastActivityUtc = DateTime.UtcNow;
            var player = _players.FirstOrDefault(p => p.PlayerId == playerId);
            if (player is null) return;

            switch (msgType)
            {
                case "switch_mode":
                    if (!player.IsHost) return;
                    if (root.TryGetProperty("mode", out var modeProp))
                    {
                        var mode = (GameMode)modeProp.GetInt32();
                        Config.Mode = mode;
                        AddLog($"Mode switched to {GameRoomHost.ModeDisplayName(mode)}.");
                        if (Phase == RoomPhase.InGame) StartCurrentModeUnsafe();
                        changed = true;
                    }
                    break;

                case "set_blinds":
                    if (!player.IsHost) return;
                    int sb = root.TryGetProperty("smallBlind", out var sbP) ? sbP.GetInt32() : 10;
                    int bb = root.TryGetProperty("bigBlind", out var bbP) ? bbP.GetInt32() : 20;
                    Config.SmallBlind = Math.Max(5, sb);
                    Config.BigBlind = Math.Max(Config.SmallBlind * 2, bb);
                    AddLog($"Blinds set to ${Config.SmallBlind}/${Config.BigBlind}.");
                    changed = true;
                    break;

                case "add_bot_seat":
                    if (!player.IsHost) return;
                    if (root.TryGetProperty("seatIndex", out var addSeatP))
                        changed = AddBotAtSeatUnsafe(addSeatP.GetInt32());
                    break;

                case "remove_bot_seat":
                    if (!player.IsHost) return;
                    if (root.TryGetProperty("seatIndex", out var remSeatP))
                        changed = RemoveBotAtSeatUnsafe(remSeatP.GetInt32());
                    break;

                case "set_bot_count":
                    if (!player.IsHost) return;
                    if (root.TryGetProperty("count", out var cntP))
                        changed = SetBotCountUnsafe(cntP.GetInt32());
                    break;

                case "start_game":
                    if (!player.IsHost) return;
                    bool needsTwo = Config.Mode is GameMode.TexasHoldem or GameMode.OmahaHoldem or GameMode.GutsPoker;
                    if (needsTwo && _players.Count(p => p.IsConnected && p.Chips > 0) < 2)
                    {
                        AddLog($"Need at least 2 players (or add a bot) to start {GameRoomHost.ModeDisplayName(Config.Mode)}.");
                        changed = true;
                        break;
                    }
                    Phase = RoomPhase.InGame;
                    foreach (var p in _players)
                        if (p.Chips <= 0) p.Chips = Config.StartingChips;
                    StartCurrentModeUnsafe();
                    _nextBotActionUtc = DateTime.UtcNow.AddMilliseconds(600);
                    changed = true;
                    break;

                case "return_lobby":
                    if (!player.IsHost) return;
                    Phase = RoomPhase.Lobby;
                    AddLog("Returned to lobby.");
                    changed = true;
                    break;

                case "next_round":
                    if (!player.IsHost || Phase != RoomPhase.InGame) return;
                    if (Config.Mode is GameMode.TexasHoldem or GameMode.OmahaHoldem)
                    {
                        foreach (var bot in _players.Where(p => p.IsBot && p.Chips <= 0))
                            bot.Chips = Config.StartingChips;
                        Holdem.StartNewHand(_players, Config);
                        _nextBotActionUtc = DateTime.UtcNow.AddMilliseconds(600);
                    }
                    else if (Config.Mode == GameMode.Baccarat)
                    {
                        if (Baccarat.Stage == BaccaratStage.Betting)
                        {
                            Baccarat.PlaceBotBets(_players, _rng);
                            Baccarat.DealAndSettle(_players);
                        }
                        else
                        {
                            foreach (var bot in _players.Where(p => p.IsBot && p.Chips < 10))
                                bot.Chips = Config.StartingChips;
                            Baccarat.StartBettingPhase(_players);
                            _nextBotActionUtc = DateTime.UtcNow.AddMilliseconds(400);
                        }
                    }
                    else if (Config.Mode == GameMode.GutsPoker)
                    {
                        if (Guts.Stage == GutsStage.Deciding)
                        {
                            Guts.DecideBots(_players, _rng);
                            foreach (var p in _players.Where(x => !x.SittingOut && x.GutsChoice == GutsDecision.Undecided))
                                p.GutsChoice = GutsDecision.Out;
                            Guts.ResolveShowdown(_players);
                        }
                        else
                        {
                            foreach (var bot in _players.Where(p => p.IsBot && p.Chips < Config.BigBlind))
                                bot.Chips = Config.StartingChips;
                            Guts.StartNewRound(_players, Config);
                            _nextBotActionUtc = DateTime.UtcNow.AddMilliseconds(500);
                        }
                    }
                    else if (Config.Mode == GameMode.Blackjack)
                    {
                        if (Blackjack.Stage == BlackjackStage.Betting)
                        {
                            Blackjack.PlaceBotBets(_players, _rng);
                            Blackjack.DealInitial(_players);
                            _nextBotActionUtc = DateTime.UtcNow.AddMilliseconds(550);
                        }
                        else
                        {
                            foreach (var bot in _players.Where(p => p.IsBot && p.Chips < 10))
                                bot.Chips = Config.StartingChips;
                            Blackjack.StartBettingPhase(_players);
                            _nextBotActionUtc = DateTime.UtcNow.AddMilliseconds(400);
                        }
                    }
                    changed = true;
                    break;

                case "holdem_action":
                    if (Phase != RoomPhase.InGame || Config.Mode is not (GameMode.TexasHoldem or GameMode.OmahaHoldem)) return;
                    var action = (HoldemActionType)(root.TryGetProperty("action", out var actP) ? actP.GetInt32() : 0);
                    int amount = root.TryGetProperty("amount", out var amtP) ? amtP.GetInt32() : 0;
                    var hRes = Holdem.ApplyAction(_players, player.SeatIndex, action, amount, Config);
                    if (hRes.Ok)
                    {
                        _nextBotActionUtc = DateTime.UtcNow.AddMilliseconds(550);
                        changed = true;
                    }
                    break;

                case "baccarat_bet":
                    if (Phase != RoomPhase.InGame || Config.Mode != GameMode.Baccarat) return;
                    var bType = (BaccaratBetType)(root.TryGetProperty("betType", out var btP) ? btP.GetInt32() : 0);
                    int bAmt = root.TryGetProperty("amount", out var baP) ? baP.GetInt32() : 0;
                    var bRes = Baccarat.PlaceBet(player, bType, bAmt);
                    changed = bRes.Ok;
                    break;

                case "baccarat_clear":
                    if (Phase != RoomPhase.InGame || Config.Mode != GameMode.Baccarat) return;
                    Baccarat.ClearBets(player);
                    changed = true;
                    break;

                case "baccarat_lock":
                    if (Phase != RoomPhase.InGame || Config.Mode != GameMode.Baccarat) return;
                    Baccarat.LockBets(player);
                    Baccarat.PlaceBotBets(_players, _rng);
                    if (Baccarat.AreAllPlayersLocked(_players))
                        Baccarat.DealAndSettle(_players);
                    changed = true;
                    break;

                case "guts_decision":
                    if (Phase != RoomPhase.InGame || Config.Mode != GameMode.GutsPoker) return;
                    var gChoice = (GutsDecision)(root.TryGetProperty("choice", out var gcP) ? gcP.GetInt32() : 2);
                    if (Guts.SubmitDecision(_players, player.SeatIndex, gChoice).Ok)
                    {
                        Guts.DecideBots(_players, _rng);
                        if (Guts.AreAllDecided(_players))
                            Guts.ResolveShowdown(_players);
                        changed = true;
                    }
                    break;

                case "bj_bet":
                    if (Phase != RoomPhase.InGame || Config.Mode != GameMode.Blackjack) return;
                    int bjAmt = root.TryGetProperty("amount", out var bjaP) ? bjaP.GetInt32() : 0;
                    changed = Blackjack.PlaceBet(player, bjAmt).Ok;
                    break;

                case "bj_clear":
                    if (Phase != RoomPhase.InGame || Config.Mode != GameMode.Blackjack) return;
                    Blackjack.ClearBet(player);
                    changed = true;
                    break;

                case "bj_lock":
                    if (Phase != RoomPhase.InGame || Config.Mode != GameMode.Blackjack) return;
                    Blackjack.LockBet(player);
                    Blackjack.PlaceBotBets(_players, _rng);
                    if (Blackjack.AreAllLocked(_players))
                    {
                        Blackjack.DealInitial(_players);
                        _nextBotActionUtc = DateTime.UtcNow.AddMilliseconds(550);
                    }
                    changed = true;
                    break;

                case "bj_action":
                    if (Phase != RoomPhase.InGame || Config.Mode != GameMode.Blackjack) return;
                    var bjAct = (BlackjackActionType)(root.TryGetProperty("action", out var bjacP) ? bjacP.GetInt32() : 1);
                    if (Blackjack.ApplyAction(_players, player.SeatIndex, bjAct).Ok)
                    {
                        _nextBotActionUtc = DateTime.UtcNow.AddMilliseconds(550);
                        changed = true;
                    }
                    break;

                case "rebuy":
                    if (player.Chips < Config.StartingChips)
                    {
                        player.Chips = Config.StartingChips;
                        AddLog($"{player.Name} rebought to ${Config.StartingChips}.");
                        changed = true;
                    }
                    break;

                case "chat":
                    if (root.TryGetProperty("text", out var txtP))
                    {
                        string text = (txtP.GetString() ?? string.Empty).Trim();
                        int pitch = root.TryGetProperty("pitch", out var ptP) ? Math.Clamp(ptP.GetInt32(), 25, 130) : 64;
                        if (text.Length > 0)
                        {
                            if (text.Length > 64) text = text[..64];
                            player.LastChatMessage = text;
                            player.LastChatPitch = pitch;
                            player.LastChatId = ++_chatSeq;
                            player.LastChatUnixMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
                            AddLog($"{player.Name}: {text}");
                            changed = true;
                        }
                    }
                    break;
            }
        }

        if (changed) BroadcastSnapshots();
    }

    public void TickBots()
    {
        bool changed = false;
        lock (_lock)
        {
            if (Phase != RoomPhase.InGame) return;
            if (DateTime.UtcNow < _nextBotActionUtc) return;

            if (Config.Mode is GameMode.TexasHoldem or GameMode.OmahaHoldem)
            {
                if (Holdem.Stage is HoldemStage.PreFlop or HoldemStage.Flop or HoldemStage.Turn or HoldemStage.River)
                {
                    var current = _players.FirstOrDefault(p => p.SeatIndex == Holdem.CurrentTurnSeat);
                    if (current is not null && current.IsBot)
                    {
                        var (action, amount) = Holdem.DecideBotAction(_players, current, Config, _rng);
                        Holdem.ApplyAction(_players, current.SeatIndex, action, amount, Config);
                        _nextBotActionUtc = DateTime.UtcNow.AddMilliseconds(600);
                        changed = true;
                    }
                }
            }
            else if (Config.Mode == GameMode.Baccarat && Baccarat.Stage == BaccaratStage.Betting)
            {
                bool anyUnlockedBot = _players.Any(p => p.IsBot && p.IsConnected && !p.BaccaratBets.LockedIn);
                if (anyUnlockedBot)
                {
                    Baccarat.PlaceBotBets(_players, _rng);
                    changed = true;
                }
                if (Baccarat.AreAllPlayersLocked(_players))
                {
                    Baccarat.DealAndSettle(_players);
                    changed = true;
                }
            }
            else if (Config.Mode == GameMode.GutsPoker && Guts.Stage == GutsStage.Deciding)
            {
                bool anyUndecidedBot = _players.Any(p => p.IsBot && !p.SittingOut && p.GutsChoice == GutsDecision.Undecided);
                if (anyUndecidedBot)
                {
                    Guts.DecideBots(_players, _rng);
                    changed = true;
                }
                if (Guts.AreAllDecided(_players))
                {
                    Guts.ResolveShowdown(_players);
                    changed = true;
                }
            }
            else if (Config.Mode == GameMode.Blackjack)
            {
                if (Blackjack.Stage == BlackjackStage.Betting)
                {
                    bool anyUnlockedBot = _players.Any(p => p.IsBot && p.IsConnected && !p.BlackjackLocked);
                    if (anyUnlockedBot)
                    {
                        Blackjack.PlaceBotBets(_players, _rng);
                        changed = true;
                    }
                    if (Blackjack.AreAllLocked(_players))
                    {
                        Blackjack.DealInitial(_players);
                        _nextBotActionUtc = DateTime.UtcNow.AddMilliseconds(550);
                        changed = true;
                    }
                }
                else if (Blackjack.Stage == BlackjackStage.PlayerTurns)
                {
                    var current = _players.FirstOrDefault(p => p.SeatIndex == Blackjack.CurrentTurnSeat);
                    if (current is not null && current.IsBot)
                    {
                        Blackjack.ApplyAction(_players, current.SeatIndex, Blackjack.DecideBotAction(current));
                        _nextBotActionUtc = DateTime.UtcNow.AddMilliseconds(550);
                        changed = true;
                    }
                }
            }
        }

        if (changed) BroadcastSnapshots();
    }

    private void StartCurrentModeUnsafe()
    {
        switch (Config.Mode)
        {
            case GameMode.TexasHoldem:
            case GameMode.OmahaHoldem:
                Holdem.StartNewHand(_players, Config);
                break;
            case GameMode.Baccarat:
                Baccarat.StartBettingPhase(_players);
                break;
            case GameMode.GutsPoker:
                Guts.StartNewRound(_players, Config);
                break;
            case GameMode.Blackjack:
                Blackjack.StartBettingPhase(_players);
                break;
        }
    }

    private bool AddBotAtSeatUnsafe(int seatIndex)
    {
        if (seatIndex < 0 || seatIndex >= Config.MaxPlayers) return false;
        if (_players.Any(p => p.SeatIndex == seatIndex)) return false;

        _botCounter++;
        string name = BotNames[(_botCounter - 1) % BotNames.Length];
        _players.Add(new HoldemPlayer
        {
            SeatIndex = seatIndex,
            PlayerId = $"bot-{_botCounter}",
            Name = name,
            IsBot = true,
            IsConnected = true,
            IsReady = true,
            Chips = Config.StartingChips
        });
        AddLog($"AI Bot {name} placed in seat #{seatIndex + 1}.");
        return true;
    }

    private bool RemoveBotAtSeatUnsafe(int seatIndex)
    {
        var bot = _players.FirstOrDefault(p => p.SeatIndex == seatIndex && p.IsBot);
        if (bot is null) return false;
        _players.Remove(bot);
        AddLog($"AI Bot {bot.Name} removed from seat #{seatIndex + 1}.");
        return true;
    }

    private bool SetBotCountUnsafe(int targetBotCount)
    {
        targetBotCount = Math.Clamp(targetBotCount, 0, Config.MaxPlayers - 1);
        bool changed = false;
        int current = _players.Count(p => p.IsBot);
        while (current < targetBotCount)
        {
            int seat = FindOpenSeat();
            if (seat == -1) break;
            if (!AddBotAtSeatUnsafe(seat)) break;
            current++;
            changed = true;
        }
        while (current > targetBotCount)
        {
            var bot = _players.LastOrDefault(p => p.IsBot);
            if (bot is null) break;
            _players.Remove(bot);
            current--;
            changed = true;
        }
        return changed;
    }

    private void BroadcastSnapshots()
    {
        List<(WebClientConnection Client, TableSnapshot Snap)> toSend = new();
        lock (_lock)
        {
            _stateVersion++;
            foreach (var c in _clients.Values)
            {
                toSend.Add((c, BuildSnapshotUnsafe(c.PlayerId)));
            }
        }

        foreach (var (client, snap) in toSend)
        {
            SendJsonToClient(client, new
            {
                type = "snapshot",
                roomCode = RoomCode,
                isPrivate = IsPrivate,
                snapshot = snap
            });
        }
    }

    private TableSnapshot BuildSnapshotUnsafe(string viewerPlayerId)
    {
        bool revealAllHoldem = Holdem.Stage == HoldemStage.Showdown &&
                               _players.Count(p => !p.SittingOut && !p.Folded) > 1;
        bool revealGutsIn = Config.Mode == GameMode.GutsPoker && Guts.Stage == GutsStage.Showdown;
        bool hideDealerHole = Blackjack.Stage == BlackjackStage.PlayerTurns;

        var viewer = _players.FirstOrDefault(p => p.PlayerId == viewerPlayerId);

        var dealerCards = new List<Card>();
        for (int i = 0; i < Blackjack.DealerCards.Count; i++)
            dealerCards.Add(hideDealerHole && i == 1 ? Card.Hidden : Blackjack.DealerCards[i]);
        int visibleDealerTotal = hideDealerHole && Blackjack.DealerCards.Count > 0
            ? BlackjackEngine.CardPointValue(Blackjack.DealerCards[0])
            : Blackjack.DealerTotal;

        var snap = new TableSnapshot
        {
            Phase = Phase,
            Config = new GameConfig
            {
                Mode = Config.Mode,
                StartingChips = Config.StartingChips,
                SmallBlind = Config.SmallBlind,
                BigBlind = Config.BigBlind,
                MaxPlayers = Config.MaxPlayers,
                RoomName = Config.RoomName
            },
            ViewerPlayerId = viewerPlayerId,
            ViewerSeatIndex = viewer?.SeatIndex ?? -1,
            StateVersion = _stateVersion,
            ActionLog = _actionLog.TakeLast(12).ToList(),
            Holdem = new HoldemSnapshot
            {
                Stage = Holdem.Stage,
                HandNumber = Holdem.HandNumber,
                DealerSeat = Holdem.DealerSeat,
                SmallBlindSeat = Holdem.SmallBlindSeat,
                BigBlindSeat = Holdem.BigBlindSeat,
                CurrentTurnSeat = Holdem.CurrentTurnSeat,
                CurrentBet = Holdem.CurrentBet,
                MinRaise = Holdem.MinRaise,
                TotalPot = Holdem.TotalPot(_players),
                CommunityCards = new List<Card>(Holdem.CommunityCards),
                SidePots = HoldemEngine.BuildSidePots(_players),
                WinnerSeats = new List<int>(Holdem.WinnerSeats),
                BannerMessage = Holdem.BannerMessage
            },
            Baccarat = new BaccaratSnapshot
            {
                Stage = Baccarat.Stage,
                RoundNumber = Baccarat.RoundNumber,
                ShoeRemaining = Baccarat.ShoeRemaining,
                PlayerCards = new List<Card>(Baccarat.PlayerCards),
                BankerCards = new List<Card>(Baccarat.BankerCards),
                PlayerScore = Baccarat.PlayerScore,
                BankerScore = Baccarat.BankerScore,
                LastOutcome = Baccarat.LastOutcome,
                WasNatural = Baccarat.WasNatural,
                WasPlayerPair = Baccarat.WasPlayerPair,
                WasBankerPair = Baccarat.WasBankerPair,
                BannerMessage = Baccarat.BannerMessage,
                History = new List<BaccaratHistoryEntry>(Baccarat.History)
            },
            Guts = new GutsSnapshot
            {
                Stage = Guts.Stage,
                RoundNumber = Guts.RoundNumber,
                Pot = Guts.Pot,
                Ante = Guts.Ante,
                WinnerSeats = new List<int>(Guts.WinnerSeats),
                BannerMessage = Guts.BannerMessage
            },
            Blackjack = new BlackjackSnapshot
            {
                Stage = Blackjack.Stage,
                RoundNumber = Blackjack.RoundNumber,
                ShoeRemaining = Blackjack.ShoeRemaining,
                CurrentTurnSeat = Blackjack.CurrentTurnSeat,
                DealerCards = dealerCards,
                DealerTotal = visibleDealerTotal,
                DealerBlackjack = !hideDealerHole && Blackjack.DealerBlackjack,
                DealerBusted = !hideDealerHole && Blackjack.DealerBusted,
                BannerMessage = Blackjack.BannerMessage
            }
        };

        foreach (var p in _players.OrderBy(x => x.SeatIndex))
        {
            bool showHole = Config.Mode == GameMode.GutsPoker
                ? (p.PlayerId == viewerPlayerId || (revealGutsIn && p.GutsChoice == GutsDecision.In))
                : (p.PlayerId == viewerPlayerId || (revealAllHoldem && !p.Folded));
            var hole = showHole
                ? new List<Card>(p.HoleCards)
                : p.HoleCards.Select(_ => Card.Hidden).ToList();

            var visibleGutsChoice = (Config.Mode == GameMode.GutsPoker && Guts.Stage == GutsStage.Deciding && p.PlayerId != viewerPlayerId)
                ? (p.GutsChoice == GutsDecision.Undecided ? GutsDecision.Undecided : GutsDecision.Out)
                : p.GutsChoice;

            snap.Seats.Add(new SeatSnapshot
            {
                SeatIndex = p.SeatIndex,
                PlayerId = p.PlayerId,
                Name = p.Name,
                IsHost = p.IsHost,
                IsBot = p.IsBot,
                IsConnected = p.IsConnected,
                IsReady = p.IsReady,
                Chips = p.Chips,
                CurrentBet = p.CurrentBet,
                TotalCommitted = p.TotalCommitted,
                Folded = p.Folded,
                IsAllIn = p.IsAllIn,
                SittingOut = p.SittingOut,
                HoleCards = hole,
                LastAction = p.LastAction,
                ShowdownHandName = p.ShowdownHandName,
                LastWinAmount = p.LastWinAmount,
                BaccaratBets = p.BaccaratBets.Clone(),
                LastBaccaratNet = p.LastBaccaratNet,
                GutsChoice = visibleGutsChoice,
                GutsNet = p.GutsNet,
                BlackjackBet = p.BlackjackBet,
                BlackjackLocked = p.BlackjackLocked,
                BlackjackDoubled = p.BlackjackDoubled,
                BlackjackStood = p.BlackjackStood,
                BlackjackBusted = p.BlackjackBusted,
                BlackjackNatural = p.BlackjackNatural,
                BlackjackCards = new List<Card>(p.BlackjackCards),
                BlackjackTotal = p.BlackjackTotal,
                BlackjackNet = p.BlackjackNet,
                LastChatMessage = p.LastChatMessage,
                LastChatPitch = p.LastChatPitch,
                LastChatId = p.LastChatId,
                LastChatUnixMs = p.LastChatUnixMs
            });
        }

        return snap;
    }

    private static void SendJsonToClient(WebClientConnection client, object payload)
    {
        string json = JsonSerializer.Serialize(payload, JsonOpts);
        client.Outbox.Writer.TryWrite(json);
    }

    private int FindOpenSeat()
    {
        for (int i = 0; i < Config.MaxPlayers; i++)
        {
            if (_players.All(p => p.SeatIndex != i))
                return i;
        }
        return -1;
    }

    private void AddLog(string msg)
    {
        _actionLog.Add(msg);
        if (_actionLog.Count > 50)
            _actionLog.RemoveAt(0);
    }
}
