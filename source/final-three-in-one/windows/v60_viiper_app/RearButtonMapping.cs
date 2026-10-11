using System;
using System.Diagnostics;
using System.Linq;

namespace Y700Switch2V60Viiper;

public enum RearButtonAction { Hold, Tap, Repeat, Sequence }

public sealed record RearButtonStep
{
    public GamepadButtons Buttons { get; init; }
    public int DelayMs { get; init; }
    public int PressMs { get; init; } = 80;
    public RearButtonStep Normalize() => this with
    {
        Buttons = RearButtonBinding.SafeButtons(Buttons),
        DelayMs = Math.Clamp(DelayMs, 0, 30000), PressMs = Math.Clamp(PressMs, 30, 30000)
    };
}

public sealed record RearButtonBinding
{
    public const GamepadButtons AllowedButtons = (GamepadButtons)((1UL << 18) - 1);
    public bool Enabled { get; init; }
    public RearButtonAction Action { get; init; }
    public GamepadButtons Buttons { get; init; }
    public int DelayMs { get; init; }
    public int PressMs { get; init; } = 80;
    public int GapMs { get; init; } = 80;
    public int RepeatCount { get; init; } // zero: while the physical rear key is held
    public RearButtonStep[] Steps { get; init; } = [];
    public RearButtonBinding Normalize() => this with
    {
        Action = Enum.IsDefined(Action) ? Action : RearButtonAction.Hold,
        Buttons = SafeButtons(Buttons),
        DelayMs = Math.Clamp(DelayMs, 0, 30000), PressMs = Math.Clamp(PressMs, 30, 30000),
        GapMs = Math.Clamp(GapMs, 30, 30000), RepeatCount = Math.Clamp(RepeatCount, 0, 1000),
        Steps = (Steps ?? []).Where(s => s != null).Take(16).Select(s => s.Normalize()).ToArray()
    };

    public static bool HasOppositeDirections(GamepadButtons buttons) =>
        (buttons & (GamepadButtons.DPadUp | GamepadButtons.DPadDown)) == (GamepadButtons.DPadUp | GamepadButtons.DPadDown) ||
        (buttons & (GamepadButtons.DPadLeft | GamepadButtons.DPadRight)) == (GamepadButtons.DPadLeft | GamepadButtons.DPadRight);
    public static GamepadButtons SafeButtons(GamepadButtons buttons)
    {
        buttons &= AllowedButtons;
        if ((buttons & (GamepadButtons.DPadUp | GamepadButtons.DPadDown)) == (GamepadButtons.DPadUp | GamepadButtons.DPadDown))
            buttons &= ~(GamepadButtons.DPadUp | GamepadButtons.DPadDown);
        if ((buttons & (GamepadButtons.DPadLeft | GamepadButtons.DPadRight)) == (GamepadButtons.DPadLeft | GamepadButtons.DPadRight))
            buttons &= ~(GamepadButtons.DPadLeft | GamepadButtons.DPadRight);
        return buttons;
    }
}

public sealed record RearButtonProfile
{
    public RearButtonBinding Left { get; init; } = new();
    public RearButtonBinding Right { get; init; } = new();
    public RearButtonBinding C { get; init; } = new();
    public RearButtonProfile Normalize() => new()
    {
        Left = (Left ?? new()).Normalize(), Right = (Right ?? new()).Normalize(),
        C = (C ?? new()).Normalize()
    };
}

public sealed record RearButtonConfiguration
{
    public RearButtonProfile Xbox { get; init; } = new();
    public RearButtonProfile Ps5 { get; init; } = new();
    public RearButtonProfile Pro1 { get; init; } = new();
    public RearButtonConfiguration Normalize() => new()
    {
        Xbox = (Xbox ?? new()).Normalize(), Ps5 = (Ps5 ?? new()).Normalize(),
        Pro1 = (Pro1 ?? new()).Normalize()
    };
    public RearButtonProfile? ForMode(ViiperVirtualMode mode) => mode switch
    {
        ViiperVirtualMode.Xbox or ViiperVirtualMode.XboxProfessionalImuTest => Xbox,
        ViiperVirtualMode.DualSenseLike or
            ViiperVirtualMode.DualSenseProfessionalImuTest => Ps5,
        ViiperVirtualMode.Pro1 => Pro1,
        _ => null // Native Pro2 controls, including GL/GR, are never remapped here.
    };
}

