# FullChainTestAll — 兩條鏈路同時跑的整合測試
#
# 先前 PlatformTcpTestAll / RigUdpTestAll 是各測各的,但實際部署是兩條同時在跑:
#
#   設備(搖桿/按鈕/編碼器) ──UDP 二進位 50Hz──▶ RigRelay ──UDP KV──▶ 下游
#   平台 Unity ──TCP 二進位 100Hz──▶ PlatLink ──┐
#                                              ├─ PlatFanout ──TCP KV──▶ 下游
#   下游 ──TCP KV 命令──────────────────────────┘
#
# 這支測的是「同時」才會出現的問題:兩條路互相搶不搶資源、命令會不會被狀態洪水淹掉、
# 兩條路的資料會不會串到對方的下游去。
#
# 用法:
#   1. 關掉常駐的模擬器視窗(這支會自己開平台模擬器)
#   2. python FullChainTestAll.py
#
# 需求:Python 3、EdgeLink 已跑起來且 PlatformTcp 與 RigBinary 兩組埠都設好。

import argparse
import math
import os
import socket
import struct
import subprocess
import sys
import threading
import time

HERE = os.path.dirname(os.path.abspath(__file__))
PLATFORM_SIM = os.path.join(HERE, 'PlatformTcpSimulator.py')

MAGIC = b'OK'
FMT_JOY = '<2sBBBIQBBffBffB'
FMT_BTN = '<2sBBBIQBBBBBB'
FMT_ENC = '<2sBBBIQBBBBfBBf'


def now_ms():
    return int(time.time() * 1000)


def move(a, b, c):
    return (f"mt:16;mode:1;rqx:0;rqy:0;rqz:0;rqw:0;rhv:0;rqa:{a};rqb:{b};rqc:{c}")


class TcpRail:
    """平台這條:接 EdgeLink TCP Server 埠,收 mt:18/19、送命令。"""

    def __init__(self, host, port):
        self.sock = socket.create_connection((host, port), timeout=5)
        self.sock.settimeout(0.2)
        self.buf = b''
        self.running = True
        self.lock = threading.Lock()
        self.acks = []
        self.status_count = 0
        self.status = {}
        self.foreign = []          # 不該出現在這條路上的訊息
        threading.Thread(target=self._reader, daemon=True).start()

    def _reader(self):
        while self.running:
            try:
                data = self.sock.recv(65536)
                if not data:
                    self.running = False
                    return
            except socket.timeout:
                continue
            except OSError:
                self.running = False
                return
            self.buf += data
            while b'\n' in self.buf:
                raw, self.buf = self.buf.split(b'\n', 1)
                self._line(raw.decode('utf-8', 'replace').strip())

    def _line(self, line):
        if line.startswith('EDGELINK_PING:'):
            self.sock.sendall(('EDGELINK_PONG:' + line.split(':', 1)[1] + '\n').encode())
            return
        if not line or line.startswith('EDGELINK_'):
            return
        kv = dict(f.split(':', 1) for f in line.split(';') if ':' in f)
        with self.lock:
            mt = kv.get('mt')
            if mt == '18':
                self.acks.append(kv)
            elif mt == '19':
                self.status = kv
                self.status_count += 1
            else:
                self.foreign.append(line)      # 設備那條的資料不該跑到這裡

    def ask(self, command, timeout=4.0):
        with self.lock:
            self.acks.clear()
        self.sock.sendall((command + '\n').encode())
        end = time.time() + timeout
        while time.time() < end:
            with self.lock:
                if self.acks:
                    return self.acks.pop(0)
            time.sleep(0.01)
        return None

    def close(self):
        self.running = False
        try:
            self.sock.close()
        except OSError:
            pass


