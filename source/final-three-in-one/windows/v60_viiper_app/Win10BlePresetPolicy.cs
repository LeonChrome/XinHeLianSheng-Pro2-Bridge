using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;

namespace Y700Switch2V60Viiper;

public enum Win10BlePreset { Fixed7p5, Flexible7p5To15, Fixed15 }

public sealed record Win10BlePresetDefinition(Win10BlePreset Preset, string Id, string Title,
    int IntervalMinUnits, int IntervalMaxUnits, int Latency, int TimeoutUnits)
{
    public IReadOnlyDictionary<string, int> Values => new ReadOnlyDictionary<string, int>(
        new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase)
        {
            ["LEConnectionIntervalMin"] = IntervalMinUnits,
            ["LEConnectionIntervalMax"] = IntervalMaxUnits,
            ["LEConnectionLatency"] = Latency,
            ["LEConnectionLSTO"] = TimeoutUnits
        });
    public double MinimumIntervalMs => IntervalMinUnits * 1.25;
    public double MaximumIntervalMs => IntervalMaxUnits * 1.25;
}

/// <summary>Audited-stack experiment choices, not rate guarantees. No I/O or elevation.</summary>
public static class Win10BlePresetPolicy
{
    public const string SelectorPrefix = "--ble-turbo-stack-preset=";
    private const string SelectorName = "--ble-turbo-stack-preset";
    public static IReadOnlyList<Win10BlePresetDefinition> Presets { get; } =
        Array.AsReadOnly(new[]
        {
            new Win10BlePresetDefinition(Win10BlePreset.Fixed7p5, "7p5", "固定 7.5ms", 6, 6, 0, 200),
            new Win10BlePresetDefinition(Win10BlePreset.Flexible7p5To15, "flexible", "7.5–15ms", 6, 12, 0, 200),
            new Win10BlePresetDefinition(Win10BlePreset.Fixed15, "15", "固定 15ms", 12, 12, 0, 200)
        });
    public static Win10BlePresetDefinition Default => Presets[0];
    public static Win10BlePresetDefinition Get(Win10BlePreset preset) =>
        Presets.FirstOrDefault(item => item.Preset == preset) ??
        throw new ArgumentOutOfRangeException(nameof(preset));
    public static Win10BlePresetDefinition Get(string id) =>
        Presets.FirstOrDefault(item => string.Equals(item.Id, id, StringComparison.OrdinalIgnoreCase)) ??
        throw new ArgumentException("未知 Win10 BLE 预设；允许 7p5、flexible、15，未修改任何参数。", nameof(id));

    public static Win10BlePresetDefinition ParseCommand(IReadOnlyList<string> args)
    {
        string[] selectors = args.Where(argument => argument.StartsWith(SelectorName,
            StringComparison.OrdinalIgnoreCase)).ToArray();
        if (selectors.Length > 1)
            throw new ArgumentException("同一操作只能指定一个 Win10 BLE 预设；未修改任何参数。", nameof(args));
        if (selectors.Length == 0) return Default;
        if (!selectors[0].StartsWith(SelectorPrefix, StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("Win10 BLE 预设格式应为 --ble-turbo-stack-preset=7p5|flexible|15；未修改任何参数。", nameof(args));
        return Get(selectors[0][SelectorPrefix.Length..]);
    }
}
