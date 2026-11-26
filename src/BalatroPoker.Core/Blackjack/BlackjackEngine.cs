using BalatroPoker.Core.Cards;
using BalatroPoker.Core.Holdem;
using BalatroPoker.Core.Models;

namespace BalatroPoker.Core.Blackjack;

public sealed class BlackjackEngine
{
    private readonly Deck _shoe;
    private readonly Action<string> _log;

    public BlackjackStage Stage { get; private set; } = BlackjackStage.Betting;
    public int RoundNumber { get; private set; }
    public int ShoeRemaining => _shoe.Remaining;
    public int CurrentTurnSeat { get; private set; } = -1;
    public List<Card> DealerCards { get; } = new();
    public int DealerTotal { get; private set; }
    public bool DealerBlackjack { get; private set; }
    public bool DealerBusted { get; private set; }
    public string BannerMessage { get; private set; } = "PLACE YOUR 21 / BLACKJACK BETS";

    public BlackjackEngine(Action<string>? logger = null, Random? deterministicRng = null)
    {
        // Standard 6-deck Blackjack shoe (312 cards), reshuffle when <= 30 cards remain
        _shoe = new Deck(numDecks: 6, reshuffleThreshold: 30, deterministicRng: deterministicRng);
        _log = logger ?? (_ => { });
    }

    public void StartBettingPhase(IList<HoldemPlayer> players)
    {
        if (_shoe.NeedsReshuffle)
        {
            _shoe.ResetAndShuffle();
            _log("Blackjack 6-deck shoe reshuffled (312 cards).");
        }

        Stage = BlackjackStage.Betting;
        CurrentTurnSeat = -1;
        DealerCards.Clear();
        DealerTotal = 0;
        DealerBlackjack = false;
        DealerBusted = false;
        BannerMessage = $"BLACKJACK #{RoundNumber + 1} - PLACE YOUR BETS";

        foreach (var p in players)
        {
            p.BlackjackBet = 0;
            p.BlackjackLocked = false;
            p.BlackjackDoubled = false;
            p.BlackjackStood = false;
            p.BlackjackBusted = false;
            p.BlackjackNatural = false;
            p.BlackjackCards.Clear();
            p.BlackjackTotal = 0;
            p.BlackjackNet = 0;
            p.LastAction = string.Empty;
        }
    }

    public (bool Ok, string Error) PlaceBet(HoldemPlayer player, int amount)
    {
        if (Stage != BlackjackStage.Betting)
            return (false, "Betting is closed.");
        if (player.BlackjackLocked)
            return (false, "Bet is locked.");
        if (amount <= 0 || player.Chips < amount)
            return (false, "Not enough chips.");

        player.Chips -= amount;
        player.BlackjackBet += amount;
        player.LastAction = $"BET ${player.BlackjackBet}";
        return (true, string.Empty);
    }

    public void ClearBet(HoldemPlayer player)
    {
        if (Stage != BlackjackStage.Betting || player.BlackjackLocked) return;
        player.Chips += player.BlackjackBet;
        player.BlackjackBet = 0;
        player.LastAction = string.Empty;
    }

    public void LockBet(HoldemPlayer player)
    {
        if (Stage != BlackjackStage.Betting) return;
        player.BlackjackLocked = true;
        player.LastAction = player.BlackjackBet > 0 ? $"LOCKED ${player.BlackjackBet}" : "SITTING OUT";
    }

    public void PlaceBotBets(IList<HoldemPlayer> players, Random rng)
    {
        if (Stage != BlackjackStage.Betting) return;
        foreach (var bot in players.Where(p => p.IsBot && p.IsConnected && !p.BlackjackLocked))
        {
            if (bot.Chips < 10)
            {
                bot.BlackjackLocked = true;
                bot.LastAction = "NO CHIPS";
                continue;
            }
            int[] sizes = { 10, 25, 50, 100 };
            int bet = Math.Min(bot.Chips, sizes[rng.Next(sizes.Length)]);
            PlaceBet(bot, bet);
            LockBet(bot);
        }
    }

    public bool AreAllLocked(IList<HoldemPlayer> players)
    {
        var active = players.Where(p => p.IsConnected && (p.Chips > 0 || p.BlackjackBet > 0)).ToList();
        return active.Count > 0 && active.All(p => p.BlackjackLocked);
    }

