using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Windows.Input;

namespace Y700Switch2V60Viiper;

public abstract class RearEditorObservable : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;
    protected void Changed([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}

public sealed class RearButtonChoice : RearEditorObservable
{
    public GamepadButtons Key { get; }
    public string Label { get; }
    private bool selected;
    public bool Selected { get => selected; set { if (selected == value) return; selected = value; Changed(); } }
    public RearButtonChoice(GamepadButtons key, string label, bool selected = false)
    { Key = key; Label = label; this.selected = selected; }
    public static IReadOnlyList<RearButtonChoice> Choices(bool ps5, bool nintendo = false) =>
    [
        new(GamepadButtons.None, "无"),
        new(GamepadButtons.South, ps5 ? "×" : nintendo ? "B" : "A"), new(GamepadButtons.East, ps5 ? "○" : nintendo ? "A" : "B"),
        new(GamepadButtons.West, ps5 ? "□" : nintendo ? "Y" : "X"), new(GamepadButtons.North, ps5 ? "△" : nintendo ? "X" : "Y"),
        new(GamepadButtons.L1, ps5 ? "L1" : nintendo ? "L" : "LB"), new(GamepadButtons.R1, ps5 ? "R1" : nintendo ? "R" : "RB"),
        new(GamepadButtons.L2, ps5 ? "L2" : nintendo ? "ZL" : "LT"), new(GamepadButtons.R2, ps5 ? "R2" : nintendo ? "ZR" : "RT"),
        new(GamepadButtons.LeftStick, ps5 ? "L3" : "LS"), new(GamepadButtons.RightStick, ps5 ? "R3" : "RS"),
        new(GamepadButtons.Back, ps5 ? "Create" : nintendo ? "−" : "View"), new(GamepadButtons.Start, ps5 ? "Options" : nintendo ? "+" : "Menu"),
        new(GamepadButtons.Home, ps5 ? "PS" : nintendo ? "Home" : "Xbox"),
        new(GamepadButtons.DPadUp, "↑"), new(GamepadButtons.DPadDown, "↓"),
        new(GamepadButtons.DPadLeft, "←"), new(GamepadButtons.DPadRight, "→"),
        ..(ps5 || nintendo ? new[] { new RearButtonChoice(GamepadButtons.Capture, ps5 ? "触摸板点击" : "截图") } : [])
    ];
}

public sealed class RearStepEditor : RearEditorObservable
{
    private int stepNumber;
    public int StepNumber { get => stepNumber; internal set { if (stepNumber == value) return; stepNumber = value; Changed(nameof(StepTitle)); } }
    public string StepTitle => "第 " + StepNumber + " 步";
    public IReadOnlyList<RearButtonChoice> Choices { get; }
    public bool Ps5 { get; }
    private Ps5TouchGesture touchAction;
    public Ps5TouchGesture TouchAction
    {
        get => touchAction;
        set { if (touchAction == value) return; touchAction = Enum.IsDefined(value) ? value : Ps5TouchGesture.None;
            if (touchAction != Ps5TouchGesture.None && (!int.TryParse(Duration, out int ms) || ms < Ps5TouchGestures.MinDurationMs))
                Duration = Ps5TouchGestures.DefaultDurationMs.ToString(CultureInfo.InvariantCulture);
            Changed(); }
    }
    private GamepadButtons first, second;
    public GamepadButtons First { get => first; set { if (first == value) return; first = value; Changed(); } }
    public GamepadButtons Second { get => second; set { if (second == value) return; second = value; Changed(); } }
    private string delay = "0", duration = "80";
    public string Delay { get => delay; set { if (delay == value) return; delay = value; Changed(); } }
    public string Duration { get => duration; set { if (duration == value) return; duration = value; Changed(); } }
    public ICommand RemoveCommand { get; }
    public RearStepEditor(bool ps5, RearButtonStep step, Action<RearStepEditor> remove, bool nintendo = false)
    {
        Choices = RearButtonChoice.Choices(ps5, nintendo);
        Ps5 = ps5; touchAction = ps5 ? step.TouchGesture : Ps5TouchGesture.None;
        var keys = Choices.Where(c => c.Key != GamepadButtons.None && (step.Buttons & c.Key) != 0).ToArray();
        first = keys.FirstOrDefault()?.Key ?? GamepadButtons.None;
        second = keys.Skip(1).FirstOrDefault()?.Key ?? GamepadButtons.None;
        Delay = step.DelayMs.ToString(CultureInfo.InvariantCulture);
        Duration = step.PressMs.ToString(CultureInfo.InvariantCulture);
        RemoveCommand = new RelayCommand(_ => remove(this));
    }
}

public sealed class RearButtonEditor : RearEditorObservable
{
    public static IReadOnlyList<string> Actions { get; } = ["按住映射", "单次按键", "按住连发", "顺序按键"];
    public string Title { get; }
    public bool Ps5 { get; }
    public bool Nintendo { get; }
    private bool isSelected;
    public bool IsSelected { get => isSelected; set { isSelected = value; Changed(); } }
    private bool enabled;
    public bool Enabled { get => enabled; set { if (enabled == value) return; enabled = value; Changed(); Changed(nameof(EnabledCaption)); } }
    public string EnabledCaption => Enabled ? "已启用" : "未启用";
    private Ps5TouchGesture touchAction;
    public Ps5TouchGesture TouchAction
    {
        get => touchAction;
        set { var next = Ps5 && Enum.IsDefined(value) ? value : Ps5TouchGesture.None; if (touchAction == next) return; touchAction = next;
            if (next != Ps5TouchGesture.None && (!int.TryParse(Duration, out int ms) || ms < Ps5TouchGestures.MinDurationMs))
                Duration = Ps5TouchGestures.DefaultDurationMs.ToString(CultureInfo.InvariantCulture);
            Changed(); Changed(nameof(MappingSummary)); Changed(nameof(IsDurationVisible)); Changed(nameof(DurationLabel)); Changed(nameof(BehaviorHint)); }
    }
    public bool IsDurationVisible => IsTimed || TouchAction != Ps5TouchGesture.None;
    public string DurationLabel => TouchAction != Ps5TouchGesture.None ? "手势时长（ms）" : "按下时长（ms）";
    public string MappingSummary => string.Join(" + ", Buttons.Where(b => b.Selected).Select(b => b.Label)
        .Concat(TouchAction == Ps5TouchGesture.None ? [] : new[] { Ps5TouchGestures.Choices.First(c => c.Gesture == TouchAction).Label })) is { Length: > 0 } summary
        ? summary : "未分配按键";
    private string action;
    public string Action
    {
        get => action;
        set { string next = Actions.Contains(value) ? value : Actions[0]; if (action == next) return; action = next; Changed();
            Changed(nameof(IsTimed)); Changed(nameof(IsDurationVisible)); Changed(nameof(IsRepeat)); Changed(nameof(IsSequence)); Changed(nameof(BehaviorHint)); }
    }
    public bool IsTimed => Action != Actions[0];
    public bool IsRepeat => Action == Actions[2];
    public bool IsSequence => Action == Actions[3];
    public string BehaviorHint => TouchAction != Ps5TouchGesture.None
        ? (IsRepeat ? "按住时重复手势，松开停止；同一时刻执行一个触控动作。"
            : Action == Actions[0] ? "按住完成一次手势，提前松开会停止；游戏需支持触控板。"
            : "每次按下完成一次手势；再次触发需松开。游戏需支持触控板。")
        : Action switch
    {
        "按住连发" => "仅按住时连发；松开停止，0 表示不限次数。",
        "单次按键" => "触发一次；执行中重复按下不排队。",
        "顺序按键" => "触发一次顺序动作；执行中不叠加，可随时停止背键。",
        _ => "按住生效，松开停止。"
    };
    private string delay = "0", duration = "80", gap = "80", count = "0";
    public string Delay { get => delay; set { if (delay == value) return; delay = value; Changed(); } }
    public string Duration { get => duration; set { if (duration == value) return; duration = value; Changed(); } }
    public string Gap { get => gap; set { if (gap == value) return; gap = value; Changed(); } }
    public string Count { get => count; set { if (count == value) return; count = value; Changed(); } }
    public ObservableCollection<RearButtonChoice> Buttons { get; }
    private RearButtonChoice Choice(GamepadButtons key) => Buttons.First(c => c.Key == key);
    public RearButtonChoice NorthChoice => Choice(GamepadButtons.North);
    public RearButtonChoice EastChoice => Choice(GamepadButtons.East);
    public RearButtonChoice SouthChoice => Choice(GamepadButtons.South);
    public RearButtonChoice WestChoice => Choice(GamepadButtons.West);
    public RearButtonChoice UpChoice => Choice(GamepadButtons.DPadUp);
    public RearButtonChoice DownChoice => Choice(GamepadButtons.DPadDown);
    public RearButtonChoice LeftChoice => Choice(GamepadButtons.DPadLeft);
    public RearButtonChoice RightChoice => Choice(GamepadButtons.DPadRight);
    public IEnumerable<RearButtonChoice> OtherButtons => Buttons.Where(c => (c.Key &
        (GamepadButtons.North | GamepadButtons.East | GamepadButtons.South | GamepadButtons.West |
         GamepadButtons.DPadUp | GamepadButtons.DPadDown | GamepadButtons.DPadLeft | GamepadButtons.DPadRight)) == 0);
    public string ActionSummary => (Enabled ? "" : "未启用 · ") + (Delay == "0" ? "" : "等待 " + Delay + "ms → ") + MappingSummary +
        (IsRepeat ? " → 连发 " + Duration + "/" + Gap + "ms" : IsSequence ? " → " + Steps.Count + " 个后续动作" : IsTimed ? " → " + Duration + "ms 后松开" : " → 松开停止");
    public string SequencePreview => "起始：" + MappingSummary + string.Concat(Steps.Select(s =>
        " → " + string.Join("+", s.Choices.Where(c => c.Key != GamepadButtons.None && (c.Key & (s.First | s.Second)) != 0).Select(c => c.Label)) +
        (s.TouchAction != Ps5TouchGesture.None ? "[" + Ps5TouchGestures.Choices.First(c => c.Gesture == s.TouchAction).Label + "]" : "") + " (" + s.Delay + "+" + s.Duration + "ms)"));
    public ObservableCollection<RearStepEditor> Steps { get; } = [];
    public ICommand AddStepCommand { get; }
    public ICommand ClearSelectionCommand { get; }
    public ICommand RepeatPresetCommand { get; }
    public RearButtonEditor(string title, bool ps5, RearButtonBinding binding, bool nintendo = false)
    {
        Title = title; Ps5 = ps5; Nintendo = nintendo; binding = binding.Normalize();
        touchAction = ps5 ? binding.TouchGesture : Ps5TouchGesture.None;
        enabled = binding.Enabled; action = Actions[(int)binding.Action];
        Delay = binding.DelayMs.ToString(CultureInfo.InvariantCulture);
        Duration = binding.PressMs.ToString(CultureInfo.InvariantCulture);
        Gap = binding.GapMs.ToString(CultureInfo.InvariantCulture);
        Count = binding.RepeatCount.ToString(CultureInfo.InvariantCulture);
        Buttons = new(RearButtonChoice.Choices(ps5, nintendo).Where(c => c.Key != GamepadButtons.None));
        foreach (var choice in Buttons) choice.Selected = (binding.Buttons & choice.Key) != 0;
        foreach (var choice in Buttons) choice.PropertyChanged += (_, _) => Changed(nameof(MappingSummary));
        Steps.CollectionChanged += (_, _) => { for (int i = 0; i < Steps.Count; i++) Steps[i].StepNumber = i + 2; Changed(nameof(Steps)); };
        foreach (var step in binding.Steps) AddStep(step);
        PropertyChanged += (_, e) =>
        {
            if (e.PropertyName is nameof(Action) or nameof(Enabled) or nameof(MappingSummary) or nameof(Delay) or nameof(Duration) or nameof(Gap) or nameof(Count) or nameof(Steps))
            { Changed(nameof(ActionSummary)); Changed(nameof(SequencePreview)); }
        };
        ClearSelectionCommand = new RelayCommand(_ => { foreach (var c in Buttons) c.Selected = false; TouchAction = Ps5TouchGesture.None; });
        RepeatPresetCommand = new RelayCommand(p =>
        {
            Action = Actions[2]; Duration = Gap = (p as string) switch { "slow" => "160", "fast" => "40", _ => "80" };
            Count = "0";
        });
        AddStepCommand = new RelayCommand(_ =>
        {
            if (Steps.Count < 16) AddStep(new() { Buttons = GamepadButtons.South, DelayMs = 100 });
        });
    }
    private void AddStep(RearButtonStep step)
    {
        var editor = new RearStepEditor(Ps5, step, removed => Steps.Remove(removed), Nintendo);
        editor.PropertyChanged += (_, _) => Changed(nameof(Steps));
        Steps.Add(editor);
    }

    public bool TryBuild(out RearButtonBinding binding, out string error)
    {
        binding = new(); error = "";
        var keys = Buttons.Where(c => c.Selected).Aggregate(GamepadButtons.None, (mask, c) => mask | c.Key);
        int actionIndex = Actions.ToList().IndexOf(Action);
        if (Enabled && keys == GamepadButtons.None && TouchAction == Ps5TouchGesture.None) { error = Title + "：请选择按键或触控板动作。"; return false; }
        if (Enabled && RearButtonBinding.HasOppositeDirections(keys)) { error = Title + "：相反方向不能同时选择。"; return false; }
        bool delayOk = Number(Delay, 0, 30000, out int delay);
        int minDuration = TouchAction != Ps5TouchGesture.None ? Ps5TouchGestures.MinDurationMs : 30;
        bool durationOk = Number(Duration, minDuration, 30000, out int duration);
        bool gapOk = Number(Gap, 30, 30000, out int gap);
        bool countOk = Number(Count, 0, 1000, out int count);
        if (Enabled && (!delayOk || (IsDurationVisible && !durationOk) || (IsRepeat && (!gapOk || !countOk))))
        { error = Title + "：延迟0–30000ms，动作时长" + minDuration + "–30000ms，间隔30–30000ms，次数0–1000。"; return false; }
        // Hidden or disabled fields cannot prevent disabling a mapping. Invalid
        // unused drafts are stored as safe defaults, never passed to the runtime.
        if (!delayOk) delay = 0;
        if (!durationOk) duration = 80;
        if (!gapOk) gap = 80;
        if (!countOk) count = 0;
        var steps = new List<RearButtonStep>();
        foreach (var step in Enabled && IsSequence ? Steps : Enumerable.Empty<RearStepEditor>())
        {
            var stepKeys = step.First | step.Second;
            var stepTouch = Ps5 ? step.TouchAction : Ps5TouchGesture.None;
            if ((stepKeys == GamepadButtons.None && stepTouch == Ps5TouchGesture.None) || RearButtonBinding.HasOppositeDirections(stepKeys) || !Number(step.Delay, 0, 30000, out int stepDelay) ||
                !Number(step.Duration, stepTouch != Ps5TouchGesture.None ? Ps5TouchGestures.MinDurationMs : 30, 30000, out int stepDuration))
            { error = Title + "：请检查后续按键、延迟和按下时长。"; return false; }
            steps.Add(new() { Buttons = stepKeys, TouchGesture = stepTouch, DelayMs = stepDelay, PressMs = stepDuration });
        }
        binding = new() { Enabled = Enabled, Action = (RearButtonAction)actionIndex, Buttons = keys,
            TouchGesture = TouchAction,
            DelayMs = delay, PressMs = duration, GapMs = gap, RepeatCount = count, Steps = steps.ToArray() };
        return true;
    }
    private static bool Number(string text, int min, int max, out int value) =>
        int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out value) && value >= min && value <= max;
}
