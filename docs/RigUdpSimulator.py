# RigUdpSimulator — 操作者輸入側的虛擬設備(UDP,無 GUI)
#
# 扮演現場那支送端程式:把搖桿 / 按鈕 / 編碼器組成 OK 二進位封包,用 UDP 打給 EdgeLink,
# 由 RigBinary mask 解成 KV 再轉發給下游。這是與平台 TCP 那條independent的另一條鏈路:
#
#   本模擬器 ──UDP 二進位──▶ EdgeLink UDP 埠(RigBinary) ──UDP KV 文字──▶ 下游
#
# 與 FireRigSimulator.py 的差別:那支是 tkinter GUI、要用滑鼠拉;這支是 headless,
# 可以掛著跑、也能進自動化測試。封包版面兩者相同(對照 RigBinary.mask.json)。
#
# 用法:
#   python RigUdpSimulator.py --target 127.0.0.1:47810            # V1.1 預設速率
#   python RigUdpSimulator.py --target 127.0.0.1:47810 --hz 10 --verbose
#   python RigUdpSimulator.py --stale-left --fault-right          # 注入故障旗標
#   python RigUdpSimulator.py --estop-left --pedal                # V1.1:急停 / 踏板
#   python RigUdpSimulator.py --recv 47811                        # 改當下游,收 KV 印出來
#
# 速率依 V1.1 §0/§2:搖桿與按鈕 100 Hz(10ms)、編碼器 50 Hz(20ms)。--hz 會等比覆寫。
#
# 需求:Python 3(只用標準函式庫)。

import argparse
import math
import socket
import struct
import sys
import time

MAGIC = b'OK'
VERSION = 1

# 版面對照 RigBinary.mask.json(與 FireRigSimulator.py 相同)
FMT_JOY = '<2sBBBIQBBffBffB'   # 37 bytes:搖桿
FMT_BTN = '<2sBBBIQBBBBBB'     # 23 bytes:按鈕
FMT_ENC = '<2sBBBIQBBBBfBBf'   # 31 bytes:編碼器

CONN_DISCONNECTED, CONN_CONNECTING, CONN_CONNECTED, CONN_RECONNECTING, CONN_NOTPRESENT = 0, 1, 2, 3, 255


def now_ms():
    return int(time.time() * 1000)


def joy_flags(fault_mask, stale, raw):
    """搖桿狀態位元組:bit0-1 = 冗餘故障(X/Y),bit2 = stale,bit3 = 未濾波。"""
    return (fault_mask & 0b11) | (0b100 if stale else 0) | (0b1000 if raw else 0)


def button_byte(btn_bits, estop_left_pressed, estop_right_pressed, pedal_down):
    """V1.1 §5.2 的按鈕位元組。

    bit0..2 = BTN1..3(1 = 按下)
    bit3    = 左搖桿急停、bit4 = 右搖桿急停 —— **反相:1 = 鬆開,0 = 按下**
    bit5    = 踏板(1 = 踩下)
    bit6..7 = 保留,恆 0

    急停反相這件事很容易寫錯,而寫錯的方向剛好是最危險的那邊:V1.1 之前
    這幾個 bit 一律送 0,用 V1.1 的讀法就是「兩個急停都被按下」。
    """
    v = btn_bits & 0b111
    if not estop_left_pressed:
        v |= 1 << 3                     # 鬆開才置 1
    if not estop_right_pressed:
        v |= 1 << 4
    if pedal_down:
        v |= 1 << 5
    return v


def enc_flags(gray_mismatch, stale):
    """編碼器狀態位元組:bit0 = GrayMismatch,bit1 = stale。"""
    return (0b1 if gray_mismatch else 0) | (0b10 if stale else 0)


