"""模擬器 ↔ mask 交叉驗證。

模擬器(Python)與 mask(JSON,由 C# 解碼器執行)是對同一份 V1.1 規格的兩套獨立實作。
這裡把模擬器產生的真實位元組丟進 mask 的欄位定義,自己解一次,比對是否還原成原值。

自己解而不是呼叫 C# 解碼器,是刻意的:如果兩邊都照抄同一個錯誤的理解,C# 那側
的測試也會一起通過。這裡改用「直接照 PDF 的表逐欄硬解」,錯位就會露出來。
"""
#
# 不需要 EdgeLink,純離線:比對模擬器產生的位元組與 PDF 的欄位表。
#
#   python docs/RigUdpSpecCrossCheck.py

import io
import json
import struct
import sys

sys.path.insert(0, r'c:\Projects\EdgeLink-Server\docs')
import RigUdpSimulator as sim   # noqa: E402

FAIL = []


def check(name, ok, detail=''):
    print(('  PASS  ' if ok else '  FAIL  ') + name + ('  -- ' + detail if detail else ''))
    if not ok:
        FAIL.append(name)


class Args:
    """模擬器的 argparse namespace 替身。"""
    conn = 2
    fault_left = fault_right = False
    stale_left = stale_right = stale_enc = False
    gray_mismatch = False
    raw_left = raw_right = False
    estop_left = estop_right = pedal = False


def header(pkt):
    """規格 §4 + §5:magic/version/msgType/kind/seq/sendTimeMs/connState/unitCount。"""
    magic0, magic1, ver, msg, kind = struct.unpack_from('<BBBBB', pkt, 0)
    seq, = struct.unpack_from('<I', pkt, 5)
    ts, = struct.unpack_from('<Q', pkt, 9)
    conn, units = struct.unpack_from('<BB', pkt, 17)
    return dict(magic=bytes([magic0, magic1]), version=ver, msgType=msg, kind=kind,
                seq=seq, ts=ts, conn=conn, units=units)


print('=== 標頭 (§4) ===')
rig = sim.Rig(Args())
joy = rig.joy_packet(0.5, -0.25, -1.0, 1.0)
h = header(joy)
check('magic = "OK"', h['magic'] == b'OK', repr(h['magic']))
check('version = 1', h['version'] == 1, str(h['version']))
check('msgType = 1', h['msgType'] == 1, str(h['msgType']))
check('kind = 0 (推送)', h['kind'] == 0, str(h['kind']))
check('seq 首包 = 1', h['seq'] == 1, str(h['seq']))
check('unitCount = 2', h['units'] == 2, str(h['units']))
check('connState = 2 (Connected)', h['conn'] == 2, str(h['conn']))

print()
print('=== §5.1 msgType=1 搖桿 (37 bytes) ===')
check('封包長度 37', len(joy) == 37, str(len(joy)))
jlx, jly, jls = struct.unpack_from('<ffB', joy, 19)
jrx, jry, jrs = struct.unpack_from('<ffB', joy, 28)
check('左 x 還原', abs(jlx - 0.5) < 1e-6, str(jlx))
check('左 y 還原', abs(jly - (-0.25)) < 1e-6, str(jly))
check('右 x 還原', abs(jrx - (-1.0)) < 1e-6, str(jrx))
check('右 y 還原', abs(jry - 1.0) < 1e-6, str(jry))
check('狀態位元全 0', jls == 0 and jrs == 0, '%d/%d' % (jls, jrs))

a = Args()
a.fault_left = 0b01
a.stale_left = True
a.raw_left = True
faulty = sim.Rig(a).joy_packet(0, 0, 0, 0)
_, _, st = struct.unpack_from('<ffB', faulty, 19)
check('bit0 X冗餘故障', st & 0b1 == 1, bin(st))
check('bit2 Stale', st >> 2 & 1 == 1, bin(st))
check('bit3 Raw', st >> 3 & 1 == 1, bin(st))
check('bit7 保留恆 0', st >> 7 & 1 == 0, bin(st))

