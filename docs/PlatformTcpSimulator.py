# PlatformTcpSimulator — 消防訓練平台「對方 Unity」的虛擬裝置
#
# 扮演協定文件裡的我方Unity:綁 0.0.0.0:47802 當 TCP server、只收一個 client、
# 持續推 msgType 19 平台狀態、對每一筆 16/17 命令回 msgType 18 執行結果。
# 用來在沒有實機的情況下把整條鏈路跑起來:
#
#   下游(KV) → EdgeLink TCP Server 埠 → 編碼 → EdgeLink TCP Client 埠 → 本模擬器
#   下游(KV) ← EdgeLink TCP Server 埠 ← 解碼 ← EdgeLink TCP Client 埠 ← 本模擬器
#
# 用法:
#   python PlatformTcpSimulator.py                     # 預設 0.0.0.0:47802、100 Hz
#   python PlatformTcpSimulator.py --hz 20 --verbose    # 慢速 + 印出每一筆命令
#   python PlatformTcpSimulator.py --drive-err a=0x8611 # 注入 A 軸跟隨誤差異警
#   python PlatformTcpSimulator.py --no-mode1           # 關閉 mode 1(命令會被 rc:11 拒絕)
#
# 需求:Python 3(只用標準函式庫)。

import argparse
import math
import socket
import struct
import sys
import threading
import time

MAGIC = b'OK'
VERSION = 1

HEADER = '<2sBBBHIQ'          # magic version msgType kind frameLength seq sendTimeMs = 19 bytes
FMT_MOVE = '<BBH4ff3f3f'      # mode reserved0 reserved1 tilt heave axis vel/acc/jerk = 48
FMT_MANAGE = '<BB'            # action reserved = 2
FMT_ACK = '<IBBBbB3f'         # ackSeq ackMsgType result reasonCode detailAxis flags resolved = 21
FMT_STATUS = '<IBBHH3H3f3f4fff'   # 見下方 build_status = 64

LEN_MOVE, LEN_MANAGE, LEN_ACK, LEN_STATUS = 67, 21, 40, 83

# reasonCode(協定文件 §7.1)
RC_OK, RC_LIMIT_MM, RC_NO_PLC, RC_NO_MOVE, RC_ESTOP = 0, 1, 4, 5, 6
RC_PLC_ERR, RC_SEQ, RC_MODE1_OFF, RC_IK, RC_NAN, RC_PROFILE, RC_NOT_EXTERNAL = 7, 10, 11, 12, 13, 14, 15

# groupStateRaw(§8 25)
GS_DISABLED, GS_STANDBY, GS_MOVING, GS_HOMING, GS_STOPPING, GS_ERRORSTOP = 0, 1, 2, 3, 4, 5

# plcConn(§8 24)
PLC_DISCONNECTED, PLC_CONNECTING, PLC_CONNECTED, PLC_RECONNECTING = 0, 1, 2, 3


def now_ms():
    return int(time.time() * 1000)


def finite(*vals):
    return all(math.isfinite(v) for v in vals)


