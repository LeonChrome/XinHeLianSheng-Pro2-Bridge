using System;
using System.Collections.Generic;
using System.Linq;
using System.IO;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace Y700Switch2V60Viiper;

public sealed partial class Pro2BleInputSource
{
    private BleAdaptiveDiagnosticSnapshot adaptiveDiagnosticSnapshot = BleAdaptiveDiagnosticSnapshot.None();
    private sealed class AdaptiveDiagnosticCapture
    {
        public BleAdaptiveDiagnosticSnapshot Snapshot = new(DateTimeOffset.Now, "running", "measuring_native",
            null, null, false, null, Array.Empty<BleAdaptiveTrialRecord>(), Array.Empty<BleAdaptiveRequestEvidence>());
        public readonly List<BleAdaptiveRequestEvidence> Requests = new();
        public readonly List<BleTrialMeasurement> Measurements = new();
    }
    private AdaptiveDiagnosticCapture? activeAdaptiveDiagnosticCapture;
    private BleAdaptiveDiagnosticSnapshot? historicalAdaptiveSnapshot;
    private bool adaptiveHistoryLoaded;
    private string adaptiveHistoryWarning = "";

    /// <summary>Cached trial facts and optional local history only; no GATT calls or new test.</summary>
    public string GetAdaptiveDiagnosticText(bool includeHistory = true)
    {
        BleAdaptiveDiagnosticSnapshot current;
        bool loadHistory;
        lock (gate)
        {
            current = adaptiveDiagnosticSnapshot;
            loadHistory = current.CapturedAt == null && includeHistory && !adaptiveHistoryLoaded;
        }
        if (loadHistory)
        {
            // No disk I/O under the input callback lock. A concurrent reset/run
            // may already have supplied newer in-memory history while reading.
            var stored = BleAdaptiveDiagnosticStore.LoadLatest(out string readWarning);
            lock (gate)
            {
                if (!adaptiveHistoryLoaded)
                { historicalAdaptiveSnapshot = stored; adaptiveHistoryWarning = readWarning; adaptiveHistoryLoaded = true; }
            }
        }
        BleAdaptiveDiagnosticSnapshot? history;
        string warning;
        lock (gate) { current = adaptiveDiagnosticSnapshot; history = historicalAdaptiveSnapshot; warning = adaptiveHistoryWarning; }
        string text = BleAdaptiveDiagnostics.Format(current);
        if (current.CapturedAt != null || !includeHistory) return text;
        if (history != null)
            text += "\n上一轮已保存记录（历史，不代表当前连接或当前电脑配置）\n" +
                BleAdaptiveDiagnostics.Format(history);
        if (warning.Length > 0) text += "\n" + warning + "\n";
        return text;
    }

    private void ResetAdaptiveDiagnosticEvidenceNoLock()
    {
        if (adaptiveDiagnosticSnapshot.CapturedAt != null)
        {
            historicalAdaptiveSnapshot = adaptiveDiagnosticSnapshot with { State = "historical" };
            adaptiveHistoryLoaded = true;
        }
        adaptiveDiagnosticSnapshot = BleAdaptiveDiagnosticSnapshot.None();
        // A cancelled run still owns its independent capture and can save its
        // partial evidence after this connection is released. Never attach it
        // to a new connection's current snapshot.
        activeAdaptiveDiagnosticCapture = null;
    }

    private AdaptiveDiagnosticCapture StartAdaptiveDiagnosticEvidence()
    {
        lock (gate)
        {
            var capture = new AdaptiveDiagnosticCapture();
            activeAdaptiveDiagnosticCapture = capture;
            adaptiveDiagnosticSnapshot = capture.Snapshot;
            return capture;
        }
    }

    // Caller holds gate. Publishing an old run may update history but cannot
    // replace the current evidence of another connection.
    private void PublishAdaptiveEvidenceNoLock(AdaptiveDiagnosticCapture capture)
    {
        if (ReferenceEquals(activeAdaptiveDiagnosticCapture, capture))
            adaptiveDiagnosticSnapshot = capture.Snapshot;
        else if (historicalAdaptiveSnapshot?.CapturedAt is not { } oldTime || capture.Snapshot.CapturedAt >= oldTime)
        {
            historicalAdaptiveSnapshot = capture.Snapshot with { State = "historical" };
            adaptiveHistoryLoaded = true;
        }
    }

