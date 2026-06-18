using System.Collections.Concurrent;
using System.Net;
using System.Text;

namespace EdgeLink.WebApi;

public class MonitorSseHandler
{
    private const int MaxQueue = 200;
    private const int MinFlushIntervalMs = 50;

    private class Client
    {
        public readonly ConcurrentQueue<string> Queue = new();
        public readonly SemaphoreSlim Signal = new(0, int.MaxValue);
        public int Count;
    }

    private static readonly ConcurrentDictionary<Guid, Client> _clients = new();
    private static long _lastEnqueueTick;
    private const long MinEnqueueIntervalTicks = TimeSpan.TicksPerMillisecond * 20;

    public static void Publish(string message)
    {
        if (_clients.IsEmpty) return;

        long now  = DateTime.UtcNow.Ticks;
        long last = Interlocked.Read(ref _lastEnqueueTick);
        if (now - last < MinEnqueueIntervalTicks) return;
        if (Interlocked.CompareExchange(ref _lastEnqueueTick, now, last) != last) return;

        foreach (var kv in _clients)
        {
            var c = kv.Value;
            if (Interlocked.Increment(ref c.Count) > MaxQueue)
            {
                Interlocked.Decrement(ref c.Count);
                continue;
            }
            c.Queue.Enqueue(message);
            c.Signal.Release();
        }
    }

    public async Task HandleAsync(HttpListenerContext ctx)
    {
        ctx.Response.ContentType = "text/event-stream; charset=utf-8";
        ctx.Response.Headers["Cache-Control"] = "no-cache";
        ctx.Response.Headers["Access-Control-Allow-Origin"] = "*";
        ctx.Response.SendChunked = true;

        var id     = Guid.NewGuid();
        var client = new Client();
        _clients[id] = client;
        var stream = ctx.Response.OutputStream;
        var sb     = new StringBuilder();

        try
        {
            while (true)
            {
                await client.Signal.WaitAsync();
                sb.Clear();
                while (client.Queue.TryDequeue(out var msg))
                {
                    Interlocked.Decrement(ref client.Count);
                    sb.Append("data: ").Append(msg).Append("\n\n");
                }
                if (sb.Length == 0) continue;

                var bytes = Encoding.UTF8.GetBytes(sb.ToString());
                await stream.WriteAsync(bytes, 0, bytes.Length);
                await stream.FlushAsync();
                await Task.Delay(MinFlushIntervalMs);

                while (client.Signal.CurrentCount > 0)
                    client.Signal.Wait(0);
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) when (ex is IOException or HttpListenerException)
        {
            // Normal client disconnect
        }
        catch (Exception ex)
        {
            EdgeLink.Infrastructure.AppLogger.Warning($"[SSE] Client {id}: {ex.GetType().Name}: {ex.Message}");
        }
        finally
        {
            _clients.TryRemove(id, out _);
            client.Signal.Dispose();
            try { stream.Close(); } catch { }
        }
    }
}
