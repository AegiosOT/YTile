using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.Graphics.Dwm;
using Windows.Win32.UI.WindowsAndMessaging;

namespace YTile.Win32;

internal static unsafe class FocusControl
{
    /// <summary>
    /// Startup: zero the foreground lock timeout and allow any process to take
    /// foreground, so directional focus can actually move focus later.
    /// </summary>
    public static void Init()
    {
        PInvoke.SystemParametersInfo(
            SYSTEM_PARAMETERS_INFO_ACTION.SPI_SETFOREGROUNDLOCKTIMEOUT,
            0,
            null,
            SYSTEM_PARAMETERS_INFO_UPDATE_FLAGS.SPIF_SENDCHANGE);
        PInvoke.AllowSetForegroundWindow(PInvoke.ASFW_ANY);
    }

    /// <summary>
    /// SetForegroundWindow refuses callers that haven't received input recently.
    /// The widespread workaround is to inject a synthetic keystroke first — do
    /// NOT do that here: a global hotkey daemon (whkd) hooks the keyboard at a
    /// level that sees injected input as a real keypress, and since the user is
    /// still holding the modifier when their binding fires, the phantom key
    /// completes another chord and re-runs a binding. That made every
    /// keybind-driven workspace switch bounce straight back.
    ///
    /// Borrowing the foreground thread's input state achieves the same
    /// permission without generating any input at all.
    /// </summary>
    public static void Focus(nint hwndRaw)
    {
        var hwnd = new HWND(hwndRaw);
        if (PInvoke.SetForegroundWindow(hwnd))
        {
            return;
        }

        HWND foreground = PInvoke.GetForegroundWindow();
        uint fgThread = foreground.IsNull ? 0 : PInvoke.GetWindowThreadProcessId(foreground, null);
        uint ourThread = PInvoke.GetCurrentThreadId();
        if (fgThread == 0 || fgThread == ourThread)
        {
            return;
        }

        if (PInvoke.AttachThreadInput(ourThread, fgThread, true))
        {
            PInvoke.SetForegroundWindow(hwnd);
            PInvoke.AttachThreadInput(ourThread, fgThread, false);
        }
    }

    /// <summary>
    /// Parks foreground on the desktop shell window — used when a workspace
    /// switch leaves nothing to focus, so the OS can't return foreground to a
    /// cloaked window.
    /// </summary>
    public static void FocusDesktop()
    {
        HWND shell = PInvoke.GetShellWindow();
        if (!shell.IsNull)
        {
            Focus((nint)shell.Value);
        }
    }

    /// <summary>Win11 native focus border; silently a no-op on Win10.</summary>
    public static void SetBorder(nint hwndRaw, uint? colorref)
    {
        uint value = colorref ?? PInvoke.DWMWA_COLOR_DEFAULT;
        PInvoke.DwmSetWindowAttribute(
            new HWND(hwndRaw), DWMWINDOWATTRIBUTE.DWMWA_BORDER_COLOR, &value, sizeof(uint));
    }
}
