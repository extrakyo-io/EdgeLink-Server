<div align="center">

# EdgeLink Server

**IoT Protocol Bridge & Message Routing Server**

[![.NET](https://img.shields.io/badge/.NET-8.0-512BD4?logo=dotnet)](https://dotnet.microsoft.com)
[![Platform](https://img.shields.io/badge/Platform-Windows-0078D4?logo=windows)](https://github.com/extrakyo-io/EdgeLink-Server/releases)
[![License](https://img.shields.io/badge/License-MIT-blue)](LICENSE)
[![Version](https://img.shields.io/badge/Version-2.2.0-informational)](https://github.com/extrakyo-io/EdgeLink-Server/releases/tag/v2.2.0)

A lightweight .NET 8 server that bridges IoT devices over TCP/UDP, transforms protocol data via custom Mask definitions, and provides a browser-based management interface.

</div>

---

## Features

| Feature | Description |
|---------|-------------|
| **Multi-protocol** | TCP Server, TCP Client, UDP, **Modbus TCP Master** — each port configured independently |
| **Modbus polling** | Built-in FluentModbus master — poll ICP DAS / generic Modbus slaves at configurable intervals, results enter the routing pipeline as native EdgeLink messages |
| **Message Routing** | Automatically bridges ports via `SourceProtocolId` |
| **Mask System** | Custom protocol parsing and transformation rules for IoT firmware output |
| **Real-time Monitor** | Per-port SSE-streamed log with keyword search |
| **Web UI** | Browser-based management — card-grid port view, no frontend setup required |
| **Device Identification** | Per-connection device ID tracking on TCP Server and UDP, exposed in WebUI and SDKs |
| **HTTPS** | Auto-generated self-signed certificate with SAN for all local IPs |
| **Security** | PBKDF2 password hashing, session persistence, HttpOnly cookies |
| **File Logging** | Daily rolling log files with 7-day retention |
| **Client SDKs** | Unity (UPM, POCO + MonoBehaviour), Arduino, C# (.NET 6), Python (asyncio), JavaScript (Node.js) |

---

## System Architecture

EdgeLink Server sits at the center of a control system as the **fan-out hub**: a numeric-processing program polls field hardware over **Modbus TCP**, computes the application values, then pushes them into EdgeLink through an SDK client. EdgeLink applies a **Mask** to each message and **routes** it (by `SourceProtocolId`) over **UDP / TCP** to every downstream consumer at once.

The reference deployment below is an aerial-ladder water-cannon training rig. An **AX-5 motion controller** (Hall-joystick buttons / pedal / e-stop, servo drives) and a **3-axis platform signal-acquisition module** (16× DI absolute encoders, 4× AI dual-axis joysticks) are read by the numeric-processing program — Gray-code→angle, low-pass filtering, joystick 0.25–4.75 V / dual-axis 5 V check, attitude + singularity detection + inverse kinematics — which then feeds EdgeLink. EdgeLink fans the processed values out to Unity 3D and two additional computers.

![EdgeLink system architecture](docs/architecture.png)

> **Editable source (FigJam):** <https://www.figma.com/board/scwJNojVE3A1ouhtjYIwLU>

---

## Requirements

- Windows 10 / 11 x64
- No .NET Runtime required (self-contained)

---

## Installation

1. Download `EdgeLink-Server-v2.1.1-win-x64.zip` from [Releases](https://github.com/extrakyo-io/EdgeLink-Server/releases)
2. Extract the zip
3. Run `EdgeLinkServer.exe`
4. Open your browser at `https://localhost:8443`
   - Accept the self-signed certificate warning (click Advanced → Proceed)
   - Default password: `admin` — **change it immediately after login**

---

## Web UI

| Tab | Description |
|-----|-------------|
| **Ports** | Add / remove / toggle ports, monitor connection status and traffic in real-time |
| **Mask** | Create / edit / delete Mask definitions — field parsing rules and output templates |
| **System Log** | View server event logs with keyword filtering |

Full documentation available at `/manual` after starting the server.

---

## CLI Options

```
EdgeLinkServer.exe [options]

  --port <n>          HTTP port (default: 8081)
  --no-https          Disable HTTPS (HTTPS is enabled by default)
  --https-port <n>    HTTPS port (default: 8443)

Environment variables:
  EDGELINK_PORT, EDGELINK_HTTPS, EDGELINK_HTTPS_PORT
```

Priority: CLI args > environment variables > defaults

---

## Modbus TCP Master

Built-in Modbus TCP master can poll any Modbus slave (ICP DAS ET-7xxx, generic PLC, simulators like ModRSsim2) and inject results into the EdgeLink routing pipeline as standard messages — downstream consumers (Unity / dashboards / TCP Clients) don't know or care whether the source is Modbus or raw TCP.

### Quick example

Add a port via WebUI → **+ Add Port** → select `Modbus TCP Master`:

| Field | Value |
|-------|-------|
| Slave IP | `192.168.1.10` (or `127.0.0.1` for local simulator) |
| Modbus Port | `502` (default) |
| Slave ID | `1` |
| Polling Interval (ms) | `100` (10 Hz) |
| Device ID | `WaterCannon01` — becomes the `id:` field in synthesized messages |
| Registers | JSON array (see below) |

Register mapping JSON:

```json
[
  { "name": "yaw",   "functionCode": 2, "startAddress": 0, "quantity": 8, "dataType": "bits" },
  { "name": "pitch", "functionCode": 2, "startAddress": 8, "quantity": 8, "dataType": "bits" },
  { "name": "joyx",  "functionCode": 4, "startAddress": 0, "quantity": 1, "dataType": "uint16", "scale": 0.001 },
  { "name": "joyy",  "functionCode": 4, "startAddress": 1, "quantity": 1, "dataType": "uint16", "scale": 0.001 }
]
```

Every poll interval, EdgeLink emits a synthesized message like:

```
id:WaterCannon01;yaw:128;pitch:64;joyx:2.5;joyy:1.802
```

### Function codes

| FunctionCode | Modbus operation | Use |
|:-:|---|---|
| `1` | Read Coils | Output bits (relay state, LED) |
| `2` | Read Discrete Inputs | Switches, buttons, GrayCode encoder bits |
| `3` | Read Holding Registers | Generic 16-bit, read/write |
| `4` | Read Input Registers | Analog input (joystick, sensor voltage) |

### Data types

| dataType | Bytes | Notes |
|---|:-:|---|
| `bit` | 1 bit | Single Coil / DI (`quantity` must be 1) |
| `bits` | N bits | Packs `quantity` bits into an unsigned int (LSB first) |
| `uint16` / `int16` | 2 | One register |
| `uint32` / `int32` | 4 | Two registers, big-endian |
| `float32` | 4 | Two registers, IEEE 754 big-endian |

Optional `scale` and `offset` apply to register reads: `output = raw * scale + offset`.

### Test simulator

For development without real hardware, [`modbus_slave_sim.py`](modbus_slave_sim.py) provides a `pymodbus`-based Modbus TCP slave that generates synthetic GrayCode + sine wave data:

```bash
pip install pymodbus==3.6.6
python modbus_slave_sim.py
# Listens on 127.0.0.1:5020
```

---

## API Reference

Base URL: `https://<host>:8443`

All `/api/*` endpoints (except auth) require a session cookie. Interactive docs at `/docs`.

| Tag | Method | Path | Description |
|-----|--------|------|-------------|
| Auth | `POST` | `/api/auth/login` | Login — body: `{"password":"..."}` |
| Auth | `POST` | `/api/auth/logout` | Logout |
| Auth | `GET` | `/api/auth/status` | Check session status |
| Auth | `POST` | `/api/auth/change-password` | Change password |
| Ports | `GET` | `/api/ports` | List all ports |
| Ports | `POST` | `/api/ports` | Add port |
| Ports | `PUT` | `/api/ports/{id}` | Update port |
| Ports | `DELETE` | `/api/ports` | Delete port |
| Ports | `POST` | `/api/ports/{id}/enabled` | Toggle port enabled |
| Ports | `GET` | `/api/ports/{id}/clients` | List connected TCP clients |
| Masks | `GET` | `/api/masks` | List all mask IDs |
| Masks | `POST` | `/api/masks` | Add mask |
| Masks | `GET` | `/api/masks/{maskId}` | Get mask definition |
| Masks | `PUT` | `/api/masks/{maskId}` | Update mask |
| Masks | `DELETE` | `/api/masks/{maskId}` | Delete mask |
| Monitor | `GET` | `/api/monitor-stream` | SSE real-time message stream |
| Monitor | `POST` | `/api/monitor/port` | Set monitor port |
| Logs | `GET` | `/api/logs` | System logs (cursor pagination) |
| Settings | `GET` | `/api/settings/export` | Export all settings as JSON |
| Settings | `POST` | `/api/settings/import` | Import settings JSON |

---

## Unity SDK

The EdgeLink Unity SDK lets Unity applications receive data forwarded by EdgeLink Server. Supports TCP, TCP Listener, and UDP connection modes with automatic Mask fetching at runtime.

### Installation (UPM)

In Unity → **Window → Package Manager → + → Add package from git URL**:

```
https://github.com/extrakyo-io/EdgeLink-Server.git?path=SDK/Unity/Package
```

Then import the **Basic Example** sample via Package Manager → EdgeLink SDK → Samples.

### Usage — two flavors

| Style | Class | Setup |
|---|---|---|
| **MonoBehaviour** (drag onto GameObject, Inspector-configured) | `EdgeLinkManager` | Drop on any GameObject, fill in Inspector fields |
| **POCO** (constructor-configured, runtime URL/Host/Port) | `EdgeLinkBridge` | `new EdgeLinkBridge(...)` — drives lifecycle manually |

Use `EdgeLinkManager` for the common case (static settings). Use `EdgeLinkBridge` when URL / Host / Port must be decided at runtime (lobby-assigned IP, config file, multi-instance scenarios).

### Usage — MonoBehaviour (`EdgeLinkManager`)

Add `EdgeLinkManager` to any GameObject and configure in the Inspector:

| Field | Description |
|-------|-------------|
| Server URL | EdgeLink Server address for runtime Mask fetching |
| Password | Login password |
| Mask ID | Name of the Mask to apply for field parsing |
| Protocol | `TCP` / `TCPListener` / `UDP` |
| TCP Host / Port | (TCP mode) EdgeLink Server IP and port |
| Listen Port | (TCPListener mode) Local port Unity listens on |
| UDP Local Port | (UDP mode) Local UDP port |
| Device Id Key | Field name in the message that identifies the device (e.g. `id`). Leave empty to disable timeout tracking. |
| Device Timeout (s) | Seconds without a message before a device is considered offline (`0` = disabled) |

### Usage — POCO (`EdgeLinkBridge`)

Pure C# class — construct with parameters, drive the lifecycle from your own MonoBehaviour:

```csharp
using System.Collections;
using UnityEngine;
using EdgeLink;

public class RuntimeConfigured : MonoBehaviour
{
    EdgeLinkBridge _bridge;

    IEnumerator Start()
    {
        // URL / Host / Port from anywhere — lobby, config.json, PlayerPrefs ...
        _bridge = new EdgeLinkBridge(
            serverUrl: PlayerPrefs.GetString("edgelink.url"),
            tcpHost:   PlayerPrefs.GetString("edgelink.host"),
            tcpPort:   PlayerPrefs.GetInt   ("edgelink.port", 9001));

        // Or with full Config (TCPListener / UDP / device timeout / custom mask …):
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

Both `EdgeLinkManager` and `EdgeLinkBridge` expose the same event surface and `Raw` / `Get(key)` accessors.

### Reading Data

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

| Member | Type | Description |
|--------|------|-------------|
| `Raw` | `string` | Latest raw message string (unparsed) |
| `Get(key)` | `string` | Latest parsed value by field name, `null` if not found |

### Device Connect / Disconnect Detection

`EdgeLinkManager` provides two complementary disconnect mechanisms:

```csharp
void Start()
{
    edgeLink = GetComponent<EdgeLinkManager>();

    // Fired when EdgeLink Server detects a TCP connection open/close (~15 s for power-cut)
    edgeLink.OnDeviceStatus += (connected, endpoint) =>
        Debug.Log(connected ? $"Online: {endpoint}" : $"Offline: {endpoint}");

    // Fired when a specific device ID stops sending data for Device Timeout seconds
    edgeLink.OnDeviceTimeout     += id => Debug.LogWarning($"{id} timed out");
    edgeLink.OnDeviceReconnected += id => Debug.Log($"{id} reconnected");
}
```

| Event | Trigger | Identifies by |
|-------|---------|---------------|
| `OnDeviceStatus` | EdgeLink Server detects TCP open/close | IP address |
| `OnDeviceTimeout` | No message received for `Device Timeout (s)` | Device ID field |
| `OnDeviceReconnected` | Message received again after timeout | Device ID field |

> **Power-cut scenario:** EdgeLink detects 3 missed PINGs (≈15 s) then fires `OnDeviceStatus(false, ...)`. After your configured timeout with no new messages, `OnDeviceTimeout` also fires.

#### When to use which event

| Scenario | OnDeviceStatus | OnDeviceTimeout |
|----------|:-:|:-:|
| Device power-cut (TCP) | ✓ ~15 s | ✓ after timeout setting |
| Device disappears over UDP | ✗ no connection to detect | ✓ |
| TCP alive but firmware hangs (no data) | ✗ connection is normal | ✓ |
| Multiple devices sharing one TCP connection | ✗ can only detect connection drop | ✓ identifies each device |

Use **both** for full coverage: `OnDeviceStatus` catches TCP-level drops; `OnDeviceTimeout` catches silent failures and works across all protocols.

---

## Arduino SDK

The EdgeLink Arduino Library lets ESP32 / ESP8266 / Arduino devices connect to EdgeLink Server. PING/PONG keepalive is handled automatically — no extra code needed.

### Installation

**Option A — ZIP import:**
1. Download `SDK/Arduino/EdgeLink.zip`
2. Arduino IDE → **Sketch → Include Library → Add .ZIP Library…**

**Option B — manual:**  
Copy the `SDK/Arduino/EdgeLink/` folder into your Arduino `libraries/` directory.

### TCP Example (ESP32 / ESP8266)

```cpp
#include <WiFi.h>
#include <EdgeLink.h>

WiFiClient  wifiClient;
EdgeLinkTCP edgelink(wifiClient);

void onMessage(const String& msg) {
    Serial.println(msg);  // response from backend
}

void setup() {
    WiFi.begin("your-ssid", "your-password");
    while (WiFi.status() != WL_CONNECTED) delay(500);

    edgelink.onMessage(onMessage);
    edgelink.setAutoReconnect(true, 5000);
    edgelink.begin("192.168.1.100", 9001);  // EdgeLink TCP Server port
}

void loop() {
    edgelink.loop();  // must be called — handles PING/PONG and receive

    static uint32_t t = 0;
    if (millis() - t >= 3000 && edgelink.isConnected()) {
        edgelink.send("id:ESP32_01;temp:25.3;humidity:60.0");
        t = millis();
    }
}
```

### UDP Example (ESP32 / ESP8266)

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

    edgelink.begin(4210);           // local receive port
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

| Class | Method | Description |
|-------|--------|-------------|
| `EdgeLinkTCP` | `begin(host, port)` | Connect to EdgeLink TCP Server port |
| | `loop()` | Must call in `loop()` — handles PING/PONG and receive |
| | `send(msg)` | Send a message (newline appended automatically) |
| | `onMessage(cb)` | Callback for incoming messages (EDGELINK_* filtered out) |
| | `isConnected()` | Returns connection state |
| | `setAutoReconnect(enable, ms)` | Auto-reconnect on disconnect (default: enabled, 5000 ms) |
| `EdgeLinkUDP` | `begin(localPort = 0)` | Start listening on local UDP port (`0` = send-only) |
| | `loop()` | Must call in `loop()` — receives incoming packets |
| | `send(host, port, msg)` | Send UDP packet to EdgeLink |
| | `onMessage(cb)` | Callback with `(msg, remoteIP, remotePort)` |

---

## C# SDK

The EdgeLink C# SDK targets **.NET 6+** and works in any non-Unity .NET application (console, WPF, ASP.NET Core, etc.). No third-party dependencies.

### Installation

Copy the [SDK/CSharp/](SDK/CSharp/) folder into your solution and add a project reference, or build it as a class library and reference the DLL.

### TCP Example

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

| Class | Member | Description |
|-------|--------|-------------|
| `EdgeLinkClient` | `ConnectAsync()` | Connect and start read loop in background |
| | `SendAsync(msg)` | Send a message |
| | `IsConnected` | Connection state |
| | `SetAutoReconnect(enable, delayMs)` | Auto-reconnect on disconnect (default: enabled, 5000 ms) |
| | `OnMessage / OnConnected / OnDisconnected / OnError` | Events |
| | `TryDequeue(out msg)` | Poll-based alternative to the event |
| `EdgeLinkTcpListener` | `Start()` | Accept incoming TCP connections |
| | `Stop()` | Stop the listener |
| | `OnMessage / OnConnected / OnDisconnected / OnError` | Events |
| `EdgeLinkUdpClient` | `Start()` | Bind to local port and receive packets |
| | `OnMessage / OnError` | Events |
| `EdgeLinkUdpSender` | `SendAsync(host, port, msg)` | Send UDP packet |

---

## Python SDK

The EdgeLink Python SDK requires **Python 3.10+** and uses only the standard library (`asyncio`, `socket`).

### Installation

```bash
pip install SDK/Python   # local install
# or simply copy the edgelink/ package into your project
```

### TCP Example

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

| Class | Member | Description |
|-------|--------|-------------|
| `EdgeLinkClient` | `connect()` | Connect and start read loop (coroutine) |
| | `send(msg)` | Send a message (coroutine) |
| | `is_connected` | Connection state |
| | `set_auto_reconnect(enable, delay)` | Auto-reconnect on disconnect (default: enabled, 5 s) |
| | `on_message / on_connected / on_disconnected / on_error` | Register callbacks |
| | `try_dequeue()` | Poll-based alternative to callbacks |
| | `disconnect()` | Close connection (coroutine) |
| `EdgeLinkTcpListener` | `start()` | Start listening (coroutine) |
| | `stop()` | Stop listener (coroutine) |
| | `on_message / on_connected / on_disconnected / on_error` | Register callbacks |
| `EdgeLinkUdpClient` | `start()` | Bind and start receiving (coroutine) |
| | `on_message / on_error` | Register callbacks |
| `EdgeLinkUdpSender` | `send(host, port, msg)` | Send UDP packet |
| | `send_async(host, port, msg)` | Send UDP packet (coroutine) |

---

## JavaScript SDK

The EdgeLink JavaScript SDK targets **Node.js 18+** and uses only built-in modules (`net`, `dgram`, `events`).

### Installation

```bash
# copy SDK/JavaScript/ into your project, then:
const { EdgeLinkClient } = require("./edgelink/src");
```

### TCP Example

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

| Class | Member | Description |
|-------|--------|-------------|
| `EdgeLinkClient` | `connect()` | Connect and start reading |
| | `send(msg)` | Send a message |
| | `isConnected` | Connection state |
| | `setAutoReconnect(enable, delayMs)` | Auto-reconnect on disconnect (default: enabled, 5000 ms) |
| | `disconnect()` | Destroy the socket |
| | Events: `"connected" / "disconnected" / "message" / "error"` | `EventEmitter` events |
| `EdgeLinkTcpListener` | `start()` | Start TCP server |
| | `stop()` | Stop TCP server |
| | Events: `"connected" / "disconnected" / "message" / "error"` | `EventEmitter` events |
| `EdgeLinkUdpClient` | `start()` | Bind and receive UDP packets |
| | `stop()` | Stop receiving |
| | Events: `"message" / "error"` | `EventEmitter` events |
| `EdgeLinkUdpSender` | `send(host, port, msg)` | Send UDP packet (returns `Promise`) |
| | `close()` | Close socket |

---

## Project Structure

```
EdgeLink-Server/
├── Server/
│   ├── Infrastructure/      # AppConfig, AppLogger, AppPaths, CertificateHelper
│   ├── NetworkServer/
│   │   ├── Base/            # Connector base, models (PortData, ...)
│   │   ├── TCP/             # TCPServerConnector, TCPClientConnector
│   │   ├── Udp/             # UdpConnector
│   │   ├── Router/          # NetworkMessageRouter
│   │   └── Services/        # PortManager, PortDataStorageService
│   ├── WebApi/              # HttpApiServer, Auth/Port/Mask/Monitor handlers
│   ├── WebUI/               # Frontend HTML/CSS/JS (index, manual, docs)
│   └── Program.cs           # Entry point
└── SDK/
    ├── Unity/
    │   └── Package/         # UPM package (Runtime + Editor + Samples~)
    ├── Arduino/
    │   └── EdgeLink/        # Arduino library (TCP + UDP, PING/PONG auto-handling)
    ├── CSharp/              # .NET 6 class library (TCP client/listener + UDP)
    ├── Python/              # Python 3.10+ package using asyncio (TCP + UDP)
    └── JavaScript/          # Node.js 18+ package using net/dgram (TCP + UDP)
```

---

## Changelog

| Version | Changes |
|---------|---------|
| v2.2.0 | **Generic binary-parsing Mask** (`BinarySpec`: offsets / types u8–f64 / bit / bitrange / const, little-big-endian, discriminator dispatch, length validation) — decode fixed-layout binary UDP into KV; WebUI binary-layout editor + hex decode preview; `POST /api/masks/preview-binary`. Monitor SSE front-end throttling (high-rate ports no longer choke the browser) |
| v2.1.3 | Unity SDK: connection resource cleanup — prevent socket / CTS leak |
| v2.1.2 | Unity SDK: add `EdgeLinkBridge.cs.meta`; package version bump |
| v2.1.1 | Unity SDK: extracted `EdgeLinkBridge` POCO with constructor-injected URL/Host/Port (MonoBehaviour `EdgeLinkManager` still works as a thin wrapper); README updated with Modbus + POCO docs |
| v2.1.0 | **Modbus TCP Master** port type (FluentModbus, FC 01/02/03/04, scale/offset); WebUI card-grid overhaul; 4× fire-and-forget tasks suppressed; UDP port-list polling preserves user selection |
| v2.0.1 | Unity SDK: `OnDeviceStatus` adds device-ID parameter; Arduino AsyncUDP example; HttpClientHandler cert fallback to `ServicePointManager` on Mono |
| v2.0.0 | Per-connection device-identification on TCP Server / UDP; PING/PONG TCP keepalive; `EDGELINK_STATUS:CONNECTED/DISCONNECTED` events forwarded with device IDs; WebUI shows identified devices per port |
| v1.1.0 | Unity SDK — device connect/disconnect detection (`OnDeviceStatus`, `OnDeviceTimeout`, `OnDeviceReconnected`); fix STATUS endpoint to use stable IP; C#, Python, JavaScript SDKs added |
| v1.0.0 | Initial release — .NET 8, HTTPS by default, PBKDF2, session persistence, rolling logger, CORS, Unity SDK |

---

## License

EdgeLink Server is released under the **[MIT License](LICENSE)** — free for personal, commercial, and closed-source use, including proprietary integration and OEM embedding, with no copyleft obligation. Just keep the copyright notice.

See [NOTICE](NOTICE) for sole-authorship declaration and third-party attribution.

---

<div align="center">

**Extrakyo** · MIT License

</div>
