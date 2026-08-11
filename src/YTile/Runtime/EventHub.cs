using System.IO.Pipes;
using System.Threading.Channels;

namespace YTile.Runtime;

/// <summary>
/// Broadcasts serialized notifications to subscriber pipe connections. The
/// actor publishes into an unbounded queue and never blocks; a dedicated
/// broadcaster task does the pipe writes and drops subscribers whose
/// connection failed.
/// </summary>
internal sealed class EventHub
{
    private readonly Channel<string> _queue = Channel.CreateUnbounded<string>(
        new UnboundedChannelOptions { SingleReader = true });
    private readonly List<(NamedPipeServerStream Pipe, StreamWriter Writer)> _subscribers = [];
    private readonly Lock _lock = new();
    private int _count;

    /// <summary>Cheap check so the actor skips building state with nobody listening.</summary>
    public bool HasSubscribers => Volatile.Read(ref _count) > 0;

    /// <summary>Takes ownership of the connection; it stays open until a write fails.</summary>
    public void Attach(NamedPipeServerStream pipe, StreamWriter writer)
    {
        lock (_lock)
        {
            _subscribers.Add((pipe, writer));
        }
        Interlocked.Increment(ref _count);
        Log("subscriber attached");
    }

    public void Publish(string json) => _queue.Writer.TryWrite(json);

    public async Task RunAsync(CancellationToken ct)
    {
        try
        {
            await foreach (string json in _queue.Reader.ReadAllAsync(ct))
            {
                (NamedPipeServerStream Pipe, StreamWriter Writer)[] subscribers;
                lock (_lock)
                {
                    subscribers = [.. _subscribers];
                }

                List<(NamedPipeServerStream, StreamWriter)>? dead = null;
                foreach ((NamedPipeServerStream pipe, StreamWriter writer) in subscribers)
                {
                    try
                    {
                        await writer.WriteLineAsync(json);
                    }
                    catch
                    {
                        (dead ??= []).Add((pipe, writer));
                    }
                }

                if (dead is not null)
                {
                    lock (_lock)
                    {
                        foreach ((NamedPipeServerStream, StreamWriter) entry in dead)
                        {
                            _subscribers.Remove(entry);
                        }
                    }
                    Interlocked.Add(ref _count, -dead.Count);
                    foreach ((NamedPipeServerStream pipe, StreamWriter writer) in dead)
                    {
                        try
                        {
                            writer.Dispose();
                            pipe.Dispose();
                        }
                        catch
                        {
                        }
                    }
                    Log($"{dead.Count} subscriber(s) dropped");
                }
            }
        }
        catch (OperationCanceledException)
        {
        }
    }

    private static void Log(string message) => Console.WriteLine($"{DateTime.Now:HH:mm:ss.fff} {message}");
}
