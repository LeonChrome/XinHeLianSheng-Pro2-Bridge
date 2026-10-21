using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using Y700Switch2V60Viiper;

internal static class LongSessionRegressionTests
{
    private const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;
    internal static async Task RunLogAsync(string directory, Action<bool, string> check)
    {
        var logger = new SessionLogWriter(directory, 256, 1024);
        for (int i = 0; i < 12; i++) logger.Write(new string('x', 300) + "\n");
        logger.Write("[PRO2_BLE_RATE_WINDOW] after_limit fd2_hz=132.9\n");
        logger.Write("[BATTERY_RUNTIME_RATE] after_limit accepted=True\n");
        await logger.DisposeAsync();
        check(File.ReadAllText(logger.FilePath).Contains("[LOG_LIMIT]"), "detail cap actually exercised");
        check(!File.ReadAllText(logger.FilePath).Contains("after_limit"), "original detail file stays capped");
        check(File.ReadAllText(logger.RecentFilePath).Contains("after_limit") &&
            logger.RecentText.Contains("fd2_hz=132.9"), "live evidence survives cap in memory and disk");
        check(new FileInfo(logger.RecentFilePath).Length <= RecentSessionStatus.MaximumBytes &&
            logger.LastError == null, "recent writer bounded and completes without error");
        var buffer = new RecentSessionStatus();
        for (int i = 0; i < 10000; i++) buffer.Add("[PRO2_BLE_RATE_WINDOW] 中文测量序号=" + i + "\n");
        check(Encoding.UTF8.GetByteCount(buffer.Snapshot) <= RecentSessionStatus.MaximumBytes &&
            buffer.Snapshot.Contains("序号=9999"), "UTF8 byte bound preserves newest status");
        buffer.Add(new string('z', 100000));
        check(!buffer.Snapshot.Contains("zzzz"), "per-packet noise excluded from recent status");
    }

    internal static void RunUi(MainWindow window, MainViewModel vm, Action<bool, string> check, Action pump)
    {
        var timer = (DispatcherTimer)typeof(MainWindow).GetField("presentationTimer", Private)!.GetValue(window)!;
        var source = vm.Pro2Slots[0].InputSource;
        void Source(string field, object value) =>
            typeof(Pro2BleInputSource).GetField(field, Private)!.SetValue(source, value);
        void Model(string field, object value) =>
            typeof(MainViewModel).GetField(field, Private)!.SetValue(vm, value);
        typeof(Pro2BleInputSource).GetProperty(nameof(Pro2BleInputSource.IsRunning))!.SetValue(source, true);
        Source("transportConnected", true);
        uint count = 4_788_000;
        foreach (string field in new[] { "rawNotifyCount", "fd2InputNotifyCount", "fd2InputParsedCount", "updates" })
            Source(field, count);
        Source("liveFd2RateHz", 133.0); Source("liveParsedRateHz", 133.0);
        Source("lastParsedNotifyTicks", Stopwatch.GetTimestamp());
        Source("liveRateMeasuredTicks", Stopwatch.GetTimestamp());
        var stateHook = (EventHandler)Delegate.CreateDelegate(typeof(EventHandler), window,
            typeof(MainWindow).GetMethod("MainWindow_StateChanged", Private)!);
        window.StateChanged -= stateHook;
        try
        {
            window.Hide(); window.WindowState = WindowState.Minimized; window.Show(); pump();
            window.WindowState = WindowState.Normal; pump();
            check(!timer.IsEnabled, "negative control reproduces old missing-state-hook refresh freeze");
        }
        finally { window.StateChanged += stateHook; }
        for (int i = 0; i < 5; i++)
        {
            window.WindowState = WindowState.Minimized; window.Hide(); pump();
            check(!timer.IsEnabled, "hidden UI stops presentation timer");
            Model("compactSampleTicks", Stopwatch.GetTimestamp() - 10 * 3600 * Stopwatch.Frequency);
            window.Show(); pump();
            // This offline source has no real producer. Refresh its evidence per
            // restore, rather than letting five OS window transitions age it out.
            Source("lastParsedNotifyTicks", Stopwatch.GetTimestamp());
            Source("liveRateMeasuredTicks", Stopwatch.GetTimestamp());
            window.WindowState = WindowState.Normal;
            check(timer.IsEnabled, "show-before-restore restarts timer");
            check(vm.CompactInputRate == "133.0", "resume uses recent rate, not hidden ten-hour average");
            // Check the synchronous resume before a periodic tick correctly sees
            // zero new frames from this stationary fixture, then process layout.
            pump();
        }
        window.Hide(); pump(); window.WindowState = WindowState.Minimized;
        typeof(MainWindow).GetMethod("ShowFromTray", Private)!.Invoke(window, null); pump();
        check(window.IsVisible && window.WindowState == WindowState.Normal && timer.IsEnabled,
            "actual tray restore restarts sampling");
        foreach (string field in new[] { "compactRaw", "compactFd2", "compactParsed" }) Model(field, count);
        foreach (string field in new[] { "rawNotifyCount", "fd2InputNotifyCount", "fd2InputParsedCount", "updates" })
            Source(field, count + 66);
        Source("lastParsedNotifyTicks", Stopwatch.GetTimestamp());
        Model("compactSampleTicks", Stopwatch.GetTimestamp() - Stopwatch.Frequency);
        var before = source.GetCompactCounters();
        vm.RefreshCompactTelemetry();
        check(double.TryParse(vm.CompactInputRate, out double rate) && rate is > 50 and < 80,
            "resumed UI reflects changed 66Hz counters, never pins 133");
        pump();
        check(source.GetCompactCounters() == before, "UI resume never invents counters");
        var migrated = JsonSerializer.Deserialize<V60UserSettings>(
            "{\"AudioEndpointGuardEnabled\":false,\"RuntimeBatteryRefreshEnabled\":false,\"SelectedModeKey\":\"xbox\"}")!;
        check(migrated.SelectedModeKey == "xbox" && !migrated.RuntimeBatteryRefreshEnabled &&
            !JsonSerializer.Serialize(migrated).Contains("AudioEndpointGuardEnabled"),
            "obsolete audio-off ignored without losing other preferences");
        check(vm.AudioEndpointGuardEnabled &&
            typeof(MainViewModel).GetProperty(nameof(vm.AudioEndpointGuardEnabled))!.SetMethod == null,
            "built-in protection cannot be disabled by old settings");
        check(!Descendants<CheckBox>(window).Any(c => c.Content?.ToString() == "保护电脑默认音频设备") &&
            !Descendants<Button>(window).Any(b => b.Content?.ToString() == "修复默认音频设备"),
            "no redundant visible audio control");
        check(typeof(MainViewModel).GetField("builtInAudioGuardCts", Private)!.GetValue(vm) == null,
            "preview never starts native guard");
        typeof(Pro2BleInputSource).GetProperty(nameof(Pro2BleInputSource.IsRunning))!.SetValue(source, false);
        Source("transportConnected", false);
    }

    private static IEnumerable<T> Descendants<T>(DependencyObject parent) where T : DependencyObject
    {
        for (int i = 0; i < System.Windows.Media.VisualTreeHelper.GetChildrenCount(parent); i++)
        {
            var child = System.Windows.Media.VisualTreeHelper.GetChild(parent, i);
            if (child is T typed) yield return typed;
            foreach (var item in Descendants<T>(child)) yield return item;
        }
    }
}
