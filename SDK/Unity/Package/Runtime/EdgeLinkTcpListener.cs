using System;
using System.Collections.Concurrent;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace EdgeLink
{
    public class EdgeLinkTcpListener : IDisposable
    {
        public event Action<string>?    OnMessage;
        public event Action?            OnConnected;
        public event Action?            OnDisconnected;
        public event Action<Exception>? OnError;
        /// <summary>Fired when an upstream device connects or disconnects from EdgeLink Server.
        /// Parameters: isConnected, endpoint (e.g. "TCPServer@192.168.1.50:9001")</summary>
        public event Action<bool, string, string>? OnDeviceStatus;

        public int  LocalPort  { get; }
        public bool IsRunning  { get; private set; }

        private TcpListener?            listener;
        private CancellationTokenSource cts = new CancellationTokenSource();
        private readonly ConcurrentQueue<string> queue = new ConcurrentQueue<string>();
        // 追蹤所有 Accept 進來的 client，Stop/Dispose 要強制關掉，否則 socket 漏到 OS。
        private readonly ConcurrentDictionary<TcpClient, byte> _accepted
            = new ConcurrentDictionary<TcpClient, byte>();
        /// <summary>行緩衝上限。對端若一直不送換行,緩衝會無限成長。</summary>
        private const int MaxLineBufferChars = 64 * 1024;
        private bool disposed;

        public EdgeLinkTcpListener(int localPort)
        {
            LocalPort = localPort;
        }

        public void Start()
        {
            if (disposed) throw new ObjectDisposedException(nameof(EdgeLinkTcpListener));
            if (IsRunning) return;
            IsRunning = true;
            // 舊 CTS 不再 leak — Stop() 應已 cancel + dispose 它，這裡再保險換新
            try { cts.Dispose(); } catch { }
            cts      = new CancellationTokenSource();
            listener = new TcpListener(IPAddress.Any, LocalPort);
            listener.Start();
            _ = Task.Run(() => AcceptLoopAsync(cts.Token));
        }

        private async Task AcceptLoopAsync(CancellationToken ct)
        {
            while (!ct.IsCancellationRequested)
            {
                try
                {
                    var client = await listener!.AcceptTcpClientAsync();
                    _accepted.TryAdd(client, 0);
                    _ = Task.Run(() => ReadLoopAsync(client, ct), ct);
                }
                catch (OperationCanceledException) { return; }
                catch (ObjectDisposedException)    { return; }   // listener 已 Stop()
                catch (Exception ex) { OnError?.Invoke(ex); }
            }
        }

        private async Task ReadLoopAsync(TcpClient client, CancellationToken ct)
        {
            OnConnected?.Invoke();
            NetworkStream? networkStream = null;
            try { networkStream = client.GetStream(); }
            catch { _accepted.TryRemove(client, out _); try { client.Dispose(); } catch { } OnDisconnected?.Invoke(); return; }

            var buf     = new byte[4096];
            var lineBuf = new StringBuilder();
            // 有狀態的 UTF-8 解碼器,**每條連線各一份**。
            // Decoder 會保留跨 chunk 的不完整位元組序列,所以它帶狀態、且非 thread-safe。
            // 這裡每個 client 各跑一個 ReadLoopAsync,若共用同一個 Decoder,A 連線殘留的
            // 半個字元會被接到 B 連線的位元組前面解碼 —— 兩邊的訊息互相污染。
            var utf8Decoder   = Encoding.UTF8.GetDecoder();

            try
            {
                while (!ct.IsCancellationRequested)
                {
                    int read = await networkStream.ReadAsync(buf, 0, buf.Length, ct);
                    if (read == 0) break;

                    // 有狀態的 Decoder 會保留跨 chunk 的不完整位元組序列。
                    // 先前是每個 chunk 各自 GetString,多位元組字元一旦被 TCP 切開,
                    // 前半會變成 U+FFFD、後半的接續位元組又變成更多 U+FFFD。
                    int charCount = utf8Decoder.GetCharCount(buf, 0, read);
                    if (charCount > 0)
                    {
                        var chars = new char[charCount];
                        utf8Decoder.GetChars(buf, 0, read, chars, 0);
                        lineBuf.Append(chars, 0, charCount);
                    }

                    if (lineBuf.Length > MaxLineBufferChars)
                    {
                        OnError?.Invoke(new InvalidDataException(
                            $"Line buffer exceeded {MaxLineBufferChars} chars without a newline — discarding."));
                        lineBuf.Clear();
                    }

                    int idx;
                    while ((idx = FindNewline(lineBuf)) >= 0)
                    {
                        string line = lineBuf.ToString(0, idx).Trim();
                        lineBuf.Remove(0, idx + 1);
                        if (line.Length > 0) await HandleLineAsync(networkStream, line);
                    }
                }
            }
            catch (OperationCanceledException) { }
            catch (ObjectDisposedException)    { }   // Stop() 主動關掉
            catch (Exception ex) { OnError?.Invoke(ex); }
            finally
            {
                _accepted.TryRemove(client, out _);
                try { networkStream?.Dispose(); } catch { }
                try { client.Dispose(); } catch { }
                OnDisconnected?.Invoke();
            }
        }

        private async Task HandleLineAsync(NetworkStream networkStream, string line)
        {
            if (line.StartsWith("EDGELINK_PING:", StringComparison.Ordinal))
            {
                string hex = line.Substring(14);
                try
                {
                    byte[] pong = Encoding.UTF8.GetBytes($"EDGELINK_PONG:{hex}\n");
                    await networkStream.WriteAsync(pong, 0, pong.Length);
                }
                catch { }
                return;
            }
            if (line.StartsWith("EDGELINK_STATUS:", StringComparison.Ordinal))
            {
                string body      = line.Substring(16);
                int    sep       = body.IndexOf(':');
                string statusStr = sep >= 0 ? body.Substring(0, sep) : body;
                string rest      = sep >= 0 ? body.Substring(sep + 1) : "";
                bool   connected = statusStr.Equals("CONNECTED", StringComparison.OrdinalIgnoreCase);
                // rest = "TCPServer@192.168.1.50" or "TCPServer@192.168.1.50:sensor-01"
                int devSep    = rest.LastIndexOf(':');
                string endpoint = devSep >= 0 ? rest.Substring(0, devSep) : rest;
                string deviceId = devSep >= 0 ? rest.Substring(devSep + 1) : "";
                OnDeviceStatus?.Invoke(connected, endpoint, deviceId);
                return;
            }
            if (line.StartsWith("EDGELINK_", StringComparison.Ordinal)) return;

            queue.Enqueue(line);
            OnMessage?.Invoke(line);   // 先前宣告了事件卻從不觸發,C# 版則有
        }

        private static int FindNewline(StringBuilder sb)
        {
            for (int i = 0; i < sb.Length; i++)
                if (sb[i] == '\n') return i;
            return -1;
        }

        public bool TryDequeue(out string message) => queue.TryDequeue(out message!);

        public void Stop()
        {
            if (!IsRunning) return;
            IsRunning = false;
            try { cts.Cancel(); } catch { }
            try { listener?.Stop(); } catch { }
            listener = null;

            // 主動關掉所有未斷的 client，避免 socket 殘留到 OS 端
            foreach (var kv in _accepted)
            {
                try { kv.Key.Close(); } catch { }
                try { kv.Key.Dispose(); } catch { }
            }
            _accepted.Clear();
        }

        public void Dispose()
        {
            if (disposed) return;
            disposed = true;
            Stop();
            try { cts.Dispose(); } catch { }
        }
    }
}
