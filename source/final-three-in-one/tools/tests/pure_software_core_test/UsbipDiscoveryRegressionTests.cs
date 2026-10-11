using System;
using System.IO;
using System.Linq;
using System.Text.Json;
using Y700Switch2V60Viiper;

internal static class UsbipDiscoveryRegressionTests
{
    public static void Run(Action<bool, string> check, string output)
    {
        const string custom = @"D:\Other Apps\USBIP 中文, tools\usbip.exe";
        foreach (string icon in new[] { '"' + custom + "\",0", '"' + custom + "\",-123", custom + ",0", '"' + custom + '"' })
            check(UsbipRuntimeLocator.ParseRegisteredExecutablePath(icon) == custom, "quoted/custom icon path parsed: " + icon);
        check(UsbipRuntimeLocator.ParseRegisteredExecutablePath(@"D:\my.exe folder\usbip.exe,0") ==
            @"D:\my.exe folder\usbip.exe", "extension-like directory does not truncate executable path");
        check(UsbipRuntimeLocator.ParseRegisteredExecutablePath(@"D:\Other Apps\uninstall.exe /S") ==
            @"D:\Other Apps\uninstall.exe", "uninstall arguments excluded and never executed");
        check(UsbipRuntimeLocator.ParseRegisteredExecutablePath("\"D:\\broken.exe") == "", "unterminated registry quote rejected");
        check(UsbipRuntimeLocator.ParseRegisteredExecutablePath("relative\\usbip.exe,0") == "", "relative registry path not executed");
        check(UsbipRuntimeLocator.NormalizeExecutablePath(@"D:\Apps\USBip-0.9.7.7-x64.exe") == "", "installer cannot be selected as usbip runtime");
        check(UsbipRuntimeLocator.NormalizeExecutablePath('"' + custom + '"') == custom, "manual path normalized without damaging comma or Unicode");
        check(UsbipRuntimeLocator.NormalizeExecutablePath(@"D:\Apps\USBIP.EXE") == @"D:\Apps\USBIP.EXE", "runtime basename case-insensitive");
        check(UsbipRuntimeLocator.NormalizeExecutablePath(null) == "", "old settings missing path remain automatic");
        var installed = UsbipRuntimeLocator.GetInstallRecordCandidatePaths(@"D:\Custom Install", null, null);
        check(installed.Contains(@"D:\Custom Install\usbip.exe") && installed.Contains(@"D:\Custom Install\bin\usbip.exe"),
            "nondefault InstallLocation searched including bin");
        var fromIcon = UsbipRuntimeLocator.GetInstallRecordCandidatePaths(null, '"' + custom + "\",0", null);
        check(fromIcon.Contains(custom), "quoted DisplayIcon supplies runtime path");
        var fromUninstaller = UsbipRuntimeLocator.GetInstallRecordCandidatePaths(null, @"D:\Per User Apps\uninstall.exe,0", null);
        check(fromUninstaller.Contains(@"D:\Per User Apps\usbip.exe"), "uninstaller icon supplies sibling runtime");
        var uninstall = UsbipRuntimeLocator.GetInstallRecordCandidatePaths(null, null, @"""D:\User install\uninstall.exe"" /S");
        check(uninstall.Contains(@"D:\User install\bin\usbip.exe"), "uninstall record finds bin without running uninstaller");
        var pathCandidates = UsbipRuntimeLocator.GetPathCandidates("\"D:\\Custom Apps\";relative;;%TEMP%\\USBip;").ToArray();
        check(pathCandidates.Length == 2 && pathCandidates[0] == @"D:\Custom Apps\usbip.exe", "quoted PATH parsed; relative PATH ignored");
        check(pathCandidates[1] == Path.Combine(Path.GetTempPath().TrimEnd(Path.DirectorySeparatorChar), "USBip", "usbip.exe"),
            "environment references expanded for persisted PATH");
        string directory = Path.Combine(Path.GetFullPath(output), "usbip custom 中文, folder");
        Directory.CreateDirectory(directory);
        string fixture = Path.Combine(directory, "usbip.exe");
        // Discovery checks only. Empty fixture is never launched or installed.
        File.WriteAllBytes(fixture, []);
        var selected = UsbipRuntimeLocator.Find(fixture);
        check(selected?.ExePath == fixture && selected.Source == "saved_manual_path", "manual custom path takes priority over local installed runtime");
        check(selected?.DirectoryPath == directory, "runtime working directory follows selected custom path");
        var decoded = JsonSerializer.Deserialize<V60UserSettings>(JsonSerializer.Serialize(new V60UserSettings
            { UsbipExecutablePath = fixture, RuntimeBatteryRefreshEnabled = false, SelectedModeKey = "xbox" }))!;
        check(decoded.UsbipExecutablePath == fixture && decoded.SelectedModeKey == "xbox" && !decoded.RuntimeBatteryRefreshEnabled,
            "saved path roundtrip preserves unrelated settings without touching user file");
        var legacy = JsonSerializer.Deserialize<V60UserSettings>("{\"SelectedModeKey\":\"pro1\"}")!;
        check(legacy.UsbipExecutablePath == "" && legacy.SelectedModeKey == "pro1", "legacy settings backwards compatible");
        check(UsbipRuntimeLocator.Find(Path.Combine(directory, "missing", "usbip.exe"))?.Source != "saved_manual_path",
            "stale configured path falls back rather than reporting nonexistent runtime ready");
        check(UsbipRuntimeLocator.BuildPathWithUsbipDirectory("D:\\Other", selected!) == directory + ";D:\\Other",
            "VIIPER child PATH includes chosen custom runtime directory");
    }
}
