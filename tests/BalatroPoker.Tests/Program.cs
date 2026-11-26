using BalatroPoker.Core.Audio;
using BalatroPoker.Core.Baccarat;
using BalatroPoker.Core.Blackjack;
using BalatroPoker.Core.Cards;
using BalatroPoker.Core.Guts;
using BalatroPoker.Core.Holdem;
using BalatroPoker.Core.Models;
using BalatroPoker.Core.Networking;

int passed = 0;
int failed = 0;

void Assert(bool condition, string message)
{
    if (!condition)
        throw new Exception($"ASSERTION FAILED: {message}");
}

async Task RunTest(string name, Func<Task> test)
{
    try
    {
        await test();
        Console.WriteLine($"[PASS] {name}");
        passed++;
    }
    catch (Exception ex)
    {
        Console.Error.WriteLine($"[FAIL] {name}: {ex.Message}");
        failed++;
    }
}

Card C(Rank r, Suit s) => new(r, s);

await RunTest("Hold'em Evaluator: All 10 hand categories & ordering", () =>
{
    var royal = HandEvaluator.Evaluate(new[] { C(Rank.Ace, Suit.Spades), C(Rank.King, Suit.Spades), C(Rank.Queen, Suit.Spades), C(Rank.Jack, Suit.Spades), C(Rank.Ten, Suit.Spades), C(Rank.Two, Suit.Hearts), C(Rank.Three, Suit.Clubs) });
    var strFlush = HandEvaluator.Evaluate(new[] { C(Rank.Nine, Suit.Hearts), C(Rank.Eight, Suit.Hearts), C(Rank.Seven, Suit.Hearts), C(Rank.Six, Suit.Hearts), C(Rank.Five, Suit.Hearts), C(Rank.Ace, Suit.Clubs), C(Rank.King, Suit.Diamonds) });
    var quads = HandEvaluator.Evaluate(new[] { C(Rank.King, Suit.Spades), C(Rank.King, Suit.Hearts), C(Rank.King, Suit.Diamonds), C(Rank.King, Suit.Clubs), C(Rank.Ace, Suit.Spades) });
    var fullHouse = HandEvaluator.Evaluate(new[] { C(Rank.Queen, Suit.Spades), C(Rank.Queen, Suit.Hearts), C(Rank.Queen, Suit.Diamonds), C(Rank.Ten, Suit.Clubs), C(Rank.Ten, Suit.Spades) });
    var flush = HandEvaluator.Evaluate(new[] { C(Rank.Ace, Suit.Diamonds), C(Rank.Jack, Suit.Diamonds), C(Rank.Eight, Suit.Diamonds), C(Rank.Four, Suit.Diamonds), C(Rank.Two, Suit.Diamonds) });
    var straight = HandEvaluator.Evaluate(new[] { C(Rank.Six, Suit.Spades), C(Rank.Five, Suit.Hearts), C(Rank.Four, Suit.Diamonds), C(Rank.Three, Suit.Clubs), C(Rank.Two, Suit.Spades) });
    var wheel = HandEvaluator.Evaluate(new[] { C(Rank.Ace, Suit.Spades), C(Rank.Five, Suit.Hearts), C(Rank.Four, Suit.Diamonds), C(Rank.Three, Suit.Clubs), C(Rank.Two, Suit.Spades) });
    var trips = HandEvaluator.Evaluate(new[] { C(Rank.Eight, Suit.Spades), C(Rank.Eight, Suit.Hearts), C(Rank.Eight, Suit.Diamonds), C(Rank.Ace, Suit.Clubs), C(Rank.Two, Suit.Spades) });
    var twoPair = HandEvaluator.Evaluate(new[] { C(Rank.Jack, Suit.Spades), C(Rank.Jack, Suit.Hearts), C(Rank.Nine, Suit.Diamonds), C(Rank.Nine, Suit.Clubs), C(Rank.Ace, Suit.Spades) });
    var onePair = HandEvaluator.Evaluate(new[] { C(Rank.Ten, Suit.Spades), C(Rank.Ten, Suit.Hearts), C(Rank.Ace, Suit.Diamonds), C(Rank.King, Suit.Clubs), C(Rank.Queen, Suit.Spades) });
    var highCard = HandEvaluator.Evaluate(new[] { C(Rank.Ace, Suit.Spades), C(Rank.King, Suit.Hearts), C(Rank.Nine, Suit.Diamonds), C(Rank.Five, Suit.Clubs), C(Rank.Two, Suit.Spades) });

    Assert(royal.Category == HandCategory.RoyalFlush, "Royal Flush");
    Assert(strFlush.Category == HandCategory.StraightFlush, "Straight Flush");
    Assert(quads.Category == HandCategory.FourOfAKind, "Four of a Kind");
    Assert(fullHouse.Category == HandCategory.FullHouse, "Full House");
    Assert(flush.Category == HandCategory.Flush, "Flush");
    Assert(straight.Category == HandCategory.Straight, "Straight");
    Assert(wheel.Category == HandCategory.Straight, "Wheel Straight");
    Assert(trips.Category == HandCategory.ThreeOfAKind, "Three of a Kind");
    Assert(twoPair.Category == HandCategory.TwoPair, "Two Pair");
    Assert(onePair.Category == HandCategory.OnePair, "One Pair");
    Assert(highCard.Category == HandCategory.HighCard, "High Card");

    Assert(royal.Score > strFlush.Score, "Royal > SF");
    Assert(strFlush.Score > quads.Score, "SF > Quads");
    Assert(quads.Score > fullHouse.Score, "Quads > FH");
    Assert(fullHouse.Score > flush.Score, "FH > Flush");
    Assert(flush.Score > straight.Score, "Flush > Straight");
    Assert(straight.Score > wheel.Score, "6-high straight > 5-high wheel");
    Assert(wheel.Score > trips.Score, "Wheel > Trips");
    Assert(trips.Score > twoPair.Score, "Trips > TwoPair");
    Assert(twoPair.Score > onePair.Score, "TwoPair > OnePair");
    Assert(onePair.Score > highCard.Score, "OnePair > HighCard");
    return Task.CompletedTask;
});

