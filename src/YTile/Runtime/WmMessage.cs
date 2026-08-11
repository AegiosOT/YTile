using YTile.Protocol;

namespace YTile.Runtime;

/// <summary>
/// The single message union consumed by the WindowManager actor. OS events,
/// IPC commands, and reaper ticks all arrive through one queue — there is no
/// other way to touch window-manager state.
/// </summary>
internal abstract record WmMessage
{
    internal sealed record Os(RawWinEvent Event) : WmMessage;

    internal sealed record Command(CommandRequest Request, TaskCompletionSource<CommandReply> Reply) : WmMessage;

    internal sealed record ReaperTick : WmMessage
    {
        public static readonly ReaperTick Instance = new();
    }

    internal sealed record DisplayChange : WmMessage
    {
        public static readonly DisplayChange Instance = new();
    }

    internal sealed record PowerResume : WmMessage
    {
        public static readonly PowerResume Instance = new();
    }
}

internal enum Direction
{
    Left,
    Right,
    Up,
    Down,
}

internal static class DirectionParser
{
    public static Direction? Parse(string? arg) => arg?.ToLowerInvariant() switch
    {
        "left" => Direction.Left,
        "right" => Direction.Right,
        "up" => Direction.Up,
        "down" => Direction.Down,
        _ => null,
    };
}
