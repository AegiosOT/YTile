using System.Runtime.InteropServices;
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
    // FRAMECHANGED forces WM_WINDOWPOSCHANGED even when the rect is unchanged
    // (a same-rect retile is otherwise a silent no-op — Chromium's occlusion
    // tracker never recomputes and a post-uncloak renderer stays suspended);
    // NOCOPYBITS forces a real repaint instead of blitting the stale surface.
    private const SET_WINDOW_POS_FLAGS Flags =
        SET_WINDOW_POS_FLAGS.SWP_NOACTIVATE |
        SET_WINDOW_POS_FLAGS.SWP_NOZORDER |
        SET_WINDOW_POS_FLAGS.SWP_ASYNCWINDOWPOS |
        SET_WINDOW_POS_FLAGS.SWP_FRAMECHANGED |
        SET_WINDOW_POS_FLAGS.SWP_NOCOPYBITS |
        SET_WINDOW_POS_FLAGS.SWP_NOSENDCHANGING;

    private const int ErrorAccessDenied = 5;

    /// <summary>Returns the adjusted rect actually requested from the OS, so the
    /// caller can later verify whether the window honored it.
    /// <paramref name="accessDenied"/> reports the one failure that verifying
    /// the rect afterwards cannot diagnose: UIPI forbids us from positioning a
    /// window owned by a process at a higher integrity level, so SetWindowPos
    /// is refused outright and the window never moves. Task Manager and the
    /// other auto-elevating system tools land here whenever ytiled itself is
    /// not elevated. Waiting cannot fix it — only restarting ytiled elevated
    /// can — so the caller must stop treating the window as tiled.</summary>
    public static RectI Apply(nint hwndRaw, RectI cell, bool dryRun, out bool accessDenied)
    {
        accessDenied = false;
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

        if (!PInvoke.SetWindowPos(hwnd, HWND.Null, adjusted.X, adjusted.Y, adjusted.W, adjusted.H, Flags))
        {
            // Every other failure (a window that died mid-call, most of all) is
            // transient or self-correcting, and the reaper already handles it.
            accessDenied = Marshal.GetLastPInvokeError() == ErrorAccessDenied;
        }

        return adjusted;
    }

    /// <summary>
    /// No-op position change + invalidate: fires WM_WINDOWPOSCHANGED so
    /// occlusion trackers (Chromium's in particular) re-evaluate the window
    /// and wake a suspended renderer after an uncloak.
    /// </summary>
    public static void Nudge(nint hwndRaw)
    {
        var hwnd = new HWND(hwndRaw);
        PInvoke.SetWindowPos(hwnd, HWND.Null, 0, 0, 0, 0,
            SET_WINDOW_POS_FLAGS.SWP_FRAMECHANGED |
            SET_WINDOW_POS_FLAGS.SWP_NOMOVE |
            SET_WINDOW_POS_FLAGS.SWP_NOSIZE |
            SET_WINDOW_POS_FLAGS.SWP_NOZORDER |
            SET_WINDOW_POS_FLAGS.SWP_NOACTIVATE);
        PInvoke.InvalidateRect(hwnd, (RECT*)null, false);
    }
}
