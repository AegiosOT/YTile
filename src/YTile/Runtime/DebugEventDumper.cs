using System.Threading.Channels;
using YTile.Win32;

namespace YTile.Runtime;

/// <summary>
/// Consumer for `ytiled --debug-events`: enriches raw hook events with window
/// identity and prints one line per event with the would-be tiling verdict.
/// </summary>
internal static class DebugEventDumper
{
    public static async Task RunAsync(ChannelReader<RawWinEvent> reader, CancellationToken ct)
    {
        // Remember identities so Destroy/Hide lines can say which window died.
        var lastLabel = new Dictionary<nint, string>();

        try
        {
            await foreach (var e in reader.ReadAllAsync(ct))
            {
                WmEventKind? kind = WinEventMap.Map(e.EventId);
                var w = WindowSnapshot.Capture(e.Hwnd);

                string label;
                if (w.IsAlive)
                {
                    label = $"{w.ExeName} class={w.ClassName} \"{w.Title}\"";
                    lastLabel[e.Hwnd] = label;
                }
                else
                {
                    label = lastLabel.TryGetValue(e.Hwnd, out var known) ? known : "(unknown)";
                }

                string? skip = Eligibility.SkipReason(in w);
                string verdict = skip is null ? "[tile]" : $"[skip: {skip}]";

                Console.WriteLine(
                    $"{DateTime.Now:HH:mm:ss.fff} {WinEventMap.RawName(e.EventId),-22} -> {kind?.ToString() ?? "-",-15} " +
                    $"hwnd=0x{e.Hwnd:X8} pid={w.ProcessId,-6} {verdict,-28} {label}");

                if (kind == WmEventKind.Destroy)
                {
                    lastLabel.Remove(e.Hwnd);
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Ctrl+C — clean exit.
        }
    }
}
