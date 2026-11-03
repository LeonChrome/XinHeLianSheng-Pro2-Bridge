namespace Y700Switch2V60Viiper;

public enum FaceLayoutPreset { Position, NintendoNames, Xbox, PlayStation, Custom }

public sealed record FaceButtonProfile
{
    public FaceLayoutPreset Preset { get; init; }
    public GamepadButtons North { get; init; } = GamepadButtons.North;
    public GamepadButtons East { get; init; } = GamepadButtons.East;
    public GamepadButtons South { get; init; } = GamepadButtons.South;
    public GamepadButtons West { get; init; } = GamepadButtons.West;
    public FaceButtonProfile Normalize() => this with { Preset = Enum.IsDefined(Preset) ? Preset : FaceLayoutPreset.Custom,
        North = Safe(North), East = Safe(East), South = Safe(South), West = Safe(West) };
    private static GamepadButtons Safe(GamepadButtons key) => key is GamepadButtons.None or GamepadButtons.North or
        GamepadButtons.East or GamepadButtons.South or GamepadButtons.West ? key : GamepadButtons.None;
    public bool SameMapping(FaceButtonProfile other) => North == other.North && East == other.East && South == other.South && West == other.West;
    public static FaceButtonProfile ForPreset(FaceLayoutPreset preset, ViiperVirtualMode mode) =>
        preset == FaceLayoutPreset.NintendoNames && mode is not (ViiperVirtualMode.Pro1 or ViiperVirtualMode.Pro2)
        ? new() { Preset = preset, North = GamepadButtons.West, East = GamepadButtons.South, South = GamepadButtons.East, West = GamepadButtons.North }
        : new() { Preset = preset };
}

public sealed record FaceButtonConfiguration
{
    public FaceButtonProfile Pro2 { get; init; } = new();
    public FaceButtonProfile Pro1 { get; init; } = new();
    public FaceButtonProfile Xbox { get; init; } = new();
    public FaceButtonProfile Ps5 { get; init; } = new();
    public FaceButtonConfiguration Normalize() => new() { Pro2 = (Pro2 ?? new()).Normalize(), Pro1 = (Pro1 ?? new()).Normalize(),
        Xbox = (Xbox ?? new()).Normalize(), Ps5 = (Ps5 ?? new()).Normalize() };
    public FaceButtonProfile ForMode(ViiperVirtualMode mode) => mode switch {
        ViiperVirtualMode.Pro2 => Pro2, ViiperVirtualMode.Pro1 => Pro1,
        ViiperVirtualMode.DualSenseLike or ViiperVirtualMode.DualSenseProfessionalImuTest => Ps5, _ => Xbox };
    public FaceButtonConfiguration WithMode(ViiperVirtualMode mode, FaceButtonProfile profile) => mode switch {
        ViiperVirtualMode.Pro2 => this with { Pro2 = profile }, ViiperVirtualMode.Pro1 => this with { Pro1 = profile },
        ViiperVirtualMode.DualSenseLike or ViiperVirtualMode.DualSenseProfessionalImuTest => this with { Ps5 = profile }, _ => this with { Xbox = profile } };
}

// Runs once on each fresh physical frame, before rear-button output mapping.
public sealed class FaceButtonMapper
{
    public const GamepadButtons FaceMask = GamepadButtons.North | GamepadButtons.East | GamepadButtons.South | GamepadButtons.West;
    private FaceButtonProfile? current;
    private GamepadButtons blocked;
    public void Reset() { current = null; blocked = GamepadButtons.None; }
    public GamepadState Apply(GamepadState physical, FaceButtonProfile profile)
    {
        if (current != null && !current.SameMapping(profile)) blocked |= physical.Buttons & FaceMask;
        current = profile;
        blocked &= physical.Buttons;
        var down = physical.Buttons & FaceMask & ~blocked;
        var buttons = physical.Buttons & ~FaceMask;
        if ((down & GamepadButtons.North) != 0) buttons |= profile.North;
        if ((down & GamepadButtons.East) != 0) buttons |= profile.East;
        if ((down & GamepadButtons.South) != 0) buttons |= profile.South;
        if ((down & GamepadButtons.West) != 0) buttons |= profile.West;
        if (buttons == physical.Buttons) return physical;
        var mapped = physical.Clone(); mapped.Buttons = buttons;
        return mapped;
    }
}
