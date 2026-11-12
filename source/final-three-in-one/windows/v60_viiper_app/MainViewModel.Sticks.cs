using System;
using System.Windows.Input;

namespace Y700Switch2V60Viiper;

public sealed partial class MainViewModel
{
    private double leftStickDeadzone, rightStickDeadzone;
    private string stickDeadzoneStatus = "";
    public double LeftStickDeadzone { get => leftStickDeadzone; set { leftStickDeadzone = StickDeadzoneSettings.Normalize(value); OnPropertyChanged(); OnPropertyChanged(nameof(LeftStickDeadzoneText)); StickDeadzoneStatus = "未保存"; } }
    public double RightStickDeadzone { get => rightStickDeadzone; set { rightStickDeadzone = StickDeadzoneSettings.Normalize(value); OnPropertyChanged(); OnPropertyChanged(nameof(RightStickDeadzoneText)); StickDeadzoneStatus = "未保存"; } }
    public string LeftStickDeadzoneText => leftStickDeadzone.ToString("0.0") + "%";
    public string RightStickDeadzoneText => rightStickDeadzone.ToString("0.0") + "%";
    public string StickDeadzoneStatus { get => stickDeadzoneStatus; private set { stickDeadzoneStatus = value; OnPropertyChanged(); } }
    public ICommand SaveStickDeadzonesCommand { get; private set; } = null!;
    public ICommand ResetStickDeadzonesCommand { get; private set; } = null!;
    private void InitializeStickDeadzones()
    {
        var settings = (userSettings.StickDeadzones ?? new()).Normalize();
        leftStickDeadzone = settings.LeftPercent; rightStickDeadzone = settings.RightPercent;
        foreach (var slot in pro2Slots) slot.InputSource.SetStickDeadzones(settings);
        SaveStickDeadzonesCommand = new RelayCommand(_ => SaveStickDeadzones());
        ResetStickDeadzonesCommand = new RelayCommand(_ => { LeftStickDeadzone = 4; RightStickDeadzone = 4; });
    }
    private void SaveStickDeadzones()
    {
        var next = new StickDeadzoneSettings(leftStickDeadzone, rightStickDeadzone).Normalize();
        var previous = userSettings.StickDeadzones;
        try { userSettings.StickDeadzones = next; if (!previewOnly) userSettings.Save(); }
        catch (Exception ex) { userSettings.StickDeadzones = previous; StickDeadzoneStatus = "保存失败：" + FirstLine(ex.Message); return; }
        foreach (var slot in pro2Slots) slot.InputSource.SetStickDeadzones(next);
        StickDeadzoneStatus = "已保存并应用 · 四种模式共用";
        AppendLog("[STICK_DEADZONE] " + next.Telemetry);
    }
}