await RunTest("Hold'em Engine: Heads-up blinds, 10-player chip conservation & full street progression", () =>
{
    var cfg = new GameConfig { SmallBlind = 10, BigBlind = 20, StartingChips = 1000 };
    var engine = new HoldemEngine(deterministicRng: new Random(42));

    // Heads-up test
    var huPlayers = new List<HoldemPlayer>
    {
        new() { SeatIndex = 0, PlayerId = "p0", Name = "P0", Chips = 1000 },
        new() { SeatIndex = 1, PlayerId = "p1", Name = "P1", Chips = 1000 }
    };
    Assert(engine.StartNewHand(huPlayers, cfg), "HU start");
    Assert(engine.DealerSeat == engine.SmallBlindSeat, "Heads-up: Dealer must post Small Blind");
    Assert(engine.CurrentTurnSeat == engine.SmallBlindSeat, "Heads-up: SB/Dealer acts first pre-flop");

    // 10-player full hand to showdown
    var tenPlayers = Enumerable.Range(0, 10)
        .Select(i => new HoldemPlayer { SeatIndex = i, PlayerId = $"p{i}", Name = $"P{i}", Chips = 1000 })
        .ToList();
    var engine10 = new HoldemEngine(deterministicRng: new Random(99));
    Assert(engine10.StartNewHand(tenPlayers, cfg), "10P start");
    Assert(tenPlayers.All(p => p.HoleCards.Count == 2), "All 10 players dealt 2 hole cards");

    int safety = 0;
    while (engine10.Stage != HoldemStage.Showdown && safety++ < 200)
    {
        int turn = engine10.CurrentTurnSeat;
        var p = tenPlayers.First(x => x.SeatIndex == turn);
        int toCall = engine10.CurrentBet - p.CurrentBet;
        var (ok, err) = engine10.ApplyAction(tenPlayers, turn, toCall > 0 ? HoldemActionType.Call : HoldemActionType.Check, 0, cfg);
        Assert(ok, $"Action failed: {err}");
    }

    Assert(engine10.Stage == HoldemStage.Showdown, "Reached showdown");
    Assert(engine10.CommunityCards.Count == 5, "5 community cards dealt");
    Assert(tenPlayers.Sum(p => p.Chips) == 10_000, $"Chip conservation violated: {tenPlayers.Sum(p => p.Chips)}");
    return Task.CompletedTask;
});

