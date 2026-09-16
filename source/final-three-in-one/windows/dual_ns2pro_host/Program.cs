using System.Buffers.Binary;
using System.ComponentModel;
using System.Diagnostics;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using Microsoft.Win32.SafeHandles;

namespace DualNs2ProHost;

internal static class Program
{
    private const int Ns2ProInputSize = 24;
    internal const int Ns2ProFeedbackSize = 34;
    private const ushort StickCenter = 0x0800;

    public static async Task<int> Main(string[] args)
    {
        HostOptions options;
        try
        {
            options = HostOptions.Parse(args);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine(ex.Message);
            HostOptions.PrintUsage();
            return 2;
        }

        using var lifetime = new CancellationTokenSource();
        Console.CancelKeyPress += (_, e) =>
        {
            e.Cancel = true;
            lifetime.Cancel();
        };
        if (options.Duration > TimeSpan.Zero)
        {
            lifetime.CancelAfter(options.Duration);
        }
        StartStopFileWatcher(options.StopFile, lifetime);

        ViiperServerProcess? ownedServer = null;
        var client = new ViiperProtocolClient("127.0.0.1", options.ApiPort);
        uint busId = 0;
        var slots = new List<DualNs2ProSlot>();

        try
        {
            UsbipRuntime usbip = UsbipRuntime.Find()
                ?? throw new InvalidOperationException("usbip.exe was not found. Install usbip-win2 first.");
            UsbipProbeResult probe = await usbip.ProbeAsync(lifetime.Token);
            if (!probe.Ready)
            {
                throw new InvalidOperationException("usbip-win2 driver is not ready: " + probe.Detail);
            }
            Console.WriteLine("[USBIP] " + usbip.ExePath);
            Console.WriteLine("[USBIP] " + probe.Detail);

            if (!await client.TryPingAsync(TimeSpan.FromMilliseconds(600), lifetime.Token))
            {
                string viiperExe = options.ViiperExe ?? ViiperRuntime.FindExe()
                    ?? throw new InvalidOperationException("viiper-haptic.exe was not found.");
                ownedServer = await ViiperServerProcess.StartAsync(
                    viiperExe,
                    usbip,
                    options.ApiPort,
                    options.UsbPort,
                    options.LogRoot,
                    lifetime.Token);
            }

            Console.WriteLine("[PING] " + await client.PingAsync(lifetime.Token));

            busId = await client.BusCreateAsync(lifetime.Token);
            Console.WriteLine("[BUS] created " + busId);

            slots.Add(await DualNs2ProSlot.CreateAsync(
                "A",
                options.SerialA,
                busId,
                client,
                options,
                lifetime.Token));
            slots.Add(await DualNs2ProSlot.CreateAsync(
                "B",
                options.SerialB,
                busId,
                client,
                options,
                lifetime.Token));

            Console.WriteLine("[READY] two ns2pro devices are alive. Press Ctrl+C to stop.");
            Console.WriteLine("[READY] target identity: VID_057E PID_2069, unique serials: " +
                              options.SerialA + ", " + options.SerialB);

            await Task.Delay(Timeout.InfiniteTimeSpan, lifetime.Token);
            return 0;
        }
        catch (OperationCanceledException)
        {
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine("[ERROR] " + ex);
            return 1;
        }
        finally
        {
            using var cleanup = new CancellationTokenSource(TimeSpan.FromSeconds(4));
            foreach (DualNs2ProSlot slot in slots)
            {
                await slot.DisposeAsync(cleanup.Token);
            }

            if (busId != 0)
            {
                try
                {
                    await client.RemoveBusAsync(busId, cleanup.Token);
                    Console.WriteLine("[BUS] removed " + busId);
                }
                catch (Exception ex)
                {
                    Console.WriteLine("[BUS] cleanup warning: " + ex.Message);
                }
            }

            if (ownedServer != null)
            {
                await ownedServer.DisposeAsync();
            }
            TryDelete(options.StopFile);
        }
    }

    private static void StartStopFileWatcher(string? stopFile, CancellationTokenSource lifetime)
    {
        if (string.IsNullOrWhiteSpace(stopFile))
        {
            return;
        }

        TryDelete(stopFile);
        _ = Task.Run(async () =>
        {
            try
            {
                while (!lifetime.IsCancellationRequested)
                {
                    if (File.Exists(stopFile))
                    {
                        Console.WriteLine("[STOP] stop-file detected: " + stopFile);
                        lifetime.Cancel();
                        return;
                    }
                    await Task.Delay(250, lifetime.Token);
                }
            }
            catch (OperationCanceledException)
            {
            }
        });
    }

