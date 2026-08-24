# VR 端（Client）KV 對照表

給接 EdgeLink 的 VR／Client 開發者。**你只會看到 `key:value` 文字，不用碰任何二進位。**
二進位的解析、位元拆解、查表、序號產生全部在 EdgeLink 裡完成。

對照的規格版本：

| 鏈路 | 規格 | 方向 |
|---|---|---|
| 平台 | 消防訓練平台 TCP 資料格式 **V1** | **雙向** — 你送命令、也收狀態 |
| 設備 | 搖桿-編碼器-踏板-按鈕 UDP 資料格式 **V1.1** | 單向 — 你只收 |

---

## 1. 線上格式

一行一筆訊息，UTF-8，`\n` 結尾：

```
mt:19;seq:52343;estop:0;plc:2;plctxt:CONNECTED;pa:210.0;pb:210.0;pc:210.0
```

- 欄位分隔 `;`、鍵值分隔 `:`
- 數值一律 **InvariantCulture**（小數點是 `.`，不是 `,`）
- **沒有跳脫機制** — 值裡不能出現 `;` 或 `:` 或換行
- 欄位順序固定，但**請用鍵取值，不要靠位置**

### 收不到某個欄位怎麼辦

一筆訊息只帶它自己那一類的欄位。三種設備訊息（搖桿／按鈕／編碼器）各自更新自己的部分，
**收端合併保留最新值**即可 —— 這是協定文件建議的做法。

判讀順序：**先看 `conn` → 再看故障旗標 → 最後才用數值。**

---

## 2. 連線方式

| 鏈路 | 你要做什麼 | 位址 |
|---|---|---|
| 平台 TCP | 連到 EdgeLink，雙向讀寫 | `EdgeLink:47900` |
| 設備 UDP | 綁一個本機 UDP 埠等資料 | EdgeLink 轉發到 `你的IP:47811` |

平台那條 EdgeLink 會**約每 5 秒**送一次心跳，你要把 token **原樣**回一筆 PONG：

```
收到  EDGELINK_PING:08DF01E2BF21A0A9
回覆  EDGELINK_PONG:08DF01E2BF21A0A9
```

**連續 3 次沒回（約 15 秒）會被主動斷線。** token 長度不要寫死，原樣回傳即可。

所有 `EDGELINK_` 開頭的行都是協定訊息，**不要當成資料解析**。另外還有
`EDGELINK_STATUS:...`（上下游裝置上下線通知）。用官方 SDK 的話這些都已經處理掉了。

---

## 3. 設備輸入（UDP，V1.1）

三種訊息，靠帶什麼欄位分辨：有 `jlx` = 搖桿、有 `bl1` = 按鈕、有 `e1p` = 編碼器。

### 3.1 每筆都有

| 欄位 | 型別 | 意義 |
|---|---|---|
| `id` | 字串 | 裝置識別，固定 `rig1` |
| `seq` | uint32 | 序號，**每種訊息各一條獨立計數**。首包 = 1、mod 2³² 回繞 |
| `ts` | uint64 | 送出當下的 Unix epoch UTC 毫秒 |
| `conn` | 0–3, 255 | Modbus 連線狀態，見下 |
| `units` | 0–2 | 有幾個 slot 有效。`0` = 該裝置未啟用 |

`conn` 對照：`0` Disconnected、`1` Connecting、`2` Connected、`3` Reconnecting、`255` NotPresent。

> **只有 `conn:2` 時數值才有效。** 其餘狀態代表送端自己說資料不可信。
>
> `seq` **倒退代表送端重啟**，跳號代表掉包 —— UDP 沒有重送。

### 3.2 搖桿

| 欄位 | 型別 | 意義 |
|---|---|---|
| `jlx` `jly` | float | 左桿 X／Y，−1 ~ 1 |
| `jrx` `jry` | float | 右桿 X／Y |
| `jlf` `jrf` | 0–3 | 冗餘故障位元：bit0 = X 軸、bit1 = Y 軸。**故障軸的值會被送端強制歸 0** |
| `jlst` `jrst` | 0/1 | Stale — 超過 1 秒沒收到該裝置的資料 |
| `jlraw` `jrraw` | 0/1 | `1` = 未經 EMA 低通濾波（較跳但無延遲）；`0` = 已平滑 |

