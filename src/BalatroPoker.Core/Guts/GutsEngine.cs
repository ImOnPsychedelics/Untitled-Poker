using BalatroPoker.Core.Cards;
using BalatroPoker.Core.Holdem;
using BalatroPoker.Core.Models;

namespace BalatroPoker.Core.Guts;

public sealed class GutsEngine
{
    private readonly Deck _deck;
    private readonly Action<string> _log;

    public GutsStage Stage { get; private set; } = GutsStage.Deciding;
    public int RoundNumber { get; private set; }
    public int Pot { get; private set; }
    public int Ante { get; private set; } = 20;
    public List<int> WinnerSeats { get; } = new();
    public string BannerMessage { get; private set; } = "GUTS POKER - DECIDE IN OR OUT";

    public GutsEngine(Action<string>? logger = null, Random? deterministicRng = null)
    {
        _deck = new Deck(1, 0, deterministicRng);
        _log = logger ?? (_ => { });
    }

    public bool StartNewRound(IList<HoldemPlayer> players, GameConfig config)
    {
        Ante = Math.Max(5, config.BigBlind);
        var eligible = players.Where(p => p.IsConnected && (p.Chips > 0 || Pot > 0)).OrderBy(p => p.SeatIndex).ToList();
        if (eligible.Count < 2)
        {
            BannerMessage = "NEED AT LEAST 2 PLAYERS FOR GUTS";
            return false;
        }

        RoundNumber++;
        _deck.ResetAndShuffle();
        WinnerSeats.Clear();

        bool freshPot = Pot == 0;
        foreach (var p in players)
        {
            p.HoleCards.Clear();
            p.GutsChoice = GutsDecision.Undecided;
            p.GutsNet = 0;
            p.LastAction = string.Empty;
            p.ShowdownHandName = string.Empty;
            p.LastWinAmount = 0;
            p.SittingOut = !p.IsConnected || (p.Chips <= 0 && freshPot);

            if (!p.SittingOut)
            {
                if (freshPot)
                {
                    int antePaid = Math.Min(Ante, p.Chips);
                    p.Chips -= antePaid;
                    Pot += antePaid;
                    p.GutsNet -= antePaid;
                }
                p.HoleCards.Add(_deck.Draw());
                p.HoleCards.Add(_deck.Draw());
            }
        }

        Stage = GutsStage.Deciding;
        BannerMessage = $"GUTS #{RoundNumber} - POT ${Pot} - GO IN OR OUT!";
        _log($"--- GUTS #{RoundNumber} --- Pot: ${Pot} ({(freshPot ? $"Ante ${Ante}" : "Carryover")})");
        return true;
    }

    public (bool Ok, string Error) SubmitDecision(IList<HoldemPlayer> players, int seatIndex, GutsDecision decision)
    {
        if (Stage != GutsStage.Deciding)
            return (false, "Not in decision phase.");
        if (decision is not (GutsDecision.In or GutsDecision.Out))
            return (false, "Must choose IN or OUT.");

        var player = players.FirstOrDefault(p => p.SeatIndex == seatIndex);
        if (player is null || player.SittingOut)
            return (false, "Player is not active in this round.");

        player.GutsChoice = decision;
        player.LastAction = "DECIDED";
        return (true, string.Empty);
    }

    public void DecideBots(IList<HoldemPlayer> players, Random rng)
    {
        if (Stage != GutsStage.Deciding) return;
        foreach (var bot in players.Where(p => p.IsBot && !p.SittingOut && p.GutsChoice == GutsDecision.Undecided))
        {
            int score = ScoreTwoCardHand(bot.HoleCards);
            bool isPair = score >= (1 << 12);
            int hi = bot.HoleCards.Count == 2 ? (int)Math.Max((int)bot.HoleCards[0].Rank, (int)bot.HoleCards[1].Rank) : 2;
            int lo = bot.HoleCards.Count == 2 ? (int)Math.Min((int)bot.HoleCards[0].Rank, (int)bot.HoleCards[1].Rank) : 2;

            bool goIn = isPair
                || (hi == 14 && lo >= 9)
                || (hi == 13 && lo >= 11 && rng.NextDouble() < 0.7)
                || (hi >= 12 && rng.NextDouble() < 0.25);

            bot.GutsChoice = goIn ? GutsDecision.In : GutsDecision.Out;
            bot.LastAction = "DECIDED";
        }
    }

