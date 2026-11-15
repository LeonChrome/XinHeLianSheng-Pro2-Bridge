namespace Y700Switch2V60Viiper;

// Only for a slot's already-known address, never first-controller discovery.
// Manufacturer data may be missing from individual advertising packets. The
// receiver still requires the exact Pro2 GATT UUIDs before initialization.
public static class Pro2ReconnectAdvertisementPolicy
{
    public static bool CanUse(ulong expected, ulong observed, bool connectable, TimeSpan advertisementAge) =>
        expected is > 0 and <= 0xFFFFFFFFFFFF && expected == observed && connectable &&
        advertisementAge.TotalSeconds is >= -1 and <= 5;

    // An unseen controller can interrupt a scan backoff. Real GATT failures keep
    // their existing cooldown and pending-Windows-request quarantine.
    public static bool CanInterruptScanBackoff(string reason) =>
        reason is "no_pro2" or "no_advertisements" or "saved_not_seen";
}