### 3.3 按鈕、急停、踏板 — **V1.1 新增**

| 欄位 | 值 | 意義 |
|---|---|---|
| `bl1` `bl2` `bl3` | 0/1 | 左桿三顆按鈕，`1` = 按下 |
| `br1` `br2` `br3` | 0/1 | 右桿三顆按鈕 |
| **`estopl`** | `OK` / `ESTOP` | **左搖桿急停** |
| **`estopr`** | `OK` / `ESTOP` | **右搖桿急停** |
| **`pedal`** | 0/1 | **踏板**，`1` = 踩下 |
| `blst` `brst` | 0/1 | Stale |
| `estopl2` `estopr2` `pedal2` | 同上 | 右 slot 帶的同一份訊號，可作交叉驗證 |

> ### 急停請務必照這段實作
>
> **線路上的急停位是反相的（`1` = 鬆開、`0` = 按下）**，但 EdgeLink 已經幫你正規化成
> `ESTOP` / `OK` 字串，所以**你不用處理反相**。直接比對字串即可：
>
> ```
> 急停中 ⟺ estopl == "ESTOP" || estopr == "ESTOP"
> ```
>
> **收不到 `estopl` / `estopr` 欄位時，一律當成急停中。** 不要用「找不到就當沒事」的預設 —— 那對一般欄位合理，對安全訊號是事故。舊版設備韌體的那幾個位元是 0，照 V1.1 讀就是急停按下；兩種情況都必須往安全的那邊倒。
>
> 左右兩支搖桿的封包都帶**同一份**急停與踏板狀態，`estopl` 與 `estopl2` 應該永遠相等。不相等代表封包有問題。

### 3.4 編碼器（水砲）

| 欄位 | 型別 | 意義 |
|---|---|---|
| `e1p` `e2p` | 0–255 | 絕對位置 |
| `e1deg` `e2deg` | float | 角度 0–360，順時針 |
| `e1gm` `e2gm` | 0/1 | GrayMismatch — 編碼器 checksum 錯 |
| `e1st` `e2st` | 0/1 | Stale |

編碼器**沒有低通濾波**，數值恆為未平滑值（所以沒有 `raw` 旗標）。

> 故障時角度請**保留最後有效值**，不要歸零 —— 0 度是一個真實方位。

### 3.5 值域一覽

設備側每個欄位的完整取值。**沒列在這裡的值不會出現** —— 出現了代表封包有問題。

| 欄位 | 可能的值 |
|---|---|
| `id` | `rig1`（固定字串） |
| `seq` | 0 ~ 4294967295（首包 1，mod 2³² 回繞） |
| `ts` | Unix epoch UTC 毫秒 |
| `conn` | `0` / `1` / `2` / `3` / `255` |
| `units` | `0` / `1` / `2` |
| `jlx` `jly` `jrx` `jry` | −1.0 ~ 1.0（故障軸強制 0） |
| `jlf` `jrf` | `0` 正常、`1` X 軸冗餘故障、`2` Y 軸、`3` 兩軸都故障 |
| `jlst` `jrst` | `0` / `1` |
| `jlraw` `jrraw` | `0` 已平滑 / `1` 未濾波 |
| `bl1` `bl2` `bl3` `br1` `br2` `br3` | `0` 放開 / `1` 按下 |
| `estopl` `estopr` `estopl2` `estopr2` | `OK` / `ESTOP`（只有這兩個字串） |
| `pedal` `pedal2` | `0` 沒踩 / `1` 踩下 |
| `blst` `brst` | `0` / `1` |
| `e1p` `e2p` | 0 ~ 255 |
| `e1deg` `e2deg` | 0.0 ~ 360.0（順時針） |
| `e1gm` `e2gm` | `0` / `1`（GrayMismatch） |
| `e1st` `e2st` | `0` / `1`（Stale） |

設備側是**單向的** —— 你只會收到，沒有任何命令可以送回設備。要控制平台請用
`mt:16` / `mt:17`。

---

## 4. 平台（TCP，V1，雙向）

### 4.1 你送出：`mt:16` 移動命令

