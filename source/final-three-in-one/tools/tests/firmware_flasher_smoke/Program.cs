using Y700Switch2V55Manager;
using System.Diagnostics;

if (args.Length == 2 &&
    string.Equals(args[0], "--driver-query-test", StringComparison.Ordinal))
{
    PortDriverInfo? driver = DeviceInspector.QueryPortDriver(args[1]);
    if (driver == null)
    {
        Console.Error.WriteLine("driver metadata was not found for " + args[1]);
        return 10;
    }

    Console.WriteLine(driver.Summary);
    Console.WriteLine("CH343 driver query test passed");
    return 0;
}

if (args.Length == 1 &&
    string.Equals(args[0], "--driver-risk-test", StringComparison.Ordinal))
{
    if (!DeviceInspector.IsKnownKernelHangRisk(
            26300, "wch.cn", "2.1.2025.7") ||
        DeviceInspector.IsKnownKernelHangRisk(
            26100, "wch.cn", "2.1.2025.7") ||
        DeviceInspector.IsKnownKernelHangRisk(
            26300, "Microsoft", "10.0.26100.8155"))
    {
        Console.Error.WriteLine("CH343 driver risk rules are incorrect");
        return 9;
    }

    Console.WriteLine("CH343 driver risk tests passed");
    return 0;
}

if (args.Length == 1 &&
    string.Equals(args[0], "--erase-args-test", StringComparison.Ordinal))
{
    List<string> eraseArgs = FirmwareFlasher.CommonArgs(
        "COM_TEST", 115200, false, "erase_flash");
    if (eraseArgs.Contains("--no-stub"))
    {
        Console.Error.WriteLine("erase_flash must upload the ESP32-S3 stub");
        return 6;
    }
    if (!eraseArgs.Contains("erase_flash") ||
        !eraseArgs.Contains("--connect-attempts"))
    {
        Console.Error.WriteLine("erase_flash stable arguments are incomplete");
        return 7;
    }

    List<string> probeArgs = FirmwareFlasher.CommonArgs(
        "COM_TEST", 115200, true, "chip_id");
    if (!probeArgs.Contains("--no-stub"))
    {
        Console.Error.WriteLine("chip_id probe must remain ROM-only");
        return 8;
    }

    Console.WriteLine("firmware erase argument test passed");
    return 0;
}

if (args.Length == 1 &&
    string.Equals(args[0], "--download-mode-error-test", StringComparison.Ordinal))
{
    const string esptoolOutput =
        "A fatal error occurred: Failed to connect to ESP32-S3: " +
        "Wrong boot mode detected (0x24)! The chip needs to be in download mode.";
    if (!FirmwareFlasher.IsDownloadModeFailure(esptoolOutput))
    {
        Console.Error.WriteLine("download mode failure signature was not detected");
        return 11;
    }

    Console.WriteLine("download mode error detection test passed");
    return 0;
}

if (args.Length == 2 &&
    string.Equals(args[0], "--watchdog-test", StringComparison.Ordinal))
{
    string fixturePath = Path.GetFullPath(args[1]);
    var progress = new Progress<string>(Console.WriteLine);
    var command = new[]
    {
        "--chip", "esp32s3",
        "-p", "COM_TEST",
        "--connect-attempts", "5",
        "chip_id"
    };
    var stopwatch = Stopwatch.StartNew();
    try
    {
        await FirmwareFlasher.RunEsptoolAsync(
            fixturePath,
            command,
            progress,
            CancellationToken.None,
            TimeSpan.FromMilliseconds(900));
        Console.Error.WriteLine("watchdog test did not time out");
        return 3;
    }
    catch (TimeoutException ex)
    {
        Console.WriteLine("watchdog timeout observed: " + ex.Message);
    }

    await Task.Delay(500);
    string expectedPath = Path.GetFullPath(fixturePath);
    int[] remaining = Process.GetProcessesByName("esptool")
        .Where(process => MatchesExecutable(process, expectedPath))
        .Select(process => process.Id)
        .ToArray();
    if (remaining.Length > 0)
    {
        Console.Error.WriteLine(
            "watchdog left fixture processes: " + string.Join(",", remaining));
        return 4;
    }
    if (stopwatch.Elapsed > TimeSpan.FromSeconds(6))
    {
        Console.Error.WriteLine(
            "watchdog response was too slow: " + stopwatch.Elapsed);
        return 5;
    }

    Console.WriteLine(
        "firmware flasher watchdog test passed in " +
        stopwatch.Elapsed.TotalSeconds.ToString("F2") + "s");
    return 0;
}

if (args.Length == 2 &&
    string.Equals(args[0], "--erase", StringComparison.Ordinal))
{
    var eraseProgress = new Progress<string>(Console.WriteLine);
    var eraseFlasher = new FirmwareFlasher();
    using var eraseTimeout =
        new CancellationTokenSource(TimeSpan.FromMinutes(2));
    await eraseFlasher.EraseFlashAsync(
        args[1],
        eraseProgress,
        eraseTimeout.Token);
    Console.WriteLine("firmware erase smoke passed");
    return 0;
}

if (args.Length != 2)
{
    Console.Error.WriteLine(
        "Usage: firmware_flasher_smoke <COM port> <profile id>\n" +
        "   or: firmware_flasher_smoke --erase <COM port>\n" +
        "   or: firmware_flasher_smoke --erase-args-test\n" +
        "   or: firmware_flasher_smoke --download-mode-error-test\n" +
        "   or: firmware_flasher_smoke --driver-risk-test\n" +
        "   or: firmware_flasher_smoke --driver-query-test <COM port>\n" +
        "   or: firmware_flasher_smoke --watchdog-test <fixture exe>");
    return 2;
}

var flashProgress = new Progress<string>(Console.WriteLine);
var flasher = new FirmwareFlasher();
using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(150));
await flasher.FlashAsync(
    args[0],
    args[1],
    FlashMode.Upgrade,
    flashProgress,
    timeout.Token);
Console.WriteLine("firmware flasher smoke passed");
return 0;

static bool MatchesExecutable(Process process, string expectedPath)
{
    try
    {
        return string.Equals(
            Path.GetFullPath(process.MainModule?.FileName ?? ""),
            expectedPath,
            StringComparison.OrdinalIgnoreCase);
    }
    catch
    {
        return false;
    }
}