await RunTest("Hold'em Engine: Multi-way all-in side pots", () =>
{
    var p0 = new HoldemPlayer { SeatIndex = 0, PlayerId = "p0", Name = "SHORT", TotalCommitted = 100, IsAllIn = true };
    var p1 = new HoldemPlayer { SeatIndex = 1, PlayerId = "p1", Name = "MID", TotalCommitted = 300, IsAllIn = true };
    var p2 = new HoldemPlayer { SeatIndex = 2, PlayerId = "p2", Name = "DEEP", TotalCommitted = 500 };
    var p3 = new HoldemPlayer { SeatIndex = 3, PlayerId = "p3", Name = "FOLDED", TotalCommitted = 200, Folded = true };

    var pots = HoldemEngine.BuildSidePots(new[] { p0, p1, p2, p3 });
    Assert(pots.Count == 3, $"Expected 3 pots, got {pots.Count}");
    Assert(pots[0].Amount == 400 && pots[0].EligibleSeats.SequenceEqual(new[] { 0, 1, 2 }), $"Main pot mismatch: {pots[0].Amount}");
    Assert(pots[1].Amount == 500 && pots[1].EligibleSeats.SequenceEqual(new[] { 1, 2 }), $"Side pot 1 mismatch: {pots[1].Amount}");
    Assert(pots[2].Amount == 200 && pots[2].EligibleSeats.SequenceEqual(new[] { 2 }), $"Side pot 2 mismatch: {pots[2].Amount}");
    Assert(pots.Sum(x => x.Amount) == 1100, "Total pot sum must equal total committed");
    return Task.CompletedTask;
});

