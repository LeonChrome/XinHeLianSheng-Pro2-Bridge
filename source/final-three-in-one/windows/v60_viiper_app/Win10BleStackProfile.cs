using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;

namespace Y700Switch2V60Viiper;

/// <summary>Reversible Win10 stack experiments. Audited hash is evidence, not an eligibility whitelist.</summary>
public static class Win10BleStackProfile
{
    public const string ExperimentalArgument = "--ble-turbo-stack-experimental";
    public const string RegistrySubKey = @"SYSTEM\CurrentControlSet\Services\BTHPORT\Parameters";
    public const string AuditedStackSha256 =
        "05A928CA6C072FB324B637A924C7C5F377FD897918D63854C2E646F7D0D62852";
    public static IReadOnlyDictionary<string, int> Values { get; } =
        new ReadOnlyDictionary<string, int>(new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase)
        {
            ["LEConnectionIntervalMin"] = 6,
            ["LEConnectionIntervalMax"] = 6,
            ["LEConnectionLatency"] = 0,
            ["LEConnectionLSTO"] = 200
        });

    public static bool IsAudited(int windowsBuild, string sha256) =>
        windowsBuild >= 19041 && windowsBuild < 22000 &&
        string.Equals(sha256, AuditedStackSha256, StringComparison.OrdinalIgnoreCase);

    public static bool ParseExperimentalConsent(IReadOnlyList<string> args)
    {
        string[] flags = args.Where(argument => argument.StartsWith(ExperimentalArgument,
            StringComparison.OrdinalIgnoreCase)).ToArray();
        if (flags.Length == 0) return false;
        if (flags.Length != 1 || !string.Equals(flags[0], ExperimentalArgument,
            StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("实验确认参数格式无效或重复；未修改任何参数。", nameof(args));
        if (!args.Any(argument => string.Equals(argument, "--ble-turbo-stack-apply",
                StringComparison.OrdinalIgnoreCase)) ||
            args.Any(argument => string.Equals(argument, "--ble-turbo-stack-restore",
                StringComparison.OrdinalIgnoreCase)))
            throw new ArgumentException("实验确认仅能用于 Win10 系统栈应用操作；未修改任何参数。", nameof(args));
        return true;
    }
}
