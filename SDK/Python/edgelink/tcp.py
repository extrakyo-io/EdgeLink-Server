import asyncio
from collections import deque
from typing import Callable


class EdgeLinkClient:
    """TCP client — connects to EdgeLink Server, handles PING/PONG, fires callbacks on messages."""

    def __init__(self, host: str, port: int) -> None:
        self.host = host
        self.port = port
        self._auto_reconnect   = True
        self._reconnect_delay  = 5.0
        self._on_message:       list[Callable[[str], None]] = []
        self._on_connected:     list[Callable[[], None]]    = []
        self._on_disconnected:  list[Callable[[], None]]    = []
        self._on_error:         list[Callable[[Exception], None]] = []
        self._on_device_status: list[Callable[[bool, str, str], None]] = []
        self._queue:   deque[str] = deque()
        self._writer:  asyncio.StreamWriter | None = None
        self._task:    asyncio.Task | None = None
        self._running  = False

    # ── configuration ──────────────────────────────────────────────────────────

    def set_auto_reconnect(self, enable: bool, delay: float = 5.0) -> None:
        self._auto_reconnect  = enable
        self._reconnect_delay = delay

    def on_message(self, cb: Callable[[str], None]) -> None:
        self._on_message.append(cb)

    def on_connected(self, cb: Callable[[], None]) -> None:
        self._on_connected.append(cb)

    def on_disconnected(self, cb: Callable[[], None]) -> None:
        self._on_disconnected.append(cb)

    def on_error(self, cb: Callable[[Exception], None]) -> None:
        self._on_error.append(cb)

    def on_device_status(self, cb: Callable[[bool, str, str], None]) -> None:
        """cb(is_connected: bool, endpoint: str, device_id: str) — fired when an upstream device connects/disconnects.
        device_id is parsed from the message id field; may be empty if no message has identified the device yet."""
        self._on_device_status.append(cb)

    # ── public API ─────────────────────────────────────────────────────────────

    @property
    def is_connected(self) -> bool:
        return self._writer is not None and not self._writer.is_closing()

    async def connect(self) -> None:
        self._running = True
        self._task    = asyncio.create_task(self._read_loop())

    async def send(self, message: str) -> None:
        if not self.is_connected or self._writer is None:
            raise RuntimeError("Not connected to EdgeLink.")
        if not message.endswith("\n"):
            message += "\n"
        self._writer.write(message.encode())
        await self._writer.drain()

    def try_dequeue(self) -> str | None:
        return self._queue.popleft() if self._queue else None

    async def disconnect(self) -> None:
        self._running = False
        if self._writer:
            self._writer.close()
            try:
                await self._writer.wait_closed()
            except Exception:
                pass
            self._writer = None
        if self._task:
            self._task.cancel()
            try:
                await self._task
            except asyncio.CancelledError:
                pass

    # ── internal ───────────────────────────────────────────────────────────────

    async def _connect_core(self) -> asyncio.StreamReader:
        reader, self._writer = await asyncio.open_connection(self.host, self.port)
        for cb in self._on_connected:
            cb()
        return reader

    async def _read_loop(self) -> None:
        reader: asyncio.StreamReader | None = None

        while self._running:
            try:
                reader = await self._connect_core()
                buf = b""
                while self._running:
                    chunk = await reader.read(4096)
                    if not chunk:
                        break
                    buf += chunk
                    while b"\n" in buf:
                        line_bytes, buf = buf.split(b"\n", 1)
                        line = line_bytes.decode(errors="replace").strip()
                        if line:
                            self._handle_line(line)

            except asyncio.CancelledError:
                return
            except Exception as ex:
                for cb in self._on_error:
                    cb(ex)

            if self._writer:
                self._writer.close()
                self._writer = None

            for cb in self._on_disconnected:
                cb()

            if not self._auto_reconnect or not self._running:
                return

            await asyncio.sleep(self._reconnect_delay)

    def _handle_line(self, line: str) -> None:
        if line.startswith("EDGELINK_PING:"):
            hex_val = line[14:]
            if self._writer and not self._writer.is_closing():
                self._writer.write(f"EDGELINK_PONG:{hex_val}\n".encode())
            return
        if line.startswith("EDGELINK_STATUS:"):
            # body: "STATUS:protocol@ip" or "STATUS:protocol@ip:deviceId"
            body      = line[16:]
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
        if line.startswith("EDGELINK_"):
            return

        self._queue.append(line)
        for cb in self._on_message:
            cb(line)


