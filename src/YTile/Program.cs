using System.Diagnostics;
using System.Security.Principal;
using System.Threading.Channels;
using Windows.Win32;
using Windows.Win32.UI.HiDpi;
using YTile.Config;
using YTile.Runtime;
using YTile.Win32;

namespace YTile;

internal static class Program
{
    private const string Version = "0.1.9";

    private static async Task<int> Main(string[] args)
    {
        if (args.Contains("--version"))
        {
            Console.WriteLine($"ytiled {Version}");
            return 0;
        }

        // --log: headless mode (`ytile start` / autostart) — everything the
        // daemon would print goes to a file instead of the hidden console.
        // Truncates per session; FileShare.Read lets `ytile`/editors tail it.
        if (args.Contains("--log"))
        {
            string logDir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ytile");
            Directory.CreateDirectory(logDir);
            var log = new StreamWriter(new FileStream(
                Path.Combine(logDir, "ytiled.log"),
                FileMode.Create, FileAccess.Write, FileShare.Read))
            { AutoFlush = true };
            Console.SetOut(log);
            Console.SetError(log);
        }

        // Physical-pixel window rects on mixed-DPI setups.
        PInvoke.SetProcessDpiAwarenessContext(DPI_AWARENESS_CONTEXT.DPI_AWARENESS_CONTEXT_PER_MONITOR_AWARE_V2);

        var raw = Channel.CreateBounded<RawWinEvent>(new BoundedChannelOptions(256)
        {
            SingleReader = true,
            SingleWriter = true,
            FullMode = BoundedChannelFullMode.DropOldest,
        });

        using var cts = new CancellationTokenSource();
        Console.CancelKeyPress += (_, e) =>
        {
            e.Cancel = true;
            EventListener.Stop();
            cts.Cancel();
        };

        if (args.Contains("--debug-events"))
        {
            Console.WriteLine($"ytiled {Version} — event debug mode, Ctrl+C to exit.");
            EventListener.Start(raw.Writer);
            await DebugEventDumper.RunAsync(raw.Reader, cts.Token);
            return 0;
        }

        bool dryRun = args.Contains("--dry-run");
        bool force = args.Contains("--force");

        // Semaphore, not Mutex: it has no thread affinity, so the async
        // continuation at shutdown can release it before the drain delay.
        using var instanceLock = new Semaphore(1, 1, @"Local\ytiled-instance", out _);
        if (!instanceLock.WaitOne(0))
        {
            Console.Error.WriteLine("ytiled: another instance is already running.");
            return 1;
        }

        bool startPaused = false;
        if (!force && Process.GetProcessesByName("komorebi").Length > 0)
        {
            startPaused = true;
            Console.WriteLine("komorebi.exe is running — starting PAUSED so two tiling managers don't fight.");
            Console.WriteLine("Stop komorebi and run 'ytile resume', or restart ytiled with --force.");
        }

        // Session-global focus policy: only when actually managing for real.
        if (!dryRun && !startPaused)
        {
            FocusControl.Init();
        }

        var wm = Channel.CreateUnbounded<WmMessage>(new UnboundedChannelOptions { SingleReader = true });

        EventListener.Start(raw.Writer);
        _ = Task.Run(async () =>
        {
            try
            {
                await foreach (RawWinEvent e in raw.Reader.ReadAllAsync(cts.Token))
                {
                    wm.Writer.TryWrite(new WmMessage.Os(e));
                }
            }
            catch (OperationCanceledException)
            {
            }
        }, CancellationToken.None);

        DisplayListener.Start(wm.Writer);
        var events = new EventHub();
        var ipc = new IpcServer(wm.Writer, events);
        _ = Task.Run(() => events.RunAsync(cts.Token), CancellationToken.None);
        _ = Task.Run(() => ipc.RunAsync(cts.Token), CancellationToken.None);
        _ = Task.Run(() => Reaper.RunAsync(wm.Writer, cts.Token), CancellationToken.None);

        YTileConfig config = YTileConfig.Load(null, out string? configError);
        Console.WriteLine(File.Exists(YTileConfig.DefaultPath)
            ? $"config: {YTileConfig.DefaultPath}"
            : $"config: defaults (no file at {YTileConfig.DefaultPath})");
        if (configError is not null)
        {
            Console.WriteLine($"config problems: {configError}");
        }

        Console.WriteLine($"ytiled {Version}{(dryRun ? " [dry-run]" : "")} — Ctrl+C to exit.");
        // Worth a line in every log: an unelevated daemon silently cannot move
        // windows owned by elevated processes, and that looks like a YTile bug
        // rather than a Windows access check unless the log says otherwise.
        Console.WriteLine(IsElevated()
            ? "elevated: yes — windows owned by elevated processes can be tiled"
            : "elevated: no — windows owned by elevated processes (Task Manager and other "
              + "auto-elevating system tools) cannot be tiled; restart with 'ytile start --elevated'");
        var manager = new WindowManager(Version, dryRun, startPaused, config, events);
        await manager.RunAsync(wm.Reader, cts.Token);

        // Reached via Ctrl+C or 'ytile stop'. Shut down the listener and IPC so
        // no new commands queue against the dead actor, let a successor start,
        // then give the in-flight stop reply a moment to flush.
        EventListener.Stop();
        DisplayListener.Stop();
        cts.Cancel();
        instanceLock.Release();
        await Task.Delay(300, CancellationToken.None);
        return 0;
    }

    /// <summary>Whether this process holds an elevated token. UIPI lets us
    /// position another window only when we are at or above its integrity
    /// level, so this decides whether elevated windows are tileable at all.</summary>
    private static bool IsElevated()
    {
        using WindowsIdentity identity = WindowsIdentity.GetCurrent();
        return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
    }
}