    public bool AreAllDecided(IList<HoldemPlayer> players)
    {
        var active = players.Where(p => !p.SittingOut).ToList();
        return active.Count > 0 && active.All(p => p.GutsChoice != GutsDecision.Undecided);
    }

    public void ResolveShowdown(IList<HoldemPlayer> players)
    {
        Stage = GutsStage.Showdown;
        WinnerSeats.Clear();

        var active = players.Where(p => !p.SittingOut).ToList();
        var inPlayers = active.Where(p => p.GutsChoice == GutsDecision.In).ToList();

        foreach (var p in active)
        {
            if (p.GutsChoice == GutsDecision.Out)
            {
                p.LastAction = "OUT";
            }
            else if (p.GutsChoice == GutsDecision.In)
            {
                p.LastAction = "IN";
                p.ShowdownHandName = DescribeTwoCardHand(p.HoleCards);
            }
        }

        if (inPlayers.Count == 0)
        {
            BannerMessage = $"EVERYONE OUT! POT ${Pot} CARRIES OVER";
            _log($"GUTS #{RoundNumber}: Everyone went OUT. Pot ${Pot} carries over.");
            return;
        }

        if (inPlayers.Count == 1)
        {
            var solo = inPlayers[0];
            int won = Pot;
            solo.Chips += won;
            solo.LastWinAmount = won;
            solo.GutsNet += won;
            WinnerSeats.Add(solo.SeatIndex);
            Pot = 0;
            BannerMessage = $"{solo.Name} WINS ${won} UNCONTESTED!";
            _log($"GUTS #{RoundNumber}: {solo.Name} went IN alone and wins ${won}!");
            return;
        }

        // 2+ players went IN: best 2-card hand wins/splits pot; every loser matches the pot!
        int potAtStake = Pot;
        var scores = inPlayers.ToDictionary(p => p.SeatIndex, p => ScoreTwoCardHand(p.HoleCards));
        int bestScore = scores.Values.Max();
        var winners = inPlayers.Where(p => scores[p.SeatIndex] == bestScore).OrderBy(p => p.SeatIndex).ToList();
        var losers = inPlayers.Where(p => scores[p.SeatIndex] < bestScore).ToList();

        int share = potAtStake / winners.Count;
        int rem = potAtStake % winners.Count;
        for (int i = 0; i < winners.Count; i++)
        {
            int award = share + (i < rem ? 1 : 0);
            winners[i].Chips += award;
            winners[i].LastWinAmount = award;
            winners[i].GutsNet += award;
            winners[i].LastAction = $"WON +${award}";
            WinnerSeats.Add(winners[i].SeatIndex);
        }

        int nextPot = 0;
        foreach (var loser in losers)
        {
            int penalty = Math.Min(potAtStake, loser.Chips);
            loser.Chips -= penalty;
            loser.GutsNet -= penalty;
            loser.LastAction = $"MATCH -${penalty}";
            nextPot += penalty;
        }

        Pot = nextPot;

        string winNames = string.Join(" & ", winners.Select(w => w.Name));
        BannerMessage = losers.Count > 0
            ? $"{winNames} WON ${potAtStake}! {losers.Count} MATCHED -> NEXT POT ${Pot}"
            : $"SPLIT POT ${potAtStake}: {winNames}";
        _log($"GUTS #{RoundNumber}: {BannerMessage}");
    }

    public static int ScoreTwoCardHand(IReadOnlyList<Card> cards)
    {
        if (cards.Count < 2) return 0;
        int r1 = (int)cards[0].Rank;
        int r2 = (int)cards[1].Rank;
        int hi = Math.Max(r1, r2);
        int lo = Math.Min(r1, r2);
        if (hi == lo)
            return (1 << 12) | (hi << 4);
        return (hi << 4) | lo;
    }

    public static string DescribeTwoCardHand(IReadOnlyList<Card> cards)
    {
        if (cards.Count < 2) return string.Empty;
        int r1 = (int)cards[0].Rank;
        int r2 = (int)cards[1].Rank;
        int hi = Math.Max(r1, r2);
        int lo = Math.Min(r1, r2);
        if (hi == lo)
            return $"PAIR OF {RankLabel(hi)}S";
        return $"{RankLabel(hi)}-{RankLabel(lo)} HIGH";
    }

    private static string RankLabel(int r) => r switch
    {
        14 => "ACE",
        13 => "KING",
        12 => "QUEEN",
        11 => "JACK",
        10 => "10",
        _ => r.ToString()
    };
}