class Platform:
    """三軸平台的狀態與極簡運動模型。實際位置以命令速度朝命令位置逼近,
    讓下游看得到 command 與 actual 之間的追隨過程(協定文件 §8 的建議畫法)。"""

    def __init__(self, args):
        self.args = args
        self.lock = threading.Lock()

        self.actual = [args.home, args.home, args.home]
        self.command = list(self.actual)
        self.velocity = args.default_vel

        self.estop = False
        self.servo_on = False
        self.plc = args.plc
        self.ethercat_err = args.ethercat_err
        self.drive_err = list(args.drive_err)

        self.ack_seq = 0            # 最後採納的移動命令 seq
        self.last_move_time = 0.0

    # ── 命令處理 ─────────────────────────────────────────────────────────────
    def apply_move(self, mode, tilt, heave, axis, vel, acc, jerk):
        """回傳 (reasonCode, detailAxis, resolvedPositionMm)。"""
        a = self.args

        if a.not_external:
            return RC_NOT_EXTERNAL, -1, (0.0, 0.0, 0.0)

        if not finite(*tilt, heave, *axis, vel, acc, jerk):
            return RC_NAN, -1, (0.0, 0.0, 0.0)

        if vel > a.max_vel or acc > a.max_acc or jerk > a.max_jerk:
            return RC_PROFILE, -1, (0.0, 0.0, 0.0)

        if mode == 1 and a.no_mode1:
            return RC_MODE1_OFF, -1, (0.0, 0.0, 0.0)

        if self.plc != PLC_CONNECTED:
            return RC_NO_PLC, -1, (0.0, 0.0, 0.0)

        if self.estop:
            return RC_ESTOP, -1, (0.0, 0.0, 0.0)

        if any(e != 0 for e in self.drive_err) or self.ethercat_err != 0:
            axis_idx = next((i for i, e in enumerate(self.drive_err) if e != 0), -1)
            return RC_PLC_ERR, axis_idx, (0.0, 0.0, 0.0)

        # 馬達沒 Enable 就不接受移動 —— 對應文件「移動平台前記得送 ServoToggle」
        if not self.servo_on:
            return RC_NO_MOVE, -1, (0.0, 0.0, 0.0)

        if mode == 0:
            norm = math.sqrt(sum(c * c for c in tilt))
            if norm < 1e-6:
                return RC_IK, -1, (0.0, 0.0, 0.0)          # 零四元數還原不出姿態
            target = self._ik(tilt, norm, heave)
        else:
            target = list(axis)

        for i, mm in enumerate(target):
            if mm < a.limit_lo or mm > a.limit_hi:
                return RC_LIMIT_MM, i, tuple(target)        # 優先度 A → B → C

        with self.lock:
            self.command = target
            self.velocity = vel if vel > 0 else a.default_vel
            self.last_move_time = time.time()
        return RC_OK, -1, tuple(target)

    def apply_manage(self, action):
        if action == 1:
            self.estop = True
        elif action == 2:
            self.estop = False
        elif action == 3:
            self.servo_on = not self.servo_on
        elif action == 4:
            self.drive_err = [0, 0, 0]
            self.ethercat_err = 0
        return RC_OK

    def _ik(self, tilt, norm, heave):
        """四元數 + 高度 → 三軸行程。這裡用平板三點支撐的線性近似,
        足以讓下游看到合理的連動,不是真機的運動學。"""
        x, y, z, w = (c / norm for c in tilt)
        # 四元數 → pitch/roll(弧度)
        pitch = math.asin(max(-1.0, min(1.0, 2.0 * (w * x - y * z))))
        roll = math.atan2(2.0 * (w * z + x * y), 1.0 - 2.0 * (z * z + x * x))
        L, W = self.args.arm_len, self.args.arm_width
        a = heave + L * math.sin(pitch)
        b = heave - 0.5 * L * math.sin(pitch) - 0.5 * W * math.sin(roll)
        c = heave - 0.5 * L * math.sin(pitch) + 0.5 * W * math.sin(roll)
        return [a, b, c]

    def _fk(self):
        """三軸實際位置 → 平台姿態(上面 _ik 的逆推近似)。"""
        a, b, c = self.actual
        L, W = self.args.arm_len, self.args.arm_width
        heave = (a + b + c) / 3.0
        pitch = math.asin(max(-1.0, min(1.0, (a - (b + c) / 2.0) / L)))
        roll = math.asin(max(-1.0, min(1.0, (c - b) / W)))
        cp, sp = math.cos(pitch / 2), math.sin(pitch / 2)
        cr, sr = math.cos(roll / 2), math.sin(roll / 2)
        # pitch 繞 X、roll 繞 Z(Unity 左手座標的簡化)
        return (sp * cr, sp * sr, cp * sr, cp * cr), heave

    # ── 每一幀 ───────────────────────────────────────────────────────────────
    def tick(self, dt):
        with self.lock:
            step = self.velocity * dt
            moving = False
            for i in range(3):
                delta = self.command[i] - self.actual[i]
                if abs(delta) <= step:
                    self.actual[i] = self.command[i]
                else:
                    self.actual[i] += math.copysign(step, delta)
                    moving = True
            return moving

    def group_state(self, moving):
        if any(e != 0 for e in self.drive_err) or self.ethercat_err != 0:
            return GS_ERRORSTOP
        if not self.servo_on:
            return GS_DISABLED
        if self.estop:
            return GS_STOPPING
        return GS_MOVING if moving else GS_STANDBY