    public void DealInitial(IList<HoldemPlayer> players, Func<Card>? customDraw = null)
    {
        Func<Card> draw = customDraw ?? _shoe.Draw;

        var bettors = players.Where(p => p.BlackjackBet > 0).OrderBy(p => p.SeatIndex).ToList();
        if (bettors.Count == 0)
        {
            BannerMessage = "PLACE AT LEAST ONE BET TO DEAL!";
            return;
        }

        RoundNumber++;
        DealerCards.Clear();
        DealerBlackjack = false;
        DealerBusted = false;

        foreach (var p in bettors)
        {
            p.BlackjackCards.Clear();
            p.BlackjackStood = false;
            p.BlackjackBusted = false;
            p.BlackjackDoubled = false;
            p.BlackjackNatural = false;
        }

        // Deal 2 cards alternating players then dealer
        for (int r = 0; r < 2; r++)
        {
            foreach (var p in bettors)
                p.BlackjackCards.Add(draw());
            DealerCards.Add(draw());
        }

        foreach (var p in bettors)
        {
            var (tot, _, isBj) = EvaluateHand(p.BlackjackCards);
            p.BlackjackTotal = tot;
            p.BlackjackNatural = isBj;
            if (isBj)
            {
                p.BlackjackStood = true;
                p.LastAction = "BLACKJACK!";
            }
        }

        var (dTot, _, dBj) = EvaluateHand(DealerCards);
        DealerTotal = dTot;
        DealerBlackjack = dBj;

        // If dealer has Natural Blackjack, immediately settle
        if (DealerBlackjack)
        {
            RunDealerAndSettle(players, customDraw);
            return;
        }

        Stage = BlackjackStage.PlayerTurns;
        CurrentTurnSeat = NextTurnSeat(players, -1);
        if (CurrentTurnSeat == -1)
        {
            RunDealerAndSettle(players, customDraw);
        }
        else
        {
            var actor = players.First(p => p.SeatIndex == CurrentTurnSeat);
            BannerMessage = $"BLACKJACK #{RoundNumber} - {actor.Name}'S TURN";
        }
    }

    public (bool Ok, string Error) ApplyAction(
        IList<HoldemPlayer> players,
        int seatIndex,
        BlackjackActionType action,
        Func<Card>? customDraw = null)
    {
        if (Stage != BlackjackStage.PlayerTurns)
            return (false, "Not in player turn phase.");
        if (seatIndex != CurrentTurnSeat)
            return (false, "Not your turn.");

        var player = players.FirstOrDefault(p => p.SeatIndex == seatIndex);
        if (player is null || player.BlackjackBet <= 0)
            return (false, "Invalid player.");

        Func<Card> draw = customDraw ?? _shoe.Draw;

        switch (action)
        {
            case BlackjackActionType.Hit:
                {
                    player.BlackjackCards.Add(draw());
                    var (tot, _, _) = EvaluateHand(player.BlackjackCards);
                    player.BlackjackTotal = tot;
                    if (tot > 21)
                    {
                        player.BlackjackBusted = true;
                        player.BlackjackStood = true;
                        player.LastAction = $"BUST ({tot})";
                        _log($"{player.Name} hits and BUSTS ({tot}).");
                        AdvanceTurn(players, customDraw);
                    }
                    else if (tot == 21)
                    {
                        player.BlackjackStood = true;
                        player.LastAction = "21!";
                        _log($"{player.Name} hits to 21!");
                        AdvanceTurn(players, customDraw);
                    }
                    else
                    {
                        player.LastAction = $"HIT ({tot})";
                        _log($"{player.Name} hits ({tot}).");
                    }
                    break;
                }

            case BlackjackActionType.Stand:
                player.BlackjackStood = true;
                player.LastAction = $"STAND ({player.BlackjackTotal})";
                _log($"{player.Name} stands on {player.BlackjackTotal}.");
                AdvanceTurn(players, customDraw);
                break;

            case BlackjackActionType.DoubleDown:
                if (player.BlackjackCards.Count != 2)
                    return (false, "Can only double down on initial 2 cards.");
                if (player.Chips < player.BlackjackBet)
                    return (false, "Not enough chips to double down.");

                player.Chips -= player.BlackjackBet;
                player.BlackjackBet *= 2;
                player.BlackjackDoubled = true;
                player.BlackjackCards.Add(draw());
                var (dTot, _, _) = EvaluateHand(player.BlackjackCards);
                player.BlackjackTotal = dTot;
                player.BlackjackStood = true;
                if (dTot > 21)
                {
                    player.BlackjackBusted = true;
                    player.LastAction = $"DBL BUST ({dTot})";
                    _log($"{player.Name} doubles down and BUSTS ({dTot}).");
                }
                else
                {
                    player.LastAction = $"DOUBLE ({dTot})";
                    _log($"{player.Name} doubles down to {dTot}.");
                }
                AdvanceTurn(players, customDraw);
                break;

            default:
                return (false, "Unknown action.");
        }

        return (true, string.Empty);
    }

