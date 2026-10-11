using System;

namespace Y700Switch2V60Viiper;

// Only use with thread-safe sinks, never with controls or UI state setters.
public sealed class DirectProgress<T>(Action<T> report) : IProgress<T>
{
    public void Report(T value) => report(value);
}