    private static void TryDelete(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return;
        }
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch
        {
        }
    }

    internal static byte[] MakeNeutralPacket()
    {
        byte[] b = new byte[Ns2ProInputSize];
        WriteU16(b, 4, StickCenter);
        WriteU16(b, 6, StickCenter);
        WriteU16(b, 8, StickCenter);
        WriteU16(b, 10, StickCenter);
        return b;
    }

    internal static byte[] MakeSyntheticPacket(string slot, ulong frame)
    {
        byte[] b = MakeNeutralPacket();
        double phase = slot == "A" ? 0 : Math.PI;
        double angle = frame * 0.052 + phase;
        ushort lx = StickAxis(Math.Cos(angle), 1250);
        ushort ly = StickAxis(Math.Sin(angle), 1250);
        ushort rx = StickAxis(Math.Sin(angle * 0.5), 800);
        ushort ry = StickAxis(Math.Cos(angle * 0.5), 800);
        WriteU16(b, 4, lx);
        WriteU16(b, 6, ly);
        WriteU16(b, 8, rx);
        WriteU16(b, 10, ry);
        WriteI16(b, 12, (short)(600 * Math.Sin(angle * 0.5)));
        WriteI16(b, 14, (short)(600 * Math.Cos(angle * 0.5)));
        WriteI16(b, 16, -4096);
        WriteI16(b, 18, (short)(450 * Math.Sin(angle)));
        WriteI16(b, 20, (short)(450 * Math.Cos(angle)));
        WriteI16(b, 22, (short)(240 * Math.Sin(angle * 0.33)));
        if ((frame / 90) % 4 == 0)
        {
            WriteU32(b, 0, slot == "A" ? 0x00000002u : 0x00000001u);
        }
        return b;
    }

    private static ushort StickAxis(double value, double radius)
    {
        int raw = (int)Math.Round(StickCenter + radius * value);
        return (ushort)Math.Clamp(raw, 0, 0x0FFF);
    }

    private static void WriteU16(byte[] b, int offset, ushort value) =>
        BinaryPrimitives.WriteUInt16LittleEndian(b.AsSpan(offset, 2), value);

    private static void WriteI16(byte[] b, int offset, short value) =>
        BinaryPrimitives.WriteInt16LittleEndian(b.AsSpan(offset, 2), value);

    private static void WriteU32(byte[] b, int offset, uint value) =>
        BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(offset, 4), value);
}

internal sealed class DualNs2ProSlot
{
    private readonly string name;
    private readonly ViiperProtocolClient client;
    private readonly ViiperDevice device;
    private readonly ViiperDeviceStream stream;
    private readonly HostOptions options;
    private readonly CancellationTokenSource cts;
    private readonly Task writerTask;
    private readonly Task feedbackTask;

    private DualNs2ProSlot(
        string name,
        ViiperProtocolClient client,
        ViiperDevice device,
        ViiperDeviceStream stream,
        HostOptions options,
        CancellationTokenSource cts)
    {
        this.name = name;
        this.client = client;
        this.device = device;
        this.stream = stream;
        this.options = options;
        this.cts = cts;
        writerTask = Task.Run(WriterLoopAsync);
        feedbackTask = Task.Run(FeedbackLoopAsync);
    }

    public static async Task<DualNs2ProSlot> CreateAsync(
        string name,
        string serial,
        uint busId,
        ViiperProtocolClient client,
        HostOptions options,
        CancellationToken cancellationToken)
    {
        ViiperDevice device = await client.AddNs2ProDeviceAsync(busId, serial, cancellationToken);
        Console.WriteLine("[NS2PRO_" + name + "] added bus=" + device.BusId +
                          " dev=" + device.DevId +
                          " vid=" + device.Vid +
                          " pid=" + device.Pid +
                          " serial=" + serial);
        ViiperDeviceStream stream = await client.OpenStreamAsync(device.BusId, device.DevId, cancellationToken);
        Console.WriteLine("[NS2PRO_" + name + "] stream connected");
        var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        return new DualNs2ProSlot(name, client, device, stream, options, linked);
    }

