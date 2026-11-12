using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json;

internal static class AirPlayLayout
{
    private static readonly string Receiver = DurakPaths.Receiver;
    private static readonly string Settings = Path.Combine(DurakPaths.Root, "airplay-window.json");

    internal sealed record Layout(int X, int Y, int Width, int Height)
    {
        internal Rectangle Bounds => new(X, Y, Width, Height);
    }

    internal static Layout SaveCurrent()
    {
        IntPtr window = FindWindow();
        if (window == IntPtr.Zero || IsIconic(window) || !GetWindowRect(window, out var rect))
            throw new InvalidOperationException("Open the AirPlay video before saving its layout.");

        var layout = new Layout(rect.Left, rect.Top, rect.Right - rect.Left, rect.Bottom - rect.Top);
        Directory.CreateDirectory(DurakPaths.Root);
        File.WriteAllText(Settings, JsonSerializer.Serialize(layout, new JsonSerializerOptions { WriteIndented = true }));
        return layout;
    }

    internal static Layout? Load()
    {
        if (!File.Exists(Settings))
            return null;
        var layout = JsonSerializer.Deserialize<Layout>(File.ReadAllText(Settings));
        return layout is { Width: >= 100, Height: >= 100 } ? layout : null;
    }

    internal static void Restore(Layout layout)
    {
        IntPtr window = FindWindow();
        if (window == IntPtr.Zero || IsIconic(window) || !GetWindowRect(window, out var rect))
            return;
        var current = Rectangle.FromLTRB(rect.Left, rect.Top, rect.Right, rect.Bottom);
        if (current != layout.Bounds)
            SetWindowPos(window, IntPtr.Zero, layout.X, layout.Y, layout.Width, layout.Height, 0x14);
    }

    internal static IntPtr FindWindow()
    {
        foreach (var process in Process.GetProcessesByName("uxplay-windows"))
        {
            using (process)
            {
                if (process.MainWindowTitle == "AirPlay Video Stream" &&
                    string.Equals(process.MainModule?.FileName, Receiver, StringComparison.OrdinalIgnoreCase))
                    return process.MainWindowHandle;
            }
        }
        return IntPtr.Zero;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeRect { internal int Left, Top, Right, Bottom; }

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetWindowRect(IntPtr window, out NativeRect rect);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsIconic(IntPtr window);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetWindowPos(IntPtr window, IntPtr insertAfter, int x, int y, int width, int height, uint flags);
}
