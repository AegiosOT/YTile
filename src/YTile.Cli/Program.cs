using System.IO.Pipes;
using System.Text.Json;
using YTile.Protocol;

namespace YTile.Cli;

internal static class Program
{
    private const string Version = "0.1.0-dev";

    private static int Main(string[] args)
    {
        if (args.Length == 0 || args[0] is "-h" or "--help" or "help")
        {
            PrintHelp();
            return args.Length == 0 ? 2 : 0;
        }

        if (args[0] is "--version" or "-V")
        {
            Console.WriteLine($"ytile {Version}");
            return 0;
        }

        string cmd = args[0];
        string? arg = args.Length > 1 ? string.Join(' ', args[1..]) : null;

        switch (cmd)
        {
            case "state" or "pause" or "resume" or "retile" or "version" or "float" or "stop" or "reload" or "monocle":
                break;
            case "subscribe":
                return Subscribe();
            case "layout" or "focus" or "move" or "workspace" or "send" or "resize" when arg is not null:
                break;
            case "reserve" when args.Length == 6:
                break;
            case "layout":
                Console.Error.WriteLine("usage: ytile layout <bsp|columns>");
                return 2;
            case "focus" or "move":
                Console.Error.WriteLine($"usage: ytile {cmd} <left|right|up|down>");
                return 2;
            case "resize":
                Console.Error.WriteLine("usage: ytile resize <left|right|up|down> [px]");
                return 2;
            case "workspace" or "send":
                Console.Error.WriteLine($"usage: ytile {cmd} <1-9>");
                return 2;
            case "reserve":
                Console.Error.WriteLine("usage: ytile reserve <monitor> <left> <top> <right> <bottom>");
                return 2;
            default:
                Console.Error.WriteLine($"ytile: unknown command '{cmd}'");
                PrintHelp();
                return 2;
        }

        CommandReply? reply = Send(new CommandRequest(cmd, arg));
        if (reply is null)
        {
            return 1;
        }

        if (!reply.Ok)
        {
            Console.Error.WriteLine($"ytile: {reply.Error}");
            return 1;
        }

        if (reply.State is not null)
        {
            PrintState(reply.State);
        }
        else if (reply.Message is not null)
        {
            Console.WriteLine(reply.Message);
        }

        return 0;
    }

    /// <summary>Streams daemon notifications (one JSON per line) to stdout until
    /// the pipe closes — the integration point for bars and scripts.</summary>
    private static int Subscribe()
    {
        using var client = new NamedPipeClientStream(".", "ytile", PipeDirection.InOut, PipeOptions.None);
        try
        {
            client.Connect(2000);
        }
        catch (Exception)
        {
            Console.Error.WriteLine("ytile: cannot reach ytiled — is the daemon running?");
            return 1;
        }

        try
        {
            using var writer = new StreamWriter(client, leaveOpen: true) { AutoFlush = true };
            using var reader = new StreamReader(client, leaveOpen: true);
            writer.WriteLine(JsonSerializer.Serialize(new CommandRequest("subscribe"), ProtocolJsonContext.Default.CommandRequest));
            string? ack = reader.ReadLine();
            if (ack is null)
            {
                Console.Error.WriteLine("ytile: daemon closed the pipe without replying");
                return 1;
            }

            while (reader.ReadLine() is { } line)
            {
                Console.WriteLine(line);
            }
            return 0;
        }
        catch (IOException)
        {
            return 0; // daemon went away — clean end of stream
        }
    }

