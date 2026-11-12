using System.Text.Json;

internal static class CardTrackerCli
{
    internal static int Run(string[] args)
    {
        try
        {
            if (args is ["--cards-self-test"]) return CardTrackerTests.Run();
            if (args is ["--cards-strategy-test"]) return DurakStrategyTests.Run();
            if (args is ["--cards-server"]) return Serve();
            if (args[0] == "--cards-statistics")
            {
                if (args.Length != 1 && !(args.Length == 3 && args[1] == "--demo-store"))
                    throw new ArgumentException("Usage: --cards-statistics [--demo-store PATH]");
                var thread = new Thread(() =>
                {
                    Application.SetHighDpiMode(HighDpiMode.PerMonitorV2); Application.EnableVisualStyles();
                    var window = new CardStatisticsWindow(new CardStore(args.Length == 3 ? args[2] : null));
                    if (args.Length == 3) window.Text += " — ТЕСТ";
                    Application.Run(window);
                });
                thread.SetApartmentState(ApartmentState.STA); thread.Start(); thread.Join(); return 0;
            }
            if (args[0] == "--cards-window")
            {
                if (args.Length != 1 && !(args.Length == 3 && args[1] == "--demo-store"))
                    throw new ArgumentException("Usage: --cards-window [--demo-store PATH]");
                var thread = new Thread(() =>
                {
                    Application.SetHighDpiMode(HighDpiMode.PerMonitorV2);
                    Application.EnableVisualStyles();
                    Application.Run(new CardTrackerWindow(new CardStore(args.Length == 3 ? args[2] : null), args.Length == 3));
                });
                thread.SetApartmentState(ApartmentState.STA);
                thread.Start();
                thread.Join();
                return 0;
            }
            if (args[0] != "--cards") throw new ArgumentException("Unknown cards option.");
            var store = new CardStore();
            if (args is [_, "clear-current", var clearGame])
            {
                store.ClearCurrent(clearGame);
                Console.WriteLine("Окно очищено; история сохранена."); return 0;
            }
            if (args is [_, "decide", var requestPath])
            {
                var request = JsonSerializer.Deserialize<DecisionRequest>(File.ReadAllText(requestPath), CardStore.Json)
                    ?? throw new ArgumentException("Нет позиции для решения.");
                var doc = store.Read() ?? throw new InvalidOperationException("Учёт партии не начат.");
                if (store.Database.Outcome(doc.GameId) is not null) throw new InvalidOperationException("Партия уже завершена.");
                var answer = DurakStrategy.Decide(doc, request);
                store.Database.Decision(doc, request, answer);
                Console.WriteLine(JsonSerializer.Serialize(answer, CardStore.Json)); return 0;
            }
            if (args is [_, "result", var resultGame, var outcome, var place])
            {
                store.Database.Result(resultGame, outcome, int.Parse(place));
                Console.WriteLine(store.Database.Summary()); return 0;
            }
            if (args is [_, "execution", var decisionId, var status, var elapsed, var note])
            {
                store.Database.Execution(decisionId, status, int.Parse(elapsed), note); Console.WriteLine("Saved"); return 0;
            }
            if (args is [_, "review", var reviewGame])
            { Console.WriteLine(JsonSerializer.Serialize(store.Database.Review(reviewGame), CardStore.Json)); return 0; }
            CardDocument? result;
            if (args is [_, "status"])
                result = store.Read();
            else if (args is [_, "new", var deck, var names, var own, var trump])
            {
                if (deck != "36" || names.Split(';').Length != 3) throw new ArgumentException("Профиль владельца: 36 карт, три игрока.");
                result = store.New(int.Parse(deck), names.Split(';', StringSplitOptions.TrimEntries),
                    CardCodes.ParseMany(own), trump == "-" ? null : CardCodes.Parse(trump));
            }
            else if (args is [_, "apply", var path])
                result = store.Apply(JsonSerializer.Deserialize<CardBatch>(File.ReadAllText(path), CardStore.Json)
                    ?? throw new ArgumentException("Empty batch."));
            else if (args is [_, "event", var game, var revision, var id, var kind, var player, var cards, var count])
                result = store.Apply(new CardBatch
                {
                    GameId = game, ExpectedRevision = long.Parse(revision), Events = [new CardEvent
                    {
                        Id = id, Kind = kind, Player = player == "-" ? "" : player,
                        Cards = cards == "-" ? [] : CardCodes.ParseMany(cards), Count = int.Parse(count),
                    }],
                });
            else if (args is [_, "undo", var undoGame, var undoRevision])
                result = store.Undo(undoGame, long.Parse(undoRevision));
            else throw new ArgumentException("Commands:\n--cards status\n--cards new 36 'Я;Слева;Справа' '6S 7S 8S 9S 10S JS' AS\n"
                + "--cards event GAME REV ID play|take|discard|draw|hand|gap PLAYER 'CARDS|-' COUNT\n"
                + "--cards apply FILE.json\n--cards undo GAME REV\n--cards clear-current GAME\n--cards decide POSITION.json\n--cards result GAME win|loss|draw PLACE\n"
                + "--cards execution DECISION_ID confirmed|missed|cancelled MS NOTE\n--cards review GAME\n--cards-window\n--cards-statistics\n--cards-self-test\n--cards-strategy-test");
            Console.WriteLine(JsonSerializer.Serialize(result is null ? new { Ready = false, Message = "Учёт раздачи ещё не начат." } : CardStore.Report(result), CardStore.Json));
            return 0;
        }
        catch (Exception error)
        {
            Console.Error.WriteLine(error.Message);
            return 2;
        }
    }
    private static int Serve()
    {
        var store = new CardStore();
        var compact = new JsonSerializerOptions(CardStore.Json) { WriteIndented = false };
        Console.WriteLine("{\"Ready\":true,\"Protocol\":\"durak-cards-v1\"}");
        while (Console.ReadLine() is string line)
        {
            if (line == "quit") break;
            try
            {
                using var json = JsonDocument.Parse(line); var root = json.RootElement;
                object answer;
                switch (root.GetProperty("op").GetString())
                {
                    case "status":
                        var current = store.Read();
                        answer = current is null ? new { Ready = false } : CardStore.Report(current); break;
                    case "apply":
                        answer = CardStore.Report(store.Apply(root.GetProperty("batch").Deserialize<CardBatch>(CardStore.Json)!)); break;
                    case "start":
                        answer = StartHand(store, root.GetProperty("hand").Deserialize<StartHandRequest>(CardStore.Json)!); break;
                    case "decide":
                        var doc = store.Read() ?? throw new InvalidOperationException("Учёт не начат.");
                        if (store.Database.Outcome(doc.GameId) is not null) throw new InvalidOperationException("Партия уже завершена.");
                        var request = root.GetProperty("request").Deserialize<DecisionRequest>(CardStore.Json)!;
                        var decision = DurakStrategy.Decide(doc, request);
                        store.Database.Decision(doc, request, decision); answer = decision; break;
                    case "execution":
                        store.Database.Execution(root.GetProperty("decisionId").GetString()!, root.GetProperty("status").GetString()!, root.GetProperty("ms").GetInt32(), root.GetProperty("note").GetString()!);
                        answer = new { Saved = true }; break;
                    case "result":
                        store.Database.Result(root.GetProperty("gameId").GetString()!, root.GetProperty("outcome").GetString()!, root.GetProperty("place").GetInt32());
                        answer = new { Saved = true }; break;
                    default: throw new ArgumentException("op: status, start, apply, decide, execution, result; quit to exit.");
                }
                Console.WriteLine(JsonSerializer.Serialize(answer, compact));
            }
            catch (Exception error) { Console.WriteLine(JsonSerializer.Serialize(new { Error = error.Message }, compact)); }
        }
        return 0;
    }
    internal static object StartHand(CardStore store, StartHandRequest hand)
    {
        if (hand.Names.Length != 3 || hand.Defender is not ("p1" or "p2") || !hand.OurTurn || string.IsNullOrWhiteSpace(hand.Observation))
            throw new ArgumentException("Нужны три имени, подтверждённый наш первый ход, защитник и свежее наблюдение.");
        var cards = hand.Cards.Select(CardCodes.Parse).ToList(); var trump = CardCodes.Parse(hand.Trump);
        _ = CardModel.NewGame(36, hand.Names, cards, trump); // Reject invalid input before writing a game.
        var doc = store.New(36, hand.Names, cards, trump);
        var request = new DecisionRequest { GameId = doc.GameId, Revision = doc.Revision, Phase = "attack", OurTurn = true,
            Attacker = "p0", Defender = hand.Defender, Trump = trump[^1..], DefenderStartCount = 6, Observation = hand.Observation };
        var decision = DurakStrategy.Decide(doc, request); store.Database.Decision(doc, request, decision);
        return new { State = CardStore.Report(doc), Decision = decision };
    }
}

internal sealed class StartHandRequest
{
    public string[] Names { get; set; } = [];
    public string[] Cards { get; set; } = [];
    public string Trump { get; set; } = "";
    public string Defender { get; set; } = "";
    public bool OurTurn { get; set; }
    public string Observation { get; set; } = "";
}
