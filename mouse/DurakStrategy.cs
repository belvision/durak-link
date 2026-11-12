using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

internal sealed class TablePair
{
    public string Attack { get; set; } = "";
    public string? Defense { get; set; }
}
internal sealed class DecisionRequest
{
    public string GameId { get; set; } = "";
    public long Revision { get; set; }
    public string Phase { get; set; } = "wait"; // attack, throw, defend, wait
    public string Attacker { get; set; } = "";
    public string Defender { get; set; } = "";
    public string Trump { get; set; } = "";
    public int DefenderStartCount { get; set; }
    public List<TablePair> Pairs { get; set; } = [];
    public bool OurTurn { get; set; }
    public bool Taking { get; set; }
    public bool FinishButtonVisible { get; set; }
    public string Observation { get; set; } = "";
    public int BudgetMs { get; set; } = 150;
}
internal sealed record CardMove(string Card, string Target);
internal sealed record MoveOption(string Kind, string? Card, string? Target, double Score, double? SamplePickupRate);
internal sealed class DecisionAnswer
{
    public string StrategyVersion { get; set; } = DurakStrategy.Version;
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string GameId { get; set; } = "";
    public long Revision { get; set; }
    public string Kind { get; set; } = "review";
    public string Template { get; set; } = "";
    public string? Card { get; set; }
    public string? Target { get; set; }
    public List<CardMove> Plan { get; set; } = [];
    public bool RequiresReview { get; set; }
    public string Reason { get; set; } = "";
    public double ElapsedMs { get; set; }
    public int Samples { get; set; }
    public List<MoveOption> Alternatives { get; set; } = [];
}

// Local, public-observation-only tactical adviser. Scores are heuristics, NOT win probabilities.
// No mouse commands, state mutations or access to hidden game data take place here.
internal static class DurakStrategy
{
    internal const string Version = "classic36-three-v3";
    internal static int Rank(string c) => c[..^1] switch { "J" => 11, "Q" => 12, "K" => 13, "A" => 14, var n => int.Parse(n) };
    internal static bool Beats(string defense, string attack, char trump) =>
        defense[^1] == attack[^1] ? Rank(defense) > Rank(attack) : defense[^1] == trump && attack[^1] != trump;

    // Exact minimum-cost matching of this hand to ALL uncovered attacks, not a greedy card-by-card decision.
    internal static List<CardMove>? Cover(IReadOnlyList<string> hand, IReadOnlyList<string> attacks, char trump, Func<string, double>? cost = null)
    {
        if (attacks.Count > 6) throw new ArgumentException("Не более шести атак.");
        if (attacks.Count == 0) return [];
        int full = (1 << attacks.Count) - 1;
        var scores = Enumerable.Repeat(double.PositiveInfinity, full + 1).ToArray(); scores[0] = 0;
        var plans = new List<CardMove>?[full + 1]; plans[0] = [];
        foreach (string card in hand)
        {
            for (int mask = full; mask >= 0; mask--)
            {
                if (plans[mask] is null) continue;
                for (int a = 0; a < attacks.Count; a++)
                {
                    int bit = 1 << a;
                    if ((mask & bit) != 0 || !Beats(card, attacks[a], trump)) continue;
                    double candidate = scores[mask] + (cost?.Invoke(card) ?? Rank(card) + (card[^1] == trump ? 30 : 0));
                    if (candidate >= scores[mask | bit]) continue;
                    scores[mask | bit] = candidate;
                    plans[mask | bit] = [.. plans[mask]!, new CardMove(card, attacks[a])];
                }
            }
        }
        return plans[full];
    }

