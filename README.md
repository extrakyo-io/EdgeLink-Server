<div align="center">

# EdgeLink Server

**IoT 協定橋接與訊息路由伺服器**

[![.NET](https://img.shields.io/badge/.NET-8.0-512BD4?logo=dotnet)](https://dotnet.microsoft.com)
[![Platform](https://img.shields.io/badge/Platform-Windows-0078D4?logo=windows)](https://github.com/extrakyo-io/EdgeLink-Server/releases)
[![License](https://img.shields.io/badge/License-MIT-blue)](LICENSE)
[![Version](https://img.shields.io/badge/Version-2.4.0-informational)](https://github.com/extrakyo-io/EdgeLink-Server/releases/tag/v2.4.0)

輕量級 .NET 8 伺服器：透過 TCP/UDP 橋接 IoT 裝置，以自訂 Mask 定義轉換協定資料，並提供瀏覽器端管理介面。

</div>

---

## 功能特色

| 功能 | 說明 |
|---------|-------------|
| **多協定** | TCP Server、TCP Client、UDP、**Modbus TCP Master** — 每個 port 獨立設定 |
| **Modbus 輪詢** | 內建 FluentModbus master — 以可設定的間隔輪詢 ICP DAS / 一般 Modbus slave，結果以原生 EdgeLink 訊息進入路由管線 |
| **訊息路由** | 透過 `SourceProtocolId` 自動橋接各 port |
| **Mask 系統** | 自訂協定解析與轉換規則；並支援**二進位版面解碼**（UDP 逐包／TCP 串流分包）轉成 KV |
| **即時監控** | 每個 port 的 SSE 串流日誌，支援關鍵字搜尋 |
| **Web 管理介面** | 瀏覽器端管理 — 卡片式 port 檢視，免前端建置 |
| **裝置識別** | TCP Server 與 UDP 上逐連線的裝置 ID 追蹤，於 WebUI 與 SDK 中呈現 |
| **HTTPS** | 自動產生含所有本機 IP SAN 的自簽憑證 |
| **安全性** | PBKDF2 密碼雜湊、工作階段持久化、HttpOnly cookie |
| **檔案日誌** | 每日輪替日誌檔，保留 7 天 |
| **用戶端 SDK** | Unity（UPM，POCO + MonoBehaviour）、Arduino、C#（.NET 6）、Python（asyncio）、JavaScript（Node.js） |

---

## 系統架構

EdgeLink Server 位於控制系統的核心，扮演**扇出樞紐（fan-out hub）**：數值處理程式透過 **Modbus TCP** 輪詢現場硬體、計算出應用數值，再經由 SDK 用戶端推送進 EdgeLink。EdgeLink 對每筆訊息套用 **Mask**，並依 `SourceProtocolId` **路由**，透過 **UDP / TCP** 一次送達所有下游消費端。

此參考部署是一套雲梯消防車水炮訓練平台。**AX-5 運動控制器**（霍爾搖桿按鈕 / 踏板 / 緊急停止、伺服驅動器）與**三軸平台訊號擷取模組**（16× DI 絕對編碼器、4× AI 雙軸搖桿）由數值處理程式讀取 —— 進行 Gray code→角度轉換、低通濾波、搖桿 0.25–4.75 V / 雙軸 5 V 檢查、姿態 + 奇異點偵測 + 逆向運動學 —— 再餵給 EdgeLink。EdgeLink 將處理後的數值扇出到 Unity 3D 與另外兩台電腦。

---

## 系統需求

- Windows 10 / 11 x64
- 免安裝 .NET Runtime（self-contained 自帶執行環境）

---

## 安裝

1. 從 [Releases](https://github.com/extrakyo-io/EdgeLink-Server/releases) 下載 `EdgeLink-Server-v2.4.0-win-x64.zip`
2. 解壓縮 zip
3. 執行 `EdgeLinkServer.exe`
4. 開啟瀏覽器前往 `https://localhost:8443`
   - 接受自簽憑證警告（點 進階 → 繼續前往）
   - 預設密碼：`admin` — **登入後請立即修改**

---

## Web 管理介面

| 分頁 | 說明 |
|-----|------|
| **Ports** | 新增 / 移除 / 切換 port，即時監控連線狀態與流量 |
| **Mask** | 建立 / 編輯 / 刪除 Mask 定義 — 欄位解析規則與輸出範本 |
| **系統日誌** | 檢視伺服器事件日誌，支援關鍵字過濾 |

啟動伺服器後，完整文件位於 `/manual`。

---

## 命令列選項

```
EdgeLinkServer.exe [options]

  --port <n>          HTTP 埠（預設：8081）
  --no-https          停用 HTTPS（預設啟用 HTTPS）
  --https-port <n>    HTTPS 埠（預設：8443）

環境變數：
  EDGELINK_PORT, EDGELINK_HTTPS, EDGELINK_HTTPS_PORT
```

優先順序：命令列參數 > 環境變數 > 預設值

---

## Modbus TCP Master

內建的 Modbus TCP master 可輪詢任何 Modbus slave（ICP DAS ET-7xxx、一般 PLC、ModRSsim2 等模擬器），並將結果以標準訊息注入 EdgeLink 路由管線 —— 下游消費端（Unity / 儀表板 / TCP Client）無從得知、也不在乎來源是 Modbus 還是原始 TCP。

### 快速範例

於 WebUI 新增 port → **+ Add Port** → 選擇 `Modbus TCP Master`：

| 欄位 | 值 |
|-------|-----|
| Slave IP | `192.168.1.10`（本機模擬器用 `127.0.0.1`） |
| Modbus Port | `502`（預設） |
| Slave ID | `1` |
| Polling Interval (ms) | `100`（10 Hz） |
| Device ID | `WaterCannon01` — 成為合成訊息中的 `id:` 欄位 |
| Registers | JSON 陣列（見下方） |

暫存器對應 JSON：

```json
[
  { "name": "yaw",   "functionCode": 2, "startAddress": 0, "quantity": 8, "dataType": "bits" },
  { "name": "pitch", "functionCode": 2, "startAddress": 8, "quantity": 8, "dataType": "bits" },
  { "name": "joyx",  "functionCode": 4, "startAddress": 0, "quantity": 1, "dataType": "uint16", "scale": 0.001 },
  { "name": "joyy",  "functionCode": 4, "startAddress": 1, "quantity": 1, "dataType": "uint16", "scale": 0.001 }
]
```

每個輪詢間隔，EdgeLink 會發出如下的合成訊息：

```
id:WaterCannon01;yaw:128;pitch:64;joyx:2.5;joyy:1.802
```

### 功能碼（Function Code）

| FunctionCode | Modbus 操作 | 用途 |
|:-:|---|---|
| `1` | Read Coils | 輸出位元（繼電器狀態、LED） |
| `2` | Read Discrete Inputs | 開關、按鈕、GrayCode 編碼器位元 |
| `3` | Read Holding Registers | 一般 16-bit，可讀寫 |
| `4` | Read Input Registers | 類比輸入（搖桿、感測器電壓） |

### 資料型別

| dataType | 位元組 | 說明 |
|---|:-:|---|
| `bit` | 1 bit | 單一 Coil / DI（`quantity` 必須為 1） |
| `bits` | N bits | 將 `quantity` 個位元打包成無號整數（LSB first） |
| `uint16` / `int16` | 2 | 一個暫存器 |
| `uint32` / `int32` | 4 | 兩個暫存器，big-endian |
| `float32` | 4 | 兩個暫存器，IEEE 754 big-endian |

選用的 `scale` 與 `offset` 會套用到暫存器讀值：`output = raw * scale + offset`。

### 測試模擬器

若在無實體硬體的情況下開發，[`modbus_slave_sim.py`](modbus_slave_sim.py) 提供一個以 `pymodbus` 為基礎的 Modbus TCP slave，可產生合成的 GrayCode + 正弦波資料：

```bash
pip install pymodbus==3.6.6
python modbus_slave_sim.py
# 監聽 127.0.0.1:5020
```

---

## Binary Mask — 解碼原始二進位協定

**Binary Mask** 會即時把裝置的原始二進位協定解碼成 EdgeLink 的 KV 文字，下游消費端（Unity／儀表板）收到的是純 `key:value`，完全看不到二進位。解碼發生在**來源埠**；轉發／輸出埠請設 `OriginalData`，原樣把解碼後的 KV 送出。

當 mask 定義帶有 `binary` 區塊（`BinarySpec`）時即為二進位 mask：一個位元組版面，用 `discriminator` 依某欄位值（如訊息型別位元組）挑選 `variant`，每個 variant 宣告自己的 `length`、輸出 `template`、以及具型別的 `fields` —— `u8/u16/u32/u64/i8/i16/i32/i64/f32/f64/bit/bitrange/const`，可帶 `scale`/`offset`/`format`。

### UDP vs TCP

| 傳輸 | 分包 | 心跳 |
|---|---|---|
| **UDP** | 每個 datagram **就是**一包 — 不需要 `sync` | 無（以逾時判斷 stale） |
| **TCP** | 串流沒有封包邊界 → 設 `binary.sync` 為封包 magic（hex，如 `"4f4b"`=`OK`）讓 EdgeLink 對齊/重新同步；長度由 discriminator → variant `length` 決定 | 二進位埠**不送 app 層 PING/PONG**，改靠 TCP keep-alive（閒置 10s／探測 1s）。二進位來源**不需要**回 PONG。 |

同一個 TCP Server 埠可以收 **KV 文字或二進位**，依該埠的 mask 決定（binary mask → 二進位分包；其他 mask → 換行分隔 KV 文字 + PING/PONG）。

### 設定範例

走 TCP 時，`binary` 區塊的重點是多了 `sync`（其餘與 UDP 相同）：

```json
{
  "byteOrder": "little",
  "sync": "4f4b",
  "discriminator": { "offset": 3, "type": "u8" },
  "variants": [
    {
      "match": 1,
      "length": 37,
      "template": "id:{id};seq:{seq};x:{x}",
      "fields": [
        { "name": "id",  "offset": 0,  "type": "const", "value": "dev1" },
        { "name": "seq", "offset": 5,  "type": "u32" },
        { "name": "x",   "offset": 19, "type": "f32", "format": "0.###" }
      ]
    }
  ]
}
```

可在 **WebUI → Mask 編輯器**建立（附 hex 解碼預覽），或用 **Settings → Import** 匯入。

### C# 來源端 SDK

[`SDK/CSharp/EdgeLinkSourceClient.cs`](SDK/CSharp/EdgeLinkSourceClient.cs) — 給「裝置／來源端」把遙測**送進** EdgeLink（TCP）的 C# client。自動回應文字模式心跳（`SendLineAsync` 送 KV）；`SendRawAsync` 送原始位元組到二進位埠。

---

## API 參考

Base URL：`https://<host>:8443`

除認證外，所有 `/api/*` 端點都需要工作階段 cookie。互動式文件位於 `/docs`。

| 分類 | 方法 | 路徑 | 說明 |
|-----|--------|------|------|
| Auth | `POST` | `/api/auth/login` | 登入 — body：`{"password":"..."}` |
| Auth | `POST` | `/api/auth/logout` | 登出 |
| Auth | `GET` | `/api/auth/status` | 查詢工作階段狀態 |
| Auth | `POST` | `/api/auth/change-password` | 修改密碼 |
| Ports | `GET` | `/api/ports` | 列出所有 port |
| Ports | `POST` | `/api/ports` | 新增 port |
| Ports | `PUT` | `/api/ports/{id}` | 更新 port |
| Ports | `DELETE` | `/api/ports` | 刪除 port |
| Ports | `POST` | `/api/ports/{id}/enabled` | 切換 port 啟用狀態 |
| Ports | `GET` | `/api/ports/{id}/clients` | 列出已連線的 TCP client |
| Masks | `GET` | `/api/masks` | 列出所有 mask ID |
| Masks | `POST` | `/api/masks` | 新增 mask |
| Masks | `GET` | `/api/masks/{maskId}` | 取得 mask 定義 |
| Masks | `PUT` | `/api/masks/{maskId}` | 更新 mask |
| Masks | `DELETE` | `/api/masks/{maskId}` | 刪除 mask |
| Monitor | `GET` | `/api/monitor-stream` | SSE 即時訊息串流 |
| Monitor | `POST` | `/api/monitor/port` | 設定監控 port |
| Logs | `GET` | `/api/logs` | 系統日誌（cursor 分頁） |
| Settings | `GET` | `/api/settings/export` | 以 JSON 匯出所有設定 |
| Settings | `POST` | `/api/settings/import` | 匯入設定 JSON |

---

## Unity SDK

EdgeLink Unity SDK 讓 Unity 應用程式接收 EdgeLink Server 轉發的資料。支援 TCP、TCP Listener 與 UDP 三種連線模式，並在執行期自動取得 Mask。

### 安裝（UPM）

在 Unity 中 → **Window → Package Manager → + → Add package from git URL**：

```
https://github.com/extrakyo-io/EdgeLink-Server.git?path=SDK/Unity/Package
```

接著透過 Package Manager → EdgeLink SDK → Samples 匯入 **Basic Example** 範例。

### 用法 — 兩種風格

| 風格 | 類別 | 設定方式 |
|---|---|---|
| **MonoBehaviour**（拖到 GameObject，以 Inspector 設定） | `EdgeLinkManager` | 掛到任一 GameObject，填入 Inspector 欄位 |
| **POCO**（以建構子設定，執行期決定 URL/Host/Port） | `EdgeLinkBridge` | `new EdgeLinkBridge(...)` — 自行驅動生命週期 |

一般情況（靜態設定）用 `EdgeLinkManager`。當 URL / Host / Port 必須在執行期才決定（大廳分配的 IP、設定檔、多實例情境）時，用 `EdgeLinkBridge`。

### 用法 — MonoBehaviour（`EdgeLinkManager`）

將 `EdgeLinkManager` 掛到任一 GameObject，並在 Inspector 中設定：

| 欄位 | 說明 |
|-------|------|
| Server URL | 供執行期取得 Mask 的 EdgeLink Server 位址 |
| Password | 登入密碼 |
| Mask ID | 用於欄位解析的 Mask 名稱 |
| Protocol | `TCP` / `TCPListener` / `UDP` |
| TCP Host / Port | （TCP 模式）EdgeLink Server IP 與埠 |
| Listen Port | （TCPListener 模式）Unity 監聽的本機埠 |
| UDP Local Port | （UDP 模式）本機 UDP 埠 |
| Device Id Key | 訊息中用來識別裝置的欄位名（例如 `id`）。留空則停用逾時追蹤。 |
| Device Timeout (s) | 多少秒未收到訊息即視為裝置離線（`0` = 停用） |

### 用法 — POCO（`EdgeLinkBridge`）

純 C# 類別 — 以參數建構，由你自己的 MonoBehaviour 驅動生命週期：

```csharp
using System.Collections;
using UnityEngine;
using EdgeLink;

public class RuntimeConfigured : MonoBehaviour
{
    EdgeLinkBridge _bridge;

    IEnumerator Start()
    {
        // URL / Host / Port 可來自任何地方 — 大廳、config.json、PlayerPrefs …
        _bridge = new EdgeLinkBridge(
            serverUrl: PlayerPrefs.GetString("edgelink.url"),
            tcpHost:   PlayerPrefs.GetString("edgelink.host"),
            tcpPort:   PlayerPrefs.GetInt   ("edgelink.port", 9001));

        // 或使用完整 Config（TCPListener / UDP / 裝置逾時 / 自訂 mask …）：
        // _bridge = new EdgeLinkBridge(new EdgeLinkBridge.Config {
        //     ServerUrl = "...", Protocol = EdgeLinkBridge.Protocol.TCPListener,
        //     TcpListenPort = 9001, DeviceTimeoutSeconds = 15,
        // });

        _bridge.OnMessage      += msg => Debug.Log(msg);
        _bridge.OnDeviceStatus += (online, ep, id) => Debug.Log($"{id}@{ep} {online}");

        yield return _bridge.InitializeCoroutine();
    }

    void Update()    => _bridge?.Tick();
    void OnDestroy() => _bridge?.Dispose();
}
```

`EdgeLinkManager` 與 `EdgeLinkBridge` 提供相同的事件介面與 `Raw` / `Get(key)` 存取子。

### 讀取資料

```csharp
using UnityEngine;

public class Example : MonoBehaviour
{
    EdgeLinkManager edgeLink;
    string          lastRaw;

    void Start()
    {
        edgeLink = GetComponent<EdgeLinkManager>();
    }

    void Update()
    {
        if (edgeLink.Raw == lastRaw) return;
        lastRaw = edgeLink.Raw;

        string temp  = edgeLink.Get("temp");
        string humid = edgeLink.Get("humid");
        Debug.Log($"Temp:{temp} Humid:{humid}");
    }
}
```

| 成員 | 型別 | 說明 |
|--------|------|------|
| `Raw` | `string` | 最新的原始訊息字串（未解析） |
| `Get(key)` | `string` | 依欄位名取得最新解析值，找不到則為 `null` |

### 裝置連線 / 斷線偵測

`EdgeLinkManager` 提供兩種互補的斷線偵測機制：

```csharp
void Start()
{
    edgeLink = GetComponent<EdgeLinkManager>();

    // 當 EdgeLink Server 偵測到 TCP 連線開啟/關閉時觸發（斷電約需 15 秒）
    edgeLink.OnDeviceStatus += (connected, endpoint) =>
        Debug.Log(connected ? $"Online: {endpoint}" : $"Offline: {endpoint}");

    // 當某個裝置 ID 超過 Device Timeout 秒未送資料時觸發
    edgeLink.OnDeviceTimeout     += id => Debug.LogWarning($"{id} timed out");
    edgeLink.OnDeviceReconnected += id => Debug.Log($"{id} reconnected");
}
```

| 事件 | 觸發時機 | 識別依據 |
|-------|---------|---------|
| `OnDeviceStatus` | EdgeLink Server 偵測到 TCP 開啟/關閉 | IP 位址 |
| `OnDeviceTimeout` | 超過 `Device Timeout (s)` 未收到訊息 | 裝置 ID 欄位 |
| `OnDeviceReconnected` | 逾時後再次收到訊息 | 裝置 ID 欄位 |

> **斷電情境：** EdgeLink 偵測到連續 3 次 PING 未回應（約 15 秒）後觸發 `OnDeviceStatus(false, ...)`。在你設定的逾時時間內仍無新訊息時，`OnDeviceTimeout` 也會觸發。

#### 何時該用哪個事件

| 情境 | OnDeviceStatus | OnDeviceTimeout |
|----------|:-:|:-:|
| 裝置斷電（TCP） | ✓ 約 15 秒 | ✓ 依逾時設定 |
| 裝置在 UDP 上消失 | ✗ 無連線可偵測 | ✓ |
| TCP 存活但韌體當掉（無資料） | ✗ 連線正常 | ✓ |
| 多裝置共用一條 TCP 連線 | ✗ 只能偵測連線中斷 | ✓ 可識別每個裝置 |

建議**兩者並用**以完整涵蓋：`OnDeviceStatus` 捕捉 TCP 層的中斷；`OnDeviceTimeout` 捕捉無聲失效，且適用於所有協定。

---

## Arduino SDK

EdgeLink Arduino Library 讓 ESP32 / ESP8266 / Arduino 裝置連上 EdgeLink Server。PING/PONG keepalive 會自動處理 — 無需額外程式碼。

### 安裝

**方式 A — ZIP 匯入：**
1. 從 [Releases](https://github.com/extrakyo-io/EdgeLink-Server/releases) 下載 `EdgeLink.zip`
2. Arduino IDE → **Sketch → Include Library → Add .ZIP Library…**

**方式 B — 手動：**  
將 `SDK/Arduino/EdgeLink/` 資料夾複製到你的 Arduino `libraries/` 目錄。

### TCP 範例（ESP32 / ESP8266）

```cpp
#include <WiFi.h>
#include <EdgeLink.h>

WiFiClient  wifiClient;
EdgeLinkTCP edgelink(wifiClient);

void onMessage(const String& msg) {
    Serial.println(msg);  // 來自後端的回應
}

void setup() {
    WiFi.begin("your-ssid", "your-password");
    while (WiFi.status() != WL_CONNECTED) delay(500);

    edgelink.onMessage(onMessage);
    edgelink.setAutoReconnect(true, 5000);
    edgelink.begin("192.168.1.100", 9001);  // EdgeLink TCP Server 埠
}

void loop() {
    edgelink.loop();  // 必須呼叫 — 處理 PING/PONG 與接收

    static uint32_t t = 0;
    if (millis() - t >= 3000 && edgelink.isConnected()) {
        edgelink.send("id:ESP32_01;temp:25.3;humidity:60.0");
        t = millis();
    }
}
```

### UDP 範例（ESP32 / ESP8266）

```cpp
#include <WiFi.h>
#include <WiFiUdp.h>
#include <EdgeLink.h>

WiFiUDP     wifiUdp;
EdgeLinkUDP edgelink(wifiUdp);

void onMessage(const String& msg, IPAddress ip, uint16_t port) {
    Serial.println(msg);
}

void setup() {
    WiFi.begin("your-ssid", "your-password");
    while (WiFi.status() != WL_CONNECTED) delay(500);

    edgelink.begin(4210);           // 本機接收埠
    edgelink.onMessage(onMessage);
}

void loop() {
    edgelink.loop();

    static uint32_t t = 0;
    if (millis() - t >= 3000) {
        edgelink.send("192.168.1.100", 9002, "id:ESP32_01;temp:25.3;humidity:60.0");
        t = millis();
    }
}
```

### API

| 類別 | 方法 | 說明 |
|-------|--------|------|
| `EdgeLinkTCP` | `begin(host, port)` | 連上 EdgeLink TCP Server 埠 |
| | `loop()` | 必須在 `loop()` 中呼叫 — 處理 PING/PONG 與接收 |
| | `send(msg)` | 送出訊息（自動附加換行） |
| | `onMessage(cb)` | 收到訊息的回呼（已濾除 EDGELINK_*） |
| | `isConnected()` | 回傳連線狀態 |
| | `setAutoReconnect(enable, ms)` | 斷線時自動重連（預設：啟用，5000 ms） |
| `EdgeLinkUDP` | `begin(localPort = 0)` | 開始監聽本機 UDP 埠（`0` = 僅送出） |
| | `loop()` | 必須在 `loop()` 中呼叫 — 接收封包 |
| | `send(host, port, msg)` | 送出 UDP 封包到 EdgeLink |
| | `onMessage(cb)` | 回呼帶 `(msg, remoteIP, remotePort)` |

---

## C# SDK

EdgeLink C# SDK 以 **.NET 6+** 為目標，可用於任何非 Unity 的 .NET 應用程式（console、WPF、ASP.NET Core 等）。無第三方相依。

### 安裝

將 [SDK/CSharp/](SDK/CSharp/) 資料夾複製進你的方案並加入專案參考，或編譯為類別庫後參考該 DLL。

### TCP 範例

```csharp
using EdgeLink;

using var client = new EdgeLinkClient("192.168.1.100", 9001);

client.OnConnected    += ()  => Console.WriteLine("Connected");
client.OnDisconnected += ()  => Console.WriteLine("Disconnected");
client.OnMessage      += msg => Console.WriteLine($"Received: {msg}");

client.SetAutoReconnect(true, delayMs: 5000);
await client.ConnectAsync();

await client.SendAsync("id:DOTNET_01;temp:25.3;humidity:60.0");
```

### API

| 類別 | 成員 | 說明 |
|-------|--------|------|
| `EdgeLinkClient` | `ConnectAsync()` | 連線並在背景啟動讀取迴圈 |
| | `SendAsync(msg)` | 送出訊息（文字，自動補換行） |
| | `SendAsync(byte[] data)` | 送出**原始位元組**（給 binary mask 埠；不補換行、不做轉換） |
| | `IsConnected` | 連線狀態 |
| | `SetAutoReconnect(enable, delayMs)` | 斷線時自動重連（預設：啟用，5000 ms） |
| | `OnMessage / OnConnected / OnDisconnected / OnError` | 事件 |
| | `TryDequeue(out msg)` | 以輪詢取代事件的替代方式 |
| `EdgeLinkTcpListener` | `Start()` | 接受進來的 TCP 連線 |
| | `Stop()` | 停止監聽器 |
| | `OnMessage / OnConnected / OnDisconnected / OnError` | 事件 |
| `EdgeLinkUdpClient` | `Start()` | 綁定本機埠並接收封包 |
| | `OnMessage / OnError` | 事件 |
| `EdgeLinkUdpSender` | `SendAsync(host, port, msg)` | 送出 UDP 封包 |
| `EdgeLinkSourceClient` | `Start()` | **來源端**：連上 EdgeLink，背景自動回應 `EDGELINK_PONG` 心跳並斷線自動重連 |
| | `SendLineAsync(kv)` | 送一行 KV 文字（自動補換行） |
| | `SendRawAsync(bytes)` | 送原始位元組（給 binary mask 埠） |

---

## Python SDK

EdgeLink Python SDK 需要 **Python 3.10+**，僅使用標準函式庫（`asyncio`、`socket`）。

### 安裝

```bash
pip install SDK/Python   # 本機安裝
# 或直接把 edgelink/ 套件複製進你的專案
```

### TCP 範例

```python
import asyncio
from edgelink import EdgeLinkClient

async def main():
    client = EdgeLinkClient("192.168.1.100", 9001)

    client.on_connected(lambda: print("Connected"))
    client.on_message(lambda msg: print(f"Received: {msg}"))
    client.set_auto_reconnect(True, delay=5.0)

    await client.connect()

    while True:
        await asyncio.sleep(3)
        if client.is_connected:
            await client.send("id:PYTHON_01;temp:25.3;humidity:60.0")

asyncio.run(main())
```

### API

| 類別 | 成員 | 說明 |
|-------|--------|------|
| `EdgeLinkClient` | `connect()` | 連線並啟動讀取迴圈（coroutine） |
| | `send(msg)` | 送出訊息（coroutine） |
| | `is_connected` | 連線狀態 |
| | `set_auto_reconnect(enable, delay)` | 斷線時自動重連（預設：啟用，5 秒） |
| | `on_message / on_connected / on_disconnected / on_error` | 註冊回呼 |
| | `try_dequeue()` | 以輪詢取代回呼的替代方式 |
| | `disconnect()` | 關閉連線（coroutine） |
| `EdgeLinkTcpListener` | `start()` | 開始監聽（coroutine） |
| | `stop()` | 停止監聽器（coroutine） |
| | `on_message / on_connected / on_disconnected / on_error` | 註冊回呼 |
| `EdgeLinkUdpClient` | `start()` | 綁定並開始接收（coroutine） |
| | `on_message / on_error` | 註冊回呼 |
| `EdgeLinkUdpSender` | `send(host, port, msg)` | 送出 UDP 封包 |
| | `send_async(host, port, msg)` | 送出 UDP 封包（coroutine） |

---

## JavaScript SDK

EdgeLink JavaScript SDK 以 **Node.js 18+** 為目標，僅使用內建模組（`net`、`dgram`、`events`）。

### 安裝

```bash
# 將 SDK/JavaScript/ 複製進你的專案，然後：
const { EdgeLinkClient } = require("./edgelink/src");
```

### TCP 範例

```js
const { EdgeLinkClient } = require("./edgelink/src");

const client = new EdgeLinkClient("192.168.1.100", 9001);

client.on("connected",    ()    => console.log("Connected"));
client.on("message",      (msg) => console.log("Received:", msg));
client.on("error",        (err) => console.error("Error:", err.message));

client.setAutoReconnect(true, 5000);
client.connect();

setInterval(() => {
    if (client.isConnected)
        client.send("id:NODE_01;temp:25.3;humidity:60.0");
}, 3000);
```

### API

| 類別 | 成員 | 說明 |
|-------|--------|------|
| `EdgeLinkClient` | `connect()` | 連線並開始讀取 |
| | `send(msg)` | 送出訊息 |
| | `isConnected` | 連線狀態 |
| | `setAutoReconnect(enable, delayMs)` | 斷線時自動重連（預設：啟用，5000 ms） |
| | `disconnect()` | 銷毀 socket |
| | 事件：`"connected" / "disconnected" / "message" / "error"` | `EventEmitter` 事件 |
| `EdgeLinkTcpListener` | `start()` | 啟動 TCP 伺服器 |
| | `stop()` | 停止 TCP 伺服器 |
| | 事件：`"connected" / "disconnected" / "message" / "error"` | `EventEmitter` 事件 |
| `EdgeLinkUdpClient` | `start()` | 綁定並接收 UDP 封包 |
| | `stop()` | 停止接收 |
| | 事件：`"message" / "error"` | `EventEmitter` 事件 |
| `EdgeLinkUdpSender` | `send(host, port, msg)` | 送出 UDP 封包（回傳 `Promise`） |
| | `close()` | 關閉 socket |

---

## 專案結構

```
EdgeLink-Server/
├── Server/
│   ├── Infrastructure/      # AppConfig、AppLogger、AppPaths、CertificateHelper
│   ├── NetworkServer/
│   │   ├── Base/            # Connector 基底、models（PortData…）
│   │   ├── TCP/             # TCPServerConnector、TCPClientConnector
│   │   ├── Udp/             # UdpConnector
│   │   ├── Router/          # NetworkMessageRouter
│   │   └── Services/        # PortManager、PortDataStorageService
│   ├── WebApi/              # HttpApiServer、Auth/Port/Mask/Monitor handlers
│   ├── WebUI/               # 前端 HTML/CSS/JS（index、manual、docs）
│   └── Program.cs           # 進入點
└── SDK/
    ├── Unity/
    │   └── Package/         # UPM 套件（Runtime + Editor + Samples~）
    ├── Arduino/
    │   └── EdgeLink/        # Arduino 函式庫（TCP + UDP，自動處理 PING/PONG）
    ├── CSharp/              # .NET 6 類別庫（TCP client/listener + UDP）
    ├── Python/              # Python 3.10+ 套件，使用 asyncio（TCP + UDP）
    └── JavaScript/          # Node.js 18+ 套件，使用 net/dgram（TCP + UDP）
```

---

## 更新紀錄

| 版本 | 變更內容 |
|---------|---------|
| v2.4.0 | **穩定性與正確性修正**（全面稽核 #11–#20）— TCP 二進位 framing 與 decoder 改為共用 discriminator 解讀（先前兩邊各自解讀，signed／跨位元組欄位會分包錯位）；BinarySpec 存檔時驗證，壞掉的定義不再癱瘓接收迴圈；停止／改 Mask 時主動關閉已接受的連線（先前舊連線變半開，資料靜默遺失）；Modbus 連線失敗不再洩漏 socket、32 位元型別的暫存器數修正；設定檔原子寫入 + 損毀自動備份，部分 PUT 不再清掉 Modbus 設定；登入加上每 IP 節流（PBKDF2 10 萬次迭代原本可被當成 CPU 放大器）；HTTP body 1 MB 上限。**SDK**：UTF-8 多位元組字元跨 TCP 讀取邊界不再毀損（C#／Unity／JS）；Unity 的 `OnMessage` 補上觸發（先前宣告了卻從不 Invoke）；新增 `EdgeLinkBridge.Get(deviceId, key)`／`KnownDeviceIds`（單參數版會跨裝置混值）；行緩衝上限（C#／Unity 64 KB、Arduino 512 B — MCU 上原本會 heap 耗盡重開機） |
| v2.3.0 | **二進位 Mask 延伸到 TCP**（v2.2.0 僅支援 UDP）— 新增 `BinaryStreamFramer` 串流分包（`binary.sync` magic 對齊 + discriminator 查長度 + 殘缺等待 + 雜訊重新同步）；TCP Server 埠依該埠 Mask 自動分流二進位／文字，二進位埠不送 app 層 PING、改用 TCP keep-alive。修正設定匯出/匯入遺漏 `binary` 欄位的 bug（備份還原後二進位 mask 會失效）。Unity/CSharp `EdgeLinkClient` 新增 `SendAsync(byte[])`；新增 `EdgeLinkSourceClient`（非 Unity .NET 來源端 SDK，自動回 PONG、自動重連）；`docs/RigBinary.mask.json` 參考 spec |
| v2.2.0 | **通用二進位解析 Mask**（`BinarySpec`：offset / 型別 u8–f64 / bit / bitrange / const、little/big-endian、discriminator 分派、長度驗證）— 將固定版面的二進位 UDP 解碼為 KV；WebUI 二進位版面編輯器 + hex 解碼預覽；`POST /api/masks/preview-binary`。Monitor SSE 前端節流（高頻率 port 不再卡住瀏覽器） |
| v2.1.3 | Unity SDK：連線資源清理 — 避免 socket / CTS 洩漏 |
| v2.1.2 | Unity SDK：新增 `EdgeLinkBridge.cs.meta`；套件版本更新 |
| v2.1.1 | Unity SDK：抽出 `EdgeLinkBridge` POCO，以建構子注入 URL/Host/Port（MonoBehaviour `EdgeLinkManager` 仍作為輕薄包裝）；README 補上 Modbus + POCO 文件 |
| v2.1.0 | **Modbus TCP Master** port 類型（FluentModbus，FC 01/02/03/04，scale/offset）；WebUI 卡片式版面翻新；4× fire-and-forget 任務抑制；UDP port 清單輪詢保留使用者選取 |
| v2.0.1 | Unity SDK：`OnDeviceStatus` 新增裝置 ID 參數；Arduino AsyncUDP 範例；Mono 上 HttpClientHandler 憑證回退至 `ServicePointManager` |
| v2.0.0 | TCP Server / UDP 上逐連線的裝置識別；PING/PONG TCP keepalive；`EDGELINK_STATUS:CONNECTED/DISCONNECTED` 事件隨裝置 ID 轉發；WebUI 顯示每個 port 已識別的裝置 |
| v1.1.0 | Unity SDK — 裝置連線/斷線偵測（`OnDeviceStatus`、`OnDeviceTimeout`、`OnDeviceReconnected`）；修正 STATUS 端點改用穩定 IP；新增 C#、Python、JavaScript SDK |
| v1.0.0 | 首次發行 — .NET 8、預設 HTTPS、PBKDF2、工作階段持久化、輪替日誌、CORS、Unity SDK |

---

## 授權

EdgeLink Server 採用 **[MIT 授權](LICENSE)** 發行 — 可自由用於個人、商業與閉源用途，包含專有整合與 OEM 內嵌，無 copyleft 義務。只需保留版權聲明即可。

單一作者宣告與第三方致謝請見 [NOTICE](NOTICE)。

---

<div align="center">

**Extrakyo** · MIT 授權

</div>
