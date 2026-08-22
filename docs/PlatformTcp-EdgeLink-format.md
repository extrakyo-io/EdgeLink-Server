# EdgeLink 平台 TCP 轉譯格式 — 欄位說明

EdgeLink 以 `PlatformTcp` mask 在「消防訓練平台 TCP 資料格式 V1」的二進位協定與 KV 文字之間雙向翻譯：

```
下游(KV 文字) ──> EdgeLink TCP Server 埠 ──編碼──> TCP Client 埠 ──二進位──> 平台 Unity:47802
下游(KV 文字) <── EdgeLink TCP Server 埠 <──解碼── TCP Client 埠 <──二進位── 平台 Unity:47802
```

同一個 TCP Client 埠、同一條 socket 收發，對應原文件 §1「收發共用同一條 TCP 連線」。

> 本文件描述的是 **EdgeLink 定義的 KV 欄位**。wire 上的位元組版面以原始協定文件為準；
> 兩者的對應關係寫在 [PlatformTcp.mask.json](PlatformTcp.mask.json)。

---

## 1. 格式

- **UTF-8 文字，每筆一行**（`\n` 結尾）
- 欄位間 `;`、鍵值間 `:`
- **四種行，靠 `mt` 分辨**：16 移動命令、17 管理命令（出站）；18 執行結果、19 平台狀態（入站）
- 收端把各行欄位**合併保留最新值**即可；四種行的欄位名互不衝突（除了共用表頭 `id`/`mt`/`seq`/`ts`）

## 2. 判讀順序（重要）

**先看 `plctxt` → 再看 `gsttxt` 與各錯誤欄位 → 最後才用數值。**

`plc` 不是 `CONNECTED` 時，位置與姿態數值是上一次成功輪詢留下的舊值。

`derr*` **等於 0 不保證該軸正常**——原始手冊有一批異警沒有 CiA-402 標準碼，那些情況同樣回報 `0000h`（KV 顯示 `NONE_OR_UNCODED`）。要判斷有沒有故障，必須搭配 `gsttxt` 是不是 `ERRORSTOP`。

`pa`/`qx..qw`/`hv`/`resid` 全部來自同一筆 Modbus 輪詢，不會出現「位置是新的、姿態是舊的」的組合。

---

## 3. 入站欄位（平台 → 下游）

### 共同表頭

| 欄位 | 型別 | 意義 |
|---|---|---|
| `id` | 字串 | 裝置台 ID（mask 內固定 `plat1`，可改） |
| `mt` | 16/17/18/19 | 訊息種類 |
| `seq` | u32 | 對方送出的序號，每個 `mt` 各一條獨立 counter；跳號=中間有丟包 |
| `ts` | u64 | 對方送出當下 Unix epoch UTC 毫秒 |

### `mt:18` — 命令執行結果

每一筆送出的命令都會收到一筆。

| 欄位 | 型別/範圍 | 意義 |
|---|---|---|
| `ackseq` | u32 | 回應的是哪一筆命令的 `seq` |
| `ackmt` | 16/17 | 回應的是哪一種命令（`seq` 各自獨立，所以必須標明） |
| `res` / `restxt` | 0/1 · ACCEPT/REJECT | 接受或拒絕 |
| `rc` / `rctxt` | 0–15 · 中文 | 拒絕原因，見 §5.1；接受時為 0 |
| `axis` / `axistxt` | −1/0/1/2 · NA/A/B/C | 出問題的軸；兩軸同時越限只報優先度最高的（A→B→C） |
| `test` | 0/1 | 1 = 對方在測試環境，命令被接受但**沒有真的輸出到控制器** |
| `rpa` `rpb` `rpc` | 浮點 mm | 對方 IK 解算出的三軸位置；`rc` 為 10/13/14 時填 0 |

> ⚠ `test` 的極性與 `mt:19` 的 `out` **相反**。`test:1` = 沒輸出；`out:1` = 有輸出。
> 原始文件在這兩處用了同一個 bit0 但語意相反，所以 EdgeLink 刻意拆成兩個不同的欄位名。

