"""EdgeLink TCP 連線範例 — 每 3 秒傳送一筆感測資料"""

import asyncio
from edgelink import EdgeLinkClient

EDGELINK_HOST = "192.168.1.100"
EDGELINK_PORT = 9001


async def main() -> None:
    client = EdgeLinkClient(EDGELINK_HOST, EDGELINK_PORT)

    client.on_connected(lambda: print("[EdgeLink] Connected"))
    client.on_disconnected(lambda: print("[EdgeLink] Disconnected"))
    client.on_message(lambda msg: print(f"[EdgeLink] Received: {msg}"))
    client.on_error(lambda ex: print(f"[EdgeLink] Error: {ex}"))

    client.set_auto_reconnect(True, delay=5.0)

    await client.connect()

    try:
        while True:
            await asyncio.sleep(3)
            if client.is_connected:
                await client.send("id:PYTHON_01;temp:25.3;humidity:60.0")
                print("[EdgeLink] Sent sensor data")
    except KeyboardInterrupt:
        pass
    finally:
        await client.disconnect()


if __name__ == "__main__":
    asyncio.run(main())
