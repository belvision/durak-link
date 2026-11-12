internal static class CardTrackerTests
{
    private static int assertions;
    internal static int Run()
    {
        assertions = 0;
        var state = Initial();
        Near(CardModel.Chances(state, "p1").Single(c => c.Card == "6H").Probability, 6.0 / 29, "Initial marginal");
        Check(!CardModel.Chances(state, "p1").Any(c => c.Card == "AS"), "Bottom trump excluded");
        Check(CardModel.Chances(state, "p0").Count == 6, "Own hand exact");
        Throws(() => CardModel.NewGame(24, ["Я", "Игрок"], CardCodes.ParseMany("6S 7S 8S 9S 10S JS"), "AS"), "Wrong deck");
        Throws(() => CardModel.NewGame(36, ["Я", "Игрок"], CardCodes.ParseMany("6S 7S 8S 9S 10S JS"), "6S"), "Duplicate trump");
        Throws(() => CardModel.Apply(CardStore.Copy(state), Event("play", "p1", "6S")), "Other player's known card");
        Throws(() => CardModel.Apply(CardStore.Copy(state), Event("play", "p0", "QH")), "Unknown self card");
        CardModel.Apply(state, Event("play", "p0", "6S"));
        CardModel.Apply(state, Event("play", "p1", "6H"));
        Check(state.Table.Count == 2 && state.Players[1].Count == 5, "Two table cards");
        Throws(() => CardModel.Apply(CardStore.Copy(state), Event("draw", "p1", "", 1)), "Cannot draw during round");
        CardModel.Apply(state, Event("take", "p2"));
        Check(state.Players[2].Known.Contains("6H") && state.Players[2].Count == 8, "Taken cards known");
        Near(CardModel.Chances(state, "p2").Single(c => c.Card == "6H").Probability, 1, "Certain taken card");
        CardModel.Apply(state, Event("draw", "p0", "QH", 1));
        CardModel.Apply(state, Event("draw", "p1", "", 1));
        Check(state.Stock == 16 && state.Players[0].Known.Contains("QH"), "Refill and own identities");
        CardModel.Apply(state, Event("play", "p2", "6H"));
        CardModel.Apply(state, Event("discard"));
        Check(!CardModel.Chances(state, "p2").Any(c => c.Card == "6H"), "Played taken card no longer held");
        Throws(() => CardModel.Apply(CardStore.Copy(state), Event("play", "p1", "6H")), "Discard cannot return");
        Throws(() => CardModel.Apply(CardStore.Copy(state), Event("draw", "p0", "", 1)), "Self draw requires identities");
        Throws(() => CardModel.Apply(CardStore.Copy(state), Event("draw", "p1", "AS", 1)), "Bottom not drawn early");
        CardModel.Apply(state, Event("draw", "p1", "", state.Stock));
        Check(state.Stock == 0 && state.BottomTrump is null && state.Players[1].Known.Contains("AS"), "Last draw assigns trump");

        // Exhaustive small endgame: 9 out of 10 possible locations belong to p1; exactly 90%, not 100%.
        var end = new CardState { DeckSize = 36, Stock = 1,
            Players = [new CardPlayer { Id = "p0", Name = "Я" }, new CardPlayer { Id = "p1", Name = "Друг", Count = 9 }],
            Discard = CardCodes.Deck(36).Take(26).ToList() };
        var probabilities = CardModel.Chances(end, "p1");
        Check(probabilities.Count == 10 && probabilities.All(c => c.Probability == .9), "90% threshold from 10 equiprobable allocations");
        Near(probabilities.Sum(c => c.Probability), 9, "Expected hand size");
        end.Stock = 0; end.Players[1].Count = 10;
        Check(CardModel.Chances(end, "p1").All(c => c.Probability == 1 && c.Evidence == "deduced"), "Exact endgame deduction");
        CardModel.Apply(end, Event("gap"));
        Check(CardModel.Chances(end, "p1").Count == 0, "Gap hides predictions");
        Throws(() => CardModel.Apply(end, Event("play", "p1", "AC")), "Gap blocks following observations");

        // Validate marginal conservation over many legal observed transitions.
        var random = new Random(1142);
        for (int round = 0; round < 30; round++)
        {
            var sample = Initial();
            for (int turn = 0; turn < 10; turn++)
            {
                int who = random.Next(3);
                var p = sample.Players[who];
                if (p.Count == 0) continue;
                string card = p.Known.FirstOrDefault() ?? CardModel.Unknown(sample)[random.Next(CardModel.Unknown(sample).Length)];
                CardModel.Apply(sample, Event("play", p.Id, card));
                if (random.Next(2) == 0) CardModel.Apply(sample, Event("take", sample.Players[random.Next(3)].Id));
                else CardModel.Apply(sample, Event("discard"));
                foreach (var q in sample.Players)
                    Near(CardModel.Chances(sample, q.Id).Sum(c => c.Probability), q.Count, "Marginals equal count");
            }
        }

        string root = Path.Combine(DurakPaths.Tests, DateTime.Now.ToString("yyyyMMdd-HHmmss") + "-" + Guid.NewGuid().ToString("N")[..6]);
        var store = new CardStore(root);
        var doc = store.New(36, ["Я", "Слева", "Справа"], CardCodes.ParseMany("6S 7S 8S 9S 10S JS"), "AS");
        var batch = new CardBatch { GameId = doc.GameId, ExpectedRevision = 0, Events = [Event("play", "p0", "6S")] };
        doc = store.Apply(batch);
        Check(doc.Revision == 1 && new CardStore(root).Read()!.State.Table.Count == 1, "Saved and reloaded");
        Check(store.Apply(batch).Revision == 1, "Retry is idempotent");
        var duplicate = CardStore.Copy(batch); duplicate.Events[0].Cards = ["7S"];
        Throws(() => store.Apply(duplicate), "Reused id with different payload");
        var stale = new CardBatch { GameId = doc.GameId, ExpectedRevision = 0, Events = [Event("play", "p0", "7S")] };
        Throws(() => store.Apply(stale), "Stale observation");
        var broken = new CardBatch { GameId = doc.GameId, ExpectedRevision = 1,
            Events = [Event("play", "p0", "7S"), Event("play", "p1", "8S")] };
        Throws(() => store.Apply(broken), "Atomic batch failure");
        Check(store.Read()!.State.Table.SequenceEqual(["6S"]) && store.Read()!.Revision == 1, "No partial write");
        doc = store.Undo(doc.GameId, doc.Revision);
        Check(doc.Revision == 2 && doc.State.Table.Count == 0 && doc.State.Players[0].Count == 6, "Undo keeps increasing revision");
        var oldGame = doc.GameId;
        doc = store.New(36, ["Я", "Слева", "Справа"], CardCodes.ParseMany("6S 7S 8S 9S 10S JS"), "AS");
        Check(store.Database.Query("SELECT game_id FROM games WHERE current=0").Rows.Count == 1, "Previous game archived in SQLite");
        Throws(() => store.Apply(batch), "Old game rejected");
        var parallelBatches = new[] { "6S", "7S" }.Select(c => new CardBatch { GameId = doc.GameId, ExpectedRevision = 0,
            Events = [Event("play", "p0", c)] }).ToArray();
        int accepted = 0;
        Parallel.ForEach(parallelBatches, b => { try { store.Apply(b); Interlocked.Increment(ref accepted); } catch (InvalidOperationException) { } });
        Check(accepted == 1 && store.Read()!.Revision == 1, "Concurrent writers serialize");
        var beforeClear = store.Read()!;
        File.WriteAllText(store.StatePath, System.Text.Json.JsonSerializer.Serialize(beforeClear, CardStore.Json));
        Throws(() => store.ClearCurrent(oldGame), "Cannot clear a different game");
        store.ClearCurrent(beforeClear.GameId);
        Check(new CardStore(root).Read() is null, "Clear does not reimport retained legacy JSON");
        Check(store.Database.Query("SELECT game_id FROM games WHERE current=0").Rows.Count == 2, "Clear retains game history");
        var afterClear = store.New(36, ["Я", "Слева", "Справа"], CardCodes.ParseMany("6S 7S 8S 9S 10S JS"), "AS");
        Check(store.Read()!.GameId == afterClear.GameId, "New game becomes current after clear");

        // Isolated visual fixture, never mistaken for a live game.
        var preview = new CardStore(Path.Combine(root, "preview"));
        var visible = preview.New(36, ["Я", "Игрок слева", "Игрок справа"], CardCodes.ParseMany("6S 7S 8S 9S 10S JS"), "AS");
        preview.Apply(new CardBatch { GameId = visible.GameId, ExpectedRevision = 0,
            Events = [Event("play", "p0", "6S"), Event("play", "p1", "6H"), Event("take", "p2"), Event("draw", "p0", "QH", 1)] });
        Console.WriteLine($"PASS: {assertions} assertions. Test data: {root}");
        Console.WriteLine($"Preview: {preview.Root}");
        return 0;
    }
    private static CardState Initial() => CardModel.NewGame(36, ["Я", "Слева", "Справа"], CardCodes.ParseMany("6S 7S 8S 9S 10S JS"), "AS");
    private static CardEvent Event(string kind, string player = "", string cards = "", int count = 0) =>
        new() { Kind = kind, Player = player, Cards = CardCodes.ParseMany(cards), Count = count };
    private static void Check(bool passed, string message) { assertions++; if (!passed) throw new Exception("FAIL: " + message); }
    private static void Near(double a, double b, string message) => Check(Math.Abs(a - b) < 1e-10, message);
    private static void Throws(Action action, string message)
    {
        bool threw = false;
        try { action(); } catch (ArgumentException) { threw = true; } catch (InvalidOperationException) { threw = true; }
        Check(threw, message);
    }
}
