using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Threading.Channels;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.UI.Accessibility;
using Windows.Win32.UI.WindowsAndMessaging;
using YTile.Runtime;

namespace YTile.Win32;

/// <summary>
/// Owns the WinEvent hooks. A dedicated thread installs narrow-range
/// WINEVENT_OUTOFCONTEXT hooks and pumps messages — out-of-context hook
/// callbacks are delivered on the installing thread from inside GetMessageW,
/// so the pump is load-bearing, not ceremony.
/// The callback never blocks and never throws: the channel is bounded with
/// drop-oldest, and a dropped event is recovered later by state resync, not
/// by backpressure against the OS.
/// </summary>
internal static unsafe class EventListener
{
    // Narrow registration: only the ranges we consume, instead of hooking
    // EVENT_MIN..EVENT_MAX and discarding ~90% in the callback.
    private static readonly (uint Min, uint Max)[] Ranges =
    [
        (PInvoke.EVENT_SYSTEM_FOREGROUND, PInvoke.EVENT_SYSTEM_FOREGROUND),
        (PInvoke.EVENT_SYSTEM_MOVESIZESTART, PInvoke.EVENT_SYSTEM_MOVESIZEEND),
        (PInvoke.EVENT_SYSTEM_MINIMIZESTART, PInvoke.EVENT_SYSTEM_MINIMIZEEND),
        (PInvoke.EVENT_OBJECT_DESTROY, PInvoke.EVENT_OBJECT_HIDE),
        (PInvoke.EVENT_OBJECT_FOCUS, PInvoke.EVENT_OBJECT_FOCUS),
        (PInvoke.EVENT_OBJECT_NAMECHANGE, PInvoke.EVENT_OBJECT_NAMECHANGE),
        (PInvoke.EVENT_OBJECT_CLOAKED, PInvoke.EVENT_OBJECT_UNCLOAKED),
    ];

    private static ChannelWriter<RawWinEvent>? s_writer;
    private static volatile uint s_pumpThreadId;

    public static Thread Start(ChannelWriter<RawWinEvent> writer)
    {
        s_writer = writer;
        var thread = new Thread(ThreadProc) { Name = "ytile-winevent-pump", IsBackground = true };
        thread.Start();
        return thread;
    }

    /// <summary>Posts WM_QUIT to the pump; the thread unhooks and completes the channel.</summary>
    public static void Stop()
    {
        uint id = s_pumpThreadId;
        if (id != 0)
        {
            PInvoke.PostThreadMessage(id, PInvoke.WM_QUIT, default, default);
        }
    }

    private static void ThreadProc()
    {
        s_pumpThreadId = PInvoke.GetCurrentThreadId();

        var hooks = new HWINEVENTHOOK[Ranges.Length];
        for (int i = 0; i < Ranges.Length; i++)
        {
            hooks[i] = PInvoke.SetWinEventHook(
                Ranges[i].Min,
                Ranges[i].Max,
                (HMODULE)IntPtr.Zero,
                &OnWinEvent,
                0,
                0,
                PInvoke.WINEVENT_OUTOFCONTEXT | PInvoke.WINEVENT_SKIPOWNPROCESS);
        }

        while (PInvoke.GetMessage(out MSG msg, default, 0, 0))
        {
            PInvoke.TranslateMessage(in msg);
            PInvoke.DispatchMessage(in msg);
        }

        foreach (var hook in hooks)
        {
            if (!hook.IsNull)
            {
                PInvoke.UnhookWinEvent(hook);
            }
        }

        s_writer?.TryComplete();
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static void OnWinEvent(
        HWINEVENTHOOK hook,
        uint eventId,
        HWND hwnd,
        int idObject,
        int idChild,
        uint idEventThread,
        uint timeMs)
    {
        // Nothing may escape an UnmanagedCallersOnly frame — an unhandled
        // exception here aborts the whole AOT process.
        try
        {
            if (hwnd.IsNull || idObject != (int)OBJECT_IDENTIFIER.OBJID_WINDOW || idChild != (int)PInvoke.CHILDID_SELF)
            {
                return;
            }

            // Bounded + DropOldest: TryWrite always succeeds without blocking.
            s_writer?.TryWrite(new RawWinEvent(eventId, (nint)hwnd.Value, timeMs));
        }
        catch
        {
            // Swallow: a lost event is recovered by resync, a crash is not.
        }
    }
}
