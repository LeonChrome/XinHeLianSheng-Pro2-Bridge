namespace Y700Switch2V60Viiper;

public sealed partial class Pro2BleInputSource
{
    private readonly object connectionEvidenceGate = new();
    private BleConnectionAttemptEvidence? connectionEvidence, lastConnectionFailure;
    private readonly Queue<string> connectionAttemptHistory = new();
    public BleConnectionDiagnosis ConnectionDiagnosis
    {
        get { lock (connectionEvidenceGate) return BleConnectionDiagnosis.Evaluate(
            connectionEvidence?.Outcome == "running" ? lastConnectionFailure : connectionEvidence); }
    }
    public string ConnectionDiagnosticText
    {
        get
        {
            lock (connectionEvidenceGate)
            {
                var d = ConnectionDiagnosis;
                return "连接诊断（缓存；不发送 GATT 查询）\n" +
                    (d.Visible ? d.Title + "\n" + d.Advice + "\n" + d.Evidence : "当前阶段：" +
                        (connectionEvidence?.Stage ?? "尚未尝试") + " / " + (connectionEvidence?.Outcome ?? "none") +
                        "\n" + connectionEvidence?.ScanTrace + connectionEvidence?.CandidateTrace) +
                    "\n最近尝试：\n" + string.Join("\n", connectionAttemptHistory) +
                    "\n驱动版本和日期仅作环境信息；未从版本号推断蓝牙协议或宣称驱动过旧。\n";
            }
        }
    }
    private void UpdateConnectionEvidence(Func<BleConnectionAttemptEvidence, BleConnectionAttemptEvidence> update)
    { lock (connectionEvidenceGate) connectionEvidence = update(connectionEvidence ?? new()); }
    private void RecordConnectionFailure(string result, string detail = "") =>
        UpdateConnectionEvidence(e => e with { Outcome = result, Error = detail.Length > 0 ? detail : e.Error });
    private void ConnectionStage(string stage, string? address = null) =>
        UpdateConnectionEvidence(e => e with { Stage = stage, Address = address ?? e.Address, Outcome = "running", Error = "" });
    private void FinishConnectionEvidence(bool cancelled, IProgress<string> progress)
    {
        lock (connectionEvidenceGate)
        {
            var e = connectionEvidence ?? new();
            e = e with { Outcome = cancelled ? "cancelled" : IsRunning ? "connected" : e.Outcome == "running" ? "operation_failed" : e.Outcome };
            connectionEvidence = e;
            if (e.Outcome == "connected" || cancelled) lastConnectionFailure = null;
            else if (e.Outcome != "gatt_draining") lastConnectionFailure = e;
            string line = $"{e.StartedAt:HH:mm:ss} address={e.Address} stage={e.Stage} result={e.Outcome} detail={e.Error}";
            connectionAttemptHistory.Enqueue(line);
            while (connectionAttemptHistory.Count > 8) connectionAttemptHistory.Dequeue();
            progress.Report("[BLE_CONNECTION_DIAG] " + line + " scan_error=" + e.ScanError + " ads=" + e.Advertisements);
        }
    }
}
