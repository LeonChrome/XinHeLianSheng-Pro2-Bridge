using System;
using System.Diagnostics;
using Microsoft.Win32;

namespace Y700Switch2V60Viiper;

public static class StartupLaunchRegistration
{
    private const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "XinHeLianShengV6Viiper";

    public static bool IsEnabled()
    {
        try
        {
            using RegistryKey? key = Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: false);
            string? value = key?.GetValue(ValueName) as string;
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
            return;
        }

        string? executable = Environment.ProcessPath;
        if (string.IsNullOrWhiteSpace(executable))
        {
            executable = Process.GetCurrentProcess().MainModule?.FileName;
        }
        if (string.IsNullOrWhiteSpace(executable))
        {
            throw new InvalidOperationException("无法定位当前 EXE 路径，不能写入开机自启动。");
        }

        key.SetValue(ValueName, Quote(executable), RegistryValueKind.String);
    }

    private static string Quote(string path)
    {
        string normalized = path.Trim().Trim('"');
        return "\"" + normalized + "\"";
    }
}