    internal static DecisionAnswer Decide(CardDocument doc, DecisionRequest r)
    {
        var timer = Stopwatch.StartNew();
        var answer = new DecisionAnswer { GameId = doc.GameId, Revision = doc.Revision };
        try
        {
            Validate(doc, r);
            if (!r.OurTurn || r.Phase == "wait") return Finish("wait", "WAIT_TURN", "Сейчас не наш ход.");
            var s = doc.State; char trump = r.Trump[0];
            var hand = s.Players[0].Known;
            // First deal: spend no sampling or human deliberation budget on an ordinary opening.
            if (r.Phase == "attack" && r.Pairs.Count == 0 && s.Stock == 18 && s.Discard.Count == 0 && s.Players.All(p => p.Count == 6))
            {
                var nontrumps = hand.Where(c => c[^1] != trump).ToList();
                var choices = nontrumps.Count > 0 ? nontrumps : hand;
                answer.Card = choices.OrderBy(Rank).ThenByDescending(c => hand.Count(h => Rank(h) == Rank(c))).ThenBy(c => c).First();
                return Finish("attack", nontrumps.Count > 0 ? "FIRST_LOW_NONTRUMP" : "FIRST_LOW_TRUMP",
                    nontrumps.Count > 0 ? "Первый ход: младшая некозырная карта; при равенстве — из пары." : "Все карты козырные: первый ход младшим козырем.");
            }
            int cap = Math.Min(6, r.DefenderStartCount);
            var open = r.Pairs.Where(p => p.Defense is null).Select(p => p.Attack).ToArray();
            double Value(string c) => Rank(c) - 5 + (c[^1] == trump ? s.Stock > 0 ? 20 : 9 : 0);
            var ranks = s.Table.Select(Rank).ToHashSet();
            // Prefer an already exposed rank, including a matching pair in hand, when costs are otherwise close.
            double NewRankCost(string card) => ranks.Contains(Rank(card)) ? 0 : 1.5;
            double Exposure(string card) => ranks.Contains(Rank(card)) ? 0 : s.Players.Skip(1)
                .SelectMany(p => CardModel.Chances(s, p.Id)).Where(c => Rank(c.Card) == Rank(card)).Sum(c => c.Probability);
            if (r.Phase == "defend")
            {
                if (open.Length == 0) return Finish("wait", "DEFENSE_COMPLETE", "Текущие карты отбиты, ждём подкидывания или отбоя.");
                if (r.Taking) return Finish("wait", "TAKE_ALREADY_DECLARED", "Взятие уже объявлено.");
                var plan = Cover(hand, open, trump, c => Value(c) + 2 * Exposure(c) + NewRankCost(c));
                if (plan is null) return Finish("take", "CANNOT_COVER_ALL", "Всю текущую атаку не отбить. Не тратим дополнительные карты.");
                plan = plan.OrderBy(m => hand.Count(c => Beats(c, m.Target, trump))).ToList();
                if (s.Stock == 0 && plan.Count == hand.Count)
                    return UsePlan(plan, "DEFENSE_LAST_CARDS", "Отбиваемся последними картами.");
                bool noFurther = r.Pairs.Count == cap || hand.Count == plan.Count;
                if (noFurther && plan.All(m => m.Card[^1] != trump) || plan.All(m => Value(m.Card) <= 6 && Exposure(m.Card) < .5))
                    return UsePlan(plan, "COVER_LOW_COST", "Полная защита без дорогих карт и лишних открытых достоинств.");

                // Enumerate alternatives for the most constrained attack. Each must still cover all other cards.
                string target = open.OrderBy(a => hand.Count(c => Beats(c, a, trump))).First();
                var candidates = new List<List<CardMove>>();
                foreach (var card in hand.Where(c => Beats(c, target, trump)))
                {
                    var tail = Cover(hand.Where(c => c != card).ToArray(), open.Where(c => c != target).ToArray(), trump,
                        c => Value(c) + 2 * Exposure(c) + NewRankCost(c));
                    if (tail is not null) candidates.Add([new CardMove(card, target), .. tail]);
                }
                var totals = new double[candidates.Count]; var failures = new int[candidates.Count];
                var random = Seed(doc, r);
                while (answer.Samples < 96 && timer.ElapsedMilliseconds < Math.Clamp(r.BudgetMs, 20, 500))
                {
                    var world = SampleHands(s, random);
                    for (int i = 0; i < candidates.Count; i++)
                    {
                        var candidate = candidates[i];
                        double risk = DefensePickup(s, r, candidate, world, trump);
                        failures[i] += risk > 0 ? 1 : 0;
                        totals[i] += candidate.Sum(m => Value(m.Card) * .11 + NewRankCost(m.Card)) + risk * (s.Table.Count + candidate.Count + 5);
                    }
                    answer.Samples++;
                }
                if (answer.Samples == 0) { answer.RequiresReview = true; return UsePlan(plan, "COVER_BUDGET_FALLBACK", "Недостаточно времени для оценки подкидывания."); }
                var options = candidates.Select((p, i) => new MoveOption("defend", p[0].Card, p[0].Target,
                    totals[i] / answer.Samples, failures[i] / (double)answer.Samples)).ToList();
                // Owner's rule: defend when the current attacks can all be covered.
                options = options.OrderBy(o => o.Score).ToList(); answer.Alternatives = options.Take(3).ToList();
                var best = options[0];
                answer.RequiresReview = options.Count > 1 && options[1].Score - best.Score < 1.1 || answer.Samples < 16;
                return UsePlan(candidates.Single(p => p[0].Card == best.Card), "COVER_BOTH_ATTACKERS", "Проверены варианты защиты и подкидывания обоих соперников.");
            }

            bool first = r.Pairs.Count == 0;
            var defender = s.Players.Single(p => p.Id == r.Defender);
            if (!first && (r.Pairs.Count >= cap || defender.Count == 0))
                return Finish(r.FinishButtonVisible ? "pass" : "wait", "ATTACK_LIMIT", "Лимит атак исчерпан.");
            var legal = hand.Where(c => first || ranks.Contains(Rank(c))).ToList();
            if (legal.Count == 0) return Finish(r.FinishButtonVisible ? "pass" : "wait", "NO_THROW", "Нет допустимого подкидывания.");
            if (s.Stock == 0 && hand.Count == 1)
            { answer.Card = legal[0]; return Finish("attack", "ATTACK_LAST_CARD", "Сбрасываем последнюю карту."); }
            if (r.Taking)
            {
                string cheapest = legal.MinBy(Value)!;
                if (cheapest[^1] != trump && Rank(cheapest) <= 10)
                { answer.Card = cheapest; return Finish("attack", "DUMP_LOW_ON_TAKE", "Сбрасываем младшую карту берущему сопернику."); }
            }
            if (first && legal.Any(c => c[^1] != trump) && s.Stock >= 12)
            {
                var low = legal.Where(c => c[^1] != trump).OrderBy(Value).ThenByDescending(c => hand.Count(h => Rank(h) == Rank(c))).First();
                if (Rank(low) <= 8 && hand.Count(c => Rank(c) == Rank(low)) >= 2)
                { answer.Card = low; return Finish("attack", "OPEN_LOW_PAIR", "Начинаем младшей парой, старшие карты сохраняем."); }
            }
            var scores = new double[legal.Count]; var blocked = new int[legal.Count]; var rng = Seed(doc, r);
            while (answer.Samples < 128 && timer.ElapsedMilliseconds < Math.Clamp(r.BudgetMs, 20, 500))
            {
                var world = SampleHands(s, rng); var opponent = world[r.Defender];
                for (int i = 0; i < legal.Count; i++)
                {
                    string card = legal[i];
                    bool cannotCover = r.Taking || Cover(opponent, [.. open, card], trump) is null;
                    if (cannotCover) blocked[i]++;
                    double duplicate = hand.Count(c => c != card && Rank(c) == Rank(card));
                    // Two hostile players, no teams: after a pickup, the third player may attack us next.
                    double retainedTrumps = hand.Count(c => c != card && c[^1] == trump);
                    double initiative = cannotCover && r.Attacker == "p0" && retainedTrumps == 0 ? 2 : 0;
                    scores[i] += Value(card) * (s.Stock > 0 ? .32 : .12) - (cannotCover ? 7 : 0)
                        - duplicate * 1.1 + initiative + (r.Taking && card[^1] == trump ? 8 : 0);
                }
                answer.Samples++;
            }
            if (answer.Samples == 0) { answer.RequiresReview = true; answer.Card = legal.MinBy(Value); return Finish("attack", "ATTACK_BUDGET_FALLBACK", "Требуется дополнительная проверка."); }
            var ranked = legal.Select((c, i) => new MoveOption("attack", c, null, scores[i] / answer.Samples,
                blocked[i] / (double)answer.Samples)).ToList();
            if (!first) ranked.Add(new MoveOption(r.FinishButtonVisible ? "pass" : "wait", null, null, .5, null));
            ranked = ranked.OrderBy(o => o.Score).ToList(); answer.Alternatives = ranked.Take(3).ToList();
            answer.Card = ranked[0].Card;
            answer.RequiresReview = ranked.Count > 1 && ranked[1].Score - ranked[0].Score < 1 || answer.Samples < 16;
            return Finish(ranked[0].Kind, "ATTACK_SAMPLE_HANDS", "Сравнены допустимые карты по совместимым раскладам и сохранению силы руки.");
        }
        catch (Exception error) when (error is ArgumentException or InvalidOperationException)
        { answer.RequiresReview = true; return Finish("review", "STATE_MISMATCH", error.Message); }

        DecisionAnswer Finish(string kind, string template, string reason)
        {
            answer.Kind = kind; answer.Template = template; answer.Reason = reason;
            answer.ElapsedMs = Math.Round(timer.Elapsed.TotalMilliseconds, 3); return answer;
        }
        DecisionAnswer UsePlan(List<CardMove> plan, string template, string reason)
        {
            answer.Plan = plan; answer.Card = plan[0].Card; answer.Target = plan[0].Target;
            return Finish("defend", template, reason);
        }
    }

