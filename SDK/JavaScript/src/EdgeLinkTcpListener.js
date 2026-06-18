"use strict";

const net          = require("net");
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
    }

    start() {
        this._server = net.createServer((socket) => {
            this.emit("connected");
            let lineBuf = "";

            socket.on("data", (chunk) => {
                lineBuf += chunk.toString("utf8");
                let idx;
                while ((idx = lineBuf.indexOf("\n")) !== -1) {
                    const line = lineBuf.slice(0, idx).trim();
                    lineBuf    = lineBuf.slice(idx + 1);
                    if (line) this._handleLine(line, socket);
                }
            });

            socket.on("close", () => this.emit("disconnected"));
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
