using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Threading.Channels;
using BalatroPoker.Core.Baccarat;
using BalatroPoker.Core.Blackjack;
using BalatroPoker.Core.Cards;
using BalatroPoker.Core.Guts;
using BalatroPoker.Core.Holdem;
using BalatroPoker.Core.Models;

namespace BalatroPoker.Core.Networking;

public sealed class GameRoomHost : IDisposable
{
    private sealed class ConnectedPeer
    {
        public string PlayerId { get; init; } = string.Empty;
        public int SeatIndex { get; init; }
        public TcpClient Tcp { get; init; } = null!;
        public NetworkStream Stream { get; init; } = null!;
        public Channel<NetworkPacket> Outbox { get; } = Channel.CreateUnbounded<NetworkPacket>(
            new UnboundedChannelOptions { SingleReader = true });
    }

    private readonly object _stateLock = new();
    private readonly List<HoldemPlayer> _players = new();
    private readonly List<string> _actionLog = new();
    private readonly ConcurrentDictionary<string, ConnectedPeer> _peers = new();
    private readonly Random _rng = new();

    private TcpListener? _listener;
    private CancellationTokenSource? _cts;
    private long _stateVersion;
    private long _chatSeq;
    private int _botCounter;
    private DateTime _nextBotActionUtc = DateTime.MinValue;

    public GameConfig Config { get; } = new();
    public RoomPhase Phase { get; private set; } = RoomPhase.Lobby;
    public HoldemEngine Holdem { get; }
    public BaccaratEngine Baccarat { get; }
    public GutsEngine Guts { get; }
    public BlackjackEngine Blackjack { get; }
    public string HostPlayerId { get; }
    public int HostSeatIndex { get; } = 0;
    public int Port { get; private set; } = PacketFraming.DefaultGamePort;
    public bool IsRunning { get; private set; }
    public bool EnableLanBroadcast { get; set; } = true;

    public GameRoomHost(string hostName, GameConfig? config = null, Random? deterministicRng = null)
    {
        if (config is not null)
        {
            Config.Mode = config.Mode;
            Config.StartingChips = config.StartingChips;
            Config.SmallBlind = config.SmallBlind;
            Config.BigBlind = config.BigBlind;
            Config.MaxPlayers = Math.Clamp(config.MaxPlayers, 2, 10);
            Config.RoomName = config.RoomName;
        }

        Holdem = new HoldemEngine(AddLog, deterministicRng);
        Baccarat = new BaccaratEngine(AddLog, deterministicRng);
        Guts = new GutsEngine(AddLog, deterministicRng);
        Blackjack = new BlackjackEngine(AddLog, deterministicRng);

        HostPlayerId = Guid.NewGuid().ToString("N")[..12];
        _players.Add(new HoldemPlayer
        {
            SeatIndex = 0,
            PlayerId = HostPlayerId,
            Name = string.IsNullOrWhiteSpace(hostName) ? "HOST" : hostName.Trim().ToUpperInvariant(),
            IsHost = true,
            IsConnected = true,
            IsReady = true,
            Chips = Config.StartingChips
        });
        AddLog($"{_players[0].Name} created room '{Config.RoomName}'.");
    }

    public void Start(int port = PacketFraming.DefaultGamePort)
    {
        if (IsRunning) return;
        Port = port;
        _cts = new CancellationTokenSource();
        _listener = new TcpListener(IPAddress.Any, port);
        _listener.Start();
        Port = ((IPEndPoint)_listener.LocalEndpoint).Port;
        IsRunning = true;

        AddLog($"P2P Host listening on port {Port} (up to {Config.MaxPlayers} players).");
        _ = Task.Run(() => AcceptLoopAsync(_cts.Token));
        if (EnableLanBroadcast)
            _ = Task.Run(() => BroadcastLoopAsync(_cts.Token));
    }

