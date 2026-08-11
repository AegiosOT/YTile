using Windows.Win32;

namespace YTile.Runtime;

/// <summary>Semantic window-manager events, mapped from raw WinEvents.</summary>
internal enum WmEventKind
{
    Show,
    Hide,
    Destroy,
    FocusChange,
    Minimize,
    MoveResizeStart,
    MoveResizeEnd,
    Cloak,
    Uncloak,
    TitleUpdate,
}

internal static class WinEventMap
{
    public static WmEventKind? Map(uint eventId) => eventId switch
    {
        PInvoke.EVENT_OBJECT_DESTROY => WmEventKind.Destroy,
        PInvoke.EVENT_OBJECT_HIDE => WmEventKind.Hide,
        PInvoke.EVENT_OBJECT_CLOAKED => WmEventKind.Cloak,
        PInvoke.EVENT_OBJECT_UNCLOAKED => WmEventKind.Uncloak,
        PInvoke.EVENT_SYSTEM_MINIMIZESTART => WmEventKind.Minimize,
        // A minimize ending is a window coming back — same handling as Show.
        PInvoke.EVENT_OBJECT_SHOW or PInvoke.EVENT_SYSTEM_MINIMIZEEND => WmEventKind.Show,
        PInvoke.EVENT_OBJECT_FOCUS or PInvoke.EVENT_SYSTEM_FOREGROUND => WmEventKind.FocusChange,
        PInvoke.EVENT_SYSTEM_MOVESIZESTART => WmEventKind.MoveResizeStart,
        PInvoke.EVENT_SYSTEM_MOVESIZEEND => WmEventKind.MoveResizeEnd,
        // Some apps (e.g. Firefox) surface their real window only via a
        // NAMECHANGE at launch; promoting this to Show is a per-app quirk that
        // belongs in the application quirks db once it exists.
        PInvoke.EVENT_OBJECT_NAMECHANGE => WmEventKind.TitleUpdate,
        _ => null,
    };

    public static string RawName(uint eventId) => eventId switch
    {
        PInvoke.EVENT_SYSTEM_FOREGROUND => "SYSTEM_FOREGROUND",
        PInvoke.EVENT_SYSTEM_MOVESIZESTART => "SYSTEM_MOVESIZESTART",
        PInvoke.EVENT_SYSTEM_MOVESIZEEND => "SYSTEM_MOVESIZEEND",
        PInvoke.EVENT_SYSTEM_MINIMIZESTART => "SYSTEM_MINIMIZESTART",
        PInvoke.EVENT_SYSTEM_MINIMIZEEND => "SYSTEM_MINIMIZEEND",
        PInvoke.EVENT_OBJECT_DESTROY => "OBJECT_DESTROY",
        PInvoke.EVENT_OBJECT_SHOW => "OBJECT_SHOW",
        PInvoke.EVENT_OBJECT_HIDE => "OBJECT_HIDE",
        PInvoke.EVENT_OBJECT_FOCUS => "OBJECT_FOCUS",
        PInvoke.EVENT_OBJECT_NAMECHANGE => "OBJECT_NAMECHANGE",
        PInvoke.EVENT_OBJECT_CLOAKED => "OBJECT_CLOAKED",
        PInvoke.EVENT_OBJECT_UNCLOAKED => "OBJECT_UNCLOAKED",
        _ => $"0x{eventId:X4}",
    };
}
