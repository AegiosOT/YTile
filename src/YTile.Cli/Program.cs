using System.Diagnostics;
using System.IO.Pipes;
using System.Text.Json;
using Microsoft.Win32;
using YTile.Protocol;

namespace YTile.Cli;

internal static class Program
{
    private const string Version = "0.1.6";

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
            case "state" or "pause" or "resume" or "retile" or "version" or "float" or "reload" or "monocle":
                break;
            case "subscribe":
                return Subscribe();
            case "start":
                return Start(args[1..]);
            case "stop":
                return Stop(args[1..]);
            case "autostart":
                return Autostart(args[1..]);
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

    private static readonly string LogPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ytile", "ytiled.log");

    private static readonly string YKeysLogPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ykeys", "ykeys.log");

    private enum HotkeyDaemon
    {
        YKeys,
        Whkd,
        None,
    }

    private static bool StartHotkeys(HotkeyDaemon choice) => choice switch
    {
        HotkeyDaemon.Whkd => StartWhkd(),
        HotkeyDaemon.YKeys => StartYKeys(),
        _ => true,
    };

    /// <summary>Launches ytiled.exe detached with no visible console, logging to
    /// %LOCALAPPDATA%\ytile\ytiled.log, and waits for its IPC pipe to appear.
    /// The bundled ykeys hotkey daemon comes up alongside it by default;
    /// --whkd starts whkd instead, --no-hotkeys starts neither.</summary>
    private static int Start(string[] extraArgs)
    {
        var hotkeys = HotkeyDaemon.YKeys;
        var daemonArgs = new List<string>();
        foreach (string a in extraArgs)
        {
            switch (a)
            {
                case "--whkd" when hotkeys is HotkeyDaemon.None:
                case "--no-hotkeys" when hotkeys is HotkeyDaemon.Whkd:
                    Console.Error.WriteLine("ytile: --whkd and --no-hotkeys are mutually exclusive");
                    return 2;
                case "--whkd":
                    hotkeys = HotkeyDaemon.Whkd;
                    break;
                case "--no-hotkeys":
                    hotkeys = HotkeyDaemon.None;
                    break;
                case "--force" or "--dry-run":
                    daemonArgs.Add(a);
                    break;
                default:
                    Console.Error.WriteLine("usage: ytile start [--force] [--dry-run] [--whkd|--no-hotkeys]");
                    return 2;
            }
        }

        if (File.Exists(@"\\.\pipe\ytile"))
        {
            Console.Error.WriteLine("ytile: ytiled is already running.");
            // Still bring the hotkey daemon up: after a crash or manual kill of
            // it alone, `ytile start` is the natural "bring everything up" retry.
            StartHotkeys(hotkeys);
            return 1;
        }

        // Prefer the daemon that ships next to this CLI; fall back to PATH.
        string sibling = Path.Combine(AppContext.BaseDirectory, "ytiled.exe");
        var psi = new ProcessStartInfo
        {
            FileName = File.Exists(sibling) ? sibling : "ytiled.exe",
            UseShellExecute = false,
            CreateNoWindow = true,
            // Without this the daemon inherits our stdout/stderr handles and
            // holds them for its whole life, so anything that captures or pipes
            // `ytile start` blocks until the daemon exits. Nothing is lost:
            // --log sends the daemon's output to a file.
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        psi.ArgumentList.Add("--log");
        foreach (string a in daemonArgs)
        {
            psi.ArgumentList.Add(a);
        }

        Process proc;
        try
        {
            proc = Process.Start(psi)!;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"ytile: cannot start ytiled — {ex.Message}");
            return 1;
        }

        // Give it a moment to take the single-instance lock and open the pipe.
        for (int i = 0; i < 20; i++)
        {
            Thread.Sleep(100);
            if (proc.HasExited)
            {
                Console.Error.WriteLine($"ytile: ytiled exited immediately (code {proc.ExitCode}) — see {LogPath}");
                return 1;
            }
            if (File.Exists(@"\\.\pipe\ytile"))
            {
                Console.WriteLine($"ytiled started (pid {proc.Id}), logging to {LogPath}");
                return StartHotkeys(hotkeys) ? 0 : 1;
            }
        }

        Console.WriteLine($"ytiled launched (pid {proc.Id}) but its pipe hasn't appeared yet — check {LogPath}");
        return StartHotkeys(hotkeys) ? 0 : 1;
    }

