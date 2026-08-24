"use strict";

/**
 * EdgeLinkTcpListener 的送出與生命週期。
 *
 * 只用內建的 node:test(Node 18+),與 SDK 本身「不依賴外部套件」的宣稱一致。跑法:
 *
 *     cd SDK/JavaScript && node --test
 *
 * 全部走真的 socket。這個類別的價值就在於它與 OS 的互動(埠釋放、半開連線、
 * 背壓),mock 掉就等於什麼都沒測。
 */

const test   = require("node:test");
const assert = require("node:assert");
const net    = require("node:net");

const { EdgeLinkTcpListener } = require("../src");

const sleep = ms => new Promise(r => setTimeout(r, ms));

function freePort() {
    return new Promise(resolve => {
        const probe = net.createServer();
        probe.listen(0, "127.0.0.1", () => {
            const { port } = probe.address();
            probe.close(() => resolve(port));
        });
    });
}

/** 建立一條連進 listener 的連線,並把收到的位元組累積起來。 */
function connect(port) {
    return new Promise((resolve, reject) => {
        const socket = net.createConnection(port, "127.0.0.1");
        const chunks = [];
        socket.on("data", d => chunks.push(Buffer.isBuffer(d) ? d : Buffer.from(d)));
        socket.once("error", reject);
        socket.once("connect", () => resolve({
            socket,
            get received() { return Buffer.concat(chunks); },
            reset() { chunks.length = 0; },
        }));
    });
}

/** 開一個 listener,並保證測試結束時一定收乾淨。 */
async function withListener(fn) {
    const port     = await freePort();
    const listener = new EdgeLinkTcpListener(port);
    const peers    = [];

    const open = async () => {
        const p = await connect(port);
        peers.push(p);
        return p;
    };

    try {
        await fn({ listener, port, open });
    } finally {
        for (const p of peers) { try { p.socket.destroy(); } catch { /* 已經關了 */ } }
        try { listener.stop(); } catch { /* 已經停了 */ }
        await sleep(150);
    }
}

// ── 生命週期 ────────────────────────────────────────────────────────────────

test("尚未 start 時 isRunning 為 false", async () => {
    await withListener(async ({ listener }) => {
        assert.strictEqual(listener.isRunning, false);
        assert.strictEqual(listener.connectionCount, 0);
    });
});

test("stop() 會主動關閉已接受的連線", async () => {
    // server.close() 只是不再 accept,已建立的連線還開著 —— 不主動收掉的話
    // socket 會殘留到 OS 端,反覆 start/stop 就是累積。
    await withListener(async ({ listener, open }) => {
        listener.start();
        const peer = await open();
        await sleep(200);
        assert.strictEqual(listener.connectionCount, 1);

        const closed = new Promise(r => peer.socket.once("close", r));
        listener.stop();
        await Promise.race([closed, sleep(3000).then(() => { throw new Error("對端沒收到 FIN"); })]);
        assert.strictEqual(listener.connectionCount, 0);
    });
});

test("stop() 之後埠已釋放", async () => {
    await withListener(async ({ listener, port, open }) => {
        listener.start();
        await open();
        await sleep(200);
        listener.stop();
        await sleep(300);

        await new Promise((resolve, reject) => {
            const probe = net.createServer();
            probe.once("error", reject);              // EADDRINUSE = 舊 socket 沒收乾淨
            probe.listen(port, "127.0.0.1", () => probe.close(resolve));
        });
    });
});

test("stop() 之後可以再 start()", async () => {
    await withListener(async ({ listener, open }) => {
        listener.start();
        await open();
        await sleep(200);
        listener.stop();
        await sleep(300);

        listener.start();
        await sleep(200);
        assert.strictEqual(listener.isRunning, true);
        const peer = await open();
        await sleep(200);
        assert.strictEqual(listener.connectionCount, 1);
        peer.socket.destroy();
    });
});

// ── 送出 ────────────────────────────────────────────────────────────────────

test("沒有連線時送出回 false", async () => {
    await withListener(async ({ listener }) => {
        listener.start();
        await sleep(150);
        assert.strictEqual(listener.send("cmd:noop"), false);
        assert.strictEqual(listener.sendBytes(Buffer.from([1])), false);
    });
});