### `mt:19` — 平台狀態（持續推送）

| 欄位 | 型別/範圍 | 意義 |
|---|---|---|
| `ack` | u32 | 最後**採納**的移動命令 `seq`；0 = 這條連線還沒採納過任何移動命令 |
| `out` | 0/1 | 1 = 實際輸出命令到控制器；0 = 測試環境（見上方極性提醒） |
| `estop` | 0/1 | 1 = 軟體 E-Stop 生效中，所有移動命令會被拒絕（`rc:6`） |
| `plc` / `plctxt` | 0–3 · 見 §5.2 | 對方 Unity 到運動控制器的連線狀態 |
| `gst` / `gsttxt` | 0–5 · 見 §5.3 | 三軸群組狀態 |
| `ecerr` / `ecerrtxt` | u16 · 見 §5.4 | EtherCAT 主站錯誤 |
| `derra` `derrb` `derrc` | u16 | A/B/C 軸伺服異警**原始碼**（十進位；面板顯示的是十六進位） |
| `derratxt` `derrbtxt` `derrctxt` | 見 §5.5 | 異警分類 |
| `pa` `pb` `pc` | 浮點 mm | 控制器回報的**實際**三軸位置——畫推桿伸縮用這組 |
| `cpa` `cpb` `cpc` | 浮點 mm | 對方最後送出的**命令**位置；與 `pa` 的差就是追隨誤差 |
| `qx` `qy` `qz` `qw` | 浮點 | 由實際位置反解的旋轉四元數——畫平台傾斜用這組；無資料時為單位四元數 (0,0,0,1) |
| `hv` | 浮點 mm | 由實際位置反解的平台高度 |
| `resid` | 浮點 mm | 殘差；正常接近 0，過大代表目前三軸組合超出機構可達範圍 |

> 想在畫面上看到追隨過程，把 `cp*`（命令）與 `p*`（實際）兩組都畫出來。

---

## 4. 出站欄位（下游 → 平台）

下游只要送 KV 文字，EdgeLink 負責編成二進位。**`seq`、`ts`、封包長度由 EdgeLink 產生，不要送**——`seq` 每個 `mt` 各一條、從 1 起算，且在 EdgeLink 對平台的連線重建時自動歸 1。

### `mt:16` — 平台移動命令

| 欄位 | 必填 | 意義 |
|---|---|---|
| `mode` | ✔ | 0 = 給四元數（對方解 IK）；1 = 直接給三軸行程 |
| `rqx` `rqy` `rqz` `rqw` | ✔ | 目標旋轉四元數；`mode:1` 時送 0 |
| `rhv` | ✔ | 目標平台高度 mm；`mode:1` 時送 0（四元數不帶高度，高度是獨立自由度） |
| `rqa` `rqb` `rqc` | ✔ | 三軸絕對位置 mm；`mode:0` 時送 0 |
| `vel` `acc` `jrk` | — | 馬達 S-Curve 速度／加減速／Jerk；**省略或 0 = 沿用對方預設**（134 / 536 / 2144） |

用不到的那一組欄位**要由送端明確送 0**，EdgeLink 不會替你補。缺任何一個必填欄位，整包丟棄並記 log。

這是刻意從嚴：`mode:1` 漏送軸位置不是 no-op，`rqa/rqb/rqc = 0` 是一筆合法的絕對位置命令，若 0 mm 落在行程界限內，平台會真的衝到底端。`mode:0` 漏送四元數則變成 (0,0,0,0)，那不是任何旋轉。

### `mt:17` — 平台管理命令

| 欄位 | 必填 | 意義 |
|---|---|---|
| `act` | ✔ | 1 = EStopOn、2 = EStopOff、3 = ServoToggle、4 = Reset |

