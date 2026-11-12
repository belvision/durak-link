using Windows.Devices.Bluetooth;
using Windows.Devices.Bluetooth.GenericAttributeProfile;
using Windows.Storage.Streams;

Console.InputEncoding = new System.Text.UTF8Encoding(false);
Console.OutputEncoding = new System.Text.UTF8Encoding(false);
Application.SetHighDpiMode(HighDpiMode.PerMonitorV2);

if (args is ["--wait-phone-turn", var turnTimeout]
    && int.TryParse(turnTimeout, out int turnSeconds) && turnSeconds is >= 1 and <= 120)
{
    var watcher = new ReadyWatcher();
    var clock = System.Diagnostics.Stopwatch.StartNew();
    var window = AirPlayLayout.FindWindow();
    string? previous = null;
    int frames = 0;
    while (clock.Elapsed < TimeSpan.FromSeconds(turnSeconds))
    {
        using var frame = ReadyWatcher.Capture(window);
        if (frame is null)
        {
            Console.WriteLine("{\"Status\":\"unavailable\"}");
            return 2;
        }
        // Read the lower left action area. Opponents' labels sit outside this crop.
        int top = (int)(frame.Height * .56);
        using var button = frame.Clone(new Rectangle(0, top, (int)(frame.Width * .39),
            (int)(frame.Height * .35)), frame.PixelFormat);
        var probe = await watcher.Read(button);
        frames++;
        string? action = System.Text.RegularExpressions.Regex.Match(probe.Text,
            @"\b(Беру|Бито|Пас|Ваш\s+ход)\b",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase).Value.ToLowerInvariant();
        if (action.Length != 0 && action == previous)
        {
            Console.WriteLine(System.Text.Json.JsonSerializer.Serialize(new
            {
                Status = "turn",
                Action = action,
                Frames = frames,
                OcrMs = Math.Round(probe.Ms)
            }));
            return 0;
        }
        previous = action;
        await Task.Delay(120);
    }
    Console.WriteLine(System.Text.Json.JsonSerializer.Serialize(new { Status = "timeout", Frames = frames }));
    return 1;
}

if (args is ["--phone-text"])
{
    using var frame = ReadyWatcher.Capture(AirPlayLayout.FindWindow(), 0);
    if (frame is null) { Console.WriteLine("AirPlay unavailable or covered"); return 2; }
    Console.WriteLine((await new ReadyWatcher().Read(frame)).Text);
    return 0;
}

if (args is ["--collect-card-corners", var sampleRegions, var sampleDirectory])
{
    var regions = System.Text.Json.JsonSerializer.Deserialize<CardFrameRegion[]>(File.ReadAllText(sampleRegions))
        ?? throw new ArgumentException("Нужны зоны карты для текущего масштаба AirPlay.");
    using var frame = ReadyWatcher.Capture(AirPlayLayout.FindWindow(), 0);
    if (frame is null) return 2;
    Directory.CreateDirectory(sampleDirectory);
    string batch = Guid.NewGuid().ToString("N");
    var bounds = new Rectangle(0, 0, frame.Width, frame.Height);
    for (int i = 0; i < regions.Length; i++)
    {
        var region = regions[i];
        using var corner = region.Extract(frame, bounds);
        var path = Path.Combine(sampleDirectory, $"{batch}-{region.Zone}-{i}.png");
        corner.Save(path, System.Drawing.Imaging.ImageFormat.Png);
    }
    Console.WriteLine(System.Text.Json.JsonSerializer.Serialize(new { Batch = batch, Count = regions.Length }));
    return 0;
}

if (args is ["--read-card-corner", var templateDirectory, var cornerPath])
{
    var reader = new CardCornerReader(templateDirectory);
    using var corner = new Bitmap(cornerPath);
    var match = reader.Read(corner);
    Console.WriteLine(System.Text.Json.JsonSerializer.Serialize(match));
    return match.Card is null ? 2 : 0;
}

