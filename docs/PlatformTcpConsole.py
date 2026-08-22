# PlatformTcpConsole — 下游消費端(扮演 VR Client)
#
# 接 EdgeLink 的 TCP Server 埠,只講 KV 文字:收平台狀態、送平台命令。
# 二進位那一段完全由 EdgeLink 的 PlatformTcp mask 負責,這支程式看不到也不需要知道。
#
# 用法:
#   python PlatformTcpConsole.py --port 47900                     # 互動模式,直接打 KV 送出
#   python PlatformTcpConsole.py --port 47900 --watch 5           # 只看狀態 5 秒
#   python PlatformTcpConsole.py --port 47900 --script bringup    # 跑內建的上線驗證流程
#
# 需求:Python 3(只用標準函式庫)。

import argparse
import socket
import sys
import threading
import time

# 上線驗證流程:先急停再下移動命令 —— 平台一步都不會動,
# 但收到 rc:6 就證明整條編碼鏈路是通的(對方收到了、欄位解得出來、seq 被接受)。
SCRIPTS = {
    'bringup': [
        (0.5, "mt:17;act:1", "EStopOn — 期待 restxt:ACCEPT"),
        (0.5, "mt:16;mode:1;rqx:0;rqy:0;rqz:0;rqw:0;rhv:0;rqa:210;rqb:210;rqc:210",
              "急停中下移動 — 期待 restxt:REJECT rctxt:E-Stop生效中"),
        (0.5, "mt:17;act:2", "EStopOff"),
        (0.5, "mt:16;mode:1;rqx:0;rqy:0;rqz:0;rqw:0;rhv:0;rqa:210;rqb:210;rqc:210",
              "馬達未 Enable — 期待 REJECT rctxt:控制器不允許Move"),
        (0.5, "mt:17;act:3", "ServoToggle — 把馬達 Enable"),
        (0.5, "mt:16;mode:1;rqx:0;rqy:0;rqz:0;rqw:0;rhv:0;rqa:210;rqb:215;rqc:205",
              "小幅度移動 — 期待 ACCEPT,接著看 pa/pb/pc 追上 cpa/cpb/cpc"),
        (2.0, None, "等待追隨完成"),
        (0.5, "mt:16;mode:1;rqx:0;rqy:0;rqz:0;rqw:0;rhv:0;rqa:900;rqb:200;rqc:200",
              "超出行程界限 — 期待 REJECT rctxt:超出行程界限(mm) axistxt:A"),
        (0.5, "mt:16;mode:0;rqx:0;rqy:0.05;rqz:0;rqw:0.999;rhv:210;rqa:0;rqb:0;rqc:0",
              "四元數模式 — 期待 ACCEPT,三軸連動"),
        (2.0, None, "等待追隨完成"),
    ],
}


# 互動模式的快捷鍵 → 實際送出的 KV。打全文也可以,快捷鍵只是少打幾個字。
CHEATSHEET = {
    '1':  "mt:17;act:1",
    '2':  "mt:17;act:2",
    '3':  "mt:17;act:3",
    '4':  "mt:17;act:4",
    'up': "mt:16;mode:1;rqx:0;rqy:0;rqz:0;rqw:0;rhv:0;rqa:250;rqb:250;rqc:250",
    'dn': "mt:16;mode:1;rqx:0;rqy:0;rqz:0;rqw:0;rhv:0;rqa:180;rqb:180;rqc:180",
    'tilt': "mt:16;mode:1;rqx:0;rqy:0;rqz:0;rqw:0;rhv:0;rqa:260;rqb:240;rqc:240",
    'quat': "mt:16;mode:0;rqx:0;rqy:0.05;rqz:0;rqw:0.999;rhv:220;rqa:0;rqb:0;rqc:0",
    'over': "mt:16;mode:1;rqx:0;rqy:0;rqz:0;rqw:0;rhv:0;rqa:900;rqb:200;rqc:200",
}

