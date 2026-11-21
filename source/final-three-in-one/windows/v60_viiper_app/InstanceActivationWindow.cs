using System.Runtime.InteropServices;
using System.Security.Principal;
using Forms = System.Windows.Forms;

namespace Y700Switch2V60Viiper;

// A message-only window is discoverable even before the WPF window has ever
// been shown. Windows scopes it to this desktop; the name also identifies the user.
internal sealed class InstanceActivationWindow : Forms.NativeWindow, IDisposable
{
    private static readonly IntPtr MessageOnly = new(-3);
    private const int ShowAssistant = 0x8000 + 41; // WM_APP
    private readonly Action show;

    private static string WindowName(bool smoke)
    {
        using var user = WindowsIdentity.GetCurrent();
        return "PRO2ControllerAssistant.Show." + user.User!.Value + (smoke ? ".Smoke" : "");
    }

    internal InstanceActivationWindow(bool smoke, Action show)
    {
        this.show = show;
        CreateHandle(new Forms.CreateParams { Parent = MessageOnly, Caption = WindowName(smoke) });
    }

    internal static bool TryShowExisting(bool smoke)
    {
        IntPtr handle = FindWindowExW(MessageOnly, IntPtr.Zero, null, WindowName(smoke));
        if (handle == IntPtr.Zero) return false;
        GetWindowThreadProcessId(handle, out uint owner);
        if (owner != 0) AllowSetForegroundWindow(owner);
        return PostMessageW(handle, ShowAssistant, IntPtr.Zero, IntPtr.Zero);
    }

    protected override void WndProc(ref Forms.Message message)
    {
        if (message.Msg == ShowAssistant)
        {
            show();
            return;
        }
        base.WndProc(ref message);
    }

    public void Dispose() => DestroyHandle();

    [DllImport("user32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern IntPtr FindWindowExW(IntPtr parent, IntPtr after, string? className, string title);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr window, out uint processId);

    [DllImport("user32.dll")]
    private static extern bool AllowSetForegroundWindow(uint processId);

    [DllImport("user32.dll", ExactSpelling = true)]
    private static extern bool PostMessageW(IntPtr window, int message, IntPtr wParam, IntPtr lParam);
}