- `EStopOn` 送出後**持續有效**，必須明確送 `EStopOff` 才解除。
- `ServoToggle` 是切換：目前 Servo Off 就開、Servo On 就關。**移動平台前記得先送一次把馬達 Enable。**

---

## 5. 對照表

KV 裡的 `*txt` 欄位是短代碼，完整說明在這裡。原始數值欄位一律保留，要對驅動器面板時用得到。

### 5.1 `rctxt` — 拒絕原因（`rc` 0–15）

| `rc` | `rctxt` | `axis` |
|---|---|---|
| 0 | 無 | — |
| 1 | 超出行程界限(mm) | 該軸 |
| 2 | 超出行程界限(場景單位) | 該軸 |
| 3 | 水平偏移超過球頭範圍 | 該軸 |
| 4 | 尚未讀到PLC狀態 | −1 |
| 5 | 控制器不允許Move | −1 |
| 6 | E-Stop生效中 | −1 |
| 7 | PLC錯誤(EtherCAT/驅動器/ErrorStop) | 驅動器錯誤時為該軸，其餘 −1 |
| 8 | 保留 | — |
| 9 | WatchDog逾時（預設停用） | −1 |
| 10 | seq倒退或重複 | −1 |
| 11 | mode1已關閉 | −1 |
| 12 | IK還原不出姿態 | −1 |
| 13 | 數值不合法(NaN/Inf/超範圍) | −1 |
| 14 | velocity/accel/jerk超上限 | −1 |
| 15 | 輸入來源未切External | −1 |

### 5.2 `plctxt`

`DISCONNECTED`(0) · `CONNECTING`(1) · `CONNECTED`(2) · `RECONNECTING`(3)

### 5.3 `gsttxt`

`DISABLED`(0 未啟用) · `STANDBY`(1 待命) · `MOVING`(2 移動中) · `HOMING`(3 回原點) · `STOPPING`(4 停止中) · `ERRORSTOP`(5 錯誤停止)

### 5.4 `ecerrtxt` — EtherCAT 主站錯誤

代碼即 PLC 列舉名，直接對得上控制器端的顯示。

| `ecerr` | `ecerrtxt` | 意義 |
|---|---|---|
| 0 | NO_ERROR | 無錯誤 |
| 1 | NO_COMM | 無 EtherCAT 通訊 |
| 2 | WRONG_WORKING_COUNTER | Working Counter 不正確，PDO 回應數量或狀態異常 |
| 3 | DC_TIME_ZERO | Distributed Clock 時間為 0 |
| 4 | OPEN_FIRSTADAPTER_FAILED | 第一張網路介面開啟失敗 |
| 5 | OPEN_SECONDADAPTER_FAILED | 第二張網路介面開啟失敗 |
| 6 | ADAPTER_MISMATCH | 網路介面不相符 |
| 7 | NO_SLAVES_FOUND | 找不到 Slave |
| 8 | VENDOR_ID_WRONG | Slave Vendor ID 不符 |
| 9 | PRODUCT_ID_WRONG | Slave Product ID 不符 |
| 10 | NUMBER_DEVICE_MISMATCH | 實際 Slave 數量與設定不符 |
| 11 | SDO_WRITE_ERROR | SDO 寫入失敗 |
| 12 | SDO_TIMEOUT | SDO 通訊逾時 |
| 13 | EMERGENCY_RECEIVED | 收到 EtherCAT Emergency |
| 14 | IDN_WRITE_ERROR | IDN 寫入失敗 |
| 15 | IDN_TIMEOUT | IDN 通訊逾時 |
| 16 | WATCHDDOG_ERROR | Watchdog 錯誤（拼法沿用 PLC 列舉原文，比對以數值 16 為準） |

### 5.5 `derr*txt` — 伺服驅動器異警

**這張表是多對一，不可反查。** 拿到代碼只能判斷「哪一類問題」，判不出是哪一個 AL 碼——要確定實際異警，看驅動器面板。