test("送出會寫給每一條連線", async () => {
    await withListener(async ({ listener, open }) => {
        listener.start();
        const a = await open(), b = await open();
        await sleep(200);
        assert.strictEqual(listener.connectionCount, 2);

        assert.strictEqual(listener.send("cmd:start;v:1"), true);
        await sleep(300);
        for (const peer of [a, b])
            assert.match(peer.received.toString(), /cmd:start;v:1/);
    });
});

test("sendBytes 不附加換行", async () => {
    await withListener(async ({ listener, open }) => {
        listener.start();
        const peer = await open();
        await sleep(200);

        assert.strictEqual(listener.sendBytes(Buffer.from([0xAA, 0xBB, 0x01, 0x02])), true);
        await sleep(300);
        assert.deepStrictEqual(peer.received, Buffer.from([0xAA, 0xBB, 0x01, 0x02]));
    });
});

test("從 'connected' 事件送出的第一筆不會被丟掉", async () => {
    // 連線要**先登記再 emit** —— 否則在事件處理器裡送的握手訊息會因為 socket
    // 還沒進集合而被靜靜跳過,呼叫端只拿到一個 false。
    await withListener(async ({ listener, open }) => {
        let result = null;
        listener.on("connected", () => { if (result === null) result = listener.send("id:srv;ready:1"); });

        listener.start();
        const peer = await open();
        await sleep(300);

        assert.strictEqual(result, true, "'connected' 裡的送出被丟棄 —— socket 尚未登記");
        assert.match(peer.received.toString(), /id:srv;ready:1/);
    });
});

test("對端斷線後送出不會丟例外", async () => {
    await withListener(async ({ listener, open }) => {
        listener.start();
        const peer = await open();
        await sleep(200);

        peer.socket.destroy();
        await sleep(400);
        assert.strictEqual(listener.connectionCount, 0, "斷掉的 socket 沒有從集合移除");
        assert.doesNotThrow(() => listener.send("after:disconnect"));
    });
});

test("併發送出不會互相切開", async () => {
    // Node 的 socket.write() 單次呼叫具原子性,所以這裡刻意不加寫入鎖。
    // 這條測試就是在驗那個前提 —— 順便涵蓋 PONG 與使用者送出的交錯。
    await withListener(async ({ listener, open }) => {
        listener.start();
        const peer = await open();
        await sleep(200);

        let i = 0;
        const pinger = setInterval(() => {
            peer.socket.write(`EDGELINK_PING:${(i++).toString(16).padStart(8, "0")}\n`);
        }, 2);

        const N = 500;
        for (let k = 0; k < N; k++) listener.send(`seq:${k}`);
        await sleep(800);
        clearInterval(pinger);
        await sleep(200);

        const lines   = peer.received.toString().split("\n").map(l => l.trim()).filter(Boolean);
        const cmds    = lines.filter(l => /^seq:\d+$/.test(l));
        const pongs   = lines.filter(l => /^EDGELINK_PONG:[0-9a-f]{8}$/.test(l));
        const corrupt = lines.filter(l => !/^seq:\d+$/.test(l) && !/^EDGELINK_PONG:[0-9a-f]{8}$/.test(l));

        assert.strictEqual(cmds.length, N, `只收到 ${cmds.length}/${N} 筆完整訊息`);
        assert.deepStrictEqual(corrupt, [], `有 ${corrupt.length} 行被切爛(PONG ${pongs.length} 筆)`);
    });
});

test("PING 會被回 PONG 且不外流成 message 事件", async () => {
    await withListener(async ({ listener, open }) => {
        const messages = [];
        listener.on("message", m => messages.push(m));
        listener.start();
        const peer = await open();
        await sleep(200);

        peer.socket.write("EDGELINK_PING:deadbeef\n");
        await sleep(300);

        assert.match(peer.received.toString(), /EDGELINK_PONG:deadbeef/);
        assert.deepStrictEqual(messages, [], "協定訊息漏給了 'message' 事件");
    });
});
