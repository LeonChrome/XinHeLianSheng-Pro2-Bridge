using System;

namespace Y700Switch2V60Viiper;

public enum Win11ParameterRoute
{
    UnsupportedPlatform,
    ApiUnavailable,
    ExplicitDiagnostic,
    ValidatedAutomatic,
    UnvalidatedEnvironment
}

/// <summary>
/// Automatic activation is limited to the exact OS stack + adapter/driver tuple
/// measured on the user's Win11 machine. This is not a certification of all Win11.
/// Driver information is read once at connection setup, never in Notify callbacks.
/// </summary>
public static class Win11ValidatedConnectionProfile
{
    public const string ProfileId = "win11_26200_intel_8087_0026_22_250_0_2";
    public const int ValidatedBuild = 26200;
    public const string ValidatedStackSha256 = "6750A4EA8ACEC328EC615863CC1A10B5808BE0BEACBF8F9F329FA340DED38D62";
    public const string ValidatedDriverVersion = "22.250.0.2";

    public static bool MatchesStack(BleEnvironmentFacts facts) =>
        facts.IsWindows && facts.Build == ValidatedBuild &&
        facts.RequestParametersApiAvailable && facts.ReadParametersApiAvailable &&
        string.Equals(facts.StackSha256, ValidatedStackSha256, StringComparison.OrdinalIgnoreCase);

    public static Win11ParameterRoute Select(
        BleEnvironmentFacts facts, BluetoothAdapterDiagnosticInfo? adapter, bool diagnosticOptIn)
    {
        if (!facts.IsWindows || facts.Build < 22000) return Win11ParameterRoute.UnsupportedPlatform;
        if (!facts.RequestParametersApiAvailable || !facts.ReadParametersApiAvailable)
            return Win11ParameterRoute.ApiUnavailable;
        if (diagnosticOptIn) return Win11ParameterRoute.ExplicitDiagnostic;
        if (MatchesStack(facts) && adapter != null &&
            string.Equals(adapter.Vid, "8087", StringComparison.OrdinalIgnoreCase) &&
            string.Equals(adapter.Pid, "0026", StringComparison.OrdinalIgnoreCase) &&
            string.Equals(adapter.DriverProvider?.Trim(), "Intel Corporation", StringComparison.OrdinalIgnoreCase) &&
            string.Equals(adapter.DriverVersion?.Trim(), ValidatedDriverVersion, StringComparison.Ordinal))
            return Win11ParameterRoute.ValidatedAutomatic;
        return Win11ParameterRoute.UnvalidatedEnvironment;
    }

    public static bool AllowsCustomRequest(Win11ParameterRoute route) =>
        route is Win11ParameterRoute.ExplicitDiagnostic or Win11ParameterRoute.ValidatedAutomatic;

    public static BluetoothAdapterDiagnosticInfo? SingleAuditedAdapter(SystemBluetoothSnapshot environment) =>
        environment.Warning.Length == 0 && environment.Adapters.Count == 1 &&
        environment.Adapters[0].LowEnergy && environment.Adapters[0].Central
            ? environment.Adapters[0].Driver : null;
}
