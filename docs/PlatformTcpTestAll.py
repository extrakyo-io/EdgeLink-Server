# PlatformTcpTestAll — 把每一種命令與每一種 reasonCode 跑過一遍(走真實鏈路)
#
# 下游送 KV → EdgeLink 編碼 → 模擬器判定 → 回 mt:18 → EdgeLink 解碼 → 下游比對。
# 中間的編碼、解碼、查表全是正式程式碼,不是 mock。
#
# 有幾種 reasonCode 需要平台端處在特定狀態才觸發得到(mode1 關閉、驅動器異警、
# PLC 斷線、輸入來源沒切 External),所以測試分階段,每一段用不同旗標重開模擬器。
#
# 用法:
#   1. 先把常駐的模擬器關掉(這支程式會自己開/關)
#   2. python PlatformTcpTestAll.py --port 47900
#
# 需求:Python 3(只用標準函式庫)、EdgeLink 已跑起來且匯入 PlatformTcp 設定。

import argparse
import os
import socket
import subprocess
import sys
import threading
import time

HERE = os.path.dirname(os.path.abspath(__file__))
SIM = os.path.join(HERE, 'PlatformTcpSimulator.py')

# 完整的三軸移動命令(mode 1);用不到的那一組欄位一律明確送 0
def move(a, b, c, mode=1, extra=''):
    return (f"mt:16;mode:{mode};rqx:0;rqy:0;rqz:0;rqw:0;rhv:0;"
            f"rqa:{a};rqb:{b};rqc:{c}" + extra)


def quat(x, y, z, w, hv):
    return f"mt:16;mode:0;rqx:{x};rqy:{y};rqz:{z};rqw:{w};rhv:{hv};rqa:0;rqb:0;rqc:0"


class Link:
    """下游連線:送 KV、收 mt:18 / mt:19。"""

    def __init__(self, host, port):
        self.sock = socket.create_connection((host, port), timeout=5)
        self.sock.settimeout(0.2)
        self.buf = b''
        self.running = True
        self.acks = []
        self.status = {}
        self.status_count = 0
        self.lock = threading.Lock()
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
            self.send('EDGELINK_PONG:' + line.split(':', 1)[1])
            return
        if not line or line.startswith('EDGELINK_'):
            return
        kv = {}
        for f in line.split(';'):
            k, sep, v = f.partition(':')
            if sep:
                kv[k.strip()] = v.strip()
        with self.lock:
            if kv.get('mt') == '18':
                self.acks.append(kv)
            elif kv.get('mt') == '19':
                self.status = kv
                self.status_count += 1

    def send(self, line):
        self.sock.sendall((line + '\n').encode('utf-8'))

    def ask(self, command, timeout=4.0):
        """送一筆命令,回傳對應的 mt:18;逾時回 None(代表整包被 EdgeLink 丟掉了)。"""
        with self.lock:
            self.acks.clear()
        self.send(command)
        end = time.time() + timeout
        while time.time() < end:
            with self.lock:
                if self.acks:
                    return self.acks.pop(0)
            time.sleep(0.02)
        return None

    def wait_status(self, timeout=6.0):
        """等一筆「比現在更新」的平台狀態。

        直接回傳快取住的最後一筆會讀到舊值:ask() 在 mt:18 一到就返回,
        而那一刻最新的 mt:19 還是命令生效之前推的。狀態要用 seq 判斷新鮮度。"""
        with self.lock:
            before = self.status.get('seq')
        end = time.time() + timeout
        while time.time() < end:
            with self.lock:
                if self.status and self.status.get('seq') != before:
                    return dict(self.status)
            time.sleep(0.02)
        with self.lock:
            return dict(self.status)

    def measure_rate(self, seconds=3.0):
        """量端到端實際收到的狀態速率(模擬器 → EdgeLink 解碼 → 下游)。
        設定 --hz 只是模擬器的目標值,中間任何一段跟不上都會反映在這裡。"""
        with self.lock:
            start = self.status_count
        t0 = time.time()
        time.sleep(seconds)
        with self.lock:
            got = self.status_count - start
        return got / (time.time() - t0)

    def close(self):
        self.running = False
        try:
            self.sock.close()
        except OSError:
            pass


