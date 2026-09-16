using System;
using System.Collections.Generic;

namespace Y700Switch2V60Viiper;

// Keeps arrival order for diagnostics, while the real-time output path drains
// to the newest parsed state so Windows BLE batching cannot be replayed later.
public sealed class Pro2SequentialInputQueue
{
    private readonly Queue<Entry> frames = new();
    private readonly int capacity;

    public Pro2SequentialInputQueue(int capacity = 64)
    {
        if (capacity < 2)
        {
            throw new ArgumentOutOfRangeException(nameof(capacity));
        }
        this.capacity = capacity;
    }

    public int Count => frames.Count;
    public int MaximumDepth { get; private set; }
    public ulong EnqueuedCount { get; private set; }
    public ulong DequeuedCount { get; private set; }
    public ulong OverflowDropCount { get; private set; }
    public ulong RealtimeSupersededCount { get; private set; }

    public void Enqueue(GamepadState state, long arrivalTicks)
    {
        if (frames.Count >= capacity)
        {
            frames.Dequeue();
            OverflowDropCount++;
        }

        frames.Enqueue(new Entry(state.Clone(), arrivalTicks));
        EnqueuedCount++;
        MaximumDepth = Math.Max(MaximumDepth, frames.Count);
    }

    public bool TryDequeue(out GamepadState state, out long arrivalTicks)
    {
        if (!frames.TryDequeue(out Entry entry))
        {
            state = GamepadState.Neutral();
            arrivalTicks = 0;
            return false;
        }

        state = entry.State;
        arrivalTicks = entry.ArrivalTicks;
        DequeuedCount++;
        return true;
    }

    public bool TryDequeueNewest(
        out GamepadState state,
        out long arrivalTicks,
        out int supersededCount)
    {
        supersededCount = 0;
        if (frames.Count == 0)
        {
            state = GamepadState.Neutral();
            arrivalTicks = 0;
            return false;
        }

        while (frames.Count > 1)
        {
            frames.Dequeue();
            DequeuedCount++;
            RealtimeSupersededCount++;
            supersededCount++;
        }

        Entry newest = frames.Dequeue();
        DequeuedCount++;
        state = newest.State;
        arrivalTicks = newest.ArrivalTicks;
        return true;
    }

    public void Reset()
    {
        frames.Clear();
        MaximumDepth = 0;
        EnqueuedCount = 0;
        DequeuedCount = 0;
        OverflowDropCount = 0;
        RealtimeSupersededCount = 0;
    }

    public void ResetTo(GamepadState state, long arrivalTicks)
    {
        Reset();
        Enqueue(state, arrivalTicks);
    }

    private readonly record struct Entry(GamepadState State, long ArrivalTicks);
}