await RunTest("Baccarat Engine: Natural 8/9, Third-Card Tableau, Pairs & Commission Payouts", () =>
{
    // Verify Banker 3rd-card tableau edge cases
    Assert(BaccaratEngine.ShouldBankerDraw(3, C(Rank.Seven, Suit.Hearts)), "Banker 3 draws on P3=7");
    Assert(!BaccaratEngine.ShouldBankerDraw(3, C(Rank.Eight, Suit.Hearts)), "Banker 3 stands on P3=8");
    Assert(BaccaratEngine.ShouldBankerDraw(4, C(Rank.Two, Suit.Hearts)), "Banker 4 draws on P3=2");
    Assert(!BaccaratEngine.ShouldBankerDraw(4, C(Rank.Ace, Suit.Hearts)), "Banker 4 stands on P3=1");
    Assert(BaccaratEngine.ShouldBankerDraw(5, C(Rank.Four, Suit.Hearts)), "Banker 5 draws on P3=4");
    Assert(!BaccaratEngine.ShouldBankerDraw(5, C(Rank.Three, Suit.Hearts)), "Banker 5 stands on P3=3");
    Assert(BaccaratEngine.ShouldBankerDraw(6, C(Rank.Six, Suit.Hearts)), "Banker 6 draws on P3=6");
    Assert(!BaccaratEngine.ShouldBankerDraw(6, C(Rank.Five, Suit.Hearts)), "Banker 6 stands on P3=5");
    Assert(!BaccaratEngine.ShouldBankerDraw(7, C(Rank.Six, Suit.Hearts)), "Banker 7 always stands");
    Assert(BaccaratEngine.ShouldBankerDraw(5, null), "When Player stands, Banker draws on 5");
    Assert(!BaccaratEngine.ShouldBankerDraw(6, null), "When Player stands, Banker stands on 6");

    // Controlled deal: Banker wins with a Natural 9 AND Banker Pair (9+10? No, let's do Banker 4+4=8 natural pair vs Player 2+3=5!)
    var engine = new BaccaratEngine();
    var p = new HoldemPlayer { SeatIndex = 0, PlayerId = "p0", Name = "BETTOR", Chips = 1000 };
    engine.StartBettingPhase(new[] { p });
    Assert(engine.PlaceBet(p, BaccaratBetType.Banker, 100).Ok, "Place Banker bet");
    Assert(engine.PlaceBet(p, BaccaratBetType.BankerPair, 10).Ok, "Place BankerPair bet");
    Assert(p.Chips == 890, "Chips deducted on wager");

    var script = new Queue<Card>(new[]
    {
        C(Rank.Two, Suit.Spades),   // P1
        C(Rank.Four, Suit.Hearts),  // B1
        C(Rank.Three, Suit.Clubs),  // P2 -> Player 5
        C(Rank.Four, Suit.Diamonds) // B2 -> Banker Natural 8 + Pair!
    });
    engine.DealAndSettle(new[] { p }, () => script.Dequeue());

    Assert(engine.WasNatural, "Should be Natural 8");
    Assert(engine.WasBankerPair, "Should be Banker Pair");
    Assert(engine.PlayerCards.Count == 2 && engine.BankerCards.Count == 2, "Natural stops drawing");
    Assert(engine.LastOutcome == BaccaratOutcome.BankerWin, "Banker wins");
    // Banker 100 returns 100 + 95 = 195; BankerPair 10 returns 10 * 12 = 120; Total returned = 315; Chips = 890 + 315 = 1205
    Assert(p.Chips == 1205, $"Expected 1205 chips after 5% commission & 11:1 pair payout, got {p.Chips}");

    // Controlled Tie push test
    var p2 = new HoldemPlayer { SeatIndex = 0, PlayerId = "p2", Name = "TIEBET", Chips = 1000 };
    engine.StartBettingPhase(new[] { p2 });
    engine.PlaceBet(p2, BaccaratBetType.Player, 100);
    engine.PlaceBet(p2, BaccaratBetType.Tie, 10);
    var tieScript = new Queue<Card>(new[]
    {
        C(Rank.Nine, Suit.Spades),  // P1
        C(Rank.Nine, Suit.Hearts),  // B1
        C(Rank.King, Suit.Clubs),   // P2 -> Natural 9
        C(Rank.Queen, Suit.Diamonds)// B2 -> Natural 9
    });
    engine.DealAndSettle(new[] { p2 }, () => tieScript.Dequeue());
    Assert(engine.LastOutcome == BaccaratOutcome.Tie, "Tie outcome");
    // 1000 - 110 = 890; Player 100 pushes (+100); Tie 10 pays 8:1 (+90) -> 890 + 190 = 1080
    Assert(p2.Chips == 1080, $"Expected 1080 chips on Tie push + 8:1 tie win, got {p2.Chips}");
    return Task.CompletedTask;
});

await RunTest("P2P Networking: 10-player TCP room, seat limit enforcement, card masking & live actions", async () =>
{
    using var host = new GameRoomHost("HOST", new GameConfig { MaxPlayers = 10, StartingChips = 1000 }, deterministicRng: new Random(7));
    host.EnableLanBroadcast = false;
    host.Start(port: 0); // OS-assigned ephemeral port

    var clients = new List<GameRoomClient>();
    try
    {
        for (int i = 1; i <= 9; i++)
        {
            var c = new GameRoomClient();
            var (ok, err) = await c.ConnectAsync("127.0.0.1", host.Port, $"PEER{i}");
            Assert(ok, $"Peer {i} failed to connect: {err}");
            clients.Add(c);
        }

        // 11th player must be rejected
        using var overflow = new GameRoomClient();
        var (overflowOk, _) = await overflow.ConnectAsync("127.0.0.1", host.Port, "OVERFLOW");
        Assert(!overflowOk, "11th player should be rejected when room is full (10/10)");

        Assert(host.StartGame(), "Host starts 10-player Hold'em game");

        // Wait briefly for snapshots to arrive at clients
        for (int w = 0; w < 30 && clients[0].LatestSnapshot?.Phase != RoomPhase.InGame; w++)
            await Task.Delay(25);

        var c0Snap = clients[0].LatestSnapshot;
        Assert(c0Snap is not null && c0Snap.Seats.Count == 10, $"Client sees all 10 seats (snap={(c0Snap is null ? "null" : $"phase={c0Snap.Phase},seats={c0Snap.Seats.Count}")}, status={clients[0].StatusMessage})");

        // Verify security: Client 0 sees own cards face-up, host's cards face-down
        var mySeat = c0Snap!.Seats.First(s => s.PlayerId == clients[0].PlayerId);
        var hostSeat = c0Snap.Seats.First(s => s.IsHost);
        Assert(mySeat.HoleCards.Count == 2 && mySeat.HoleCards.All(c => !c.FaceDown), "Client sees own hole cards");
        Assert(hostSeat.HoleCards.Count == 2 && hostSeat.HoleCards.All(c => c.FaceDown), "Opponent hole cards must be masked");

        // Switch to Baccarat and place remote bet from Client 0
        host.SwitchGameMode(GameMode.Baccarat);
        await clients[0].SendBaccaratBetAsync(BaccaratBetType.Player, 50);
        await Task.Delay(80);
        var hostView = host.BuildSnapshotFor(host.HostPlayerId);
        var peer0OnHost = hostView.Seats.First(s => s.PlayerId == clients[0].PlayerId);
        Assert(peer0OnHost.BaccaratBets.Player == 50, $"Expected remote Baccarat bet of 50, got {peer0OnHost.BaccaratBets.Player}");
    }
    finally
    {
        foreach (var c in clients) c.Dispose();
    }
});

