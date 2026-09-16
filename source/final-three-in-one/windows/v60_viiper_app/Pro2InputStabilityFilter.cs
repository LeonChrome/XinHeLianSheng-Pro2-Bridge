using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;

namespace Y700Switch2V60Viiper;

public enum Pro2AxisFilterDecisionKind
{
    Accept,
    Hold,
    Reject,
    RampAccept
}

public sealed record Pro2AxisFilterEvent(
    string AxisName,
    Pro2AxisFilterDecisionKind Decision,
    string Reason,
    ushort OldValue,
    ushort NewValue,
    ushort OutputValue,
    int Delta,
    ushort OldLeftX,
    ushort OldLeftY,
    ushort OldRightX,
    ushort OldRightY,
    ushort NewLeftX,
    ushort NewLeftY,
    ushort NewRightX,
    ushort NewRightY,
    double StickVectorDelta,
    int ConsecutiveSuspectFrames,
    double CandidateAgeMs,
    double FrameDeltaMs,
    bool DirectionStable,
    bool Continuous,
    string MotionClass,
    bool ActiveMotion,
    bool FastReversal,
    bool CenterCrossing,
    bool InputSwallowed,
    int RawToFilteredDelta);

public sealed class Pro2InputFilterResult
{
    public Pro2InputFilterResult(
        GamepadState rawState,
        GamepadState acceptedState,
        IReadOnlyList<Pro2AxisFilterEvent> events)
    {
        RawState = rawState;
        AcceptedState = acceptedState;
        Events = events;
    }

    public GamepadState RawState { get; }
    public GamepadState AcceptedState { get; }
    public IReadOnlyList<Pro2AxisFilterEvent> Events { get; }
    public bool HasAxisTelemetry => Events.Count > 0;
    public int InterventionCount => Events.Count(e =>
        e.Decision != Pro2AxisFilterDecisionKind.Accept ||
        e.RawToFilteredDelta > 0 ||
        e.InputSwallowed);
    public bool HasAxisIntervention => InterventionCount > 0;
    public bool HasHoldOrReject => Events.Any(e =>
        e.Decision == Pro2AxisFilterDecisionKind.Hold ||
        e.Decision == Pro2AxisFilterDecisionKind.Reject);
    public bool HasRamp => Events.Any(e => e.Decision == Pro2AxisFilterDecisionKind.RampAccept);
    public bool HasInputSwallowed => Events.Any(e => e.InputSwallowed);
    public int RawToFilteredMaxDelta => Events.Count == 0
        ? 0
        : Events.Max(e => e.RawToFilteredDelta);
    public string PrimaryReason => Events.Count == 0
        ? "accept_normal"
        : string.Join(",", Events.Select(e => e.AxisName + ":" + e.Reason).Distinct());
}

public sealed class Pro2InputStabilityFilter
{
    private readonly Pro2InputStabilityOptions options;
    private readonly AxisState lx = new("left_x");
    private readonly AxisState ly = new("left_y");
    private readonly AxisState rx = new("right_x");
    private readonly AxisState ry = new("right_y");
    private GamepadState? lastAccepted;
    private ulong heldCount;
    private ulong rejectedCount;
    private ulong acceptedCandidateCount;
    private ulong rampedCount;
    private ulong rawSpikeCount;
    private ulong filteredSpikeCount;
    private ulong rawFastMotionCount;
    private ulong filteredFastMotionCount;
    private ulong falseHoldCount;
    private ulong activeMotionCount;
    private ulong fastReversalCount;
    private ulong centerCrossCount;
    private ulong inputSwallowedCount;
    private int rawToFilteredMaxDelta;

    public Pro2InputStabilityFilter()
        : this(Pro2InputStabilityOptions.Default)
    {
    }

    public Pro2InputStabilityFilter(Pro2InputStabilityOptions? options)
    {
        this.options = Pro2InputStabilityOptions.Normalize(options);
    }