if (args is ["--probe-live-cards", var atlasDirectory, var regionsPath])
{
    var regions = System.Text.Json.JsonSerializer.Deserialize<CardFrameRegion[]>(File.ReadAllText(regionsPath))
        ?? throw new ArgumentException("Нужны зоны карты для текущего масштаба AirPlay.");
    using var frame = ReadyWatcher.Capture(AirPlayLayout.FindWindow(), 0);
    if (frame is null)
    {
        Console.Error.WriteLine("Окно AirPlay недоступно или перекрыто.");
        return 2;
    }
    var reader = new CardCornerReader(atlasDirectory);
    var matches = CardFrameRegion.ReadAll(frame, regions, reader);
    Console.WriteLine(System.Text.Json.JsonSerializer.Serialize(new { frame.Width, frame.Height, Matches = matches }));
    return 0;
}

if (args is ["--watch-live-cards", var watchAtlas, var watchRegions, var duration]
    && int.TryParse(duration, out int seconds) && seconds is >= 1 and <= 3600)
{
    var regions = System.Text.Json.JsonSerializer.Deserialize<CardFrameRegion[]>(File.ReadAllText(watchRegions))
        ?? throw new ArgumentException("Нужны зоны карты для текущего масштаба AirPlay.");
    var reader = new CardCornerReader(watchAtlas);
    var window = AirPlayLayout.FindWindow();
    var clock = System.Diagnostics.Stopwatch.StartNew();
    string? previous = null;
    string? candidate = null;
    while (clock.Elapsed < TimeSpan.FromSeconds(seconds))
    {
        using var frame = ReadyWatcher.Capture(window, 0);
        object observation = frame is null
            ? new { Status = "unavailable" }
            : new
            {
                Status = "observed",
                frame.Width,
                frame.Height,
                Cards = CardFrameRegion.ReadAll(frame, regions, reader)
                    .Select(result => new { result.Region.Zone, result.Match.Card }).ToArray()
            };
        var json = System.Text.Json.JsonSerializer.Serialize(observation);
        if (json == candidate && json != previous)
        {
            Console.WriteLine(json);
            previous = json;
        }
        candidate = json;
        await Task.Delay(120);
    }
    return 0;
}

if (args is ["--ready-self-test"]) return await ReadyWatcher.Test();
if (args is ["--ready-probe"])
{
    Application.SetHighDpiMode(HighDpiMode.PerMonitorV2);
    using var bitmap = ReadyWatcher.Capture(AirPlayLayout.FindWindow());
    Console.WriteLine(bitmap is null ? "AirPlay region unavailable or occluded"
        : System.Text.Json.JsonSerializer.Serialize(await new ReadyWatcher().Read(bitmap)));
    return 0;
}

// Card tracking is available without advertising Bluetooth or sending any input.
if (args.Length > 0 && args[0].StartsWith("--cards", StringComparison.Ordinal))
    return CardTrackerCli.Run(args);

if (args.Length > 0 && args[0] == "--plan-shot")
{
    if (!TryPlanShot(args, out var planned))
    {
        Console.Error.WriteLine("Expected cursorX cursorY cardX cardY targetX targetY in the saved AirPlay layout.");
        return 2;
    }
    Console.WriteLine(string.Join(' ', planned));
    return 0;
}

if (args is ["--save-layout"])
{
    Application.SetHighDpiMode(HighDpiMode.PerMonitorV2);
    Console.WriteLine($"Saved AirPlay window: {AirPlayLayout.SaveCurrent()}");
    return 0;
}

// HID Report ID 1. Three buttons, then signed relative X and Y.
byte[] reportMap =
[
    0x05, 0x01, 0x09, 0x02, 0xa1, 0x01, 0x85, 0x01,
    0x09, 0x01, 0xa1, 0x00, 0x05, 0x09, 0x19, 0x01,
    0x29, 0x03, 0x15, 0x00, 0x25, 0x01, 0x95, 0x03,
    0x75, 0x01, 0x81, 0x02, 0x95, 0x01, 0x75, 0x05,
    0x81, 0x03, 0x05, 0x01, 0x09, 0x30, 0x09, 0x31,
    0x15, 0x81, 0x25, 0x7f, 0x75, 0x08, 0x95, 0x02,
    0x81, 0x06, 0xc0, 0xc0,
];

