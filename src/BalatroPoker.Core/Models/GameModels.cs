using BalatroPoker.Core.Cards;

namespace BalatroPoker.Core.Models;

public enum GameMode
{
    TexasHoldem = 0,
    Baccarat = 1,
    OmahaHoldem = 2,
    GutsPoker = 3,
    Blackjack = 4
}

public enum GutsStage
{
    Deciding = 0,
    Showdown = 1
}

public enum GutsDecision
{
    Undecided = 0,
    In = 1,
    Out = 2
}

public enum BlackjackStage
{
    Betting = 0,
    PlayerTurns = 1,
    Settled = 2
}

public enum BlackjackActionType
{
    Hit = 0,
    Stand = 1,
    DoubleDown = 2
}

public enum RoomPhase
{
    Lobby = 0,
    InGame = 1
}

public enum HoldemStage
{
    Waiting = 0,
    PreFlop = 1,
    Flop = 2,
    Turn = 3,
    River = 4,
    Showdown = 5
}

public enum HoldemActionType
{
    Fold = 0,
    Check = 1,
    Call = 2,
    Bet = 3,
    Raise = 4,
    AllIn = 5
}

public enum BaccaratStage
{
    Betting = 0,
    Dealing = 1,
    Settled = 2
}

public enum BaccaratBetType
{
    Player = 0,
    Banker = 1,
    Tie = 2,
    PlayerPair = 3,
    BankerPair = 4
}

public enum BaccaratOutcome
{
    PlayerWin = 0,
    BankerWin = 1,
    Tie = 2
}

public sealed class GameConfig
{
    public GameMode Mode { get; set; } = GameMode.TexasHoldem;
    public int StartingChips { get; set; } = 1000;
    public int SmallBlind { get; set; } = 10;
    public int BigBlind { get; set; } = 20;
    public int MaxPlayers { get; set; } = 10;
    public string RoomName { get; set; } = "UNTITLED POKER ROOM";
}

public sealed class BaccaratPlayerBets
{
    public int Player { get; set; }
    public int Banker { get; set; }
    public int Tie { get; set; }
    public int PlayerPair { get; set; }
    public int BankerPair { get; set; }
    public bool LockedIn { get; set; }

    public int Total => Player + Banker + Tie + PlayerPair + BankerPair;

    public void Clear()
    {
        Player = 0;
        Banker = 0;
        Tie = 0;
        PlayerPair = 0;
        BankerPair = 0;
        LockedIn = false;
    }

    public BaccaratPlayerBets Clone() => new()
    {
        Player = Player,
        Banker = Banker,
        Tie = Tie,
        PlayerPair = PlayerPair,
        BankerPair = BankerPair,
        LockedIn = LockedIn
    };
}