```
mt:16;mode:1;rqx:0;rqy:0;rqz:0;rqw:0;rhv:0;rqa:210;rqb:210;rqc:210;vel:50;acc:50;jrk:50
```

**`mode` 決定哪一組欄位有效**，另一組必須明確送 0：

| mode | 意義 | 有效欄位 | 要送 0 的 |
|---|---|---|---|
| `0` | 給平台四元數，**對端解 IK** | `rqx` `rqy` `rqz` `rqw` `rhv` | `rqa` `rqb` `rqc` |
| `1` | 直接指定三軸絕對位置 | `rqa` `rqb` `rqc` | `rqx` `rqy` `rqz` `rqw` `rhv` |

| 欄位 | 值域 | 意義 |
|---|---|---|
| `mode` | `0` / `1` | 見上表 |
| `rqx` `rqy` `rqz` `rqw` | float | 目標姿態四元數（mode 0 有效） |
| `rhv` | float, mm | 目標 heave（mode 0 有效） |
| `rqa` `rqb` `rqc` | float, mm | A/B/C 三軸絕對位置（mode 1 有效） |
| `vel` | float | S Curve 速度。**0 = 沿用對端預設** |
| `acc` | float | S Curve 加／減速度。**0 = 沿用預設** |
| `jrk` | float | S Curve Jerk。**0 = 沿用預設** |

`seq`、`ts`、`len` 由 EdgeLink 自動填，**不要自己送**。缺欄位整包會被丟掉 —— EdgeLink 不會替你補 0。

mode 1（直接指定三軸）：

```
mt:16;mode:1;rqx:0;rqy:0;rqz:0;rqw:0;rhv:0;rqa:210;rqb:210;rqc:210;vel:0;acc:0;jrk:0
```

mode 0（給姿態＋高度，對端解 IK）：

```
mt:16;mode:0;rqx:0.0436;rqy:0;rqz:0;rqw:0.999;rhv:210;rqa:0;rqb:0;rqc:0;vel:0;acc:0;jrk:0
```

Unity SDK：

```csharp
// mode 1 —— 三軸絕對位置
_link.Send(("mt", "16"), ("mode", "1"),
           ("rqx", "0"), ("rqy", "0"), ("rqz", "0"), ("rqw", "0"), ("rhv", "0"),
           ("rqa", EdgeLinkManager.Num(210f)),
           ("rqb", EdgeLinkManager.Num(210f)),
           ("rqc", EdgeLinkManager.Num(210f)),
           ("vel", "0"), ("acc", "0"), ("jrk", "0"));

// mode 0 —— 姿態 + 高度
Quaternion q = Quaternion.Euler(5f, 0f, 0f);
_link.Send(("mt", "16"), ("mode", "0"),
           ("rqx", EdgeLinkManager.Num(q.x)), ("rqy", EdgeLinkManager.Num(q.y)),
           ("rqz", EdgeLinkManager.Num(q.z)), ("rqw", EdgeLinkManager.Num(q.w)),
           ("rhv", EdgeLinkManager.Num(210f)),
           ("rqa", "0"), ("rqb", "0"), ("rqc", "0"),
           ("vel", "0"), ("acc", "0"), ("jrk", "0"));
```

### 4.2 你送出：`mt:17` 管理命令

```
mt:17;act:1
```

| act | 名稱 | 作用 |
|---|---|---|
| `1` | `EStopOn` | 軟體急停 ON |
| `2` | `EStopOff` | 軟體急停 OFF |
| `3` | `ServoToggle` | **切換**伺服馬達 On/Off —— 不是設定 |
| `4` | `Reset` | 軟體 Reset，清除異常 |

```
mt:17;act:1     急停 ON
mt:17;act:2     急停 OFF
mt:17;act:3     伺服切換
mt:17;act:4     Reset
```

```csharp
_link.Send(("mt", "17"), ("act", "3"));    // 伺服切換
```

> **`act:3` 是切換不是設定。** 按下去之前要先看 `gsttxt` 現在是不是 `DISABLED`，
> 否則你可能把伺服關掉而不是打開。
>
> 伺服關著時所有 `mt:16` 都會被回 **`rc:5` 該模式不允許 Move**。

