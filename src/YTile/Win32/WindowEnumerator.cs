using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Windows.Win32;
using Windows.Win32.Foundation;

namespace YTile.Win32;

/// <summary>EnumWindows wrapper for the startup adoption pass.</summary>
internal static unsafe class WindowEnumerator
{
    private static readonly List<nint> s_result = [];
    private static readonly Lock s_lock = new();

    public static List<nint> TopLevelWindows()
    {
        lock (s_lock)
        {
            s_result.Clear();
            PInvoke.EnumWindows(&Callback, default);
            return [.. s_result];
        }
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static BOOL Callback(HWND hwnd, LPARAM lparam)
    {
        try
        {
            s_result.Add((nint)hwnd.Value);
        }
        catch
        {
            // Never throw across the native callback boundary.
        }

        return true;
    }
}
