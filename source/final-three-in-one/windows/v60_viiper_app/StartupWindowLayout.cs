using System;
using System.Windows;

namespace Y700Switch2V60Viiper;

public static class StartupWindowLayout
{
    // WPF DIPs, not physical pixels: retain taskbar space even with display scaling.
    public static Rect Fit(Rect workArea)
    {
        if (workArea.IsEmpty || !double.IsFinite(workArea.Width) || !double.IsFinite(workArea.Height) ||
            workArea.Width <= 0 || workArea.Height <= 0)
            throw new ArgumentOutOfRangeException(nameof(workArea));
        double width = Math.Min(1040, Math.Max(1, workArea.Width - Math.Min(32, workArea.Width * .04)));
        double height = Math.Min(820, Math.Max(1, workArea.Height - Math.Min(32, workArea.Height * .04)));
        return new(workArea.Left + (workArea.Width - width) / 2,
            workArea.Top + (workArea.Height - height) / 2, width, height);
    }
}