class Rig:
    """搖桿掃描 / 按鈕輪播 / 編碼器旋轉 —— 讓下游看得到會動的數值。"""

    def __init__(self, args):
        self.args = args
        self.t0 = time.time()
        self.seq = {1: 0, 2: 0, 3: 0}

    def next_seq(self, msg_type):
        self.seq[msg_type] += 1
        return self.seq[msg_type]

    def sample(self):
        t = time.time() - self.t0
        a = self.args

        # 左桿畫圓、右桿慢速掃 —— 故障軸依協定由送端強制歸 0
        jlx, jly = math.sin(t * 0.8), math.cos(t * 0.8)
        jrx, jry = math.sin(t * 0.3) * 0.6, 0.0
        if a.fault_left:
            jlx = jly = 0.0
        if a.fault_right:
            jrx = jry = 0.0
        if a.stale_left:
            jlx = jly = 0.0
        if a.stale_right:
            jrx = jry = 0.0

        # 編碼器:0-255 絕對位置 + 對應角度
        e1p = int((t * 12) % 256)
        e2p = int((t * 5) % 256)
        return jlx, jly, jrx, jry, e1p, e1p * 360.0 / 256.0, e2p, e2p * 360.0 / 256.0

    def joy_packet(self, jlx, jly, jrx, jry):
        a = self.args
        return struct.pack(
            FMT_JOY, MAGIC, VERSION, 1, 0, self.next_seq(1), now_ms(), a.conn, 2,
            jlx, jly, joy_flags(a.fault_left, a.stale_left, a.raw_left),
            jrx, jry, joy_flags(a.fault_right, a.stale_right, a.raw_right))

    def btn_packet(self, t):
        a = self.args
        # 左三顆輪流亮、右三顆反方向 —— 一眼就看得出有沒有在動
        left = button_byte(1 << (int(t) % 3), a.estop_left, a.estop_right, a.pedal)
        right = button_byte(1 << (2 - int(t) % 3), a.estop_left, a.estop_right, a.pedal)
        return struct.pack(
            FMT_BTN, MAGIC, VERSION, 2, 0, self.next_seq(2), now_ms(), a.conn, 2,
            left, 0b100 if a.stale_left else 0, right, 0b100 if a.stale_right else 0)

    def enc_packet(self, e1p, e1deg, e2p, e2deg):
        a = self.args
        return struct.pack(
            FMT_ENC, MAGIC, VERSION, 3, 0, self.next_seq(3), now_ms(), a.conn, 2,
            e1p, enc_flags(a.gray_mismatch, a.stale_enc), e1deg,
            e2p, enc_flags(False, a.stale_enc), e2deg)


def send_loop(args):
    host, _, port = args.target.rpartition(':')
    dest = (host, int(port))
    sock = socket.socket(socket.AF_INET, socket.SOCK_DGRAM)
    rig = Rig(args)
    period = 1.0 / args.hz

    # V1.1 §0:「搖桿輪詢改為 100Hz/10ms」;§2 表列編碼器為 50Hz/20ms。
    # 全部擠在同一個速率會讓編碼器多送一倍,對下游的 seq 連續性檢查也不真實。
    joy_hz = args.hz
    enc_hz = args.hz / 2.0
    joy_period = 1.0 / joy_hz
    enc_period = 1.0 / enc_hz

    print(f"[設備] 送往 {dest[0]}:{dest[1]} — 搖桿/按鈕 {joy_hz:g} Hz、編碼器 {enc_hz:g} Hz(V1.1)",
          flush=True)
    flags = [n for n, v in (('左桿故障', args.fault_left), ('右桿故障', args.fault_right),
                            ('左桿stale', args.stale_left), ('右桿stale', args.stale_right),
                            ('編碼器stale', args.stale_enc), ('GrayMismatch', args.gray_mismatch),
                            ('未濾波L', args.raw_left), ('未濾波R', args.raw_right)) if v]
    if flags:
        print(f"[設備] 已注入:{'、'.join(flags)}", flush=True)
    if args.conn != CONN_CONNECTED:
        print(f"[設備] conn = {args.conn}(非 2 = 資料無效)", flush=True)

    estop = [n for n, v in (('左急停', args.estop_left), ('右急停', args.estop_right),
                            ('踏板', args.pedal)) if v]
    if estop:
        print(f"[設備] V1.1:{'、'.join(estop)} 作動中", flush=True)

    now = time.time()
    next_joy, next_enc = now, now
    try:
        while True:
            now = time.time()
            t = now - rig.t0
            jlx, jly, jrx, jry, e1p, e1deg, e2p, e2deg = rig.sample()

            if now >= next_joy:
                sock.sendto(rig.joy_packet(jlx, jly, jrx, jry), dest)
                sock.sendto(rig.btn_packet(t), dest)
                next_joy += joy_period
                if args.verbose:
                    print(f"  → seq={rig.seq[1]} jl=({jlx:+.2f},{jly:+.2f}) "
                          f"jr=({jrx:+.2f},{jry:+.2f})", flush=True)

            if now >= next_enc:
                sock.sendto(rig.enc_packet(e1p, e1deg, e2p, e2deg), dest)
                next_enc += enc_period
                if args.verbose:
                    print(f"  → seq={rig.seq[3]} e1={e1deg:.1f}° e2={e2deg:.1f}°", flush=True)

            # 落後太多就重新對齊,不要追著補送(補送會在下游看到一陣爆量)
            now = time.time()
            if next_joy < now - joy_period:
                next_joy = now
            if next_enc < now - enc_period:
                next_enc = now

            time.sleep(max(0.0, min(next_joy, next_enc) - time.time()))
    except KeyboardInterrupt:
        print(f"\n[設備] 結束(搖桿 {rig.seq[1]} / 按鈕 {rig.seq[2]} / 編碼器 {rig.seq[3]})",
              flush=True)


