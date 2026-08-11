using System.Threading.Channels;

namespace YTile.Runtime;

/// <summary>
/// Destroy/Hide WinEvents are not guaranteed for every window death; a slow
/// periodic liveness sweep inside the actor catches the strays. The timer
/// lives here; the actual IsWindow checks run on the actor thread.
/// </summary>
internal static class Reaper
{
    public static async Task RunAsync(ChannelWriter<WmMessage> wm, CancellationToken ct)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(250));
        try
        {
            while (await timer.WaitForNextTickAsync(ct))
            {
                wm.TryWrite(WmMessage.ReaperTick.Instance);
            }
        }
        catch (OperationCanceledException)
        {
        }
    }
}
