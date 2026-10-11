using System;
using System.Linq;
using System.Windows.Media;
using Brush = System.Windows.Media.Brush;
using Color = System.Windows.Media.Color;
using ColorConverter = System.Windows.Media.ColorConverter;

namespace Y700Switch2V60Viiper;

public sealed record BatterySegment(bool Filled, Brush Fill);
public sealed record BatteryVisual(string Level, string Caption, string Charge, BatterySegment[] Segments)
{
    private static readonly Brush Green = Frozen("#40866A"), Amber = Frozen("#AD751D"), Empty = Frozen("#E5EBE7");
    private static Brush Frozen(string color) { var brush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(color)); brush.Freeze(); return brush; }
    public static BatteryVisual Create(GamepadState state, bool connected, bool hasLevel, bool freshPower,
        string levelCaption = "连接时档位")
    {
        bool known = connected && hasLevel && state.BatteryLevel <= 9;
        int level = known ? state.BatteryLevel : 0;
        string caption = !connected ? "连接后读取" :
            (known ? levelCaption : "档位未知") +
            (state.BatteryVoltageMillivolts > 0 ? " · " + (state.BatteryVoltageMillivolts / 1000.0).ToString("F2") + " V" : "");
        string charge = connected && freshPower && state.BatteryPowerKnown
            ? state.BatteryCharging ? "充电中" : state.BatteryExternalPower ? "外接电源" : "" : "";
        return new(known ? level + " / 9" : "未知", caption, charge,
            Enumerable.Range(0, 9).Select(i => new BatterySegment(known && i < level,
                known && i < level ? level <= 2 ? Amber : Green : Empty)).ToArray());
    }
}
