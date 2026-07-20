#!/usr/bin/env python3
"""
持續送字串到 EdgeLink 的埠(預設 127.0.0.1:1234)。純標準函式庫,無相依。

TCP(預設):
  - 連上 EdgeLink 的 TCP Server 埠,每隔一段時間送一行字串。
  - 會自動回應 EdgeLink 的心跳(收到 EDGELINK_PING 就回 EDGELINK_PONG),
    否則約 15 秒沒回會被伺服器斷線。
  - 斷線自動重連。
UDP(--udp):
  - 無連線、fire-and-forget,不需要心跳。

用法:
  python string_sender.py                       # TCP 127.0.0.1:1234, 1 Hz
  python string_sender.py --hz 50               # 每秒 50 筆
  python string_sender.py --udp                 # 改走 UDP
  python string_sender.py --host 192.168.1.10 --port 1234 --id rig1
  python string_sender.py --msg "id:test;temp:25.3;humid:60"   # 自訂整行(不自動加 seq)
"""
import argparse
import socket
import sys
import threading
import time

PING = "EDGELINK_PING:"
PONG = "EDGELINK_PONG:"


def build_line(args, seq):
    if args.msg:
        line = args.msg
    else:
        line = f"id:{args.id};seq:{seq};ts:{int(time.time() * 1000)};msg:hello"
    return line if line.endswith("\n") else line + "\n"


def run_udp(args):
    sock = socket.socket(socket.AF_INET, socket.SOCK_DGRAM)
    addr = (args.host, args.port)
    interval = 1.0 / max(args.hz, 0.001)
    seq = 0
    print(f"[udp] 送往 {args.host}:{args.port}, {args.hz} Hz")
    try:
        while True:
            seq += 1
            line = build_line(args, seq)
            sock.sendto(line.encode("utf-8"), addr)
            print(f"[sent] {line.strip()}")
            time.sleep(interval)
    except KeyboardInterrupt:
        pass
    finally:
        sock.close()


def pong_loop(sock, send_lock, stop):
    """讀 socket,收到 EDGELINK_PING 就回 PONG;對端關閉則結束。"""
    buf = b""
    try:
        while not stop.is_set():
            data = sock.recv(4096)
            if not data:
                break
            buf += data
            while b"\n" in buf:
                raw, buf = buf.split(b"\n", 1)
                line = raw.decode("utf-8", "replace").strip()
                if line.startswith(PING):
                    token = line[len(PING):]
                    with send_lock:
                        sock.sendall((PONG + token + "\n").encode("ascii"))
    except OSError:
        pass
    finally:
        stop.set()


def run_tcp(args):
    interval = 1.0 / max(args.hz, 0.001)
    seq = 0
    while True:
        try:
            with socket.create_connection((args.host, args.port), timeout=5) as sock:
                sock.settimeout(None)   # 連上後改回阻塞:否則 recv 在 PING 間隔(每5s)會逾時而誤判斷線
                sock.setsockopt(socket.IPPROTO_TCP, socket.TCP_NODELAY, 1)
                print(f"[connected] {args.host}:{args.port}  ({args.hz} Hz)")
                send_lock = threading.Lock()
                stop = threading.Event()
                threading.Thread(target=pong_loop, args=(sock, send_lock, stop), daemon=True).start()

                while not stop.is_set():
                    seq += 1
                    line = build_line(args, seq)
                    with send_lock:
                        sock.sendall(line.encode("utf-8"))
                    print(f"[sent] {line.strip()}")
                    time.sleep(interval)
        except KeyboardInterrupt:
            print("\n[bye]")
            return
        except (ConnectionError, OSError) as e:
            print(f"[disconnected] {e} — 3 秒後重連", file=sys.stderr)
            time.sleep(3)


def main():
    ap = argparse.ArgumentParser(description="持續送字串到 EdgeLink")
    ap.add_argument("--host", default="127.0.0.1")
    ap.add_argument("--port", type=int, default=1234)
    ap.add_argument("--hz", type=float, default=1.0, help="每秒送幾筆(預設 1)")
    ap.add_argument("--id", default="test", help="自動組行時的 id 欄位(預設 test)")
    ap.add_argument("--msg", default="", help="自訂整行內容;給了就不自動加 seq/ts")
    ap.add_argument("--udp", action="store_true", help="改走 UDP(不需心跳)")
    args = ap.parse_args()

    if args.udp:
        run_udp(args)
    else:
        run_tcp(args)


if __name__ == "__main__":
    main()
