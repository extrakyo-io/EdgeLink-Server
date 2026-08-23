"""回呼派送 —— 讓 async def 的回呼真的會跑,並且回呼爆掉不會拖垮接收迴圈。"""

import asyncio
import inspect
from typing import Any, Callable, Iterable

# 保留 fire-and-forget 任務的參考。事件迴圈只持有弱參考,沒有人抓著的話
# 任務可能在跑完之前就被回收掉。
_pending: set[asyncio.Task] = set()


def fire(
    callbacks: Iterable[Callable[..., Any]],
    *args: Any,
    on_error: Iterable[Callable[[Exception], Any]] | None = None,
) -> None:
    """呼叫每個回呼,並容忍兩件事:回呼是 ``async def``、回呼自己拋例外。

    先前是直接 ``for cb in cbs: cb(...)``,兩個問題:

    1. 註冊 ``async def`` 的回呼時,``cb(...)`` 只是產生一個沒人 await 的 coroutine
       物件。Python 只丟一句 RuntimeWarning,回呼裡一行都不會執行 —— 而 ``send()``
       本身就是 coroutine,「在 on_connected 裡送出握手訊息」這個最自然的用法
       因此完全失效(而 ``_handle_client`` 特地把 writer 登記在通知之前,
       就是為了支援這個用法)。

    2. 回呼拋例外會往上炸進接收迴圈。在 ``EdgeLinkTcpListener`` 的 on_connected
       上尤其糟:那段程式在 ``try`` 之外,例外會讓 handler 直接結束、跳過
       ``finally``,writer 永遠留在 ``_writers`` 裡 —— ``connection_count`` 虛高,
       之後每次 ``send()`` 都往一條死掉的連線寫。
    """
    for cb in list(callbacks):
        try:
            result = cb(*args)
        except Exception as ex:
            _report(on_error, ex)
            continue
        if inspect.isawaitable(result):
            _schedule(result, on_error)


def _schedule(awaitable: Any, on_error: Iterable[Callable[[Exception], Any]] | None) -> None:
    try:
        loop = asyncio.get_running_loop()
    except RuntimeError:
        # 沒有執行中的事件迴圈 —— 明確關掉 coroutine,免得留下 "never awaited" 警告
        close = getattr(awaitable, "close", None)
        if close is not None:
            close()
        return

    task = loop.create_task(awaitable)
    _pending.add(task)
    task.add_done_callback(_pending.discard)
    task.add_done_callback(lambda t: _on_task_done(t, on_error))


def _on_task_done(task: asyncio.Task, on_error: Iterable[Callable[[Exception], Any]] | None) -> None:
    if task.cancelled():
        return
    ex = task.exception()
    if ex is not None:
        # 不轉交的話,這裡會變成 "Task exception was never retrieved" —— 只在
        # 直譯器關閉時才印出來,呼叫端當下拿不到任何訊號。
        _report(on_error, ex)


def _report(on_error: Iterable[Callable[[Exception], Any]] | None, ex: BaseException) -> None:
    if not on_error:
        return
    for cb in list(on_error):
        try:
            cb(ex)
        except Exception:
            pass
