using System.Drawing.Drawing2D;

internal sealed class CardTrackerWindow : Form
{
    private readonly CardStore store;
    private readonly System.Windows.Forms.Timer refresh = new() { Interval = 600 };
    private readonly TrackBar threshold = new() { Minimum = 0, Maximum = 100, TickFrequency = 10, SmallChange = 1,
        LargeChange = 10, Dock = DockStyle.Fill, AccessibleName = "Минимальная вероятность карты" };
    private readonly Label value = new() { Dock = DockStyle.Left, Width = 180, TextAlign = ContentAlignment.MiddleLeft };
    private readonly OpponentBoard board = new() { Dock = DockStyle.Fill };
    private string revision = "";
    private readonly string baseTitle;
    internal CardTrackerWindow(CardStore? store = null, bool demo = false)
    {
        this.store = store ?? new CardStore();
        Text = demo ? "Durak Link — Карты соперников — ТЕСТ" : "Durak Link — Карты соперников";
        baseTitle = Text;
        Font = new Font("Segoe UI", 10); BackColor = Color.White;
        ClientSize = new Size(850, 580); MinimumSize = new Size(730, 520);
        StartPosition = FormStartPosition.CenterScreen;
        var top = new Panel { Dock = DockStyle.Top, Height = 52, Padding = new Padding(12, 5, 12, 0) };
        top.Controls.Add(threshold); top.Controls.Add(value);
        Controls.Add(board); Controls.Add(top);
        try { threshold.Value = this.store.Database.Threshold(); } catch { threshold.Value = 90; }
        void UpdateThreshold()
        {
            value.Text = $"Вероятность ≥ {threshold.Value}%";
            board.Threshold = threshold.Value;
            board.Invalidate();
        }
        threshold.ValueChanged += (_, _) => UpdateThreshold();
        threshold.MouseUp += (_, _) => SaveThreshold();
        threshold.KeyUp += (_, _) => SaveThreshold();
        UpdateThreshold();
        refresh.Tick += (_, _) => Read(); refresh.Start();
        Shown += (_, _) => Read();
        FormClosed += (_, _) => { refresh.Dispose(); SaveThreshold(); };
    }
    private void SaveThreshold() { try { store.Database.SetThreshold(threshold.Value); } catch { } }
    private void Read()
    {
        try
        {
            var doc = store.Read();
            string? outcome = doc is null ? null : store.Database.Outcome(doc.GameId);
            string next = doc is null ? "empty" : $"{doc.GameId}:{doc.Revision}:{outcome}";
            if (revision == next) return;
            revision = next;
            Text = baseTitle + (outcome is null ? "" : " — Партия завершена");
            board.SetState(doc?.State, doc is null ? "Ожидаю раздачу" : doc.State.Reliable ? "" : "Учёт прерван");
        }
        catch { revision = ""; board.SetState(null, "Данные недоступны"); }
    }
}