class EdgeLinkTcpListener:
    """TCP listener — accepts incoming connections from EdgeLink Server, handles PING/PONG."""

    def __init__(self, local_port: int) -> None:
        self.local_port     = local_port
        self._on_message:       list[Callable[[str], None]] = []
        self._on_connected:     list[Callable[[], None]]    = []
        self._on_disconnected:  list[Callable[[], None]]    = []
        self._on_error:         list[Callable[[Exception], None]] = []
        self._on_device_status: list[Callable[[bool, str, str], None]] = []
        self._queue:   deque[str] = deque()
        self._server:  asyncio.Server | None = None
        # 已接受、還沒斷的對端。send() 要寫給它們,stop() 要主動關掉它們 ——
        # 只關 server 不會關掉已建立的連線。
        self._writers: set[asyncio.StreamWriter] = set()
        self.is_running = False

    @property
    def connection_count(self) -> int:
        """目前連進來的對端數(通常只有 EdgeLink 一條)。"""
        return len(self._writers)

    def on_message(self, cb: Callable[[str], None]) -> None:
        self._on_message.append(cb)

    def on_connected(self, cb: Callable[[], None]) -> None:
        self._on_connected.append(cb)

    def on_disconnected(self, cb: Callable[[], None]) -> None:
        self._on_disconnected.append(cb)

    def on_error(self, cb: Callable[[Exception], None]) -> None:
        self._on_error.append(cb)

    def on_device_status(self, cb: Callable[[bool, str, str], None]) -> None:
        """cb(is_connected: bool, endpoint: str, device_id: str) — fired when an upstream device connects/disconnects.
        device_id is parsed from the message id field; may be empty if no message has identified the device yet."""
        self._on_device_status.append(cb)

    async def start(self) -> None:
        self._server  = await asyncio.start_server(self._handle_client, "0.0.0.0", self.local_port)
        self.is_running = True
        await self._server.start_serving()

    async def stop(self) -> None:
        if self._server:
            self._server.close()
            await self._server.wait_closed()

        # 關 server 只是不再 accept,已建立的連線還開著 —— 要主動收掉,
        # 否則 socket 會殘留到 OS 端,反覆 start/stop 就是累積。
        for writer in list(self._writers):
            try:
                writer.close()
            except Exception:
                pass
        self._writers.clear()
        self.is_running = False

    async def send(self, line: str) -> bool:
        """送一行 KV 給所有已連線的對端(自動補換行)。沒有任何連線時回 False。

        對端是 EdgeLink 的 TCP Client 埠連進來,所以「送」= 回應 EdgeLink。

        這裡刻意**不**加每條連線的寫入鎖:asyncio 的 StreamWriter.write() 是同步把
        整段 bytes 追加進緩衝區,單次呼叫具原子性 —— 兩個 coroutine 各呼叫一次不會把
        彼此的內容切開。這與 .NET 的 NetworkStream 不同(那邊併發 WriteAsync 會真的
        交錯,所以 C#/Unity 版才需要 SemaphoreSlim)。
        """
        if not line:
            return False
        if not line.endswith("\n"):
            line += "\n"
        return await self.send_bytes(line.encode())

    async def send_bytes(self, data: bytes) -> bool:
        """送原始位元組給所有已連線的對端,不附加換行。"""
        if not data:
            return False
        sent = False
        for writer in list(self._writers):
            try:
                writer.write(data)
                await writer.drain()
                sent = True
            except Exception as ex:
                for cb in self._on_error:
                    cb(ex)
        return sent

    def try_dequeue(self) -> str | None:
        return self._queue.popleft() if self._queue else None

    async def _handle_client(self, reader: asyncio.StreamReader, writer: asyncio.StreamWriter) -> None:
        # 先登記再通知 —— 否則 on_connected 的回呼裡呼叫 send() 會漏掉這條連線
        self._writers.add(writer)
        for cb in self._on_connected:
            cb()
        buf = b""
        try:
            while True:
                chunk = await reader.read(4096)
                if not chunk:
                    break
                buf += chunk
                while b"\n" in buf:
                    line_bytes, buf = buf.split(b"\n", 1)
                    line = line_bytes.decode(errors="replace").strip()
                    if line:
                        await self._handle_line(line, writer)
        except Exception as ex:
            for cb in self._on_error:
                cb(ex)
        finally:
            self._writers.discard(writer)
            try:
                writer.close()
            except Exception:
                pass
            for cb in self._on_disconnected:
                cb()

    async def _handle_line(self, line: str, writer: asyncio.StreamWriter) -> None:
        if line.startswith("EDGELINK_PING:"):
            hex_val = line[14:]
            try:
                writer.write(f"EDGELINK_PONG:{hex_val}\n".encode())
                await writer.drain()
            except Exception:
                pass
            return
        if line.startswith("EDGELINK_STATUS:"):
            # body: "STATUS:protocol@ip" or "STATUS:protocol@ip:deviceId"
            body      = line[16:]
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
        if line.startswith("EDGELINK_"):
            return

        self._queue.append(line)
        for cb in self._on_message:
            cb(line)
