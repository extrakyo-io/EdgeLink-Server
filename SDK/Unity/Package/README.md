# EdgeLink SDK for Unity

把 EdgeLink Server 的資料收進 Unity，並把命令送回去。二進位協定由 EdgeLink 端的
Mask 處理，Unity 這側只看得到 `key:value` 文字。

## 安裝

Package Manager → **Add package from disk…** → 選這個資料夾的 `package.json`。
或直接在 `Packages/manifest.json` 加一行：

```json
"com.extrakyo.edgelink": "file:../../EdgeLink-Server/SDK/Unity/Package"
```

路徑是相對於專案的 `Packages` 資料夾。

## 兩種用法

**MonoBehaviour** — 把 `EdgeLinkManager` 掛到任一 GameObject，在 Inspector 填設定：

```csharp
void Start() {
    var el = GetComponent<EdgeLinkManager>();
    el.OnMessage += msg => Debug.Log(msg);
}

void Fire() {
    var el = GetComponent<EdgeLinkManager>();
    if (el.CanSend)
        el.Send(("cmd", "start"), ("power", EdgeLinkManager.Num(0.8f)));
}
```

**POCO** — URL / Host / Port 要在執行期才決定時用 `EdgeLinkBridge`，
自己驅動 `Tick()` 與 `Dispose()`（見 Samples 的「純程式碼建構」）。

## 送出目的地

| Protocol | 送到哪 | `CanSend` 條件 |
|---|---|---|
| TCP | 那條對外連線 | 已連上 |
| TCPListener | 所有連進來的對端 | 有對端連進來 |
| UDP | `Udp Target Host:Port` | 有設目的地 |

UDP 的目的地與收資料的 Local Port **不是同一個**：EdgeLink 的 UDP 埠是
「聽 remotePort、轉發到 localPort」，要送進去得打它的監聽埠。

## 數值一定要用 `Num()`

```csharp
el.Send(("temp", EdgeLinkManager.Num(25.3f)));   // 對
el.Send(("temp", temp.ToString()));              // 錯:某些地區設定會輸出 "25,3"
```

系統地區設定會把小數點變成逗號，而逗號在 KV 裡沒有特殊意義 —— EdgeLink 會把整包
丟掉，而且**只在特定地區的機器上發生**，開發機永遠測不到。`Num()` 固定
`InvariantCulture` 並擋掉 NaN / Infinity。

## 流量控制

`EdgeLinkBridge.Config` 兩個相關設定：

| 欄位 | 預設 | 說明 |
|---|---|---|
| `MaxMessagesPerTick` | 500 | 單一幀最多處理幾筆。0 = 不設限 |
| `MaxQueuedMessages` | 1000 | 接收佇列上限，滿了丟最舊的。0 = 不設限 |

元件被 disable 或場景載入卡住時 `Tick()` 不會跑，背景 socket 照收。有上限才不會
一路長到記憶體耗盡，也不會在恢復的那一幀一次處理完整個 backlog。丟棄不是靜默的：
`EdgeLinkBridge.DroppedMessageCount` 拿得到累計數，Console 也會在數字變動時提醒。

## 範例

Package Manager → EdgeLink SDK → Samples 匯入，共四個：TCP Listener、
TCP Listener（含斷線偵測）、純程式碼建構 (POCO)、雙向（收狀態 + 送命令）。

完整文件見 repo 根目錄的 README。