    public Pro2InputStabilityOptions Options => options;

    public string MetricsSummary =>
        " axis_hold=" + heldCount +
        " axis_reject=" + rejectedCount +
        " axis_accept_candidate=" + acceptedCandidateCount +
        " axis_ramp=" + rampedCount +
        " raw_spike_count=" + rawSpikeCount +
        " filtered_spike_count=" + filteredSpikeCount +
        " raw_fast_motion_count=" + rawFastMotionCount +
        " filtered_fast_motion_count=" + filteredFastMotionCount +
        " false_hold_count=" + falseHoldCount +
        " active_motion_count=" + activeMotionCount +
        " fast_reversal_count=" + fastReversalCount +
        " center_cross_count=" + centerCrossCount +
        " raw_to_filtered_max_delta=" + rawToFilteredMaxDelta +
        " input_swallowed_count=" + inputSwallowedCount;

    public bool TryAccept(
        GamepadState parsed,
        out GamepadState accepted,
        out string reason)
    {
        Pro2InputFilterResult result = Process(parsed, Stopwatch.GetTimestamp());
        accepted = result.AcceptedState;
        reason = result.PrimaryReason;
        return !result.HasHoldOrReject && !result.HasRamp;
    }

    public Pro2InputFilterResult Process(GamepadState parsed, long nowTicks)
    {
        if (lastAccepted == null)
        {
            GamepadState initial = parsed.Clone();
            InitializeAxisStates(initial, nowTicks);
            lastAccepted = initial.Clone();
            return new Pro2InputFilterResult(
                parsed.Clone(),
                initial,
                Array.Empty<Pro2AxisFilterEvent>());
        }

        GamepadState previous = lastAccepted;
        GamepadState accepted = parsed.Clone();
        var events = new List<Pro2AxisFilterEvent>(4);

        accepted.Lx = ProcessAxis(lx, previous, parsed, parsed.Lx, nowTicks, events);
        accepted.Ly = ProcessAxis(ly, previous, parsed, parsed.Ly, nowTicks, events);
        accepted.Rx = ProcessAxis(rx, previous, parsed, parsed.Rx, nowTicks, events);
        accepted.Ry = ProcessAxis(ry, previous, parsed, parsed.Ry, nowTicks, events);

        lastAccepted = accepted.Clone();
        return new Pro2InputFilterResult(parsed.Clone(), accepted, events);
    }

    public Pro2InputFilterResult ProcessAt(GamepadState parsed, TimeSpan elapsed)
    {
        long ticks = Math.Max(
            1,
            (long)Math.Round(elapsed.TotalSeconds * Stopwatch.Frequency));
        return Process(parsed, ticks);
    }

    public void Reset()
    {
        lastAccepted = null;
        lx.Reset();
        ly.Reset();
        rx.Reset();
        ry.Reset();
        heldCount = 0;
        rejectedCount = 0;
        acceptedCandidateCount = 0;
        rampedCount = 0;
        rawSpikeCount = 0;
        filteredSpikeCount = 0;
        rawFastMotionCount = 0;
        filteredFastMotionCount = 0;
        falseHoldCount = 0;
        activeMotionCount = 0;
        fastReversalCount = 0;
        centerCrossCount = 0;
        inputSwallowedCount = 0;
        rawToFilteredMaxDelta = 0;
    }

    private void InitializeAxisStates(GamepadState state, long nowTicks)
    {
        lx.Initialize(state.Lx, nowTicks);
        ly.Initialize(state.Ly, nowTicks);
        rx.Initialize(state.Rx, nowTicks);
        ry.Initialize(state.Ry, nowTicks);
    }