| `derr*`（hex） | `derr*txt` | 對應 AL 碼 / 意義 |
|---|---|---|
| 0000 | NONE_OR_UNCODED | 無異警，**或**有異警但無標準碼（防堵轉保護、馬達參數／型式錯誤、速度過高、位置回授異常、電容充電異常、參數程序異常等）。要靠 `gsttxt` 分辨 |
| 0207–0249 | PR_PARAM | AL207/209/211/213/215/217/219/231/235/237/245/249 — PR 命令參數錯誤（來源參數、格式、唯讀、Servo On 時不可寫入、定位超時等）；末兩位即 AL 碼後兩碼 |
| 2310 | OVERCURRENT | AL001、AL083 — 過電流／輸出電流過大 |
| 3110 | OVERVOLTAGE | AL002、AL086 — 過電壓／回生電阻過負載 |
| 3120 | UNDERVOLTAGE | AL003 — 低電壓 |
| 3130 | MAIN_POWER | AL022 — 主迴路電源異常 |
| 3210 | REGEN | AL005、AL010、AL085 — 回生錯誤／回生電壓異常／回生設定異常 |
| 3230 | OVERLOAD | AL006、AL02C — 過負載（馬達／驅動器） |
| 3231 | OVERLOAD_WARN | AL023 — 預先過負載警告 |
| 3300 | MOTOR_WIRING | AL031、ALC31 — 馬達動力線錯線／斷線 |
| 4210 | IGBT_TEMP | AL016 — IGBT 溫度異常 |
| 5330 | MEMORY | AL017 — 記憶體異常 |
| 5441 | ESTOP | AL013 — 緊急停止 |
| 5442 | LIMIT_POS_HW | AL015 — 正向極限異常（硬體限位） |
| 5443 | LIMIT_NEG_HW | AL014 — 反向極限異常（硬體限位） |
| 5444 | LIMIT_POS_SW | AL283 — 軟體正向極限 |
| 5445 | LIMIT_NEG_SW | AL285 — 軟體反向極限 |
| 5500 | DSP_FIRMWARE | AL099、AL09C — DSP 韌體錯誤／參數重置失敗 |
| 6100 | DRIVE_FUNC_WARN | AL044、AL089、AL521 — 功能使用率警告／電流感測遭干擾／撓性補償參數異常 |
| 6200 | SYNC_COMM | AL301–305、AL35F、AL380、AL3CF、AL3E1–3E3、AL3F1 — 通訊同步類與緊急停止（減速中） |
| 6310 | OBJ_DICT_INIT | AL201 — 物件字典資料初始錯誤 |
| 6320 | GEAR_RATIO | AL045 — 電子齒輪比設定錯誤 |
| 7036 | OA_OB_OUTPUT | AL048 — OA 與 OB 輸出異常（見下方註記） |
| 7121 | MOTOR_COLLISION | AL030 — 馬達碰撞錯誤 |
| 7122 | MOTOR_MISMATCH | AL004 — 馬達匹配異常 |
| 7305 | ENCODER_ABS | AL011、AL024–02B、AL032–036、AL060–07F、AL08A–08C、AL0A6、AL289 — 編碼器與絕對位置類（CN2 通訊失敗、編碼器內部／溫度／振動／圈數／記憶體錯誤、絕對位置遺失或溢位、自動增益調整異常、位置計數器溢位） |
| 7306 | OA_OB_OUTPUT | AL018 — OA 與 OB 輸出異常（見下方註記） |
| 7520 | SERIAL_TIMEOUT | AL020 — 串列通訊逾時 |
| 8100 | BUS_DATA | AL186 — 總線資料傳輸錯誤 |
| 8110 | PDO_OVERFLOW | AL111–113 — SDO／PDO 接收溢位、TxPDO 傳送失敗 |
| 8120 | BUS_HARDWARE | AL185 — 總線硬體異常 |
| 8130 | BUS_TIMEOUT | AL170、AL180 — 總線通訊逾時 |
| 8200 | PDO_OBJ_ACCESS | AL121–132 — PDO 物件字典存取錯誤（Index／Sub-index／長度／範圍／唯讀／EEPROM CRC） |
| 8400 | SPEED_ERROR | AL007 — 速度控制誤差過大 |
| 8600 | PULSE_CMD | AL008 — 異常脈波控制命令 |
| 8611 | FOLLOWING_ERROR | AL009 — 位置控制誤差過大（跟隨誤差） |
| 9000 | STO | AL500–503 — STO 安全功能：STO 啟動、SF1／SF2 無訊號、自我診斷錯誤 |
| FF01 | ANALOG_VOLT_HIGH | AL042 — 類比速度指令電壓過高 |
| FF05 | INDEX_COORD | AL400 — 分度座標設定錯誤 |
| FF07 | PR_FILTER | AL404 — PR 特殊濾波器設定過大 |
| 其他 | UNKNOWN | 表上沒有的碼；另有 AL555（系統故障）在手冊中未列 16-bit 碼 |

