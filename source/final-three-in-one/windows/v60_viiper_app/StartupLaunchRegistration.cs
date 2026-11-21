using System;
using System.Diagnostics;
using Microsoft.Win32;

namespace Y700Switch2V60Viiper;

public static class StartupLaunchRegistration
{
    public const string LoginTrayArgument = "--login-tray";
    private const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "PRO2ControllerAssistant";
    private const string LegacyValueName = "XinHeLianShengV6Viiper";

    public static bool IsEnabled()
    {
        try
        {
            using RegistryKey? key = Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: false);
            string? value = key?.GetValue(ValueName) as string
                ?? key?.GetValue(LegacyValueName) as string;
            return !string.IsNullOrWhiteSpace(value);
        }
        catch
        {
            return false;
        }
    }

    public static void SetEnabled(bool enabled)
    {
        using RegistryKey key = Registry.CurrentUser.CreateSubKey(RunKeyPath, writable: true)
            ?? throw new InvalidOperationException("无法打开当前用户开机启动注册表。请检查 Windows 权限策略。");
        if (!enabled)
        {
            key.DeleteValue(ValueName, throwOnMissingValue: false);
            key.DeleteValue(LegacyValueName, throwOnMissingValue: false);
            return;
        }

        key.SetValue(ValueName, CurrentLoginCommand(), RegistryValueKind.String);
        key.DeleteValue(LegacyValueName, throwOnMissingValue: false);
    }

    // Keep the value name: Task Manager stores its enabled/disabled choice against that name.
    // Only update records which are still present; deleting a Run entry is a user's opt-out.
    public static bool RefreshExisting()
    {
        using RegistryKey? key = Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: true);
        return key != null && RefreshExisting(key, CurrentLoginCommand());
    }

    internal static bool RefreshExisting(RegistryKey key, string command)
    {
        bool updated = false;
        foreach (string name in new[] { ValueName, LegacyValueName })
        {
            if (key.GetValue(name) is string old && !string.IsNullOrWhiteSpace(old) &&
                !string.Equals(old, command, StringComparison.OrdinalIgnoreCase))
            {
                key.SetValue(name, command, RegistryValueKind.String);
                updated = true;
            }
        }
        return updated;
    }

    private static string CurrentLoginCommand()
    {
        string? executable = Environment.GetEnvironmentVariable("PRO2_LAUNCHER_PATH");
        if (string.IsNullOrWhiteSpace(executable) || !System.IO.File.Exists(executable)) executable = Environment.ProcessPath;
        if (string.IsNullOrWhiteSpace(executable))
        {
            executable = Process.GetCurrentProcess().MainModule?.FileName;
        }
        if (string.IsNullOrWhiteSpace(executable))
        {
            throw new InvalidOperationException("无法定位当前 EXE 路径，不能写入开机自启动。");
        }

        return "\"" + executable.Trim().Trim('"') + "\" " + LoginTrayArgument;
    }
}