    private void RecordAdaptiveRequest(AdaptiveDiagnosticCapture capture, BleAdaptiveRequestEvidence request)
    {
        lock (gate)
        {
            capture.Requests.Add(request);
            capture.Snapshot = capture.Snapshot with { Requests = capture.Requests.ToArray() };
            PublishAdaptiveEvidenceNoLock(capture);
        }
    }

    private void RecordAdaptiveStage(AdaptiveDiagnosticCapture capture, string stage)
    {
        lock (gate)
        {
            capture.Snapshot = capture.Snapshot with { FinalCode = "running", Stage = stage };
            PublishAdaptiveEvidenceNoLock(capture);
        }
    }

    private void RecordAdaptiveMeasurement(AdaptiveDiagnosticCapture capture, BleTrialMeasurement measured)
    {
        lock (gate)
        {
            capture.Measurements.Add(measured);
            capture.Snapshot = capture.Snapshot with
            {
                Native = measured.Phase is "AUTO_NATIVE" or "AUTO_NATIVE_RETRY"
                    ? new(ToMeasuredTrialSample(measured, measured.ConnectedAtEnd), MaximumGapMs: measured.Gaps.MaxMs)
                    : capture.Snapshot.Native,
                Windows = capture.Measurements.ToArray()
            };
            PublishAdaptiveEvidenceNoLock(capture);
        }
    }

    private BleAdaptiveDiagnosticSnapshot CompleteAdaptiveDiagnosticEvidence(AdaptiveDiagnosticCapture capture, BleAdaptiveRunResult result)
    {
        lock (gate)
        {
            string lastStage = capture.Snapshot.Stage;
            capture.Snapshot = BleAdaptiveDiagnosticSnapshot.FromRun(result,
                capture.Requests.ToArray(), capturedAt: capture.Snapshot.CapturedAt,
                windows: capture.Measurements.ToArray()) with { Stage = lastStage };
            PublishAdaptiveEvidenceNoLock(capture);
            return capture.Snapshot;
        }
    }

    private void MarkAdaptiveDiagnosticInterrupted(AdaptiveDiagnosticCapture capture, string code)
    {
        lock (gate)
        {
            capture.Snapshot = capture.Snapshot with { State = "interrupted", FinalCode = code };
            PublishAdaptiveEvidenceNoLock(capture);
        }
    }

    private async Task SaveAdaptiveDiagnosticEvidenceAsync(AdaptiveDiagnosticCapture capture, BleAdaptiveEnvironmentKey? environment,
        IProgress<string> progress)
    {
        BleAdaptiveDiagnosticSnapshot snapshot;
        lock (gate) snapshot = capture.Snapshot;
        if (snapshot.CapturedAt == null) return;
        try
        {
            string directory = BleAdaptiveDiagnosticStore.ReportDirectory;
            Directory.CreateDirectory(directory);
            string path = Path.Combine(directory, "presets_" + snapshot.CapturedAt.Value.ToLocalTime().ToString("yyyyMMdd_HHmmss_fff") +
                "_" + Guid.NewGuid().ToString("N")[..8] + ".json");
            await File.WriteAllTextAsync(path, JsonSerializer.Serialize(new
            {
                schema = 2, appVersion = SystemBluetoothDiagnostics.CurrentAppVersion, environment,
                diagnostics = snapshot,
                longTermStabilityProven = false
            }, BleAdaptiveDiagnosticStore.JsonOptions), CancellationToken.None);
            progress.Report("[BLE_AUTO_PRESET_RESULT] report=\"" + path + "\" detailed_requests=true cache_read_only=true");
        }
        catch (Exception ex)
        { progress.Report("[BLE_AUTO_PRESET] result_save_error=" + ex.GetType().Name + " in_memory_evidence_retained=true"); }
    }
}