HELP = """
── 指令 ──────────────────────────────────────────────────────────────────
  1 / 2       EStopOn / EStopOff
  3           ServoToggle(馬達 On/Off 切換,移動前要先 Enable)
  4           Reset(清除異常)

  up / dn     三軸一起到 250 / 180 mm
  tilt        前高後低 260/240/240 —— 看三軸分開走
  quat        四元數模式,對方解 IK
  over        故意超出行程 900mm —— 期待 REJECT axistxt:A

  s           印出目前平台狀態
  h           重印這張表
  q           離開

  也可以直接打完整的 KV,例如:
    mt:16;mode:1;rqx:0;rqy:0;rqz:0;rqw:0;rhv:0;rqa:230;rqb:230;rqc:230
──────────────────────────────────────────────────────────────────────────
"""

# 被拒時光看 reasonCode 還要自己想「那要打什麼」,直接把下一步印出來。
# 對照協定文件 §7.1。
RC_HINT = {
    '1':  "超出行程界限,看 axistxt 是哪一軸;模擬器預設行程 0-400mm",
    '2':  "換算後的場景單位超出界限",
    '4':  "對方還沒讀到 PLC 狀態,先看 plctxt 是不是 CONNECTED",
    '5':  "馬達沒 Enable → 打 3 (ServoToggle)",
    '6':  "急停生效中 → 打 2 (EStopOff);Reset(4) 不會解除急停",
    '7':  "PLC/驅動器有錯 → 看 derra/b/ctxt 與 gsttxt,再打 4 (Reset)",
    '10': "seq 倒退或重複 —— 通常是連線重建後對方沒重置期望值",
    '11': "對方關掉了 mode 1 → 改用 mode 0 送四元數",
    '12': "三軸組合還原不出 IK 姿態(例如零四元數)",
    '13': "數值不合法(NaN/Inf/超出可表示範圍)",
    '14': "velocity/acceleration/jerk 超出上限",
    '15': "對方 Unity 的輸入來源沒切到 External —— 這要對方那邊處理",
}

def parse_kv(line):
    out = {}
    for field in line.split(';'):
        k, sep, v = field.partition(':')
        if sep:
            out[k.strip()] = v.strip()
    return out


