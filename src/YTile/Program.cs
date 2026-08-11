using System.Threading.Channels;
using Windows.Win32;
using Windows.Win32.UI.HiDpi;
using YTile.Runtime;
using YTile.Win32;

namespace YTile;

internal static class Program
{
    private const string Version = "0.1.0-dev";

    private static async Task<int> Main(string[] args)
    {
        if (args.Contains("--version"))
        {
            Console.WriteLine($"ytiled {Version}");
            return 0;
        }

        if (!args.Contains("--debug-events"))
        {
            Console.WriteLine($"ytiled {Version} — window management is not implemented yet.");
            Console.WriteLine("Run 'ytiled --debug-events' to watch the window-event stream.");
            return 2;
        }

        // Physical-pixel window rects on mixed-DPI setups.
        PInvoke.SetProcessDpiAwarenessContext(DPI_AWARENESS_CONTEXT.DPI_AWARENESS_CONTEXT_PER_MONITOR_AWARE_V2);

        var channel = Channel.CreateBounded<RawWinEvent>(new BoundedChannelOptions(256)
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

        Console.WriteLine($"ytiled {Version} — event debug mode, Ctrl+C to exit.");
        EventListener.Start(channel.Writer);
        await DebugEventDumper.RunAsync(channel.Reader, cts.Token);
        return 0;
    }
}