/// <summary>
/// Single output-consumer state machine. Evaluated ONLY on fresh physical input
/// frames; it never starts a timer, performs GATT operations, or fabricates FD2.
/// Configuration snapshots belong to the session and cannot be edited by the UI.
/// </summary>
public sealed class RearButtonMapper
{
    private readonly Channel left = new(), right = new(), c = new();
    private RearButtonConfiguration? current;
    private ViiperVirtualMode currentMode;
    private long lastTicks;
    private GamepadState? lastPhysical;
    private GamepadButtons lastMapped;
    public bool HasPendingAction => left.Active || right.Active || c.Active || lastMapped != GamepadButtons.None;
    public void Reset()
    {
        current = null; lastTicks = 0; left.Reset(); right.Reset(); c.Reset(); lastPhysical = null; lastMapped = GamepadButtons.None;
    }

    // Called by the single output consumer. Only remove this mapper's additions;
    // never release an independently held physical key or manufacture FD2 input.
    public bool TryCancelOutput(out GamepadState release, bool forceRelease = false)
    {
        bool needed = lastPhysical != null && (lastMapped != GamepadButtons.None ||
            (forceRelease && current?.ForMode(currentMode) != null));
        release = lastPhysical?.Clone() ?? GamepadState.Neutral();
        var profile = current?.ForMode(currentMode);
        if (profile?.Left.Enabled == true) release.Buttons &= ~GamepadButtons.PaddleLeft;
        if (profile?.Right.Enabled == true) release.Buttons &= ~GamepadButtons.PaddleRight;
        if (profile?.C.Enabled == true) release.Buttons &= ~GamepadButtons.Aux;
        Reset();
        return needed;
    }

    public static bool ProfilesEquivalent(RearButtonProfile? a, RearButtonProfile? b) =>
        ReferenceEquals(a, b) || (a != null && b != null && BindingEquivalent(a.Left, b.Left) &&
            BindingEquivalent(a.Right, b.Right) && BindingEquivalent(a.C, b.C));
    private static bool BindingEquivalent(RearButtonBinding a, RearButtonBinding b) =>
        a.Enabled == b.Enabled && a.Action == b.Action && a.Buttons == b.Buttons && a.DelayMs == b.DelayMs &&
        a.PressMs == b.PressMs && a.GapMs == b.GapMs && a.RepeatCount == b.RepeatCount && a.Steps.SequenceEqual(b.Steps);

    public static GamepadState? MergeProfessionalControls(GamepadState mapped, GamepadState? professional)
    {
        if (professional == null || (professional.Buttons == mapped.Buttons &&
            professional.L2 == mapped.L2 && professional.R2 == mapped.R2)) return professional;
        var result = professional.Clone();
        result.Buttons = mapped.Buttons; result.L2 = mapped.L2; result.R2 = mapped.R2;
        return result;
    }

    public GamepadState Apply(GamepadState physical, ViiperVirtualMode mode,
        RearButtonConfiguration configuration, long ticks)
    {
        var profile = configuration.ForMode(mode);
        bool profileChanged = !ReferenceEquals(current, configuration) &&
            !ProfilesEquivalent(current?.ForMode(currentMode), profile);
        if (profileChanged || currentMode != mode || ticks < lastTicks ||
            (current != null && ticks - lastTicks > Stopwatch.Frequency / 10))
        {
            left.Reset(); right.Reset(); c.Reset();
        }
        current = configuration; currentMode = mode;
        lastTicks = ticks;
        lastPhysical = physical;
        lastMapped = GamepadButtons.None;
        if (profile == null || (!profile.Left.Enabled && !profile.Right.Enabled && !profile.C.Enabled))
            return physical;
        GamepadButtons mapped = left.Get(profile.Left, physical.IsPressed(GamepadButtons.PaddleLeft), ticks) |
            right.Get(profile.Right, physical.IsPressed(GamepadButtons.PaddleRight), ticks) |
            c.Get(profile.C, physical.IsPressed(GamepadButtons.Aux), ticks);
        // A physical direction wins over an opposing macro; opposing rear macros
        // neutralize each other instead of producing an invalid D-pad chord.
        if (physical.IsPressed(GamepadButtons.DPadUp)) mapped &= ~GamepadButtons.DPadDown;
        if (physical.IsPressed(GamepadButtons.DPadDown)) mapped &= ~GamepadButtons.DPadUp;
        if (physical.IsPressed(GamepadButtons.DPadLeft)) mapped &= ~GamepadButtons.DPadRight;
        if (physical.IsPressed(GamepadButtons.DPadRight)) mapped &= ~GamepadButtons.DPadLeft;
        mapped = RearButtonBinding.SafeButtons(mapped);
        lastMapped = mapped;
        if (mapped != GamepadButtons.None) lastPhysical = physical.Clone();
        GamepadButtons buttons = physical.Buttons;
        if (profile.Left.Enabled) buttons &= ~GamepadButtons.PaddleLeft;
        if (profile.Right.Enabled) buttons &= ~GamepadButtons.PaddleRight;
        if (profile.C.Enabled) buttons &= ~GamepadButtons.Aux;
        buttons |= mapped;
        if (buttons == physical.Buttons &&
            ((mapped & GamepadButtons.L2) == 0 || physical.L2 == GamepadState.TriggerMax) &&
            ((mapped & GamepadButtons.R2) == 0 || physical.R2 == GamepadState.TriggerMax)) return physical;
        var result = physical.Clone();
        result.Buttons = buttons; // OR preserves independently held face/shoulder keys.
        if ((mapped & GamepadButtons.L2) != 0) result.L2 = GamepadState.TriggerMax;
        if ((mapped & GamepadButtons.R2) != 0) result.R2 = GamepadState.TriggerMax;
        return result;
    }