    public async Task DisposeAsync(CancellationToken cancellationToken)
    {
        cts.Cancel();
        await stream.DisposeAsync();
        await DrainAsync(writerTask);
        await DrainAsync(feedbackTask);
        try
        {
            await client.RemoveDeviceAsync(device.BusId, device.DevId, cancellationToken);
            Console.WriteLine("[NS2PRO_" + name + "] removed dev=" + device.DevId);
        }
        catch (Exception ex)
        {
            Console.WriteLine("[NS2PRO_" + name + "] cleanup warning: " + ex.Message);
        }

        cts.Dispose();
    }

    private async Task WriterLoopAsync()
    {
        byte[] neutral = Program.MakeNeutralPacket();
        using var timerResolution = WindowsTimerResolutionScope.Begin();
        using var timer = new HighResolutionPeriodicTimer(TimeSpan.FromMilliseconds(1000.0 / options.Hz));
        var rateWatch = Stopwatch.StartNew();
        ulong frames = 0;
        ulong lastFrames = 0;
        long lastTicks = 0;
        Console.WriteLine("[NS2PRO_" + name + "] timer backend=" + timer.Backend +
                          " timer_resolution_active=" + timerResolution.IsActive +
                          " target_hz=" + options.Hz.ToString("F1"));
        try
        {
            while (timer.WaitForNextTick(cts.Token))
            {
                byte[] packet = options.Synthetic
                    ? Program.MakeSyntheticPacket(name, frames)
                    : neutral;
                await stream.WriteAsync(packet, cts.Token);
                frames++;
                if (frames % 500 == 0)
                {
                    long now = rateWatch.ElapsedTicks;
                    double seconds = lastTicks == 0
                        ? rateWatch.Elapsed.TotalSeconds
                        : (now - lastTicks) / (double)Stopwatch.Frequency;
                    double hz = seconds > 0 ? (frames - lastFrames) / seconds : 0;
                    lastTicks = now;
                    lastFrames = frames;
                    Console.WriteLine("[NS2PRO_" + name + "] pushed frames=" + frames +
                                      " hz=" + hz.ToString("F1") +
                                      " mode=" + (options.Synthetic ? "synthetic" : "neutral"));
                }
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (ObjectDisposedException)
        {
        }
        catch (Exception ex)
        {
            Console.WriteLine("[NS2PRO_" + name + "] writer failed: " + ex.Message);
            cts.Cancel();
        }
    }

    private async Task FeedbackLoopAsync()
    {
        ulong frames = 0;
        byte lastFlags = 0xFF;
        byte lastLed = 0xFF;
        try
        {
            while (!cts.IsCancellationRequested)
            {
                byte[] feedback = await stream.ReadExactAsync(Program.Ns2ProFeedbackSize, cts.Token);
                frames++;
                byte flags = feedback[32];
                byte led = feedback[33];
                if (frames <= 4 || frames % 100 == 0 || flags != lastFlags || led != lastLed)
                {
                    lastFlags = flags;
                    lastLed = led;
                    Console.WriteLine("[NS2PRO_" + name + "] feedback frames=" + frames +
                                      " flags=0x" + flags.ToString("X2") +
                                      " led=0x" + led.ToString("X2"));
                }
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (ObjectDisposedException)
        {
        }
        catch (EndOfStreamException ex)
        {
            Console.WriteLine("[NS2PRO_" + name + "] feedback stream closed: " + ex.Message);
            cts.Cancel();
        }
        catch (Exception ex)
        {
            Console.WriteLine("[NS2PRO_" + name + "] feedback failed: " + ex.Message);
            cts.Cancel();
        }
    }

    private static async Task DrainAsync(Task task)
    {
        try
        {
            await task;
        }
        catch
        {
        }
    }
}

internal sealed record HostOptions(
    int ApiPort,
    int UsbPort,
    double Hz,
    bool Synthetic,
    TimeSpan Duration,
    string SerialA,
    string SerialB,
    string? ViiperExe,
    string LogRoot,
    string? StopFile)
{
    public static HostOptions Parse(string[] args)
    {
        int api = 3342;
        int usb = 3341;
        double hz = 125;
        bool synthetic = false;
        TimeSpan duration = TimeSpan.Zero;
        string serialA = "Y700-NS2PRO-A1";
        string serialB = "Y700-NS2PRO-B2";
        string? viiperExe = null;
        string logRoot = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "PRO2WirelessReceiverControlBoard",
            "dual_ns2pro_logs");
        string? stopFile = null;

        for (int i = 0; i < args.Length; i++)
        {
            string arg = args[i];
            string Next()
            {
                if (++i >= args.Length)
                {
                    throw new ArgumentException("Missing value for " + arg);
                }
                return args[i];
            }

            switch (arg)
            {
                case "--api":
                case "--api-port":
                    api = int.Parse(Next());
                    break;
                case "--usb":
                case "--usb-port":
                    usb = int.Parse(Next());
                    break;
                case "--hz":
                    hz = double.Parse(Next());
                    break;
                case "--duration":
                case "--seconds":
                    duration = TimeSpan.FromSeconds(double.Parse(Next()));
                    break;
                case "--synthetic":
                    synthetic = true;
                    break;
                case "--neutral":
                    synthetic = false;
                    break;
                case "--serial-a":
                    serialA = Next();
                    break;
                case "--serial-b":
                    serialB = Next();
                    break;
                case "--viiper":
                    viiperExe = Next();
                    break;
                case "--log-root":
                    logRoot = Next();
                    break;
                case "--stop-file":
                    stopFile = Next();
                    break;
                case "--help":
                case "-h":
                    PrintUsage();
                    Environment.Exit(0);
                    break;
                default:
                    throw new ArgumentException("Unknown argument: " + arg);
            }
        }

        if (api <= 0 || api > 65535 || usb <= 0 || usb > 65535)
        {
            throw new ArgumentOutOfRangeException(nameof(args), "Ports must be 1..65535.");
        }
        if (api == usb)
        {
            throw new ArgumentException("API and USB ports must be different.");
        }
        if (hz < 30 || hz > 500)
        {
            throw new ArgumentOutOfRangeException(nameof(args), "Hz must be 30..500.");
        }
        if (string.Equals(serialA, serialB, StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException("Serials must be different.");
        }

        return new HostOptions(api, usb, hz, synthetic, duration, serialA, serialB, viiperExe, logRoot, stopFile);
    }

    public static void PrintUsage()
    {
        Console.WriteLine("DualNs2ProHost [--api-port 3342] [--usb-port 3341] [--hz 125] [--synthetic] [--seconds 30] [--stop-file path]");
        Console.WriteLine("Creates two independent VIIPER ns2pro virtual devices: VID_057E PID_2069.");
    }
}

internal sealed record ViiperDevice(uint BusId, string DevId, string Vid, string Pid, string Type);

internal sealed class ViiperProtocolClient
{
    private readonly string host;
    private readonly int port;
    private readonly TimeSpan timeout = TimeSpan.FromSeconds(5);

    public ViiperProtocolClient(string host, int port)
    {
        this.host = host;
        this.port = port;
    }

    public async Task<bool> TryPingAsync(TimeSpan pingTimeout, CancellationToken cancellationToken)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        linked.CancelAfter(pingTimeout);
        try
        {
            string response = await PingAsync(linked.Token);
            return response.Contains("VIIPER", StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }

    public Task<string> PingAsync(CancellationToken cancellationToken) =>
        RequestAsync("ping", null, cancellationToken);

    public async Task<uint> BusCreateAsync(CancellationToken cancellationToken)
    {
        using JsonDocument doc = JsonDocument.Parse(await RequestAsync("bus/create", "0", cancellationToken));
        return doc.RootElement.GetProperty("busId").GetUInt32();
    }

    public Task RemoveBusAsync(uint busId, CancellationToken cancellationToken) =>
        RequestAsync("bus/remove", busId.ToString(), cancellationToken);

    public async Task<ViiperDevice> AddNs2ProDeviceAsync(
        uint busId,
        string serial,
        CancellationToken cancellationToken)
    {
        string payload = JsonSerializer.Serialize(new Dictionary<string, object?>
        {
            ["type"] = "ns2pro",
            ["deviceSpecific"] = new Dictionary<string, object?>
            {
                ["serial_number"] = serial,
                ["battery_level"] = 9,
                ["charging"] = false,
                ["external_power"] = true,
                ["battery_volts"] = 3800
            }
        });
        string response = await RequestAsync(
            "bus/" + busId + "/add",
            payload,
            cancellationToken,
            TimeSpan.FromSeconds(20));
        using JsonDocument doc = JsonDocument.Parse(response);
        JsonElement root = doc.RootElement;
        return new ViiperDevice(
            root.GetProperty("busId").GetUInt32(),
            root.GetProperty("devId").GetString() ?? "",
            root.TryGetProperty("vid", out JsonElement vid) ? vid.GetString() ?? "" : "",
            root.TryGetProperty("pid", out JsonElement pid) ? pid.GetString() ?? "" : "",
            root.TryGetProperty("type", out JsonElement type) ? type.GetString() ?? "ns2pro" : "ns2pro");
    }

    public Task RemoveDeviceAsync(uint busId, string devId, CancellationToken cancellationToken) =>
        RequestAsync("bus/" + busId + "/remove", devId, cancellationToken);

    public async Task<ViiperDeviceStream> OpenStreamAsync(
        uint busId,
        string devId,
        CancellationToken cancellationToken)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        linked.CancelAfter(timeout);

        var tcp = CreateTcpClient();
        try
        {
            await tcp.ConnectAsync(host, port, linked.Token);
            NetworkStream stream = tcp.GetStream();
            byte[] request = Encoding.UTF8.GetBytes("bus/" + busId + "/" + devId + "\0");
            await stream.WriteAsync(request, linked.Token);
            return new ViiperDeviceStream(tcp, stream);
        }
        catch
        {
            tcp.Dispose();
            throw;
        }
    }

    private async Task<string> RequestAsync(
        string path,
        string? payload,
        CancellationToken cancellationToken,
        TimeSpan? requestTimeout = null)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        linked.CancelAfter(requestTimeout ?? timeout);

        using var tcp = CreateTcpClient();
        await tcp.ConnectAsync(host, port, linked.Token);
        await using NetworkStream stream = tcp.GetStream();
        string line = string.IsNullOrWhiteSpace(payload) ? path + "\0" : path + " " + payload + "\0";
        await stream.WriteAsync(Encoding.UTF8.GetBytes(line), linked.Token);

        using var memory = new MemoryStream();
        byte[] buffer = new byte[4096];
        while (true)
        {
            int read = await stream.ReadAsync(buffer, linked.Token);
            if (read == 0)
            {
                break;
            }
            memory.Write(buffer, 0, read);
        }

        string response = Encoding.UTF8.GetString(memory.ToArray()).TrimEnd('\0', '\r', '\n', ' ');
        ThrowIfProblem(response);
        return response;
    }

    private static void ThrowIfProblem(string response)
    {
        if (string.IsNullOrWhiteSpace(response))
        {
            return;
        }
        try
        {
            using JsonDocument doc = JsonDocument.Parse(response);
            JsonElement root = doc.RootElement;
            if (root.TryGetProperty("status", out JsonElement status) &&
                status.ValueKind == JsonValueKind.Number)
            {
                string title = root.TryGetProperty("title", out JsonElement titleElement)
                    ? titleElement.GetString() ?? "VIIPER API error"
                    : "VIIPER API error";
                string detail = root.TryGetProperty("detail", out JsonElement detailElement)
                    ? detailElement.GetString() ?? ""
                    : "";
                throw new InvalidOperationException(status.GetInt32() + " " + title + ": " + detail);
            }
        }
        catch (JsonException)
        {
        }
    }

    private static TcpClient CreateTcpClient()
    {
        var tcp = new TcpClient
        {
            NoDelay = true,
            SendBufferSize = 64 * 1024,
            ReceiveBufferSize = 64 * 1024
        };
        tcp.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.KeepAlive, true);
        return tcp;
    }
}

internal sealed class ViiperDeviceStream : IAsyncDisposable
{
    private readonly TcpClient tcp;
    private readonly NetworkStream stream;

    public ViiperDeviceStream(TcpClient tcp, NetworkStream stream)
    {
        this.tcp = tcp;
        this.stream = stream;
    }

    public Task WriteAsync(byte[] data, CancellationToken cancellationToken) =>
        stream.WriteAsync(data, cancellationToken).AsTask();

    public async Task<byte[]> ReadExactAsync(int size, CancellationToken cancellationToken)
    {
        byte[] data = new byte[size];
        int offset = 0;
        while (offset < data.Length)
        {
            int read = await stream.ReadAsync(data.AsMemory(offset), cancellationToken);
            if (read == 0)
            {
                throw new EndOfStreamException("VIIPER device stream closed.");
            }
            offset += read;
        }
        return data;
    }

    public ValueTask DisposeAsync()
    {
        stream.Dispose();
        tcp.Dispose();
        return ValueTask.CompletedTask;
    }
}

internal sealed record UsbipProbeResult(bool Ready, string Detail);

internal sealed record UsbipRuntime(string ExePath, string DirectoryPath)
{
    public static UsbipRuntime? Find()
    {
        foreach (string candidate in CandidatePaths().Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (File.Exists(candidate))
            {
                return new UsbipRuntime(candidate, Path.GetDirectoryName(candidate) ?? "");
            }
        }
        return null;
    }

    public async Task<UsbipProbeResult> ProbeAsync(CancellationToken cancellationToken)
    {
        try
        {
            using Process? process = Process.Start(new ProcessStartInfo
            {
                FileName = ExePath,
                Arguments = "port",
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                WorkingDirectory = DirectoryPath
            });
            if (process == null)
            {
                return new UsbipProbeResult(false, "process_start returned null");
            }

            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(5));
            await process.WaitForExitAsync(timeout.Token);
            string text = ((await process.StandardOutput.ReadToEndAsync()) + " " +
                           (await process.StandardError.ReadToEndAsync())).Trim();
            return process.ExitCode == 0
                ? new UsbipProbeResult(true, string.IsNullOrWhiteSpace(text) ? "usbip port ok" : text)
                : new UsbipProbeResult(false, "exit=" + process.ExitCode + " " + text);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return new UsbipProbeResult(false, ex.Message);
        }
    }

    public string BuildPath(string currentPath)
    {
        if (string.IsNullOrWhiteSpace(DirectoryPath))
        {
            return currentPath;
        }
        return DirectoryPath + Path.PathSeparator + (currentPath ?? "");
    }

    private static IEnumerable<string> CandidatePaths()
    {
        string path = Environment.GetEnvironmentVariable("PATH") ?? "";
        foreach (string dir in path.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            yield return Path.Combine(dir, "usbip.exe");
        }

        string? cursor = AppContext.BaseDirectory;
        for (int i = 0; i < 8 && !string.IsNullOrWhiteSpace(cursor); i++)
        {
            yield return Path.Combine(cursor, "tools", "usbip-win2", "v0.9.7.7", "usbip.exe");
            yield return Path.Combine(cursor, "tools", "usbip-win2", "usbip.exe");
            yield return Path.Combine(cursor, "usbip-win2", "usbip.exe");
            cursor = Directory.GetParent(cursor)?.FullName;
        }

        string programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        if (!string.IsNullOrWhiteSpace(programFiles))
        {
            yield return Path.Combine(programFiles, "USBip", "usbip.exe");
            yield return Path.Combine(programFiles, "USBIP", "usbip.exe");
            yield return Path.Combine(programFiles, "usbip-win2", "usbip.exe");
        }
    }
}

internal static class ViiperRuntime
{
    public static string? FindExe()
    {
        foreach (string candidate in CandidatePaths().Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (File.Exists(candidate))
            {
                return candidate;
            }
        }
        return null;
    }

