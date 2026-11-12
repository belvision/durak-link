using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading.Channels;

internal enum LobbyTarget { Open, First, Ready }

internal static class ManualMouse
{
    internal static Rectangle ParseBounds(string[] args)
    {
        if (args.Length != 4 || !int.TryParse(args[0], out int x) || !int.TryParse(args[1], out int y)
            || !int.TryParse(args[2], out int width) || !int.TryParse(args[3], out int height)
            || width < 100 || height < 100)
            throw new ArgumentException("--manual requires X Y WIDTH HEIGHT of the AirPlay video.");
        return new Rectangle(x, y, width, height);
    }

    internal static async Task RunAsync(Rectangle? bounds, Func<byte[], Task<bool>> send, Func<LobbyTarget, Task<bool>> selectLobby)
    {
        var reports = Channel.CreateUnbounded<byte[]>(new UnboundedChannelOptions
        {
            SingleReader = true,
            SingleWriter = true,
        });
        var ui = new Thread(() =>
        {
            try
            {
                Application.SetHighDpiMode(HighDpiMode.PerMonitorV2);
                using var overlay = new Overlay(bounds, reports.Writer, selectLobby);
                Application.Run(overlay.ControlWindow);
                reports.Writer.TryComplete();
            }
            catch (Exception error)
            {
                reports.Writer.TryComplete(error);
            }
        });
        ui.SetApartmentState(ApartmentState.STA);
        ui.Start();

        bool lastReportSucceeded = true;
        await foreach (var report in reports.Reader.ReadAllAsync())
        {
            bool succeeded = await send(report);
            if (succeeded != lastReportSucceeded)
                Console.WriteLine(succeeded ? "Bluetooth input restored." : "Bluetooth input not delivered.");
            lastReportSucceeded = succeeded;
        }
    }

    private sealed class Overlay : Form
    {
        private const int ToggleHotkey = 1;
        private const int HotkeyMessage = 0x0312;
        private readonly ChannelWriter<byte[]> reports;
        private readonly Stopwatch clock = Stopwatch.StartNew();
        private readonly System.Windows.Forms.Timer timer = new() { Interval = 16 };
        private readonly System.Windows.Forms.Timer layoutTimer = new() { Interval = 500 };
        private readonly Form controls;
        private readonly Button toggle;
        private readonly Button lobby;
        private readonly Button firstGame;
        private readonly Button readyGame;
        private CardTrackerWindow? cardWindow;
        private CardStatisticsWindow? statisticsWindow;
        private readonly Rectangle? fixedBounds;
        private Point? previous;
        private double deltaX;
        private double deltaY;
        private byte buttons;
        private bool cursorHidden;
        private bool lobbyBusy;
        private CancellationTokenSource? readyWait;
        private readonly SemaphoreSlim lobbyGate = new(1, 1);
        private long lastReadySent;
        internal Form ControlWindow => controls;

