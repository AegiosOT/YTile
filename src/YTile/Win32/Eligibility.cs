using Windows.Win32.UI.WindowsAndMessaging;

namespace YTile.Win32;

/// <summary>
/// First cut of the tile-eligibility heuristic (the ruleset komorebi converged
/// on over five years): titled, captioned, top-level app windows tile; tool
/// windows, modal dialog frames, layered and cloaked windows do not.
/// </summary>
internal static class Eligibility
{
    /// <summary>Null means the window would be tiled; otherwise why not.</summary>
    public static string? SkipReason(in WindowSnapshot w)
    {
        if (!w.IsAlive)
        {
            return "window gone";
        }
        if (w.Title.Length == 0)
        {
            return "no title";
        }
        if (w.Cloaked)
        {
            return "cloaked";
        }
        if ((w.Style & WINDOW_STYLE.WS_CHILD) != 0)
        {
            return "child window";
        }
        if ((w.ExStyle & WINDOW_EX_STYLE.WS_EX_TOOLWINDOW) != 0)
        {
            return "tool window";
        }
        if ((w.ExStyle & WINDOW_EX_STYLE.WS_EX_NOACTIVATE) != 0)
        {
            return "no-activate";
        }
        if ((w.ExStyle & WINDOW_EX_STYLE.WS_EX_DLGMODALFRAME) != 0)
        {
            return "modal dialog frame";
        }
        // Layered windows generate duplicate-event storms; manage only via an
        // explicit whitelist once the application quirks db exists.
        if ((w.ExStyle & WINDOW_EX_STYLE.WS_EX_LAYERED) != 0)
        {
            return "layered (needs whitelist)";
        }
        if ((w.Style & WINDOW_STYLE.WS_CAPTION) == 0)
        {
            return "no caption";
        }
        if ((w.ExStyle & WINDOW_EX_STYLE.WS_EX_WINDOWEDGE) == 0)
        {
            return "no window edge";
        }
        if (w.Width < 50 || w.Height < 50)
        {
            return "too small";
        }

        return null;
    }
}
