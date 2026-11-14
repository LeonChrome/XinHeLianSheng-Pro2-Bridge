using System.Diagnostics;
using System.Globalization;

namespace Y700Switch2V60Viiper;

// A total order of logger acceptance, not a claimed total order of radio events.
public sealed class BleOrderedDiagnosticLog(Action<string> write)
{
    private readonly object gate = new();
    private long sequence;
    public void Report(string line)
    {
        lock (gate)
        {
            string prefix="["+DateTime.Now.ToString("HH:mm:ss.fff",CultureInfo.InvariantCulture)+"] "+
                "[CLI_ORDER seq="+(++sequence).ToString(CultureInfo.InvariantCulture)+
                " qpc="+Stopwatch.GetTimestamp().ToString(CultureInfo.InvariantCulture)+"] ";
            write(prefix+line);
        }
    }
}