    public void TickBots(bool forceImmediate = false)
    {
        bool changed = false;
        lock (_stateLock)
        {
            if (Phase != RoomPhase.InGame) return;
            if (!forceImmediate && DateTime.UtcNow < _nextBotActionUtc) return;

            if (Config.Mode is GameMode.TexasHoldem or GameMode.OmahaHoldem)
            {
                if (Holdem.Stage is HoldemStage.PreFlop or HoldemStage.Flop or HoldemStage.Turn or HoldemStage.River)
                {
                    var current = _players.FirstOrDefault(p => p.SeatIndex == Holdem.CurrentTurnSeat);
                    if (current is not null && current.IsBot)
                    {
                        var (action, amount) = Holdem.DecideBotAction(_players, current, Config, _rng);
                        Holdem.ApplyAction(_players, current.SeatIndex, action, amount, Config);
                        _nextBotActionUtc = DateTime.UtcNow.AddMilliseconds(550);
                        changed = true;
                    }
                }
            }
            else if (Config.Mode == GameMode.Baccarat)
            {
                if (Baccarat.Stage == BaccaratStage.Betting)
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
            }
            else if (Config.Mode == GameMode.GutsPoker)
            {
                if (Guts.Stage == GutsStage.Deciding)
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
                        var act = Blackjack.DecideBotAction(current);
                        Blackjack.ApplyAction(_players, current.SeatIndex, act);
                        _nextBotActionUtc = DateTime.UtcNow.AddMilliseconds(550);
                        changed = true;
                    }
                }
            }
        }

        if (changed) BroadcastSnapshots();
    }

    public static string ModeDisplayName(GameMode mode) => mode switch
    {
        GameMode.TexasHoldem => "TEXAS HOLD'EM",
        GameMode.OmahaHoldem => "OMAHA HOLD'EM",
        GameMode.Baccarat => "BACCARAT",
        GameMode.GutsPoker => "GUTS POKER",
        GameMode.Blackjack => "21 / BLACKJACK",
        _ => mode.ToString().ToUpperInvariant()
    };

    public void SwitchGameMode(GameMode mode)
    {
        lock (_stateLock)
        {
            Config.Mode = mode;
            AddLog($"Game mode switched to {ModeDisplayName(mode)}.");
            if (Phase == RoomPhase.InGame)
            {
                StartCurrentModeUnsafe();
            }
        }
        BroadcastSnapshots();
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

    public void SetBlinds(int smallBlind, int bigBlind)
    {
        lock (_stateLock)
        {
            Config.SmallBlind = Math.Max(5, smallBlind);
            Config.BigBlind = Math.Max(Config.SmallBlind * 2, bigBlind);
            AddLog($"Blinds updated to ${Config.SmallBlind}/${Config.BigBlind}.");
        }
        BroadcastSnapshots();
    }

    public bool AddBot(string? customName = null)
    {
        int seat;
        lock (_stateLock)
        {
            seat = FindOpenSeat();
        }
        return seat != -1 && AddBotAtSeat(seat, customName);
    }

    public bool AddBotAtSeat(int seatIndex, string? customName = null)
    {
        lock (_stateLock)
        {
            if (seatIndex < 0 || seatIndex >= Config.MaxPlayers) return false;
            if (_players.Any(p => p.SeatIndex == seatIndex)) return false;

            _botCounter++;
            string[] botNames = { "JIMBO", "GROS MICHEL", "CAVENDISH", "BLUEPRINT", "BRAINSTORM", "PERKEO", "CANIO", "TRIBOULET", "YORICK", "CHICOT" };
            string name = customName ?? botNames[(_botCounter - 1) % botNames.Length];

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
            AddLog($"AI Bot {name} joined seat {seatIndex + 1}.");
        }
        BroadcastSnapshots();
        return true;
    }

    public bool RemoveBot()
    {
        int seat = -1;
        lock (_stateLock)
        {
            var bot = _players.LastOrDefault(p => p.IsBot);
            if (bot is not null) seat = bot.SeatIndex;
        }
        return seat != -1 && RemoveBotAtSeat(seat);
    }

    public bool RemoveBotAtSeat(int seatIndex)
    {
        lock (_stateLock)
        {
            var bot = _players.FirstOrDefault(p => p.SeatIndex == seatIndex && p.IsBot);
            if (bot is null) return false;
            _players.Remove(bot);
            AddLog($"AI Bot {bot.Name} removed from seat {seatIndex + 1}.");
        }
        BroadcastSnapshots();
        return true;
    }

    public void SetBotCount(int targetBotCount)
    {
        targetBotCount = Math.Clamp(targetBotCount, 0, Config.MaxPlayers - 1);
        bool changed = false;
        lock (_stateLock)
        {
            int currentBots = _players.Count(p => p.IsBot);
            while (currentBots < targetBotCount)
            {
                int seat = FindOpenSeat();
                if (seat == -1) break;
                _botCounter++;
                string[] botNames = { "JIMBO", "GROS MICHEL", "CAVENDISH", "BLUEPRINT", "BRAINSTORM", "PERKEO", "CANIO", "TRIBOULET", "YORICK", "CHICOT" };
                string name = botNames[(_botCounter - 1) % botNames.Length];
                _players.Add(new HoldemPlayer
                {
                    SeatIndex = seat,
                    PlayerId = $"bot-{_botCounter}",
                    Name = name,
                    IsBot = true,
                    IsConnected = true,
                    IsReady = true,
                    Chips = Config.StartingChips
                });
                currentBots++;
                changed = true;
            }
            while (currentBots > targetBotCount)
            {
                var bot = _players.LastOrDefault(p => p.IsBot);
                if (bot is null) break;
                _players.Remove(bot);
                currentBots--;
                changed = true;
            }
            if (changed)
                AddLog($"AI Bots set to {currentBots}.");
        }
        if (changed) BroadcastSnapshots();
    }

    public bool StartGame()
    {
        lock (_stateLock)
        {
            bool needsTwo = Config.Mode is GameMode.TexasHoldem or GameMode.OmahaHoldem or GameMode.GutsPoker;
            if (needsTwo && _players.Count(p => p.IsConnected && p.Chips > 0) < 2)
            {
                AddLog($"Need at least 2 players (or add a bot) to start {ModeDisplayName(Config.Mode)}.");
                return false;
            }

            Phase = RoomPhase.InGame;
            foreach (var p in _players)
            {
                if (p.Chips <= 0) p.Chips = Config.StartingChips;
            }

            StartCurrentModeUnsafe();
            _nextBotActionUtc = DateTime.UtcNow.AddMilliseconds(600);
        }
        BroadcastSnapshots();
        return true;
    }

    public void ReturnToLobby()
    {
        lock (_stateLock)
        {
            Phase = RoomPhase.Lobby;
            AddLog("Returned to lobby.");
        }
        BroadcastSnapshots();
    }

    public void NextHandOrRound()
    {
        lock (_stateLock)
        {
            if (Phase != RoomPhase.InGame) return;

            if (Config.Mode is GameMode.TexasHoldem or GameMode.OmahaHoldem)
            {
                foreach (var bot in _players.Where(p => p.IsBot && p.Chips <= 0))
                {
                    bot.Chips = Config.StartingChips;
                    AddLog($"{bot.Name} rebought for ${Config.StartingChips}.");
                }
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
        }
        BroadcastSnapshots();
    }

    public (bool Ok, string Error) HandleHoldemAction(string playerId, HoldemActionType action, int raiseToAmount = 0)
    {
        (bool Ok, string Error) res;
        lock (_stateLock)
        {
            var p = _players.FirstOrDefault(x => x.PlayerId == playerId);
            if (p is null) return (false, "Player not found.");
            res = Holdem.ApplyAction(_players, p.SeatIndex, action, raiseToAmount, Config);
            _nextBotActionUtc = DateTime.UtcNow.AddMilliseconds(550);
        }
        if (res.Ok) BroadcastSnapshots();
        return res;
    }

    public (bool Ok, string Error) HandleBaccaratBet(string playerId, BaccaratBetType betType, int amount)
    {
        (bool Ok, string Error) res;
        lock (_stateLock)
        {
            var p = _players.FirstOrDefault(x => x.PlayerId == playerId);
            if (p is null) return (false, "Player not found.");
            res = Baccarat.PlaceBet(p, betType, amount);
        }
        if (res.Ok) BroadcastSnapshots();
        return res;
    }

    public void HandleBaccaratClear(string playerId)
    {
        lock (_stateLock)
        {
            var p = _players.FirstOrDefault(x => x.PlayerId == playerId);
            if (p is not null) Baccarat.ClearBets(p);
        }
        BroadcastSnapshots();
    }

    public void HandleBaccaratLock(string playerId)
    {
        lock (_stateLock)
        {
            var p = _players.FirstOrDefault(x => x.PlayerId == playerId);
            if (p is not null)
            {
                Baccarat.LockBets(p);
                Baccarat.PlaceBotBets(_players, _rng);
                if (Baccarat.AreAllPlayersLocked(_players))
                {
                    Baccarat.DealAndSettle(_players);
                }
            }
        }
        BroadcastSnapshots();
    }

    public void HandleGutsDecision(string playerId, GutsDecision choice)
    {
        bool changed = false;
        lock (_stateLock)
        {
            var p = _players.FirstOrDefault(x => x.PlayerId == playerId);
            if (p is not null && Guts.SubmitDecision(_players, p.SeatIndex, choice).Ok)
            {
                Guts.DecideBots(_players, _rng);
                if (Guts.AreAllDecided(_players))
                    Guts.ResolveShowdown(_players);
                changed = true;
            }
        }
        if (changed) BroadcastSnapshots();
    }

    public void HandleBlackjackBet(string playerId, int amount)
    {
        bool changed = false;
        lock (_stateLock)
        {
            var p = _players.FirstOrDefault(x => x.PlayerId == playerId);
            if (p is not null && Blackjack.PlaceBet(p, amount).Ok)
                changed = true;
        }
        if (changed) BroadcastSnapshots();
    }

    public void HandleBlackjackClear(string playerId)
    {
        lock (_stateLock)
        {
            var p = _players.FirstOrDefault(x => x.PlayerId == playerId);
            if (p is not null) Blackjack.ClearBet(p);
        }
        BroadcastSnapshots();
    }

    public void HandleBlackjackLock(string playerId)
    {
        lock (_stateLock)
        {
            var p = _players.FirstOrDefault(x => x.PlayerId == playerId);
            if (p is not null)
            {
                Blackjack.LockBet(p);
                Blackjack.PlaceBotBets(_players, _rng);
                if (Blackjack.AreAllLocked(_players))
                {
                    Blackjack.DealInitial(_players);
                    _nextBotActionUtc = DateTime.UtcNow.AddMilliseconds(550);
                }
            }
        }
        BroadcastSnapshots();
    }

    public void HandleBlackjackAction(string playerId, BlackjackActionType action)
    {
        bool changed = false;
        lock (_stateLock)
        {
            var p = _players.FirstOrDefault(x => x.PlayerId == playerId);
            if (p is not null && Blackjack.ApplyAction(_players, p.SeatIndex, action).Ok)
            {
                _nextBotActionUtc = DateTime.UtcNow.AddMilliseconds(550);
                changed = true;
            }
        }
        if (changed) BroadcastSnapshots();
    }

    public void HandleRebuy(string playerId)
    {
        lock (_stateLock)
        {
            var p = _players.FirstOrDefault(x => x.PlayerId == playerId);
            if (p is not null && p.Chips < Config.StartingChips)
            {
                p.Chips = Config.StartingChips;
                AddLog($"{p.Name} rebought to ${Config.StartingChips}.");
            }
        }
        BroadcastSnapshots();
    }

    public void HandleChat(string playerId, string text, int pitch = 64)
    {
        if (string.IsNullOrWhiteSpace(text)) return;
        string clean = text.Trim();
        if (clean.Length > 64) clean = clean[..64];
        int clampedPitch = Math.Clamp(pitch <= 0 ? 64 : pitch, 25, 130);

        lock (_stateLock)
        {
            var p = _players.FirstOrDefault(x => x.PlayerId == playerId);
            string sender = p?.Name ?? "PLAYER";
            if (p is not null)
            {
                p.LastChatMessage = clean;
                p.LastChatPitch = clampedPitch;
                p.LastChatId = ++_chatSeq;
                p.LastChatUnixMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            }
            AddLog($"{sender}: {clean}");
        }
        BroadcastSnapshots();
    }

    public TableSnapshot BuildSnapshotFor(string viewerPlayerId)
    {
        lock (_stateLock)
        {
            return BuildSnapshotUnsafe(viewerPlayerId);
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
        {
            dealerCards.Add(hideDealerHole && i == 1 ? Card.Hidden : Blackjack.DealerCards[i]);
        }
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
            ActionLog = _actionLog.TakeLast(10).ToList(),
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
                ? (p.GutsChoice == GutsDecision.Undecided ? GutsDecision.Undecided : GutsDecision.Out) // hidden until reveal
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

    private void BroadcastSnapshots()
    {
        List<(ConnectedPeer Peer, TableSnapshot Snap)> toSend = new();
        lock (_stateLock)
        {
            _stateVersion++;
            foreach (var peer in _peers.Values)
            {
                toSend.Add((peer, BuildSnapshotUnsafe(peer.PlayerId)));
            }
        }

        foreach (var (peer, snap) in toSend)
        {
            peer.Outbox.Writer.TryWrite(new NetworkPacket
            {
                Type = MessageType.TableSnapshot,
                Snapshot = snap
            });
        }
    }

    private static async Task PeerWriteLoopAsync(ConnectedPeer peer, CancellationToken ct)
    {
        try
        {
            await foreach (var packet in peer.Outbox.Reader.ReadAllAsync(ct).ConfigureAwait(false))
            {
                await PacketFraming.SendAsync(peer.Stream, packet, ct).ConfigureAwait(false);
            }
        }
        catch
        {
            // Peer disconnect handled in read loop
        }
    }

    private async Task AcceptLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested && _listener is not null)
        {
            try
            {
                TcpClient client = await _listener.AcceptTcpClientAsync(ct).ConfigureAwait(false);
                client.NoDelay = true;
                _ = Task.Run(() => HandleClientAsync(client, ct), ct);
            }
            catch
            {
                if (ct.IsCancellationRequested) break;
            }
        }
    }

    private async Task HandleClientAsync(TcpClient tcp, CancellationToken ct)
    {
        ConnectedPeer? peer = null;
        try
        {
            using var stream = tcp.GetStream();
            var hello = await PacketFraming.ReceiveAsync(stream, ct).ConfigureAwait(false);
            if (hello is null || hello.Type != MessageType.JoinRequest)
            {
                tcp.Close();
                return;
            }

            string playerId = Guid.NewGuid().ToString("N")[..12];
            int seat;
            string cleanName = string.IsNullOrWhiteSpace(hello.PlayerName)
                ? "PLAYER"
                : hello.PlayerName.Trim().ToUpperInvariant();
            if (cleanName.Length > 14) cleanName = cleanName[..14];

            lock (_stateLock)
            {
                seat = FindOpenSeat();
                if (seat == -1)
                {
                    _ = PacketFraming.SendAsync(stream, new NetworkPacket
                    {
                        Type = MessageType.JoinRejected,
                        Text = $"Room is full ({Config.MaxPlayers}/{Config.MaxPlayers})."
                    }, ct);
                    return;
                }

                _players.Add(new HoldemPlayer
                {
                    SeatIndex = seat,
                    PlayerId = playerId,
                    Name = cleanName,
                    IsHost = false,
                    IsBot = false,
                    IsConnected = true,
                    IsReady = true,
                    Chips = Config.StartingChips,
                    SittingOut = Phase == RoomPhase.InGame && Config.Mode == GameMode.TexasHoldem
                });
                AddLog($"{cleanName} joined seat {seat + 1}.");
            }

            peer = new ConnectedPeer
            {
                PlayerId = playerId,
                SeatIndex = seat,
                Tcp = tcp,
                Stream = stream
            };

            peer.Outbox.Writer.TryWrite(new NetworkPacket
            {
                Type = MessageType.JoinAccepted,
                PlayerId = playerId,
                SeatIndex = seat,
                Text = Config.RoomName
            });

            _peers[playerId] = peer;
            _ = Task.Run(() => PeerWriteLoopAsync(peer, ct), ct);

            BroadcastSnapshots();

            while (!ct.IsCancellationRequested)
            {
                var pkt = await PacketFraming.ReceiveAsync(stream, ct).ConfigureAwait(false);
                if (pkt is null || pkt.Type == MessageType.Leave) break;
                ProcessClientPacket(playerId, pkt);
            }
        }
        catch
        {
            // Disconnected
        }
        finally
        {
            if (peer is not null)
            {
                peer.Outbox.Writer.TryComplete();
                _peers.TryRemove(peer.PlayerId, out _);
                lock (_stateLock)
                {
                    var p = _players.FirstOrDefault(x => x.PlayerId == peer.PlayerId);
                    if (p is not null)
                    {
                        AddLog($"{p.Name} disconnected.");
                        if (Phase == RoomPhase.InGame && Config.Mode == GameMode.TexasHoldem && Holdem.CurrentTurnSeat == p.SeatIndex)
                        {
                            Holdem.ApplyAction(_players, p.SeatIndex, HoldemActionType.Fold, 0, Config);
                        }
                        _players.Remove(p);
                    }
                }
                BroadcastSnapshots();
            }
            try { tcp.Close(); } catch { }
        }
    }

    private void ProcessClientPacket(string playerId, NetworkPacket pkt)
    {
        switch (pkt.Type)
        {
            case MessageType.HoldemAction:
                HandleHoldemAction(playerId, pkt.HoldemAction, pkt.Amount);
                break;
            case MessageType.BaccaratBet:
                HandleBaccaratBet(playerId, pkt.BaccaratBetType, pkt.Amount);
                break;
            case MessageType.BaccaratClear:
                HandleBaccaratClear(playerId);
                break;
            case MessageType.BaccaratLock:
                HandleBaccaratLock(playerId);
                break;
            case MessageType.GutsDecision:
                HandleGutsDecision(playerId, pkt.GutsChoice);
                break;
            case MessageType.BlackjackBet:
                HandleBlackjackBet(playerId, pkt.Amount);
                break;
            case MessageType.BlackjackClear:
                HandleBlackjackClear(playerId);
                break;
            case MessageType.BlackjackLock:
                HandleBlackjackLock(playerId);
                break;
            case MessageType.BlackjackAction:
                HandleBlackjackAction(playerId, pkt.BlackjackAction);
                break;
            case MessageType.RebuyRequest:
                HandleRebuy(playerId);
                break;
            case MessageType.ChatMessage:
                HandleChat(playerId, pkt.Text ?? string.Empty, pkt.Amount > 0 ? pkt.Amount : 64);
                break;
            case MessageType.ReadyToggle:
                lock (_stateLock)
                {
                    var p = _players.FirstOrDefault(x => x.PlayerId == playerId);
                    if (p is not null) p.IsReady = !p.IsReady;
                }
                BroadcastSnapshots();
                break;
        }
    }

    private async Task BroadcastLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            LanRoomAdvertisement adv;
            lock (_stateLock)
            {
                adv = new LanRoomAdvertisement
                {
                    RoomName = Config.RoomName,
                    Port = Port,
                    Mode = Config.Mode,
                    PlayerCount = _players.Count,
                    MaxPlayers = Config.MaxPlayers
                };
            }
            await LanDiscoveryService.BroadcastOnceAsync(adv).ConfigureAwait(false);
            try { await Task.Delay(2000, ct).ConfigureAwait(false); } catch { break; }
        }
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

    public void Dispose()
    {
        IsRunning = false;
        try { _cts?.Cancel(); } catch { }
        try { _listener?.Stop(); } catch { }
        foreach (var peer in _peers.Values)
        {
            try { peer.Tcp.Close(); } catch { }
        }
        _peers.Clear();
        _cts?.Dispose();
    }
}
