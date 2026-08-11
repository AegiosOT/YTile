using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.Graphics.Dwm;
using Windows.Win32.UI.WindowsAndMessaging;
using YTile.Core;

namespace YTile.Win32;

/// <summary>
/// Applies a layout cell to a window. Windows since Vista draw an invisible
/// resize border around the visible frame, so aligning GetWindowRect to the
/// cell leaves ugly gaps; we compensate with DWMWA_EXTENDED_FRAME_BOUNDS so
/// the *visible* frame lands exactly on the cell.
/// </summary>
internal static unsafe class WindowPositioner
{
    private const SET_WINDOW_POS_FLAGS Flags =
        SET_WINDOW_POS_FLAGS.SWP_NOACTIVATE |
        SET_WINDOW_POS_FLAGS.SWP_NOZORDER |
        SET_WINDOW_POS_FLAGS.SWP_ASYNCWINDOWPOS;

    /// <summary>Returns the adjusted rect actually requested from the OS, so the
    /// caller can later verify whether the window honored it.</summary>
    public static RectI Apply(nint hwndRaw, RectI cell, bool dryRun)
    {
        var hwnd = new HWND(hwndRaw);

        RECT window = default;
        PInvoke.GetWindowRect(hwnd, &window);

        RECT frame = default;
        bool haveFrame = PInvoke.DwmGetWindowAttribute(
            hwnd, DWMWINDOWATTRIBUTE.DWMWA_EXTENDED_FRAME_BOUNDS, &frame, (uint)sizeof(RECT)).Succeeded;

        int left = haveFrame ? frame.left - window.left : 0;
        int top = haveFrame ? frame.top - window.top : 0;
        int right = haveFrame ? window.right - frame.right : 0;
        int bottom = haveFrame ? window.bottom - frame.bottom : 0;

        var adjusted = new RectI(cell.X - left, cell.Y - top, cell.W + left + right, cell.H + top + bottom);

        if (dryRun)
        {
            Console.WriteLine($"{DateTime.Now:HH:mm:ss.fff} DRYRUN SetWindowPos hwnd=0x{hwndRaw:X8} -> {cell} (adjusted {adjusted})");
            return adjusted;
        }

        PInvoke.SetWindowPos(hwnd, HWND.Null, adjusted.X, adjusted.Y, adjusted.W, adjusted.H, Flags);
        return adjusted;
    }
}
