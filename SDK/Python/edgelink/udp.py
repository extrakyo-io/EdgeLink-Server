import asyncio
import socket
from collections import deque
from typing import Callable


class _UdpReceiverProtocol(asyncio.DatagramProtocol):
    def __init__(self, on_receive: Callable[[bytes, tuple], None]) -> None:
        self._on_receive = on_receive

    def datagram_received(self, data: bytes, addr: tuple) -> None:
        self._on_receive(data, addr)

    def error_received(self, exc: Exception) -> None:
        pass


class EdgeLinkUdpClient:
    """UDP receiver — binds to a local port and fires callbacks for each received packet."""

    def __init__(self, local_port: int) -> None:
        self.local_port   = local_port
        self._on_message:       list[Callable[[str], None]] = []
        self._on_error:         list[Callable[[Exception], None]] = []
        self._on_device_status: list[Callable[[bool, str, str], None]] = []
        self._queue:      deque[str] = deque()
        self._transport:  asyncio.BaseTransport | None = None
        self.is_running   = False

    def on_message(self, cb: Callable[[str], None]) -> None:
        self._on_message.append(cb)

    def on_error(self, cb: Callable[[Exception], None]) -> None:
        self._on_error.append(cb)

    def on_device_status(self, cb: Callable[[bool, str, str], None]) -> None:
        """cb(is_connected: bool, endpoint: str, device_id: str) — fired when an upstream device starts/stops sending packets (timeout-based)."""
        self._on_device_status.append(cb)

    async def start(self) -> None:
        loop = asyncio.get_running_loop()
        self._transport, _ = await loop.create_datagram_endpoint(
            lambda: _UdpReceiverProtocol(self._receive),
            local_addr=("0.0.0.0", self.local_port),
        )
        self.is_running = True

    def stop(self) -> None:
        if self._transport:
            self._transport.close()
        self.is_running = False

    def try_dequeue(self) -> str | None:
        return self._queue.popleft() if self._queue else None

    def _receive(self, data: bytes, _addr: tuple) -> None:
        msg = data.decode(errors="replace").strip()
        if not msg:
            return

        if msg.startswith("EDGELINK_STATUS:"):
            # body: "STATUS:protocol@ip" or "STATUS:protocol@ip:deviceId"
            body      = msg[16:]
            sep       = body.find(":")
            status    = body[:sep] if sep >= 0 else body
            rest      = body[sep + 1:] if sep >= 0 else ""
            connected = status.upper() == "CONNECTED"
            dev_sep   = rest.rfind(":")
            endpoint  = rest[:dev_sep]      if dev_sep >= 0 else rest
            device_id = rest[dev_sep + 1:]  if dev_sep >= 0 else ""
            for cb in self._on_device_status:
                cb(connected, endpoint, device_id)
            return
        if msg.startswith("EDGELINK_"):
            return

        self._queue.append(msg)
        for cb in self._on_message:
            cb(msg)


class EdgeLinkUdpSender:
    """UDP sender — send-only, no local port binding required."""

    def __init__(self) -> None:
        self._sock = socket.socket(socket.AF_INET, socket.SOCK_DGRAM)

    def send(self, host: str, port: int, message: str) -> None:
        self._sock.sendto(message.encode(), (host, port))

    async def send_async(self, host: str, port: int, message: str) -> None:
        loop = asyncio.get_running_loop()
        await loop.run_in_executor(None, self.send, host, port, message)

    def close(self) -> None:
        self._sock.close()

    def __enter__(self):
        return self

    def __exit__(self, *_):
        self.close()