await RunTest("Host Bot Placement: Specific seats & bot count controls", () =>
{
    using var host = new GameRoomHost("HOST", new GameConfig { MaxPlayers = 10 });
    Assert(!host.AddBotAtSeat(0), "Cannot place bot in host's seat 0");
    Assert(host.AddBotAtSeat(4, "MIDBOT"), "Place bot at seat 4");
    Assert(host.AddBotAtSeat(9, "LASTBOT"), "Place bot at seat 9");
    Assert(!host.AddBotAtSeat(4), "Cannot double-occupy seat 4");

    var snap = host.BuildSnapshotFor(host.HostPlayerId);
    Assert(snap.Seats.Any(s => s.SeatIndex == 4 && s.IsBot && s.Name == "MIDBOT"), "MIDBOT in seat 4");
    Assert(snap.Seats.Any(s => s.SeatIndex == 9 && s.IsBot && s.Name == "LASTBOT"), "LASTBOT in seat 9");

    Assert(host.RemoveBotAtSeat(4), "Remove bot from seat 4");
    Assert(!host.RemoveBotAtSeat(0), "Cannot remove human host via RemoveBotAtSeat");

    host.SetBotCount(5);
    snap = host.BuildSnapshotFor(host.HostPlayerId);
    Assert(snap.Seats.Count(s => s.IsBot) == 5, $"Expected 5 bots, got {snap.Seats.Count(s => s.IsBot)}");

    host.SetBotCount(0);
    snap = host.BuildSnapshotFor(host.HostPlayerId);
    Assert(snap.Seats.Count == 1 && snap.Seats[0].IsHost, "Only human host remains after SetBotCount(0)");
    return Task.CompletedTask;
});

