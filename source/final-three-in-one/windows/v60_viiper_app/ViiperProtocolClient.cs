using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace Y700Switch2V60Viiper;

public sealed record ViiperDevice(uint BusId, string DevId, string Vid, string Pid, string Type);

public sealed class ViiperProtocolClient
{
    private readonly string host;
    private readonly int port;
    private readonly TimeSpan timeout = TimeSpan.FromSeconds(5);
    private readonly TimeSpan deviceCreateTimeout = TimeSpan.FromSeconds(20);

    public ViiperProtocolClient(string host, int port)
    {
        this.host = string.IsNullOrWhiteSpace(host) ? "127.0.0.1" : host.Trim();
        this.port = port;
    }

    public string Host => host;

    public int Port => port;

    public async Task<string> PingAsync(CancellationToken cancellationToken)
    {
        return await RequestAsync("ping", null, cancellationToken);
    }

    public async Task<IReadOnlyList<uint>> BusListAsync(CancellationToken cancellationToken)
    {
        using JsonDocument doc = JsonDocument.Parse(await RequestAsync("bus/list", null, cancellationToken));
        var list = new List<uint>();
        if (doc.RootElement.TryGetProperty("buses", out JsonElement buses))
        {
            foreach (JsonElement bus in buses.EnumerateArray())
            {
                list.Add(bus.GetUInt32());
            }
        }

        return list;
    }

    public async Task<IReadOnlyList<ViiperDevice>> BusDevicesAsync(
        uint busId,
        CancellationToken cancellationToken)
    {
        using JsonDocument doc = JsonDocument.Parse(
            await RequestAsync("bus/" + busId + "/list", null, cancellationToken));
        var list = new List<ViiperDevice>();
        if (doc.RootElement.TryGetProperty("devices", out JsonElement devices))
        {
            foreach (JsonElement item in devices.EnumerateArray())
            {
                list.Add(new ViiperDevice(
                    item.TryGetProperty("busId", out JsonElement itemBusId)
                        ? itemBusId.GetUInt32()
                        : busId,
                    item.TryGetProperty("devId", out JsonElement devId)
                        ? devId.GetString() ?? ""
                        : "",
                    item.TryGetProperty("vid", out JsonElement vid)
                        ? vid.GetString() ?? ""
                        : "",
                    item.TryGetProperty("pid", out JsonElement pid)
                        ? pid.GetString() ?? ""
                        : "",
                    item.TryGetProperty("type", out JsonElement type)
                        ? type.GetString() ?? ""
                        : ""));
            }
        }

        return list;
    }

    public async Task<uint> BusCreateAsync(CancellationToken cancellationToken)
    {
        using JsonDocument doc = JsonDocument.Parse(await RequestAsync("bus/create", "0", cancellationToken));
        return doc.RootElement.GetProperty("busId").GetUInt32();
    }

    public async Task RemoveBusAsync(uint busId, CancellationToken cancellationToken)
    {
        await RequestAsync("bus/remove", busId.ToString(), cancellationToken);
    }

    public async Task<ViiperDevice> AddDeviceAsync(
        uint busId,
        string deviceType,
        IReadOnlyDictionary<string, object?>? deviceSpecific,
        CancellationToken cancellationToken)
    {
        var request = new Dictionary<string, object?>
        {
            ["type"] = deviceType
        };
        if (deviceSpecific is { Count: > 0 })
        {
            request["deviceSpecific"] = deviceSpecific;
        }

        string payload = JsonSerializer.Serialize(request);
        string response = await RequestAsync(
            "bus/" + busId + "/add",
            payload,
            cancellationToken,
            deviceCreateTimeout);
        using JsonDocument doc = JsonDocument.Parse(response);
        JsonElement root = doc.RootElement;
        return new ViiperDevice(
            root.GetProperty("busId").GetUInt32(),
            root.GetProperty("devId").GetString() ?? "",
            root.TryGetProperty("vid", out JsonElement vid) ? vid.GetString() ?? "" : "",
            root.TryGetProperty("pid", out JsonElement pid) ? pid.GetString() ?? "" : "",
            root.TryGetProperty("type", out JsonElement type) ? type.GetString() ?? deviceType : deviceType);
    }

    public async Task RemoveDeviceAsync(uint busId, string devId, CancellationToken cancellationToken)
    {
        await RequestAsync("bus/" + busId + "/remove", devId, cancellationToken);
    }

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
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            tcp.Dispose();
            throw new TimeoutException(
                "VIIPER API stream timed out: " + host + ":" + port +
                " bus=" + busId + " dev=" + devId);
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
        try
        {
            await tcp.ConnectAsync(host, port, linked.Token);
            await using NetworkStream stream = tcp.GetStream();

            string line = string.IsNullOrWhiteSpace(payload)
                ? path + "\0"
                : path + " " + payload + "\0";
            byte[] request = Encoding.UTF8.GetBytes(line);
            await stream.WriteAsync(request, linked.Token);

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
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new TimeoutException(
                "VIIPER API request timed out: " + host + ":" + port + " path=" + path);
        }
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

public sealed class ViiperDeviceStream : IAsyncDisposable
{
    private readonly TcpClient tcp;
    private readonly NetworkStream stream;

    public ViiperDeviceStream(TcpClient tcp, NetworkStream stream)
    {
        this.tcp = tcp;
        this.stream = stream;
    }

    public async Task WriteAsync(byte[] data, CancellationToken cancellationToken)
    {
        await stream.WriteAsync(data, cancellationToken);
    }

    public async Task<byte[]> ReadExactAsync(int size, CancellationToken cancellationToken)
    {
        byte[] data = new byte[size];
        int offset = 0;
        while (offset < data.Length)
        {
            int read = await stream.ReadAsync(data.AsMemory(offset, data.Length - offset), cancellationToken);
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