    /// <summary>Sends stop to the daemon and takes the bundled ykeys down with
    /// it; --whkd also kills the whkd hotkey daemon (the counterpart of
    /// `ytile start --whkd`), --no-hotkeys leaves hotkey daemons alone — the
    /// counterpart of `start --no-hotkeys` for people running their own ykeys.</summary>
    private static int Stop(string[] extraArgs)
    {
        bool withWhkd = false;
        bool noHotkeys = false;
        foreach (string a in extraArgs)
        {
            switch (a)
            {
                case "--whkd":
                    withWhkd = true;
                    break;
                case "--no-hotkeys":
                    noHotkeys = true;
                    break;
                default:
                    Console.Error.WriteLine("usage: ytile stop [--whkd|--no-hotkeys]");
                    return 2;
            }
        }

        int rc = 0;
        CommandReply? reply = Send(new CommandRequest("stop"));
        if (reply is null)
        {
            rc = 1;
        }
        else if (!reply.Ok)
        {
            Console.Error.WriteLine($"ytile: {reply.Error}");
            rc = 1;
        }
        else if (reply.Message is not null)
        {
            Console.WriteLine(reply.Message);
        }

        // Tear the hotkey daemons down even when the daemon was already gone:
        // `stop` means "take it all down", whatever half is still standing.
        // ykeys is ours by default, whkd only when asked for.
        if (!noHotkeys && !StopYKeys())
        {
            rc = 1;
        }
        if (withWhkd && !StopWhkd())
        {
            rc = 1;
        }
        return rc;
    }

    /// <summary>Instances of a companion in this session only. Hotkeys are
    /// per-session, so another user's daemon (fast user switching) neither
    /// serves this session's keys nor is ours to kill.</summary>
    private static Process[] SessionProcesses(string name)
    {
        using Process current = Process.GetCurrentProcess();
        int session = current.SessionId;
        var mine = new List<Process>();
        foreach (Process proc in Process.GetProcessesByName(name))
        {
            if (proc.SessionId == session)
            {
                mine.Add(proc);
            }
            else
            {
                proc.Dispose();
            }
        }
        return mine.ToArray();
    }

    /// <summary>Launches the bundled ykeys hotkey daemon (hidden) unless one is
    /// already running in this session. A missing ykeys.exe is a hint, not a
    /// failure — hotkeys are optional and whkd users won't have it installed.</summary>
    private static bool StartYKeys()
    {
        Process[] running = SessionProcesses("ykeys");
        if (running.Length > 0)
        {
            foreach (Process proc in running)
            {
                proc.Dispose();
            }
            Console.WriteLine("ykeys already running");
            return true;
        }

        // Prefer the ykeys that ships next to this CLI; fall back to PATH.
        string sibling = Path.Combine(AppContext.BaseDirectory, "ykeys.exe");
        try
        {
            Process? launched = Process.Start(new ProcessStartInfo
            {
                FileName = File.Exists(sibling) ? sibling : "ykeys.exe",
                Arguments = "--log",
                UseShellExecute = true,
                WindowStyle = ProcessWindowStyle.Hidden,
            });
            if (launched is not null)
            {
                using (launched)
                {
                    // ykeys runs fine without a config (no bindings yet), so an
                    // immediate exit means something is genuinely wrong.
                    Thread.Sleep(400);
                    if (launched.HasExited)
                    {
                        Console.Error.WriteLine($"ytile: ykeys exited immediately — see {YKeysLogPath}");
                        return false;
                    }
                }
            }
        }
        // Only a genuine not-found is a soft skip; access-denied (AV block),
        // bad image, and the like must fail loudly, not claim "not found".
        catch (Exception ex) when (ex is FileNotFoundException or System.ComponentModel.Win32Exception { NativeErrorCode: 2 })
        {
            Console.WriteLine(
                "ytile: ykeys.exe not found — hotkeys off (reinstall YTile, use --whkd, or --no-hotkeys to silence this)");
            return true;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"ytile: cannot start ykeys — {ex.Message}");
            return false;
        }