class Runner:
    def __init__(self, args):
        self.args = args
        self.results = []
        self.sim = None

    # ── 模擬器生命週期 ───────────────────────────────────────────────────────
    def start_sim(self, *flags):
        self.stop_sim()
        env = dict(os.environ, PYTHONIOENCODING='utf-8')
        self.sim = subprocess.Popen(
            [sys.executable, SIM, '--hz', str(self.args.hz), *flags],
            stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL, env=env)
        # 等 EdgeLink 重新撥號並開始推狀態
        link = Link(self.args.host, self.args.port)
        if not link.wait_status(timeout=self.args.reconnect):
            link.close()
            raise SystemExit(f"模擬器起來了但 {self.args.reconnect}s 內收不到平台狀態 —— "
                             "EdgeLink 有跑嗎?PlatLink 埠是否啟用?")
        return link

    def stop_sim(self):
        if self.sim:
            self.sim.terminate()
            try:
                self.sim.wait(timeout=5)
            except subprocess.TimeoutExpired:
                self.sim.kill()
            self.sim = None
            time.sleep(0.3)

    # ── 判定 ─────────────────────────────────────────────────────────────────
    def check(self, name, ack, want_res, want_rc=None, want_axis=None):
        if ack is None:
            self.results.append((False, name, "沒收到 mt:18(命令被丟棄或逾時)"))
            return None
        got = f"{ack.get('restxt')} rc={ack.get('rc')}({ack.get('rctxt')}) axis={ack.get('axistxt')}"
        ok = ack.get('restxt') == want_res
        if want_rc is not None:
            ok = ok and ack.get('rc') == str(want_rc)
        if want_axis is not None:
            ok = ok and ack.get('axistxt') == want_axis
        self.results.append((ok, name, got))
        return ack

    def expect(self, name, cond, detail):
        self.results.append((bool(cond), name, detail))

    # ── 各階段 ───────────────────────────────────────────────────────────────
    def phase_normal(self):
        print("\n── 階段 1:正常狀態(預設模擬器)" + "─" * 30, flush=True)
        link = self.start_sim()
        try:
            # 開機狀態:馬達未 Enable
            self.check("馬達未 Enable 就移動 → rc:5", link.ask(move(210, 210, 210)), 'REJECT', 5)

            # 管理命令四種
            self.check("EStopOn (act:1)", link.ask("mt:17;act:1"), 'ACCEPT', 0)
            self.check("急停中移動 → rc:6", link.ask(move(210, 210, 210)), 'REJECT', 6)
            self.check("EStopOff (act:2)", link.ask("mt:17;act:2"), 'ACCEPT', 0)
            self.check("Reset (act:4)", link.ask("mt:17;act:4"), 'ACCEPT', 0)
            self.check("ServoToggle (act:3) 開", link.ask("mt:17;act:3"), 'ACCEPT', 0)

            st = link.wait_status()
            self.expect("Enable 後 gst 應為 STANDBY", st.get('gsttxt') == 'STANDBY',
                        f"gsttxt={st.get('gsttxt')}")

            # 正常移動 + 追隨
            self.check("mode 1 三軸移動", link.ask(move(260, 240, 240)), 'ACCEPT', 0)
            time.sleep(2.0)
            st = link.wait_status()
            self.expect("實際位置追上命令位置",
                        (st.get('pa'), st.get('pb'), st.get('pc')) == ('260', '240', '240'),
                        f"pa/pb/pc = {st.get('pa')}/{st.get('pb')}/{st.get('pc')}")
            self.expect("姿態由實際位置反解(前高 → qx>0)",
                        float(st.get('qx', 0)) > 0.01,
                        f"q=({st.get('qx')},{st.get('qy')},{st.get('qz')},{st.get('qw')}) hv={st.get('hv')}")

            self.check("mode 0 四元數移動", link.ask(quat(0, 0.05, 0, 0.999, 220)), 'ACCEPT', 0)

            rate = link.measure_rate(3.0)
            self.expect(f"端到端推送速率 (設定 {self.args.hz:g} Hz)",
                        rate >= self.args.hz * 0.8,
                        f"實測 {rate:.1f} Hz ({rate / self.args.hz * 100:.0f}%)")

            # 各種被拒
            self.check("超出行程界限 → rc:1 axis:A", link.ask(move(900, 200, 200)), 'REJECT', 1, 'A')
            self.check("B 軸超界 → rc:1 axis:B", link.ask(move(200, 900, 200)), 'REJECT', 1, 'B')
            self.check("C 軸超界 → rc:1 axis:C", link.ask(move(200, 200, 900)), 'REJECT', 1, 'C')
            self.check("零四元數 → rc:12", link.ask(quat(0, 0, 0, 0, 220)), 'REJECT', 12)
            self.check("速度超上限 → rc:14", link.ask(move(210, 210, 210, extra=';vel:9999')), 'REJECT', 14)

            # 缺必填欄位 → EdgeLink 就地丟棄,對方根本收不到(所以沒有 mt:18)
            self.expect("缺 rqc 的命令不會上線",
                        link.ask("mt:16;mode:1;rqx:0;rqy:0;rqz:0;rqw:0;rhv:0;rqa:210;rqb:210",
                                 timeout=1.5) is None,
                        "沒有 mt:18 回來 = 整包在 EdgeLink 被丟掉")
            self.expect("mt 不存在的命令不會上線",
                        link.ask("mt:99;mode:1", timeout=1.5) is None, "沒有 mt:18 回來")
            self.expect("數值不合法的命令不會上線",
                        link.ask(move('abc', 210, 210), timeout=1.5) is None, "沒有 mt:18 回來")
        finally:
            link.close()

    def phase_mode1_off(self):
        print("\n── 階段 2:對方關閉 mode 1 " + "─" * 34, flush=True)
        link = self.start_sim('--no-mode1')
        try:
            link.ask("mt:17;act:3")     # Enable
            self.check("mode 1 被關閉 → rc:11", link.ask(move(210, 210, 210)), 'REJECT', 11)
            self.check("改用 mode 0 仍可移動", link.ask(quat(0, 0.05, 0, 0.999, 220)), 'ACCEPT', 0)
        finally:
            link.close()

    def phase_drive_error(self):
        print("\n── 階段 3:注入驅動器異警 " + "─" * 34, flush=True)
        link = self.start_sim('--drive-err', 'a=0x8611', 'b=0x0231', '--ethercat-err', '2')
        try:
            link.ask("mt:17;act:3")
            self.check("PLC/驅動器有錯 → rc:7", link.ask(move(210, 210, 210)), 'REJECT', 7)

            st = link.wait_status()
            self.expect("精確查表 0x8611 → FOLLOWING_ERROR",
                        st.get('derratxt') == 'FOLLOWING_ERROR', f"derratxt={st.get('derratxt')}")
            self.expect("範圍查表 0x0231 → PR_PARAM",
                        st.get('derrbtxt') == 'PR_PARAM', f"derrbtxt={st.get('derrbtxt')}")
            self.expect("無異警 → NONE_OR_UNCODED",
                        st.get('derrctxt') == 'NONE_OR_UNCODED', f"derrctxt={st.get('derrctxt')}")
            self.expect("EtherCAT 查表 2 → WRONG_WORKING_COUNTER",
                        st.get('ecerrtxt') == 'WRONG_WORKING_COUNTER', f"ecerrtxt={st.get('ecerrtxt')}")
            self.expect("有異警時 gst 應為 ERRORSTOP",
                        st.get('gsttxt') == 'ERRORSTOP', f"gsttxt={st.get('gsttxt')}")
            self.expect("原始碼要保留(34321 = 0x8611)",
                        st.get('derra') == '34321', f"derra={st.get('derra')}")

            self.check("Reset 清除異警", link.ask("mt:17;act:4"), 'ACCEPT', 0)
            time.sleep(1.0)
            st = link.wait_status()
            self.expect("Reset 後異警清空",
                        st.get('derratxt') == 'NONE_OR_UNCODED', f"derratxt={st.get('derratxt')}")
        finally:
            link.close()

    def phase_plc_down(self):
        print("\n── 階段 4:PLC 未連線 " + "─" * 38, flush=True)
        link = self.start_sim('--plc', '0')
        try:
            self.check("尚未讀到 PLC 狀態 → rc:4", link.ask(move(210, 210, 210)), 'REJECT', 4)
            st = link.wait_status()
            self.expect("plcConn 查表 0 → DISCONNECTED",
                        st.get('plctxt') == 'DISCONNECTED', f"plctxt={st.get('plctxt')}")
        finally:
            link.close()

    def phase_not_external(self):
        print("\n── 階段 5:輸入來源沒切 External " + "─" * 27, flush=True)
        link = self.start_sim('--not-external')
        try:
            link.ask("mt:17;act:3")
            self.check("輸入來源未切 External → rc:15", link.ask(move(210, 210, 210)), 'REJECT', 15)
        finally:
            link.close()

    # ── 主流程 ───────────────────────────────────────────────────────────────
    def run(self):
        try:
            self.phase_normal()
            self.phase_mode1_off()
            self.phase_drive_error()
            self.phase_plc_down()
            self.phase_not_external()
        finally:
            self.stop_sim()

        print("\n" + "═" * 74, flush=True)
        passed = sum(1 for ok, _, _ in self.results if ok)
        for ok, name, detail in self.results:
            print(f"  {'PASS' if ok else 'FAIL'}  {name:38} {detail}", flush=True)
        print("═" * 74, flush=True)
        print(f"  {passed}/{len(self.results)} 通過", flush=True)

        print("""
  這個測試台觸發不到的 reasonCode(不是失敗,是環境限制):
    rc:2  場景單位超界 —— 模擬器不模擬 Unity 場景單位
    rc:3  水平偏移超過球頭範圍 —— 文件說基本上不會發生
    rc:8  保留,協定未使用
    rc:9  WatchDog 逾時 —— 文件說預設停用
    rc:10 seq 倒退或重複 —— 下游無法製造,seq 由 EdgeLink 自己產生;
          這條由 Server.Tests 的 Route_BinarySeq_ResetsAfterReconnect 覆蓋
""", flush=True)
        return 0 if passed == len(self.results) else 1


def main():
    p = argparse.ArgumentParser(description="平台 TCP 鏈路 — 全命令自動測試")
    p.add_argument('--host', default='127.0.0.1')
    p.add_argument('--port', type=int, default=47900, help="EdgeLink TCP Server 埠")
    p.add_argument('--hz', type=float, default=20.0, help="模擬器推送頻率")
    p.add_argument('--reconnect', type=float, default=25.0,
                   help="等 EdgeLink 重新撥號的秒數(心跳 5s + 重試)")
    args = p.parse_args()

    print(f"[測試] EdgeLink {args.host}:{args.port} — 每個階段會自己重開模擬器", flush=True)
    print("[測試] 請先把常駐的模擬器視窗關掉,否則 47802 會被佔住", flush=True)
    return Runner(args).run()


if __name__ == '__main__':
    sys.exit(main())
