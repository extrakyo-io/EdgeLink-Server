# RigUdpTestAll — 操作者輸入側 UDP 鏈路的全欄位自動測試
#
# 這支程式同時扮演兩端:自己組二進位封包用 UDP 送進 EdgeLink,再從轉發埠收回解碼後的 KV,
# 逐欄位比對。中間的分包判斷、位元解析、bitrange、f32 格式化全是 EdgeLink 的正式程式碼。
#
#   本程式(設備) ──UDP 二進位──▶ EdgeLink UDP 埠(RigBinary) ──UDP KV──▶ 本程式(下游)
#
# 直接組封包而不透過 RigUdpSimulator,是為了能送出模擬器不會產生的東西:
# 長度不對的封包、未知 msgType、壞掉的 magic —— 那些才是驗「該丟的有沒有丟」的關鍵。
#
# 用法:
#   1. 先把常駐的 RigUdpSimulator --recv 關掉(否則 47811 會被搶走)
#   2. python RigUdpTestAll.py
#
# 需求:Python 3(只用標準函式庫)、EdgeLink 已跑起來且有 RigRelay(RigBinary)UDP 埠。

import argparse
import socket
import struct
import sys
import threading
import time

MAGIC = b'OK'
VERSION = 1

FMT_JOY = '<2sBBBIQBBffBffB'   # 37
FMT_BTN = '<2sBBBIQBBBBBB'     # 23
FMT_ENC = '<2sBBBIQBBBBfBBf'   # 31


def now_ms():
    return int(time.time() * 1000)


def joy(seq, conn=2, jlx=0.0, jly=0.0, jls=0, jrx=0.0, jry=0.0, jrs=0):
    return struct.pack(FMT_JOY, MAGIC, VERSION, 1, 0, seq, now_ms(), conn, 2,
                       jlx, jly, jls, jrx, jry, jrs)


def btn(seq, conn=2, bl=0, bls=0, br=0, brs=0):
    return struct.pack(FMT_BTN, MAGIC, VERSION, 2, 0, seq, now_ms(), conn, 2, bl, bls, br, brs)


def enc(seq, conn=2, e1p=0, e1s=0, e1deg=0.0, e2p=0, e2s=0, e2deg=0.0):
    return struct.pack(FMT_ENC, MAGIC, VERSION, 3, 0, seq, now_ms(), conn, 2,
                       e1p, e1s, e1deg, e2p, e2s, e2deg)


class Rig:
    """設備端(送)+ 下游端(收)兩個 socket。"""

    def __init__(self, args):
        self.args = args
        self.tx = socket.socket(socket.AF_INET, socket.SOCK_DGRAM)
        self.dest = (args.host, args.send_port)

        self.rx = socket.socket(socket.AF_INET, socket.SOCK_DGRAM)
        self.rx.setsockopt(socket.SOL_SOCKET, socket.SO_REUSEADDR, 1)
        self.rx.bind(('0.0.0.0', args.recv_port))
        self.rx.settimeout(0.2)

        self.lines = []
        self.lock = threading.Lock()
        self.running = True
        threading.Thread(target=self._reader, daemon=True).start()

    def _reader(self):
        while self.running:
            try:
                data, _ = self.rx.recvfrom(65536)
            except socket.timeout:
                continue
            except OSError:
                return
            for line in data.decode('utf-8', 'replace').splitlines():
                line = line.strip()
                if not line:
                    continue
                kv = {}
                for f in line.split(';'):
                    k, sep, v = f.partition(':')
                    if sep:
                        kv[k.strip()] = v.strip()
                with self.lock:
                    self.lines.append(kv)

    def ask(self, packet, marker, seq, timeout=1.5):
        """送一包,等回對應的 KV(用 seq + 該訊息獨有的欄位辨識)。收不到回 None。"""
        with self.lock:
            self.lines.clear()
        self.tx.sendto(packet, self.dest)
        end = time.time() + timeout
        while time.time() < end:
            with self.lock:
                for kv in self.lines:
                    if kv.get('seq') == str(seq) and marker in kv:
                        return kv
            time.sleep(0.01)
        return None

    def close(self):
        self.running = False
        for s in (self.tx, self.rx):
            try:
                s.close()
            except OSError:
                pass


