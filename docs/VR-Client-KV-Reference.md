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

---

## 4. 平台（TCP，V1，雙向）

### 4.1 你送出：`mt:16` 移動命令

```
mt:16;mode:1;rqx:0;rqy:0;rqz:0;rqw:0;rhv:0;rqa:210;rqb:210;rqc:210;vel:50;acc:50;jrk:50
```

| 欄位 | 意義 |
|---|---|
| `mode` | 命令模式 |
| `rqx` `rqy` `rqz` `rqw` | 目標姿態四元數 |
| `rhv` | 目標 heave (mm) |
| `rqa` `rqb` `rqc` | 三軸目標位置 (mm) |
| `vel` `acc` `jrk` | 速度／加速度／加加速度 |

**用不到的那一組也要明確送 0。** `seq`、`ts`、`len` 由 EdgeLink 自動填，不要自己送。

### 4.2 你送出：`mt:17` 管理命令

```
mt:17;act:1
```

| 欄位 | 值 |
|---|---|
| `act` | `1` 急停 ON、`2` 急停 OFF、`3` 伺服切換、`4` Reset |

### 4.3 你收到：`mt:18` 命令執行結果

每一筆 `mt:16` / `mt:17` 都會回一筆。

| 欄位 | 意義 |
|---|---|
| `ackseq` `ackmt` | **回應的是哪一筆命令** — 用這兩個配對，不要靠順序 |
| `res` / `restxt` | `0` `ACCEPT` / `1` `REJECT` |
| `rc` / `rctxt` | 被拒原因，見下 |
| `axis` / `axistxt` | 是哪一支軸出問題：`NA` / `A` / `B` / `C` |
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
| `plc` / `plctxt` | `DISCONNECTED` / `CONNECTING` / `CONNECTED` / `RECONNECTING` |
| `gst` / `gsttxt` | `DISABLED` / `STANDBY` / `MOVING` / `HOMING` / `STOPPING` / `ERRORSTOP` |
| `ecerr` / `ecerrtxt` | EtherCAT 錯誤，`NO_ERROR` 以外都要處理（共 17 種） |
| `derra` `derrb` `derrc` + `derratxt` `derrbtxt` `derrctxt` | A/B/C 三軸驅動器異警，`NONE_OR_UNCODED` = 正常（共 40 種） |
| `pa` `pb` `pc` | 三軸**實際**位置 (mm) |
| `cpa` `cpb` `cpc` | 三軸**命令**位置 (mm) |
| `qx` `qy` `qz` `qw` | 由實際位置反解的姿態四元數 |
| `hv` | heave (mm) |
| `resid` | 殘差 (mm)，過大代表超出機構可達範圍 |

> 凡是有 `xxx` 與 `xxxtxt` 成對的欄位，`xxx` 是原始碼、`xxxtxt` 是查表後的文字。
> **判斷邏輯請用原始碼**（文字可能因表更新而變動），**顯示給人看用文字**。

---

## 5. 建議的可用性判斷

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

## 6. 有現成 SDK

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

## 7. 這份文件的權威來源

欄位定義來自這兩個檔案，**它們是唯一的真實來源**：

- `docs/RigBinary.mask.json` — 設備 UDP V1.1
- `docs/PlatformTcp.mask.json` — 平台 TCP V1

`Server.Tests/Unit/RigUdpV11MaskTests.cs` 會直接讀第一個檔案，逐欄比對規格 —— mask 改壞了測試就會紅。
要自己驗整條鏈路，跑 `docs/RigUdpV11ChainTest.py` 與 `docs/PlatformTcpChainTest.py`。
