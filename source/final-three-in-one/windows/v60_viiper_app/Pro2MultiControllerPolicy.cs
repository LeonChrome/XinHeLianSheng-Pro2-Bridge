using System;
using System.Collections.Generic;
using System.Linq;

namespace Y700Switch2V60Viiper;

public sealed record Pro2SlotAddress(int Index, bool Enabled, string SavedAddress, string ConnectedAddress, bool Occupied);

public static class Pro2MultiControllerPolicy
{
    // Called only after the connection gate is acquired. Keep sleeping or offline
    // enabled controllers assigned to their slot, rather than stealing them.
    public static HashSet<string> Exclusions(int index, IEnumerable<Pro2SlotAddress> slots)
    {
        var all = slots.ToArray();
        string own = all.FirstOrDefault(s => s.Index == index)?.SavedAddress ?? "";
        var excluded = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var slot in all.Where(s => s.Index != index))
        {
            if (slot.Occupied && !string.IsNullOrWhiteSpace(slot.ConnectedAddress)) excluded.Add(slot.ConnectedAddress);
            if (slot.Enabled && !string.IsNullOrWhiteSpace(slot.SavedAddress) &&
                (!slot.SavedAddress.Equals(own, StringComparison.OrdinalIgnoreCase) || slot.Index < index))
                excluded.Add(slot.SavedAddress);
        }
        return excluded;
    }
}

public sealed record BlePeerRate(int Slot, string Address, bool Connected, bool Fresh, double Hz);
public static class BlePeerRateGuard
{
    public static bool Healthy(IReadOnlyList<BlePeerRate> before, IReadOnlyList<BlePeerRate> now)
    {
        foreach (var peer in before.Where(p => p.Connected && p.Fresh && double.IsFinite(p.Hz) && p.Hz > 0))
        {
            var after = now.FirstOrDefault(p => p.Slot == peer.Slot && p.Address.Equals(peer.Address, StringComparison.OrdinalIgnoreCase));
            if (after == null || !after.Connected || !after.Fresh || !double.IsFinite(after.Hz) ||
                after.Hz < peer.Hz - Math.Max(8, peer.Hz * .2)) return false;
        }
        return true;
    }
}