    internal static void Validate(CardDocument doc, DecisionRequest r)
    {
        CardModel.Validate(doc.State);
        if (r.GameId != doc.GameId || r.Revision != doc.Revision || !doc.State.Reliable)
            throw new InvalidOperationException("Нужен свежий непрерывный учёт этой раздачи.");
        if (doc.State.DeckSize != 36 || doc.State.Players.Count != 3)
            throw new InvalidOperationException("Профиль стратегии: только 36 карт и три начальных игрока.");
        if (!r.OurTurn || r.Phase == "wait") return;
        if (r.Phase is not ("attack" or "throw" or "defend") || r.Trump.Length != 1 || !"SHDC".Contains(r.Trump))
            throw new ArgumentException("Укажите фазу и масть козыря S/H/D/C.");
        var recordedTrump = doc.State.TrumpSuit ?? doc.State.BottomTrump?[^1..];
        if (recordedTrump is not null && recordedTrump != r.Trump) throw new InvalidOperationException("Козырь не совпадает с раздачей.");
        if (r.Attacker == r.Defender || !doc.State.Players.Any(p => p.Id == r.Attacker) || !doc.State.Players.Any(p => p.Id == r.Defender))
            throw new ArgumentException("Укажите текущего атакующего и защитника.");
        if ((r.Phase == "defend") != (r.Defender == "p0") || r.Phase == "attack" && r.Attacker != "p0")
            throw new InvalidOperationException("Фаза не соответствует нашему месту за столом.");
        if (r.Phase == "throw" && r.Pairs.Count == 0) throw new InvalidOperationException("Третий игрок не начинает чужую атаку.");
        if (r.DefenderStartCount < 1 || r.Pairs.Count > Math.Min(6, r.DefenderStartCount))
            throw new InvalidOperationException("Неверный лимит атак.");
        var table = r.Pairs.SelectMany(p => p.Defense is null ? new[] { p.Attack } : new[] { p.Attack, p.Defense }).ToArray();
        if (table.Distinct().Count() != table.Length || !table.Order().SequenceEqual(doc.State.Table.Order()))
            throw new InvalidOperationException("Пары на столе не совпадают с учётом карт.");
        foreach (var pair in r.Pairs)
            if (pair.Defense is not null && !Beats(pair.Defense, pair.Attack, r.Trump[0]))
                throw new InvalidOperationException("На столе указана недопустимая пара защиты.");
        if (doc.State.Players.Single(p => p.Id == r.Defender).Count + r.Pairs.Count(p => p.Defense is not null) != r.DefenderStartCount)
            throw new InvalidOperationException("Количество карт защитника не сходится с началом розыгрыша.");
        if (doc.State.Players[0].Count == 0) throw new InvalidOperationException("Нет карт для хода. Дождитесь добора или окончания.");
    }
    private static Random Seed(CardDocument doc, DecisionRequest request) => new(BitConverter.ToInt32(
        SHA256.HashData(Encoding.UTF8.GetBytes(doc.GameId + doc.Revision + JsonSerializer.Serialize(request))), 0));

