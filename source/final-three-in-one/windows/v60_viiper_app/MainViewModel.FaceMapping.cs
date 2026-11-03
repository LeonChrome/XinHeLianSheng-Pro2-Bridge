using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Windows.Input;

namespace Y700Switch2V60Viiper;

public sealed record FaceKeyChoice(GamepadButtons Button, string Label);
public sealed record FacePresetChoice(FaceLayoutPreset Preset, string Label);
public sealed class FaceBindingEditor : INotifyPropertyChanged
{
    private GamepadButtons target;
    public string PhysicalLabel { get; }
    public string Position { get; }
    public IReadOnlyList<FaceKeyChoice> Choices { get; }
    public GamepadButtons Target { get => target; set { if (target == value) return; target = value; PropertyChanged?.Invoke(this, new(nameof(Target))); PropertyChanged?.Invoke(this, new(nameof(TargetLabel))); } }
    public string TargetLabel => Choices.FirstOrDefault(c => c.Button == Target)?.Label ?? "不输出";
    public FaceBindingEditor(string label, string position, IReadOnlyList<FaceKeyChoice> choices, GamepadButtons key) { PhysicalLabel = label; Position = position; Choices = choices; target = key; }
    public event PropertyChangedEventHandler? PropertyChanged;
}

public sealed partial class MainViewModel
{
    private readonly Dictionary<ViiperVirtualMode, ObservableCollection<FaceBindingEditor>> faceDrafts = new();
    private readonly Dictionary<ViiperVirtualMode, FaceLayoutPreset> facePresets = new();
    private readonly HashSet<ViiperVirtualMode> faceDirty = new();
    private ViiperVirtualMode faceEditingMode = ViiperVirtualMode.Xbox;
    private bool changingFacePreset;
    private string faceStatus = "";
    public IReadOnlyList<Pro2SlotModeOption> FaceModes => Pro2SlotModes.Options;
    public IReadOnlyList<FacePresetChoice> FacePresets { get; } = [new(FaceLayoutPreset.Position,"按位置（默认）"),
        new(FaceLayoutPreset.NintendoNames,"Pro2 字母对应"), new(FaceLayoutPreset.Xbox,"Xbox 位置习惯"),
        new(FaceLayoutPreset.PlayStation,"PS5 位置习惯"), new(FaceLayoutPreset.Custom,"自定义")];
    public ViiperVirtualMode FaceEditingMode
    {
        get => faceEditingMode;
        set { if (!faceDrafts.ContainsKey(value) || value == faceEditingMode) return; faceEditingMode = value;
            FaceStatus = ""; OnPropertyChanged(); RefreshFacePage(); }
    }
    public FaceLayoutPreset SelectedFacePreset
    {
        get => facePresets[faceEditingMode];
        set {
            if (value == SelectedFacePreset) return;
            facePresets[faceEditingMode] = value;
            if (value != FaceLayoutPreset.Custom) {
                changingFacePreset = true;
                var p = FaceButtonProfile.ForPreset(value, faceEditingMode);
                var keys = new[] { p.North, p.East, p.South, p.West };
                for (int i = 0; i < 4; i++) FaceRows[i].Target = keys[i];
                changingFacePreset = false;
            }
            faceDirty.Add(faceEditingMode); FaceStatus = ""; RefreshFacePage();
        }
    }
    public ObservableCollection<FaceBindingEditor> FaceRows => faceDrafts[faceEditingMode];
    public string FaceDraftStatus => faceDirty.Contains(faceEditingMode) ? "未保存 · 切换保留草稿" : "已保存";
    public string FaceStatus { get => faceStatus; private set { faceStatus = value; OnPropertyChanged(); } }
    public string FacePresetHint => SelectedFacePreset == FaceLayoutPreset.NintendoNames
        ? "按实体字母对应；PS5 采用 A=×、B=○、X=□、Y=△。以右侧结果为准。"
        : SelectedFacePreset == FaceLayoutPreset.Custom ? "自定义每个实体按键的输出。多个按键可对应同一输出。"
        : "按上下左右的位置对应。Xbox 与 PS5 的位置规则相同，键名和符号不同。";
    public ICommand SaveFaceMappingCommand { get; private set; } = null!;
    public ICommand ResetFaceMappingCommand { get; private set; } = null!;
    public static string FaceLabel(ViiperVirtualMode mode, GamepadButtons key) => key == GamepadButtons.None ? "不输出" : mode switch {
        ViiperVirtualMode.Pro1 or ViiperVirtualMode.Pro2 => key switch { GamepadButtons.North => "X · 上", GamepadButtons.East => "A · 右", GamepadButtons.South => "B · 下", _ => "Y · 左" },
        ViiperVirtualMode.DualSenseLike => key switch { GamepadButtons.North => "△ · 上", GamepadButtons.East => "○ · 右", GamepadButtons.South => "× · 下", _ => "□ · 左" },
        _ => key switch { GamepadButtons.North => "Y · 上", GamepadButtons.East => "B · 右", GamepadButtons.South => "A · 下", _ => "X · 左" } };
    private void InitializeFaceMapping()
    {
        userSettings.FaceButtons = (userSettings.FaceButtons ?? new()).Normalize();
        foreach (var mode in Pro2SlotModes.Options.Select(m => m.Mode)) {
            var p = userSettings.FaceButtons.ForMode(mode);
            var keys = new[] { p.North, p.East, p.South, p.West };
            var choices = new[] { GamepadButtons.North, GamepadButtons.East, GamepadButtons.South, GamepadButtons.West, GamepadButtons.None }
                .Select(k => new FaceKeyChoice(k, FaceLabel(mode, k))).ToArray();
            var rows = new ObservableCollection<FaceBindingEditor>();
            string[] physical = ["X", "A", "B", "Y"], positions = ["上", "右", "下", "左"];
            for (int i = 0; i < 4; i++) rows.Add(new(physical[i], positions[i], choices, keys[i]));
            faceDrafts[mode] = rows; facePresets[mode] = p.Preset;
            foreach (var row in rows) row.PropertyChanged += (_, e) => {
                if (e.PropertyName != nameof(FaceBindingEditor.Target) || changingFacePreset) return;
                facePresets[mode] = FaceLayoutPreset.Custom; faceDirty.Add(mode);
                if (mode == faceEditingMode) { FaceStatus = ""; RefreshFacePage(); }
            };
        }
        SaveFaceMappingCommand = new RelayCommand(_ => SaveFaceMapping(), _ => !Busy);
        ResetFaceMappingCommand = new RelayCommand(_ => { SelectedFacePreset = FaceLayoutPreset.Position; });
    }
    private void RefreshFacePage() {
        OnPropertyChanged(nameof(FaceRows)); OnPropertyChanged(nameof(SelectedFacePreset));
        OnPropertyChanged(nameof(FaceDraftStatus)); OnPropertyChanged(nameof(FacePresetHint));
    }
    private void SaveFaceMapping()
    {
        if (Busy) return;
        var p = new FaceButtonProfile { Preset = SelectedFacePreset, North = FaceRows[0].Target, East = FaceRows[1].Target,
            South = FaceRows[2].Target, West = FaceRows[3].Target }.Normalize();
        var previous = userSettings.FaceButtons;
        try { userSettings.FaceButtons = previous.WithMode(faceEditingMode, p); if (!previewOnly) userSettings.Save(); }
        catch (Exception ex) { userSettings.FaceButtons = previous; FaceStatus = "保存失败：" + FirstLine(ex.Message); return; }
        session?.SetFaceButtons(userSettings.FaceButtons);
        foreach (var slot in pro2Slots) slot.Session?.SetFaceButtons(userSettings.FaceButtons);
        faceDirty.Remove(faceEditingMode); FaceStatus = "已保存 · 按住的 ABXY 需松开再按；游戏内映射仍会叠加。";
        RefreshFacePage(); AppendLog("[FACE_MAPPING] mode=" + faceEditingMode + " preset=" + p.Preset +
            " north=" + p.North + " east=" + p.East + " south=" + p.South + " west=" + p.West +
            " order=physical_face_then_rear_then_game raw_fd2_unchanged=true");
    }
}