public sealed class SeatSnapshot
{
    public int SeatIndex { get; set; }
    public string PlayerId { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public bool IsHost { get; set; }
    public bool IsBot { get; set; }
    public bool IsConnected { get; set; } = true;
    public bool IsReady { get; set; }
    public int Chips { get; set; }

    // Texas Hold'em fields
    public int CurrentBet { get; set; }
    public int TotalCommitted { get; set; }
    public bool Folded { get; set; }
    public bool IsAllIn { get; set; }
    public bool SittingOut { get; set; }
    public List<Card> HoleCards { get; set; } = new();
    public string LastAction { get; set; } = string.Empty;
    public string ShowdownHandName { get; set; } = string.Empty;
    public int LastWinAmount { get; set; }

    // Baccarat fields
    public BaccaratPlayerBets BaccaratBets { get; set; } = new();
    public int LastBaccaratNet { get; set; }

    // Guts Poker fields
    public GutsDecision GutsChoice { get; set; } = GutsDecision.Undecided;
    public int GutsNet { get; set; }

    // Blackjack 21 fields
    public int BlackjackBet { get; set; }
    public bool BlackjackLocked { get; set; }
    public bool BlackjackDoubled { get; set; }
    public bool BlackjackStood { get; set; }
    public bool BlackjackBusted { get; set; }
    public bool BlackjackNatural { get; set; }
    public List<Card> BlackjackCards { get; set; } = new();
    public int BlackjackTotal { get; set; }
    public int BlackjackNet { get; set; }

    // SAM Voice Chat bubble fields
    public string LastChatMessage { get; set; } = string.Empty;
    public int LastChatPitch { get; set; } = 64;
    public long LastChatId { get; set; }
    public long LastChatUnixMs { get; set; }
}

public sealed class PotSnapshot
{
    public int Amount { get; set; }
    public List<int> EligibleSeats { get; set; } = new();
}

public sealed class BaccaratHistoryEntry
{
    public BaccaratOutcome Outcome { get; set; }
    public int PlayerScore { get; set; }
    public int BankerScore { get; set; }
    public bool PlayerPair { get; set; }
    public bool BankerPair { get; set; }
    public bool Natural { get; set; }
}

public sealed class HoldemSnapshot
{
    public HoldemStage Stage { get; set; } = HoldemStage.Waiting;
    public int HandNumber { get; set; }
    public int DealerSeat { get; set; } = -1;
    public int SmallBlindSeat { get; set; } = -1;
    public int BigBlindSeat { get; set; } = -1;
    public int CurrentTurnSeat { get; set; } = -1;
    public int CurrentBet { get; set; }
    public int MinRaise { get; set; }
    public int TotalPot { get; set; }
    public List<Card> CommunityCards { get; set; } = new();
    public List<PotSnapshot> SidePots { get; set; } = new();
    public List<int> WinnerSeats { get; set; } = new();
    public string BannerMessage { get; set; } = string.Empty;
}

public sealed class BaccaratSnapshot
{
    public BaccaratStage Stage { get; set; } = BaccaratStage.Betting;
    public int RoundNumber { get; set; }
    public int ShoeRemaining { get; set; } = 416;
    public List<Card> PlayerCards { get; set; } = new();
    public List<Card> BankerCards { get; set; } = new();
    public int PlayerScore { get; set; }
    public int BankerScore { get; set; }
    public BaccaratOutcome? LastOutcome { get; set; }
    public bool WasNatural { get; set; }
    public bool WasPlayerPair { get; set; }
    public bool WasBankerPair { get; set; }
    public string BannerMessage { get; set; } = "PLACE YOUR BETS";
    public List<BaccaratHistoryEntry> History { get; set; } = new();
}

public sealed class GutsSnapshot
{
    public GutsStage Stage { get; set; } = GutsStage.Deciding;
    public int RoundNumber { get; set; }
    public int Pot { get; set; }
    public int Ante { get; set; } = 20;
    public List<int> WinnerSeats { get; set; } = new();
    public string BannerMessage { get; set; } = "DECIDE: IN OR OUT";
}

public sealed class BlackjackSnapshot
{
    public BlackjackStage Stage { get; set; } = BlackjackStage.Betting;
    public int RoundNumber { get; set; }
    public int ShoeRemaining { get; set; } = 312;
    public int CurrentTurnSeat { get; set; } = -1;
    public List<Card> DealerCards { get; set; } = new();
    public int DealerTotal { get; set; }
    public bool DealerBlackjack { get; set; }
    public bool DealerBusted { get; set; }
    public string BannerMessage { get; set; } = "PLACE YOUR BLACKJACK BETS";
}

public sealed class TableSnapshot
{
    public RoomPhase Phase { get; set; } = RoomPhase.Lobby;
    public GameConfig Config { get; set; } = new();
    public string ViewerPlayerId { get; set; } = string.Empty;
    public int ViewerSeatIndex { get; set; } = -1;
    public List<SeatSnapshot> Seats { get; set; } = new();
    public HoldemSnapshot Holdem { get; set; } = new();
    public BaccaratSnapshot Baccarat { get; set; } = new();
    public GutsSnapshot Guts { get; set; } = new();
    public BlackjackSnapshot Blackjack { get; set; } = new();
    public List<string> ActionLog { get; set; } = new();
    public long StateVersion { get; set; }
}