    private ushort ProcessAxis(
        AxisState axis,
        GamepadState previous,
        GamepadState current,
        ushort rawValue,
        long nowTicks,
        List<Pro2AxisFilterEvent> events)
    {
        if (!axis.Initialized)
        {
            axis.Initialize(rawValue, nowTicks);
            return rawValue;
        }

        double frameDeltaMs = FrameDeltaMs(axis, nowTicks);
        int deltaFromGood = AxisDelta(axis.LastGoodValue, rawValue);
        int deltaFromLastRaw = AxisDelta(axis.LastRawValue, rawValue);
        bool rawSpike = deltaFromGood >= options.AxisSpikeDeltaThreshold ||
                        deltaFromGood / Math.Max(frameDeltaMs, 1.0) > options.AxisMaxDeltaPerMs;
        bool rawFastMotion = deltaFromGood >= options.AxisMotionDeltaThreshold ||
                             deltaFromLastRaw >= options.AxisMotionDeltaThreshold;
        bool centerCrossing = IsCenterCrossing(axis.LastGoodValue, rawValue);
        bool recentlyActive = IsRecentlyActive(axis, nowTicks);
        bool fastReversal = recentlyActive &&
                            centerCrossing &&
                            deltaFromGood >= options.AxisMotionDeltaThreshold;
        bool activeMotion = recentlyActive || fastReversal;

        if (rawSpike)
        {
            rawSpikeCount++;
        }
        if (rawFastMotion)
        {
            rawFastMotionCount++;
        }
        if (centerCrossing)
        {
            centerCrossCount++;
        }
        if (activeMotion)
        {
            activeMotionCount++;
        }
        if (fastReversal)
        {
            fastReversalCount++;
        }

        if (frameDeltaMs >= options.AxisLinkRecoveryGapMs &&
            deltaFromGood >= options.AxisMotionDeltaThreshold)
        {
            return FollowFastMotion(
                axis,
                previous,
                current,
                rawValue,
                nowTicks,
                frameDeltaMs,
                deltaFromGood,
                "link_recovery_accept",
                fastReversal,
                centerCrossing,
                events);
        }

        if (axis.SuspectActive)
        {
            return ProcessSuspectAxis(
                axis,
                previous,
                current,
                rawValue,
                nowTicks,
                frameDeltaMs,
                deltaFromGood,
                recentlyActive,
                fastReversal,
                centerCrossing,
                events);
        }

        if (deltaFromGood <= options.AxisNormalDeltaThreshold)
        {
            axis.Accept(rawValue, nowTicks, options.AxisMotionDeltaThreshold);
            return rawValue;
        }

        if (activeMotion || fastReversal)
        {
            return FollowFastMotion(
                axis,
                previous,
                current,
                rawValue,
                nowTicks,
                frameDeltaMs,
                deltaFromGood,
                fastReversal ? "fast_reversal" : "active_motion",
                fastReversal,
                centerCrossing,
                events);
        }

        if (rawSpike)
        {
            axis.StartCandidate(rawValue, axis.LastGoodValue, nowTicks);
            return HoldCandidate(
                axis,
                previous,
                current,
                rawValue,
                nowTicks,
                frameDeltaMs,
                deltaFromGood,
                "hold_idle_spike_candidate",
                "idle_spike",
                activeMotion: false,
                fastReversal: false,
                centerCrossing,
                directionStable: true,
                continuous: true,
                events);
        }

        axis.Accept(rawValue, nowTicks, options.AxisMotionDeltaThreshold);
        return rawValue;
    }