        Console.WriteLine($"ykeys started (config: ~/.config/ykeys/ykeys.json, log: {YKeysLogPath})");
        return true;
    }

    /// <summary>ykeys has no IPC; kill is the stop. Quiet when none is running —
    /// whkd users see no noise from the default stop path.</summary>
    private static bool StopYKeys()
    {
        bool ok = true;
        foreach (Process proc in SessionProcesses("ykeys"))
        {
            using (proc)
            {
                try
                {
                    proc.Kill();
                    Console.WriteLine($"ykeys stopped (pid {proc.Id})");
                }
                catch (Exception ex)
                {
                    Console.Error.WriteLine($"ytile: cannot stop ykeys (pid {proc.Id}) — {ex.Message}");
                    ok = false;
                }
            }
        }
        return ok;
    }

    /// <summary>Launches whkd with a hidden window unless it is already running
    /// in this session. UseShellExecute keeps our stdio handles out of it: a
    /// redirected pipe nobody drains would block whkd's own logging, and an
    /// inherited one keeps `ytile start | ...` captures open for whkd's whole
    /// life. Returns false when whkd could not be brought up.</summary>
    private static bool StartWhkd()
    {
        Process[] running = SessionProcesses("whkd");
        if (running.Length > 0)
        {
            foreach (Process proc in running)
            {
                proc.Dispose();
            }
            Console.WriteLine("whkd already running");
            return true;
        }

        Process? launched;
        try
        {
            launched = Process.Start(new ProcessStartInfo
            {
                FileName = "whkd",
                UseShellExecute = true,
                WindowStyle = ProcessWindowStyle.Hidden,
            });
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine(
                $"ytile: cannot start whkd — {ex.Message} (is whkd installed? https://github.com/LGUG2Z/whkd)");
            return false;
        }

        if (launched is not null)
        {
            using (launched)
            {
                // whkd panics straight away without ~/.config/whkdrc, and its
                // hidden console vanishes with it — this check is the only
                // diagnostic the user will ever see.
                Thread.Sleep(400);
                if (launched.HasExited)
                {
                    Console.Error.WriteLine(
                        "ytile: whkd exited immediately — is ~/.config/whkdrc present? see examples/whkdrc-ytile");
                    return false;
                }
            }
        }

        Console.WriteLine("whkd started");
        return true;
    }

    /// <summary>whkd has no IPC to ask it to exit, so kill is the only stop.
    /// Returns false when a kill failed; "not running" is the desired end
    /// state, not a failure.</summary>
    private static bool StopWhkd()
    {
        Process[] procs = SessionProcesses("whkd");
        if (procs.Length == 0)
        {
            Console.WriteLine("whkd not running");
            return true;
        }

        bool ok = true;
        foreach (Process proc in procs)
        {
            using (proc)
            {
                try
                {
                    proc.Kill();
                    Console.WriteLine($"whkd stopped (pid {proc.Id})");
                }
                catch (Exception ex)
                {
                    Console.Error.WriteLine($"ytile: cannot stop whkd (pid {proc.Id}) — {ex.Message}");
                    ok = false;
                }
            }
        }
        return ok;
    }

    private const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string RunValueName = "YTile";

    /// <summary>Manages the HKCU Run entry that launches `ytile start` at login.
    /// The daemon still auto-pauses if komorebi is running, so an enabled entry
    /// is safe even while another tiler owns the desktop.</summary>
    private static int Autostart(string[] args)
    {
        string? mode = args.Length > 0 ? args[0] : null;
        string? flag = args.Length == 2 && args[1] is "--whkd" or "--no-hotkeys" ? args[1] : null;
        if (args.Length > 1 && (flag is null || mode is not ("on" or "enable")))
        {
            Console.Error.WriteLine("usage: ytile autostart <on [--whkd|--no-hotkeys]|off|status>");
            return 2;
        }

        switch (mode)
        {
            case "on" or "enable":
            {
                string self = Environment.ProcessPath
                    ?? Path.Combine(AppContext.BaseDirectory, "ytile.exe");
                string command = $"\"{self}\" start{(flag is null ? "" : $" {flag}")}";
                using RegistryKey key = Registry.CurrentUser.CreateSubKey(RunKeyPath);
                key.SetValue(RunValueName, command);
                Console.WriteLine($"autostart on: {command}");
                return 0;
            }
            case "off" or "disable":
            {
                using RegistryKey? key = Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: true);
                if (key?.GetValue(RunValueName) is null)
                {
                    Console.WriteLine("autostart was already off.");
                    return 0;
                }
                key.DeleteValue(RunValueName);
                Console.WriteLine("autostart off.");
                return 0;
            }
            case "status" or null:
            {
                using RegistryKey? key = Registry.CurrentUser.OpenSubKey(RunKeyPath);
                Console.WriteLine(key?.GetValue(RunValueName) is string s
                    ? $"autostart: on — {s}"
                    : "autostart: off");
                return 0;
            }
            default:
                Console.Error.WriteLine("usage: ytile autostart <on [--whkd|--no-hotkeys]|off|status>");
                return 2;
        }
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

              start [--force] [--dry-run] [--whkd|--no-hotkeys]
                                        launch ytiled in the background plus the
                                        bundled ykeys hotkey daemon (--whkd uses whkd
                                        instead, --no-hotkeys skips both;
                                        logs to %LOCALAPPDATA%\ytile\ytiled.log)
              autostart <on [--whkd|--no-hotkeys]|off|status>  run 'ytile start' at login
              state                     show monitors, workspaces, and windows
              focus <left|right|up|down>   focus that way (crosses monitors at the edge)
              move  <left|right|up|down>   swap focused window that way (crosses monitors)
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
              stop [--whkd|--no-hotkeys]  shut the daemon and ykeys down (--whkd stops
                                        whkd too, --no-hotkeys leaves hotkey daemons alone)
              version                   daemon version
            """);
    }
}
