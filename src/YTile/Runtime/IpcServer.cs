using System.IO.Pipes;
using System.Text.Json;
using System.Threading.Channels;
using YTile.Protocol;

namespace YTile.Runtime;

/// <summary>
/// Named-pipe NDJSON server: one request line in, one reply line out, per
/// connection. Commands are posted into the actor queue and answered via a
/// TaskCompletionSource — the IPC thread never touches manager state.
/// </summary>
internal sealed class IpcServer(ChannelWriter<WmMessage> wm)
{
    public const string PipeName = "ytile";

    public async Task RunAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            // The constructor lives inside the try: a creation failure must be
            // logged and retried, not silently fault this fire-and-forget task.
            NamedPipeServerStream? server = null;
            try
            {
                server = new NamedPipeServerStream(
                    PipeName, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
                await server.WaitForConnectionAsync(ct);
                using var reader = new StreamReader(server, leaveOpen: true);
                await using var writer = new StreamWriter(server, leaveOpen: true) { AutoFlush = true };

                // A connected-but-silent client must not wedge the single pipe instance.
                string? line = await reader.ReadLineAsync(ct).AsTask().WaitAsync(TimeSpan.FromSeconds(5), ct);
                if (line is not null)
                {
                    CommandReply reply = await DispatchAsync(line, ct);
                    await writer.WriteLineAsync(JsonSerializer.Serialize(reply, ProtocolJsonContext.Default.CommandReply));
                }
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"{DateTime.Now:HH:mm:ss.fff} ipc error: {ex.Message}");
                try
                {
                    await Task.Delay(250, ct);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
            }
            finally
            {
                if (server is not null)
                {
                    await server.DisposeAsync();
                }
            }
        }
    }

    private async Task<CommandReply> DispatchAsync(string line, CancellationToken ct)
    {
        CommandRequest? request;
        try
        {
            request = JsonSerializer.Deserialize(line, ProtocolJsonContext.Default.CommandRequest);
        }
        catch (JsonException ex)
        {
            return new CommandReply(false, $"bad request: {ex.Message}");
        }

        if (request is null)
        {
            return new CommandReply(false, "empty request");
        }

        var tcs = new TaskCompletionSource<CommandReply>(TaskCreationOptions.RunContinuationsAsynchronously);
        wm.TryWrite(new WmMessage.Command(request, tcs));
        try
        {
            return await tcs.Task.WaitAsync(TimeSpan.FromSeconds(5), ct);
        }
        catch (TimeoutException)
        {
            // Cancel the TCS so the actor skips a command nobody is waiting for
            // — otherwise it would still execute after we reported failure.
            tcs.TrySetCanceled(CancellationToken.None);
            return new CommandReply(false, "daemon busy (timeout)");
        }
    }
}
