using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.UI.WindowsAndMessaging;

namespace YTile.Win32;

/// <summary>
/// Hides the shell taskbar outright (SW_HIDE on the tray windows), rather than
/// switching Windows' own auto-hide setting on — the Hyprland-style "the bar is
/// simply not there" behaviour.
///
/// Two things follow from this and are handled by the caller, not here:
/// hiding the tray window does NOT change the monitor's work area (Windows
/// keeps reserving the strip), so layouts must be computed from the monitor
/// bounds instead; and the taskbar MUST be restored on shutdown, pause, and
/// config reload, or the user is left with no taskbar and no obvious way back.
/// </summary>
internal static unsafe class TaskbarControl
{
    private const string PrimaryClass = "Shell_TrayWnd";
    private const string SecondaryClass = "Shell_SecondaryTrayWnd";

    /// <summary>True once we have hidden the bars, so we know to restore them.</summary>
    public static bool Hidden { get; private set; }

    /// <summary>
    /// Shows or hides every taskbar (primary plus one per additional monitor).
    /// Re-applying a hide is cheap and idempotent — call it again after a
    /// monitor change, since a newly attached display grows a new tray window.
    /// </summary>
    public static void SetHidden(bool hidden)
    {
        SHOW_WINDOW_CMD cmd = hidden ? SHOW_WINDOW_CMD.SW_HIDE : SHOW_WINDOW_CMD.SW_SHOW;

        fixed (char* primary = PrimaryClass)
        {
            HWND bar = PInvoke.FindWindow(primary, null);
            if (!bar.IsNull)
            {
                PInvoke.ShowWindow(bar, cmd);
            }
        }

        fixed (char* secondary = SecondaryClass)
        {
            HWND next = HWND.Null;
            // FindWindowEx walks siblings: one Shell_SecondaryTrayWnd per extra
            // monitor. Bounded so a shell that keeps handing back windows can't
            // spin the actor thread forever.
            for (int i = 0; i < 16; i++)
            {
                next = PInvoke.FindWindowEx(HWND.Null, next, secondary, null);
                if (next.IsNull)
                {
                    break;
                }
                PInvoke.ShowWindow(next, cmd);
            }
        }

        Hidden = hidden;
    }
}
