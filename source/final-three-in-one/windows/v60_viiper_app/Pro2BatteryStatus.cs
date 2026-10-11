using System;

namespace Y700Switch2V60Viiper;

// Pro2 report 0x09 / UUID 7492866c, not the FD2 report's counter byte.
// Protocol reference: ndeadly/switch2_controller_research/hid_reports.md.
public readonly record struct Pro2BatteryStatus(byte RawPower)
{
    public byte Level => (byte)((RawPower >> 2) & 0x0f);
    public bool HasLevel => Level <= 9;
    public bool ExternalPower => (RawPower & 1) != 0;
    public bool Charging => (RawPower & 2) != 0;
    // An approximate scale conversion for consumers that require a percentage.
    // The UI shows the real level instead of pretending this is exact SOC.
    public byte ApproximatePercent => HasLevel
        ? (byte)((Level * 100 + 4) / 9) : GamepadState.BatteryUnknown;

    public static bool TryDecodePrimary(ReadOnlySpan<byte> payload, out Pro2BatteryStatus status)
    {
        status = default;
        if (payload.Length < 11) return false;
        status = new Pro2BatteryStatus(payload[1]);
        return true;
    }

    public void ApplyTo(GamepadState state)
    {
        state.BatteryLevel = HasLevel ? Level : GamepadState.BatteryUnknown;
        state.BatteryPercent = ApproximatePercent;
        state.BatteryPercentApproximate = HasLevel;
        state.BatteryPowerKnown = true;
        state.BatteryExternalPower = ExternalPower;
        state.BatteryCharging = Charging;
    }

    public string Telemetry => $"raw_power=0x{RawPower:X2} level=" +
        (HasLevel ? Level + "/9" : "unknown") +
        " percent_approx=" + (HasLevel ? ApproximatePercent.ToString() : "unknown") +
        " external_power=" + ExternalPower.ToString().ToLowerInvariant() +
        " charging=" + Charging.ToString().ToLowerInvariant();

    public static string Display(GamepadState state, bool fresh, bool levelAtConnection = false)
    {
        if (!fresh) return "电量未知";
        string charge = state.BatteryPowerKnown
            ? state.BatteryCharging ? " · 充电中" : state.BatteryExternalPower ? " · 外接电源" : ""
            : "";
        if (state.BatteryLevel <= 9) return state.BatteryLevel + "/9 格" +
            (levelAtConnection ? "（连接时）" : "") + charge;
        if (state.BatteryPercent <= 100)
            return (state.BatteryPercentApproximate ? "约 " : "") + state.BatteryPercent + "%" + charge;
        return "电量未知" + charge;
    }
}
