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

    private static readonly string MarkerDir =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ytile");
    private static readonly string MarkerPath = Path.Combine(MarkerDir, "taskbar-hidden");

    /// <summary>
    /// True while at least one tray window is hidden BY US and observed to
    /// exist. Derived from what the shell actually reported, not from what we
    /// asked for — if the shell handed back no tray window there is nothing
    /// hidden, and claiming otherwise makes the caller tile over a bar that is
    /// still on screen.
    /// </summary>
    public static bool Hidden { get; private set; }

    /// <summary>
    /// Shows or hides every taskbar (primary plus one per additional monitor)
    /// and returns whether any tray window was actually found. Idempotent and
    /// cheap — re-run it after a monitor change (a new display grows a new tray
    /// window) and periodically (an explorer.exe restart recreates them all,
    /// visible, with no notification to us).
    /// </summary>
    public static bool SetHidden(bool hidden)
    {
        SHOW_WINDOW_CMD cmd = hidden ? SHOW_WINDOW_CMD.SW_HIDE : SHOW_WINDOW_CMD.SW_SHOW;
        bool found = false;

        fixed (char* primary = PrimaryClass)
        {
            HWND bar = PInvoke.FindWindow(primary, null);
            if (!bar.IsNull)
            {
                PInvoke.ShowWindow(bar, cmd);
                found = true;
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
                found = true;
            }
        }

        Hidden = hidden && found;
        WriteMarker(Hidden);
        return found;
    }

    /// <summary>True if a primary tray window exists and is currently visible.</summary>
    public static bool AnyBarVisible()
    {
        fixed (char* primary = PrimaryClass)
        {
            HWND bar = PInvoke.FindWindow(primary, null);
            return !bar.IsNull && PInvoke.IsWindowVisible(bar);
        }
    }

    /// <summary>
    /// Crash insurance, mirroring CloakPersistence: a daemon killed outright
    /// never runs its restore path, and nothing else on the system will ever
    /// un-hide the tray window. Called at startup — if the marker says a
    /// previous instance hid the taskbar, put it back before doing anything
    /// else, whatever the current config says.
    /// </summary>
    public static bool RecoverFromCrash()
    {
        bool stranded;
        try
        {
            stranded = File.Exists(MarkerPath);
        }
        catch (IOException)
        {
            return false;
        }

        if (!stranded || AnyBarVisible())
        {
            WriteMarker(false);
            return false;
        }

        SetHidden(false);
        return true;
    }

    private static void WriteMarker(bool hidden)
    {
        try
        {
            if (!hidden)
            {
                File.Delete(MarkerPath);
                return;
            }
            Directory.CreateDirectory(MarkerDir);
            File.WriteAllText(MarkerPath, "1");
        }
        catch (IOException)
        {
            // Best-effort, exactly like CloakPersistence — never take the
            // daemon down over a marker file.
        }
    }
}
