using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace Y700Switch2V60Viiper;

public sealed partial class MainViewModel
{
    private Pro2ManualCandidate? manualReconnectTarget;
    private string? manualReconnectAddress => manualReconnectTarget?.AddressText;
    private System.Collections.Generic.IReadOnlySet<string> PrimaryPreferredAddresses(Pro2ControllerSlot slot) =>
        string.IsNullOrWhiteSpace(manualReconnectAddress) ? PreferredAddressSetForSlot(slot) :
            new System.Collections.Generic.HashSet<string>(StringComparer.OrdinalIgnoreCase) { manualReconnectAddress };
    public async Task<bool> RunManualConnectionSessionAsync(Func<CancellationToken, Task<bool>> showDialog)
    {
        bool success = false;
        await RunExclusiveAsync("手动连接 Pro2", async token =>
        {
            bool resumePrimary = AutoReconnectEnabled;
            bool[] resumeSlots = pro2Slots.Select(slot => slot.AutoReconnectEnabled).ToArray();
            string previousAddress = inputSource.ConnectedAddress;
            await StopAutoReconnectAsync();
            AppendLog("[BLE_MANUAL] session_begin auto_reconnect_paused=true");
            try { success = await showDialog(token); }
            finally
            {
                // Closing a scan must not silently disable the guard on an existing live controller.
                if (!success && !shuttingDown && !lifetimeCts.IsCancellationRequested)
                {
                    if (resumePrimary && inputSource.IsRunning &&
                        string.Equals(previousAddress, inputSource.ConnectedAddress, StringComparison.OrdinalIgnoreCase))
                        StartAutoReconnect();
                    for (int i = 0; i < pro2Slots.Length; i++)
                        if (Running && activeMode.HasValue && IsMultiSlotMode(activeMode.Value) &&
                            resumeSlots[i] && (i != 0 || inputSource.IsRunning &&
                            string.Equals(previousAddress, inputSource.ConnectedAddress, StringComparison.OrdinalIgnoreCase)))
                            await StartPro2SlotAutoReconnectAsync(pro2Slots[i], lifetimeCts.Token);
                }
                AppendLog("[BLE_MANUAL] session_end connected=" + success);
                RaiseConnectionStateChanged();
            }
        });
        return success;
    }

    public async Task<System.Collections.Generic.IReadOnlyList<Pro2ManualCandidate>> ScanManualCandidatesAsync(
        IProgress<Pro2ManualCandidate> progress, CancellationToken token)
    {
        if (previewOnly) return [];
        await bleOperationGate.WaitAsync(token);
        try { return await inputSource.ScanManualAsync(new DirectProgress<string>(AppendLog), progress, token); }
        finally { bleOperationGate.Release(); }
    }

    public async Task<ManualConnectionResult> ConnectManualCandidateAsync(Pro2ManualCandidate candidate, CancellationToken token)
    {
        if (previewOnly) return new(false, "界面预览不连接蓝牙设备。");
        var excluded = ConnectedPro2AddressesExcept(pro2Slots[0]);
        if (!candidate.IsValid || excluded.Contains(candidate.AddressText))
            return new(false, "此地址无效或已被其他手柄通道占用。");
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
        deadline.CancelAfter(TimeSpan.FromSeconds(90));
        Status = "正在连接手动选择的地址：" + candidate.AddressText;
        InputStatus = Status;
        await bleOperationGate.WaitAsync(deadline.Token);
        try
        {
            bool sameLiveSource = inputSource.IsRunning &&
                string.Equals(inputSource.ConnectedAddress, candidate.AddressText, StringComparison.OrdinalIgnoreCase) &&
                inputSource.ConnectedAddressType == candidate.AddressType &&
                inputSource.TryGetLatest(out _, out TimeSpan oldAge) && oldAge.TotalMilliseconds < LostInputTimeoutMilliseconds;
            if (!sameLiveSource) await inputSource.StopAsync();
            await inputSource.StartManualAsync(candidate, excluded, ResolveStickCalibration,
                new DirectProgress<string>(AppendLog), deadline.Token);
        }
        catch (OperationCanceledException) when (!token.IsCancellationRequested && deadline.IsCancellationRequested)
        {
            return new(false, "连接超时。请确认手柄仍在广播，然后重新扫描。");
        }
        finally
        {
            bleOperationGate.Release();
            InputStatus = inputSource.Status;
            pro2Slots[0].RefreshFromSource();
            RaiseConnectionStateChanged();
        }
        token.ThrowIfCancellationRequested();
        if (!inputSource.IsRunning || !inputSource.TryGetLatest(out _, out TimeSpan age) ||
            age.TotalMilliseconds >= LostInputTimeoutMilliseconds ||
            !string.Equals(inputSource.ConnectedAddress, candidate.AddressText, StringComparison.OrdinalIgnoreCase))
            return new(false, "此地址未通过 Pro2 身份或真实 FD2 输入验证。请确认配对广播，或尝试其他候选。");
        RememberConnectedAddress(pro2Slots[0]);
        manualReconnectTarget = candidate;
        // Same output/calibration/rate policy as the main connection button; no second bridge implementation.
        try { await EnterGameAsync(token, allowAutomaticConnect: false); }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            // A virtual-driver/setup failure is not a failed physical Bluetooth connection.
            enterGamePending = false;
            Status = "真实 Pro2 已连接；虚拟输出尚未启动：" + FirstLine(ex.Message);
            AppendLog("[BLE_MANUAL] physical_connected=true virtual_output_error=" + ex);
            StartAutoReconnect();
        }
        RaiseConnectionStateChanged();
        RefreshCompactTelemetry();
        bool live = inputSource.IsRunning && inputSource.TryGetLatest(out _, out age) &&
            age.TotalMilliseconds < LostInputTimeoutMilliseconds;
        return new(live, Status);
    }
}