print()
print('=== §5.2 msgType=2 按鈕 / 急停 / 踏板 (23 bytes) — V1.1 變更 ===')
btn = sim.Rig(Args()).btn_packet(0.0)
check('封包長度 23', len(btn) == 23, str(len(btn)))
left, lst, right, rst = struct.unpack_from('<BBBB', btn, 19)
check('預設:左急停 bit3 = 1 (鬆開)', left >> 3 & 1 == 1, bin(left))
check('預設:右急停 bit4 = 1 (鬆開)', left >> 4 & 1 == 1, bin(left))
check('預設:踏板 bit5 = 0 (沒踩)', left >> 5 & 1 == 0, bin(left))
check('bit6..7 保留恆 0', left >> 6 == 0, bin(left))
check('右 slot 帶同一份急停/踏板', (left >> 3) == (right >> 3),
      '%s / %s' % (bin(left), bin(right)))

a = Args()
a.estop_left = True
a.pedal = True
btn2 = sim.Rig(a).btn_packet(0.0)
l2, _, r2, _ = struct.unpack_from('<BBBB', btn2, 19)
check('左急停按下 → bit3 = 0', l2 >> 3 & 1 == 0, bin(l2))
check('右急停仍鬆開 → bit4 = 1', l2 >> 4 & 1 == 1, bin(l2))
check('踏板踩下 → bit5 = 1', l2 >> 5 & 1 == 1, bin(l2))
check('右 slot 同步', l2 >> 3 == r2 >> 3, '%s / %s' % (bin(l2), bin(r2)))

print()
print('=== §5.3 msgType=3 編碼器 (31 bytes) ===')
enc = sim.Rig(Args()).enc_packet(128, 180.0, 64, 90.0)
check('封包長度 31', len(enc) == 31, str(len(enc)))
p1, s1, d1 = struct.unpack_from('<BBf', enc, 19)
p2, s2, d2 = struct.unpack_from('<BBf', enc, 25)
check('編碼器1 position 還原', p1 == 128, str(p1))
check('編碼器1 degrees 還原', abs(d1 - 180.0) < 1e-4, str(d1))
check('編碼器2 position 還原', p2 == 64, str(p2))
check('編碼器2 degrees 還原', abs(d2 - 90.0) < 1e-4, str(d2))

a = Args()
a.gray_mismatch = True
a.stale_enc = True
encf = sim.Rig(a).enc_packet(0, 0, 0, 0)
_, sf, _ = struct.unpack_from('<BBf', encf, 19)
check('bit0 GrayMismatch', sf & 1 == 1, bin(sf))
check('bit1 Stale', sf >> 1 & 1 == 1, bin(sf))

print()
print('=== seq:每個 msgType 一條獨立 counter (§4) ===')
r = sim.Rig(Args())
seqs = []
for _ in range(3):
    seqs.append(header(r.joy_packet(0, 0, 0, 0))['seq'])
    r.btn_packet(0.0)
    r.enc_packet(0, 0, 0, 0)
check('搖桿 seq 連續遞增', seqs == [1, 2, 3], str(seqs))
check('按鈕 counter 獨立', r.seq[2] == 3, str(r.seq[2]))
check('編碼器 counter 獨立', r.seq[3] == 3, str(r.seq[3]))

print()
print('=== mask 與模擬器的欄位/長度一致 ===')
d = json.load(io.open(r'c:\Projects\EdgeLink-Server\docs\RigBinary.mask.json', encoding='utf-8'))
b = d['masks'][0]['binary']
lens = {v['match']: v['length'] for v in b['variants']}
check('mask sync 含 version', b['sync'] == '4f4b01', b['sync'])
check('mask msgType1 長度 = 模擬器', lens[1] == len(joy), '%d/%d' % (lens[1], len(joy)))
check('mask msgType2 長度 = 模擬器', lens[2] == len(btn), '%d/%d' % (lens[2], len(btn)))
check('mask msgType3 長度 = 模擬器', lens[3] == len(enc), '%d/%d' % (lens[3], len(enc)))

print()
print('FAILED: ' + (', '.join(FAIL) if FAIL else '(none)'))
sys.exit(1 if FAIL else 0)
