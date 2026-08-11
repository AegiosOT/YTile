using System.IO.Pipes;
using System.Text.Json;
using System.Threading.Channels;
using YTile.Protocol;

namespace YTile.Runtime;

/// <summary>
/// Named-pipe NDJSON server. Request/reply connections carry one line each
/// way; a "subscribe" request keeps the connection open and hands it to the
/// EventHub, which streams a NotificationDto line on every state change.
/// Commands are posted into the actor queue and answered via a
/// TaskCompletionSource — the IPC threads never touch manager state.
/// </summary>
internal sealed class IpcServer(ChannelWriter<WmMessage> wm, EventHub events)
{
    public const string PipeName = "ytile";
    private const int MaxInstances = 8;

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
                    PipeName, PipeDirection.InOut, MaxInstances, PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
                await server.WaitForConnectionAsync(ct);
                NamedPipeServerStream connected = server;
                server = null; // the handler task owns it now
                _ = Task.Run(() => HandleConnectionAsync(connected, ct), CancellationToken.None);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                Log($"ipc error: {ex.Message}");
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

    private async Task HandleConnectionAsync(NamedPipeServerStream server, CancellationToken ct)
    {
        bool handedOff = false;
        try
        {
            using var reader = new StreamReader(server, leaveOpen: true);
            var writer = new StreamWriter(server, leaveOpen: true) { AutoFlush = true };
            try
            {
                // A connected-but-silent client must not hold an instance forever.
                string? line = await reader.ReadLineAsync(ct).AsTask().WaitAsync(TimeSpan.FromSeconds(5), ct);
                if (line is null)
                {
                    return;
                }

                CommandRequest? request;
                try
                {
                    request = JsonSerializer.Deserialize(line, ProtocolJsonContext.Default.CommandRequest);
                }
                catch (JsonException ex)
                {
                    await writer.WriteLineAsync(JsonSerializer.Serialize(
                        new CommandReply(false, $"bad request: {ex.Message}"), ProtocolJsonContext.Default.CommandReply));
                    return;
                }

                if (request is null)
                {
                    await writer.WriteLineAsync(JsonSerializer.Serialize(
                        new CommandReply(false, "empty request"), ProtocolJsonContext.Default.CommandReply));
                    return;
                }

                if (request.Cmd == "subscribe")
                {
                    await writer.WriteLineAsync(JsonSerializer.Serialize(
                        new CommandReply(true, Message: "subscribed"), ProtocolJsonContext.Default.CommandReply));
                    events.Attach(server, writer);
                    handedOff = true;
                    return;
                }

                CommandReply reply = await DispatchAsync(request, ct);
                await writer.WriteLineAsync(JsonSerializer.Serialize(reply, ProtocolJsonContext.Default.CommandReply));
            }
            finally
            {
                if (!handedOff)
                {
                    await writer.DisposeAsync();
                }
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            Log($"ipc connection error: {ex.Message}");
        }
        finally
        {
            if (!handedOff)
            {
                await server.DisposeAsync();
            }
        }
    }

    private async Task<CommandReply> DispatchAsync(CommandRequest request, CancellationToken ct)
    {
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

    private static void Log(string message) => Console.WriteLine($"{DateTime.Now:HH:mm:ss.fff} {message}");
}