await RunTest("Omaha Hold'em: Strict 2-hole + 3-board rule & 4-card deal", () =>
{
    // Board has 4 Hearts, player has ONLY 1 Heart (Ace of Hearts) + 3 offsuit cards -> CANNOT make a Flush in Omaha!
    var board = new[]
    {
        C(Rank.King, Suit.Hearts), C(Rank.Queen, Suit.Hearts), C(Rank.Jack, Suit.Hearts),
        C(Rank.Nine, Suit.Hearts), C(Rank.Two, Suit.Clubs)
    };
    var oneHeartHole = new[]
    {
        C(Rank.Ace, Suit.Hearts), C(Rank.Seven, Suit.Spades), C(Rank.Five, Suit.Diamonds), C(Rank.Three, Suit.Clubs)
    };
    var rankNoFlush = HandEvaluator.EvaluateOmaha(oneHeartHole, board);
    Assert(rankNoFlush.Category != HandCategory.Flush, $"Omaha must require 2 hole cards of suit for Flush, got {rankNoFlush.Category}");

    // With 2 Hearts in hole (Ah + 4h), player makes Ace-high Flush using Ah+4h + Kh+Qh+Jh
    var twoHeartHole = new[]
    {
        C(Rank.Ace, Suit.Hearts), C(Rank.Four, Suit.Hearts), C(Rank.Five, Suit.Diamonds), C(Rank.Three, Suit.Clubs)
    };
    var rankFlush = HandEvaluator.EvaluateOmaha(twoHeartHole, board);
    Assert(rankFlush.Category == HandCategory.Flush, $"Expected Flush with 2 hole hearts, got {rankFlush.Category}");

    // Engine deals 4 hole cards per player in Omaha mode
    var cfg = new GameConfig { Mode = GameMode.OmahaHoldem, SmallBlind = 10, BigBlind = 20 };
    var players = Enumerable.Range(0, 4).Select(i => new HoldemPlayer { SeatIndex = i, PlayerId = $"o{i}", Name = $"O{i}", Chips = 1000 }).ToList();
    var eng = new HoldemEngine(deterministicRng: new Random(123));
    Assert(eng.StartNewHand(players, cfg), "Omaha hand started");
    Assert(players.All(p => p.HoleCards.Count == 4), "Every Omaha player must receive 4 hole cards");
    return Task.CompletedTask;
});

await RunTest("Guts Poker: Ante, IN/OUT showdown & loser pot-match carryover", () =>
{
    var cfg = new GameConfig { Mode = GameMode.GutsPoker, BigBlind = 20 };
    var p0 = new HoldemPlayer { SeatIndex = 0, PlayerId = "g0", Name = "G0", Chips = 1000 };
    var p1 = new HoldemPlayer { SeatIndex = 1, PlayerId = "g1", Name = "G1", Chips = 1000 };
    var p2 = new HoldemPlayer { SeatIndex = 2, PlayerId = "g2", Name = "G2", Chips = 1000 };
    var list = new List<HoldemPlayer> { p0, p1, p2 };

    var guts = new GutsEngine(deterministicRng: new Random(11));
    Assert(guts.StartNewRound(list, cfg), "Guts round 1 started");
    Assert(guts.Pot == 60, $"Expected $60 pot (3 x $20 ante), got ${guts.Pot}");

    // Force known 2-card hands: p0 has Pair of Aces, p1 has King-Queen, p2 folds OUT
    p0.HoleCards.Clear(); p0.HoleCards.Add(C(Rank.Ace, Suit.Spades)); p0.HoleCards.Add(C(Rank.Ace, Suit.Hearts));
    p1.HoleCards.Clear(); p1.HoleCards.Add(C(Rank.King, Suit.Spades)); p1.HoleCards.Add(C(Rank.Queen, Suit.Hearts));

    guts.SubmitDecision(list, 0, GutsDecision.In);
    guts.SubmitDecision(list, 1, GutsDecision.In);
    guts.SubmitDecision(list, 2, GutsDecision.Out);
    Assert(guts.AreAllDecided(list), "All decided");
    guts.ResolveShowdown(list);

    // p0 started 980 -> wins $60 -> 1040; p1 started 980 -> matches $60 -> 920; p2 stays 980; next Pot = $60!
    Assert(p0.Chips == 1040, $"Winner p0 should have 1040, got {p0.Chips}");
    Assert(p1.Chips == 920, $"Loser p1 should match $60 and have 920, got {p1.Chips}");
    Assert(p2.Chips == 980, $"OUT player p2 should keep 980, got {p2.Chips}");
    Assert(guts.Pot == 60, $"Carryover pot should be $60, got {guts.Pot}");
    return Task.CompletedTask;
});

