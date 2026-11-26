using BalatroPoker.Core.Cards;
using BalatroPoker.Core.Holdem;
using BalatroPoker.Core.Models;

namespace BalatroPoker.Core.Baccarat;

public sealed class BaccaratEngine
{
    private readonly Deck _shoe;
    private readonly Action<string> _log;

    public BaccaratStage Stage { get; private set; } = BaccaratStage.Betting;
    public int RoundNumber { get; private set; }
    public int ShoeRemaining => _shoe.Remaining;
    public List<Card> PlayerCards { get; } = new();
    public List<Card> BankerCards { get; } = new();
    public int PlayerScore { get; private set; }
    public int BankerScore { get; private set; }
    public BaccaratOutcome? LastOutcome { get; private set; }
    public bool WasNatural { get; private set; }
    public bool WasPlayerPair { get; private set; }
    public bool WasBankerPair { get; private set; }
    public string BannerMessage { get; private set; } = "PLACE YOUR BETS";
    public List<BaccaratHistoryEntry> History { get; } = new();

    public BaccaratEngine(Action<string>? logger = null, Random? deterministicRng = null)
    {
        // Standard 8-deck Baccarat shoe, reshuffle when <= 20 cards remain
        _shoe = new Deck(numDecks: 8, reshuffleThreshold: 20, deterministicRng: deterministicRng);
        _log = logger ?? (_ => { });
    }

    public void StartBettingPhase(IList<HoldemPlayer> players)
    {
        if (_shoe.NeedsReshuffle)
        {
            _shoe.ResetAndShuffle();
            _log("Baccarat 8-deck shoe reshuffled (416 cards).");
        }

        Stage = BaccaratStage.Betting;
        PlayerCards.Clear();
        BankerCards.Clear();
        PlayerScore = 0;
        BankerScore = 0;
        LastOutcome = null;
        WasNatural = false;
        WasPlayerPair = false;
        WasBankerPair = false;
        BannerMessage = $"ROUND #{RoundNumber + 1} - PLACE YOUR BETS";

        foreach (var p in players)
        {
            p.BaccaratBets.Clear();
            p.LastBaccaratNet = 0;
            p.LastAction = string.Empty;
        }
    }

    public (bool Ok, string Error) PlaceBet(HoldemPlayer player, BaccaratBetType betType, int amount)
    {
        if (Stage != BaccaratStage.Betting)
            return (false, "Betting is closed.");
        if (player.BaccaratBets.LockedIn)
            return (false, "Bets are already locked in.");
        if (amount <= 0)
            return (false, "Bet amount must be positive.");
        if (player.Chips < amount)
            return (false, "Not enough chips.");

        player.Chips -= amount;
        switch (betType)
        {
            case BaccaratBetType.Player:
                player.BaccaratBets.Player += amount;
                break;
            case BaccaratBetType.Banker:
                player.BaccaratBets.Banker += amount;
                break;
            case BaccaratBetType.Tie:
                player.BaccaratBets.Tie += amount;
                break;
            case BaccaratBetType.PlayerPair:
                player.BaccaratBets.PlayerPair += amount;
                break;
            case BaccaratBetType.BankerPair:
                player.BaccaratBets.BankerPair += amount;
                break;
            default:
                player.Chips += amount;
                return (false, "Invalid bet type.");
        }

        player.LastAction = $"BET ${player.BaccaratBets.Total}";
        return (true, string.Empty);
    }

    public void ClearBets(HoldemPlayer player)
    {
        if (Stage != BaccaratStage.Betting) return;
        int refund = player.BaccaratBets.Total;
        player.Chips += refund;
        player.BaccaratBets.Clear();
        player.LastAction = string.Empty;
    }

    public void LockBets(HoldemPlayer player)
    {
        if (Stage != BaccaratStage.Betting) return;
        player.BaccaratBets.LockedIn = true;
        player.LastAction = player.BaccaratBets.Total > 0
            ? $"LOCKED ${player.BaccaratBets.Total}"
            : "SITTING OUT";
    }

    public void PlaceBotBets(IList<HoldemPlayer> players, Random rng)
    {
        if (Stage != BaccaratStage.Betting) return;

        foreach (var bot in players.Where(p => p.IsBot && p.IsConnected && !p.BaccaratBets.LockedIn))
        {
            if (bot.Chips < 10)
            {
                bot.BaccaratBets.LockedIn = true;
                bot.LastAction = "NO CHIPS";
                continue;
            }

            int[] sizes = { 10, 25, 50, 100 };
            int mainBet = sizes[rng.Next(sizes.Length)];
            mainBet = Math.Min(mainBet, bot.Chips);

            int roll = rng.Next(100);
            BaccaratBetType mainType = roll < 46
                ? BaccaratBetType.Banker
                : roll < 90
                    ? BaccaratBetType.Player
                    : BaccaratBetType.Tie;

            PlaceBet(bot, mainType, mainBet);

            if (bot.Chips >= 10 && rng.NextDouble() < 0.15)
            {
                var sideType = rng.Next(2) == 0 ? BaccaratBetType.PlayerPair : BaccaratBetType.BankerPair;
                PlaceBet(bot, sideType, 10);
            }

            LockBets(bot);
        }
    }

    public bool AreAllPlayersLocked(IList<HoldemPlayer> players)
    {
        var active = players.Where(p => p.IsConnected && (p.Chips > 0 || p.BaccaratBets.Total > 0)).ToList();
        return active.Count > 0 && active.All(p => p.BaccaratBets.LockedIn);
    }

