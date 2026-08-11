using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.Graphics.Gdi;
using YTile.Core;

namespace YTile.Win32;

internal readonly record struct MonitorDesc(nint Handle, string Device, RectI Bounds, RectI WorkArea, bool Primary);

internal static unsafe class MonitorProbe
{
    private static readonly List<MonitorDesc> s_result = [];
    private static readonly Lock s_lock = new();

    public static List<MonitorDesc> Enumerate()
    {
        lock (s_lock)
        {
            s_result.Clear();
            PInvoke.EnumDisplayMonitors(HDC.Null, (RECT*)null, &Callback, default);
            // Primary first, then left-to-right — stable monitor indices.
            return [.. s_result.OrderByDescending(m => m.Primary).ThenBy(m => m.Bounds.X)];
        }
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static BOOL Callback(HMONITOR monitor, HDC hdc, RECT* clip, LPARAM lparam)
    {
        try
        {
            MONITORINFOEXW info = default;
            info.monitorInfo.cbSize = (uint)sizeof(MONITORINFOEXW);
            if (PInvoke.GetMonitorInfo(monitor, (MONITORINFO*)&info))
            {
                s_result.Add(new MonitorDesc(
                    (nint)monitor.Value,
                    info.szDevice.ToString(),
                    RectI.From(info.monitorInfo.rcMonitor),
                    RectI.From(info.monitorInfo.rcWork),
                    (info.monitorInfo.dwFlags & PInvoke.MONITORINFOF_PRIMARY) != 0));
            }
        }
        catch
        {
            // Never throw across the native callback boundary.
        }

        return true;
    }

    public static nint MonitorForWindow(nint hwnd)
        => (nint)PInvoke.MonitorFromWindow(new HWND(hwnd), MONITOR_FROM_FLAGS.MONITOR_DEFAULTTONEAREST).Value;
}
