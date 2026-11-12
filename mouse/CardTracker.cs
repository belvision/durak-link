using System.Text.Json;

// This model consumes confirmed observations. A mouse command is not evidence of a played card.
internal static class CardCodes
{
    internal static string[] Deck(int size)
    {
        string[] ranks = size switch
        {
            24 => ["9", "10", "J", "Q", "K", "A"],
            36 => ["6", "7", "8", "9", "10", "J", "Q", "K", "A"],
            52 => ["2", "3", "4", "5", "6", "7", "8", "9", "10", "J", "Q", "K", "A"],
            _ => throw new ArgumentException("Колода должна содержать 24, 36 или 52 карты."),
        };
        return "SHDC".SelectMany(suit => ranks.Select(rank => rank + suit)).ToArray();
    }

    internal static string Parse(string value)
    {
        string code = value.Trim().ToUpperInvariant().Replace("♠", "S").Replace("♥", "H")
            .Replace("♦", "D").Replace("♣", "C");
        if (code.Length == 2 && code[0] == 'T') code = "10" + code[1];
        if (!Deck(52).Contains(code)) throw new ArgumentException($"Неизвестная карта: {value}. Пример: 7S, QH, 10D, AC.");
        return code;
    }

    internal static List<string> ParseMany(string value)
    {
        var cards = value.Split([' ', ',', ';', '\r', '\n', '\t'], StringSplitOptions.RemoveEmptyEntries).Select(Parse).ToList();
        if (cards.Distinct().Count() != cards.Count) throw new ArgumentException("Одна карта указана дважды.");
        return cards;
    }

    internal static string Display(string code) => code[..^1] + (code[^1] switch { 'S' => "♠", 'H' => "♥", 'D' => "♦", _ => "♣" });
}

internal sealed class CardPlayer
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public int Count { get; set; }
    public List<string> Known { get; set; } = [];
}

internal sealed class CardState
{
    public int DeckSize { get; set; }
    public int Stock { get; set; }
    public string? BottomTrump { get; set; }
    public string? TrumpSuit { get; set; }
    public List<CardPlayer> Players { get; set; } = [];
    public List<string> Table { get; set; } = [];
    public List<string> Discard { get; set; } = [];
    public bool Reliable { get; set; } = true;
    public string Warning { get; set; } = "";
}

internal sealed class CardEvent
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Kind { get; set; } = "";
    public string Player { get; set; } = "";
    public List<string> Cards { get; set; } = [];
    public int Count { get; set; }
    public string Note { get; set; } = "";
}

internal sealed class CardBatch
{
    public string GameId { get; set; } = "";
    public long ExpectedRevision { get; set; }
    public List<CardEvent> Events { get; set; } = [];
}

internal sealed class CardCheckpoint
{
    public DateTimeOffset At { get; set; }
    public string Description { get; set; } = "";
    public CardState Before { get; set; } = new();
}

internal sealed class CardDocument
{
    public int Version { get; set; } = 1;
    public string GameId { get; set; } = Guid.NewGuid().ToString("N");
    public long Revision { get; set; }
    public DateTimeOffset Updated { get; set; } = DateTimeOffset.UtcNow;
    public CardState State { get; set; } = new();
    public List<CardCheckpoint> History { get; set; } = [];
    public Dictionary<string, string> Processed { get; set; } = [];
}

internal sealed record CardChance(string Card, double Probability, string Evidence);

internal static class CardModel
{
    internal static CardState NewGame(int deckSize, string[] names, List<string> own, string? trump)
    {
        if (names.Length is < 2 or > 6 || names.Any(string.IsNullOrWhiteSpace) || names.Any(n => n.Length > 40))
            throw new ArgumentException("Нужно от 2 до 6 имён игроков, не длиннее 40 символов.");
        if (own.Count != 6) throw new ArgumentException("Для начала учёта укажите все 6 карт своей начальной руки.");
        var state = new CardState { DeckSize = deckSize, Stock = deckSize - names.Length * 6, BottomTrump = trump, TrumpSuit = trump?[^1..] };
        for (int i = 0; i < names.Length; i++)
            state.Players.Add(new CardPlayer { Id = $"p{i}", Name = names[i].Trim(), Count = 6, Known = i == 0 ? [.. own] : [] });
        Validate(state);
        return state;
    }

