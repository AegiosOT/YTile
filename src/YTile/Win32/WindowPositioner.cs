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

    public static void Apply(nint hwndRaw, RectI cell, bool dryRun)
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

        int x = cell.X - left;
        int y = cell.Y - top;
        int w = cell.W + left + right;
        int h = cell.H + top + bottom;

        if (dryRun)
        {
            Console.WriteLine($"{DateTime.Now:HH:mm:ss.fff} DRYRUN SetWindowPos hwnd=0x{hwndRaw:X8} -> {cell} (adjusted {w}x{h}@{x},{y})");
            return;
        }

        PInvoke.SetWindowPos(hwnd, HWND.Null, x, y, w, h, Flags);
    }
}
