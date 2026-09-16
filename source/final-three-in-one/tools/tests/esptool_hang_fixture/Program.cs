using System.Diagnostics;

if (args.Contains("--fixture-child", StringComparer.Ordinal))
{
    await Task.Delay(Timeout.InfiniteTimeSpan);
    return;
}

string executable = Environment.ProcessPath ??
    throw new InvalidOperationException("Fixture executable path is unavailable.");
using Process child = Process.Start(new ProcessStartInfo(executable)
{
    UseShellExecute = false,
    CreateNoWindow = true,
    ArgumentList = { "--fixture-child" }
}) ?? throw new InvalidOperationException("Unable to start fixture child.");

Console.WriteLine("fixture parent=" + Environment.ProcessId + " child=" + child.Id);
await Task.Delay(Timeout.InfiniteTimeSpan);