await RunTest("21 / Blackjack: Natural 3:2 payout, Soft Ace handling & Double Down", () =>
{
    var (softTot, isSoft, isBj) = BlackjackEngine.EvaluateHand(new[] { C(Rank.Ace, Suit.Spades), C(Rank.Six, Suit.Hearts) });
    Assert(softTot == 17 && isSoft && !isBj, "A+6 = Soft 17");
    var (hardTot, isSoft2, _) = BlackjackEngine.EvaluateHand(new[] { C(Rank.Ace, Suit.Spades), C(Rank.Six, Suit.Hearts), C(Rank.Nine, Suit.Clubs) });
    Assert(hardTot == 16 && !isSoft2, "A+6+9 = Hard 16");

    var bj = new BlackjackEngine();
    var p0 = new HoldemPlayer { SeatIndex = 0, PlayerId = "b0", Name = "BJ_NAT", Chips = 1000 };
    var p1 = new HoldemPlayer { SeatIndex = 1, PlayerId = "b1", Name = "BJ_DBL", Chips = 1000 };
    var list = new List<HoldemPlayer> { p0, p1 };

    bj.StartBettingPhase(list);
    Assert(bj.PlaceBet(p0, 100).Ok, "p0 bet 100");
    Assert(bj.PlaceBet(p1, 100).Ok, "p1 bet 100");

    // Deal order: p0-1, p1-1, d-1, p0-2, p1-2, d-2
    var deck = new Queue<Card>(new[]
    {
        C(Rank.Ace, Suit.Spades),  // p0 card 1
        C(Rank.Five, Suit.Hearts), // p1 card 1
        C(Rank.Ten, Suit.Clubs),   // dealer card 1
        C(Rank.King, Suit.Spades), // p0 card 2 -> Natural Blackjack (21)!
        C(Rank.Six, Suit.Hearts),  // p1 card 2 -> 11
        C(Rank.Eight, Suit.Clubs), // dealer card 2 -> 18
        C(Rank.Nine, Suit.Diamonds)// p1 double-down draw -> 20 (beats dealer 18!)
    });

    bj.DealInitial(list, () => deck.Dequeue());
    Assert(p0.BlackjackNatural, "p0 has Natural 21");
    Assert(bj.CurrentTurnSeat == 1, "Turn skips p0 Natural 21 to p1");

    var (ok, err) = bj.ApplyAction(list, 1, BlackjackActionType.DoubleDown, () => deck.Dequeue());
    Assert(ok, $"DoubleDown failed: {err}");
    Assert(bj.Stage == BlackjackStage.Settled, "Round settled after p1 double down");

    // p0: 900 + 100 + 150 (3:2) = 1150
    // p1: 900 - 100 (double) = 800 + 400 (200 bet * 2) = 1200
    Assert(p0.Chips == 1150, $"Expected p0=1150 (3:2 BJ), got {p0.Chips}");
    Assert(p1.Chips == 1200, $"Expected p1=1200 (Double Down win), got {p1.Chips}");
    return Task.CompletedTask;
});

await RunTest("SAM Voice Synth & Pitched Chat Bubble propagation", () =>
{
    var phonemes = SamVoiceSynth.TextToPhonemes("ALL IN! $500");
    Assert(phonemes.Count > 5, "TextToPhonemes should produce phoneme sequence");

    short[] pcmLow = SamVoiceSynth.SynthesizePcm16("HELLO", pitch: 35);
    short[] pcmHigh = SamVoiceSynth.SynthesizePcm16("HELLO", pitch: 110);
    Assert(pcmLow.Length > 2000 && pcmHigh.Length == pcmLow.Length, "PCM buffers generated with matching length");
    Assert(!pcmLow.SequenceEqual(pcmHigh), "Different pitches must produce different waveforms");
    Assert(pcmLow.Any(s => Math.Abs(s) > 1000), "PCM waveform must have audible energy");

    using var host = new GameRoomHost("HOST");
    host.HandleChat(host.HostPlayerId, "ALL IN BABY!", pitch: 96);
    var snap = host.BuildSnapshotFor(host.HostPlayerId);
    var me = snap.Seats[0];
    Assert(me.LastChatMessage == "ALL IN BABY!", "Chat text stored on speaker seat");
    Assert(me.LastChatPitch == 96, $"Expected pitch 96, got {me.LastChatPitch}");
    Assert(me.LastChatId == 1 && me.LastChatUnixMs > 0, "Chat sequence ID & timestamp populated");
    return Task.CompletedTask;
});

Console.WriteLine($"\nTest Summary: {passed} passed, {failed} failed.");
return failed == 0 ? 0 : 1;
