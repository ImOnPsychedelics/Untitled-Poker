using BalatroPoker.Core.Cards;
using BalatroPoker.Core.Models;

namespace BalatroPoker.Core.Holdem;

public sealed class HoldemPlayer
{
    public int SeatIndex { get; init; }
    public string PlayerId { get; init; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public bool IsHost { get; set; }
    public bool IsBot { get; set; }
    public bool IsConnected { get; set; } = true;
    public bool IsReady { get; set; }
    public int Chips { get; set; }

    public int CurrentBet { get; set; }
    public int TotalCommitted { get; set; }
    public bool Folded { get; set; }
    public bool IsAllIn { get; set; }
    public bool SittingOut { get; set; }
    public bool HasActedThisRound { get; set; }
    public List<Card> HoleCards { get; } = new();
    public string LastAction { get; set; } = string.Empty;
    public string ShowdownHandName { get; set; } = string.Empty;
    public int LastWinAmount { get; set; }

    // Shared baccarat bets kept on the same seat roster for seamless mode switching
    public BaccaratPlayerBets BaccaratBets { get; } = new();
    public int LastBaccaratNet { get; set; }

    // Shared Guts Poker state
    public GutsDecision GutsChoice { get; set; } = GutsDecision.Undecided;
    public int GutsNet { get; set; }

    // Shared Blackjack 21 state
    public int BlackjackBet { get; set; }
    public bool BlackjackLocked { get; set; }
    public bool BlackjackDoubled { get; set; }
    public bool BlackjackStood { get; set; }
    public bool BlackjackBusted { get; set; }
    public bool BlackjackNatural { get; set; }
    public List<Card> BlackjackCards { get; } = new();
    public int BlackjackTotal { get; set; }
    public int BlackjackNet { get; set; }

    // SAM Voice Chat bubble state
    public string LastChatMessage { get; set; } = string.Empty;
    public int LastChatPitch { get; set; } = 64;
    public long LastChatId { get; set; }
    public long LastChatUnixMs { get; set; }
}

public sealed class HoldemEngine
{
    private readonly Deck _deck;
    private readonly Action<string> _log;

    public HoldemStage Stage { get; private set; } = HoldemStage.Waiting;
    public int HandNumber { get; private set; }
    public int DealerSeat { get; private set; } = -1;
    public int SmallBlindSeat { get; private set; } = -1;
    public int BigBlindSeat { get; private set; } = -1;
    public int CurrentTurnSeat { get; private set; } = -1;
    public int CurrentBet { get; private set; }
    public int MinRaise { get; private set; }
    public List<Card> CommunityCards { get; } = new();
    public List<int> WinnerSeats { get; } = new();
    public string BannerMessage { get; private set; } = "WAITING FOR PLAYERS";
    public bool IsOmaha { get; private set; }

    public HoldemEngine(Action<string>? logger = null, Random? deterministicRng = null)
    {
        _deck = new Deck(1, 0, deterministicRng);
        _log = logger ?? (_ => { });
    }

    public int TotalPot(IEnumerable<HoldemPlayer> players) =>
        players.Sum(p => p.TotalCommitted);