    private ushort ProcessSuspectAxis(
        AxisState axis,
        GamepadState previous,
        GamepadState current,
        ushort rawValue,
        long nowTicks,
        double frameDeltaMs,
        int deltaFromGood,
        bool recentlyActive,
        bool fastReversal,
        bool centerCrossing,
        List<Pro2AxisFilterEvent> events)
    {
        if (deltaFromGood <= options.AxisReturnToGoodThreshold)
        {
            rejectedCount++;
            filteredSpikeCount++;
            events.Add(BuildEvent(
                axis,
                previous,
                current,
                rawValue,
                rawValue,
                Pro2AxisFilterDecisionKind.Reject,
                "reject_returned_to_last_good",
                deltaFromGood,
                nowTicks,
                frameDeltaMs,
                directionStable: true,
                continuous: true,
                motionClass: "idle_spike",
                activeMotion: false,
                fastReversal,
                centerCrossing,
                inputSwallowed: false));
            axis.ClearCandidate();
            axis.Accept(rawValue, nowTicks, options.AxisMotionDeltaThreshold);
            return rawValue;
        }

        int direction = Sign(rawValue - axis.CandidateBaseValue);
        bool directionStable =
            direction == 0 ||
            axis.CandidateDirection == 0 ||
            direction == axis.CandidateDirection;
        int stepFromPreviousCandidate = AxisDelta(axis.CandidateLastValue, rawValue);
        bool continuous =
            stepFromPreviousCandidate <= options.AxisSpikeDeltaThreshold ||
            stepFromPreviousCandidate / Math.Max(frameDeltaMs, 1.0) <= options.AxisMaxDeltaPerMs;

        int nextCandidateFrameCount = axis.CandidateFrameCount + 1;
        bool activeCandidate = recentlyActive ||
                               fastReversal ||
                               nextCandidateFrameCount >= 2;
        if ((!directionStable || !continuous) && (recentlyActive || fastReversal))
        {
            return FollowFastMotion(
                axis,
                previous,
                current,
                rawValue,
                nowTicks,
                frameDeltaMs,
                deltaFromGood,
                fastReversal ? "fast_reversal_discontinuous_follow" : "active_motion_discontinuous_follow",
                fastReversal,
                centerCrossing,
                events);
        }

        if (!directionStable || !continuous)
        {
            axis.StartCandidate(rawValue, axis.LastGoodValue, nowTicks);
            return HoldCandidate(
                axis,
                previous,
                current,
                rawValue,
                nowTicks,
                frameDeltaMs,
                deltaFromGood,
                directionStable ? "hold_candidate_discontinuous_reset" : "hold_candidate_direction_reset",
                "raw_corruption",
                activeMotion: false,
                fastReversal,
                centerCrossing,
                directionStable,
                continuous,
                events);
        }

        axis.UpdateCandidate(rawValue);
        double candidateAgeMs = TicksToMilliseconds(nowTicks - axis.CandidateStartTicks);
        double minConfirmMs = fastReversal
            ? options.AxisFastReversalConfirmMs
            : activeCandidate
                ? options.AxisActiveMinConfirmMs
                : options.AxisIdleMinConfirmMs;
        if (candidateAgeMs < minConfirmMs)
        {
            return HoldCandidate(
                axis,
                previous,
                current,
                rawValue,
                nowTicks,
                frameDeltaMs,
                deltaFromGood,
                activeCandidate ? "hold_active_candidate_min_confirm" : "hold_idle_candidate_min_confirm",
                activeCandidate ? "active_motion" : "idle_spike",
                activeCandidate,
                fastReversal,
                centerCrossing,
                directionStable,
                continuous,
                events);
        }

        return FollowFastMotion(
            axis,
            previous,
            current,
            rawValue,
            nowTicks,
            frameDeltaMs,
            deltaFromGood,
            fastReversal
                ? "fast_reversal_confirmed"
                : activeCandidate
                    ? "active_motion_confirmed"
                    : "idle_candidate_confirmed",
            fastReversal,
            centerCrossing,
            events);
    }

