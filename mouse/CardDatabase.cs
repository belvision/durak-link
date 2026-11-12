using Microsoft.Data.Sqlite;
using System.Data;
using System.Text.Json;

internal sealed class CardDatabase
{
    private readonly string root;
    internal string PathName => Path.Combine(root, "durak.sqlite3");
    internal CardDatabase(string root) => this.root = root;
    private SqliteConnection Open()
    {
        Directory.CreateDirectory(root);
        var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = PathName, DefaultTimeout = 3 }.ToString());
        connection.Open();
        using var cmd = connection.CreateCommand();
        cmd.CommandText = """
            PRAGMA journal_mode=WAL;
            PRAGMA foreign_keys=ON;
            CREATE TABLE IF NOT EXISTS games(game_id TEXT PRIMARY KEY, revision INTEGER NOT NULL, updated TEXT NOT NULL,
                current INTEGER NOT NULL, payload TEXT NOT NULL);
            CREATE UNIQUE INDEX IF NOT EXISTS one_current ON games(current) WHERE current=1;
            CREATE TABLE IF NOT EXISTS events(game_id TEXT NOT NULL REFERENCES games(game_id), event_id TEXT NOT NULL,
                payload TEXT NOT NULL, PRIMARY KEY(game_id,event_id));
            CREATE TABLE IF NOT EXISTS decisions(id TEXT PRIMARY KEY, game_id TEXT NOT NULL REFERENCES games(game_id), revision INTEGER NOT NULL,
                at TEXT NOT NULL, template TEXT NOT NULL, elapsed_ms REAL NOT NULL, review INTEGER NOT NULL, request TEXT NOT NULL, answer TEXT NOT NULL);
            CREATE INDEX IF NOT EXISTS decisions_game ON decisions(game_id,revision);
            CREATE TABLE IF NOT EXISTS results(game_id TEXT PRIMARY KEY REFERENCES games(game_id), outcome TEXT NOT NULL,
                placement INTEGER NOT NULL, at TEXT NOT NULL);
            CREATE TABLE IF NOT EXISTS settings(key TEXT PRIMARY KEY,value TEXT NOT NULL);
            CREATE TABLE IF NOT EXISTS executions(decision_id TEXT PRIMARY KEY REFERENCES decisions(id), status TEXT NOT NULL,
                elapsed_ms INTEGER NOT NULL,note TEXT NOT NULL,at TEXT NOT NULL);
            CREATE TABLE IF NOT EXISTS execution_attempts(id INTEGER PRIMARY KEY,decision_id TEXT NOT NULL REFERENCES decisions(id),
                status TEXT NOT NULL,elapsed_ms INTEGER NOT NULL,note TEXT NOT NULL,at TEXT NOT NULL);
            CREATE TABLE IF NOT EXISTS decision_states(decision_id TEXT PRIMARY KEY REFERENCES decisions(id),payload TEXT NOT NULL);
            CREATE TABLE IF NOT EXISTS control_events(id INTEGER PRIMARY KEY,at TEXT NOT NULL,kind TEXT NOT NULL,note TEXT NOT NULL);
            """;
        cmd.ExecuteNonQuery();
        return connection;
    }
    internal CardDocument? ReadCurrent()
    {
        using var c = Open(); using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT payload FROM games WHERE current=1";
        return cmd.ExecuteScalar() is string json ? JsonSerializer.Deserialize<CardDocument>(json, CardStore.Json) : null;
    }
    internal bool DisplayCleared()
    {
        using var c = Open(); using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT value FROM settings WHERE key='display-cleared'";
        return cmd.ExecuteScalar() as string == "1";
    }
    internal void ClearCurrent(string gameId)
    {
        using var c = Open(); using var tx = c.BeginTransaction(); using var cmd = c.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = "UPDATE games SET current=0 WHERE current=1 AND game_id=$game";
        cmd.Parameters.AddWithValue("$game", gameId);
        if (cmd.ExecuteNonQuery() != 1) throw new InvalidOperationException("Текущая партия изменилась; обновите состояние.");
        cmd.CommandText = "INSERT INTO settings(key,value) VALUES('display-cleared','1') ON CONFLICT(key) DO UPDATE SET value='1'";
        cmd.ExecuteNonQuery(); tx.Commit();
    }
    internal void ControlEvent(string kind, string note)
    {
        using var c = Open(); using var cmd = c.CreateCommand();
        cmd.CommandText = "INSERT INTO control_events(at,kind,note) VALUES($at,$kind,$note)";
        cmd.Parameters.AddWithValue("$at", DateTimeOffset.UtcNow.ToString("O"));
        cmd.Parameters.AddWithValue("$kind", kind); cmd.Parameters.AddWithValue("$note", note);
        cmd.ExecuteNonQuery();
    }
    internal void Save(CardDocument doc)
    {
        using var c = Open(); using var tx = c.BeginTransaction(); using var cmd = c.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = "UPDATE games SET current=0 WHERE current=1 AND game_id<>$game";
        cmd.Parameters.AddWithValue("$game", doc.GameId); cmd.ExecuteNonQuery();
        cmd.CommandText = "INSERT INTO settings(key,value) VALUES('display-cleared','0') ON CONFLICT(key) DO UPDATE SET value='0'";
        cmd.ExecuteNonQuery();
        cmd.CommandText = "INSERT INTO games VALUES($game,$revision,$updated,1,$payload) ON CONFLICT(game_id) DO UPDATE SET revision=$revision,updated=$updated,current=1,payload=$payload";
        cmd.Parameters.AddWithValue("$revision", doc.Revision); cmd.Parameters.AddWithValue("$updated", doc.Updated.ToString("O"));
        cmd.Parameters.AddWithValue("$payload", JsonSerializer.Serialize(doc, CardStore.Json)); cmd.ExecuteNonQuery();
        foreach (var entry in doc.Processed)
        {
            using var evt = c.CreateCommand(); evt.Transaction = tx;
            evt.CommandText = "INSERT OR IGNORE INTO events VALUES($game,$id,$payload)";
            evt.Parameters.AddWithValue("$game", doc.GameId); evt.Parameters.AddWithValue("$id", entry.Key);
            evt.Parameters.AddWithValue("$payload", entry.Value); evt.ExecuteNonQuery();
        }
        tx.Commit();
    }
    internal int Threshold()
    {
        using var c = Open(); using var cmd = c.CreateCommand(); cmd.CommandText = "SELECT value FROM settings WHERE key='threshold'";
        return int.TryParse(cmd.ExecuteScalar() as string, out int n) ? Math.Clamp(n, 0, 100) : 90;
    }
    internal void SetThreshold(int value)
    {
        using var c = Open(); using var cmd = c.CreateCommand();
        cmd.CommandText = "INSERT INTO settings VALUES('threshold',$v) ON CONFLICT(key) DO UPDATE SET value=$v";
        cmd.Parameters.AddWithValue("$v", Math.Clamp(value, 0, 100).ToString()); cmd.ExecuteNonQuery();
    }
    internal void Decision(CardDocument doc, DecisionRequest request, DecisionAnswer answer)
    {
        using var c = Open(); using var tx = c.BeginTransaction(); using var cmd = c.CreateCommand(); cmd.Transaction = tx;
        cmd.CommandText = "INSERT INTO decisions VALUES($id,$game,$revision,$at,$template,$ms,$review,$request,$answer)";
        cmd.Parameters.AddWithValue("$id", answer.Id); cmd.Parameters.AddWithValue("$game", doc.GameId);
        cmd.Parameters.AddWithValue("$revision", doc.Revision); cmd.Parameters.AddWithValue("$at", DateTimeOffset.UtcNow.ToString("O"));
        cmd.Parameters.AddWithValue("$template", answer.Template); cmd.Parameters.AddWithValue("$ms", answer.ElapsedMs);
        cmd.Parameters.AddWithValue("$review", answer.RequiresReview ? 1 : 0);
        cmd.Parameters.AddWithValue("$request", JsonSerializer.Serialize(request, CardStore.Json));
        cmd.Parameters.AddWithValue("$answer", JsonSerializer.Serialize(answer, CardStore.Json)); cmd.ExecuteNonQuery();
        using var state = c.CreateCommand(); state.Transaction = tx;
        state.CommandText = "INSERT INTO decision_states VALUES($id,$state)";
        state.Parameters.AddWithValue("$id", answer.Id); state.Parameters.AddWithValue("$state", JsonSerializer.Serialize(doc.State, CardStore.Json));
        state.ExecuteNonQuery(); tx.Commit();
    }
    internal void Result(string game, string outcome, int placement)
    {
        if (outcome is not ("win" or "loss" or "draw") || placement is < 1 or > 3)
            throw new ArgumentException("Нужен подтверждённый результат win/loss/draw и место 1–3.");
        using var c = Open(); using var cmd = c.CreateCommand();
        cmd.CommandText = "INSERT INTO results VALUES($game,$outcome,$place,$at) ON CONFLICT(game_id) DO UPDATE SET outcome=$outcome,placement=$place,at=$at";
        cmd.Parameters.AddWithValue("$game", game); cmd.Parameters.AddWithValue("$outcome", outcome);
        cmd.Parameters.AddWithValue("$place", placement); cmd.Parameters.AddWithValue("$at", DateTimeOffset.UtcNow.ToString("O")); cmd.ExecuteNonQuery();
    }
    internal DataTable Query(string sql)
    {
        using var c = Open(); using var cmd = c.CreateCommand(); cmd.CommandText = sql;
        using var reader = cmd.ExecuteReader(); var table = new DataTable(); table.Load(reader); return table;
    }
    internal void Execution(string decisionId, string status, int ms, string note)
    {
        if (status is not ("confirmed" or "missed" or "cancelled") || ms < 0 || note.Length > 2000)
            throw new ArgumentException("Нужен факт выполнения: confirmed/missed/cancelled, время в мс и короткое пояснение.");
        using var c = Open(); using var tx = c.BeginTransaction(); using var cmd = c.CreateCommand(); cmd.Transaction = tx;
        cmd.CommandText = "INSERT INTO executions VALUES($id,$status,$ms,$note,$at) ON CONFLICT(decision_id) DO UPDATE SET status=$status,elapsed_ms=$ms,note=$note,at=$at";
        cmd.Parameters.AddWithValue("$id", decisionId); cmd.Parameters.AddWithValue("$status", status); cmd.Parameters.AddWithValue("$ms", ms);
        cmd.Parameters.AddWithValue("$note", note); cmd.Parameters.AddWithValue("$at", DateTimeOffset.UtcNow.ToString("O")); cmd.ExecuteNonQuery();
        cmd.CommandText = "INSERT INTO execution_attempts(decision_id,status,elapsed_ms,note,at) VALUES($id,$status,$ms,$note,$at)";
        cmd.ExecuteNonQuery(); tx.Commit();
    }
    internal object Review(string game)
    {
        using var c = Open(); using var cmd = c.CreateCommand(); cmd.CommandText = "SELECT payload FROM games WHERE game_id=$id";
        cmd.Parameters.AddWithValue("$id", game);
        var json = cmd.ExecuteScalar() as string ?? throw new ArgumentException("Раздача не найдена.");
        var doc = JsonSerializer.Deserialize<CardDocument>(json, CardStore.Json)!;
        cmd.CommandText = "SELECT d.template,d.elapsed_ms,d.review,d.request,d.answer,e.status,e.elapsed_ms,e.note,s.payload FROM decisions d LEFT JOIN executions e ON d.id=e.decision_id LEFT JOIN decision_states s ON d.id=s.decision_id WHERE d.game_id=$id ORDER BY d.at";
        var rows = new List<object>();
        using (var reader = cmd.ExecuteReader())
            while (reader.Read()) rows.Add(new { Template = reader.GetString(0), DecisionMs = reader.GetDouble(1), Review = reader.GetInt64(2) != 0,
                Position = JsonSerializer.Deserialize<DecisionRequest>(reader.GetString(3), CardStore.Json),
                Answer = JsonSerializer.Deserialize<DecisionAnswer>(reader.GetString(4), CardStore.Json),
                Execution = reader.IsDBNull(5) ? "unrecorded" : reader.GetString(5),
                ExecutionMs = reader.IsDBNull(6) ? (int?)null : reader.GetInt32(6), Note = reader.IsDBNull(7) ? "" : reader.GetString(7),
                ObservedState = reader.IsDBNull(8) ? null : JsonSerializer.Deserialize<CardState>(reader.GetString(8), CardStore.Json) });
        cmd.CommandText = "SELECT a.decision_id,a.status,a.elapsed_ms,a.note,a.at FROM execution_attempts a JOIN decisions d ON d.id=a.decision_id WHERE d.game_id=$id ORDER BY a.id";
        var attempts = new List<object>();
        using (var reader = cmd.ExecuteReader())
            while (reader.Read()) attempts.Add(new { DecisionId = reader.GetString(0), Status = reader.GetString(1), ElapsedMs = reader.GetInt32(2), Note = reader.GetString(3), At = reader.GetString(4) });
        cmd.CommandText = "SELECT outcome FROM results WHERE game_id=$id";
        return new { GameId = game, Outcome = cmd.ExecuteScalar() as string ?? "unrecorded", doc.History, CurrentState = doc.State, ProcessedEvents = doc.Processed, Decisions = rows, Attempts = attempts,
            Interpretation = "Confirmed observations and recommendations; missing executions and gaps limit causal conclusions. No automatic proof of loss cause." };
    }
    internal string Summary()
    {
        using var c = Open(); using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*),COALESCE(SUM(outcome='win'),0),COALESCE(SUM(outcome='loss'),0),COALESCE(SUM(outcome='draw'),0) FROM results";
        using var reader = cmd.ExecuteReader(); reader.Read();
        return $"Завершено: {reader.GetInt64(0)}    Победы: {reader.GetInt64(1)}    Поражения: {reader.GetInt64(2)}    Ничьи: {reader.GetInt64(3)}";
    }
    internal string? Outcome(string game)
    {
        using var c = Open(); using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT outcome FROM results WHERE game_id=$id"; cmd.Parameters.AddWithValue("$id", game);
        return cmd.ExecuteScalar() as string;
    }
    internal string HistoryText(string game)
    {
        using var c = Open(); using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT payload FROM games WHERE game_id=$id"; cmd.Parameters.AddWithValue("$id", game);
        if (cmd.ExecuteScalar() is not string payload) return "Партия не найдена.";
        var doc = JsonSerializer.Deserialize<CardDocument>(payload, CardStore.Json)!;
        var initial = doc.History.FirstOrDefault()?.Before ?? doc.State;
        var lines = new List<string>
        {
            "Игроки: " + string.Join(" · ", initial.Players.Select(p => p.Name)),
            "Наши карты в начале: " + string.Join(" ", initial.Players[0].Known.Select(CardCodes.Display)),
            "Открытый козырь: " + (initial.BottomTrump is null ? "не записан" : CardCodes.Display(initial.BottomTrump)), "", "Ходы", ""
        };
        lines.AddRange(doc.History.Select(h => $"{h.At.ToOffset(TimeSpan.FromHours(3)):dd.MM HH:mm:ss}  {h.Description}"));
        lines.AddRange(["", "Решения и выполнение", ""]);
        cmd.CommandText = "SELECT COALESCE(a.at,d.at),d.answer,COALESCE(a.status,e.status),COALESCE(a.note,e.note) FROM decisions d LEFT JOIN execution_attempts a ON a.decision_id=d.id LEFT JOIN executions e ON e.decision_id=d.id WHERE d.game_id=$id ORDER BY d.at,a.id";
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            var answer = JsonSerializer.Deserialize<DecisionAnswer>(reader.GetString(1), CardStore.Json)!;
            string execution = reader.IsDBNull(2) ? "выполнение не записано" : reader.GetString(2) switch
            { "confirmed" => "подтверждено", "missed" => "не сработало", _ => "отменено" };
            string card = answer.Card is null ? "" : CardCodes.Display(answer.Card);
            string target = answer.Target is null ? "" : " → " + CardCodes.Display(answer.Target);
            lines.Add($"{DateTimeOffset.Parse(reader.GetString(0)).ToOffset(TimeSpan.FromHours(3)):HH:mm:ss}  {card}{target}  {answer.ElapsedMs:0.#} мс; {execution}");
            lines.Add("  " + answer.Reason + (reader.IsDBNull(3) ? "" : " / " + reader.GetString(3)));
        }
        if (!doc.State.Reliable) lines.Add("\nЕсть пропуск наблюдений: выводы о причине поражения ограничены.");
        return string.Join(Environment.NewLine, lines);
    }
}