    public bool StartNewHand(IList<HoldemPlayer> players, GameConfig config)
    {
        var eligible = players.Where(p => p.Chips > 0 && p.IsConnected).OrderBy(p => p.SeatIndex).ToList();
        if (eligible.Count < 2)
        {
            Stage = HoldemStage.Waiting;
            CurrentTurnSeat = -1;
            BannerMessage = "NEED AT LEAST 2 PLAYERS WITH CHIPS";
            return false;
        }

        HandNumber++;
        _deck.ResetAndShuffle();
        CommunityCards.Clear();
        WinnerSeats.Clear();

        foreach (var p in players)
        {
            p.HoleCards.Clear();
            p.CurrentBet = 0;
            p.TotalCommitted = 0;
            p.Folded = false;
            p.IsAllIn = false;
            p.HasActedThisRound = false;
            p.LastAction = string.Empty;
            p.ShowdownHandName = string.Empty;
            p.LastWinAmount = 0;
            p.SittingOut = p.Chips <= 0 || !p.IsConnected;
        }

        // Advance dealer button clockwise among eligible seats
        DealerSeat = NextEligibleSeat(eligible, DealerSeat);

        if (eligible.Count == 2)
        {
            // Heads-up rule: Dealer posts SB, other player posts BB
            SmallBlindSeat = DealerSeat;
            BigBlindSeat = NextEligibleSeat(eligible, SmallBlindSeat);
        }
        else
        {
            SmallBlindSeat = NextEligibleSeat(eligible, DealerSeat);
            BigBlindSeat = NextEligibleSeat(eligible, SmallBlindSeat);
        }

        // Deal hole cards (2 for Texas Hold'em, 4 for Omaha Hold'em)
        IsOmaha = config.Mode == GameMode.OmahaHoldem;
        int holeCount = IsOmaha ? 4 : 2;
        for (int round = 0; round < holeCount; round++)
        {
            int seat = NextEligibleSeat(eligible, DealerSeat);
            for (int i = 0; i < eligible.Count; i++)
            {
                var p = eligible.First(x => x.SeatIndex == seat);
                p.HoleCards.Add(_deck.Draw());
                seat = NextEligibleSeat(eligible, seat);
            }
        }

        Stage = HoldemStage.PreFlop;
        CurrentBet = 0;
        MinRaise = config.BigBlind;

        var sbPlayer = eligible.First(x => x.SeatIndex == SmallBlindSeat);
        var bbPlayer = eligible.First(x => x.SeatIndex == BigBlindSeat);

        PostBlind(sbPlayer, config.SmallBlind, "SB");
        PostBlind(bbPlayer, config.BigBlind, "BB");
        CurrentBet = Math.Max(sbPlayer.CurrentBet, bbPlayer.CurrentBet);

        _log($"--- HAND #{HandNumber} --- Dealer: {eligible.First(x => x.SeatIndex == DealerSeat).Name}, SB: {sbPlayer.Name}, BB: {bbPlayer.Name}");

        // Pre-flop action starts left of BB (in heads-up, that is SB/Dealer)
        CurrentTurnSeat = NextActionableSeat(players, BigBlindSeat);
        BannerMessage = $"HAND #{HandNumber} - PRE-FLOP";

        if (CurrentTurnSeat == -1 || ShouldAutoRunout(players))
        {
            RunOutToShowdown(players);
        }

        return true;
    }