    private ushort FollowFastMotion(
        AxisState axis,
        GamepadState previous,
        GamepadState current,
        ushort rawValue,
        long nowTicks,
        double frameDeltaMs,
        int deltaFromGood,
        string reason,
        bool fastReversal,
        bool centerCrossing,
        List<Pro2AxisFilterEvent> events)
    {
        double rampPerMs = fastReversal
            ? options.AxisFastReversalRampMaxDeltaPerMs
            : reason.Contains("idle", StringComparison.Ordinal)
                ? options.AxisIdleRampMaxDeltaPerMs
                : options.AxisActiveRampMaxDeltaPerMs;
        ushort output = rawValue;
        Pro2AxisFilterDecisionKind decision = Pro2AxisFilterDecisionKind.Accept;
        string motionClass = fastReversal
            ? "fast_reversal"
            : reason.Contains("idle", StringComparison.Ordinal)
                ? "idle_confirmed"
                : "active_motion";

        bool idleMotion = reason.Contains("idle", StringComparison.Ordinal);
        bool linkRecovery = reason.Contains("link_recovery", StringComparison.Ordinal);
        bool lowLatencyBypass =
            options.AxisLowLatencyMotionBypassEnabled &&
            (!idleMotion || linkRecovery);

        if (options.AxisRampEnabled && !lowLatencyBypass)
        {
            int maxStep = Math.Max(
                1,
                (int)Math.Round(rampPerMs * Math.Max(frameDeltaMs, 1.0)));
            output = StepToward(axis.LastGoodValue, rawValue, maxStep);
            if (output != rawValue)
            {
                decision = Pro2AxisFilterDecisionKind.RampAccept;
                rampedCount++;
                reason = "ramp_" + reason;
            }
        }

        if (decision == Pro2AxisFilterDecisionKind.Accept)
        {
            acceptedCandidateCount++;
        }
        if (AxisDelta(axis.LastGoodValue, output) >= options.AxisMotionDeltaThreshold)
        {
            filteredFastMotionCount++;
        }
        filteredSpikeCount++;

        events.Add(BuildEvent(
            axis,
            previous,
            current,
            rawValue,
            output,
            decision,
            reason,
            deltaFromGood,
            nowTicks,
            frameDeltaMs,
            directionStable: true,
            continuous: true,
            motionClass,
            activeMotion: motionClass != "idle_confirmed",
            fastReversal,
            centerCrossing,
            inputSwallowed: false));

        axis.Accept(output, nowTicks, options.AxisMotionDeltaThreshold);
        axis.LastRawValue = rawValue;
        if (output == rawValue)
        {
            axis.ClearCandidate();
        }
        else
        {
            axis.KeepCandidate(rawValue, nowTicks);
        }

        return output;
    }

    private ushort HoldCandidate(
        AxisState axis,
        GamepadState previous,
        GamepadState current,
        ushort rawValue,
        long nowTicks,
        double frameDeltaMs,
        int deltaFromGood,
        string reason,
        string motionClass,
        bool activeMotion,
        bool fastReversal,
        bool centerCrossing,
        bool directionStable,
        bool continuous,
        List<Pro2AxisFilterEvent> events)
    {
        double candidateAgeMs = axis.SuspectActive
            ? TicksToMilliseconds(nowTicks - axis.CandidateStartTicks)
            : 0;
        bool inputSwallowed =
            activeMotion &&
            candidateAgeMs >= options.AxisInputSwallowDetectMs &&
            deltaFromGood >= options.AxisMotionDeltaThreshold;
        if (activeMotion || fastReversal)
        {
            falseHoldCount++;
        }
        if (inputSwallowed)
        {
            inputSwallowedCount++;
        }
        heldCount++;
        filteredSpikeCount++;

        events.Add(BuildEvent(
            axis,
            previous,
            current,
            rawValue,
            axis.LastGoodValue,
            Pro2AxisFilterDecisionKind.Hold,
            reason,
            deltaFromGood,
            nowTicks,
            frameDeltaMs,
            directionStable,
            continuous,
            motionClass,
            activeMotion,
            fastReversal,
            centerCrossing,
            inputSwallowed));
        axis.Hold(nowTicks);
        return axis.LastGoodValue;
    }

