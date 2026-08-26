"""實跑設備 UDP 鏈路:模擬器 → EdgeLink(RigBinary V1.1) → 下游 KV。

不用常駐模擬器,自己送特定的封包再收 EdgeLink 轉出來的 KV,逐項比對。
這樣才驗得到「某個位元進去 → 某個欄位出來」的對應,而不是只看到有資料在流。
"""
#
# 與 RigUdpTestAll.py 的差別:那支要獨佔埠、會自己開關模擬器;這支用獨一無二的
# seq 標記自己送的封包,所以**可以在常駐模擬器仍在跑的時候直接執行**。
#
#   python docs/RigUdpV11ChainTest.py

import socket
import struct
import sys
import time

sys.path.insert(0, r'c:\Projects\EdgeLink-Server\docs')
import RigUdpSimulator as sim   # noqa: E402

EDGELINK = ('127.0.0.1', 47810)     # EdgeLink 的 RigBinary UDP 埠
DOWNSTREAM = 47811                  # EdgeLink 轉發出來的 KV

FAIL = []


def check(name, ok, detail=''):
    print(('  PASS  ' if ok else '  FAIL  ') + name + ('  -- ' + detail if detail else ''))
    if not ok:
        FAIL.append(name)


class Args:
    conn = 2
    fault_left = fault_right = False
    stale_left = stale_right = stale_enc = False
    gray_mismatch = False
    raw_left = raw_right = False
    estop_left = estop_right = pedal = False


MARK = 0x7F000000          # 現場模擬器不可能跑到這個 seq
_mark = [MARK]


def marked(packet):
    """把封包的 seq 換成獨一無二的值,好在共用的下游埠上認出自己送的那一包。

    你的 RigUdpSimulator 正掛著跑,下游埠上同時有它的流量 —— 不做標記的話
    收到的可能是它的封包,測試就會用錯誤的理由通過或失敗。
    """
    _mark[0] += 1
    b = bytearray(packet)
    struct.pack_into('<I', b, 5, _mark[0])
    return bytes(b), str(_mark[0])


def parse(line):
    kv = {}
    for f in line.split(';'):
        k, sep, v = f.partition(':')
        if sep:
            kv[k.strip()] = v.strip()
    return kv


def roundtrip(packet, want_key, timeout=3.0):
    """送一包進 EdgeLink,收回**我自己那一包**轉出來的 KV(依 seq 認)。"""
    pkt, seq = marked(packet)
    rx.settimeout(0.4)
    deadline = time.time() + timeout
    tx.sendto(pkt, EDGELINK)
    while time.time() < deadline:
        try:
            data, _ = rx.recvfrom(65536)
        except socket.timeout:
            continue
        for line in data.decode('utf-8', 'replace').splitlines():
            line = line.strip()
            if not line:
                continue
            kv = parse(line)
            if kv.get('seq') == seq and want_key in kv:
                return kv
    return None


rx = socket.socket(socket.AF_INET, socket.SOCK_DGRAM)
rx.setsockopt(socket.SOL_SOCKET, socket.SO_REUSEADDR, 1)
try:
    rx.bind(('0.0.0.0', DOWNSTREAM))
except OSError as ex:
    # 最常見的原因:Unity 正開著監看面板 / RigTelemetry,它綁著同一個埠。
    # 讓訊息直接說出來,不要丟一個看不懂的 WinError。
    print('無法綁定 UDP %d —— %s' % (DOWNSTREAM, ex))
    print('這個埠被別的程式佔用了。最常見的是 Unity 正在 Play(RigTelemetry 綁著它),')
    print('停掉 Play 再跑一次即可。')
    sys.exit(2)
tx = socket.socket(socket.AF_INET, socket.SOCK_DGRAM)

print('=== 設備 UDP → EdgeLink(%s:%d) → 下游 KV(:%d) ===' % (EDGELINK + (DOWNSTREAM,)))
print()

