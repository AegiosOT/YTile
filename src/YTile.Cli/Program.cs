using System.Diagnostics;
using System.IO.Pipes;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text.Json;
using Microsoft.Win32;
using YTile.Protocol;

namespace YTile.Cli;

internal static class Program
{
    private const string Version = "0.1.9";

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
        bool elevated = false;
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
                case "--elevated":
                    elevated = true;
                    break;
                case "--force" or "--dry-run":
                    daemonArgs.Add(a);
                    break;
                default:
                    Console.Error.WriteLine("usage: ytile start [--force] [--dry-run] [--elevated] [--whkd|--no-hotkeys]");
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
        };
        if (elevated && !IsAdminOnlyDirectory(
                Path.GetDirectoryName(sibling) ?? AppContext.BaseDirectory, out string weak))
        {
            // Not fatal: the user consents at a UAC prompt, and nothing here
            // survives the session. Still worth saying, because they are
            // elevating a binary that other code running as them could swap.
            Console.Error.WriteLine($"ytile: warning - {weak} in the install directory,");
            Console.Error.WriteLine("       so anything running as you could replace the binary being elevated.");
        }

        if (elevated)
        {
            // Windows only raises integrity through ShellExecute, which cannot
            // redirect the child's streams. That costs nothing here: --log
            // already sends every line to the log file. Without elevation the
            // daemon cannot position windows owned by elevated processes
            // (Task Manager and the other auto-elevating system tools) — UIPI
            // fails those SetWindowPos calls outright.
            psi.UseShellExecute = true;
            psi.Verb = "runas";
            psi.WindowStyle = ProcessWindowStyle.Hidden;
        }
        else
        {
            psi.UseShellExecute = false;
            psi.CreateNoWindow = true;
            // Without this the daemon inherits our stdout/stderr handles and
            // holds them for its whole life, so anything that captures or pipes
            // `ytile start` blocks until the daemon exits. Nothing is lost:
            // --log sends the daemon's output to a file.
            psi.RedirectStandardOutput = true;
            psi.RedirectStandardError = true;
        }
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
        catch (System.ComponentModel.Win32Exception ex) when (ex.NativeErrorCode == 1223)
        {
            // ERROR_CANCELLED: the user dismissed the UAC prompt. That is a
            // decision, not a fault — say so without a stack-trace-shaped message.
            Console.Error.WriteLine("ytile: elevation declined — ytiled was not started.");
            return 1;
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

    /// <summary>Scheduled task used for elevated autostart. The Run key cannot
    /// raise integrity — everything it launches starts at medium, and a medium
    /// daemon cannot position windows owned by elevated processes. A logon task
    /// registered at RunLevel HIGHEST can, and does it without a UAC prompt.</summary>
    private const string TaskName = "YTile";

    private const string AutostartUsage =
        "usage: ytile autostart <on [--elevated] [--whkd|--no-hotkeys]|off|status>";

    /// <summary>Manages the login entry that launches `ytile start` — an HKCU Run
    /// value normally, a highest-privilege scheduled task with --elevated. The
    /// daemon still auto-pauses if komorebi is running, so an enabled entry is
    /// safe even while another tiler owns the desktop.</summary>
    private static int Autostart(string[] args)
    {
        string? mode = args.Length > 0 ? args[0] : null;
        bool elevated = false;
        string? flag = null;
        for (int i = 1; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--elevated":
                    elevated = true;
                    break;
                case "--whkd" or "--no-hotkeys":
                    flag = args[i];
                    break;
                default:
                    Console.Error.WriteLine(AutostartUsage);
                    return 2;
            }
        }

        if (args.Length > 1 && mode is not ("on" or "enable"))
        {
            Console.Error.WriteLine(AutostartUsage);
            return 2;
        }

        switch (mode)
        {
            case "on" or "enable":
            {
                string self = Environment.ProcessPath
                    ?? Path.Combine(AppContext.BaseDirectory, "ytile.exe");
                string command = $"\"{self}\" start{(flag is null ? "" : $" {flag}")}";
                if (elevated)
                {
                    return RegisterElevatedTask(command);
                }

                // Both entries firing would start two daemons and let the
                // single-instance lock decide at random which one survives.
                RunSchtasks(["/delete", "/tn", TaskName, "/f"], out _);
                using RegistryKey key = Registry.CurrentUser.CreateSubKey(RunKeyPath);
                key.SetValue(RunValueName, command);
                Console.WriteLine($"autostart on: {command}");
                return 0;
            }
            case "off" or "disable":
            {
                bool removed;
                using (RegistryKey? key = Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: true))
                {
                    removed = key?.GetValue(RunValueName) is not null;
                    if (removed)
                    {
                        key!.DeleteValue(RunValueName);
                    }
                }

                // Off must mean off whichever way it was turned on.
                if (RunSchtasks(["/delete", "/tn", TaskName, "/f"], out _))
                {
                    removed = true;
                }

                Console.WriteLine(removed ? "autostart off." : "autostart was already off.");
                return 0;
            }
            case "status" or null:
            {
                using RegistryKey? key = Registry.CurrentUser.OpenSubKey(RunKeyPath);
                string? run = key?.GetValue(RunValueName) as string;
                bool task = RunSchtasks(["/query", "/tn", TaskName], out _);
                if (run is null && !task)
                {
                    Console.WriteLine("autostart: off");
                    return 0;
                }
                if (run is not null)
                {
                    Console.WriteLine($"autostart: on — {run}");
                }
                if (task)
                {
                    Console.WriteLine($"autostart: on (elevated) — scheduled task \"{TaskName}\", run level highest");
                }
                return 0;
            }
            default:
                Console.Error.WriteLine(AutostartUsage);
                return 2;
        }
    }

    // TrustedInstaller owns most of %ProgramFiles% and is not a SID that
    // WellKnownSidType can name.
    private const string TrustedInstallerSid =
        "S-1-5-80-956008885-3418522649-1831038044-1853292631-2271478464";

    /// <summary>Rights that let a principal swap the binary out: directly, by
    /// deleting it and writing its own, or by granting itself the rest.</summary>
    private const FileSystemRights PlantingRights =
        FileSystemRights.WriteData                      // also CreateFiles
        | FileSystemRights.AppendData                   // also CreateDirectories
        | FileSystemRights.Delete
        | FileSystemRights.DeleteSubdirectoriesAndFiles
        | FileSystemRights.WriteAttributes
        | FileSystemRights.WriteExtendedAttributes
        | FileSystemRights.ChangePermissions
        | FileSystemRights.TakeOwnership;

    /// <summary>Principals that are already administrators, so write access
    /// gives an attacker nothing they would not already have. CREATOR OWNER
    /// qualifies only because ownership is checked separately: it resolves to
    /// whoever owns the directory, which must itself be safe.</summary>
    private static bool IsAdminPrincipal(SecurityIdentifier sid)
        => sid.IsWellKnown(WellKnownSidType.LocalSystemSid)
        || sid.IsWellKnown(WellKnownSidType.BuiltinAdministratorsSid)
        || sid.IsWellKnown(WellKnownSidType.CreatorOwnerSid)
        || sid.Value == TrustedInstallerSid;

    private static string Describe(SecurityIdentifier sid)
    {
        try
        {
            return sid.Translate(typeof(NTAccount)).Value;
        }
        catch (IdentityNotMappedException)
        {
            return sid.Value;
        }
    }

    /// <summary>
    /// Whether only administrators can change what lives in <paramref name="dir"/>.
    ///
    /// A logon task at run level HIGHEST hands its target a full administrator
    /// token at every logon, with no prompt and nobody watching. Whoever can
    /// replace the binary therefore inherits that token. In a per-user install
    /// the ordinary medium-integrity token owns the directory outright, which
    /// would turn an unprivileged file write into silent, persistent
    /// administrator execution: the classic weakly-permissioned privileged-task
    /// escalation. Refusing to register the task is the only honest answer.
    ///
    /// Ownership is checked as well as the DACL, because an owner holds an
    /// implicit WRITE_DAC and can grant the rest straight back.
    /// </summary>
    private static bool IsAdminOnlyDirectory(string dir, out string reason)
    {
        try
        {
            DirectorySecurity sec = new DirectoryInfo(dir)
                .GetAccessControl(AccessControlSections.Access | AccessControlSections.Owner);

            if (sec.GetOwner(typeof(SecurityIdentifier)) is SecurityIdentifier owner
                && !IsAdminPrincipal(owner))
            {
                reason = $"it is owned by {Describe(owner)}, who can rewrite its permissions at will";
                return false;
            }

            foreach (AuthorizationRule entry in sec.GetAccessRules(true, true, typeof(SecurityIdentifier)))
            {
                if (entry is not FileSystemAccessRule rule
                    || rule.AccessControlType != AccessControlType.Allow
                    || (rule.FileSystemRights & PlantingRights) == 0
                    || rule.IdentityReference is not SecurityIdentifier sid
                    || IsAdminPrincipal(sid))
                {
                    continue;
                }

                reason = $"{Describe(sid)} can modify its contents";
                return false;
            }

            reason = "";
            return true;
        }
        catch (Exception ex)
        {
            // An unreadable ACL is not evidence of a safe one.
            reason = $"its permissions could not be read ({ex.Message})";
            return false;
        }
    }

    /// <summary>Registers the logon task that brings ytiled up elevated.
    /// /rl highest is the part that makes elevated windows tileable; /it keeps
    /// the task interactive-only so no password has to be stored. Neither is
    /// grantable from a medium-integrity process, so this one command has to be
    /// run from an admin shell — after that, logon is silent forever.</summary>
    private static int RegisterElevatedTask(string command)
    {
        // The task also launches ytiled.exe and ykeys.exe from beside this
        // binary, each inheriting the elevated token, so the whole directory
        // must be administrator-only, not merely the file named in /tr.
        string dir = Path.GetDirectoryName(Environment.ProcessPath ?? "") is { Length: > 0 } d
            ? d
            : AppContext.BaseDirectory;
        if (!IsAdminOnlyDirectory(dir, out string unsafeReason))
        {
            Console.Error.WriteLine($"ytile: refusing to register an elevated logon task for {dir}");
            Console.Error.WriteLine($"       because {unsafeReason}.");
            Console.Error.WriteLine();
            Console.Error.WriteLine("A task at run level HIGHEST runs this binary with a full administrator token");
            Console.Error.WriteLine("at every logon, with no prompt. Anything able to replace the file inherits");
            Console.Error.WriteLine("that token, so it has to sit where only administrators can write.");
            Console.Error.WriteLine();
            Console.Error.WriteLine(@"Reinstall for all users from an admin terminal (installs to %ProgramFiles%\ytile):");
            Console.Error.WriteLine("    $env:YTILE_ALLUSERS = 1");
            Console.Error.WriteLine("    irm https://raw.githubusercontent.com/AegiosOT/YTile/main/scripts/install.ps1 | iex");
            Console.Error.WriteLine();
            Console.Error.WriteLine("Or skip autostart and use 'ytile start --elevated', which prompts once per");
            Console.Error.WriteLine("session instead of granting administrator silently forever.");
            return 1;
        }

        string user = $@"{Environment.UserDomainName}\{Environment.UserName}";
        bool ok = RunSchtasks(
            ["/create", "/tn", TaskName, "/tr", command, "/sc", "onlogon",
             "/ru", user, "/it", "/rl", "highest", "/f"],
            out string output);

        if (!ok)
        {
            Console.Error.WriteLine("ytile: could not register the elevated autostart task.");
            string detail = output.Trim();
            if (detail.Length > 0)
            {
                Console.Error.WriteLine(detail);
            }
            Console.Error.WriteLine(
                "Registering a highest-privilege task needs elevation — run this once from an admin terminal.");
            return 1;
        }

        // A leftover Run value would start a second, unelevated daemon.
        using (RegistryKey? key = Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: true))
        {
            if (key?.GetValue(RunValueName) is not null)
            {
                key.DeleteValue(RunValueName);
            }
        }

        Console.WriteLine($"autostart on (elevated): {command}");
        Console.WriteLine($"Registered scheduled task \"{TaskName}\" at run level highest — no UAC prompt at logon.");
        return 0;
    }

    /// <summary>ArgumentList, not a command string: the task's /tr value contains
    /// both quotes and spaces, and hand-escaping that is how these break.</summary>
    private static bool RunSchtasks(string[] arguments, out string output)
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = "schtasks.exe",
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };
            foreach (string a in arguments)
            {
                psi.ArgumentList.Add(a);
            }

            using Process proc = Process.Start(psi)!;
            output = proc.StandardOutput.ReadToEnd() + proc.StandardError.ReadToEnd();
            proc.WaitForExit();
            return proc.ExitCode == 0;
        }
        catch (Exception ex)
        {
            output = ex.Message;
            return false;
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

              start [--force] [--dry-run] [--elevated] [--whkd|--no-hotkeys]
                                        launch ytiled in the background plus the
                                        bundled ykeys hotkey daemon (--whkd uses whkd
                                        instead, --no-hotkeys skips both;
                                        --elevated runs the daemon as administrator,
                                        the only way to tile windows owned by an
                                        elevated process, such as Task Manager;
                                        logs to %LOCALAPPDATA%\ytile\ytiled.log)
              autostart <on [--elevated] [--whkd|--no-hotkeys]|off|status>
                                        run 'ytile start' at login (--elevated
                                        registers a highest-privilege logon task
                                        instead of the Run key, so the daemon comes
                                        up elevated with no UAC prompt; registering
                                        it needs an admin terminal once)
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
