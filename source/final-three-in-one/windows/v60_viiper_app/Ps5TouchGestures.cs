using System;
using System.Collections.Generic;

namespace Y700Switch2V60Viiper;

// Separate from button bits: a finger trajectory is not a touchpad click.
public enum Ps5TouchGesture { None, Touch, SwipeUp, SwipeDown, SwipeLeft, SwipeRight, CircleClockwise, CircleCounterClockwise }

public readonly record struct Ps5TouchPoint(bool Active, ushort X, ushort Y);
public sealed record Ps5TouchGestureChoice(Ps5TouchGesture Gesture, string Label);

public static class Ps5TouchGestures
{
    public const int MinDurationMs = 180;
    public const int DefaultDurationMs = 400;
    public const ushort MaxX = 1919, MaxY = 1079;
    public static IReadOnlyList<Ps5TouchGestureChoice> Choices { get; } =
    [
        new(Ps5TouchGesture.None, "无"), new(Ps5TouchGesture.Touch, "中央轻触"),
        new(Ps5TouchGesture.SwipeUp, "向上滑动"), new(Ps5TouchGesture.SwipeDown, "向下滑动"),
        new(Ps5TouchGesture.SwipeLeft, "向左滑动"), new(Ps5TouchGesture.SwipeRight, "向右滑动"),
        new(Ps5TouchGesture.CircleClockwise, "顺时针画圈"), new(Ps5TouchGesture.CircleCounterClockwise, "逆时针画圈")
    ];
    public static Ps5TouchPoint PointAt(Ps5TouchGesture gesture, double progress)
    {
        double p = double.IsFinite(progress) ? Math.Clamp(progress, 0, 1) : 0;
        double x = 960, y = 540;
        switch (gesture)
        {
            case Ps5TouchGesture.SwipeUp: y = 900 - 720 * p; break;
            case Ps5TouchGesture.SwipeDown: y = 180 + 720 * p; break;
            case Ps5TouchGesture.SwipeLeft: x = 1660 - 1400 * p; break;
            case Ps5TouchGesture.SwipeRight: x = 260 + 1400 * p; break;
            case Ps5TouchGesture.CircleClockwise:
            case Ps5TouchGesture.CircleCounterClockwise:
                // Screen coordinates: +Y is down, hence positive angle is clockwise.
                double angle = -Math.PI / 2 + (gesture == Ps5TouchGesture.CircleClockwise ? 1 : -1) * 2 * Math.PI * p;
                x += 460 * Math.Cos(angle); y += 340 * Math.Sin(angle); break;
        }
        return new(gesture != Ps5TouchGesture.None && Enum.IsDefined(gesture),
            (ushort)Math.Clamp((int)Math.Round(x), 0, MaxX), (ushort)Math.Clamp((int)Math.Round(y), 0, MaxY));
    }
}

internal readonly record struct Ps5TouchFrame(Ps5TouchPoint Point, bool Started = false);

/// <summary>Fresh-source-frame playback, with explicit endpoint and finger-up reports.
/// No timer, background queue or replay of missed gestures.</summary>
internal sealed class Ps5TouchPlayback
{
    private Ps5TouchGesture gesture;
    private long pulse, completedPulse;
    private bool hasCompleted;
    private double progress;
    private Ps5TouchPoint point;
    public bool HasPending => point.Active;
    public void Reset() { gesture = Ps5TouchGesture.None; pulse = completedPulse = 0; hasCompleted = false; progress = 0; point = default; }
    public Ps5TouchFrame Cancel()
    {
        if (point.Active) { completedPulse = pulse; hasCompleted = true; }
        point = point with { Active = false };
        return new(point);
    }
    public Ps5TouchFrame Sample(Ps5TouchGesture requested, long token, double fraction)
    {
        if (point.Active)
        {
            if (requested == gesture && token == pulse)
            {
                progress = Math.Max(progress, Math.Clamp(fraction, 0, 1));
                point = Ps5TouchGestures.PointAt(gesture, progress);
                return new(point);
            }
            // A short/late source window still exposes the endpoint, then a lift.
            // A new gesture may only start after that lift, never mid-path.
            if (progress < 1) { progress = 1; point = Ps5TouchGestures.PointAt(gesture, 1); return new(point); }
            return Cancel();
        }
        if (requested == Ps5TouchGesture.None || (hasCompleted && token == completedPulse)) return new(point);
        gesture = requested; pulse = token; progress = 0;
        point = Ps5TouchGestures.PointAt(gesture, 0);
        return new(point, Started: true);
    }
}