    private Pro2AxisFilterEvent BuildEvent(
        AxisState axis,
        GamepadState previous,
        GamepadState current,
        ushort rawValue,
        ushort outputValue,
        Pro2AxisFilterDecisionKind decision,
        string reason,
        int delta,
        long nowTicks,
        double frameDeltaMs,
        bool directionStable,
        bool continuous,
        string motionClass,
        bool activeMotion,
        bool fastReversal,
        bool centerCrossing,
        bool inputSwallowed)
    {
        int rawToFilteredDelta = AxisDelta(rawValue, outputValue);
        rawToFilteredMaxDelta = Math.Max(rawToFilteredMaxDelta, rawToFilteredDelta);
        return new Pro2AxisFilterEvent(
            axis.Name,
            decision,
            reason,
            axis.LastGoodValue,
            rawValue,
            outputValue,
            delta,
            previous.Lx,
            previous.Ly,
            previous.Rx,
            previous.Ry,
            current.Lx,
            current.Ly,
            current.Rx,
            current.Ry,
            StickVectorDelta(axis.Name, previous, current),
            axis.CandidateFrameCount,
            axis.SuspectActive ? TicksToMilliseconds(nowTicks - axis.CandidateStartTicks) : 0,
            frameDeltaMs,
            directionStable,
            continuous,
            motionClass,
            activeMotion,
            fastReversal,
            centerCrossing,
            inputSwallowed,
            rawToFilteredDelta);
    }

    private bool IsRecentlyActive(AxisState axis, long nowTicks)
    {
        return axis.LastMotionTicks != 0 &&
               TicksToMilliseconds(nowTicks - axis.LastMotionTicks) <= options.AxisMotionActiveWindowMs;
    }

    private bool IsCenterCrossing(ushort oldValue, ushort newValue)
    {
        int oldSign = CenterSign(oldValue, options.AxisCenterCrossThreshold);
        int newSign = CenterSign(newValue, options.AxisCenterCrossThreshold);
        return oldSign != 0 && newSign != 0 && oldSign != newSign;
    }

    private static int CenterSign(ushort value, int threshold)
    {
        int offset = value - GamepadState.AxisCenter;
        if (Math.Abs(offset) <= threshold)
        {
            return 0;
        }

        return Sign(offset);
    }

    private static double FrameDeltaMs(AxisState axis, long nowTicks)
    {
        long reference = axis.LastRawTicks != 0 ? axis.LastRawTicks : axis.LastOutputTicks;
        double frameDeltaMs = TicksToMilliseconds(nowTicks - reference);
        return frameDeltaMs <= 0 ? 1000.0 / 65.0 : frameDeltaMs;
    }

    private static double StickVectorDelta(string axisName, GamepadState previous, GamepadState current)
    {
        if (axisName.StartsWith("left_", StringComparison.Ordinal))
        {
            return VectorDelta(previous.Lx, previous.Ly, current.Lx, current.Ly);
        }

        return VectorDelta(previous.Rx, previous.Ry, current.Rx, current.Ry);
    }

    private static double VectorDelta(ushort ax, ushort ay, ushort bx, ushort by)
    {
        int dx = ax - bx;
        int dy = ay - by;
        return Math.Sqrt(dx * dx + dy * dy);
    }

    private static ushort StepToward(ushort from, ushort to, int maxStep)
    {
        int delta = to - from;
        if (Math.Abs(delta) <= maxStep)
        {
            return to;
        }

        return ClampAxis(from + Math.Sign(delta) * maxStep);
    }

    private static ushort ClampAxis(int value)
    {
        if (value < 0) return 0;
        if (value > GamepadState.AxisMax) return GamepadState.AxisMax;
        return (ushort)value;
    }

    private static int AxisDelta(ushort a, ushort b)
    {
        int delta = a - b;
        return delta < 0 ? -delta : delta;
    }

    private static int Sign(int value)
    {
        if (value > 0) return 1;
        if (value < 0) return -1;
        return 0;
    }

    private static double TicksToMilliseconds(long ticks)
    {
        return ticks <= 0 ? 0 : ticks * 1000.0 / Stopwatch.Frequency;
    }

    private sealed class AxisState
    {
        public AxisState(string name)
        {
            Name = name;
        }

