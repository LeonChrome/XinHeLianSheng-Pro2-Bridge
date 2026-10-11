using System;
using System.Reflection;
using System.Threading.Tasks;
using Y700Switch2V60Viiper;

internal static class EnvironmentDiagnosticsRegressionTests
{
    public static SystemBluetoothSnapshot Fixture()
    {
        var intel = new BluetoothAdapterDiagnosticInfo("Intel(R) Wireless Bluetooth(R)",
            @"USB\VID_8087&PID_0026\TEST", "8087", "0026", "Intel Corporation", "22.250.0.2",
            "2023-09-08", "intel-interface");
        var realtek = new BluetoothAdapterDiagnosticInfo("Realtek Bluetooth Adapter",
            @"USB\VID_0BDA&PID_0852\TEST", "0BDA", "0852", "Realtek", "1.9.1051.3008", "unknown", "realtek-interface");
        return new(new DateTimeOffset(2026, 10, 9, 22, 41, 24, TimeSpan.FromHours(8)),
            new("Windows 11", "Professional", "25H2", "10.0.26200.1234", "X64", "X64", "1.3.12", "10.0.26200.1234"),
            [new(intel, true, "A8:59:5F:19:7E:53", true, true, true, true),
             new(realtek, false, "11:22:33:44:55:66", true, true, false, true)],
            "route=Windows11PublicApi build=26200 request_api=True", "");
    }

    public static async Task RunAsync(Action<bool, string> check)
    {
        foreach (int build in new[] { 19041, 19045, 21999 })
            check(SystemBluetoothDiagnostics.WindowsFamily(build) == "Windows 10", "Windows 10 build family " + build);
        foreach (int build in new[] { 22000, 22631, 26100, 26200 })
            check(SystemBluetoothDiagnostics.WindowsFamily(build) == "Windows 11", "Windows 11 build family " + build);
        check(SystemBluetoothDiagnostics.Value(null) == "未获取" &&
            SystemBluetoothDiagnostics.Value("unknown") == "未获取", "missing data not invented");
        check(SystemBluetoothDiagnostics.Value("a\r\nb\t") == "a  b", "metadata cannot inject new log lines");
        string report = SystemBluetoothDiagnostics.Format(Fixture());
        foreach (string value in new[] { "Windows 11", "25H2", "26200.1234", "8087", "0026", "0BDA", "0852",
                     "22.250.0.2", "1.9.1051.3008", "2023-09-08", "PNP ID", "WinRT ID", "系统默认", "不能" })
            check(report.Contains(value), "report includes " + value);
        check(!report.Contains("unknown") && report.Contains("驱动日期：未获取"), "unavailable driver dates explicit");
        var lines = SystemBluetoothDiagnostics.LogLines(Fixture());
        check(lines.Count == 4 && lines[0].Contains("[SYSTEM_INFO]") && lines[1].Contains("adapter_count=2"),
            "startup metadata tags and multi adapter count");
        check(lines[2].Contains("22.250.0.2") && lines[3].Contains("1.9.1051.3008") &&
            lines[2].Contains("system_default=True") && lines[3].Contains("system_default=False"), "drivers remain adapter scoped");
        var empty = Fixture() with { Adapters = [], Warning = "蓝牙信息读取超时" };
        check(SystemBluetoothDiagnostics.Format(empty).Contains("没有获取到") &&
            SystemBluetoothDiagnostics.Format(empty).Contains("超时"), "empty/partial metadata explained");

        // Exact missing PNP instance must not pick another installed adapter's driver.
        object details = typeof(MainViewModel).Assembly.GetType("Y700Switch2V60Viiper.Win10BleDiagnostic")!
            .GetMethod("ReadRegistryAdapter", BindingFlags.Static | BindingFlags.NonPublic)!
            .Invoke(null, [@"USB\VID_FFFF&PID_FFFF\PRO2_ENVIRONMENT_NONEXISTENT_TEST"] )!;
        check(details.GetType().GetProperty("DriverVersion")!.GetValue(details)?.ToString() == "", "unknown adapter cannot inherit another driver's validated tuple");

        var source = new Pro2BleInputSource();
        var flags = BindingFlags.Instance | BindingFlags.NonPublic;
        void Field(string name, object value) => typeof(Pro2BleInputSource).GetField(name, flags)!.SetValue(source, value);
        string offline = source.GetLinkDiagnosticText();
        check(offline.Contains("BLE 物理连接：未连接") && offline.Contains("尚未通过"), "offline diagnostics honest");
        Field("transportConnected", true); Field("connectionPhase", Pro2ConnectionPhase.Measuring);
        Field("connectionIntervalUnits", (ushort)12); Field("connectionLatency", (ushort)0);
        Field("connectionLinkTimeout", (ushort)600); Field("connectionPreferenceStatus", "custom_7p5_Success");
        Field("fd2ReportRateStatus", "write_success"); Field("fd2ReportRateDescriptorHandle", (ushort)12);
        check(source.GetLinkDiagnosticText().Contains("15.00 ms") && source.GetLinkDiagnosticText().Contains("6000 ms") &&
            source.GetLinkDiagnosticText().Contains("正在测量") && source.GetLinkDiagnosticText().Contains("write_success"),
            "cached interval, timeout and pending measurement displayed independently from request success");
        var initial = source.GetCompactCounters();
        for (int i = 0; i < 50; i++) source.GetLinkDiagnosticText();
        check(source.GetCompactCounters() == initial && !source.IsRunning && source.GetCompactCounters().Parsed == 0,
            "repeated diagnostic display never connects, polls, parses or generates input");
        await source.DisposeAsync();
    }
}