class Session:
    """一條 client 連線。seq 期望值綁在連線上 —— 新連線一律從 1 重新算起。"""

    def __init__(self, conn, addr, plat, args):
        self.conn, self.addr, self.plat, self.args = conn, addr, plat, args
        self.expect = {16: 1, 17: 1}     # 下一筆可接受的 seq
        self.out_seq = {18: 0, 19: 0}    # 我方送出的 seq(每個 msgType 各一條)
        self.buf = b''
        self.alive = True
        self.stats = {'rx': 0, 'tx': 0, 'rejected': 0}

    def next_seq(self, msg_type):
        self.out_seq[msg_type] += 1
        return self.out_seq[msg_type]

    def send(self, data):
        try:
            self.conn.sendall(data)
            self.stats['tx'] += 1
        except OSError:
            self.alive = False

    # ── 收 ───────────────────────────────────────────────────────────────────
    def feed(self, data):
        self.buf += data
        while True:
            idx = self.buf.find(MAGIC)
            if idx < 0:
                self.buf = self.buf[-1:]           # 留住可能是 magic 前綴的那個 byte
                return
            if idx:
                self.buf = self.buf[idx:]
            if len(self.buf) < 19:
                return

            _, ver, mt, _kind, frame_len, seq, _ts = struct.unpack(HEADER, self.buf[:19])
            if ver != VERSION or mt not in (16, 17):
                self.buf = self.buf[1:]            # 對不上就跳一個 byte 重新對齊
                continue
            if frame_len not in (LEN_MOVE, LEN_MANAGE) or len(self.buf) < frame_len:
                if frame_len not in (LEN_MOVE, LEN_MANAGE):
                    self.buf = self.buf[1:]
                    continue
                return                              # 等整包到齊

            packet, self.buf = self.buf[:frame_len], self.buf[frame_len:]
            self.stats['rx'] += 1
            self.handle(mt, seq, packet)

    def handle(self, mt, seq, packet):
        # seq 規則(協定文件 §4):同一條連線必須嚴格遞增,倒退或重複一律拒絕
        if seq < self.expect[mt]:
            self.reject(seq, mt, RC_SEQ)
            return
        self.expect[mt] = seq + 1

        if mt == 16:
            (mode, _r0, _r1, tx, ty, tz, tw, heave,
             ax, bx, cx, vel, acc, jerk) = struct.unpack(FMT_MOVE, packet[19:])
            rc, axis, resolved = self.plat.apply_move(
                mode, (tx, ty, tz, tw), heave, (ax, bx, cx), vel, acc, jerk)
            if rc == RC_OK:
                self.plat.ack_seq = seq
            if self.args.verbose:
                kind = f"mode{mode} " + (f"axis=({ax:.1f},{bx:.1f},{cx:.1f})" if mode == 1
                                         else f"q=({tx:.3f},{ty:.3f},{tz:.3f},{tw:.3f}) hv={heave:.1f}")
                print(f"  ← 16 seq={seq} {kind} → {'接受' if rc == 0 else f'拒絕 rc={rc} axis={axis}'}",
                      flush=True)
            self.ack(seq, 16, rc, axis, resolved)
        else:
            action, _ = struct.unpack(FMT_MANAGE, packet[19:])
            rc = self.plat.apply_manage(action)
            if self.args.verbose:
                name = {1: 'EStopOn', 2: 'EStopOff', 3: 'ServoToggle', 4: 'Reset'}.get(action, f'?{action}')
                state = f"servo={'ON' if self.plat.servo_on else 'OFF'} estop={self.plat.estop}"
                print(f"  ← 17 seq={seq} {name} → 接受 ({state})", flush=True)
            self.ack(seq, 17, rc, -1, (0.0, 0.0, 0.0))

    # ── 送 ───────────────────────────────────────────────────────────────────
    def reject(self, ack_seq, ack_mt, rc):
        self.stats['rejected'] += 1
        if self.args.verbose:
            print(f"  ← {ack_mt} seq={ack_seq} → 拒絕 rc={rc}", flush=True)
        self.ack(ack_seq, ack_mt, rc, -1, (0.0, 0.0, 0.0))

    def ack(self, ack_seq, ack_mt, rc, axis, resolved):
        if rc != RC_OK:
            self.stats['rejected'] += 1
        flags = 1 if self.args.test_mode else 0
        body = struct.pack(FMT_ACK, ack_seq, ack_mt, 0 if rc == RC_OK else 1,
                           rc, axis, flags, *resolved)
        self.send(self._header(18, LEN_ACK) + body)

    def send_status(self, moving):
        p = self.plat
        tilt, heave = p._fk()
        link = (0 if self.args.test_mode else 1) | (2 if p.estop else 0)
        body = struct.pack(FMT_STATUS,
                           p.ack_seq, link, p.plc,
                           p.group_state(moving), p.ethercat_err,
                           p.drive_err[0], p.drive_err[1], p.drive_err[2],
                           *p.actual, *p.command, *tilt, heave, 0.0)
        self.send(self._header(19, LEN_STATUS) + body)

    def _header(self, msg_type, frame_len):
        return struct.pack(HEADER, MAGIC, VERSION, msg_type, 0,
                           frame_len, self.next_seq(msg_type), now_ms())


