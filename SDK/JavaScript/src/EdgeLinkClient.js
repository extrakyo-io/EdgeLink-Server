"use strict";

const net      = require("net");
const { StringDecoder } = require("string_decoder");
const EventEmitter = require("events");

/**
 * TCP client — connects to EdgeLink Server, handles PING/PONG automatically.
 *
 * Events:
 *   "connected"    ()
 *   "disconnected" ()
 *   "message"      (string)
 *   "error"        (Error)
 */
class EdgeLinkClient extends EventEmitter {
    /**
     * @param {string} host
     * @param {number} port
     */
    constructor(host, port) {
        super();
        this.host              = host;
        this.port              = port;
        this._autoReconnect    = true;
        this._reconnectDelay   = 5000;
        this._socket           = null;
        this._lineBuf          = "";
        this._decoder          = new StringDecoder("utf8");
        this._reconnectTimer   = null;
        this._destroyed        = false;
    }

    get isConnected() {
        return this._socket !== null && !this._socket.destroyed;
    }

    setAutoReconnect(enable, delayMs = 5000) {
        this._autoReconnect  = enable;
        this._reconnectDelay = delayMs;
    }

    connect() {
        if (this._destroyed) throw new Error("EdgeLinkClient has been destroyed.");
        this._connectCore();
    }

    /**
     * @param {string} message
     */
    send(message) {
        if (!this.isConnected) throw new Error("Not connected to EdgeLink.");
        if (!message.endsWith("\n")) message += "\n";
        this._socket.write(message, "utf8");
    }

    disconnect() {
        this._destroyed = true;
        this._clearReconnectTimer();
        if (this._socket) {
            this._socket.destroy();
            this._socket = null;
        }
    }

    // ── internal ──────────────────────────────────────────────────────────────

    _connectCore() {
        this._lineBuf = "";
        this._decoder = new StringDecoder("utf8");   // 重連:丟棄殘留的半個字元
        const socket  = new net.Socket();
        this._socket  = socket;

        socket.setNoDelay(true);
        socket.connect(this.port, this.host, () => {
            this.emit("connected");
        });

        socket.on("data", (chunk) => {
            // StringDecoder 會保留跨 chunk 的不完整 UTF-8 序列;
            // chunk.toString("utf8") 是每段各自解碼,多位元組字元被切開就變成 U+FFFD
            this._lineBuf += this._decoder.write(chunk);
            let idx;
            while ((idx = this._lineBuf.indexOf("\n")) !== -1) {
                const line = this._lineBuf.slice(0, idx).trim();
                this._lineBuf = this._lineBuf.slice(idx + 1);
                if (line) this._handleLine(line);
            }
        });

        socket.on("close", () => {
            this._socket = null;
            this.emit("disconnected");
            if (!this._destroyed && this._autoReconnect) {
                this._scheduleReconnect();
            }
        });

        socket.on("error", (err) => {
            this.emit("error", err);
        });
    }

    _handleLine(line) {
        if (line.startsWith("EDGELINK_PING:")) {
            const hex = line.slice(14);
            if (this.isConnected) {
                this._socket.write(`EDGELINK_PONG:${hex}\n`, "utf8");
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

    _scheduleReconnect() {
        this._clearReconnectTimer();
        this._reconnectTimer = setTimeout(() => {
            if (!this._destroyed) this._connectCore();
        }, this._reconnectDelay);
    }

    _clearReconnectTimer() {
        if (this._reconnectTimer !== null) {
            clearTimeout(this._reconnectTimer);
            this._reconnectTimer = null;
        }
    }
}

module.exports = EdgeLinkClient;