class Console:
    def __init__(self, args):
        self.args = args
        self.sock = socket.create_connection((args.host, args.port), timeout=5)
        self.sock.settimeout(0.2)
        self.buf = b''
        self.running = True
        self.latest = {}
        self.counts = {}
        self.lock = threading.Lock()

    def send(self, line):
        self.sock.sendall((line + '\n').encode('utf-8'))

    def reader(self):
        while self.running:
            try:
                data = self.sock.recv(65536)
                if not data:
                    print("[下游] EdgeLink 關閉連線", flush=True)
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
                self.on_line(raw.decode('utf-8', 'replace').strip())

    def on_line(self, line):
        if not line:
            return
        # EdgeLink 的應用層心跳:沒回 PONG 連續 3 次會被踢掉
        if line.startswith('EDGELINK_PING:'):
            self.send('EDGELINK_PONG:' + line.split(':', 1)[1])
            return
        if line.startswith('EDGELINK_'):
            return

        kv = parse_kv(line)
        mt = kv.get('mt', '?')
        with self.lock:
            self.counts[mt] = self.counts.get(mt, 0) + 1
            self.latest[mt] = kv

        # 19 是持續推送,全印會洗版;18 是每筆命令的回應,一定要看
        if mt == '18':
            print(f"  → 18 ack={kv.get('ackseq')} {kv.get('restxt')} "
                  f"rc={kv.get('rc')}({kv.get('rctxt')}) axis={kv.get('axistxt')}", flush=True)
            hint = RC_HINT.get(kv.get('rc', ''))
            if hint and kv.get('restxt') == 'REJECT':
                print(f"       ↳ {hint}", flush=True)
        elif mt == '19' and self.args.all:
            print(f"  → 19 {line}", flush=True)

    def status_line(self):
        with self.lock:
            s = self.latest.get('19')
        if not s:
            return "(還沒收到平台狀態)"
        return (f"plc={s.get('plctxt')} gst={s.get('gsttxt')} estop={s.get('estop')} "
                f"ec={s.get('ecerrtxt')} derr={s.get('derratxt')}/{s.get('derrbtxt')}/{s.get('derrctxt')}\n"
                f"    實際 pa={s.get('pa')} pb={s.get('pb')} pc={s.get('pc')}\n"
                f"    命令 cpa={s.get('cpa')} cpb={s.get('cpb')} cpc={s.get('cpc')}\n"
                f"    姿態 q=({s.get('qx')},{s.get('qy')},{s.get('qz')},{s.get('qw')}) "
                f"hv={s.get('hv')} resid={s.get('resid')} ack={s.get('ack')}")

    def run(self):
        threading.Thread(target=self.reader, daemon=True).start()
        print(f"[下游] 已連上 EdgeLink {self.args.host}:{self.args.port}", flush=True)

        if self.args.script:
            self.run_script(SCRIPTS[self.args.script])
        elif self.args.watch is not None:
            # 用 is not None 判斷:--watch 0(一直看)傳進來是 0.0,
            # 拿它當布林會是 falsy,整個掉進互動模式 —— 視窗看起來開了卻在等你打字。
            self.watch(self.args.watch)
        else:
            self.interactive()

        self.running = False
        with self.lock:
            summary = ', '.join(f"mt:{k}×{v}" for k, v in sorted(self.counts.items()))
        print(f"\n[下游] 收到 {summary or '(無)'}", flush=True)
        print("[下游] 最後狀態:\n    " + self.status_line(), flush=True)

    def run_script(self, steps):
        for delay, cmd, note in steps:
            time.sleep(delay)
            if cmd:
                print(f"\n[下游] 送出 {cmd}\n       ({note})", flush=True)
                self.send(cmd)
            else:
                print(f"\n[下游] {note}", flush=True)
            time.sleep(0.4)
            if not cmd:
                print("    " + self.status_line(), flush=True)
        time.sleep(0.5)

    def watch(self, seconds):
        """只在數值變動時才印。平台停著時安靜,一動就逐格印出來 ——
        追隨過程只有幾秒,靠手按 s 幾乎抓不到。seconds<=0 = 一直看下去。"""
        end = time.time() + seconds if seconds > 0 else float('inf')
        last_key = None
        last_beat = 0.0

        while time.time() < end and self.running:
            time.sleep(0.1)
            with self.lock:
                st = self.latest.get('19')
            if not st:
                continue

            # 只比對「會變才有意義」的欄位;ts/seq 每包都變,拿來比對等於每包都印
            key = tuple(st.get(k) for k in (
                'pa', 'pb', 'pc', 'cpa', 'cpb', 'cpc',
                'gsttxt', 'plctxt', 'estop', 'ecerrtxt',
                'derratxt', 'derrbtxt', 'derrctxt', 'ack'))

            now = time.time()
            if key != last_key:
                last_key = key
                last_beat = now
                print("\n[" + time.strftime('%H:%M:%S') + "] " + self.status_line(), flush=True)
            elif now - last_beat >= 30:
                last_beat = now
                print("[" + time.strftime('%H:%M:%S') + "] (無變化)", flush=True)

    def interactive(self):
        self.help()
        try:
            for line in sys.stdin:
                line = line.strip()
                if not line:
                    continue
                if line in ('q', 'quit', 'exit'):
                    break
                if line in ('h', '?'):
                    self.help()
                    continue
                if line == 's':
                    print("    " + self.status_line(), flush=True)
                    continue
                if line in CHEATSHEET:
                    line = CHEATSHEET[line]
                    print(f"       ({line})", flush=True)
                self.send(line)
        except KeyboardInterrupt:
            pass

    @staticmethod
    def help():
        print(HELP, flush=True)


def main():
    p = argparse.ArgumentParser(description="EdgeLink 平台 TCP — 下游消費端")
    p.add_argument('--host', default='127.0.0.1')
    p.add_argument('--port', type=int, required=True, help="EdgeLink TCP Server 埠")
    p.add_argument('--watch', type=float, help="監看狀態幾秒(0 = 一直看);只在數值變動時才印")
    p.add_argument('--script', choices=sorted(SCRIPTS), help="跑內建流程")
    p.add_argument('--all', action='store_true', help="連 mt:19 每一筆都印出來")
    args = p.parse_args()

    try:
        Console(args).run()
    except ConnectionRefusedError:
        print(f"[下游] 連不上 {args.host}:{args.port} — EdgeLink 的 TCP Server 埠開了嗎?", flush=True)
        return 1
    return 0


if __name__ == '__main__':
    sys.exit(main())