    /// <summary>
    /// Deals the Punto Banco hand using exact third-card rules and settles all player bets.
    /// </summary>
    public void DealAndSettle(IList<HoldemPlayer> players, Func<Card>? customDraw = null)
    {
        Func<Card> draw = customDraw ?? _shoe.Draw;

        RoundNumber++;
        Stage = BaccaratStage.Dealing;
        PlayerCards.Clear();
        BankerCards.Clear();

        // Standard alternating deal: Player 1, Banker 1, Player 2, Banker 2
        PlayerCards.Add(draw());
        BankerCards.Add(draw());
        PlayerCards.Add(draw());
        BankerCards.Add(draw());

        WasPlayerPair = PlayerCards[0].Rank == PlayerCards[1].Rank;
        WasBankerPair = BankerCards[0].Rank == BankerCards[1].Rank;

        PlayerScore = ComputeScore(PlayerCards);
        BankerScore = ComputeScore(BankerCards);

        // Natural 8 or 9 ends drawing immediately
        if (PlayerScore >= 8 || BankerScore >= 8)
        {
            WasNatural = true;
        }
        else
        {
            WasNatural = false;
            Card? playerThird = null;

            // Player draws on 0-5, stands on 6-7
            if (PlayerScore <= 5)
            {
                playerThird = draw();
                PlayerCards.Add(playerThird.Value);
                PlayerScore = ComputeScore(PlayerCards);
            }

            // Banker third-card tableau
            if (ShouldBankerDraw(BankerScore, playerThird))
            {
                BankerCards.Add(draw());
                BankerScore = ComputeScore(BankerCards);
            }
        }

        LastOutcome = PlayerScore > BankerScore
            ? BaccaratOutcome.PlayerWin
            : BankerScore > PlayerScore
                ? BaccaratOutcome.BankerWin
                : BaccaratOutcome.Tie;

        SettleBets(players, LastOutcome.Value);

        History.Add(new BaccaratHistoryEntry
        {
            Outcome = LastOutcome.Value,
            PlayerScore = PlayerScore,
            BankerScore = BankerScore,
            PlayerPair = WasPlayerPair,
            BankerPair = WasBankerPair,
            Natural = WasNatural
        });
        if (History.Count > 28)
            History.RemoveAt(0);

        Stage = BaccaratStage.Settled;

        string outcomeText = LastOutcome.Value switch
        {
            BaccaratOutcome.PlayerWin => $"PLAYER WINS ({PlayerScore} TO {BankerScore})",
            BaccaratOutcome.BankerWin => $"BANKER WINS ({BankerScore} TO {PlayerScore})",
            _ => $"TIE ({PlayerScore} - {BankerScore})"
        };
        if (WasNatural) outcomeText = "NATURAL! " + outcomeText;
        BannerMessage = outcomeText;

        _log($"BACCARAT #{RoundNumber}: P[{string.Join(" ", PlayerCards)}]={PlayerScore} vs B[{string.Join(" ", BankerCards)}]={BankerScore} -> {outcomeText}");
    }

    public static bool ShouldBankerDraw(int bankerTwoCardScore, Card? playerThirdCard)
    {
        if (playerThirdCard is null)
        {
            // Player stood on 6 or 7: Banker draws on 0-5, stands on 6-7
            return bankerTwoCardScore <= 5;
        }

        int p3 = playerThirdCard.Value.BaccaratValue;
        return bankerTwoCardScore switch
        {
            <= 2 => true,
            3 => p3 != 8,
            4 => p3 is >= 2 and <= 7,
            5 => p3 is >= 4 and <= 7,
            6 => p3 is 6 or 7,
            _ => false
        };
    }

    public static int ComputeScore(IReadOnlyList<Card> cards)
    {
        int sum = 0;
        for (int i = 0; i < cards.Count; i++)
            sum += cards[i].BaccaratValue;
        return sum % 10;
    }

    private void SettleBets(IList<HoldemPlayer> players, BaccaratOutcome outcome)
    {
        foreach (var p in players)
        {
            var b = p.BaccaratBets;
            int wagered = b.Total;
            if (wagered <= 0)
            {
                p.LastBaccaratNet = 0;
                continue;
            }

            int returned = 0;

            // Player bet: 1:1 on Player win, push on Tie
            if (b.Player > 0)
            {
                if (outcome == BaccaratOutcome.PlayerWin) returned += b.Player * 2;
                else if (outcome == BaccaratOutcome.Tie) returned += b.Player;
            }

            // Banker bet: 0.95:1 (5% commission) on Banker win, push on Tie
            if (b.Banker > 0)
            {
                if (outcome == BaccaratOutcome.BankerWin)
                    returned += b.Banker + (int)Math.Floor(b.Banker * 0.95);
                else if (outcome == BaccaratOutcome.Tie)
                    returned += b.Banker;
            }

            // Tie bet: 8:1
            if (b.Tie > 0 && outcome == BaccaratOutcome.Tie)
            {
                returned += b.Tie * 9;
            }

            // Side bets: Pairs pay 11:1
            if (b.PlayerPair > 0 && WasPlayerPair)
            {
                returned += b.PlayerPair * 12;
            }
            if (b.BankerPair > 0 && WasBankerPair)
            {
                returned += b.BankerPair * 12;
            }

            p.Chips += returned;
            p.LastBaccaratNet = returned - wagered;
            p.LastAction = p.LastBaccaratNet > 0
                ? $"+${p.LastBaccaratNet}"
                : p.LastBaccaratNet < 0
                    ? $"-${-p.LastBaccaratNet}"
                    : "PUSH";
        }
    }
}