    internal static void Validate(CardState state)
    {
        var deck = CardCodes.Deck(state.DeckSize);
        if (state.Players.Count is < 2 or > 6 || state.Players.Select(p => p.Id).Distinct().Count() != state.Players.Count
            || state.Players[0].Id != "p0" || state.Stock < 0)
            throw new InvalidOperationException("Неверный состав игроков или размер колоды.");
        if (state.Players.Any(p => p.Count < 0 || p.Known.Count > p.Count))
            throw new InvalidOperationException("Известных карт больше, чем карт у игрока.");
        if (state.Players[0].Known.Count != state.Players[0].Count)
            throw new InvalidOperationException("Своя рука должна быть известна полностью. Укажите карты добора.");
        var known = KnownLocations(state).ToArray();
        if (known.Any(c => !deck.Contains(c)) || known.Distinct().Count() != known.Length)
            throw new InvalidOperationException("Карта повторяется в разных местах или отсутствует в выбранной колоде.");
        if (state.BottomTrump is not null && state.Stock < 1)
            throw new InvalidOperationException("Открытая козырная карта не может оставаться в пустой колоде.");
        if (state.Stock + state.Table.Count + state.Discard.Count + state.Players.Sum(p => p.Count) != state.DeckSize)
            throw new InvalidOperationException("Число карт не сходится с размером колоды. Учёт остановлен до исправления наблюдения.");
    }

    internal static IEnumerable<string> KnownLocations(CardState state) => state.Players.SelectMany(p => p.Known)
        .Concat(state.Table).Concat(state.Discard).Concat(state.BottomTrump is null ? [] : new[] { state.BottomTrump });

    internal static string[] Unknown(CardState state) => CardCodes.Deck(state.DeckSize).Except(KnownLocations(state)).ToArray();

    internal static List<CardChance> Chances(CardState state, string playerId)
    {
        Validate(state);
        if (!state.Reliable) return [];
        var player = state.Players.Single(p => p.Id == playerId);
        var result = player.Known.Select(c => new CardChance(c, 1, "observed")).ToList();
        var unknown = Unknown(state);
        int slots = player.Count - player.Known.Count;
        if (slots > 0)
            result.AddRange(unknown.Select(c => new CardChance(c, (double)slots / unknown.Length,
                slots == unknown.Length ? "deduced" : "uniform-model")));
        return result.OrderByDescending(c => c.Probability).ThenBy(c => Array.IndexOf(CardCodes.Deck(state.DeckSize), c.Card)).ToList();
    }

