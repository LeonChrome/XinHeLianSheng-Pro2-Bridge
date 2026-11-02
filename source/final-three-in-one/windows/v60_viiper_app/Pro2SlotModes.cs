namespace Y700Switch2V60Viiper;

public sealed record Pro2SlotModeOption(ViiperVirtualMode Mode, string Key, string Label);

public static class Pro2SlotModes
{
    public static IReadOnlyList<Pro2SlotModeOption> Options { get; } =
    [
        new(ViiperVirtualMode.Pro2, "pro2", "Steam / Pro2"),
        new(ViiperVirtualMode.Pro1, "pro1", "Pro1"),
        new(ViiperVirtualMode.Xbox, "xbox", "Xbox"),
        new(ViiperVirtualMode.DualSenseLike, "dualsense", "PS5")
    ];
    public static Pro2SlotModeOption FromKey(string? key) => Options.FirstOrDefault(o =>
        o.Key == V60UserSettings.NormalizeModeKey(key)) ?? Options[0];
    public static Pro2SlotModeOption FromMode(ViiperVirtualMode mode) => Options.FirstOrDefault(o => o.Mode == mode) ?? Options[0];
    public static string[] Normalize(string[]? saved, string primaryKey)
    {
        string fallback = FromKey(primaryKey).Key;
        return Enumerable.Range(0, 4).Select(i => saved != null && i < saved.Length && !string.IsNullOrWhiteSpace(saved[i])
            ? FromKey(saved[i]).Key : fallback).ToArray();
    }
    public static ViiperDeviceProfile Profile(ViiperVirtualMode mode, int slot, ViiperPushRateOption cadence)
    {
        if (slot is < 1 or > 4) throw new ArgumentOutOfRangeException(nameof(slot));
        var profile = mode switch
        {
            ViiperVirtualMode.Pro2 => ViiperDeviceProfile.Pro2,
            ViiperVirtualMode.Pro1 => ViiperDeviceProfile.Pro1,
            ViiperVirtualMode.Xbox => ViiperDeviceProfile.Xbox,
            ViiperVirtualMode.DualSenseLike => ViiperDeviceProfile.DualSenseLike,
            _ => throw new ArgumentOutOfRangeException(nameof(mode))
        };
        return profile with { Label = profile.Label + " Slot " + slot,
            DeviceSpecificSerialNumber = ViiperDeviceProfile.SlotSerialNumber(mode, slot),
            SendInterval = cadence.Interval, SourcePaced = cadence.SourcePaced };
    }
}

public enum Pro2SlotModeChangeResult { Changed, RestoredPrevious, OutputStopped }
public static class Pro2SlotModeSwitch
{
    // The caller supplies only the chosen slot's virtual lifecycle. Physical BLE and
    // sibling sessions are deliberately outside this transaction.
    public static async Task<Pro2SlotModeChangeResult> ReplaceAsync(ViiperVirtualMode? previous,
        ViiperVirtualMode requested, Func<Task> stopSlot, Func<ViiperVirtualMode, CancellationToken, Task<bool>> startSlot,
        CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        await stopSlot();
        try
        {
            if (await startSlot(requested, token)) return Pro2SlotModeChangeResult.Changed;
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
        catch { /* Restore a known previous mode after a failed create. */ }
        token.ThrowIfCancellationRequested();
        if (previous.HasValue && await startSlot(previous.Value, token)) return Pro2SlotModeChangeResult.RestoredPrevious;
        return Pro2SlotModeChangeResult.OutputStopped;
    }
}