> `7036` 與 `7306` 異警名稱相同、碼卻不同，看起來像手冊的數字轉置誤植，但兩者都照原文收錄。
> 實機出現其中一個時，以驅動器面板顯示的 AL 碼為準。

---

## 6. 範例

```
# 入站:平台狀態(正常)
id:plat1;mt:19;seq:7;ts:1755870000000;ack:42;out:1;estop:0;plc:2;plctxt:CONNECTED;gst:2;gsttxt:MOVING;ecerr:0;ecerrtxt:NO_ERROR;derra:0;derratxt:NONE_OR_UNCODED;derrb:0;derrbtxt:NONE_OR_UNCODED;derrc:0;derrctxt:NONE_OR_UNCODED;pa:120.5;pb:118.25;pc:119;cpa:121;cpb:118;cpc:119.5;qx:0;qy:0;qz:0;qw:1;hv:350.75;resid:0.02

# 入站:平台狀態(B 軸 PR 參數異警、群組錯誤停止)
...;gst:5;gsttxt:ERRORSTOP;ecerr:2;ecerrtxt:WRONG_WORKING_COUNTER;derrb:561;derrbtxt:PR_PARAM;...

# 入站:命令被拒(B 軸越限)
id:plat1;mt:18;seq:3;ts:1755870000000;ackseq:12;ackmt:16;res:1;restxt:REJECT;rc:1;rctxt:超出行程界限(mm);axis:1;axistxt:B;test:0;rpa:0;rpb:0;rpc:0

# 出站:三軸行程命令
mt:16;mode:1;rqx:0;rqy:0;rqz:0;rqw:0;rhv:0;rqa:120.5;rqb:118.25;rqc:119

# 出站:四元數命令(附帶覆寫馬達速度)
mt:16;mode:0;rqx:0;rqy:0.087;rqz:0;rqw:0.996;rhv:350;rqa:0;rqb:0;rqc:0;vel:100

# 出站:Servo ON/OFF 切換
mt:17;act:3
```

---

## 7. 本機模擬（沒有實機也能跑整條鏈路）

兩支腳本把上下游都補齊，中間就是真正的 EdgeLink：

```
PlatformTcpConsole.py          EdgeLink                    PlatformTcpSimulator.py
   (扮演 VR / 下游)                                              (扮演平台 Unity)

   KV 文字  ──連入──▶  TCP Server 47900
                            │ SourceProtocolId
                            ▼
                       TCP Client ──編碼──▶ 連出 47802 ──二進位──▶ 收命令 → 回 mt:18
                            ▲                                        持續推 mt:19
   KV 文字  ◀──廣播───  解碼 ◀──────────────二進位────────────────────┘
```

啟動順序（模擬器要先起，EdgeLink 的 TCP Client 才連得上；晚起也可以，會自動重連）：

```bash
python docs/PlatformTcpSimulator.py --hz 100 --verbose
python docs/PlatformTcpConsole.py --port 47900 --script bringup
```

