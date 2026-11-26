using BalatroPoker.Core.Cards;

namespace BalatroPoker.Core.Holdem;

public enum HandCategory
{
    HighCard = 0,
    OnePair = 1,
    TwoPair = 2,
    ThreeOfAKind = 3,
    Straight = 4,
    Flush = 5,
    FullHouse = 6,
    FourOfAKind = 7,
    StraightFlush = 8,
    RoyalFlush = 9
}

public readonly record struct HandRank(
    HandCategory Category,
    int Score,
    Card[] BestFive,
    string Description) : IComparable<HandRank>
{
    public int CompareTo(HandRank other) => Score.CompareTo(other.Score);
}

public static class HandEvaluator
{
    public static HandRank Evaluate(IReadOnlyList<Card> cards)
    {
        if (cards.Count < 5)
            throw new ArgumentException("Need at least 5 cards to evaluate a poker hand.", nameof(cards));

        if (cards.Count == 5)
            return EvaluateFive(cards[0], cards[1], cards[2], cards[3], cards[4]);

        HandRank best = default;
        bool hasBest = false;
        int n = cards.Count;

        for (int a = 0; a < n - 4; a++)
        for (int b = a + 1; b < n - 3; b++)
        for (int c = b + 1; c < n - 2; c++)
        for (int d = c + 1; d < n - 1; d++)
        for (int e = d + 1; e < n; e++)
        {
            HandRank rank = EvaluateFive(cards[a], cards[b], cards[c], cards[d], cards[e]);
            if (!hasBest || rank.Score > best.Score)
            {
                best = rank;
                hasBest = true;
            }
        }

        return best;
    }

    /// <summary>
    /// Evaluates an Omaha Hold'em hand: MUST use exactly 2 of the player's hole cards
    /// and exactly 3 of the community board cards.
    /// </summary>
    public static HandRank EvaluateOmaha(IReadOnlyList<Card> holeCards, IReadOnlyList<Card> boardCards)
    {
        if (holeCards.Count < 2)
            throw new ArgumentException("Need at least 2 hole cards for Omaha.", nameof(holeCards));
        if (boardCards.Count < 3)
            throw new ArgumentException("Need at least 3 board cards for Omaha.", nameof(boardCards));

        HandRank best = default;
        bool hasBest = false;

        for (int h1 = 0; h1 < holeCards.Count - 1; h1++)
        for (int h2 = h1 + 1; h2 < holeCards.Count; h2++)
        {
            for (int b1 = 0; b1 < boardCards.Count - 2; b1++)
            for (int b2 = b1 + 1; b2 < boardCards.Count - 1; b2++)
            for (int b3 = b2 + 1; b3 < boardCards.Count; b3++)
            {
                HandRank rank = EvaluateFive(holeCards[h1], holeCards[h2], boardCards[b1], boardCards[b2], boardCards[b3]);
                if (!hasBest || rank.Score > best.Score)
                {
                    best = rank;
                    hasBest = true;
                }
            }
        }

        return best;
    }