    public (bool Ok, string Error) ApplyAction(
        IList<HoldemPlayer> players,
        int seatIndex,
        HoldemActionType action,
        int raiseToAmount,
        GameConfig config)
    {
        if (Stage is HoldemStage.Waiting or HoldemStage.Showdown)
            return (false, "No active betting round.");

        if (seatIndex != CurrentTurnSeat)
            return (false, "Not your turn.");

        var player = players.FirstOrDefault(p => p.SeatIndex == seatIndex);
        if (player is null || player.Folded || player.SittingOut || player.IsAllIn)
            return (false, "Player cannot act.");

        int toCall = Math.Max(0, CurrentBet - player.CurrentBet);

        switch (action)
        {
            case HoldemActionType.Fold:
                player.Folded = true;
                player.HasActedThisRound = true;
                player.LastAction = "FOLD";
                _log($"{player.Name} folds.");
                break;

            case HoldemActionType.Check:
                if (toCall > 0)
                    return (false, $"Cannot check; must call {toCall}.");
                player.HasActedThisRound = true;
                player.LastAction = "CHECK";
                _log($"{player.Name} checks.");
                break;

            case HoldemActionType.Call:
                if (toCall <= 0)
                {
                    player.HasActedThisRound = true;
                    player.LastAction = "CHECK";
                    _log($"{player.Name} checks.");
                }
                else
                {
                    int paid = CommitChips(player, toCall);
                    player.HasActedThisRound = true;
                    player.LastAction = player.IsAllIn ? $"ALL-IN ({player.CurrentBet})" : $"CALL {paid}";
                    _log($"{player.Name} {(player.IsAllIn ? $"calls all-in ({paid})" : $"calls {paid}")}.");
                }
                break;

            case HoldemActionType.Bet:
            case HoldemActionType.Raise:
                {
                    int minTarget = CurrentBet == 0
                        ? config.BigBlind
                        : CurrentBet + Math.Max(MinRaise, config.BigBlind);
                    int maxTarget = player.CurrentBet + player.Chips;

                    int target = raiseToAmount;
                    if (target < minTarget && target < maxTarget)
                        target = Math.Min(minTarget, maxTarget);
                    if (target > maxTarget)
                        target = maxTarget;

                    int needed = target - player.CurrentBet;
                    if (needed <= 0)
                        return (false, "Invalid bet/raise amount.");

                    CommitChips(player, needed);
                    int prevBet = CurrentBet;
                    bool isFullRaise = player.CurrentBet >= minTarget;

                    if (player.CurrentBet > CurrentBet)
                    {
                        int raiseDelta = player.CurrentBet - prevBet;
                        if (isFullRaise)
                        {
                            MinRaise = Math.Max(MinRaise, raiseDelta);
                            ResetOthersActed(players, player.SeatIndex);
                        }
                        else if (prevBet == 0)
                        {
                            ResetOthersActed(players, player.SeatIndex);
                        }
                        CurrentBet = player.CurrentBet;
                    }

                    player.HasActedThisRound = true;
                    string verb = prevBet == 0 ? "BET" : "RAISE";
                    player.LastAction = player.IsAllIn ? $"ALL-IN ({player.CurrentBet})" : $"{verb} {player.CurrentBet}";
                    _log($"{player.Name} {(player.IsAllIn ? $"goes all-in to {player.CurrentBet}" : $"{verb.ToLowerInvariant()}s to {player.CurrentBet}")}.");
                    break;
                }

            case HoldemActionType.AllIn:
                {
                    int prevBet = CurrentBet;
                    int minTarget = CurrentBet == 0
                        ? config.BigBlind
                        : CurrentBet + Math.Max(MinRaise, config.BigBlind);

                    CommitChips(player, player.Chips);

                    if (player.CurrentBet > CurrentBet)
                    {
                        int raiseDelta = player.CurrentBet - prevBet;
                        if (player.CurrentBet >= minTarget || prevBet == 0)
                        {
                            MinRaise = Math.Max(MinRaise, raiseDelta);
                            ResetOthersActed(players, player.SeatIndex);
                        }
                        CurrentBet = player.CurrentBet;
                    }

                    player.HasActedThisRound = true;
                    player.LastAction = $"ALL-IN ({player.CurrentBet})";
                    _log($"{player.Name} goes ALL-IN ({player.CurrentBet})!");
                    break;
                }

            default:
                return (false, "Unknown action.");
        }

        AdvanceAfterAction(players, config);
        return (true, string.Empty);
    }

    private void AdvanceAfterAction(IList<HoldemPlayer> players, GameConfig config)
    {
        var contenders = players.Where(p => !p.SittingOut && !p.Folded).ToList();
        if (contenders.Count == 1)
        {
            AwardByFold(players, contenders[0]);
            return;
        }

        if (IsBettingRoundComplete(players))
        {
            AdvanceStage(players, config);
            return;
        }

        CurrentTurnSeat = NextActionableSeat(players, CurrentTurnSeat);
        if (CurrentTurnSeat == -1)
        {
            AdvanceStage(players, config);
        }
    }

    private bool IsBettingRoundComplete(IList<HoldemPlayer> players)
    {
        foreach (var p in players)
        {
            if (p.SittingOut || p.Folded || p.IsAllIn) continue;
            if (!p.HasActedThisRound) return false;
            if (p.CurrentBet < CurrentBet) return false;
        }
        return true;
    }

