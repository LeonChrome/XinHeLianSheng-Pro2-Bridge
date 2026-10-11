using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using Windows.Devices.Bluetooth;
using Y700Switch2V60Viiper;

internal static class ManualConnectionRegressionTests
{
    public static async Task RunAsync(Action<bool, string> check)
    {
        foreach (double scale in new[] { 1.0, 1.25, 1.5, 2.0, 2.5 })
        foreach (var size in new[] { (1920, 1040), (1366, 728), (1280, 680), (800, 560), (400, 300) })
        foreach (double left in new[] { 0.0, -1920.0, 1920.0 })
        {
            var area = new Rect(left / scale, 0, size.Item1 / scale, size.Item2 / scale);
            var bounds = StartupWindowLayout.Fit(area);
            check(area.Contains(bounds), "startup bounds inside monitor work area, scale=" + scale);
            check(bounds.Width <= 1040 && bounds.Height <= 820 && bounds.Width > 0 && bounds.Height > 0,
                "startup size preferred but bounded");
        }
        bool badArea = false;
        try { StartupWindowLayout.Fit(Rect.Empty); } catch (ArgumentOutOfRangeException) { badArea = true; }
        check(badArea, "empty work area rejected");

        var high = new Pro2ManualCandidate(0x38C6CE27FC2D, BluetoothAddressType.Public, "Pro2 Controller", -80, true, 90);
        var middle = new Pro2ManualCandidate(0x38C6CE27FC2E, BluetoothAddressType.Public, "", -42, true, 30);
        var unknown = new Pro2ManualCandidate(0xDEADBEEF1201, BluetoothAddressType.Random, "Nearby beacon", -20, false, 0);
        var randomSameAddress = high with { AddressType = BluetoothAddressType.Random };
        var invalid = high with { Address = 0 };
        var sorted = Pro2ManualCandidate.Sort([unknown, invalid, middle, high]);
        check(sorted.SequenceEqual(new[] { high, middle, unknown }), "identity evidence ranks above stronger RSSI; invalid omitted");
        check(high.AddressText == "38:C6:CE:27:FC:2D" && randomSameAddress.AddressDetail.EndsWith("随机地址"), "MAC and address type retained");
        check(high.Confidence == "高匹配" && middle.Confidence == "可能是 Pro2" && unknown.Confidence == "低匹配", "heuristics do not claim verified identity or percentages");
        check((high with { Name = "\nPro2\r\t" }).DisplayName == "Pro2", "control characters stripped from UI names");
        check(!(high with { Address = 0x1000000000000 }).IsValid &&
            !(high with { AddressType = (BluetoothAddressType)9 }).IsValid, "out-of-range addresses/types rejected");

        int connectCalls = 0, successes = 0;
        using (var model = new ManualConnectionViewModel((_, _) => Task.FromResult<IReadOnlyList<Pro2ManualCandidate>>(
            new[] { unknown, middle, high, randomSameAddress }), (candidate, _) =>
            { connectCalls++; return Task.FromResult(new ManualConnectionResult(true, "FD2 已确认")); }, CancellationToken.None))
        {
            await model.RefreshAsync();
            check(!model.IsScanning && model.Candidates.Count == 4 && model.Candidates[0].Score == 90,
                "scan populates sorted candidates, public/random addresses remain distinct");
            model.ConnectionSucceeded += () => successes++;
            await model.ConnectAsync(invalid);
            check(connectCalls == 0 && successes == 0, "invalid click never reaches transport");
            await model.ConnectAsync(high);
            check(connectCalls == 1 && successes == 1 && !model.IsConnecting, "only confirmed live result fires success");
            await model.StopAsync();
        }
        bool scanReleased = false;
        var transportEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var finish = new TaskCompletionSource<ManualConnectionResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        Pro2ManualCandidate? chosen = null;
        connectCalls = 0; successes = 0;
        using (var model = new ManualConnectionViewModel(async (_, token) =>
        {
            try { await Task.Delay(Timeout.Infinite, token); return Array.Empty<Pro2ManualCandidate>(); }
            finally { scanReleased = true; }
        }, async (candidate, token) =>
        {
            chosen = candidate; connectCalls++; check(scanReleased, "watcher stops before selected GATT connection");
            transportEntered.TrySetResult(); return await finish.Task.WaitAsync(token);
        }, CancellationToken.None))
        {
            model.ConnectionSucceeded += () => successes++;
            var scanning = model.RefreshAsync();
            check(model.IsScanning, "scan in progress observable");
            var connecting = model.ConnectAsync(randomSameAddress);
            await transportEntered.Task.WaitAsync(TimeSpan.FromSeconds(3));
            await model.ConnectAsync(unknown);
            await model.RefreshAsync();
            check(connectCalls == 1 && chosen == randomSameAddress && model.IsConnecting && model.Status.Contains("等待"),
                "repeat clicks/refresh show wait, never replace or overlap chosen target");
            finish.TrySetResult(new(false, "身份不匹配"));
            await Task.WhenAll(scanning, connecting);
            check(successes == 0 && !model.IsConnecting && model.Status == "身份不匹配", "failed attempt stays in manual page");
            await model.StopAsync();
        }
        using (var model = new ManualConnectionViewModel((_, _) => Task.FromResult<IReadOnlyList<Pro2ManualCandidate>>([]),
            async (_, token) => { await Task.Delay(Timeout.Infinite, token); return new(true, "not reached"); }, CancellationToken.None))
        {
            model.ConnectionSucceeded += () => successes++;
            var connecting = model.ConnectAsync(high);
            await model.StopAsync().WaitAsync(TimeSpan.FromSeconds(3));
            await connecting;
            check(!model.IsConnecting && successes == 0, "close cancels and awaits operation cleanup without success");
        }
        using (var model = new ManualConnectionViewModel((_, _) => throw new InvalidOperationException("radio off\nmore"),
            (_, _) => throw new InvalidOperationException("connection failed"), CancellationToken.None))
        {
            await model.RefreshAsync();
            check(!model.IsScanning && model.Status == "扫描失败：radio off", "scan failure remains recoverable");
            await model.ConnectAsync(high);
            check(!model.IsConnecting && model.Status.Contains("连接失败"), "connect exception stays in page");
            await model.StopAsync();
        }
        var source = new Pro2BleInputSource();
        bool excluded = false;
        try { await source.StartManualAsync(high, new HashSet<string> { high.AddressText }, null,
            new DirectProgress<string>(_ => { }), CancellationToken.None); }
        catch (InvalidOperationException) { excluded = true; }
        check(excluded, "occupied target rejected before opening radio or stopping current connection");
        using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
        bool cancellation = false;
        try { await source.StartManualAsync(high, new HashSet<string>(), null,
            new DirectProgress<string>(_ => { }), cancelled.Token); }
        catch (OperationCanceledException) { cancellation = true; }
        check(cancellation, "pre-cancelled manual start performs no Bluetooth operation");
        const System.Reflection.BindingFlags flags = System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic;
        typeof(Pro2BleInputSource).GetField("connectedAddress", flags)!.SetValue(source, high.AddressText);
        typeof(Pro2BleInputSource).GetField("connectedAddressType", flags)!.SetValue(source, BluetoothAddressType.Public);
        typeof(Pro2BleInputSource).GetProperty(nameof(Pro2BleInputSource.IsRunning))!.SetValue(source, true);
        bool wrongType = false;
        try { await source.StartManualAsync(randomSameAddress, new HashSet<string>(), null,
            new DirectProgress<string>(_ => { }), CancellationToken.None); }
        catch (InvalidOperationException) { wrongType = true; }
        check(wrongType, "same MAC with different public/random type never reuses a running connection");
        typeof(Pro2BleInputSource).GetProperty(nameof(Pro2BleInputSource.IsRunning))!.SetValue(source, false);
        await source.DisposeAsync();
    }
}