    private sealed class Channel
    {
        private bool initialized, down, active;
        private long start;
        private long lastPulse = -1;
        private GamepadButtons lastOutput;
        public bool Active => active;
        public void Reset() { initialized = down = active = false; start = 0; lastPulse = -1; lastOutput = GamepadButtons.None; }
        private GamepadButtons Emit(GamepadButtons buttons, long pulse = 0)
        {
            if (pulse != lastPulse && (lastOutput & buttons) != 0)
            {
                // Even if delivery skipped the configured gap, overlapping
                // consecutive pulses must expose an actual release frame.
                lastOutput = GamepadButtons.None;
                return GamepadButtons.None;
            }
            lastPulse = pulse; lastOutput = buttons;
            return buttons;
        }
        public GamepadButtons Get(RearButtonBinding binding, bool pressed, long ticks)
        {
            if (!initialized)
            {
                initialized = true; down = pressed;
                // Starting a session or applying settings while held must not fire.
                return GamepadButtons.None;
            }
            if (!binding.Enabled) { active = false; down = pressed; return Emit(GamepadButtons.None); }
            // No queue and no overlapping restart for delayed taps/sequences.
            if (pressed && !down && !active) { active = true; start = ticks; }
            down = pressed;
            if (!pressed && binding.Action is RearButtonAction.Hold or RearButtonAction.Repeat)
                active = false;
            if (!active) return Emit(GamepadButtons.None);
            double elapsed = (ticks - start) * 1000.0 / Stopwatch.Frequency - binding.DelayMs;
            if (elapsed < 0) return Emit(GamepadButtons.None);
            switch (binding.Action)
            {
                case RearButtonAction.Hold: return Emit(binding.Buttons);
                case RearButtonAction.Tap:
                    if (elapsed < binding.PressMs) return Emit(binding.Buttons);
                    break;
                case RearButtonAction.Repeat:
                    int period = binding.PressMs + binding.GapMs;
                    double cycle = Math.Floor(elapsed / period);
                    if (binding.RepeatCount == 0 || cycle < binding.RepeatCount)
                        return Emit(elapsed % period < binding.PressMs ? binding.Buttons : GamepadButtons.None, (long)cycle);
                    break;
                case RearButtonAction.Sequence:
                    // First step uses the primary chord. Further steps have independent
                    // delay/duration. Late frames never replay a backlog of missed pulses.
                    if (elapsed < binding.PressMs) return Emit(binding.Buttons);
                    elapsed -= binding.PressMs;
                    long stepIndex = 1;
                    foreach (var step in binding.Steps)
                    {
                        if (elapsed < step.DelayMs) return Emit(GamepadButtons.None);
                        elapsed -= step.DelayMs;
                        if (elapsed < step.PressMs) return Emit(step.Buttons, stepIndex);
                        elapsed -= step.PressMs;
                        stepIndex++;
                    }
                    break;
            }
            active = false;
            return Emit(GamepadButtons.None);
        }
    }
}