    private bool ShouldAutoRunout(IList<HoldemPlayer> players)
    {
        int actionable = players.Count(p => !p.SittingOut && !p.Folded && !p.IsAllIn);
        return actionable <= 1;
    }

    private void AdvanceStage(IList<HoldemPlayer> players, GameConfig config)
    {
        foreach (var p in players)
        {
            p.CurrentBet = 0;
            p.HasActedThisRound = false;
            if (!p.Folded && !p.IsAllIn && !p.SittingOut)
                p.LastAction = string.Empty;
        }
        CurrentBet = 0;
        MinRaise = config.BigBlind;

        if (Stage == HoldemStage.River)
        {
            ResolveShowdown(players);
            return;
        }

        if (ShouldAutoRunout(players))
        {
            RunOutToShowdown(players);
            return;
        }

        DealNextStreet();
        CurrentTurnSeat = NextActionableSeat(players, DealerSeat);
        if (CurrentTurnSeat == -1)
        {
            RunOutToShowdown(players);
        }
    }

    private void DealNextStreet()
    {
        _deck.Burn();
        switch (Stage)
        {
            case HoldemStage.PreFlop:
                CommunityCards.Add(_deck.Draw());
                CommunityCards.Add(_deck.Draw());
                CommunityCards.Add(_deck.Draw());
                Stage = HoldemStage.Flop;
                BannerMessage = $"HAND #{HandNumber} - FLOP";
                _log($"FLOP: {string.Join(" ", CommunityCards)}");
                break;
            case HoldemStage.Flop:
                CommunityCards.Add(_deck.Draw());
                Stage = HoldemStage.Turn;
                BannerMessage = $"HAND #{HandNumber} - TURN";
                _log($"TURN: {CommunityCards[^1]}");
                break;
            case HoldemStage.Turn:
                CommunityCards.Add(_deck.Draw());
                Stage = HoldemStage.River;
                BannerMessage = $"HAND #{HandNumber} - RIVER";
                _log($"RIVER: {CommunityCards[^1]}");
                break;
        }
    }

    private void RunOutToShowdown(IList<HoldemPlayer> players)
    {
        while (Stage is HoldemStage.PreFlop or HoldemStage.Flop or HoldemStage.Turn)
        {
            DealNextStreet();
        }
        ResolveShowdown(players);
    }

    private void AwardByFold(IList<HoldemPlayer> players, HoldemPlayer winner)
    {
        int pot = TotalPot(players);
        winner.Chips += pot;
        winner.LastWinAmount = pot;
        WinnerSeats.Clear();
        WinnerSeats.Add(winner.SeatIndex);
        Stage = HoldemStage.Showdown;
        CurrentTurnSeat = -1;
        BannerMessage = $"{winner.Name} WINS ${pot} (ALL FOLDED)";
        _log($"{winner.Name} wins ${pot} (everyone else folded).");
    }