    internal static string Apply(CardState state, CardEvent e)
    {
        Validate(state);
        if (e.Kind == "gap")
        {
            state.Reliable = false;
            state.Warning = string.IsNullOrWhiteSpace(e.Note) ? "Пропущен ход. Вероятности скрыты. Отмените ошибку или начните учёт со следующей раздачи." : e.Note;
            return "Пропуск наблюдения: " + state.Warning;
        }
        if (!state.Reliable) throw new InvalidOperationException("Учёт потерял синхронизацию. Исправьте пропуск или начните новую раздачу.");
        if (e.Cards.Any(c => CardCodes.Parse(c) != c) || e.Cards.Distinct().Count() != e.Cards.Count)
            throw new ArgumentException("Нужны уникальные карты в формате 7S, QH, 10D, AC.");
        var player = state.Players.SingleOrDefault(p => p.Id == e.Player);
        if (e.Kind is "play" or "take" or "draw" or "hand" && player is null)
            throw new ArgumentException("Игрок не найден.");
        string label = string.Join(' ', e.Cards.Select(CardCodes.Display));
        string message;
        switch (e.Kind)
        {
            case "play":
                if (e.Cards.Count == 0) throw new ArgumentException("Укажите сыгранные карты.");
                foreach (string card in e.Cards)
                {
                    if (player!.Count == 0) throw new InvalidOperationException("У игрока больше нет карт.");
                    if (!player.Known.Remove(card))
                    {
                        if (!Unknown(state).Contains(card) || player.Count == player.Known.Count)
                            throw new InvalidOperationException($"{CardCodes.Display(card)} не может быть сыграна этим игроком. Проверьте наблюдение.");
                    }
                    player.Count--;
                    state.Table.Add(card);
                }
                message = $"{player!.Name} сыграл: {label}";
                break;
            case "take":
                if (state.Table.Count == 0) throw new InvalidOperationException("На столе нет учтённых карт.");
                message = $"{player!.Name} взял: {string.Join(' ', state.Table.Select(CardCodes.Display))}";
                player.Count += state.Table.Count;
                player.Known.AddRange(state.Table);
                state.Table.Clear();
                break;
            case "discard":
                if (state.Table.Count == 0) throw new InvalidOperationException("На столе нет учтённых карт.");
                message = $"В отбой: {string.Join(' ', state.Table.Select(CardCodes.Display))}";
                state.Discard.AddRange(state.Table);
                state.Table.Clear();
                break;
            case "draw":
                if (state.Table.Count != 0) throw new InvalidOperationException("Сначала завершите взятие или отбой.");
                if (e.Count <= 0 || e.Count > state.Stock || e.Cards.Count > e.Count)
                    throw new ArgumentException("Неверное число карт добора.");
                if (player!.Id == "p0" && e.Cards.Count != e.Count)
                    throw new ArgumentException("Для своего добора нужны все карты, а не только количество.");
                bool takesBottom = e.Count == state.Stock && state.BottomTrump is not null;
                foreach (string card in e.Cards)
                    if (!Unknown(state).Contains(card) && !(takesBottom && card == state.BottomTrump))
                        throw new InvalidOperationException($"{CardCodes.Display(card)} не может быть в доборе.");
                if (takesBottom && e.Cards.Count == e.Count && !e.Cards.Contains(state.BottomTrump!))
                    throw new InvalidOperationException("Последний добор должен включать открытую козырную карту.");
                player.Known.AddRange(e.Cards);
                if (takesBottom)
                {
                    if (!player.Known.Contains(state.BottomTrump!)) player.Known.Add(state.BottomTrump!);
                    state.BottomTrump = null;
                }
                player.Count += e.Count;
                state.Stock -= e.Count;
                message = $"{player.Name}: добор {e.Count}" + (label.Length > 0 ? $" ({label})" : "");
                break;
            case "hand":
                if (e.Cards.Count != player!.Count || player.Known.Except(e.Cards).Any())
                    throw new InvalidOperationException("Полная рука не совпадает с учтённым количеством или известными картами.");
                player.Known = [.. e.Cards];
                message = $"{player.Name}: подтверждена рука {label}";
                break;
            default: throw new ArgumentException("Неизвестное событие. Допустимо: play, take, discard, draw, hand, gap.");
        }
        Validate(state);
        return message;
    }
}