internal sealed class OpponentBoard : Control
{
    private List<(string Name, int Count, List<CardChance> Cards)> players = [];
    private string message = "Ожидаю раздачу";
    private string accounting = "";
    private List<string> discarded = [];
    private bool stale;
    [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
    internal int Threshold { get; set; } = 90;
    internal OpponentBoard() { DoubleBuffered = true; ResizeRedraw = true; BackColor = Color.White; }
    internal void SetState(CardState? state, string message)
    {
        this.message = message;
        stale = state is { Reliable: false };
        accounting = state is null ? "" : stale
            ? "Последняя запись до пропуска: карты могли измениться"
            : $"Колода: {state.Stock}   Стол: {state.Table.Count}   Отбой: {state.Discard.Count}";
        discarded = state?.Discard.ToList() ?? [];
        players = state is null ? [] : state.Players.Skip(1)
            .Select(p => (p.Name, p.Count, stale
                ? p.Known.Select(c => new CardChance(c, 1, "historical")).ToList()
                : CardModel.Chances(state, p.Id))).ToList();
        AccessibleName = stale ? accounting : string.Join("; ", players.Select(p => $"{p.Name}: {p.Count} карт"));
        Invalidate();
    }
    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        var g = e.Graphics; g.SmoothingMode = SmoothingMode.AntiAlias;
        if (players.Count == 0)
        {
            TextRenderer.DrawText(g, message, Font, ClientRectangle, Color.DimGray,
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
            return;
        }
        TextRenderer.DrawText(g, accounting, Font, new Point(14, 4), Color.DimGray);
        const int summaryHeight = 26;
        int discardWidth = Math.Clamp(Width / 3, 190, 280);
        int opponentWidth = Width - discardWidth;
        DrawDiscard(g, opponentWidth, summaryHeight, discardWidth);
        int groupHeight = (ClientSize.Height - summaryHeight) / players.Count;
        using var heading = new Font("Segoe UI", 12, FontStyle.Bold);
        for (int index = 0; index < players.Count; index++)
        {
            var p = players[index];
            int top = summaryHeight + index * groupHeight;
            TextRenderer.DrawText(g, stale ? $"{p.Name} · до пропуска" : $"{p.Name} · {p.Count} карт", heading, new Rectangle(14, top + 5, opponentWidth - 28, 29),
                Color.FromArgb(30, 50, 45), TextFormatFlags.Left);
            if (index > 0) g.DrawLine(Pens.Gainsboro, 12, top, opponentWidth - 12, top);
            var visible = p.Cards.Where(c => c.Probability + 1e-12 >= Threshold / 100.0).ToList();
            if (visible.Count == 0)
            {
                if (p.Count == 0) continue;
                var best = p.Cards.MaxBy(c => c.Probability);
                string detail = best is null ? (stale ? "Нет записанных карт до пропуска" : "Нет данных о картах") :
                    $"Ни одна карта не достигла порога {Threshold}%. Максимальная оценка: {best.Probability:P1}.";
                TextRenderer.DrawText(g, detail, Font, new Rectangle(18, top + 40, opponentWidth - 36, 25),
                    Color.DimGray, TextFormatFlags.EndEllipsis);
                if (best is not null)
                {
                    string candidates = string.Join("  ", p.Cards.Select(c => CardCodes.Display(c.Card)));
                    TextRenderer.DrawText(g, "Возможные карты: " + candidates, Font,
                        new Rectangle(18, top + 68, opponentWidth - 36, Math.Max(0, groupHeight - 70)),
                        Color.DimGray, TextFormatFlags.WordBreak);
                }
                continue;
            }
            int availableWidth = opponentWidth - 28, availableHeight = groupHeight - 42;
            int bestCols = 1; float bestWidth = 0;
            for (int cols = 1; cols <= visible.Count; cols++)
            {
                int rows = (visible.Count + cols - 1) / cols;
                float width = Math.Min(66, Math.Min((availableWidth - cols * 6f) / cols,
                    (availableHeight / (float)rows - 6) / 1.47f));
                if (width >= bestWidth) { bestWidth = width; bestCols = cols; }
            }
            for (int i = 0; i < visible.Count; i++)
                DrawCard(g, visible[i], new RectangleF(14 + i % bestCols * (bestWidth + 6),
                    top + 36 + i / bestCols * (bestWidth * 1.47f + 6), bestWidth, bestWidth * 1.47f));
        }
    }
    private void DrawDiscard(Graphics graphics, int left, int top, int width)
    {
        graphics.DrawLine(Pens.Gainsboro, left, top, left, Height - 10);
        using var heading = new Font("Segoe UI", 11, FontStyle.Bold);
        TextRenderer.DrawText(graphics, stale ? "Отбой до пропуска" : $"Отбой · {discarded.Count}", heading,
            new Point(left + 12, top + 7), Color.FromArgb(30, 50, 45));
        const int cellWidth = 39, cellHeight = 31;
        int columns = Math.Max(1, (width - 20) / cellWidth);
        for (int i = 0; i < discarded.Count; i++)
        {
            string card = discarded[i];
            var cell = new Rectangle(left + 10 + i % columns * cellWidth, top + 40 + i / columns * cellHeight,
                cellWidth - 4, cellHeight - 4);
            graphics.DrawRectangle(Pens.Gainsboro, cell);
            TextRenderer.DrawText(graphics, CardCodes.Display(card), Font, cell,
                card[^1] is 'H' or 'D' ? Color.Firebrick : Color.FromArgb(20, 30, 40),
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
        }
    }
    private static void DrawCard(Graphics g, CardChance chance, RectangleF box)
    {
        float caption = 17, faceHeight = box.Height - caption;
        bool historical = chance.Evidence == "historical";
        Color edge = historical ? Color.DarkOrange : chance.Probability == 1 ? Color.SeaGreen : Color.SlateGray;
        Color ink = chance.Card[^1] is 'H' or 'D' ? Color.Firebrick : Color.FromArgb(20, 30, 40);
        using var border = new Pen(edge, chance.Probability == 1 ? 2 : 1);
        g.DrawRectangle(border, box.X + 1, box.Y + 1, box.Width - 2, faceHeight - 2);
        using var rank = new Font("Segoe UI", Math.Max(9, box.Width * .2f), FontStyle.Bold);
        using var suit = new Font("Segoe UI Symbol", Math.Max(12, box.Width * .33f));
        using var small = new Font("Segoe UI", 8);
        TextRenderer.DrawText(g, chance.Card[..^1], rank, new Point((int)box.X + 3, (int)box.Y + 3), ink);
        TextRenderer.DrawText(g, CardCodes.Display(chance.Card)[^1..], suit,
            Rectangle.Round(new RectangleF(box.X, box.Y + faceHeight * .38f, box.Width, faceHeight * .58f)), ink, TextFormatFlags.HorizontalCenter);
        string percent = historical ? "ранее" : chance.Probability == 1 ? "100%" : $"{Math.Floor(chance.Probability * 1000) / 10:0.#}%";
        TextRenderer.DrawText(g, percent, small, Rectangle.Round(new RectangleF(box.X, box.Y + faceHeight, box.Width, caption)),
            edge, TextFormatFlags.HorizontalCenter);
    }
}
