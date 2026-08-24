"""實跑平台 TCP 鏈路的**雙向**流(消防訓練平台 TCP 資料格式 V1)。

拓撲(從跑著的 EdgeLink 讀出來的):

    本程式 ──KV──▶ EdgeLink TCP Server :47900 (OriginalData)
                        │ SourceProtocolId 路由
                        ▼
                   TCP Client → 127.0.0.1:47802 (PlatformTcp 編碼)
                        │
                        ▼  你的 PlatformTcpSimulator
                   回覆二進位 → EdgeLink 用 PlatformTcp 解碼 → 送回 :47900

送出方向(VR → 平台)與回覆方向(平台 → VR)都會經過正式的編/解碼器。
不動你正在跑的模擬器 —— 只是接上下游那一端說話。
"""
#
# 與 PlatformTcpTestAll.py 的差別:那支要先關掉常駐模擬器、自己開關;這支只是
# 接上下游那一端說話,**可以在模擬器仍掛著跑的時候直接執行**。
#
#   python docs/PlatformTcpChainTest.py

import socket
import sys
import time

VR_PORT = 47900
FAIL = []


def check(name, ok, detail=''):
    print(('  PASS  ' if ok else '  FAIL  ') + name + ('  -- ' + detail if detail else ''))
    if not ok:
        FAIL.append(name)


def parse(line):
    kv = {}
    for f in line.split(';'):
        k, sep, v = f.partition(':')
        if sep:
            kv[k.strip()] = v.strip()
    return kv


class Link:
    def __init__(self):
        self.sock = socket.create_connection(('127.0.0.1', VR_PORT), timeout=5)
        self.sock.settimeout(0.3)
        self.buf = ''

    def close(self):
        try:
            self.sock.close()
        except Exception:
            pass

    def send(self, kv_line):
        self.sock.sendall((kv_line + '\n').encode())

    def lines(self, seconds):
        """收 seconds 秒內的所有完整行。"""
        out, deadline = [], time.time() + seconds
        while time.time() < deadline:
            try:
                chunk = self.sock.recv(65536)
            except socket.timeout:
                continue
            if not chunk:
                break
            self.buf += chunk.decode('utf-8', 'replace')
            while '\n' in self.buf:
                line, self.buf = self.buf.split('\n', 1)
                line = line.strip()
                if line:
                    out.append(line)
        return out

    def await_ack(self, ackmt, seconds=4.0):
        """等一筆回應指定 msgType 的 mt:18。"""
        deadline = time.time() + seconds
        while time.time() < deadline:
            for line in self.lines(0.4):
                kv = parse(line)
                if kv.get('mt') == '18' and kv.get('ackmt') == str(ackmt):
                    return kv
        return None


def move_cmd(mode=1, vel=50, acc=50, jrk=50):
    return (f"mt:16;mode:{mode};rqx:0;rqy:0;rqz:0;rqw:0;rhv:0;"
            f"rqa:0;rqb:0;rqc:0;vel:{vel};acc:{acc};jrk:{jrk}")


link = Link()
print('已連上 EdgeLink 的下游埠 :%d' % VR_PORT)
print()

print('--- 平台 -> VR:msgType 19 平台狀態(解碼方向) ---')
seen = [parse(l) for l in link.lines(2.0)]
status = [k for k in seen if k.get('mt') == '19']
check('有收到平台狀態', len(status) > 0, '%d 筆 / 2 秒' % len(status))
if status:
    s = status[-1]
    check('帶 seq', s.get('seq', '').isdigit(), s.get('seq'))
    check('帶 estop 欄位', 'estop' in s, str('estop' in s))
    check('查表欄位有解成文字', bool(s.get('plctxt')), '%s -> %s' % (s.get('plc'), s.get('plctxt')))
    check('姿態數值解得出來', 'pa' in s, ','.join(k for k in ('pa', 'pb', 'pc') if k in s))

print()
print('--- VR -> 平台:msgType 16 移動命令(編碼方向)-> mt:18 回覆 ---')
link.send(move_cmd(mode=1))
ack = link.await_ack(16)
check('移動命令有收到 mt:18 回覆', ack is not None)
if ack:
    check('ackmt 指回 16', ack.get('ackmt') == '16', ack.get('ackmt'))
    check('ackseq 是數字', ack.get('ackseq', '').isdigit(), ack.get('ackseq'))
    check('res 有值', ack.get('res') is not None, ack.get('res'))
    check('res 有解成文字', bool(ack.get('restxt')), '%s -> %s' % (ack.get('res'), ack.get('restxt')))
    check('rc 有解成文字', 'rctxt' in ack, '%s -> %s' % (ack.get('rc'), ack.get('rctxt')))

print()
print('--- VR -> 平台:msgType 17 管理命令 ---')
link.send('mt:17;act:1')
ack = link.await_ack(17)
check('管理命令有收到 mt:18 回覆', ack is not None)
if ack:
    check('ackmt 指回 17', ack.get('ackmt') == '17', ack.get('ackmt'))

print()
print('--- 編碼防呆:未知 msgType ---')
link.lines(0.5)
link.send('mt:99;mode:1')
ghost = link.await_ack(99, seconds=1.5)
check('未知 msgType 不會產生回覆', ghost is None, str(ghost))

link.send(move_cmd(mode=1))
check('鏈路在被拒的命令之後仍正常', link.await_ack(16) is not None)

print()
print('--- seq:每個 msgType 各一條 counter ---')
seqs = []
for _ in range(3):
    link.send(move_cmd(mode=1))
    a = link.await_ack(16)
    if a and a.get('ackseq', '').isdigit():
        seqs.append(int(a['ackseq']))
check('連續三筆命令的 seq 遞增',
      len(seqs) == 3 and seqs == sorted(seqs) and len(set(seqs)) == 3, str(seqs))

link.close()
print()
print('FAILED: ' + (', '.join(FAIL) if FAIL else '(none)'))
sys.exit(1 if FAIL else 0)
