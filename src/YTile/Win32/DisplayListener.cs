using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Threading.Channels;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.System.Power;
using Windows.Win32.UI.WindowsAndMessaging;
using YTile.Runtime;

namespace YTile.Win32;

/// <summary>
/// Hidden top-level window (NOT message-only — those never receive broadcast
/// messages) whose only job is turning WM_DISPLAYCHANGE / WM_DEVICECHANGE /
/// work-area WM_SETTINGCHANGE / resume-from-sleep into actor messages. The
/// debouncing lives in the actor; this thread just forwards.
/// </summary>
internal static unsafe class DisplayListener
{
    private const string ClassName = "ytile-display";

    private static ChannelWriter<WmMessage>? s_writer;
    private static volatile uint s_threadId;

    public static void Start(ChannelWriter<WmMessage> writer)
    {
        s_writer = writer;
        var thread = new Thread(ThreadProc) { Name = "ytile-display-pump", IsBackground = true };
        thread.Start();
    }

    public static void Stop()
    {
        uint id = s_threadId;
        if (id != 0)
        {
            PInvoke.PostThreadMessage(id, PInvoke.WM_QUIT, default, default);
        }
    }

    private static void ThreadProc()
    {
        s_threadId = PInvoke.GetCurrentThreadId();

        var hInstance = (HINSTANCE)PInvoke.GetModuleHandle(default(PCWSTR)).Value;
        HWND hwnd = default;
        HPOWERNOTIFY powerNotify = default;

        fixed (char* className = ClassName)
        {
            var wc = new WNDCLASSW
            {
                lpfnWndProc = &WndProc,
                hInstance = hInstance,
                lpszClassName = className,
            };
            PInvoke.RegisterClass(&wc);

            hwnd = PInvoke.CreateWindowEx(
                0, className, className, 0, 0, 0, 0, 0, HWND.Null, HMENU.Null, hInstance, null);
            if (!hwnd.IsNull)
            {
                // Modern-standby systems only deliver PBT_APMRESUME* to
                // explicitly registered windows.
                powerNotify = PInvoke.RegisterSuspendResumeNotification(
                    (HANDLE)hwnd.Value, REGISTER_NOTIFICATION_FLAGS.DEVICE_NOTIFY_WINDOW_HANDLE);
            }

            while (PInvoke.GetMessage(out MSG msg, default, 0, 0))
            {
                PInvoke.TranslateMessage(in msg);
                PInvoke.DispatchMessage(in msg);
            }

            if (!powerNotify.IsNull)
            {
                PInvoke.UnregisterSuspendResumeNotification(powerNotify);
            }
            if (!hwnd.IsNull)
            {
                PInvoke.DestroyWindow(hwnd);
            }
            PInvoke.UnregisterClass(className, hInstance);
        }
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static LRESULT WndProc(HWND hwnd, uint msg, WPARAM wParam, LPARAM lParam)
    {
        try
        {
            switch (msg)
            {
                case PInvoke.WM_DISPLAYCHANGE:
                case PInvoke.WM_DEVICECHANGE:
                    s_writer?.TryWrite(WmMessage.DisplayChange.Instance);
                    break;

                case PInvoke.WM_SETTINGCHANGE:
                    // Taskbar moved/resized — the work area changed.
                    if (wParam.Value == (nuint)Windows.Win32.UI.WindowsAndMessaging.SYSTEM_PARAMETERS_INFO_ACTION.SPI_SETWORKAREA)
                    {
                        s_writer?.TryWrite(WmMessage.DisplayChange.Instance);
                    }
                    break;

                case PInvoke.WM_POWERBROADCAST:
                    if (wParam.Value == PInvoke.PBT_APMRESUMEAUTOMATIC || wParam.Value == PInvoke.PBT_APMRESUMESUSPEND)
                    {
                        s_writer?.TryWrite(WmMessage.PowerResume.Instance);
                    }
                    break;
            }
        }
        catch
        {
            // Never throw across the native callback boundary.
        }

        return PInvoke.DefWindowProc(hwnd, msg, wParam, lParam);
    }
}