GattServiceProvider? provider = null;
try
{
    bool baseline = args.Contains("--baseline");
    var serviceId = baseline ? new Guid("07fecb1d-e47f-43cb-b8b0-d533b2a7bb0a") : Uuid(0x1812);
    var service = await GattServiceProvider.CreateAsync(serviceId);
    Check("Create HID service", service.Error);
    provider = service.ServiceProvider;
    var gatt = provider.Service;
    await AddCharacteristic(gatt, 0x2a4a, GattCharacteristicProperties.Read, [0x11, 0x01, 0, 0x02]);
    await AddCharacteristic(gatt, 0x2a4b, GattCharacteristicProperties.Read, reportMap);
    var control = await AddCharacteristic(gatt, 0x2a4c, GattCharacteristicProperties.WriteWithoutResponse);
    control.WriteRequested += async (_, args) =>
    {
        using var deferral = args.GetDeferral();
        var request = await args.GetRequestAsync();
        if (request?.Option == GattWriteOption.WriteWithResponse)
            request.Respond();
    };
    var report = await AddCharacteristic(gatt, 0x2a4d,
        GattCharacteristicProperties.Read | GattCharacteristicProperties.Notify);
    report.ReadRequested += async (_, args) =>
    {
        using var deferral = args.GetDeferral();
        (await args.GetRequestAsync())?.RespondWithValue(BufferOf([0, 0, 0]));
    };
    var reference = await report.CreateDescriptorAsync(Uuid(0x2908), new GattLocalDescriptorParameters
    {
        StaticValue = BufferOf([1, 1]),
        ReadProtectionLevel = GattProtectionLevel.EncryptionRequired,
    });
    Check("Create input report reference", reference.Error);
    Console.WriteLine("Mouse GATT characteristics created.");
    if (!args.Contains("--advertise"))
        return 0;

    provider.AdvertisementStatusChanged += (_, change) =>
        Console.WriteLine($"Advertising: {change.Status}; {change.Error}");
    report.SubscribedClientsChanged += (_, _) =>
        Console.WriteLine($"Subscribed input clients: {report.SubscribedClients.Count}");
    provider.StartAdvertising(new GattServiceProviderAdvertisingParameters
    {
        IsConnectable = true,
        IsDiscoverable = true,
    });
    await Task.Delay(TimeSpan.FromSeconds(2));
    Console.WriteLine($"Advertising state: {provider.AdvertisementStatus}");
    if (provider.AdvertisementStatus is not (GattServiceProviderAdvertisementStatus.Started
        or GattServiceProviderAdvertisementStatus.StartedWithoutAllAdvertisementData)
        && (baseline || report.SubscribedClients.Count != 1))
        return 2;
    int manualIndex = Array.IndexOf(args, "--manual");
    if (manualIndex >= 0 && !baseline)
    {
        var coordinates = args.Skip(manualIndex + 1).ToArray();
        Rectangle? bounds = coordinates.Length == 0 ? null : ManualMouse.ParseBounds(coordinates);
        await ManualMouse.RunAsync(bounds, async bytes =>
        {
            var subscribers = report.SubscribedClients;
            if (subscribers.Count != 1)
                return false;
            var result = await report.NotifyValueAsync(BufferOf(bytes), subscribers[0]);
            return result.Status == GattCommunicationStatus.Success;
        }, async target =>
        {
            var subscribers = report.SubscribedClients;
            return subscribers.Count == 1 && await SelectLobbyTarget(report, subscribers[0], target);
        });
        return 0;
    }
    Console.WriteLine(baseline
        ? "Press Enter to stop the baseline."
        : "Commands: shot cursorX cursorY cardX cardY targetX targetY, move X Y, drag X Y, play/flick/tap2 moveX moveY targetX targetY, lobby-open, lobby-first, lobby-ready, click, quit.");
    CancellationTokenSource? autoReadyCancel = null;
    Task autoReadyTask = Task.CompletedTask;
    while (Console.ReadLine() is { Length: > 0 } command && command != "quit" && !baseline)
    {
        autoReadyCancel?.Cancel();
        await autoReadyTask;
        var clients = report.SubscribedClients;
        if (clients.Count != 1)
        {
            Console.WriteLine($"Expected exactly one subscribed device; found {clients.Count}.");
            Console.WriteLine("DONE " + command);
            continue;
        }
        var client = clients[0];
        var fields = command.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (fields.Length > 0 && fields[0] == "shot")
        {
            if (!TryPlanShot(fields, out var planned))
            {
                Console.WriteLine("Shot out of range. Check the cursor, card and target coordinates.");
                Console.WriteLine("DONE " + command);
                continue;
            }
            fields = planned;
        }
        if (fields.Length == 3 && fields[0] is "move" or "drag" &&
            TryDelta(fields[1], out var x) && TryDelta(fields[2], out var y))
        {
            if (fields[0] == "drag")
                await Drag(report, client, x, y);
            else
                await SendReport(report, client, [0, unchecked((byte)x), unchecked((byte)y)]);
        }
        else if (fields.Length == 5 && fields[0] is "play" or "flick" or "tap2" &&
            TryDelta(fields[1], out var moveX) && TryDelta(fields[2], out var moveY) &&
            TryDelta(fields[3], out var dragX) && TryDelta(fields[4], out var dragY))
        {
            if (await SendReport(report, client, [0, unchecked((byte)moveX), unchecked((byte)moveY)]))
            {
                await Task.Delay(70);
                if (fields[0] is "play" or "flick")
                    await Drag(report, client, dragX, dragY, flick: fields[0] == "flick",
                        liftFirst: fields[0] == "play" && dragY < 0);
                else if (await Click(report, client))
                {
                    await Task.Delay(160);
                    if (await SendReport(report, client, [0, unchecked((byte)dragX), unchecked((byte)dragY)]))
                    {
                        await Task.Delay(70);
                        await Click(report, client);
                    }
                }
            }
        }
        else if (command is "lobby-open" or "lobby-first" or "lobby-ready" or "table-take" or "table-done")
        {
            bool delivered = await SelectLobbyTarget(report, client, command switch
            {
                "lobby-open" => LobbyTarget.Open,
                "lobby-first" => LobbyTarget.First,
                _ => LobbyTarget.Ready,
            });
            if (delivered && command == "lobby-first")
            {
                autoReadyCancel?.Dispose();
                autoReadyCancel = new CancellationTokenSource();
                var token = autoReadyCancel.Token;
                autoReadyTask = Task.Run(async () =>
                {
                    string result;
                    try
                    {
                        result = await new ReadyWatcher().WaitAndClick(async () =>
                        {
                            var subscribed = report.SubscribedClients;
                            return subscribed.Count == 1 && await SelectLobbyTarget(report, subscribed[0], LobbyTarget.Ready);
                        }, token);
                    }
                    catch (OperationCanceledException) { result = "cancelled"; }
                    catch (Exception error) { result = "error: " + error.Message; }
                    new CardDatabase(DurakPaths.Cards).ControlEvent("auto-ready", result);
                    Console.WriteLine("AUTO_READY: " + result);
                });
            }
        }
        else if (command == "click")
        {
            await Click(report, client);
        }
        else
        {
            Console.WriteLine("Unknown command.");
        }
        Console.WriteLine("DONE " + command);
    }
    autoReadyCancel?.Cancel();
    await autoReadyTask;
    autoReadyCancel?.Dispose();
    return 0;
}
catch (Exception error)
{
    Console.Error.WriteLine(error.Message);
    return 1;
}
finally
{
    if (provider?.AdvertisementStatus is GattServiceProviderAdvertisementStatus.Started
        or GattServiceProviderAdvertisementStatus.StartedWithoutAllAdvertisementData)
        provider.StopAdvertising();
}