### 模擬器（`PlatformTcpSimulator.py`）

照協定文件實作了會影響對接的那些規則，不是只會回 ACK 的假物件：

- **只收一個 client**，新連線覆蓋舊的；每條新連線的 `seq` 期望值重置回 1
- **`seq` 嚴格遞增**，倒退或重複 → `rc:10`
- **狀態機**：E-Stop（`rc:6`）、馬達未 Enable（`rc:5`）、行程界限（`rc:1` + `detailAxis`）、
  零四元數（`rc:12`）、NaN/Inf（`rc:13`）、速度剖線超上限（`rc:14`）、`--no-mode1`（`rc:11`）
- **運動模型**：實際位置以命令速度朝命令位置逼近，所以 `pa` 會追 `cpa`，`gst` 在
  `MOVING` / `STANDBY` 之間切換
- **錯誤注入**：`--drive-err a=0x8611 b=0x0231`、`--ethercat-err 2` 用來驗查表與判讀順序

常用參數：`--hz`、`--home`、`--limit-lo/--limit-hi`、`--test-mode`（接受命令但不真的輸出）。

### 下游（`PlatformTcpConsole.py`）

只講 KV，二進位那段完全交給 EdgeLink。`--script bringup` 會跑一次上線驗證流程——
**先急停再下移動命令**，平台一步都不會動，但收到 `rc:6` 就證明整條編碼鏈路是通的。
`--watch N` 只看狀態，不帶參數則是互動模式（直接打 KV 送出、`s` 看目前狀態）。

它也示範了下游必須做的事：回覆 `EDGELINK_PING` 的 `EDGELINK_PONG`。

---

## 8. EdgeLink 埠設定

| | TCP Server 埠（下游接這） | TCP Client 埠（連平台 Unity） |
|---|---|---|
| `targetIp` / `remotePort` | — | 平台 Unity 主機 / `47802` |
| `maskType` | `OriginalData` | `PlatformTcp` ← 出站編碼 |
| `responseMaskType` | — | `PlatformTcp` ← 入站解碼 |
| `requestMode` | — | **`concurrent`** |
| `sourceProtocolId` | — | 指向左邊那個 TCP Server 埠 |

`requestMode` 必須是 `concurrent`：`serial` 模式送完會等回應信號最多 5 秒才送下一筆，而 broadcast routeMode 從不設那個信號，命令會被限流成每 5 秒一筆。

### 注意事項

- **下游只讀不寫**：任何接在該 TCP Server 埠上的程式送出的資料，都會被路由到 `sourceProtocolId` 相符的 TCP Client 埠，也就是進入編碼器並送往平台。這條鏈路不要跟其他會回寫的埠共用同一個 `sourceProtocolId`。
- **下游要回 PONG**：TCP Server 埠會送 `EDGELINK_PING:`，連續 3 次沒回 `EDGELINK_PONG:` 就會被斷線。用 EdgeLink SDK 已內建。
- **Unity 只接受一個 client**：EdgeLink 佔用該名額後，不要再有第二個程式直連 47802，否則兩邊會互相踢掉並各自無限重連。
- **一個 TCP Client 埠只接一個來源埠**：`seq` 在編碼當下配號、在寫入時才上線。若有兩個不同的 TCP Server 埠都把 `sourceProtocolId` 指向同一個 TCP Client 埠，兩條路由執行緒會並行配號與送出，線上的 `seq` 順序可能顛倒 —— 對端會用 `rc:10`（seq 倒退或重複）拒絕，症狀是「命令有時候沒反應」。單一來源埠時不會發生（訊息在來源埠是單一消費者依序處理）。
- **改 Mask 要重連**：收包模式在連線建立當下判斷一次，連線中改 Mask 不生效。
- **嫌 `mt:19` 每包太長**：`*txt` 欄位可以直接從 mask 刪掉，原始數值欄位不受影響。
