using System;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;

namespace Y700Switch2V60Viiper;

public sealed class Pro2ControllerSlot : INotifyPropertyChanged
{
    private bool enabled;
    private bool autoReconnectEnabled;
    private bool virtualDeviceRunning;
    private string status;
    private string connectedAddress = "";
    private string stickCalibrationStatus = "摇杆校准：连接手柄后可执行零位或完整行程校准。";

    public Pro2ControllerSlot(int index, bool enabled)
    {
        Index = index;
        Name = "Pro2 Slot " + index;
        this.enabled = enabled;
        status = enabled
            ? "已启用，等待进入游戏。"
            : "未启用。";
        InputSource = new Pro2BleInputSource();
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public int Index { get; }
    public string Name { get; }
    public Pro2BleInputSource InputSource { get; }
    public ViiperBridgeSession? Session { get; set; }
    public CancellationTokenSource? AutoReconnectCts { get; set; }
    public Task? AutoReconnectTask { get; set; }
    public string AppliedStickCalibrationKey { get; set; } = "";

    public bool Enabled
    {
        get => enabled;
        set
        {
            if (enabled == value)
            {
                return;
            }

            enabled = value;
            Status = enabled
                ? "已启用，等待进入游戏。"
                : "未启用。";
            OnPropertyChanged();
            OnPropertyChanged(nameof(Headline));
        }
    }

    public bool AutoReconnectEnabled
    {
        get => autoReconnectEnabled;
        set
        {
            if (autoReconnectEnabled == value)
            {
                return;
            }

            autoReconnectEnabled = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(Headline));
        }
    }

    public bool VirtualDeviceRunning
    {
        get => virtualDeviceRunning;
        set
        {
            if (virtualDeviceRunning == value)
            {
                return;
            }

            virtualDeviceRunning = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(Headline));
        }
    }

    public string ConnectedAddress
    {
        get => connectedAddress;
        set
        {
            string normalized = value ?? "";
            if (connectedAddress == normalized)
            {
                return;
            }

            connectedAddress = normalized;
            OnPropertyChanged();
            OnPropertyChanged(nameof(Headline));
        }
    }

    public string Status
    {
        get => status;
        set
        {
            string normalized = value ?? "";
            if (status == normalized)
            {
                return;
            }

            status = normalized;
            OnPropertyChanged();
        }
    }

    public string StickCalibrationStatus
    {
        get => stickCalibrationStatus;
        set
        {
            string normalized = value ?? "";
            if (stickCalibrationStatus == normalized)
            {
                return;
            }

            stickCalibrationStatus = normalized;
            OnPropertyChanged();
        }
    }

    public string Headline =>
        Name +
        (Enabled ? " · ON" : " · OFF") +
        (VirtualDeviceRunning ? " · USB" : "") +
        (InputSource.IsRunning ? " · BLE " + InputSource.LinkRateClass : "") +
        (AutoReconnectEnabled ? " · AUTO" : "") +
        (string.IsNullOrWhiteSpace(ConnectedAddress) ? "" : " · " + ConnectedAddress);

    public void RefreshFromSource()
    {
        ConnectedAddress = InputSource.ConnectedAddress;
        Status = InputSource.Status;
        OnPropertyChanged(nameof(Headline));
    }

    public async Task StopAutoReconnectAsync()
    {
        CancellationTokenSource? cts = AutoReconnectCts;
        Task? task = AutoReconnectTask;
        AutoReconnectCts = null;
        AutoReconnectTask = null;
        AutoReconnectEnabled = false;
        if (cts != null)
        {
            cts.Cancel();
        }

        if (task != null)
        {
            try
            {
                await task;
            }
            catch (OperationCanceledException)
            {
            }
        }

        cts?.Dispose();
    }

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }
}