    internal static Dictionary<string, List<string>> SampleHands(CardState s, Random random)
    {
        var unknown = CardModel.Unknown(s); random.Shuffle(unknown);
        int cursor = 0;
        var hands = new Dictionary<string, List<string>>();
        foreach (var p in s.Players)
        {
            int count = p.Count - p.Known.Count;
            hands[p.Id] = [.. p.Known, .. unknown.Skip(cursor).Take(count)]; cursor += count;
        }
        return hands;
    }
    internal static double DefensePickup(CardState s, DecisionRequest r, List<CardMove> plan,
        Dictionary<string, List<string>> world, char trump)
    {
        var hand = s.Players[0].Known.Except(plan.Select(m => m.Card)).ToList();
        var ranks = s.Table.Concat(plan.Select(m => m.Card)).Select(Rank).ToHashSet();
        // Both opponents can throw. The stress test chooses their hardest currently matching card.
        var enemies = world.Where(p => p.Key != "p0").SelectMany(p => p.Value).ToList();
        int attacks = r.Pairs.Count, cap = Math.Min(6, r.DefenderStartCount);
        while (attacks < cap && hand.Count > 0)
        {
            var next = enemies.Where(c => ranks.Contains(Rank(c)))
                .OrderBy(c => hand.Count(h => Beats(h, c, trump))).ThenByDescending(Rank).FirstOrDefault();
            if (next is null) break;
            enemies.Remove(next); attacks++;
            var cover = hand.Where(c => Beats(c, next, trump)).MinBy(c => Rank(c) + (c[^1] == trump ? 30 : 0));
            if (cover is null) return 1;
            hand.Remove(cover); ranks.Add(Rank(cover));
        }
        return 0;
    }
}