def recv_loop(args):
    """當下游用:收 EdgeLink 轉出來的 KV 文字。"""
    sock = socket.socket(socket.AF_INET, socket.SOCK_DGRAM)
    sock.setsockopt(socket.SOL_SOCKET, socket.SO_REUSEADDR, 1)
    sock.bind(('0.0.0.0', args.recv))
    sock.settimeout(1.0)

    print(f"[下游] 在 UDP {args.recv} 等 EdgeLink 轉出來的 KV", flush=True)
    latest, counts, last_print = {}, {}, 0.0
    try:
        while True:
            try:
                data, _ = sock.recvfrom(65536)
            except socket.timeout:
                continue
            for line in data.decode('utf-8', 'replace').splitlines():
                line = line.strip()
                if not line:
                    continue
                kv = {}
                for f in line.split(';'):
                    k, sep, v = f.partition(':')
                    if sep:
                        kv[k.strip()] = v.strip()
                latest.update(kv)
                kind = ('搖桿' if 'jlx' in kv else '按鈕' if 'bl1' in kv else
                        '編碼器' if 'e1p' in kv else '?')
                counts[kind] = counts.get(kind, 0) + 1

            now = time.time()
            if now - last_print >= args.interval:
                last_print = now
                print(f"\n[{time.strftime('%H:%M:%S')}] " +
                      '、'.join(f"{k}×{v}" for k, v in sorted(counts.items())), flush=True)
                print(f"    conn={latest.get('conn')} "
                      f"jl=({latest.get('jlx')},{latest.get('jly')}) "
                      f"jr=({latest.get('jrx')},{latest.get('jry')}) "
                      f"jlf={latest.get('jlf')} jlst={latest.get('jlst')} "
                      f"jrf={latest.get('jrf')} jrst={latest.get('jrst')}", flush=True)
                print(f"    按鈕 L={latest.get('bl1')}{latest.get('bl2')}{latest.get('bl3')} "
                      f"R={latest.get('br1')}{latest.get('br2')}{latest.get('br3')} "
                      f"blst={latest.get('blst')} brst={latest.get('brst')}", flush=True)
                print(f"    編碼器 e1={latest.get('e1deg')}°(p={latest.get('e1p')} "
                      f"gm={latest.get('e1gm')} st={latest.get('e1st')}) "
                      f"e2={latest.get('e2deg')}°(p={latest.get('e2p')})", flush=True)
    except KeyboardInterrupt:
        print("\n[下游] 結束", flush=True)


def main():
    p = argparse.ArgumentParser(description="消防 rig 操作者輸入側 — UDP 虛擬設備(headless)")
    p.add_argument('--target', default='127.0.0.1:47810', help="EdgeLink UDP 埠(host:port)")
    p.add_argument('--hz', type=float, default=100.0,
                   help="搖桿/按鈕頻率(V1.1 預設 100 Hz);編碼器自動取一半")
    p.add_argument('--verbose', action='store_true')
    p.add_argument('--recv', type=int, help="改當下游:在這個 UDP 埠收 KV")
    p.add_argument('--interval', type=float, default=1.0, help="--recv 模式的印出間隔(秒)")

    p.add_argument('--conn', type=int, default=CONN_CONNECTED, choices=[0, 1, 2, 3, 255],
                   help="連線狀態:0 斷線 1 連線中 2 已連線 3 重連中 255 未啟用")
    p.add_argument('--fault-left', action='store_true', help="左桿雙軸 5V check 未過(值歸 0)")
    p.add_argument('--fault-right', action='store_true')
    p.add_argument('--stale-left', action='store_true', help="左桿 >1s 沒新資料")
    p.add_argument('--stale-right', action='store_true')
    p.add_argument('--stale-enc', action='store_true', help="編碼器 stale")
    p.add_argument('--gray-mismatch', action='store_true', help="編碼器 1 gray 一致性失敗")
    p.add_argument('--raw-left', action='store_true', help="左桿值未經低通濾波")
    p.add_argument('--raw-right', action='store_true')

    # V1.1 §5.2 新增。急停在線路上是反相的(1 = 鬆開),這裡的旗標是「按下」的語意,
    # 反相由 button_byte() 統一處理 —— 不讓每個呼叫點各自去記那件事。
    p.add_argument('--estop-left', action='store_true', help="左搖桿急停按下")
    p.add_argument('--estop-right', action='store_true', help="右搖桿急停按下")
    p.add_argument('--pedal', action='store_true', help="踏板踩下")

    args = p.parse_args()
    # fault 是位元遮罩(bit0=X 冗餘、bit1=Y 冗餘);這裡簡化成兩軸同時故障
    args.fault_left = 0b11 if args.fault_left else 0
    args.fault_right = 0b11 if args.fault_right else 0

    if args.recv:
        recv_loop(args)
    else:
        send_loop(args)
    return 0


if __name__ == '__main__':
    sys.exit(main())