def serve(args):
    srv = socket.socket(socket.AF_INET, socket.SOCK_STREAM)
    srv.setsockopt(socket.SOL_SOCKET, socket.SO_REUSEADDR, 1)
    srv.bind((args.host, args.port))
    srv.listen(4)
    # 非阻塞 accept:先前設了 0.2s timeout,沒有新連線時每圈都白等 0.2 秒,
    # 整個主迴圈(含狀態推送)被壓到 5 Hz —— --hz 根本沒作用。
    srv.setblocking(False)

    plat = Platform(args)
    session = None
    last = time.time()
    period = 1.0 / args.hz
    next_push = last

    print(f"[模擬器] 平台 Unity 已就緒 {args.host}:{args.port} "
          f"({args.hz} Hz、行程 {args.limit_lo}–{args.limit_hi} mm、原點 {args.home} mm)", flush=True)
    if args.drive_err != [0, 0, 0] or args.ethercat_err:
        print(f"[模擬器] 已注入錯誤 driveErr={[hex(e) for e in args.drive_err]} "
              f"etherCat={args.ethercat_err}", flush=True)
    print("[模擬器] 等待 client…(只接受一個,新連線會覆蓋舊的)", flush=True)

    try:
        while True:
            # 只留一個 client:新連線覆蓋舊的(協定文件 §1)
            try:
                conn, addr = srv.accept()
                if session and session.alive:
                    print(f"[模擬器] 新連線 {addr} 覆蓋舊連線 {session.addr}", flush=True)
                    try:
                        session.conn.close()
                    except OSError:
                        pass
                conn.setblocking(False)
                session = Session(conn, addr, plat, args)
                print(f"[模擬器] client 連上 {addr} — seq 期望值重置為 1", flush=True)
            except BlockingIOError:
                pass
            except OSError:
                pass

            if session and session.alive:
                try:
                    data = session.conn.recv(65536)
                    if data:
                        session.feed(data)
                    else:
                        raise ConnectionResetError
                except BlockingIOError:
                    pass
                except OSError:
                    print(f"[模擬器] client {session.addr} 斷線 "
                          f"(收 {session.stats['rx']} 筆 / 送 {session.stats['tx']} 筆 / "
                          f"拒絕 {session.stats['rejected']} 筆)", flush=True)
                    session.alive = False

            now = time.time()
            moving = plat.tick(now - last)
            last = now

            # 沒連上運動控制器時也照心跳下限推送(協定文件 §1)
            if session and session.alive and now >= next_push:
                next_push = now + period
                session.send_status(moving)

            # Windows 的 time.sleep 解析度約 15.6 ms,睡固定的一小段會讓 50Hz 以上達不到。
            # 睡到「下一次該推送」為止,並留一點餘裕給收包。
            slack = next_push - time.time()
            time.sleep(max(0.0, min(slack, 0.002)))
    except KeyboardInterrupt:
        print("\n[模擬器] 結束", flush=True)
    finally:
        srv.close()