        internal Overlay(Rectangle? fixedBounds, ChannelWriter<byte[]> reports, Func<LobbyTarget, Task<bool>> selectLobby)
        {
            this.reports = reports;
            this.fixedBounds = fixedBounds;
            var savedLayout = AirPlayLayout.Load();
            if (savedLayout is not null)
            {
                AirPlayLayout.Restore(savedLayout);
                layoutTimer.Tick += (_, _) => AirPlayLayout.Restore(savedLayout);
                layoutTimer.Start();
            }
            var bounds = fixedBounds ?? FindVideoBounds() ?? new Rectangle(140, 125, 405, 872);
            StartPosition = FormStartPosition.Manual;
            Bounds = bounds;
            FormBorderStyle = FormBorderStyle.None;
            ShowInTaskbar = true;
            TopMost = true;
            Opacity = 0.02;
            BackColor = Color.Black;
            KeyPreview = true;
            Text = "Durak Link - manual mouse";
            KeyDown += (_, e) =>
            {
                if (e.KeyCode == Keys.Escape) Close();
                if (e.KeyCode == Keys.F8 && buttons == 0) Align();
                if (e.KeyCode == Keys.F9) ToggleControl();
            };
            MouseEnter += (_, _) =>
            {
                if (cursorHidden) return;
                Cursor.Hide();
                cursorHidden = true;
            };
            MouseLeave += (_, _) =>
            {
                ShowWindowsCursor();
                if (buttons == 0) previous = null;
            };
            MouseMove += (_, e) =>
            {
                Track(e.Location);
                if (e.X <= 18 || e.Y <= 18 ||
                    e.X >= ClientSize.Width - 18 || e.Y >= ClientSize.Height - 18)
                {
                    // Keep relative movement available when the Windows cursor reaches the overlay edge.
                    var center = new Point(ClientSize.Width / 2, ClientSize.Height / 2);
                    previous = center;
                    Cursor.Position = PointToScreen(center);
                }
            };
            MouseDown += (_, e) => { if (e.Button == MouseButtons.Left) SetButton(e.Location, 1); };
            MouseUp += (_, e) => { if (e.Button == MouseButtons.Left) SetButton(e.Location, 0); };
            MouseCaptureChanged += (_, _) =>
            {
                if (!Capture && buttons != 0)
                    SetButton(PointToClient(Cursor.Position), 0);
            };
            FormClosing += (_, _) =>
            {
                readyWait?.Cancel();
                if (buttons != 0)
                    SetButton(PointToClient(Cursor.Position), 0);
                ShowWindowsCursor();
                timer.Dispose();
                layoutTimer.Dispose();
            };
            controls = new Form
            {
                Text = "Durak Link",
                StartPosition = FormStartPosition.Manual,
                FormBorderStyle = FormBorderStyle.FixedSingle,
                MaximizeBox = false,
                MinimizeBox = true,
                ShowInTaskbar = true,
                TopMost = true,
                ClientSize = new Size(235, 290),
            };
            var area = Screen.FromRectangle(bounds).WorkingArea;
            int left = bounds.Right + controls.Width + 8 <= area.Right
                ? bounds.Right + 8 : Math.Max(area.Left, bounds.Left - controls.Width - 8);
            controls.Location = new Point(left, Math.Max(area.Top, bounds.Top));
            toggle = new Button { Text = "Включить мышь", Bounds = new Rectangle(8, 8, 217, 34) };
            toggle.Click += (_, _) => ToggleControl();
            lobby = new Button { Text = "Открытые игры", Bounds = new Rectangle(8, 48, 217, 34) };
            lobby.Click += async (_, _) => await RunLobbyStep(selectLobby, LobbyTarget.Open);
            firstGame = new Button { Text = "Первая игра", Bounds = new Rectangle(8, 88, 217, 34) };
            firstGame.Click += async (_, _) => await RunLobbyStep(selectLobby, LobbyTarget.First);
            readyGame = new Button { Text = "Готов", Bounds = new Rectangle(8, 128, 217, 34),
                BackColor = Color.Honeydew, Font = new Font(SystemFonts.DefaultFont, FontStyle.Bold) };
            readyGame.Click += async (_, _) => await RunLobbyStep(selectLobby, LobbyTarget.Ready);
            var tracker = new Button { Text = "Карты игроков", Bounds = new Rectangle(8, 168, 217, 34) };
            tracker.Click += (_, _) =>
            {
                if (cardWindow is null || cardWindow.IsDisposed)
                {
                    cardWindow = new CardTrackerWindow();
                    cardWindow.Show(controls);
                }
                else
                {
                    if (cardWindow.WindowState == FormWindowState.Minimized) cardWindow.WindowState = FormWindowState.Normal;
                    cardWindow.Activate();
                }
            };
            var statistics = new Button { Text = "История и статистика", Bounds = new Rectangle(8, 208, 217, 34) };
            statistics.Click += (_, _) =>
            {
                if (statisticsWindow is null || statisticsWindow.IsDisposed)
                { statisticsWindow = new CardStatisticsWindow(); statisticsWindow.Show(controls); }
                else { statisticsWindow.WindowState = FormWindowState.Normal; statisticsWindow.Activate(); }
            };
            var stop = new Button { Text = "Закрыть режим", Bounds = new Rectangle(8, 248, 217, 34) };
            stop.Click += (_, _) => controls.Close();
            controls.Controls.AddRange([toggle, lobby, firstGame, readyGame, tracker, statistics, stop]);
            controls.Shown += (_, _) =>
            {
                _ = Handle;
                cardWindow = new CardTrackerWindow();
                cardWindow.Show(controls);
            };
            controls.FormClosed += (_, _) => { if (!IsDisposed) Close(); };
            FormClosed += (_, _) => { if (!controls.IsDisposed) controls.Close(); };
            timer.Tick += (_, _) => FlushMove();
            timer.Start();
            Console.WriteLine("Manual overlay ready. Ctrl+Alt+F8 pauses or resumes. F8 aligns; Esc closes.");
        }

        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            if (!RegisterHotKey(Handle, ToggleHotkey, 0x4003, (uint)Keys.F8))
                Console.WriteLine("Could not register Ctrl+Alt+F8. Esc still closes the overlay.");
        }

