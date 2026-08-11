using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.Graphics.Dwm;
using Windows.Win32.System.Threading;
using Windows.Win32.UI.WindowsAndMessaging;

namespace YTile.Win32;

/// <summary>Point-in-time probe of a top-level window's identity and state.</summary>
internal readonly struct WindowSnapshot
{
    public required nint Hwnd { get; init; }
    public required bool IsAlive { get; init; }
    public string Title { get; init; }
    public string ClassName { get; init; }
    public string ExeName { get; init; }
    public uint ProcessId { get; init; }
    public bool Visible { get; init; }
    public bool Iconic { get; init; }
    public bool Zoomed { get; init; }
    public bool Cloaked { get; init; }
    public WINDOW_STYLE Style { get; init; }
    public WINDOW_EX_STYLE ExStyle { get; init; }
    public int X { get; init; }
    public int Y { get; init; }
    public int Width { get; init; }
    public int Height { get; init; }

    public static unsafe WindowSnapshot Capture(nint hwndRaw)
    {
        var hwnd = new HWND(hwndRaw);
        if (!PInvoke.IsWindow(hwnd))
        {
            return new WindowSnapshot { Hwnd = hwndRaw, IsAlive = false, Title = "", ClassName = "", ExeName = "" };
        }

        Span<char> buffer = stackalloc char[512];

        int titleLen = PInvoke.GetWindowText(hwnd, buffer);
        string title = new(buffer[..Math.Max(titleLen, 0)]);

        int classLen = PInvoke.GetClassName(hwnd, buffer);
        string className = new(buffer[..Math.Max(classLen, 0)]);

        uint pid = 0;
        PInvoke.GetWindowThreadProcessId(hwnd, &pid);

        string exeName = "";
        if (pid != 0)
        {
            HANDLE process = PInvoke.OpenProcess(PROCESS_ACCESS_RIGHTS.PROCESS_QUERY_LIMITED_INFORMATION, false, pid);
            if (!process.IsNull)
            {
                uint size = (uint)buffer.Length;
                fixed (char* p = buffer)
                {
                    if (PInvoke.QueryFullProcessImageName(process, PROCESS_NAME_FORMAT.PROCESS_NAME_WIN32, new PWSTR(p), &size))
                    {
                        exeName = Path.GetFileName(new string(buffer[..(int)size]));
                    }
                }
                PInvoke.CloseHandle(process);
            }
        }

        var style = (WINDOW_STYLE)unchecked((uint)PInvoke.GetWindowLongPtr(hwnd, WINDOW_LONG_PTR_INDEX.GWL_STYLE));
        var exStyle = (WINDOW_EX_STYLE)unchecked((uint)PInvoke.GetWindowLongPtr(hwnd, WINDOW_LONG_PTR_INDEX.GWL_EXSTYLE));

        // Cloaked = exists but invisible (other virtual desktop, suspended UWP).
        uint cloaked = 0;
        PInvoke.DwmGetWindowAttribute(hwnd, DWMWINDOWATTRIBUTE.DWMWA_CLOAKED, &cloaked, sizeof(uint));

        RECT rect = default;
        PInvoke.GetWindowRect(hwnd, &rect);

        return new WindowSnapshot
        {
            Hwnd = hwndRaw,
            IsAlive = true,
            Title = title,
            ClassName = className,
            ExeName = exeName,
            ProcessId = pid,
            Visible = PInvoke.IsWindowVisible(hwnd),
            Iconic = PInvoke.IsIconic(hwnd),
            Zoomed = PInvoke.IsZoomed(hwnd),
            Cloaked = cloaked != 0,
            Style = style,
            ExStyle = exStyle,
            X = rect.left,
            Y = rect.top,
            Width = rect.right - rect.left,
            Height = rect.bottom - rect.top,
        };
    }
}