### 4.3 你收到：`mt:18` 命令執行結果

每一筆 `mt:16` / `mt:17` 都會回一筆。

| 欄位 | 意義 |
|---|---|
| `ackseq` `ackmt` | **回應的是哪一筆命令** — 用這兩個配對，不要靠順序 |
| `res` / `restxt` | `0` `ACCEPT` / `1` `REJECT` |
| `rc` / `rctxt` | 被拒原因，見下 |
| `axis` / `axistxt` | 哪一支軸出問題：`-1` NA、`0` A、`1` B、`2` C |
| `test` | `1` = 對端收下了但沒有真的輸出（測試環境） |
| `rpa` `rpb` `rpc` | 平台回報的三軸位置 |

`rc` 完整對照（來自 `PlatformTcp.mask.json` 的 `maps.reason`）：

| rc | 意義 | | rc | 意義 |
|---:|---|---|---:|---|
| 0 | 無 | | 8 | 保留 |
| 1 | 超出行程界限(mm) | | 9 | WatchDog逾時 |
| 2 | 超出行程界限(場景單位) | | 10 | seq倒退或重複 |
| 3 | 水平偏移超過球頭範圍 | | 11 | mode1已關閉 |
| 4 | 尚未讀到PLC狀態 | | 12 | IK還原不出姿態 |
| 5 | 控制器不允許Move | | 13 | 數值不合法(NaN/Inf/超範圍) |
| 6 | E-Stop生效中 | | 14 | velocity/accel/jerk超上限 |
| 7 | PLC錯誤(EtherCAT/驅動器/ErrorStop) | | 15 | 輸入來源未切External |

最常遇到的兩個：**`6` E-Stop 生效中**、**`5` 該模式不允許 Move**（伺服沒開）。速度參數超限是 **`14`**，不是 `3`。

### 4.4 你收到：`mt:19` 平台狀態

約 50 Hz 推送。

| 欄位 | 意義 |
|---|---|
| `ack` | 最後被採納的移動命令 seq |
| `estop` | 0/1，**平台自己的急停**（與搖桿急停是兩回事） |
| `out` | 0/1，`0` = 沒有真的輸出 |
| `plc` / `plctxt` | `0` DISCONNECTED、`1` CONNECTING、`2` CONNECTED、`3` RECONNECTING |
| `gst` / `gsttxt` | `0` DISABLED、`1` STANDBY、`2` MOVING、`3` HOMING、`4` STOPPING、`5` ERRORSTOP |
| `ecerr` / `ecerrtxt` | EtherCAT 錯誤，`0`「正常」以外都要處理（見下方完整表） |
| `derra` `derrb` `derrc` + `derratxt` `derrbtxt` `derrctxt` | A/B/C 三軸驅動器異警，`0` = 正常（共 40 種） |
| `pa` `pb` `pc` | 三軸**實際**位置 (mm) |
| `cpa` `cpb` `cpc` | 三軸**命令**位置 (mm) |
| `qx` `qy` `qz` `qw` | 由實際位置反解的姿態四元數 |
| `hv` | heave (mm) |
| `resid` | 殘差 (mm)，過大代表超出機構可達範圍 |

> 凡是有 `xxx` 與 `xxxtxt` 成對的欄位，`xxx` 是原始碼、`xxxtxt` 是查表後的文字。
> **判斷邏輯請用原始碼**（文字可能因表更新而變動），**顯示給人看用文字**。

---

## 5. 錯誤碼完整表

都來自 `PlatformTcp.mask.json` 的 `maps`，這裡列的是全部。

### ecerr — EtherCAT（17 種）

| 值 | 意義 | | 值 | 意義 |
|---|---|---|---|---|
| `0` | 正常 | | `9` | 產品 ID 不符 |
| `1` | 無通訊 | | `10` | 裝置數量不符 |
| `2` | 工作計數器不符 | | `11` | SDO 寫入錯誤 |
| `3` | 分散式時脈為零 | | `12` | SDO 逾時 |
| `4` | 主網卡開啟失敗 | | `13` | 收到緊急訊息 |
| `5` | 備援網卡開啟失敗 | | `14` | IDN 寫入錯誤 |
| `6` | 網卡不符 | | `15` | IDN 逾時 |
| `7` | 找不到從站 | | `16` | 看門狗錯誤 |
| `8` | 廠商 ID 不符 | | `` |  |