        protected override void OnHandleDestroyed(EventArgs e)
        {
            UnregisterHotKey(Handle, ToggleHotkey);
            base.OnHandleDestroyed(e);
        }

        protected override void WndProc(ref Message message)
        {
            if (message.Msg == HotkeyMessage && message.WParam == ToggleHotkey)
            {
                ToggleControl();
                return;
            }
            base.WndProc(ref message);
        }

        private void ToggleControl()
        {
            if (lobbyBusy) return;
            readyWait?.Cancel();
            if (Visible)
            {
                if (buttons != 0)
                    SetButton(PointToClient(Cursor.Position), 0);
                ShowWindowsCursor();
                previous = null;
                deltaX = deltaY = 0;
                Hide();
                toggle.Text = "Включить мышь";
                lobby.Enabled = true;
                Console.WriteLine("Manual control paused.");
            }
            else
            {
                var video = fixedBounds ?? FindVideoBounds();
                if (video is null)
                {
                    MessageBox.Show(controls, "На iPhone выберите «Повтор экрана» → Durak-iPhone.",
                        "Трансляция не подключена", MessageBoxButtons.OK, MessageBoxIcon.Information);
                    return;
                }
                Bounds = video.Value;
                previous = null;
                Show();
                Activate();
                toggle.Text = "Пауза";
                lobby.Enabled = false;
                Console.WriteLine("Manual control resumed.");
            }
        }

        private async Task RunLobbyStep(Func<LobbyTarget, Task<bool>> selectLobby, LobbyTarget target)
        {
            if (lobbyBusy) return;
            readyWait?.Cancel();
            if (Visible) ToggleControl();
            if (FindVideoBounds() is null)
            {
                MessageBox.Show(controls, "Сначала подключите трансляцию iPhone.", "Durak Link");
                return;
            }

            lobbyBusy = true;
            lobby.Enabled = firstGame.Enabled = readyGame.Enabled = toggle.Enabled = false;
            try
            {
                var clock = Stopwatch.StartNew();
                if (!await SelectSerialized(selectLobby, target))
                {
                    MessageBox.Show(controls, "Команда не доставлена на iPhone.", "Durak Link");
                    return;
                }
                Console.WriteLine($"Lobby {target} delivered in {clock.ElapsedMilliseconds} ms.");
                if (target == LobbyTarget.First)
                {
                    readyWait = new CancellationTokenSource();
                    _ = WatchReady(selectLobby, readyWait.Token);
                }
            }
            catch (Exception error)
            {
                MessageBox.Show(controls, error.Message, "Durak Link");
            }
            finally
            {
                lobbyBusy = false;
                lobby.Enabled = firstGame.Enabled = readyGame.Enabled = toggle.Enabled = true;
            }
        }

