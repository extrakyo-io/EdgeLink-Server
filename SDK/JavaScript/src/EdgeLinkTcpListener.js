"use strict";

const net          = require("net");
const { StringDecoder } = require("string_decoder");
const EventEmitter = require("events");

/**
 * TCP listener — accepts incoming connections, handles PING/PONG automatically.
 *
 * Events:
 *   "connected"    ()
 *   "disconnected" ()
 *   "message"      (string)
 *   "error"        (Error)
 */
class EdgeLinkTcpListener extends EventEmitter {
    /**
     * @param {number} localPort
     */
    constructor(localPort) {
        super();
        this.localPort  = localPort;
        this._server    = null;
        this.isRunning  = false;
        // 已接受、還沒斷的對端。send() 要寫給它們,stop() 要主動收掉它們 ——
        // server.close() 只是不再 accept,已建立的連線還開著。
        this._sockets   = new Set();
    }

    /** 目前連進來的對端數(通常只有 EdgeLink 一條)。 */
    get connectionCount() {
        return this._sockets.size;
    }

    /**
     * 送一行 KV 給所有已連線的對端(自動補換行)。沒有任何連線時回 false。
     *
     * 對端是 EdgeLink 的 TCP Client 埠連進來,所以「送」= 回應 EdgeLink。
     *
     * 這裡刻意不加每條連線的寫入鎖:Node 的 socket.write() 是同步把整段內容排進
     * 內部佇列,單次呼叫具原子性 —— 與 PONG 的寫入不會互相切開。這與 .NET 的
     * NetworkStream 不同(那邊併發 WriteAsync 會真的交錯,所以 C#/Unity 版需要鎖)。
     *
     * @param {string} line
     * @returns {boolean} 是否至少寫進一條連線
     */
    send(line) {
        if (!line) return false;
        if (!line.endsWith("\n")) line += "\n";
        return this.sendBytes(Buffer.from(line, "utf8"));
    }

    /**
     * 送原始位元組給所有已連線的對端,不附加換行。
     * @param {Buffer} data
     * @returns {boolean}
     */
    sendBytes(data) {
        if (!data || data.length === 0) return false;
        let sent = false;
        for (const socket of this._sockets) {
            if (socket.destroyed) continue;
            try {
                socket.write(data);
                sent = true;
            } catch (err) {
                this.emit("error", err);
            }
        }
        return sent;
    }

    start() {
        this._server = net.createServer((socket) => {
            // 先登記再 emit —— 否則 "connected" 的處理器裡呼叫 send() 會漏掉這條連線
            this._sockets.add(socket);
            this.emit("connected");
            let lineBuf = "";
            const decoder = new StringDecoder("utf8");

            socket.on("data", (chunk) => {
                // 見 EdgeLinkClient:必須用有狀態的 decoder,否則被切開的多位元組字元會壞掉
                lineBuf += decoder.write(chunk);
                let idx;
                while ((idx = lineBuf.indexOf("\n")) !== -1) {
                    const line = lineBuf.slice(0, idx).trim();
                    lineBuf    = lineBuf.slice(idx + 1);
                    if (line) this._handleLine(line, socket);
                }
            });

            socket.on("close", () => {
                this._sockets.delete(socket);
                this.emit("disconnected");
            });
            socket.on("error", (err) => this.emit("error", err));
        });

        this._server.on("error", (err) => this.emit("error", err));
        this._server.listen(this.localPort, () => {
            this.isRunning = true;
        });
    }

    stop() {
        if (this._server) {
            this._server.close();
            this._server = null;
        }
        // 主動收掉已建立的連線,否則 socket 會殘留到 OS 端
        for (const socket of this._sockets) {
            try { socket.destroy(); } catch { /* 已經斷了 */ }
        }
        this._sockets.clear();
        this.isRunning = false;
    }

    _handleLine(line, socket) {
        if (line.startsWith("EDGELINK_PING:")) {
            const hex = line.slice(14);
            if (!socket.destroyed) {
                socket.write(`EDGELINK_PONG:${hex}\n`, "utf8");
            }
            return;
        }
        if (line.startsWith("EDGELINK_STATUS:")) {
            // body: "STATUS:protocol@ip" or "STATUS:protocol@ip:deviceId"
            const body      = line.slice(16);
            const sep       = body.indexOf(":");
            const status    = sep >= 0 ? body.slice(0, sep) : body;
            const rest      = sep >= 0 ? body.slice(sep + 1) : "";
            const connected = status.toUpperCase() === "CONNECTED";
            const devSep    = rest.lastIndexOf(":");
            const endpoint  = devSep >= 0 ? rest.slice(0, devSep)  : rest;
            const deviceId  = devSep >= 0 ? rest.slice(devSep + 1) : "";
            this.emit("deviceStatus", connected, endpoint, deviceId);
            return;
        }
        if (line.startsWith("EDGELINK_")) return;

        this.emit("message", line);
    }
}

module.exports = EdgeLinkTcpListener;