class Runner:
    def __init__(self, args):
        self.args = args
        self.rig = Rig(args)
        self.results = []
        self.seq = 0

    def next_seq(self):
        self.seq += 1
        return self.seq

    def fields(self, name, kv, expected):
        """比對多個欄位,一次記一條結果。"""
        if kv is None:
            self.results.append((False, name, "沒收到 KV(封包被丟棄或逾時)"))
            return
        bad = [f"{k}={kv.get(k)}≠{v}" for k, v in expected.items() if kv.get(k) != v]
        detail = '、'.join(f"{k}={kv.get(k)}" for k in expected) if not bad else '、'.join(bad)
        self.results.append((not bad, name, detail))

    def expect(self, name, cond, detail):
        self.results.append((bool(cond), name, detail))

    def dropped(self, name, packet, marker, seq):
        got = self.rig.ask(packet, marker, seq, timeout=1.0)
        self.results.append((got is None, name,
                             "沒有 KV 轉出 = 已丟棄" if got is None else f"竟然解出來了:{got}"))

    # ── 搖桿(msgType 1)────────────────────────────────────────────────────
    def test_joystick(self):
        print("\n── 搖桿 msgType 1 " + "─" * 40, flush=True)

        s = self.next_seq()
        self.fields("搖桿數值與 conn", self.rig.ask(
            joy(s, conn=2, jlx=0.75, jly=-0.5, jrx=0.25, jry=1.0), 'jlx', s),
            {'id': 'rig1', 'seq': str(s), 'conn': '2',
             'jlx': '0.75', 'jly': '-0.5', 'jrx': '0.25', 'jry': '1'})

        # f32 + format "0.###":小數第四位要被捨掉
        s = self.next_seq()
        self.fields("f32 格式化到小數三位", self.rig.ask(
            joy(s, jlx=0.123456, jly=-0.987654), 'jlx', s),
            {'jlx': '0.123', 'jly': '-0.988'})

        # jlf 是 bitrange(bit0-1),0~3 四種組合都要對
        for mask, label in ((0, '無故障'), (1, 'X 冗餘'), (2, 'Y 冗餘'), (3, 'XY 都故障')):
            s = self.next_seq()
            self.fields(f"jlf bitrange = {mask}({label})", self.rig.ask(
                joy(s, jls=mask), 'jlf', s), {'jlf': str(mask)})

        # stale / raw 是同一個位元組的 bit2 / bit3
        s = self.next_seq()
        self.fields("jlst=bit2、jlraw=bit3 同時成立", self.rig.ask(
            joy(s, jls=0b1100), 'jlst', s), {'jlf': '0', 'jlst': '1', 'jlraw': '1'})

        s = self.next_seq()
        self.fields("左右桿旗標互不干擾", self.rig.ask(
            joy(s, jls=0b0011, jrs=0b0100), 'jlf', s),
            {'jlf': '3', 'jlst': '0', 'jrf': '0', 'jrst': '1'})

        for conn, label in ((0, '斷線'), (1, '連線中'), (3, '重連中'), (255, '未啟用')):
            s = self.next_seq()
            self.fields(f"conn = {conn}({label})", self.rig.ask(
                joy(s, conn=conn), 'jlx', s), {'conn': str(conn)})

    # ── 按鈕(msgType 2)────────────────────────────────────────────────────
    def test_buttons(self):
        print("\n── 按鈕 msgType 2 " + "─" * 40, flush=True)

        for bit, name in ((0, 'bl1'), (1, 'bl2'), (2, 'bl3')):
            s = self.next_seq()
            want = {'bl1': '0', 'bl2': '0', 'bl3': '0'}
            want[name] = '1'
            self.fields(f"左鈕 {name} 單獨按下", self.rig.ask(
                btn(s, bl=1 << bit), 'bl1', s), want)

        s = self.next_seq()
        self.fields("左三顆全按 + 右三顆全放", self.rig.ask(
            btn(s, bl=0b111, br=0), 'bl1', s),
            {'bl1': '1', 'bl2': '1', 'bl3': '1', 'br1': '0', 'br2': '0', 'br3': '0'})

        s = self.next_seq()
        self.fields("右鈕與 stale", self.rig.ask(
            btn(s, br=0b101, brs=0b100, bls=0), 'br1', s),
            {'br1': '1', 'br2': '0', 'br3': '1', 'brst': '1', 'blst': '0'})

    # ── 編碼器(msgType 3)──────────────────────────────────────────────────
    def test_encoder(self):
        print("\n── 編碼器 msgType 3 " + "─" * 38, flush=True)

        s = self.next_seq()
        self.fields("兩顆編碼器的位置與角度", self.rig.ask(
            enc(s, e1p=203, e1deg=285.4, e2p=118, e2deg=166.1), 'e1p', s),
            {'e1p': '203', 'e1deg': '285.4', 'e2p': '118', 'e2deg': '166.1'})

        s = self.next_seq()
        self.fields("u8 邊界 0 與 255", self.rig.ask(
            enc(s, e1p=0, e1deg=0.0, e2p=255, e2deg=359.9), 'e1p', s),
            {'e1p': '0', 'e1deg': '0', 'e2p': '255', 'e2deg': '359.9'})

        s = self.next_seq()
        self.fields("e1gm=bit0、e1st=bit1", self.rig.ask(
            enc(s, e1s=0b11, e2s=0b01), 'e1gm', s),
            {'e1gm': '1', 'e1st': '1', 'e2gm': '1', 'e2st': '0'})

    # ── 該丟的要丟 ─────────────────────────────────────────────────────────
    def test_rejects(self):
        print("\n── 壞封包必須丟棄 " + "─" * 40, flush=True)

        s = self.next_seq()
        self.dropped("長度短一個 byte", joy(s)[:-1], 'jlx', s)

        s = self.next_seq()
        self.dropped("長度多一個 byte", joy(s) + b'\x00', 'jlx', s)

        s = self.next_seq()
        bad_magic = b'XK' + joy(s)[2:]
        self.dropped("magic 不是 OK", bad_magic, 'jlx', s)

        s = self.next_seq()
        unknown = struct.pack(FMT_JOY, MAGIC, VERSION, 9, 0, s, now_ms(), 2, 2,
                              0.0, 0.0, 0, 0.0, 0.0, 0)
        self.dropped("msgType 9 沒有對應 variant", unknown, 'jlx', s)

        s = self.next_seq()
        self.dropped("空封包", b'', 'jlx', s)

    # ── 連續流 ─────────────────────────────────────────────────────────────
    def test_stream(self):
        print("\n── 連續流 " + "─" * 48, flush=True)

        with self.rig.lock:
            self.rig.lines.clear()

        n, hz = 150, 50.0
        base = self.seq
        t0 = time.time()
        for i in range(n):
            sq = self.next_seq()
            self.rig.tx.sendto(joy(sq, jlx=i / 100.0), self.rig.dest)
            self.rig.tx.sendto(btn(sq, bl=i % 8), self.rig.dest)
            self.rig.tx.sendto(enc(sq, e1p=i % 256, e1deg=i * 1.4), self.rig.dest)
            time.sleep(max(0.0, (i + 1) / hz - (time.time() - t0)))
        time.sleep(0.6)

        with self.rig.lock:
            lines = list(self.rig.lines)
        joys = [k for k in lines if 'jlx' in k]
        btns = [k for k in lines if 'bl1' in k]
        encs = [k for k in lines if 'e1p' in k]

        self.expect("三種訊息都有轉出", joys and btns and encs,
                    f"搖桿×{len(joys)}、按鈕×{len(btns)}、編碼器×{len(encs)}(各送 {n})")
        self.expect("轉出比例 ≥95%", min(len(joys), len(btns), len(encs)) >= n * 0.95,
                    f"最少的一種 {min(len(joys), len(btns), len(encs))}/{n}")

        seqs = [int(k['seq']) for k in joys]
        self.expect("seq 保持遞增(沒有亂序)", seqs == sorted(seqs),
                    f"{seqs[0]}…{seqs[-1]}" if seqs else "(無)")
        self.expect("seq 由送端決定,EdgeLink 原樣轉出",
                    seqs and seqs[0] > base, f"起始 seq={seqs[0] if seqs else '?'} > {base}")

    def run(self):
        try:
            self.test_joystick()
            self.test_buttons()
            self.test_encoder()
            self.test_rejects()
            self.test_stream()
        finally:
            self.rig.close()

        print("\n" + "═" * 74, flush=True)
        passed = sum(1 for ok, _, _ in self.results if ok)
        for ok, name, detail in self.results:
            print(f"  {'PASS' if ok else 'FAIL'}  {name:32} {detail}", flush=True)
        print("═" * 74, flush=True)
        print(f"  {passed}/{len(self.results)} 通過", flush=True)
        return 0 if passed == len(self.results) else 1


def main():
    p = argparse.ArgumentParser(description="Rig UDP 鏈路 — 全欄位自動測試")
    p.add_argument('--host', default='127.0.0.1')
    p.add_argument('--send-port', type=int, default=47810, help="EdgeLink UDP 監聽埠")
    p.add_argument('--recv-port', type=int, default=47811, help="EdgeLink 轉發出來的埠")
    args = p.parse_args()

    print(f"[測試] 送 → {args.host}:{args.send_port} / 收 ← UDP {args.recv_port}", flush=True)
    print("[測試] 請先關掉常駐的 RigUdpSimulator --recv,否則 47811 會被搶走", flush=True)
    return Runner(args).run()


if __name__ == '__main__':
    sys.exit(main())