static Guid Uuid(ushort value) => BluetoothUuidHelper.FromShortId(value);

static IBuffer BufferOf(byte[] bytes)
{
    using var writer = new DataWriter();
    writer.WriteBytes(bytes);
    return writer.DetachBuffer();
}

static void Check(string operation, BluetoothError error)
{
    if (error != BluetoothError.Success)
        throw new InvalidOperationException($"{operation}: {error}");
}

static bool TryDelta(string value, out sbyte delta) =>
    sbyte.TryParse(value, out delta) && delta >= -127;

static bool TryPlanShot(string[] args, out string[] planned)
{
    planned = [];
    if (args.Length != 7)
        return false;

    var points = new int[6];
    for (int i = 0; i < points.Length; i++)
        if (!int.TryParse(args[i + 1], out points[i]) || points[i] is < 0 or > 10000)
            return false;

    var (cursorX, cursorY, cardX, cardY, targetX, targetY) =
        (points[0], points[1], points[2], points[3], points[4], points[5]);
    if (targetY >= cardY)
        return false;

    // AirPlay screenshot pixels per HID unit measured with this iPhone and window layout.
    const double moveScale = 5.5;
    const double dragScale = 3.8;
    int[] deltas =
    [
        (int)Math.Round((cardX - cursorX) / moveScale),
        (int)Math.Round((cardY - cursorY) / moveScale),
        (int)Math.Round((targetX - cardX) / dragScale),
        (int)Math.Round((targetY - cardY) / dragScale),
    ];
    if (deltas.Any(delta => delta is < -127 or > 127))
        return false;

    planned = ["play", .. deltas.Select(delta => delta.ToString())];
    return true;
}

