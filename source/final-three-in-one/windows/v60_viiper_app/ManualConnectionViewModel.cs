using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace Y700Switch2V60Viiper;

// Injected transport delegates keep UI state/races testable without opening a radio.
public sealed class ManualConnectionViewModel : RearEditorObservable, IDisposable
{
    private readonly Func<IProgress<Pro2ManualCandidate>, CancellationToken, Task<IReadOnlyList<Pro2ManualCandidate>>> scan;
    private readonly Func<Pro2ManualCandidate, CancellationToken, Task<ManualConnectionResult>> connect;
    private readonly CancellationTokenSource lifetime;
    private CancellationTokenSource? scanCancellation;
    private Task scanTask = Task.CompletedTask, connectionTask = Task.CompletedTask;
    private int scanGeneration;
    private bool stopping, scanning, connecting;
    private string status = "准备扫描附近的 BLE 广播…";
    public ObservableCollection<Pro2ManualCandidate> Candidates { get; } = [];
    public bool IsScanning { get => scanning; private set { scanning = value; Changed(); Changed(nameof(ActivityText)); } }
    public bool IsConnecting { get => connecting; private set { connecting = value; Changed(); Changed(nameof(ActivityText)); } }
    public string ActivityText => IsConnecting ? "正在连接…" : IsScanning ? "扫描中…" : Candidates.Count + " 个地址";
    public string Status { get => status; private set { status = value; Changed(); } }
    public event Action? ConnectionSucceeded;
    public ManualConnectionViewModel(
        Func<IProgress<Pro2ManualCandidate>, CancellationToken, Task<IReadOnlyList<Pro2ManualCandidate>>> scan,
        Func<Pro2ManualCandidate, CancellationToken, Task<ManualConnectionResult>> connect, CancellationToken token)
    { this.scan = scan; this.connect = connect; lifetime = CancellationTokenSource.CreateLinkedTokenSource(token); }

    public Task RefreshAsync()
    {
        if (stopping) return Task.CompletedTask;
        if (IsConnecting || IsScanning) { ShowWait(); return Task.CompletedTask; }
        Candidates.Clear(); Changed(nameof(ActivityText));
        scanCancellation?.Dispose();
        scanCancellation = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
        IsScanning = true;
        return scanTask = ScanCoreAsync(++scanGeneration, scanCancellation.Token);
    }
    private async Task ScanCoreAsync(int generation, CancellationToken token)
    {
        Status = "正在扫描；找到地址后可直接点击，不必等扫描结束。";
        var progress = new Progress<Pro2ManualCandidate>(candidate =>
        {
            if (!stopping && IsScanning && !IsConnecting && generation == scanGeneration) Upsert(candidate);
        });
        try
        {
            var result = await scan(progress, token);
            if (stopping || IsConnecting || token.IsCancellationRequested) return;
            foreach (var candidate in result) Upsert(candidate);
            Status = Candidates.Count == 0 ? "未发现 BLE 广播。请唤醒 Pro2 并开启配对广播，再点重新扫描。" :
                "点击地址尝试连接。匹配度仅根据广播标识与名称估计，并非身份认证。";
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        { if (!IsConnecting && !stopping) Status = "扫描已取消。"; }
        catch (Exception ex)
        { if (!stopping && !IsConnecting) Status = "扫描失败：" + FirstLine(ex.Message); }
        finally { IsScanning = false; }
    }
    private void Upsert(Pro2ManualCandidate candidate)
    {
        if (!candidate.IsValid) return;
        var previous = Candidates.FirstOrDefault(c => c.Address == candidate.Address && c.AddressType == candidate.AddressType);
        if (previous != null)
        {
            candidate = candidate with { Score = Math.Max(previous.Score, candidate.Score),
                NintendoManufacturer = previous.NintendoManufacturer || candidate.NintendoManufacturer,
                Name = string.IsNullOrWhiteSpace(candidate.Name) ? previous.Name : candidate.Name };
            if (candidate == previous) return;
            Candidates.Remove(previous);
        }
        // Bound crowded-environment UI memory; never evict a stronger Pro2 match for an unknown beacon.
        int index = Pro2ManualCandidate.Sort(Candidates.Append(candidate)).ToList().IndexOf(candidate);
        if (index >= 256) return;
        Candidates.Insert(index, candidate);
        if (Candidates.Count > 256) Candidates.RemoveAt(256);
        Changed(nameof(ActivityText));
    }
    public Task ConnectAsync(Pro2ManualCandidate candidate)
    {
        if (stopping) return Task.CompletedTask;
        if (IsConnecting) { ShowWait(); return Task.CompletedTask; }
        if (!candidate.IsValid) { Status = "地址无效，请重新扫描。"; return Task.CompletedTask; }
        IsConnecting = true;
        Status = "正在连接 " + candidate.AddressText + "，请等待…";
        scanCancellation?.Cancel();
        return connectionTask = ConnectCoreAsync(candidate);
    }
    private async Task ConnectCoreAsync(Pro2ManualCandidate candidate)
    {
        try
        {
            // A watcher must release ownership before GATT initialization begins.
            await scanTask;
            lifetime.Token.ThrowIfCancellationRequested();
            var result = await connect(candidate, lifetime.Token);
            if (stopping || lifetime.IsCancellationRequested) return;
            Status = result.Message;
            if (result.Connected) ConnectionSucceeded?.Invoke();
        }
        catch (OperationCanceledException) when (lifetime.IsCancellationRequested)
        { Status = "已取消，正在释放本次连接…"; }
        catch (Exception ex) { if (!stopping) Status = "连接失败：" + FirstLine(ex.Message); }
        finally { IsConnecting = false; }
    }
    private void ShowWait() => Status = IsConnecting ? "正在连接，请等待当前连接完成。" : "正在扫描，请等待，或直接点击已发现的地址。";
    public async Task StopAsync()
    {
        stopping = true;
        Status = "正在取消并释放本次操作，请等待…";
        lifetime.Cancel(); scanCancellation?.Cancel();
        await Task.WhenAll(scanTask, connectionTask);
    }
    public void Dispose() { scanCancellation?.Dispose(); lifetime.Dispose(); }
    private static string FirstLine(string message) => message.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries).FirstOrDefault() ?? "未知错误";
}