    public static HandRank EvaluateFive(Card c0, Card c1, Card c2, Card c3, Card c4)
    {
        Span<Card> hand = stackalloc Card[5] { c0, c1, c2, c3, c4 };
        // Sort descending by rank
        for (int i = 0; i < 4; i++)
        {
            for (int j = i + 1; j < 5; j++)
            {
                if (hand[j].Rank > hand[i].Rank)
                {
                    (hand[i], hand[j]) = (hand[j], hand[i]);
                }
            }
        }

        bool isFlush = hand[0].Suit == hand[1].Suit &&
                       hand[1].Suit == hand[2].Suit &&
                       hand[2].Suit == hand[3].Suit &&
                       hand[3].Suit == hand[4].Suit;

        int r0 = (int)hand[0].Rank;
        int r1 = (int)hand[1].Rank;
        int r2 = (int)hand[2].Rank;
        int r3 = (int)hand[3].Rank;
        int r4 = (int)hand[4].Rank;

        bool isWheel = r0 == 14 && r1 == 5 && r2 == 4 && r3 == 3 && r4 == 2;
        bool isRegularStraight = r0 == r1 + 1 && r1 == r2 + 1 && r2 == r3 + 1 && r3 == r4 + 1;
        bool isStraight = isRegularStraight || isWheel;
        int straightHigh = isWheel ? 5 : r0;

        Card[] bestFive = { hand[0], hand[1], hand[2], hand[3], hand[4] };

        if (isFlush && isStraight)
        {
            if (straightHigh == 14)
            {
                return new HandRank(
                    HandCategory.RoyalFlush,
                    EncodeScore(HandCategory.RoyalFlush, 14, 0, 0, 0, 0),
                    bestFive,
                    "ROYAL FLUSH");
            }

            return new HandRank(
                HandCategory.StraightFlush,
                EncodeScore(HandCategory.StraightFlush, straightHigh, 0, 0, 0, 0),
                bestFive,
                $"STRAIGHT FLUSH ({RankName(straightHigh)} HIGH)");
        }

        // Count rank frequencies
        Span<int> counts = stackalloc int[15];
        counts[r0]++;
        counts[r1]++;
        counts[r2]++;
        counts[r3]++;
        counts[r4]++;

        int quad = 0, trips = 0, pairHigh = 0, pairLow = 0;
        Span<int> kickers = stackalloc int[5];
        int kickerCount = 0;

        for (int r = 14; r >= 2; r--)
        {
            switch (counts[r])
            {
                case 4:
                    quad = r;
                    break;
                case 3:
                    trips = r;
                    break;
                case 2:
                    if (pairHigh == 0) pairHigh = r;
                    else pairLow = r;
                    break;
                case 1:
                    kickers[kickerCount++] = r;
                    break;
            }
        }

        if (quad > 0)
        {
            return new HandRank(
                HandCategory.FourOfAKind,
                EncodeScore(HandCategory.FourOfAKind, quad, kickers[0], 0, 0, 0),
                bestFive,
                $"FOUR OF A KIND ({RankPlural(quad)})");
        }

        if (trips > 0 && pairHigh > 0)
        {
            return new HandRank(
                HandCategory.FullHouse,
                EncodeScore(HandCategory.FullHouse, trips, pairHigh, 0, 0, 0),
                bestFive,
                $"FULL HOUSE ({RankPlural(trips)} FULL OF {RankPlural(pairHigh)})");
        }

        if (isFlush)
        {
            return new HandRank(
                HandCategory.Flush,
                EncodeScore(HandCategory.Flush, r0, r1, r2, r3, r4),
                bestFive,
                $"FLUSH ({RankName(r0)} HIGH)");
        }

        if (isStraight)
        {
            return new HandRank(
                HandCategory.Straight,
                EncodeScore(HandCategory.Straight, straightHigh, 0, 0, 0, 0),
                bestFive,
                $"STRAIGHT ({RankName(straightHigh)} HIGH)");
        }

        if (trips > 0)
        {
            return new HandRank(
                HandCategory.ThreeOfAKind,
                EncodeScore(HandCategory.ThreeOfAKind, trips, kickers[0], kickers[1], 0, 0),
                bestFive,
                $"THREE OF A KIND ({RankPlural(trips)})");
        }

        if (pairHigh > 0 && pairLow > 0)
        {
            return new HandRank(
                HandCategory.TwoPair,
                EncodeScore(HandCategory.TwoPair, pairHigh, pairLow, kickers[0], 0, 0),
                bestFive,
                $"TWO PAIR ({RankPlural(pairHigh)} & {RankPlural(pairLow)})");
        }

        if (pairHigh > 0)
        {
            return new HandRank(
                HandCategory.OnePair,
                EncodeScore(HandCategory.OnePair, pairHigh, kickers[0], kickers[1], kickers[2], 0),
                bestFive,
                $"PAIR OF {RankPlural(pairHigh)}");
        }

        return new HandRank(
            HandCategory.HighCard,
            EncodeScore(HandCategory.HighCard, r0, r1, r2, r3, r4),
            bestFive,
            $"HIGH CARD ({RankName(r0)})");
    }

    private static int EncodeScore(HandCategory cat, int t1, int t2, int t3, int t4, int t5)
    {
        return ((int)cat << 20) | (t1 << 16) | (t2 << 12) | (t3 << 8) | (t4 << 4) | t5;
    }

    private static string RankName(int r) => r switch
    {
        14 => "ACE",
        13 => "KING",
        12 => "QUEEN",
        11 => "JACK",
        10 => "TEN",
        9 => "NINE",
        8 => "EIGHT",
        7 => "SEVEN",
        6 => "SIX",
        5 => "FIVE",
        4 => "FOUR",
        3 => "THREE",
        2 => "TWO",
        _ => r.ToString()
    };

    private static string RankPlural(int r) => r switch
    {
        6 => "SIXES",
        _ => RankName(r) + "S"
    };
}
