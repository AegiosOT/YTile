using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.Graphics.Dwm;
using Windows.Win32.UI.Input.KeyboardAndMouse;
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
    /// Emitting one benign no-op key-up first satisfies that heuristic.
    /// </summary>
    public static void Focus(nint hwndRaw)
    {
        INPUT input = default;
        input.type = INPUT_TYPE.INPUT_KEYBOARD;
        input.Anonymous.ki.wVk = 0;
        input.Anonymous.ki.dwFlags = KEYBD_EVENT_FLAGS.KEYEVENTF_KEYUP;
        PInvoke.SendInput(new Span<INPUT>(ref input), sizeof(INPUT));
        PInvoke.SetForegroundWindow(new HWND(hwndRaw));
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
