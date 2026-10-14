using System;

namespace Y700Switch2V60Viiper;

public sealed partial class MainViewModel
{
    private bool skipUsbBleInitialization;
    public bool SkipUsbBleInitialization
    {
        get => skipUsbBleInitialization;
        set
        {
            if (skipUsbBleInitialization == value) return;
            skipUsbBleInitialization = value;
            foreach (Pro2ControllerSlot slot in pro2Slots)
                slot.InputSource.SkipUsbInitializationOnNextConnect = value;
            AppendLog("[BLE_INIT_UI] next_profile=" + BleInitAuditPolicy.Profile(value) +
                " active_link_unchanged=true system_writes=false");
            OnPropertyChanged();
            OnPropertyChanged(nameof(EnvironmentReportText));
        }
    }
}