        public string Name { get; }
        public bool Initialized { get; private set; }
        public ushort LastGoodValue { get; private set; } = GamepadState.AxisCenter;
        public ushort LastRawValue { get; set; } = GamepadState.AxisCenter;
        public ushort CandidateBaseValue { get; private set; } = GamepadState.AxisCenter;
        public ushort CandidateLastValue { get; private set; } = GamepadState.AxisCenter;
        public long CandidateStartTicks { get; private set; }
        public int CandidateFrameCount { get; private set; }
        public int CandidateDirection { get; private set; }
        public long LastAcceptTicks { get; private set; }
        public long LastRejectTicks { get; private set; }
        public long LastOutputTicks { get; private set; }
        public long LastRawTicks { get; private set; }
        public long LastMotionTicks { get; private set; }
        public bool SuspectActive { get; private set; }

        public void Initialize(ushort value, long nowTicks)
        {
            Initialized = true;
            LastGoodValue = value;
            LastRawValue = value;
            CandidateBaseValue = value;
            CandidateLastValue = value;
            CandidateStartTicks = 0;
            CandidateFrameCount = 0;
            CandidateDirection = 0;
            LastAcceptTicks = nowTicks;
            LastOutputTicks = nowTicks;
            LastRawTicks = nowTicks;
            LastRejectTicks = 0;
            LastMotionTicks = 0;
            SuspectActive = false;
        }

        public void Reset()
        {
            Initialized = false;
            LastGoodValue = GamepadState.AxisCenter;
            LastRawValue = GamepadState.AxisCenter;
            CandidateBaseValue = GamepadState.AxisCenter;
            CandidateLastValue = GamepadState.AxisCenter;
            CandidateStartTicks = 0;
            CandidateFrameCount = 0;
            CandidateDirection = 0;
            LastAcceptTicks = 0;
            LastRejectTicks = 0;
            LastOutputTicks = 0;
            LastRawTicks = 0;
            LastMotionTicks = 0;
            SuspectActive = false;
        }

        public void Accept(ushort value, long nowTicks, int motionThreshold)
        {
            if (AxisDelta(LastGoodValue, value) >= motionThreshold)
            {
                LastMotionTicks = nowTicks;
            }

            LastGoodValue = value;
            LastRawValue = value;
            LastAcceptTicks = nowTicks;
            LastOutputTicks = nowTicks;
            LastRawTicks = nowTicks;
        }

        public void Hold(long nowTicks)
        {
            LastOutputTicks = nowTicks;
            LastRawTicks = nowTicks;
        }

        public void StartCandidate(ushort value, ushort baseValue, long nowTicks)
        {
            SuspectActive = true;
            CandidateBaseValue = baseValue;
            CandidateLastValue = value;
            CandidateStartTicks = nowTicks;
            CandidateFrameCount = 1;
            CandidateDirection = Sign(value - baseValue);
            LastRawValue = value;
            LastRawTicks = nowTicks;
        }

        public void UpdateCandidate(ushort value)
        {
            CandidateLastValue = value;
            CandidateFrameCount++;
            LastRawValue = value;
        }

        public void KeepCandidate(ushort value, long nowTicks)
        {
            SuspectActive = true;
            CandidateBaseValue = LastGoodValue;
            CandidateLastValue = value;
            LastRawValue = value;
            LastRawTicks = nowTicks;
            if (CandidateStartTicks == 0)
            {
                CandidateStartTicks = nowTicks;
                CandidateFrameCount = 1;
            }
            else
            {
                CandidateFrameCount++;
            }
            CandidateDirection = Sign(value - LastGoodValue);
        }

        public void ClearCandidate()
        {
            SuspectActive = false;
            CandidateStartTicks = 0;
            CandidateFrameCount = 0;
            CandidateDirection = 0;
            CandidateBaseValue = LastGoodValue;
            CandidateLastValue = LastGoodValue;
        }
    }
}