    private static CommandReply? Send(CommandRequest request)
    {
        using var client = new NamedPipeClientStream(".", "ytile", PipeDirection.InOut, PipeOptions.None);
        try
        {
            client.Connect(2000);
        }
        catch (TimeoutException)
        {
            // The pipe path exists while a daemon holds the single instance —
            // distinguishes "busy with another client" from "not running".
            Console.Error.WriteLine(File.Exists(@"\\.\pipe\ytile")
                ? "ytile: daemon is busy with another client — try again"
                : "ytile: cannot reach ytiled — is the daemon running?");
            return null;
        }
        catch (Exception)
        {
            Console.Error.WriteLine("ytile: cannot reach ytiled — is the daemon running?");
            return null;
        }

        try
        {
            // leaveOpen on both: the client stream is disposed exactly once, by
            // its own using — not a second time by reader/writer disposal.
            using var writer = new StreamWriter(client, leaveOpen: true) { AutoFlush = true };
            using var reader = new StreamReader(client, leaveOpen: true);
            writer.WriteLine(JsonSerializer.Serialize(request, ProtocolJsonContext.Default.CommandRequest));
            string? line = reader.ReadLine();
            if (line is null)
            {
                Console.Error.WriteLine("ytile: daemon closed the pipe without replying");
                return null;
            }

            return JsonSerializer.Deserialize(line, ProtocolJsonContext.Default.CommandReply);
        }
        catch (IOException ex)
        {
            Console.Error.WriteLine($"ytile: connection lost — {ex.Message}");
            return null;
        }
    }

    private static void PrintState(StateDto state)
    {
        Console.WriteLine($"ytiled {state.Version}{(state.Paused ? " [paused]" : "")}{(state.DryRun ? " [dry-run]" : "")}");
        for (int m = 0; m < state.Monitors.Count; m++)
        {
            MonitorDto monitor = state.Monitors[m];
            Console.WriteLine(
                $"monitor {m} {monitor.Device}{(monitor.Primary ? " (primary)" : "")} " +
                $"{monitor.WorkArea.W}x{monitor.WorkArea.H}@{monitor.WorkArea.X},{monitor.WorkArea.Y}");

            IReadOnlyList<WorkspaceDto> workspaces = monitor.Workspaces ?? [];
            for (int n = 0; n < workspaces.Count; n++)
            {
                WorkspaceDto ws = workspaces[n];
                bool active = n == monitor.Active;
                // Empty inactive workspaces are noise.
                if (!active && ws.Windows.Count == 0 && (ws.Floating?.Count ?? 0) == 0)
                {
                    continue;
                }

                Console.WriteLine($"  workspace {n + 1}{(active ? " (active)" : "")} [{ws.Layout}]");
                for (int i = 0; i < ws.Windows.Count; i++)
                {
                    WindowDto w = ws.Windows[i];
                    string marker = active && i == ws.Focused ? "*" : " ";
                    Console.WriteLine(
                        $"    {marker} {i} 0x{w.Hwnd:X8} {w.Exe,-20} {w.Rect.W}x{w.Rect.H}@{w.Rect.X},{w.Rect.Y}  \"{w.Title}\"");
                }

                foreach (WindowDto w in ws.Floating ?? [])
                {
                    Console.WriteLine(
                        $"    ~   0x{w.Hwnd:X8} {w.Exe,-20} {w.Rect.W}x{w.Rect.H}@{w.Rect.X},{w.Rect.Y}  \"{w.Title}\" (floating)");
                }
            }
        }
    }

    private static void PrintHelp()
    {
        Console.WriteLine(
            $"""
            ytile {Version} — CLI for the YTile daemon (ytiled)

            usage: ytile <command>

              state                     show monitors, workspaces, and windows
              focus <left|right|up|down>   focus the window in that direction
              move  <left|right|up|down>   swap focused window in that direction
              resize <left|right|up|down> [px]  grow the focused window that way (negative px shrinks)
              workspace <1-9>           switch the focused monitor's workspace
              send <1-9>                send the focused window to a workspace
              layout <bsp|columns>      set layout on the active workspace
              float                     toggle floating for the focused window
              monocle                   toggle fullscreen-within-layout for the focused window
              subscribe                 stream state-change notifications (NDJSON)
              reserve <m> <l> <t> <r> <b>  reserve screen edges on a monitor (bars)
              retile                    recompute and apply the layout
              reload                    reload ~/.config/ytile/ytile.json and resync
              pause                     restore hidden windows, stop reacting
              resume                    resync from the OS and start tiling
              stop                      shut the daemon down
              version                   daemon version
            """);
    }
}