internal sealed class CardStore
{
    internal static readonly string DefaultRoot = DurakPaths.Cards;
    internal static readonly JsonSerializerOptions Json = new() { WriteIndented = true, PropertyNameCaseInsensitive = true,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping };
    internal string Root { get; }
    internal string StatePath => Path.Combine(Root, "current.json");
    internal CardDatabase Database => new(Root);
    private string MutexName => @"Local\DurakCards-" + Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(
        System.Text.Encoding.UTF8.GetBytes(Root.ToUpperInvariant())))[..24];
    internal CardStore(string? root = null) => Root = Path.GetFullPath(root ?? DefaultRoot);
    internal static T Copy<T>(T data) => JsonSerializer.Deserialize<T>(JsonSerializer.Serialize(data, Json), Json)!;

    internal CardDocument? Read()
    {
        var doc = Database.ReadCurrent();
        if (doc is null && !Database.DisplayCleared() && File.Exists(StatePath))
            doc = Locked(() =>
            {
                var existing = Database.ReadCurrent();
                if (existing is not null) return existing;
                if (Database.DisplayCleared()) return null;
                using var file = new FileStream(StatePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                var legacy = JsonSerializer.Deserialize<CardDocument>(file, Json) ?? throw new InvalidDataException("Не читается прежний журнал карт.");
                CardModel.Validate(legacy.State);
                Database.Save(legacy); // Retain the original JSON as the migration backup.
                return legacy;
            });
        if (doc is null) return null;
        if (doc.Version != 1) throw new InvalidDataException("Неподдерживаемая версия журнала карт.");
        CardModel.Validate(doc.State);
        return doc;
    }

    internal void ClearCurrent(string gameId) => Locked(() =>
    {
        Database.ClearCurrent(gameId);
        return true;
    });

    internal CardDocument New(int deck, string[] names, List<string> own, string? trump) => Locked(() =>
    {
        var doc = new CardDocument { State = CardModel.NewGame(deck, names, own, trump) };
        _ = Read(); // Import any legacy current.json before archiving it in SQLite.
        Write(doc);
        return doc;
    });

    internal CardDocument Apply(CardBatch batch) => Locked(() =>
    {
        var doc = Read() ?? throw new InvalidOperationException("Сначала начните учёт раздачи.");
        if (doc.GameId != batch.GameId) throw new InvalidOperationException("Другая раздача. Сначала обновите состояние.");
        if (batch.Events.Count is < 1 or > 100 || batch.Events.Any(e => string.IsNullOrWhiteSpace(e.Id) || e.Id.Length > 100)
            || batch.Events.Select(e => e.Id).Distinct().Count() != batch.Events.Count)
            throw new ArgumentException("Нужно 1–100 событий с уникальными Id.");
        foreach (var e in batch.Events)
            if (doc.Processed.TryGetValue(e.Id, out string? previous) && previous != JsonSerializer.Serialize(e, Json))
                throw new InvalidOperationException("Повторный Id содержит другое событие.");
        // A retry after a lost response must not count the cards twice.
        if (batch.Events.All(e => doc.Processed.ContainsKey(e.Id))) return doc;
        CheckRevision(doc, batch.GameId, batch.ExpectedRevision);
        foreach (var e in batch.Events.Where(e => !doc.Processed.ContainsKey(e.Id)))
        {
            var before = Copy(doc.State);
            string description = CardModel.Apply(doc.State, e);
            doc.History.Add(new CardCheckpoint { At = DateTimeOffset.UtcNow, Description = description, Before = before });
            doc.Processed.Add(e.Id, JsonSerializer.Serialize(e, Json));
            doc.Revision++;
        }
        Write(doc);
        return doc;
    });

    internal CardDocument Undo(string game, long revision) => Locked(() =>
    {
        var doc = Read() ?? throw new InvalidOperationException("Нет раздачи.");
        CheckRevision(doc, game, revision);
        if (doc.History.Count == 0) throw new InvalidOperationException("Нет действий для отмены.");
        doc.State = doc.History[^1].Before;
        doc.History.RemoveAt(doc.History.Count - 1);
        doc.Revision++;
        Write(doc);
        return doc;
    });

    private static void CheckRevision(CardDocument doc, string game, long revision)
    {
        if (game != doc.GameId || revision != doc.Revision)
            throw new InvalidOperationException("Состояние уже изменилось. Обновите его перед записью наблюдения.");
    }

    private T Locked<T>(Func<T> action)
    {
        using var mutex = new Mutex(false, MutexName);
        bool acquired;
        try { acquired = mutex.WaitOne(3000); }
        catch (AbandonedMutexException) { acquired = true; }
        if (!acquired) throw new IOException("Журнал занят другим окном. Повторите позже.");
        try { return action(); }
        finally { mutex.ReleaseMutex(); }
    }

    private void Write(CardDocument doc)
    {
        CardModel.Validate(doc.State);
        doc.Updated = DateTimeOffset.UtcNow;
        Database.Save(doc);
    }

    internal static object Report(CardDocument doc) => new
    {
        doc.GameId, doc.Revision, doc.Updated, doc.State.Reliable, doc.State.Warning,
        doc.State.Stock, doc.State.BottomTrump, doc.State.Table, doc.State.Discard,
        Model = "Uniform unknown slots, not calibrated behaviour; candidates are not simultaneous possessions.",
        UnknownCards = doc.State.Reliable ? CardModel.Unknown(doc.State) : [],
        Players = doc.State.Players.Select(p => new { p.Id, p.Name, p.Count, p.Known,
            UnknownCount = p.Count - p.Known.Count,
            UnknownCardProbability = doc.State.Reliable && CardModel.Unknown(doc.State).Length > 0
                ? (double?)(p.Count - p.Known.Count) / CardModel.Unknown(doc.State).Length : null,
            CertainOr90Percent = CardModel.Chances(doc.State, p.Id).Where(c => c.Probability >= .9) }),
    };
}