internal sealed class CardStatisticsWindow : Form
{
    private readonly System.Windows.Forms.Timer refresh = new() { Interval = 2000 };
    internal CardStatisticsWindow(CardStore? store = null)
    {
        var database = new CardDatabase((store ?? new CardStore()).Root);
        Text = "Durak Link — Статистика"; ClientSize = new Size(820, 460); Font = new Font("Segoe UI", 10);
        var summary = new Label { Dock = DockStyle.Top, Height = 46, Padding = new Padding(10) };
        var tabs = new TabControl { Dock = DockStyle.Fill };
        var gamesPage = new TabPage("История партий"); var decisionsPage = new TabPage("Скорость решений");
        tabs.TabPages.AddRange([gamesPage, decisionsPage]);
        DataGridView NewGrid() => new() { Dock = DockStyle.Fill, ReadOnly = true, AllowUserToAddRows = false,
            AllowUserToDeleteRows = false, AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill,
            RowHeadersVisible = false, SelectionMode = DataGridViewSelectionMode.FullRowSelect, MultiSelect = false };
        var games = NewGrid(); var grid = NewGrid();
        var split = new SplitContainer { Size = new Size(800, 380), Dock = DockStyle.Fill, Orientation = Orientation.Horizontal, SplitterDistance = 150 };
        var history = new TextBox { Dock = DockStyle.Fill, ReadOnly = true, Multiline = true, ScrollBars = ScrollBars.Vertical, BackColor = Color.White };
        split.Panel1.Controls.Add(games); split.Panel2.Controls.Add(history); gamesPage.Controls.Add(split); decisionsPage.Controls.Add(grid);
        Controls.Add(tabs); Controls.Add(summary);
        string? selection = null;
        games.SelectionChanged += (_, _) =>
        {
            if (!games.Columns.Contains("game_id") || games.CurrentRow?.Cells["game_id"].Value is not string id) return;
            selection = id;
            try { history.Text = database.HistoryText(id); } catch (Exception error) { history.Text = error.Message; }
        };
        string previousList = "";
        void UpdateView()
        {
            try
            {
                summary.Text = database.Summary();
                var list = database.Query("SELECT g.game_id,datetime(g.updated,'+3 hours') AS 'Минск',json_extract(g.payload,'$.State.Players[1].Name') || ' / ' || json_extract(g.payload,'$.State.Players[2].Name') AS 'Соперники',CASE r.outcome WHEN 'win' THEN 'Победа' WHEN 'loss' THEN 'Поражение' WHEN 'draw' THEN 'Ничья' ELSE 'Итог не записан' END AS 'Результат',r.placement AS 'Место',json_array_length(g.payload,'$.History') AS 'Событий' FROM games g LEFT JOIN results r ON r.game_id=g.game_id ORDER BY g.updated DESC");
                string signature = string.Join('|', list.Rows.Cast<DataRow>().Select(row => string.Join('/', row.ItemArray)));
                if (signature != previousList)
                {
                    string? oldSelection = selection;
                    games.DataSource = list;
                    if (games.Columns["game_id"] is DataGridViewColumn idColumn) idColumn.Visible = false;
                    previousList = signature;
                    foreach (DataGridViewRow row in games.Rows)
                        if (row.Cells["game_id"].Value as string == oldSelection) games.CurrentCell = row.Cells[1];
                }
                if (selection is not null && !history.Focused)
                {
                    string text = database.HistoryText(selection);
                    if (history.Text != text) history.Text = text;
                }
                if (tabs.SelectedTab == decisionsPage)
                    grid.DataSource = database.Query("SELECT datetime(at,'+3 hours') AS 'Минск',template AS 'Правило',ROUND(elapsed_ms,2) AS 'мс',review AS 'Нужен разбор' FROM decisions ORDER BY at DESC LIMIT 100");
            }
            catch (Exception error) { summary.Text = "Статистика недоступна: " + error.Message; }
        }
        Shown += (_, _) => UpdateView(); refresh.Tick += (_, _) => UpdateView(); refresh.Start();
        FormClosed += (_, _) => refresh.Dispose();
    }
}
