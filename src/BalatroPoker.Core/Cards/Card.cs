using System.Security.Cryptography;
using System.Text.Json.Serialization;

namespace BalatroPoker.Core.Cards;

public enum Suit
{
    Spades = 0,
    Hearts = 1,
    Diamonds = 2,
    Clubs = 3
}

public enum Rank
{
    Two = 2,
    Three = 3,
    Four = 4,
    Five = 5,
    Six = 6,
    Seven = 7,
    Eight = 8,
    Nine = 9,
    Ten = 10,
    Jack = 11,
    Queen = 12,
    King = 13,
    Ace = 14
}

public readonly record struct Card(Rank Rank, Suit Suit, bool FaceDown = false)
{
    [JsonIgnore]
    public bool IsRed => Suit is Suit.Hearts or Suit.Diamonds;

    [JsonIgnore]
    public int BaccaratValue => Rank switch
    {
        Rank.Ace => 1,
        >= Rank.Two and <= Rank.Nine => (int)Rank,
        _ => 0
    };

    public string ShortRank => Rank switch
    {
        Rank.Ten => "10",
        Rank.Jack => "J",
        Rank.Queen => "Q",
        Rank.King => "K",
        Rank.Ace => "A",
        _ => ((int)Rank).ToString()
    };

    public string SuitChar => Suit switch
    {
        Suit.Spades => "S",
        Suit.Hearts => "H",
        Suit.Diamonds => "D",
        Suit.Clubs => "C",
        _ => "?"
    };

    public static Card Hidden => new(Rank.Two, Suit.Spades, FaceDown: true);

    public override string ToString() => FaceDown ? "??" : $"{ShortRank}{SuitChar}";
}

public sealed class Deck
{
    private readonly List<Card> _cards = new();
    private readonly int _numDecks;
    private readonly int _reshuffleThreshold;
    private readonly Random? _deterministicRng;

    public int Remaining => _cards.Count;
    public int TotalCards => _numDecks * 52;

    public Deck(int numDecks = 1, int reshuffleThreshold = 0, Random? deterministicRng = null)
    {
        _numDecks = Math.Max(1, numDecks);
        _reshuffleThreshold = reshuffleThreshold;
        _deterministicRng = deterministicRng;
        ResetAndShuffle();
    }

    public void ResetAndShuffle()
    {
        _cards.Clear();
        for (int d = 0; d < _numDecks; d++)
        {
            foreach (Suit suit in Enum.GetValues<Suit>())
            {
                foreach (Rank rank in Enum.GetValues<Rank>())
                {
                    _cards.Add(new Card(rank, suit));
                }
            }
        }
        Shuffle();
    }

    public void Shuffle()
    {
        for (int i = _cards.Count - 1; i > 0; i--)
        {
            int j = _deterministicRng is not null
                ? _deterministicRng.Next(i + 1)
                : RandomNumberGenerator.GetInt32(i + 1);
            (_cards[i], _cards[j]) = (_cards[j], _cards[i]);
        }
    }

    public bool NeedsReshuffle => _cards.Count <= _reshuffleThreshold;

    public Card Draw()
    {
        if (_cards.Count == 0)
        {
            ResetAndShuffle();
        }
        int last = _cards.Count - 1;
        Card card = _cards[last];
        _cards.RemoveAt(last);
        return card;
    }

    public void Burn()
    {
        if (_cards.Count > 0)
        {
            _cards.RemoveAt(_cards.Count - 1);
        }
    }
}