        private async Task<bool> SelectSerialized(Func<LobbyTarget, Task<bool>> selectLobby, LobbyTarget target, CancellationToken cancel = default)
        {
            await lobbyGate.WaitAsync(cancel);
            try
            {
                cancel.ThrowIfCancellationRequested();
                if (target == LobbyTarget.Ready && Environment.TickCount64 - lastReadySent < 1500) return true;
                bool delivered = await selectLobby(target);
                if (delivered && target == LobbyTarget.Ready) lastReadySent = Environment.TickCount64;
                return delivered;
            }
            finally { lobbyGate.Release(); }
        }

        private async Task WatchReady(Func<LobbyTarget, Task<bool>> selectLobby, CancellationToken cancel)
        {
            readyGame.Text = "Готов · автоожидание";
            try
            {
                var watcher = new ReadyWatcher();
                string result = await watcher.WaitAndClick(async () =>
                {
                    cancel.ThrowIfCancellationRequested();
                    return await SelectSerialized(selectLobby, LobbyTarget.Ready, cancel);
                }, cancel);
                Console.WriteLine("AutoReady: " + result);
                new CardDatabase(DurakPaths.Cards).ControlEvent("auto-ready", result);
                readyGame.Text = result == "ready-sent" ? "Готов · отправлено" : "Готов";
            }
            catch (OperationCanceledException) { if (!controls.IsDisposed) readyGame.Text = "Готов"; }
            catch (Exception error)
            {
                if (!controls.IsDisposed) readyGame.Text = "Готов · вручную";
                Console.WriteLine("AutoReady error: " + error.Message);
            }
        }

        private static Rectangle? FindVideoBounds()
        {
            var window = AirPlayLayout.FindWindow();
            var origin = new NativePoint();
            if (window != IntPtr.Zero && GetClientRect(window, out var rect) && ClientToScreen(window, ref origin)
                && rect.Right >= 100 && rect.Bottom >= 100)
                return new Rectangle(origin.X, origin.Y, rect.Right, rect.Bottom);
            return null;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct NativePoint { internal int X, Y; }

        [StructLayout(LayoutKind.Sequential)]
        private struct NativeRect { internal int Left, Top, Right, Bottom; }

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool GetClientRect(IntPtr window, out NativeRect rect);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool ClientToScreen(IntPtr window, ref NativePoint point);

        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool RegisterHotKey(IntPtr window, int id, uint modifiers, uint key);

        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool UnregisterHotKey(IntPtr window, int id);

        private void Align()
        {
            deltaX = deltaY = 0;
            // Reset iOS to a known corner and give the Windows mouse room in every direction.
            for (int step = 0; step < 4; step++)
                WriteReport(-127, -127);
            var origin = new Point(ClientSize.Width / 2, ClientSize.Height / 2);
            previous = origin;
            Cursor.Position = PointToScreen(origin);
            Console.WriteLine($"{clock.ElapsedMilliseconds} ms aligned top-left");
        }

        private void ShowWindowsCursor()
        {
            if (!cursorHidden) return;
            Cursor.Show();
            cursorHidden = false;
        }

        private void Track(Point position)
        {
            if (previous is Point last)
            {
                // The iPhone applies its own pointer acceleration.
                deltaX += (position.X - last.X) / 5.0;
                deltaY += (position.Y - last.Y) / 5.0;
            }
            previous = position;
        }

        private void SetButton(Point position, byte next)
        {
            Track(position);
            FlushMove();
            if (buttons == next)
                return;
            buttons = next;
            WriteReport(0, 0);
            Console.WriteLine($"{clock.ElapsedMilliseconds} ms {(buttons == 1 ? "down" : "up")} ({position.X},{position.Y})");
            Capture = next != 0;
        }

        private void FlushMove()
        {
            int x = Math.Clamp((int)deltaX, -127, 127);
            int y = Math.Clamp((int)deltaY, -127, 127);
            if (x == 0 && y == 0)
                return;
            deltaX -= x;
            deltaY -= y;
            WriteReport(x, y);
            if (buttons != 0)
                Console.WriteLine($"{clock.ElapsedMilliseconds} ms held dx={x} dy={y}");
        }

        private void WriteReport(int x, int y) =>
            reports.TryWrite([buttons, unchecked((byte)x), unchecked((byte)y)]);
    }
}