def parse_drive_err(values):
    out = [0, 0, 0]
    for item in values:
        axis, _, code = item.partition('=')
        idx = {'a': 0, 'b': 1, 'c': 2}.get(axis.strip().lower())
        if idx is None:
            raise argparse.ArgumentTypeError(f"軸只能是 a/b/c(收到 '{axis}')")
        out[idx] = int(code, 0)
    return out


def main():
    p = argparse.ArgumentParser(description="消防訓練平台 TCP V1 — 對方 Unity 虛擬裝置")
    p.add_argument('--host', default='0.0.0.0')
    p.add_argument('--port', type=int, default=47802)
    p.add_argument('--hz', type=float, default=100.0, help="狀態推送頻率")
    p.add_argument('--verbose', action='store_true', help="印出每一筆收到的命令")
    p.add_argument('--test-mode', action='store_true', help="接受命令但不真的輸出(linkFlags bit0=0)")
    p.add_argument('--no-mode1', action='store_true', help="關閉 mode 1(命令回 rc:11)")
    p.add_argument('--not-external', action='store_true',
                   help="模擬輸入來源沒切到 External(命令回 rc:15)")
    p.add_argument('--plc', type=int, default=PLC_CONNECTED, choices=[0, 1, 2, 3],
                   help="plcConn 狀態:0 斷線 1 連線中 2 已連線 3 重連中(非 2 時命令回 rc:4)")

    p.add_argument('--home', type=float, default=200.0, help="開機時的三軸位置 mm")
    p.add_argument('--limit-lo', type=float, default=0.0, help="行程下限 mm")
    p.add_argument('--limit-hi', type=float, default=400.0, help="行程上限 mm")
    p.add_argument('--arm-len', type=float, default=600.0, help="A 軸到 BC 中點的距離 mm")
    p.add_argument('--arm-width', type=float, default=500.0, help="B 軸到 C 軸的距離 mm")

    p.add_argument('--default-vel', type=float, default=134.0)
    p.add_argument('--max-vel', type=float, default=500.0)
    p.add_argument('--max-acc', type=float, default=2000.0)
    p.add_argument('--max-jerk', type=float, default=8000.0)

    p.add_argument('--drive-err', nargs='*', default=[], metavar='軸=碼',
                   help="注入伺服異警,例如 a=0x8611 b=0x0231")
    p.add_argument('--ethercat-err', type=int, default=0, help="注入 EtherCAT 錯誤碼(0-16)")

    args = p.parse_args()
    args.drive_err = parse_drive_err(args.drive_err)
    serve(args)


if __name__ == '__main__':
    sys.exit(main())
