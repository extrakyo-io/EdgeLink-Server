"""EdgeLinkTcpListener 的送出與生命週期。

只用標準函式庫(unittest.IsolatedAsyncioTestCase),與 SDK 本身「不依賴外部套件」
的宣稱一致。跑法:

    cd SDK/Python && python -m unittest discover -s tests -v

全部走真的 socket。這個類別的價值就在於它與事件迴圈、OS 的互動 ——
mock 掉就等於什麼都沒測。
"""

import asyncio
import socket
import sys
import unittest
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parents[1]))

from edgelink.tcp import EdgeLinkTcpListener  # noqa: E402


def free_port() -> int:
    s = socket.socket()
    s.bind(("127.0.0.1", 0))
    port = s.getsockname()[1]
    s.close()
    return port


class TcpListenerTests(unittest.IsolatedAsyncioTestCase):
    async def asyncSetUp(self) -> None:
        self.port = free_port()
        self.listener = EdgeLinkTcpListener(self.port)
        self._writers: list[asyncio.StreamWriter] = []

    async def asyncTearDown(self) -> None:
        for w in self._writers:
            try:
                w.close()
            except Exception:
                pass
        try:
            await asyncio.wait_for(self.listener.stop(), timeout=5)
        except Exception:
            pass

    async def peer(self) -> tuple[asyncio.StreamReader, asyncio.StreamWriter]:
        reader, writer = await asyncio.open_connection("127.0.0.1", self.port)
        self._writers.append(writer)
        return reader, writer

    # ── 生命週期 ────────────────────────────────────────────────────────────

    async def test_尚未start時is_running為false(self):
        self.assertFalse(self.listener.is_running)
        self.assertEqual(0, self.listener.connection_count)

    async def test_連線活著時stop不會卡住(self):
        """先前 stop() 在還有連線時**永遠不返回**。

        Python 3.12.1 起 Server.wait_closed() 會等所有 handler 任務結束,而 handler
        卡在 `await reader.read()`,要等 writer 關掉才結束 —— 而關 writer 的程式碼
        排在 wait_closed() 後面。stop() 於是在等一件只有自己後面那段能促成的事。

        這是正常路徑(伺服器連著的時候關掉監聽),不是邊角情境。
        """
        await self.listener.start()
        await self.peer()
        await asyncio.sleep(0.2)
        self.assertEqual(1, self.listener.connection_count)

        try:
            await asyncio.wait_for(self.listener.stop(), timeout=5)
        except asyncio.TimeoutError:
            self.fail("stop() 在有連線時沒有返回 —— wait_closed() 與關閉連線的順序反了")

        self.assertFalse(self.listener.is_running)

    async def test_stop會主動關閉已接受的連線(self):
        await self.listener.start()
        reader, _ = await self.peer()
        await asyncio.sleep(0.2)

        await asyncio.wait_for(self.listener.stop(), timeout=5)

        # 對端讀到 b"" = server 真的送了 FIN
        data = await asyncio.wait_for(reader.read(100), timeout=3)
        self.assertEqual(b"", data)
        self.assertEqual(0, self.listener.connection_count)

    async def test_stop之後埠已釋放(self):
        await self.listener.start()
        await self.peer()
        await asyncio.sleep(0.2)
        await asyncio.wait_for(self.listener.stop(), timeout=5)
        await asyncio.sleep(0.2)

        probe = socket.socket()
        try:
            probe.bind(("127.0.0.1", self.port))   # 綁得起來 = 舊 socket 收乾淨了
        finally:
            probe.close()

    # ── 送出 ────────────────────────────────────────────────────────────────

    async def test_沒有連線時送出回false(self):
        await self.listener.start()
        self.assertFalse(await self.listener.send("cmd:noop"))
        self.assertFalse(await self.listener.send_bytes(b"\x01"))

    async def test_送出會寫給每一條連線(self):
        await self.listener.start()
        r1, _ = await self.peer()
        r2, _ = await self.peer()
        await asyncio.sleep(0.2)
        self.assertEqual(2, self.listener.connection_count)

        self.assertTrue(await self.listener.send("cmd:start;v:1"))

        for reader in (r1, r2):
            data = await asyncio.wait_for(reader.read(256), timeout=3)
            self.assertIn("cmd:start;v:1", data.decode())

    async def test_send_bytes不附加換行(self):
        await self.listener.start()
        reader, _ = await self.peer()
        await asyncio.sleep(0.2)

        self.assertTrue(await self.listener.send_bytes(b"\xaa\xbb\x01\x02"))
        data = await asyncio.wait_for(reader.read(16), timeout=3)
        self.assertEqual(b"\xaa\xbb\x01\x02", data)

    async def test_每一條寫出都失敗時送出要回false(self):
        """回傳值必須代表「至少一條**真的**寫出去了」,與 C# 版的 `return any` 一致。

        對端送 RST 之後 `StreamWriter.write()` **不會拋例外** —— 它只是靜靜把資料
        丟掉,錯誤要到 `drain()` 才浮出來。只看「有沒有對象可寫」就回 True 的話,
        呼叫端會把一筆從沒送達的控制指令當成成功:不重送、不告警。

        這裡用假的 writer 而不是真的 RST 一條 socket:在 Windows 上 reader 會立刻
        收到 ConnectionResetError,handler 的 finally 搶在送出之前就把 writer 移除了
        —— 那樣測出來的 False 是「沒有對象」造成的,換掉回傳值也照樣通過,
        等於什麼都沒測(這條測試的第一版就是這樣寫的)。
        """
        class FailingWriter:
            def __init__(self) -> None:
                self.written: list[bytes] = []

            def write(self, data: bytes) -> None:
                self.written.append(data)          # 與真實行為一致:不拋例外

            async def drain(self) -> None:
                raise ConnectionResetError("模擬對端 RST")

            def close(self) -> None:
                pass

        errors: list[Exception] = []
        self.listener.on_error(errors.append)
        await self.listener.start()

        writer = FailingWriter()
        self.listener._writers.add(writer)         # type: ignore[arg-type]
        try:
            self.assertEqual(1, self.listener.connection_count)
            result = await self.listener.send("cmd:estop")
            self.assertIs(False, result, "每一條寫出都失敗,送出卻回報成功")
            self.assertTrue(writer.written, "資料根本沒交給 writer")
            self.assertTrue(any(isinstance(e, ConnectionResetError) for e in errors),
                            "寫出失敗沒有轉交給 on_error")
        finally:
            self.listener._writers.discard(writer)  # type: ignore[arg-type]

    async def test_部分對端失敗時送出仍回true(self):
        """一條壞掉不代表整批失敗 —— 只要還有人收得到就是 True。"""
        class FailingWriter:
            def write(self, data: bytes) -> None:
                pass

            async def drain(self) -> None:
                raise ConnectionResetError("模擬對端 RST")

            def close(self) -> None:
                pass

        await self.listener.start()
        reader, _ = await self.peer()
        await asyncio.sleep(0.2)

        broken = FailingWriter()
        self.listener._writers.add(broken)          # type: ignore[arg-type]
        try:
            self.assertIs(True, await self.listener.send("cmd:start"))
            data = await asyncio.wait_for(reader.read(256), timeout=3)
            self.assertIn("cmd:start", data.decode())
        finally:
            self.listener._writers.discard(broken)  # type: ignore[arg-type]

    async def test_async回呼會真的執行(self):
        """`async def` 的 on_connected 回呼先前**一行都不會跑**。

        `cb()` 只是產生一個沒人 await 的 coroutine 物件,Python 丟一句 RuntimeWarning
        就算了。而 send() 本身是 coroutine —— 「在 on_connected 裡送出握手訊息」
        這個最自然的用法因此完全失效,即使程式碼特地把 writer 登記在通知之前。
        """
        done = asyncio.Event()
        result: dict = {}

        async def on_conn():
            result["sent"] = await self.listener.send("id:srv;ready:1")
            done.set()

        self.listener.on_connected(on_conn)
        await self.listener.start()
        reader, _ = await self.peer()

        await asyncio.wait_for(done.wait(), timeout=3)
        self.assertIs(True, result["sent"], "on_connected 裡的送出被丟棄 —— writer 尚未登記")

        data = await asyncio.wait_for(reader.read(256), timeout=3)
        self.assertIn("id:srv;ready:1", data.decode())

    async def test_回呼拋例外不會洩漏連線(self):
        """on_connected 的呼叫點在 try 之外,回呼拋例外會讓 handler 跳過 finally ——
        writer 永遠留在集合裡,connection_count 虛高,之後每次 send() 都往死連線寫。
        """
        def boom():
            raise RuntimeError("回呼自己爆掉")

        errors: list[Exception] = []
        self.listener.on_connected(boom)
        self.listener.on_error(errors.append)
        await self.listener.start()

        _, writer = await self.peer()
        await asyncio.sleep(0.3)
        self.assertEqual(1, self.listener.connection_count)
        self.assertTrue(any(isinstance(e, RuntimeError) for e in errors),
                        "回呼的例外沒有轉交給 on_error")

        writer.close()
        await asyncio.sleep(0.4)
        self.assertEqual(0, self.listener.connection_count,
                         "回呼拋例外之後 writer 沒有被清掉")

    async def test_併發送出不會互相切開(self):
        """asyncio 的 StreamWriter.write() 單次呼叫具原子性,所以這裡刻意不加寫入鎖。
        這條測試就是在驗那個前提 —— 順便涵蓋 PONG 與使用者送出的交錯。
        """
        await self.listener.start()
        reader, writer = await self.peer()
        await asyncio.sleep(0.2)

        stop = asyncio.Event()

        async def pinger():
            i = 0
            while not stop.is_set():
                writer.write(f"EDGELINK_PING:{i:08x}\n".encode())
                await writer.drain()
                i += 1
                await asyncio.sleep(0.002)

        ping_task = asyncio.create_task(pinger())
        n = 300
        await asyncio.gather(*(self.listener.send(f"seq:{i}") for i in range(n)))
        stop.set()
        await ping_task
        await asyncio.sleep(0.3)

        buf = b""
        while True:
            try:
                chunk = await asyncio.wait_for(reader.read(65536), timeout=1.0)
            except asyncio.TimeoutError:
                break
            if not chunk:
                break
            buf += chunk

        cmds, pongs, corrupt = 0, 0, []
        for raw in buf.decode(errors="replace").split("\n"):
            line = raw.strip()
            if not line:
                continue
            if line.startswith("seq:") and line[4:].isdigit():
                cmds += 1
            elif line.startswith("EDGELINK_PONG:") and len(line) == 22:
                pongs += 1
            else:
                corrupt.append(line)

        self.assertEqual(n, cmds, f"只收到 {cmds}/{n} 筆完整訊息")
        self.assertEqual([], corrupt[:3], f"有 {len(corrupt)} 行被切爛(PONG {pongs} 筆)")

    async def test_塞住的對端不會擋住其他對端拿到資料(self):
        """drain() 是背壓等待點。先前擺在寫入迴圈裡,第一個對端沒把資料讀走的話,
        排在它後面的對端連 bytes 都拿不到 —— 資料被一條塞住的連線擋在門外。

        現在改成「先對所有對端 write(),再一起 gather(drain())」:每個對端立刻
        拿到資料,誰慢誰自己慢。

        注意這條**不**主張 send() 會馬上返回 —— 它仍然會等所有對端的背壓,
        一個不讀資料的對端會讓 send() 一直卡著。那是刻意的(與 C# 的
        NetworkStream.WriteAsync 一致):不等於放棄背壓、讓緩衝區無限長大。
        """
        await self.listener.start()

        # 用原生 socket 當「不讀」的對端:接收緩衝區調到最小並且完全不 recv
        stuck = socket.socket()
        stuck.setsockopt(socket.SOL_SOCKET, socket.SO_RCVBUF, 2048)
        stuck.connect(("127.0.0.1", self.port))
        reader, _ = await self.peer()
        await asyncio.sleep(0.3)
        self.assertEqual(2, self.listener.connection_count)

        blob = "x" * 4096

        async def flood():
            await asyncio.gather(*(self.listener.send(blob) for _ in range(200)))

        sender = asyncio.create_task(flood())
        try:
            # 健康的那條必須照樣收得到 —— 這才是「不被擋住」的意思
            got = 0
            deadline = asyncio.get_running_loop().time() + 5
            while got < 4096 * 10 and asyncio.get_running_loop().time() < deadline:
                chunk = await asyncio.wait_for(reader.read(65536), timeout=3)
                if not chunk:
                    break
                got += len(chunk)
            self.assertGreaterEqual(
                got, 4096 * 10,
                "健康的對端被塞住的那條擋住了 —— write() 排在別人的 drain() 後面")
        finally:
            stuck.close()
            sender.cancel()
            try:
                await sender
            except (asyncio.CancelledError, Exception):
                pass

if __name__ == "__main__":
    unittest.main()