static async Task<bool> SelectLobbyTarget(GattLocalCharacteristic report, GattSubscribedClient client, LobbyTarget target)
{
    // Previously observed on this iPhone at its current pointer speed.
    // Anchor at the left screen edge before applying the relative offset.
    int anchorY = target == LobbyTarget.First ? -127 : 127;
    for (int step = 0; step < 4; step++)
    {
        if (!await SendReport(report, client, [0, unchecked((byte)-127), unchecked((byte)anchorY)]))
            return false;
        await Task.Delay(20);
    }
    await Task.Delay(180);

    byte[] offset = target switch
    {
        LobbyTarget.Open => [0, 31, unchecked((byte)-10)],
        LobbyTarget.First => [0, 10, 40],
        _ => [0, 16, unchecked((byte)-12)],
    };
    if (!await SendReport(report, client, offset))
        return false;
    await Task.Delay(100);
    return await Click(report, client);
}

static async Task<bool> Click(GattLocalCharacteristic report, GattSubscribedClient client)
{
    bool pressed;
    bool released;
    try
    {
        pressed = await SendReport(report, client, [1, 0, 0]);
    }
    finally
    {
        released = await SendReport(report, client, [0, 0, 0]);
    }
    return pressed && released;
}

static async Task Drag(GattLocalCharacteristic report, GattSubscribedClient client, int x, int y,
    bool flick = false, bool liftFirst = false)
{
    try
    {
        if (!await SendReport(report, client, [1, 0, 0]))
            return;
        await Task.Delay(60);

        // A flick accelerates toward the target and releases on the last report.
        int[] progress = flick ? [0, 8, 22, 42, 68, 100] : [0, 12, 25, 37, 50, 62, 75, 87, 100];
        for (int step = 1; step < progress.Length; step++)
        {
            int lateralStart = liftFirst ? 37 : 0;
            int currentX = x * Math.Max(0, progress[step] - lateralStart) / (100 - lateralStart);
            int previousX = x * Math.Max(0, progress[step - 1] - lateralStart) / (100 - lateralStart);
            var dx = currentX - previousX;
            var dy = y * progress[step] / 100 - y * progress[step - 1] / 100;
            if (!await SendReport(report, client, [1, unchecked((byte)dx), unchecked((byte)dy)]))
                return;
            if (step < progress.Length - 1 || !flick)
                await Task.Delay(flick ? 12 : 24);
        }
        if (!flick)
            await Task.Delay(50);
    }
    finally
    {
        await SendReport(report, client, [0, 0, 0]);
    }
}

static async Task<bool> SendReport(GattLocalCharacteristic report, GattSubscribedClient client, byte[] bytes)
{
    var result = await report.NotifyValueAsync(BufferOf(bytes), client);
    Console.WriteLine($"Input report: {result.Status}");
    return result.Status == GattCommunicationStatus.Success;
}

static async Task<GattLocalCharacteristic> AddCharacteristic(
    GattLocalService service, ushort uuid, GattCharacteristicProperties properties, byte[]? value = null)
{
    var parameters = new GattLocalCharacteristicParameters
    {
        CharacteristicProperties = properties,
        ReadProtectionLevel = GattProtectionLevel.EncryptionRequired,
        WriteProtectionLevel = GattProtectionLevel.EncryptionRequired,
    };
    if (value is not null)
        parameters.StaticValue = BufferOf(value);
    var result = await service.CreateCharacteristicAsync(Uuid(uuid), parameters);
    Check($"Create characteristic {uuid:x4}", result.Error);
    return result.Characteristic;
}
