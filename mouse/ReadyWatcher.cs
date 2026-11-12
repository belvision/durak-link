using System.Diagnostics;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using Windows.Globalization;
using Windows.Graphics.Imaging;
using Windows.Media.Ocr;
using Windows.Storage.Streams;

// Armed only by selection of a table. Never joins tables and never clicks a game action.
internal sealed class ReadyWatcher
{
    private readonly OcrEngine engine = OcrEngine.TryCreateFromLanguage(new Language("ru-RU"))
        ?? throw new InvalidOperationException("Распознавание русского текста Windows недоступно.");
    internal sealed record Probe(bool Ready, string Text, double Ms);

    internal async Task<Probe> Read(Bitmap bitmap)
    {
        var clock = Stopwatch.StartNew();
        using var stream = new InMemoryRandomAccessStream();
        using (var bytes = new MemoryStream())
        {
            bitmap.Save(bytes, ImageFormat.Png);
            using var writer = new DataWriter(stream.GetOutputStreamAt(0));
            writer.WriteBytes(bytes.ToArray()); await writer.StoreAsync(); await writer.FlushAsync();
        }
        var decoder = await BitmapDecoder.CreateAsync(stream);
        using var image = await decoder.GetSoftwareBitmapAsync(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Premultiplied);
        var result = await engine.RecognizeAsync(image);
        bool prompt = result.Lines.Any(line => line.Text.ToUpperInvariant().Contains("НАЖМИТЕ")
            && line.Words.Any(w => w.BoundingRect.Y < bitmap.Height * .6));
        bool button = result.Lines.SelectMany(l => l.Words).Any(w =>
            Normalize(w.Text) == "ГОТОВ" && w.BoundingRect.X < bitmap.Width * .42
            && w.BoundingRect.Y > bitmap.Height * .55);
        return new Probe(prompt && button, result.Text, clock.Elapsed.TotalMilliseconds);
    }

    private static string Normalize(string text) => new(text.ToUpperInvariant().Where(char.IsLetter).ToArray());

    internal async Task<string> WaitAndClick(Func<Task<bool>> click, CancellationToken cancel)
    {
        var clock = Stopwatch.StartNew();
        int hits = 0;
        var initialWindow = AirPlayLayout.FindWindow();
        while (clock.Elapsed < TimeSpan.FromSeconds(90))
        {
            cancel.ThrowIfCancellationRequested();
            if (initialWindow == IntPtr.Zero || initialWindow != AirPlayLayout.FindWindow()) return "window-changed";
            using var bitmap = Capture(initialWindow);
            if (bitmap is null) { hits = 0; await Task.Delay(120, cancel); continue; }
            var probe = await Read(bitmap);
            cancel.ThrowIfCancellationRequested();
            hits = probe.Ready ? hits + 1 : 0;
            if (hits >= 2)
            {
                // Recheck visibility after OCR, then run the existing Ready script exactly once.
                using var visible = Capture(initialWindow);
                if (visible is null) { hits = 0; continue; }
                bool delivered = await click();
                return delivered ? "ready-sent" : "delivery-failed";
            }
            await Task.Delay(120, cancel);
        }
        return "timeout-no-ready";
    }

    internal static Bitmap? Capture(IntPtr window, double topFraction = .65)
    {
        if (window == IntPtr.Zero || IsIconic(window) || !IsWindowVisible(window)
            || !GetClientRect(window, out var rect)) return null;
        var origin = new Point();
        if (!ClientToScreen(window, ref origin) || rect.Right < 200 || rect.Bottom < 500) return null;
        var screen = Screen.FromHandle(window).WorkingArea;
        int top = origin.Y + (int)(rect.Bottom * topFraction);
        int bottom = Math.Min(origin.Y + (int)(rect.Bottom * .98), screen.Bottom);
        var area = Rectangle.FromLTRB(origin.X, top, origin.X + rect.Right, bottom);
        if (area.Height < 180 || !screen.Contains(area)) return null;
        // Refuse occluded/minimized/off-screen AirPlay; no screenshots of unrelated windows.
        for (int y = 10; y < area.Height; y += 30)
            for (int x = 10; x < area.Width; x += 30)
                if (GetAncestor(WindowFromPoint(new Point(area.X + x, area.Y + y)), 2) != window) return null;
        var bitmap = new Bitmap(area.Width, area.Height, PixelFormat.Format32bppArgb);
        try { using var g = Graphics.FromImage(bitmap); g.CopyFromScreen(area.Location, Point.Empty, area.Size); return bitmap; }
        catch { bitmap.Dispose(); throw; }
    }

    internal static async Task<int> Test()
    {
        var watcher = new ReadyWatcher(); int passed = 0;
        foreach (var label in new[] { "Готов", "Беру", "Бито", "Открытые", "" })
        foreach (bool prompt in new[] { true, false })
        {
            using var image = new Bitmap(440, 300);
            using (var g = Graphics.FromImage(image))
            using (var font = new Font("Arial", 24, FontStyle.Bold))
            {
                g.Clear(Color.SteelBlue);
                if (prompt) g.DrawString("Нажмите \"Готов\"", font, Brushes.White, 70, 50);
                g.FillRectangle(Brushes.White, 0, 210, 170, 65);
                g.DrawString(label, font, Brushes.Crimson, 15, 220);
            }
            var result = await watcher.Read(image);
            if (result.Ready != (prompt && label == "Готов"))
                throw new Exception($"Ready detector failed: {label}, prompt={prompt}; {result.Text}");
            passed++;
        }
        Console.WriteLine($"PASS {passed} OCR cases; no Bluetooth input."); return 0;
    }

    [StructLayout(LayoutKind.Sequential)] private struct NativeRect { internal int Left, Top, Right, Bottom; }
    [DllImport("user32.dll")] private static extern bool GetClientRect(IntPtr window, out NativeRect rect);
    [DllImport("user32.dll")] private static extern bool ClientToScreen(IntPtr window, ref Point point);
    [DllImport("user32.dll")] private static extern bool IsIconic(IntPtr window);
    [DllImport("user32.dll")] private static extern bool IsWindowVisible(IntPtr window);
    [DllImport("user32.dll")] private static extern IntPtr WindowFromPoint(Point point);
    [DllImport("user32.dll")] private static extern IntPtr GetAncestor(IntPtr window, uint flags);
}
