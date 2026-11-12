using System;
using System.Collections.Generic;
using System.Linq;

namespace Y700Switch2V60Viiper;

/// <summary>Advertisement hints only. Initialisation still requires exact Pro2
/// command/FD2 characteristic identities; no commands to arbitrary devices.</summary>
public static class Pro2AdvertisementIdentity
{
    public static readonly Guid Fd2Service = new("ab7de9be-89fe-49ad-828f-118f09df7fd2");
    public static bool NameMatches(string? name)
    {
        string text = (name ?? "").Replace("_", " ").Replace("-", " ");
        return text.Contains("pro controller", StringComparison.OrdinalIgnoreCase) ||
            text.Contains("switch", StringComparison.OrdinalIgnoreCase) ||
            text.Contains("nintendo", StringComparison.OrdinalIgnoreCase) ||
            text.Contains("pro2", StringComparison.OrdinalIgnoreCase) ||
            text.Contains("pro 2", StringComparison.OrdinalIgnoreCase) ||
            text.Contains("ns2pro", StringComparison.OrdinalIgnoreCase);
    }
    public static int Score(string? name, bool nintendo, IEnumerable<Guid> services)
    {
        bool service = services.Contains(Fd2Service), named = NameMatches(name);
        if (!nintendo && !named && !service) return 0;
        return (service ? 80 : 0) + (nintendo ? 30 : 0) + (named ? 40 : 0) +
            ((name ?? "").Contains("Pro", StringComparison.OrdinalIgnoreCase) ? 10 : 0) +
            ((name ?? "").Contains("Controller", StringComparison.OrdinalIgnoreCase) ? 10 : 0);
    }
}
