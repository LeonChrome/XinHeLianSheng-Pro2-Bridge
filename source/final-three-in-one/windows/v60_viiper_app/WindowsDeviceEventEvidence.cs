using System.Diagnostics;
using System.IO;
using System.Text;
using System.Xml.Linq;

namespace Y700Switch2V60Viiper;

public static class WindowsDeviceEventEvidence
{
    private const string Query = "*[System[(Level=1 or Level=2 or Level=3) and TimeCreated[timediff(@SystemTime) <= 86400000] and (Provider[@Name='BTHUSB'] or Provider[@Name='BTHMINI'] or Provider[@Name='Microsoft-Windows-Kernel-PnP'] or Provider[@Name='Microsoft-Windows-UserPnp'] or Provider[@Name='USBIP'] or Provider[@Name='usbip_vhci'] or Provider[@Name='usbip_ude'])]]";
    public static async Task<string> CaptureAsync(CancellationToken token)
    {
        try
        {
            var info = new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, "wevtutil.exe")) {
                UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true,
                StandardOutputEncoding = Encoding.Unicode, StandardErrorEncoding = Encoding.Unicode
            };
            foreach (string arg in new[] { "qe", "System", "/q:" + Query, "/rd:true", "/c:20", "/f:xml", "/uni:true" }) info.ArgumentList.Add(arg);
            using var child = Process.Start(info) ?? throw new InvalidOperationException("无法启动系统日志读取器");
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
            timeout.CancelAfter(TimeSpan.FromSeconds(6));
            try
            {
                var stdout = child.StandardOutput.ReadToEndAsync(timeout.Token);
                var stderr = child.StandardError.ReadToEndAsync(timeout.Token);
                await child.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
                string xml = await stdout.ConfigureAwait(false), error = await stderr.ConfigureAwait(false);
                return child.ExitCode == 0 ? Format(xml) : "相关系统设备事件：读取失败（exit=" + child.ExitCode + "）" + error.Trim();
            }
            finally { try { if (!child.HasExited) child.Kill(entireProcessTree: true); } catch { } }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
        catch (Exception ex) { return "相关系统设备事件：未获取（" + ex.GetType().Name + "）"; }
    }

    public static string Format(string xml)
    {
        if (xml.Length > 262144) return "相关系统设备事件：返回过大，未解析";
        if (string.IsNullOrWhiteSpace(xml)) return "相关系统设备事件：过去 24 小时未查到匹配的警告或错误；不能据此证明连接正常。";
        var root = XDocument.Parse("<Events>" + xml.Trim().TrimStart('\uFEFF') + "</Events>");
        XNamespace ns = "http://schemas.microsoft.com/win/2004/08/events/event";
        var lines = new List<string>();
        foreach (var e in root.Descendants(ns + "Event").Take(20))
        {
            var sys = e.Element(ns + "System");
            string provider = (string?)sys?.Element(ns + "Provider")?.Attribute("Name") ?? "";
            // Defense in depth: never include Security auditing / credentials / full XML dumps.
            if (provider != "BTHUSB" && provider != "BTHMINI" && provider != "Microsoft-Windows-Kernel-PnP" &&
                provider != "Microsoft-Windows-UserPnp" && provider != "USBIP" && provider != "usbip_vhci" && provider != "usbip_ude") continue;
            string data = string.Join("; ", e.Element(ns + "EventData")?.Elements().Take(12).Select(d =>
                ((string?)d.Attribute("Name") ?? "值") + "=" + d.Value.Replace('\r', ' ').Replace('\n', ' ')) ?? []);
            if (data.Length > 600) data = data[..600] + "…";
            string stamp = (string?)sys?.Element(ns + "TimeCreated")?.Attribute("SystemTime") ?? "";
            if (DateTimeOffset.TryParse(stamp, out var time)) stamp = time.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss zzz");
            lines.Add(stamp + " · " + provider + " · ID " + sys?.Element(ns + "EventID")?.Value + " · " + data);
        }
        return "相关系统设备事件（过去 24 小时，最近至多 20 条，只读）\n" +
            (lines.Count == 0 ? "未查到匹配记录。" : string.Join("\n", lines)) +
            "\n以上是系统事件，可能属于其他设备；需与本次连接的时间、设备 ID 核对，不能单独认定断联原因。";
    }
}
