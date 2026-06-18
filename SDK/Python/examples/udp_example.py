"""EdgeLink UDP 傳送範例 — 每 3 秒傳送一個 UDP 封包"""

import asyncio
from edgelink import EdgeLinkUdpSender

EDGELINK_HOST = "192.168.1.100"
EDGELINK_PORT = 9002


async def main() -> None:
    print("[EdgeLink] Sending UDP packets every 3s. Ctrl+C to stop.")
    with EdgeLinkUdpSender() as sender:
        try:
            while True:
                sender.send(EDGELINK_HOST, EDGELINK_PORT, "id:PYTHON_01;temp:25.3;humidity:60.0")
                print("[EdgeLink] UDP sent")
                await asyncio.sleep(3)
        except KeyboardInterrupt:
            pass


if __name__ == "__main__":
    asyncio.run(main())
