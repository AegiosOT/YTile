using System.IO.Pipes;
using System.Security.AccessControl;
using System.Security.Principal;
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

    /// <summary>
    /// Explicit ACL granting this user's SID access to the pipe. Without one the
    /// pipe inherits the creating token's default DACL — and an ELEVATED token's
    /// default DACL grants Administrators, which is a deny-only SID in the same
    /// user's medium token. An elevated daemon would then publish a pipe that
    /// its own CLI can see but not open, so `ytile` would report the daemon
    /// missing and every ykeys hotkey would silently do nothing.
    ///
    /// The user SID is identical across the split token's two halves, so naming
    /// it explicitly is what lets a medium CLI drive an elevated daemon. This
    /// widens nothing: an unelevated daemon's pipe was always reachable by
    /// anything running as this user, and the commands behind it only move
    /// windows around.
    /// </summary>
    private static readonly PipeSecurity Security = BuildSecurity();

    private static PipeSecurity BuildSecurity()
    {
        var security = new PipeSecurity();
        using WindowsIdentity me = WindowsIdentity.GetCurrent();
        if (me.User is not null)
        {
            security.AddAccessRule(new PipeAccessRule(me.User, PipeAccessRights.FullControl, AccessControlType.Allow));
        }
        security.AddAccessRule(new PipeAccessRule(
            new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null),
            PipeAccessRights.FullControl,
            AccessControlType.Allow));
        return security;
    }

    /// <summary>Consecutive accept failures, so a persistent fault is logged
    /// once and then periodically rather than four times a second.</summary>
    private int _acceptFailures;

    public async Task RunAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            // The constructor lives inside the try: a creation failure must be
            // logged and retried, not silently fault this fire-and-forget task.
            NamedPipeServerStream? server = null;
            try
            {
                server = NamedPipeServerStreamAcl.Create(
                    PipeName, PipeDirection.InOut, MaxInstances, PipeTransmissionMode.Byte,
                    PipeOptions.Asynchronous, inBufferSize: 0, outBufferSize: 0, Security);
                await server.WaitForConnectionAsync(ct);
                NamedPipeServerStream connected = server;
                server = null; // the handler task owns it now
                _acceptFailures = 0;
                _ = Task.Run(() => HandleConnectionAsync(connected, ct), CancellationToken.None);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                // This retries every 250ms for as long as the cause persists, so
                // logging every attempt buries everything else — one wedged daemon
                // produced 2464 identical lines. Say it immediately, then every ten
                // seconds, with a running count so the duration is visible.
                if (_acceptFailures++ == 0 || _acceptFailures % 40 == 0)
                {
                    Log(_acceptFailures == 1
                        ? $"ipc error: {ex.Message}"
                        : $"ipc error: {ex.Message} (x{_acceptFailures}, still failing)");
                }

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
