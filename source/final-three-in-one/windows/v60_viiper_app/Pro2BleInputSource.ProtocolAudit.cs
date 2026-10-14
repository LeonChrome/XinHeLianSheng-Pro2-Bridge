using System;
using System.Diagnostics;
using Windows.Devices.Bluetooth.GenericAttributeProfile;

namespace Y700Switch2V60Viiper;

public sealed partial class Pro2BleInputSource
{
    private readonly BleProtocolSessionAudit protocolAudit = new();
    private volatile bool skipUsbInitialization;
    private bool sessionSkipUsbInitialization;
    public bool SkipUsbInitializationOnNextConnect { get => skipUsbInitialization; set => skipUsbInitialization = value; }
    public string ProtocolDiagnosticText => protocolAudit.DiagnosticText(Stopwatch.GetTimestamp());

    private void BeginProtocolAudit(IProgress<string> progress)
    {
        sessionSkipUsbInitialization = skipUsbInitialization || Array.Exists(Environment.GetCommandLineArgs(),
            item => item.Equals("--ble-skip-usb-init", StringComparison.OrdinalIgnoreCase));
        protocolAudit.Begin(sessionSkipUsbInitialization, Stopwatch.GetTimestamp());
        progress.Report("[BLE_INIT_POLICY] profile=" + BleInitAuditPolicy.Profile(sessionSkipUsbInitialization) +
            " pairing_finalize_15_03=blocked usb_initialize_03_0d=" + (sessionSkipUsbInitialization ? "skipped" : "retained") +
            " policy_frozen_until_reconnect=true fd2_request_hz=133 fd2_report=0x05 init_selected_report=0x09_unchanged system_writes=false");
    }

    private void AuditConnectionActive(string observer) => protocolAudit.Connected(observer, Stopwatch.GetTimestamp());
    private void AuditControl(string detail)
    {
        protocolAudit.Note(detail, Stopwatch.GetTimestamp());
        connectionProgress?.Report("[BLE_CONTROL] " + detail);
    }
    private void AuditSessionEnd(string reason)
    {
        string summary = protocolAudit.End(reason, Stopwatch.GetTimestamp());
        if (summary.Length > 0) connectionProgress?.Report(summary);
    }
    private void AuditPdu(GattSession session)
    {
        try
        {
            protocolAudit.SetPdu(session.MaxPduSize);
            AuditControl("winrt_max_pdu=" + session.MaxPduSize + " scope=Windows_GATT_not_LL_data_length");
        }
        catch (Exception ex) { AuditControl("max_pdu_unavailable=" + ex.GetType().Name); }
    }
    private void OnGattMaxPduSizeChanged(GattSession sender, object args)
    {
        if (ReferenceEquals(sender, gattSession)) AuditPdu(sender);
    }
}
