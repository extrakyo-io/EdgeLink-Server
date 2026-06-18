# TCPListener 模式模擬情境

## 架構

```
FakeArduino ──TCP──▶ EdgeLink Port 8888 (TCP Server)
                          ↓ 路由轉發
                     EdgeLink Port 9001 (TCP Client) ──TCP──▶ FakeUnity
```

## EdgeLink Server 設定步驟

1. 開啟 EdgeLink Web UI（https://localhost:8443）
2. 新增 **TCP Server Port 8888**（等待 Arduino 連入）
3. 新增 **TCP Client Port 9001**
   - Host：執行 FakeUnity 的機器 IP（本機測試填 `127.0.0.1`）
   - Port：`9001`
   - SourceProtocolId：選擇 Port 8888 的 ID（路由設定）

## 執行順序

```bash
# 終端機 1 — 先啟動 FakeUnity（開 Port 等待）
cd FakeUnity
dotnet run

# 終端機 2 — 再啟動 EdgeLink Server（publish 目錄）
EdgeLinkServer.exe

# 終端機 3 — 最後啟動 FakeArduino（模擬感測器）
cd FakeArduino
dotnet run
```

## 預期輸出

**FakeUnity**
```
[FakeUnity] 監聽 Port 9001，等待 EdgeLink 連入...
[FakeUnity] EdgeLink 已連入
[FakeUnity] ▲ 設備上線  IP: 127.0.0.1
[FakeUnity] [ESP32_SIM] seq=1 temp=25.0°C humid=60.0%
[FakeUnity] [ESP32_SIM] seq=2 temp=25.9°C humid=59.0%
...
# 關閉 FakeArduino 後約 15 秒
[FakeUnity] ▼ 設備斷線  IP: 127.0.0.1
[FakeUnity] ⚠ ESP32_SIM 超時 20s，可能已離線
```

**FakeArduino**
```
[FakeArduino] 已連線，開始傳送資料...
[FakeArduino] 送出 → id:ESP32_SIM;seq:1;temp:25.0;humid:60.0
[FakeArduino] ← PING  → PONG
[FakeArduino] 送出 → id:ESP32_SIM;seq:2;temp:25.9;humid:59.0
...
```
