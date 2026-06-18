/**
 * FakeArduino — 模擬 ESP32 感測器
 *
 * 模擬情境：TCPListener 模式
 *   FakeArduino ──TCP──▶ EdgeLink Port 8888 (TCP Server)
 *                              ↓ 路由轉發
 *                         EdgeLink Port 9001 (TCP Client) ──TCP──▶ FakeUnity
 *
 * 執行前請先在 EdgeLink Web UI 設定：
 *   1. 新增 TCP Server Port 8888（Arduino 連入）
 *   2. 新增 TCP Client Port 9001，Host = 執行 FakeUnity 的機器 IP
 *      SourceProtocolId = Port 8888 的 ID（路由設定）
 */

using System.Net.Sockets;
using System.Text;

const string HOST    = "127.0.0.1"; // EdgeLink Server IP
const int    PORT    = 8888;         // EdgeLink TCP Server Port
const int    SEND_MS = 2000;         // 每 2 秒送一筆

int   seq      = 0;
float baseTemp = 25.0f;

Console.WriteLine($"[FakeArduino] 連線到 EdgeLink {HOST}:{PORT}");

while (true)
{
    try
    {
        using var client = new TcpClient();
        client.NoDelay = true;
        await client.ConnectAsync(HOST, PORT);
        Console.WriteLine("[FakeArduino] 已連線，開始傳送資料...");

        using var stream = client.GetStream();
        var lineBuf = new StringBuilder();

        // 讀取 PING 並回應 PONG（背景執行）
        _ = Task.Run(async () =>
        {
            var buf = new byte[256];
            while (client.Connected)
            {
                try
                {
                    int n = await stream.ReadAsync(buf);
                    if (n == 0) break;
                    lineBuf.Append(Encoding.UTF8.GetString(buf, 0, n));
                    int idx;
                    while ((idx = lineBuf.ToString().IndexOf('\n')) >= 0)
                    {
                        string line = lineBuf.ToString(0, idx).Trim();
                        lineBuf.Remove(0, idx + 1);
                        if (line.StartsWith("EDGELINK_PING:"))
                        {
                            string pong = $"EDGELINK_PONG:{line[14..]}\n";
                            await stream.WriteAsync(Encoding.UTF8.GetBytes(pong));
                            Console.WriteLine($"[FakeArduino] ← PING  → PONG");
                        }
                    }
                }
                catch { break; }
            }
        });

        // 定期送感測資料
        while (client.Connected)
        {
            seq++;
            float temp  = baseTemp + MathF.Sin(seq * 0.3f) * 3f;
            float humid = 60f      + MathF.Cos(seq * 0.2f) * 5f;

            string msg = $"id:ESP32_SIM;seq:{seq};temp:{temp:F1};humid:{humid:F1}";
            byte[] bytes = Encoding.UTF8.GetBytes(msg + "\n");
            await stream.WriteAsync(bytes);
            Console.WriteLine($"[FakeArduino] 送出 → {msg}");

            await Task.Delay(SEND_MS);
        }
    }
    catch (Exception ex)
    {
        Console.WriteLine($"[FakeArduino] 連線失敗: {ex.Message}，5 秒後重試...");
    }

    await Task.Delay(5000);
}