    public BlackjackActionType DecideBotAction(HoldemPlayer bot)
    {
        int dealerUp = DealerCards.Count > 0 ? CardPointValue(DealerCards[0]) : 10;
        var (tot, isSoft, _) = EvaluateHand(bot.BlackjackCards);

        if (bot.BlackjackCards.Count == 2 && bot.Chips >= bot.BlackjackBet && (tot == 11 || (tot == 10 && dealerUp <= 9)))
            return BlackjackActionType.DoubleDown;

        if (isSoft)
            return tot <= 17 ? BlackjackActionType.Hit : BlackjackActionType.Stand;

        if (tot <= 11) return BlackjackActionType.Hit;
        if (tot <= 16 && dealerUp >= 7) return BlackjackActionType.Hit;
        return BlackjackActionType.Stand;
    }

    private void AdvanceTurn(IList<HoldemPlayer> players, Func<Card>? customDraw)
    {
        CurrentTurnSeat = NextTurnSeat(players, CurrentTurnSeat);
        if (CurrentTurnSeat == -1)
        {
            RunDealerAndSettle(players, customDraw);
        }
        else
        {
            var actor = players.First(p => p.SeatIndex == CurrentTurnSeat);
            BannerMessage = $"BLACKJACK #{RoundNumber} - {actor.Name}'S TURN";
        }
    }

    private void RunDealerAndSettle(IList<HoldemPlayer> players, Func<Card>? customDraw)
    {
        Func<Card> draw = customDraw ?? _shoe.Draw;
        CurrentTurnSeat = -1;

        var bettors = players.Where(p => p.BlackjackBet > 0).ToList();
        bool anyLiveNonNatural = bettors.Any(p => !p.BlackjackBusted && !p.BlackjackNatural);

        if (!DealerBlackjack && anyLiveNonNatural)
        {
            while (true)
            {
                var (dt, _, _) = EvaluateHand(DealerCards);
                DealerTotal = dt;
                if (dt >= 17) break;
                DealerCards.Add(draw());
            }
        }

        var (finalDealerTot, _, finalDealerBj) = EvaluateHand(DealerCards);
        DealerTotal = finalDealerTot;
        DealerBlackjack = finalDealerBj;
        DealerBusted = finalDealerTot > 21;

        foreach (var p in bettors)
        {
            int bet = p.BlackjackBet;
            int returned = 0;

            if (p.BlackjackBusted)
            {
                returned = 0;
                p.LastAction = $"BUST -${bet}";
            }
            else if (p.BlackjackNatural && !DealerBlackjack)
            {
                // Natural 21 pays 3:2
                returned = bet + (bet * 3) / 2;
                p.LastAction = $"BJ +${returned - bet}";
            }
            else if (DealerBlackjack && !p.BlackjackNatural)
            {
                returned = 0;
                p.LastAction = $"DEALER BJ -${bet}";
            }
            else if (DealerBusted || p.BlackjackTotal > DealerTotal)
            {
                returned = bet * 2;
                p.LastAction = $"WIN +${bet}";
            }
            else if (p.BlackjackTotal == DealerTotal)
            {
                returned = bet;
                p.LastAction = "PUSH";
            }
            else
            {
                returned = 0;
                p.LastAction = $"LOSE -${bet}";
            }

            p.Chips += returned;
            p.BlackjackNet = returned - bet;
        }

        Stage = BlackjackStage.Settled;
        BannerMessage = DealerBlackjack
            ? "DEALER NATURAL BLACKJACK (21)!"
            : DealerBusted
                ? $"DEALER BUSTS ({DealerTotal})! SURVIVORS WIN!"
                : $"DEALER STANDS ON {DealerTotal}";
        _log($"BLACKJACK #{RoundNumber}: Dealer [{string.Join(" ", DealerCards)}]={DealerTotal} -> {BannerMessage}");
    }

    public static (int Total, bool IsSoft, bool IsBlackjack) EvaluateHand(IReadOnlyList<Card> cards)
    {
        int sum = 0;
        int aces = 0;
        for (int i = 0; i < cards.Count; i++)
        {
            if (cards[i].Rank == Rank.Ace)
            {
                aces++;
                sum += 11;
            }
            else
            {
                sum += CardPointValue(cards[i]);
            }
        }

        while (sum > 21 && aces > 0)
        {
            sum -= 10;
            aces--;
        }

        bool isSoft = aces > 0;
        bool isBj = cards.Count == 2 && sum == 21;
        return (sum, isSoft, isBj);
    }

    public static int CardPointValue(Card c) => c.Rank switch
    {
        Rank.Ace => 11,
        >= Rank.Ten => 10,
        _ => (int)c.Rank
    };

    private static int NextTurnSeat(IList<HoldemPlayer> players, int afterSeat)
    {
        var candidates = players
            .Where(p => p.BlackjackBet > 0 && !p.BlackjackStood && !p.BlackjackBusted)
            .OrderBy(p => p.SeatIndex)
            .ToList();

        foreach (var p in candidates)
        {
            if (p.SeatIndex > afterSeat)
                return p.SeatIndex;
        }
        return -1;
    }
}