    private void ResolveShowdown(IList<HoldemPlayer> players)
    {
        Stage = HoldemStage.Showdown;
        CurrentTurnSeat = -1;
        WinnerSeats.Clear();

        var contenders = players.Where(p => !p.SittingOut && !p.Folded).ToList();
        var ranks = new Dictionary<int, HandRank>();

        foreach (var p in contenders)
        {
            HandRank rank;
            if (IsOmaha && p.HoleCards.Count >= 4)
            {
                rank = HandEvaluator.EvaluateOmaha(p.HoleCards, CommunityCards);
            }
            else
            {
                var seven = new List<Card>(7);
                seven.AddRange(p.HoleCards);
                seven.AddRange(CommunityCards);
                rank = HandEvaluator.Evaluate(seven);
            }
            ranks[p.SeatIndex] = rank;
            p.ShowdownHandName = rank.Description;
        }

        var pots = BuildSidePots(players);
        var seatLookup = players.ToDictionary(p => p.SeatIndex);

        foreach (var pot in pots)
        {
            var eligible = pot.EligibleSeats.Where(ranks.ContainsKey).ToList();
            if (eligible.Count == 0) continue;

            int bestScore = eligible.Max(s => ranks[s].Score);
            var potWinners = eligible.Where(s => ranks[s].Score == bestScore).ToList();

            // Order winners clockwise starting left of dealer for odd chip distribution
            potWinners = OrderClockwiseFrom(potWinners, DealerSeat);

            int share = pot.Amount / potWinners.Count;
            int remainder = pot.Amount % potWinners.Count;

            for (int i = 0; i < potWinners.Count; i++)
            {
                int seat = potWinners[i];
                int award = share + (i < remainder ? 1 : 0);
                seatLookup[seat].Chips += award;
                seatLookup[seat].LastWinAmount += award;
                if (!WinnerSeats.Contains(seat))
                    WinnerSeats.Add(seat);
            }
        }

        var winnerSummary = WinnerSeats
            .Select(s => $"{seatLookup[s].Name} (+${seatLookup[s].LastWinAmount} {seatLookup[s].ShowdownHandName})")
            .ToList();
        BannerMessage = winnerSummary.Count == 1
            ? winnerSummary[0]
            : $"SPLIT POT: {string.Join(" / ", WinnerSeats.Select(s => seatLookup[s].Name))}";
        _log($"SHOWDOWN: {string.Join(" | ", winnerSummary)}");
    }

    public static List<PotSnapshot> BuildSidePots(IEnumerable<HoldemPlayer> players)
    {
        var pots = new List<PotSnapshot>();
        var contributors = players.Where(p => p.TotalCommitted > 0).ToList();
        if (contributors.Count == 0) return pots;

        var levels = contributors
            .Select(p => p.TotalCommitted)
            .Distinct()
            .OrderBy(x => x)
            .ToList();

        int prevLevel = 0;
        foreach (int level in levels)
        {
            int slice = level - prevLevel;
            if (slice <= 0) continue;

            var involved = contributors.Where(p => p.TotalCommitted >= level).ToList();
            int potAmount = slice * involved.Count;
            var eligible = involved
                .Where(p => !p.Folded && !p.SittingOut)
                .Select(p => p.SeatIndex)
                .OrderBy(s => s)
                .ToList();

            if (eligible.Count == 0)
            {
                // Dead money if everyone at this level folded; merge into previous pot if present
                if (pots.Count > 0)
                    pots[^1].Amount += potAmount;
            }
            else if (pots.Count > 0 && pots[^1].EligibleSeats.SequenceEqual(eligible))
            {
                pots[^1].Amount += potAmount;
            }
            else
            {
                pots.Add(new PotSnapshot
                {
                    Amount = potAmount,
                    EligibleSeats = eligible
                });
            }

            prevLevel = level;
        }

        return pots;
    }

    /// <summary>
    /// Simple, reasonable bot AI for Texas Hold'em so players can fill empty seats or practice solo.
    /// </summary>
    public (HoldemActionType Action, int Amount) DecideBotAction(
        IList<HoldemPlayer> players,
        HoldemPlayer bot,
        GameConfig config,
        Random rng)
    {
        int toCall = Math.Max(0, CurrentBet - bot.CurrentBet);
        double strength = EstimateBotStrength(bot);

        if (toCall == 0)
        {
            if (strength > 0.72 && bot.Chips > config.BigBlind)
            {
                int betSize = Math.Min(bot.Chips, Math.Max(config.BigBlind, (TotalPot(players) / 2 / 5) * 5));
                return (HoldemActionType.Bet, bot.CurrentBet + Math.Max(config.BigBlind, betSize));
            }
            if (rng.NextDouble() < 0.12 && bot.Chips > config.BigBlind)
            {
                return (HoldemActionType.Bet, bot.CurrentBet + config.BigBlind);
            }
            return (HoldemActionType.Check, 0);
        }

        double potOdds = toCall / (double)Math.Max(1, TotalPot(players) + toCall);
        if (strength > 0.84 && bot.Chips > toCall + config.BigBlind && rng.NextDouble() < 0.55)
        {
            int raiseTo = CurrentBet + Math.Max(MinRaise, config.BigBlind * 2);
            return (HoldemActionType.Raise, Math.Min(bot.CurrentBet + bot.Chips, raiseTo));
        }

        if (strength + 0.15 >= potOdds || toCall <= config.BigBlind)
        {
            return (HoldemActionType.Call, 0);
        }

        return (HoldemActionType.Fold, 0);
    }