    private static IEnumerable<string> CandidatePaths()
    {
        string? cursor = AppContext.BaseDirectory;
        for (int i = 0; i < 8 && !string.IsNullOrWhiteSpace(cursor); i++)
        {
            yield return Path.Combine(cursor, "tools", "viiper", "haptic-v0.8.0", "viiper-haptic.exe");
            yield return Path.Combine(cursor, "tools", "viiper", "v0.7.0", "viiper.exe");
            yield return Path.Combine(cursor, "viiper-haptic.exe");
            cursor = Directory.GetParent(cursor)?.FullName;
        }
        yield return Path.Combine("tools", "viiper", "haptic-v0.8.0", "viiper-haptic.exe");
        yield return Path.Combine("tools", "viiper", "v0.7.0", "viiper.exe");
    }
}

internal sealed class ViiperServerProcess : IAsyncDisposable
{
    private readonly Process process;

    private ViiperServerProcess(Process process)
    {
        this.process = process;
    }

    public static async Task<ViiperServerProcess> StartAsync(
        string exe,
        UsbipRuntime usbip,
        int apiPort,
        int usbPort,
        string logRoot,
        CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(logRoot);
        string stamp = DateTime.Now.ToString("yyyyMMdd_HHmmss");
        string logPath = Path.Combine(logRoot, "dual_ns2pro_viiper_" + stamp + ".log");
        string args = "server --api.addr=127.0.0.1:" + apiPort +
                      " --usb.addr=127.0.0.1:" + usbPort +
                      " --api.device-handler-connect-timeout=60s" +
                      " --usb.write-batch-flush-interval=0ms" +
                      " --update-notify=none" +
                      " --log.level=debug" +
                      " --log.file=\"" + logPath + "\"";
        var psi = new ProcessStartInfo
        {
            FileName = exe,
            Arguments = args,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            WorkingDirectory = Path.GetDirectoryName(exe) ?? AppContext.BaseDirectory,
            WindowStyle = ProcessWindowStyle.Hidden
        };
        psi.Environment["PATH"] = usbip.BuildPath(
            psi.Environment.TryGetValue("PATH", out string? path)
                ? path ?? ""
                : Environment.GetEnvironmentVariable("PATH") ?? "");

        Process? process = Process.Start(psi);
        if (process == null)
        {
            throw new InvalidOperationException("Failed to start VIIPER.");
        }

        _ = Task.Run(async () =>
        {
            try
            {
                string output = await process.StandardOutput.ReadToEndAsync();
                if (!string.IsNullOrWhiteSpace(output))
                {
                    Console.WriteLine("[VIIPER_STDOUT] " + output.Trim());
                }
            }
            catch
            {
            }
        });
        _ = Task.Run(async () =>
        {
            try
            {
                string output = await process.StandardError.ReadToEndAsync();
                if (!string.IsNullOrWhiteSpace(output))
                {
                    Console.WriteLine("[VIIPER_STDERR] " + output.Trim());
                }
            }
            catch
            {
            }
        });

        Console.WriteLine("[VIIPER] start pid=" + process.Id + " exe=" + exe);
        Console.WriteLine("[VIIPER] args=" + args);
        Console.WriteLine("[VIIPER] log=" + logPath);

        var client = new ViiperProtocolClient("127.0.0.1", apiPort);
        for (int attempt = 1; attempt <= 24; attempt++)
        {
            await Task.Delay(250, cancellationToken);
            if (process.HasExited)
            {
                throw new InvalidOperationException("VIIPER exited early, code=" + process.ExitCode +
                                                    ". log=" + logPath);
            }
            if (await client.TryPingAsync(TimeSpan.FromMilliseconds(700), cancellationToken))
            {
                return new ViiperServerProcess(process);
            }
        }

        throw new TimeoutException("VIIPER API did not become ready. log=" + logPath);
    }

