# 變更紀錄

## [2.6.0]

### 修正

- **裝置離線偵測改用 `Time.unscaledTime`。** 先前用 `Time.time`，那是受 `timeScale`
  影響的遊戲時間 —— VR 訓練程式開暫停選單設 `timeScale = 0` 是標準做法，那一刻起
  逾時偵測完全停擺，暫停期間把設備拔掉也不會觸發 `OnDeviceTimeout`。
- **接收佇列加上上限（預設 1000，滿了丟最舊的）與每幀處理上限（預設 500）。**
  元件被 disable 時 `Update()` 不跑，背景 socket 照收，佇列會一路長到記憶體耗盡；
  恢復的那一幀則會一次處理完整個 backlog。丟棄不是靜默的 ——
  `EdgeLinkBridge.DroppedMessageCount` 拿得到累計數。
- **`EdgeLinkBridge` 新增 `OnError` 事件。** 先前錯誤只進 `Debug.LogWarning`，
  無法用程式反應；而 `SendAsync` 失敗時丟的例外訊息卻叫人「見 OnError」，
  指向一個不存在的東西。
- **`Connect()` 的 TCPListener 與 UDP 兩個分支補上 try。** 埠被占用時
  `Start()` 丟的 `SocketException` 會從 `async void` 逃出去變成未處理例外。
- **`EdgeLinkTcpListener.IsRunning` 改成綁定成功之後才設。** 先前擺在
  `listener.Start()` 之前，埠被占用時物件會謊報自己在跑。
- **`EdgeLinkUdpSender` 的 DNS 快取加上 TTL（預設 5 分鐘）。** 先前永不失效，
  對端換 IP 之後會永遠打舊位址直到程式重啟，而 UDP 送出本來就沒有回報。
- **Inspector 改用 `SerializedProperty`。** 先前直接寫欄位，Undo 無效、
  prefab override 不成立（藍色標記與 Revert 都不會出現）。
- 套件補上 README / CHANGELOG / LICENSE。

## [2.5.0]

- `EdgeLinkTcpListener` 新增 `SendAsync` / `ConnectionCount`，可寫回連進來的 EdgeLink。
- `EdgeLinkUdpSender` 移植進 Unity SDK 並加上端點快取。
- `EdgeLinkManager` / `EdgeLinkBridge` 三種 Protocol 都能送，並提供 `Num()`
  （固定 `InvariantCulture`、擋 NaN/Infinity）與欄位分隔符驗證。
- 每條連線一把寫入鎖，PONG 與使用者送出共用同一把。
- **安全性**：移除 Editor 面板的 TLS 憑證驗證繞過。v2.4.3 宣稱已移除但只改到
  Runtime，Editor 這支被漏掉，而它的 Mono fallback 走 `ServicePointManager`
  是**行程全域**的。
