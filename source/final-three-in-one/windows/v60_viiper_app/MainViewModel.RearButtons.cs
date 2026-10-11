using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Windows.Input;

namespace Y700Switch2V60Viiper;

public sealed partial class MainViewModel
{
    private readonly Dictionary<string, ObservableCollection<RearButtonEditor>> rearEditors = new();
    private readonly HashSet<string> rearDirtyModes = new();
    private string rearEditingMode = "Xbox", rearSettingsStatus = "";
    private int selectedRearIndex;
    public IReadOnlyList<string> RearModes { get; } = ["Xbox", "PS5", "Pro1"];
    public string RearEditingMode
    {
        get => rearEditingMode;
        set { if (!RearModes.Contains(value) || rearEditingMode == value) return; rearEditingMode = value; RearSettingsStatus = ""; RefreshRearSelection(); OnPropertyChanged();
            OnPropertyChanged(nameof(RearEditors)); OnPropertyChanged(nameof(SelectedRearIndex)); OnPropertyChanged(nameof(SelectedRearEditor)); OnPropertyChanged(nameof(RearDraftStatus)); }
    }
    public ObservableCollection<RearButtonEditor> RearEditors => rearEditors[rearEditingMode];
    public int SelectedRearIndex
    {
        get => selectedRearIndex;
        set { if (value < 0 || value >= RearEditors.Count) return; selectedRearIndex = value; RefreshRearSelection();
            OnPropertyChanged(); OnPropertyChanged(nameof(SelectedRearEditor)); }
    }
    public RearButtonEditor SelectedRearEditor
    {
        get => RearEditors[selectedRearIndex];
        set
        {
            int index = RearEditors.IndexOf(value);
            if (index < 0) return;
            SelectedRearIndex = index;
        }
    }
    public string RearSettingsStatus
    { get => rearSettingsStatus; private set { rearSettingsStatus = value; OnPropertyChanged(); } }
    public string RearDraftStatus => rearDirtyModes.Contains(RearEditingMode) ? "未保存 · 切换保留草稿" : "已保存";
    public ICommand ApplyRearButtonsCommand { get; private set; } = null!;
    public ICommand ResetRearButtonsCommand { get; private set; } = null!;
    public ICommand SelectRearButtonCommand { get; private set; } = null!;
    public ICommand CancelRearButtonsCommand { get; private set; } = null!;
    private void RefreshRearSelection()
    {
        for (int i = 0; i < RearEditors.Count; i++) RearEditors[i].IsSelected = i == selectedRearIndex;
    }

    private void InitializeRearButtonSettings()
    {
        var config = userSettings.RearButtons.Normalize();
        rearEditors["Xbox"] = MakeRearEditors(false, config.Xbox);
        rearEditors["PS5"] = MakeRearEditors(true, config.Ps5);
        rearEditors["Pro1"] = MakeRearEditors(false, config.Pro1, true);
        foreach (string mode in RearModes) TrackRearDraft(mode);
        RefreshRearSelection();
        SelectRearButtonCommand = new RelayCommand(parameter =>
        { if (parameter is RearButtonEditor editor) SelectedRearEditor = editor; });
        ApplyRearButtonsCommand = new RelayCommand(_ => ApplyRearButtonSettings(), _ => !Busy);
        CancelRearButtonsCommand = new RelayCommand(_ =>
        {
            session?.CancelRearButtons();
            foreach (var slot in pro2Slots) slot.Session?.CancelRearButtons();
            RearSettingsStatus = "已请求停止背键与 C 键；松开后重新按下可再次使用。";
        });
        ResetRearButtonsCommand = new RelayCommand(_ =>
        {
            rearEditors[RearEditingMode] = MakeRearEditors(RearEditingMode == "PS5", new(), RearEditingMode == "Pro1");
            TrackRearDraft(RearEditingMode); rearDirtyModes.Add(RearEditingMode); OnPropertyChanged(nameof(RearDraftStatus));
            RefreshRearSelection();
            OnPropertyChanged(nameof(RearEditors)); OnPropertyChanged(nameof(SelectedRearIndex)); OnPropertyChanged(nameof(SelectedRearEditor));
            RearSettingsStatus = "已重置当前配置；点击保存生效。";
        });
    }
    private static ObservableCollection<RearButtonEditor> MakeRearEditors(bool ps5, RearButtonProfile profile, bool nintendo = false) =>
        [new("左背键 · GL", ps5, profile.Left, nintendo), new("右背键 · GR", ps5, profile.Right, nintendo),
         new("C 键", ps5, profile.C, nintendo)];
    private void TrackRearDraft(string mode)
    {
        foreach (var editor in rearEditors[mode]) editor.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(RearButtonEditor.IsSelected)) return;
            rearDirtyModes.Add(mode);
            if (RearEditingMode == mode) { RearSettingsStatus = ""; OnPropertyChanged(nameof(RearDraftStatus)); }
        };
    }

    private void ApplyRearButtonSettings()
    {
        if (Busy) { RearSettingsStatus = "请等待连接或模式切换完成后再保存。"; return; }
        // Save only the visible profile. Invalid drafts in another mode must
        // neither block this save nor silently replace that mode's live mapping.
        var bindings = new List<RearButtonBinding>();
        foreach (var editor in RearEditors)
        {
            if (!editor.TryBuild(out var binding, out string error))
            { RearSettingsStatus = RearEditingMode + " · " + error; return; }
            bindings.Add(binding);
        }
        var profile = new RearButtonProfile { Left = bindings[0], Right = bindings[1], C = bindings[2] };
        var config = RearEditingMode switch
        {
            "PS5" => userSettings.RearButtons with { Ps5 = profile },
            "Pro1" => userSettings.RearButtons with { Pro1 = profile },
            _ => userSettings.RearButtons with { Xbox = profile }
        };
        config = config.Normalize();
        var previous = userSettings.RearButtons;
        try
        {
            userSettings.RearButtons = config;
            if (!previewOnly) userSettings.Save();
        }
        catch (Exception ex)
        { userSettings.RearButtons = previous; RearSettingsStatus = "保存失败：" + FirstLine(ex.Message); return; }
        session?.SetRearButtons(config);
        foreach (var slot in pro2Slots) slot.Session?.SetRearButtons(config);
        rearDirtyModes.Remove(RearEditingMode); OnPropertyChanged(nameof(RearDraftStatus));
        RearSettingsStatus = "已保存并应用。";
        AppendLog("[REAR_BUTTONS] profile=" + RearEditingMode + " applied=true scheduling=fresh_source_frames_only");
    }
}
