/**
 * FakeUnity — 模擬 Unity TCPListener 模式
 *
 * 模擬情境：TCPListener 模式
 *   FakeArduino ──TCP──▶ EdgeLink Port 8888 (TCP Server)
 *                              ↓ 路由轉發
 *                         EdgeLink Port 9001 (TCP Client) ──TCP──▶ FakeUnity（此程式）
 *
 * FakeUnity 開啟本地 Port 等待 EdgeLink 主動連入，
 * 收到訊息後解析 id / temp / humid 欄位，並模擬設備斷線偵測。
 */

using EdgeLink;

const int   LOCAL_PORT      = 9001;  // EdgeLink TCP Client 會連到這個 Port
const float TIMEOUT_SECONDS = 20f;   // 超過幾秒沒資料視為設備斷線

var listener = new EdgeLinkTcpListener(LOCAL_PORT);

// ── 連線狀態 ─────────────────────────────────────────────────────────────────
listener.OnConnected    += () => Console.WriteLine("[FakeUnity] EdgeLink 已連入");
listener.OnDisconnected += () => Console.WriteLine("[FakeUnity] EdgeLink 已斷線");
listener.OnError        += ex => Console.WriteLine($"[FakeUnity] 錯誤: {ex.Message}");

// ── 設備上線 / 斷線通知（TCP，拔電後 ~15 秒）──────────────────────────────
listener.OnDeviceStatus += (connected, endpoint, deviceId) =>
{
    string ip  = endpoint.Contains("@") ? endpoint.Split('@')[1] : endpoint;
    string did = string.IsNullOrEmpty(deviceId) ? "(未識別)" : deviceId;
    Console.ForegroundColor = connected ? ConsoleColor.Green : ConsoleColor.Red;
    Console.WriteLine(connected
        ? $"[FakeUnity] ▲ 設備上線  IP: {ip}  Device: {did}"
        : $"[FakeUnity] ▼ 設備斷線  IP: {ip}  Device: {did}");
    Console.ResetColor();
};

// ── 訊息處理 ──────────────────────────────────────────────────────────────────
var lastSeen = new Dictionary<string, DateTime>();
var timedOut = new HashSet<string>();

listener.OnMessage += msg =>
{
    // 解析 key:value;key:value 格式
    var fields = new Dictionary<string, string>();
    foreach (var part in msg.Split(';'))
    {
        int i = part.IndexOf(':');
        if (i < 0) continue;
        fields[part[..i].Trim()] = part[(i + 1)..].Trim();
    }

    fields.TryGetValue("id",    out string? id);
    fields.TryGetValue("temp",  out string? temp);
    fields.TryGetValue("humid", out string? humid);
    fields.TryGetValue("seq",   out string? seq);

    Console.WriteLine($"[FakeUnity] [{id ?? "?"}] seq={seq} temp={temp}°C humid={humid}%");

    // 更新 lastSeen，處理重新上線
    if (id != null)
    {
        if (timedOut.Remove(id))
        {
            Console.ForegroundColor = ConsoleColor.Cyan;
            Console.WriteLine($"[FakeUnity] ↑ {id} 重新上線");
            Console.ResetColor();
        }
        lastSeen[id] = DateTime.UtcNow;
    }
};

listener.Start();
Console.WriteLine($"[FakeUnity] 監聽 Port {LOCAL_PORT}，等待 EdgeLink 連入...");
Console.WriteLine("[FakeUnity] 按 Ctrl+C 結束\n");

// ── Timeout 偵測迴圈（模擬 Unity Update()）────────────────────────────────────
using var cts = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };

try
{
    while (!cts.Token.IsCancellationRequested)
    {
        await Task.Delay(1000, cts.Token);

        foreach (var kv in lastSeen)
        {
            double elapsed = (DateTime.UtcNow - kv.Value).TotalSeconds;
            if (elapsed > TIMEOUT_SECONDS && !timedOut.Contains(kv.Key))
            {
                timedOut.Add(kv.Key);
                Console.ForegroundColor = ConsoleColor.Yellow;
                Console.WriteLine($"[FakeUnity] ⚠ {kv.Key} 超時 {elapsed:F0}s，可能已離線");
                Console.ResetColor();
            }
        }
    }
}
catch (OperationCanceledException) { }

listener.Stop();
Console.WriteLine("[FakeUnity] 已停止");