    private double EstimateBotStrength(HoldemPlayer bot)
    {
        if (bot.HoleCards.Count < 2) return 0.3;
        if (CommunityCards.Count >= 3)
        {
            HandRank rank;
            if (IsOmaha && bot.HoleCards.Count >= 4)
            {
                rank = HandEvaluator.EvaluateOmaha(bot.HoleCards, CommunityCards);
            }
            else
            {
                var all = new List<Card>(bot.HoleCards);
                all.AddRange(CommunityCards);
                rank = HandEvaluator.Evaluate(all);
            }
            return rank.Category switch
            {
                HandCategory.HighCard => 0.25 + ((int)bot.HoleCards.Max(c => c.Rank) / 40.0),
                HandCategory.OnePair => 0.55,
                HandCategory.TwoPair => 0.72,
                HandCategory.ThreeOfAKind => 0.80,
                HandCategory.Straight => 0.86,
                HandCategory.Flush => 0.90,
                HandCategory.FullHouse => 0.95,
                _ => 0.99
            };
        }

        // Pre-flop heuristic
        int r1 = (int)bot.HoleCards[0].Rank;
        int r2 = (int)bot.HoleCards[1].Rank;
        bool paired = r1 == r2;
        bool suited = bot.HoleCards[0].Suit == bot.HoleCards[1].Suit;
        int high = Math.Max(r1, r2);
        int low = Math.Min(r1, r2);

        double score = (high + low) / 36.0;
        if (paired) score += 0.32;
        if (suited) score += 0.06;
        if (high - low <= 2) score += 0.05;
        return Math.Clamp(score, 0.1, 0.98);
    }

    private static int CommitChips(HoldemPlayer player, int amount)
    {
        int actual = Math.Min(player.Chips, Math.Max(0, amount));
        player.Chips -= actual;
        player.CurrentBet += actual;
        player.TotalCommitted += actual;
        if (player.Chips == 0)
            player.IsAllIn = true;
        return actual;
    }

    private static void PostBlind(HoldemPlayer player, int blindAmount, string label)
    {
        int paid = CommitChips(player, blindAmount);
        player.LastAction = player.IsAllIn ? $"ALL-IN {paid}" : $"{label} {paid}";
    }

    private static void ResetOthersActed(IList<HoldemPlayer> players, int actingSeat)
    {
        foreach (var p in players)
        {
            if (p.SeatIndex != actingSeat && !p.Folded && !p.IsAllIn && !p.SittingOut)
            {
                p.HasActedThisRound = false;
            }
        }
    }

    private static int NextEligibleSeat(List<HoldemPlayer> eligibleSorted, int afterSeat)
    {
        foreach (var p in eligibleSorted)
        {
            if (p.SeatIndex > afterSeat)
                return p.SeatIndex;
        }
        return eligibleSorted[0].SeatIndex;
    }

    private static int NextActionableSeat(IList<HoldemPlayer> players, int afterSeat)
    {
        var actionable = players
            .Where(p => !p.SittingOut && !p.Folded && !p.IsAllIn)
            .OrderBy(p => p.SeatIndex)
            .ToList();

        if (actionable.Count == 0) return -1;

        foreach (var p in actionable)
        {
            if (p.SeatIndex > afterSeat)
                return p.SeatIndex;
        }
        return actionable[0].SeatIndex;
    }

    private static List<int> OrderClockwiseFrom(List<int> seats, int dealerSeat)
    {
        return seats
            .OrderBy(s => s > dealerSeat ? s - dealerSeat : s + 100 - dealerSeat)
            .ToList();
    }
}