# ── 搖桿 ────────────────────────────────────────────────────────────────────
print('--- msgType=1 搖桿 ---')
kv = roundtrip(sim.Rig(Args()).joy_packet(0.5, -0.25, -1.0, 1.0), 'jlx')
if kv is None:
    check('搖桿封包有轉發出來', False, '3 秒內沒收到 —— EdgeLink 或埠設定沒起來')
else:
    check('搖桿封包有轉發出來', True)
    check('左 x', kv.get('jlx') == '0.5', kv.get('jlx'))
    check('左 y', kv.get('jly') == '-0.25', kv.get('jly'))
    check('右 x', kv.get('jrx') == '-1', kv.get('jrx'))
    check('右 y', kv.get('jry') == '1', kv.get('jry'))
    check('connState', kv.get('conn') == '2', kv.get('conn'))
    check('unitCount(V1.1 新解)', kv.get('units') == '2', kv.get('units'))

# ── 按鈕 / 急停 / 踏板 ──────────────────────────────────────────────────────
print()
print('--- msgType=2 按鈕 / 急停 / 踏板(V1.1) ---')
kv = roundtrip(sim.Rig(Args()).btn_packet(0.0), 'bl1')
if kv is None:
    check('按鈕封包有轉發出來', False, '3 秒內沒收到')
else:
    check('按鈕封包有轉發出來', True)
    check('預設兩個急停都是 OK', kv.get('estopl') == 'OK' and kv.get('estopr') == 'OK',
          '%s / %s' % (kv.get('estopl'), kv.get('estopr')))
    check('預設踏板 0', kv.get('pedal') == '0', kv.get('pedal'))

a = Args()
a.estop_left = True
kv = roundtrip(sim.Rig(a).btn_packet(0.0), 'bl1')
if kv:
    check('左急停按下 → estopl=ESTOP', kv.get('estopl') == 'ESTOP', kv.get('estopl'))
    check('右急停不受影響 → OK', kv.get('estopr') == 'OK', kv.get('estopr'))
    check('右 slot 同步', kv.get('estopl2') == 'ESTOP', kv.get('estopl2'))

a = Args()
a.estop_right = True
a.pedal = True
kv = roundtrip(sim.Rig(a).btn_packet(0.0), 'bl1')
if kv:
    check('右急停按下 → estopr=ESTOP', kv.get('estopr') == 'ESTOP', kv.get('estopr'))
    check('踏板踩下 → pedal=1', kv.get('pedal') == '1', kv.get('pedal'))

# ── 編碼器 ──────────────────────────────────────────────────────────────────
print()
print('--- msgType=3 編碼器 ---')
kv = roundtrip(sim.Rig(Args()).enc_packet(128, 180.0, 64, 90.0), 'e1p')
if kv is None:
    check('編碼器封包有轉發出來', False, '3 秒內沒收到')
else:
    check('編碼器封包有轉發出來', True)
    check('編碼器1 position', kv.get('e1p') == '128', kv.get('e1p'))
    check('編碼器1 degrees', kv.get('e1deg') == '180', kv.get('e1deg'))
    check('編碼器2 degrees', kv.get('e2deg') == '90', kv.get('e2deg'))

# ── 版本驗證:V2 的封包必須被丟掉 ────────────────────────────────────────────
print()
print('--- 版本驗證(規格 §3) ---')
pkt = bytearray(sim.Rig(Args()).joy_packet(0.1, 0.2, 0.3, 0.4))
pkt[2] = 2                                        # version = 2
got = roundtrip(bytes(pkt), 'jlx', timeout=1.5)
check('version=2 的封包不會被轉發', got is None,
      '竟然轉出來了:%s' % (got or {}).get('jlx'))

# 確認鏈路仍然活著(不是因為整條掛了才收不到)
kv = roundtrip(sim.Rig(Args()).joy_packet(0.75, 0, 0, 0), 'jlx')
check('鏈路在丟包之後仍正常', kv is not None and kv.get('jlx') == '0.75',
      (kv or {}).get('jlx'))

rx.close()
tx.close()
print()
print('FAILED: ' + (', '.join(FAIL) if FAIL else '(none)'))
sys.exit(1 if FAIL else 0)
