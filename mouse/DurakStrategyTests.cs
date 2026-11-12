using System.Diagnostics;
using System.Text.Json;

internal static class DurakStrategyTests
{
    internal static int Run()
    {
        int checks = 0;
        void Check(bool ok, string name) { checks++; if (!ok) throw new Exception("FAIL: " + name); }
        Check(DurakStrategy.Beats("6S", "AH", 'S'), "Small trump beats nontrump ace");
        Check(!DurakStrategy.Beats("AH", "6S", 'S'), "Nontrump never beats trump");
        Check(!DurakStrategy.Beats("QH", "KH", 'S'), "Rank direction");
        var matchingRank = Make("9H 10H 6D", [new() { Attack = "8C", Defense = "9C" }, new() { Attack = "7H" }]);
        var matchingRequest = Request(matchingRank, "defend");
        matchingRequest.Pairs = [new() { Attack = "8C", Defense = "9C" }, new() { Attack = "7H" }];
        matchingRequest.DefenderStartCount = 4;
        var matchingAnswer = DurakStrategy.Decide(matchingRank, matchingRequest);
        Check(matchingAnswer.Kind == "defend" && matchingAnswer.Card == "9H", $"Prefer already exposed rank when defending: {matchingAnswer.Kind}/{matchingAnswer.Card}/{matchingAnswer.Template}");
        var cover = DurakStrategy.Cover(["AS", "7H"], ["6H", "7S"], 'S');
        Check(cover is { Count: 2 } && cover.Any(m => m.Card == "7H" && m.Target == "6H"), "Complete matching prevents wasted trump");
        Check(DurakStrategy.Cover(["JH", "JC", "AS", "JD"], ["7H", "9H", "7C", "9C"], 'S') is null,
            "Four attacks cannot be beaten by three usable cards");
        var random = new Random(42);
        // Independently enumerate small matchings to verify the DP's existence result.
        for (int i = 0; i < 180; i++)
        {
            var deck = CardCodes.Deck(36); random.Shuffle(deck);
            var hand = deck.Take(6).ToArray(); var attacks = deck.Skip(6).Take(i % 4 + 1).ToArray();
            bool brute(string[] h, string[] a) => a.Length == 0 || h.Any(c => DurakStrategy.Beats(c, a[0], 'S') && brute(h.Where(x => x != c).ToArray(), a.Skip(1).ToArray()));
            Check((DurakStrategy.Cover(hand, attacks, 'S') is not null) == brute(hand, attacks), "DP agrees with independent enumeration");
        }
        var impossible = Make("JH JC AS JD", [new() { Attack = "7H" }, new() { Attack = "9H" }, new() { Attack = "7C" }, new() { Attack = "9C" }]);
        var request = Request(impossible, "defend");
        var answer = DurakStrategy.Decide(impossible, request);
        Check(answer.Template == "CANNOT_COVER_ALL" && answer.Kind == "take" && !answer.RequiresReview, "Unavoidable pickup template");
        var finish = Make("AS 7H", [new() { Attack = "6H" }, new() { Attack = "7S" }]);
        answer = DurakStrategy.Decide(finish, Request(finish, "defend"));
        Check(answer.Template == "DEFENSE_LAST_CARDS" && answer.Plan.Count == 2, "Last cards defence");
        var hazard = Make("JH 7C 8D", [new() { Attack = "6H" }]);
        var hazardRequest = Request(hazard, "defend");
        Check(DurakStrategy.Decide(hazard, hazardRequest).Kind == "defend", "Defend when full coverage exists");
        var world = new Dictionary<string, List<string>> { ["p0"] = ["JH", "7C", "8D"], ["p1"] = ["QC"], ["p2"] = ["JD"] };
        Check(DurakStrategy.DefensePickup(hazard.State, hazardRequest, [new("JH", "6H")], world, 'S') == 1, "Third player can throw");
        world["p2"] = ["KD"];
        Check(DurakStrategy.DefensePickup(hazard.State, hazardRequest, [new("JH", "6H")], world, 'S') == 0, "No imaginary matching rank");
        var start = new CardDocument { State = CardModel.NewGame(36, ["Я", "Слева", "Справа"], CardCodes.ParseMany("6H 6D 8C JC QD AS"), "KS") };
        answer = DurakStrategy.Decide(start, Request(start, "attack"));
        Check(answer.Template == "FIRST_LOW_NONTRUMP" && answer.Card is "6H" or "6D" && answer.Samples == 0 && !answer.RequiresReview, "First move needs no sampling or deliberation");
        var timedOutHand = new CardDocument { State = CardModel.NewGame(36, ["Я", "blessed", "Kkkk"], CardCodes.ParseMany("KH JH JS 9H 8D 6C"), "KC") };
        var firstRequest = Request(timedOutHand, "attack"); firstRequest.Trump = "C";
        var quick = DurakStrategy.Decide(timedOutHand, firstRequest);
        Check(quick.Card == "8D" && quick.Samples == 0 && !quick.RequiresReview, "Actual timed-out hand selects 8D immediately");
        var allTrumps = new CardDocument { State = CardModel.NewGame(36, ["Я", "A", "B"], CardCodes.ParseMany("6S 7S 8S 9S 10S JS"), "AS") };
        quick = DurakStrategy.Decide(allTrumps, Request(allTrumps, "attack"));
        Check(quick.Card == "6S" && quick.Template == "FIRST_LOW_TRUMP" && !quick.RequiresReview, "All-trump opening has a fixed rule");
        var invalid = Request(start, "attack"); invalid.Revision++;
        Check(DurakStrategy.Decide(start, invalid).Kind == "review", "Stale context blocked");
        invalid = Request(start, "attack"); invalid.OurTurn = false;
        Check(DurakStrategy.Decide(start, invalid).Kind == "wait", "Opponent turn waits");
        invalid = Request(start, "throw");
        Check(DurakStrategy.Decide(start, invalid).Kind == "review", "Cannot throw onto empty table");
        invalid = Request(hazard, "defend"); invalid.Pairs[0].Attack = "7H";
        Check(DurakStrategy.Decide(hazard, invalid).Kind == "review", "Wrong screenshot table blocked");
        for (int i = 0; i < 100; i++)
        {
            var sampled = DurakStrategy.SampleHands(start.State, random);
            Check(sampled.Values.SelectMany(x => x).Distinct().Count() == 18, "Sample contains no duplicate cards");
            Check(!sampled.Values.SelectMany(x => x).Contains("KS"), "Samples leave bottom trump in stock");
        }
        // Monte Carlo branch returns only legal actions, with a bounded sampling count.
        var difficult = new CardDocument { State = CardModel.NewGame(36, ["Я", "Слева", "Справа"], CardCodes.ParseMany("9H 10D QC KC AH AS"), "KS") };
        difficult.State.Stock -= 2; difficult.State.Discard.AddRange(["6H", "7H"]); // Later round, not the first-deal fast path.
        var times = new List<double>();
        for (int i = 0; i < 20; i++)
        {
            answer = DurakStrategy.Decide(difficult, Request(difficult, "attack"));
            Check(answer.Kind == "attack" && difficult.State.Players[0].Known.Contains(answer.Card!), "Legal sampled attack");
            Check(answer.Samples is > 0 and <= 128, "Bounded samples"); times.Add(answer.ElapsedMs);
        }
        var limit = Make("AH AS", [new() { Attack = "6H", Defense = "7H" }], defender: "p1", p1Count: 0);
        var limitRequest = Request(limit, "throw"); limitRequest.Pairs = [new() { Attack = "6H", Defense = "7H" }];
        limitRequest.DefenderStartCount = 1; limitRequest.FinishButtonVisible = true;
        Check(DurakStrategy.Decide(limit, limitRequest).Template == "ATTACK_LIMIT", "Starting defender capacity limits throw-ins");

        string root = Path.Combine(DurakPaths.Tests, "strategy-" + DateTime.Now.ToString("yyyyMMdd-HHmmss"));
        var store = new CardStore(root);
        var doc = store.New(36, ["Я", "Слева", "Справа"], CardCodes.ParseMany("6H 6D 8C JC QD AS"), "KS");
        var input = Request(doc, "attack"); answer = DurakStrategy.Decide(doc, input);
        store.Database.Decision(doc, input, answer);
        store.Database.Execution(answer.Id, "missed", 410, "Тест: карта не легла на стол");
        store.Database.Result(doc.GameId, "loss", 3);
        store.Database.Result(doc.GameId, "loss", 3);
        Check(store.Database.Query("SELECT * FROM results").Rows.Count == 1, "Results idempotent");
        Check(store.Database.Query("SELECT * FROM executions").Rows.Count == 1, "Failed motion recorded separately");
        Check(JsonSerializer.Serialize(store.Database.Review(doc.GameId)).Contains("missed"), "Game review includes execution evidence");
        store.Database.Execution(answer.Id, "confirmed", 500, "Тест: повторный бросок сработал");
        Check(store.Database.Query("SELECT * FROM execution_attempts").Rows.Count == 2, "Retry preserves failed attempt");
        Check(store.Database.Query("SELECT * FROM decision_states").Rows.Count == 1, "Position snapshot recorded with decision");
        Check(store.Database.HistoryText(doc.GameId).Contains("не сработало") && store.Database.HistoryText(doc.GameId).Contains("подтверждено"), "Both motion outcomes visible in history");
        Check(JsonSerializer.Serialize(store.Database.Review(doc.GameId)).Contains(DurakStrategy.Version), "History identifies strategy version");
        Check(store.Database.Query("PRAGMA quick_check").Rows[0][0].ToString() == "ok", "SQLite integrity");
        Check(store.Database.Outcome(doc.GameId) == "loss", "Completed game can be distinguished from live game");
        store.Database.SetThreshold(37); Check(new CardDatabase(root).Threshold() == 37, "Slider persisted in SQLite");
        // Legacy migration is non-destructive and is not replayed over newer SQL state.
        var legacyRoot = Path.Combine(root, "legacy"); Directory.CreateDirectory(legacyRoot);
        File.WriteAllText(Path.Combine(legacyRoot, "current.json"), JsonSerializer.Serialize(doc, CardStore.Json));
        var migrated = new CardStore(legacyRoot);
        Check(migrated.Read()!.GameId == doc.GameId && File.Exists(migrated.StatePath), "Legacy JSON retained");
        var next = migrated.New(36, ["Я", "Слева", "Справа"], CardCodes.ParseMany("6H 6D 8C JC QD AS"), "KS");
        Check(migrated.Read()!.GameId == next.GameId, "Old JSON cannot replace current SQLite game");
        var combined = CardTrackerCli.StartHand(new CardStore(Path.Combine(root, "combined")), new StartHandRequest
            { Names = ["Я", "Левый", "Правый"], Cards = ["KH", "JH", "JS", "9H", "8D", "6C"], Trump = "KC", Defender = "p1", OurTurn = true, Observation = "test-initial" });
        using var startJson = JsonDocument.Parse(JsonSerializer.Serialize(combined));
        Check(startJson.RootElement.GetProperty("Decision").GetProperty("Card").GetString() == "8D", "One request starts history and chooses first move");
        Console.WriteLine($"PASS: {checks} strategy/storage assertions. Decision ms: median={times.Order().ElementAt(times.Count / 2):0.00}, max={times.Max():0.00}");
        Console.WriteLine("Evidence: " + root);
        return 0;
    }
    private static CardDocument Make(string own, List<TablePair> pairs, string defender = "p0", int p1Count = 3)
    {
        var hand = CardCodes.ParseMany(own);
        var table = pairs.SelectMany(p => p.Defense is null ? new[] { p.Attack } : new[] { p.Attack, p.Defense }).ToList();
        var s = new CardState { DeckSize = 36, TrumpSuit = "S", Table = table,
            Players = [new() { Id = "p0", Name = "Я", Count = hand.Count, Known = hand },
                new() { Id = "p1", Name = "Слева", Count = p1Count }, new() { Id = "p2", Name = "Справа", Count = 3 }] };
        s.Discard = CardCodes.Deck(36).Except(hand).Except(table).Take(36 - hand.Count - table.Count - p1Count - 3).ToList();
        CardModel.Validate(s); return new CardDocument { State = s };
    }
    private static DecisionRequest Request(CardDocument doc, string phase) => new()
    {
        GameId = doc.GameId, Revision = doc.Revision, OurTurn = true, Phase = phase, Trump = "S",
        Attacker = phase == "defend" ? "p1" : "p0", Defender = phase == "defend" ? "p0" : "p1",
        DefenderStartCount = doc.State.Players.Single(p => p.Id == (phase == "defend" ? "p0" : "p1")).Count,
        Pairs = doc.State.Table.Select(c => new TablePair { Attack = c }).ToList(),
    };
}