### derra / derrb / derrc — 驅動器異警（40 種）

三軸各一個。`0` = 「正常」。

| 值 | 意義 | | 值 | 意義 |
|---|---|---|---|---|
| `0` | 正常 | | `0x6310` | 物件字典初始化失敗 |
| `0x0207-0x0249` | PR 參數錯誤 | | `0x6320` | 電子齒輪比錯誤 |
| `0x2310` | 過電流 | | `0x7036` | OA／OB 輸出異常 |
| `0x3110` | 過電壓 | | `0x7121` | 馬達碰撞 |
| `0x3120` | 欠電壓 | | `0x7122` | 馬達型號不符 |
| `0x3130` | 主電源異常 | | `0x7305` | 絕對式編碼器異常 |
| `0x3210` | 回生電阻異常 | | `0x7306` | OA／OB 輸出異常 |
| `0x3230` | 過載 | | `0x7520` | 串列通訊逾時 |
| `0x3231` | 過載預警 | | `0x8100` | 匯流排資料錯誤 |
| `0x3300` | 馬達配線錯誤 | | `0x8110` | PDO 溢位 |
| `0x4210` | IGBT 過溫 | | `0x8120` | 匯流排硬體異常 |
| `0x5330` | 記憶體異常 | | `0x8130` | 匯流排逾時 |
| `0x5441` | 緊急停止 | | `0x8200` | PDO 物件存取錯誤 |
| `0x5442` | 正向硬體極限 | | `0x8400` | 速度異常 |
| `0x5443` | 負向硬體極限 | | `0x8600` | 脈波命令異常 |
| `0x5444` | 正向軟體極限 | | `0x8611` | 追隨誤差過大 |
| `0x5445` | 負向軟體極限 | | `0x9000` | 安全轉矩關斷 STO |
| `0x5500` | DSP 韌體異常 | | `0xFF01` | 類比電壓過高 |
| `0x6100` | 驅動器功能警告 | | `0xFF05` | 索引座標錯誤 |
| `0x6200` | 同步通訊異常 | | `0xFF07` | PR 濾波器錯誤 |

---

## 6. 建議的可用性判斷

```
設備數值可用  ⟺ conn == 2 && 對應的 st 旗標 == 0
可以驅動平台  ⟺ 設備數值可用
              && estopl == "OK" && estopr == "OK"
              && plctxt == "CONNECTED"
              && gsttxt != "ERRORSTOP"
              && estop == 0
```

兩個急停是**不同的東西**，都要看：

- `estopl` / `estopr` — 操作者手上的實體急停鈕，走 **UDP**
- `estop` — 平台自己的急停狀態，走 **TCP**

---

## 7. 有現成 SDK

不用自己刻 socket 與 PING/PONG。Unity 用 UPM 裝：

```
https://github.com/extrakyo-io/EdgeLink-Server.git?path=/SDK/Unity/Package
```

```csharp
var el = GetComponent<EdgeLinkManager>();
el.OnMessage += line => { /* 一行 KV */ };

if (el.CanSend)
    el.Send(("mt", "17"), ("act", "1"));
```

**數值一定要用 `EdgeLinkManager.Num()` 轉字串** —— 某些系統地區設定會把小數點輸出成逗號，
EdgeLink 會把那樣的值當成不合法而丟掉整包，而且**只在特定地區的機器上發生**，你的開發機永遠測不到。

另有 C# / Python / JavaScript / Arduino 版本，見 repo 根目錄 README。

---

## 8. 這份文件的權威來源

欄位定義來自這兩個檔案，**它們是唯一的真實來源**：

- `docs/RigBinary.mask.json` — 設備 UDP V1.1
- `docs/PlatformTcp.mask.json` — 平台 TCP V1

`Server.Tests/Unit/RigUdpV11MaskTests.cs` 會直接讀第一個檔案，逐欄比對規格 —— mask 改壞了測試就會紅。
要自己驗整條鏈路，跑 `docs/RigUdpV11ChainTest.py` 與 `docs/PlatformTcpChainTest.py`。