    public async ValueTask DisposeAsync()
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
                await process.WaitForExitAsync();
            }
        }
        catch
        {
        }
        finally
        {
            process.Dispose();
        }
    }
}

internal sealed class HighResolutionPeriodicTimer : IDisposable
{
    private const uint CreateWaitableTimerHighResolution = 0x00000002;
    private const uint TimerAllAccess = 0x001F0003;
    private const uint WaitObject0 = 0x00000000;
    private const uint WaitFailed = 0xFFFFFFFF;
    private readonly SafeWaitHandle? timer;
    private readonly Stopwatch scheduleWatch = Stopwatch.StartNew();
    private readonly long intervalStopwatchTicks;
    private long fallbackNextTicks;
    private bool disposed;

    public HighResolutionPeriodicTimer(TimeSpan period)
    {
        if (period <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(period));
        }

        intervalStopwatchTicks = ToStopwatchTicks(period);
        fallbackNextTicks = intervalStopwatchTicks;

        if (!OperatingSystem.IsWindows())
        {
            Backend = "stopwatch_fallback";
            return;
        }

        timer = CreateWaitableTimerExW(
            IntPtr.Zero,
            null,
            CreateWaitableTimerHighResolution,
            TimerAllAccess);
        if (timer.IsInvalid)
        {
            timer.Dispose();
            timer = CreateWaitableTimerExW(IntPtr.Zero, null, 0, TimerAllAccess);
        }