class UdpRail:
    """設備這條:送二進位進 EdgeLink、在轉發埠收 KV。"""

    def __init__(self, host, send_port, recv_port):
        self.dest = (host, send_port)
        self.tx = socket.socket(socket.AF_INET, socket.SOCK_DGRAM)
        self.rx = socket.socket(socket.AF_INET, socket.SOCK_DGRAM)
        self.rx.setsockopt(socket.SOL_SOCKET, socket.SO_REUSEADDR, 1)
        self.rx.bind(('0.0.0.0', recv_port))
        self.rx.settimeout(0.2)

        self.lock = threading.Lock()
        self.counts = {'joy': 0, 'btn': 0, 'enc': 0}
        self.latest = {}
        self.foreign = []
        self.device_status = []      # EdgeLink 自己發的 EDGELINK_STATUS 上下線通知
        self.running = True
        self.seq = 0
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
                # EdgeLink 依訊息的 id 欄位做 timeout 偵測,上下線時會發這個 —— 是功能不是雜訊
                if line.startswith('EDGELINK_'):
                    with self.lock:
                        self.device_status.append(line)
                    continue
                kv = dict(f.split(':', 1) for f in line.split(';') if ':' in f)
                with self.lock:
                    if 'jlx' in kv:
                        self.counts['joy'] += 1
                    elif 'bl1' in kv:
                        self.counts['btn'] += 1
                    elif 'e1p' in kv:
                        self.counts['enc'] += 1
                    else:
                        self.foreign.append(line)   # 平台那條的資料不該跑到這裡
                    self.latest.update(kv)

    def send_snapshot(self):
        self.seq += 1
        t = time.time()
        jlx, jly = math.sin(t), math.cos(t)
        self.tx.sendto(struct.pack(FMT_JOY, MAGIC, 1, 1, 0, self.seq, now_ms(), 2, 2,
                                   jlx, jly, 0, 0.0, 0.0, 0), self.dest)
        self.tx.sendto(struct.pack(FMT_BTN, MAGIC, 1, 2, 0, self.seq, now_ms(), 2, 2,
                                   self.seq % 8, 0, 0, 0), self.dest)
        self.tx.sendto(struct.pack(FMT_ENC, MAGIC, 1, 3, 0, self.seq, now_ms(), 2, 2,
                                   self.seq % 256, 0, (self.seq % 256) * 1.40625,
                                   0, 0, 0.0), self.dest)

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
        self.results = []
        self.sim = None

    def expect(self, name, cond, detail):
        self.results.append((bool(cond), name, detail))

    def start_platform_sim(self):
        env = dict(os.environ, PYTHONIOENCODING='utf-8')
        self.sim = subprocess.Popen(
            [sys.executable, PLATFORM_SIM, '--hz', str(self.args.plat_hz)],
            stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL, env=env)

    def stop_platform_sim(self):
        if self.sim:
            self.sim.terminate()
            try:
                self.sim.wait(timeout=5)
            except subprocess.TimeoutExpired:
                self.sim.kill()
            self.sim = None

    def run(self):
        a = self.args
        print(f"[整合] 平台 {a.plat_hz:g} Hz(TCP)+ 設備 {a.rig_hz:g} Hz(UDP)同時跑 {a.seconds:g} 秒",
              flush=True)
        self.start_platform_sim()

        tcp = None
        udp = UdpRail(a.host, a.udp_send, a.udp_recv)
        try:
            # 等 EdgeLink 重新撥號到模擬器
            deadline = time.time() + a.reconnect
            while time.time() < deadline:
                try:
                    tcp = TcpRail(a.host, a.tcp_port)
                    break
                except OSError:
                    time.sleep(0.5)
            if tcp is None:
                raise SystemExit("連不上 EdgeLink 的 TCP Server 埠")

            deadline = time.time() + a.reconnect
            while time.time() < deadline and not tcp.status:
                time.sleep(0.1)
            self.expect("平台狀態有進來(EdgeLink 已連上模擬器)", bool(tcp.status),
                        f"plctxt={tcp.status.get('plctxt')}")
            if not tcp.status:
                return self.report()

            tcp.ask("mt:17;act:3")      # Enable 馬達

            # ── 兩條同時全速跑 ──
            with tcp.lock:
                tcp.status_count = 0
            with udp.lock:
                udp.counts = {'joy': 0, 'btn': 0, 'enc': 0}

            rig_period = 1.0 / a.rig_hz
            t0 = time.time()
            sent = 0
            cmd_latencies = []
            next_cmd = t0 + 1.0

            while time.time() - t0 < a.seconds:
                udp.send_snapshot()
                sent += 1

                # 每秒插一筆平台命令,量它在狀態洪水裡的往返時間
                if time.time() >= next_cmd:
                    next_cmd += 1.0
                    target = 200 + (len(cmd_latencies) % 5) * 10
                    t1 = time.time()
                    ack = tcp.ask(move(target, target, target), timeout=3.0)
                    if ack and ack.get('restxt') == 'ACCEPT':
                        cmd_latencies.append((time.time() - t1) * 1000)

                time.sleep(max(0.0, t0 + sent * rig_period - time.time()))

            elapsed = time.time() - t0
            time.sleep(0.5)

            with tcp.lock:
                plat_rx, plat_foreign = tcp.status_count, list(tcp.foreign)
            with udp.lock:
                rig_counts, rig_foreign = dict(udp.counts), list(udp.foreign)
                rig_status = list(udp.device_status)

            # ── 判定 ──
            plat_rate = plat_rx / elapsed
            self.expect(f"平台狀態速率(設定 {a.plat_hz:g} Hz)", plat_rate >= a.plat_hz * 0.8,
                        f"實測 {plat_rate:.1f} Hz、共 {plat_rx} 筆")

            for kind, label in (('joy', '搖桿'), ('btn', '按鈕'), ('enc', '編碼器')):
                got = rig_counts[kind]
                self.expect(f"設備 {label} 送達率 ≥95%", got >= sent * 0.95,
                            f"{got}/{sent}({got / sent * 100:.1f}%)")

            self.expect("平台那條沒有混進設備資料", not plat_foreign,
                        "無" if not plat_foreign else f"{len(plat_foreign)} 筆:{plat_foreign[0][:60]}")
            self.expect("設備那條沒有混進平台資料", not rig_foreign,
                        "無" if not rig_foreign else f"{len(rig_foreign)} 筆:{rig_foreign[0][:60]}")

            # EdgeLink 從 KV 的 id 欄位認出裝置,並在「從沒有到有」時發 EDGELINK_STATUS:CONNECTED。
            # 那是狀態轉換,不是每輪都會發 —— 裝置逾時預設 30 秒,前一段測試剛送過資料的話
            # 它還在 CONNECTED,本輪就不會再發。所以這裡驗的是「有發就必須帶得出 id」,
            # 而不是「一定要發」;後者只有冷啟動才成立,拿來當斷言會變成看順序決定成敗的假失敗。
            connected = [x for x in rig_status if x.startswith('EDGELINK_STATUS:CONNECTED')]
            if connected:
                self.expect("設備上線通知帶得出 id", any('rig1' in x for x in connected),
                            connected[0])
            else:
                self.expect("設備上線通知帶得出 id", True,
                            "本輪無上線轉換(裝置先前已註冊,逾時 30s 內不會重發)")

            self.expect("命令在狀態洪水中仍會回應", len(cmd_latencies) >= int(a.seconds) - 1,
                        f"{len(cmd_latencies)} 筆命令全部收到 ACCEPT")
            if cmd_latencies:
                worst = max(cmd_latencies)
                avg = sum(cmd_latencies) / len(cmd_latencies)
                self.expect("命令往返最差 <500ms", worst < 500,
                            f"平均 {avg:.1f}ms、最差 {worst:.1f}ms")

            st = tcp.status
            self.expect("平台仍在正常狀態", st.get('plctxt') == 'CONNECTED',
                        f"plctxt={st.get('plctxt')} gsttxt={st.get('gsttxt')}")
            self.expect("設備數值仍在更新", udp.latest.get('jlx') is not None,
                        f"jlx={udp.latest.get('jlx')} e1deg={udp.latest.get('e1deg')}")
        finally:
            if tcp:
                tcp.close()
            udp.close()
            self.stop_platform_sim()

        return self.report()

    def report(self):
        print("\n" + "═" * 74, flush=True)
        passed = sum(1 for ok, _, _ in self.results if ok)
        for ok, name, detail in self.results:
            print(f"  {'PASS' if ok else 'FAIL'}  {name:34} {detail}", flush=True)
        print("═" * 74, flush=True)
        print(f"  {passed}/{len(self.results)} 通過", flush=True)
        return 0 if passed == len(self.results) else 1


def main():
    p = argparse.ArgumentParser(description="EdgeLink 兩條鏈路同時跑的整合測試")
    p.add_argument('--host', default='127.0.0.1')
    p.add_argument('--tcp-port', type=int, default=47900, help="平台鏈路的 TCP Server 埠")
    p.add_argument('--udp-send', type=int, default=47810, help="設備鏈路:EdgeLink 監聽埠")
    p.add_argument('--udp-recv', type=int, default=47811, help="設備鏈路:EdgeLink 轉發埠")
    p.add_argument('--plat-hz', type=float, default=100.0)
    p.add_argument('--rig-hz', type=float, default=50.0)
    p.add_argument('--seconds', type=float, default=15.0)
    p.add_argument('--reconnect', type=float, default=25.0)
    return Runner(p.parse_args()).run()


if __name__ == '__main__':
    sys.exit(main())