        if (timer.IsInvalid)
        {
            timer.Dispose();
            timer = null;
            Backend = "stopwatch_fallback";
            return;
        }

        ArmNativeTimer();
        Backend = "high_resolution_waitable_timer_absolute";
    }

    public string Backend { get; }

    public bool WaitForNextTick(CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        cancellationToken.ThrowIfCancellationRequested();

        if (timer != null)
        {
            uint result = WaitForSingleObject(timer, 1000);
            cancellationToken.ThrowIfCancellationRequested();
            if (result == WaitObject0)
            {
                AdvanceDeadline();
                ArmNativeTimer();
                return true;
            }

            if (result == WaitFailed)
            {
                throw new Win32Exception(
                    Marshal.GetLastWin32Error(),
                    "High-resolution timer wait failed.");
            }

            throw new TimeoutException("High-resolution timer did not signal within one second.");
        }

        WaitWithStopwatch(cancellationToken);
        return true;
    }

    private void WaitWithStopwatch(CancellationToken cancellationToken)
    {
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            long remaining = fallbackNextTicks - scheduleWatch.ElapsedTicks;
            if (remaining <= 0)
            {
                AdvanceDeadline();
                return;
            }

            double remainingMilliseconds = remaining * 1000.0 / Stopwatch.Frequency;
            if (remainingMilliseconds > 2)
            {
                Thread.Sleep(1);
            }
            else
            {
                Thread.SpinWait(80);
            }
        }
    }

    private void AdvanceDeadline()
    {
        fallbackNextTicks += intervalStopwatchTicks;
        long now = scheduleWatch.ElapsedTicks;
        if (fallbackNextTicks <= now)
        {
            long missed = ((now - fallbackNextTicks) / intervalStopwatchTicks) + 1;
            fallbackNextTicks += missed * intervalStopwatchTicks;
        }
    }

    private void ArmNativeTimer()
    {
        if (timer == null)
        {
            return;
        }

        long remainingStopwatchTicks = Math.Max(
            1,
            fallbackNextTicks - scheduleWatch.ElapsedTicks);
        long remainingTimeSpanTicks = Math.Max(
            1,
            checked((long)Math.Round(
                remainingStopwatchTicks *
                (double)TimeSpan.TicksPerSecond /
                Stopwatch.Frequency)));
        long dueTime = -remainingTimeSpanTicks;
        if (!SetWaitableTimerEx(
                timer,
                ref dueTime,
                0,
                IntPtr.Zero,
                IntPtr.Zero,
                IntPtr.Zero,
                0))
        {
            throw new Win32Exception(
                Marshal.GetLastWin32Error(),
                "Unable to arm the high-resolution waitable timer.");
        }
    }

    private static long ToStopwatchTicks(TimeSpan value)
    {
        return Math.Max(
            1,
            checked((long)Math.Round(value.TotalSeconds * Stopwatch.Frequency)));
    }

    public void Dispose()
    {
        if (disposed)
        {
            return;
        }

        disposed = true;
        timer?.Dispose();
    }

    [DllImport("kernel32.dll", EntryPoint = "CreateWaitableTimerExW", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern SafeWaitHandle CreateWaitableTimerExW(
        IntPtr timerAttributes,
        string? timerName,
        uint flags,
        uint desiredAccess);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetWaitableTimerEx(
        SafeWaitHandle timer,
        ref long dueTime,
        int periodMilliseconds,
        IntPtr completionRoutine,
        IntPtr completionRoutineArgument,
        IntPtr wakeContext,
        uint tolerableDelay);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern uint WaitForSingleObject(SafeWaitHandle handle, uint milliseconds);
}

internal sealed class WindowsTimerResolutionScope : IDisposable
{
    private const uint TimePeriodMilliseconds = 1;
    private const uint TimerrNoError = 0;
    private bool disposed;

    private WindowsTimerResolutionScope(bool active, uint result)
    {
        IsActive = active;
        Result = result;
    }

    public bool IsActive { get; }
    public uint Result { get; }

    public static WindowsTimerResolutionScope Begin()
    {
        if (!OperatingSystem.IsWindows())
        {
            return new WindowsTimerResolutionScope(active: false, result: uint.MaxValue);
        }

        uint result = timeBeginPeriod(TimePeriodMilliseconds);
        return new WindowsTimerResolutionScope(result == TimerrNoError, result);
    }

    public void Dispose()
    {
        if (disposed)
        {
            return;
        }

        disposed = true;
        if (IsActive)
        {
            _ = timeEndPeriod(TimePeriodMilliseconds);
        }
    }

    [DllImport("winmm.dll", ExactSpelling = true)]
    private static extern uint timeBeginPeriod(uint periodMilliseconds);

    [DllImport("winmm.dll", ExactSpelling = true)]
    private static extern uint timeEndPeriod(uint periodMilliseconds);
}
