"use strict";

const dgram        = require("dgram");
const EventEmitter = require("events");

/**
 * UDP receiver — binds to a local port and emits "message" for each packet.
 *
 * Events:
 *   "message"      (string)
 *   "deviceStatus" (connected: boolean, endpoint: string, deviceId: string)
 *                  Fired when an upstream device starts/stops sending packets to EdgeLink Server (timeout-based).
 *   "error"        (Error)
 */
class EdgeLinkUdpClient extends EventEmitter {
    /**
     * @param {number} localPort
     */
    constructor(localPort) {
        super();
        this.localPort = localPort;
        this._socket   = null;
        this.isRunning = false;
    }

    start() {
        this._socket = dgram.createSocket("udp4");

        this._socket.on("message", (buf) => {
            const msg = buf.toString("utf8").trim();
            if (!msg) return;

            if (msg.startsWith("EDGELINK_STATUS:")) {
                // body: "STATUS:protocol@ip" or "STATUS:protocol@ip:deviceId"
                const body      = msg.slice(16);
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
            if (msg.startsWith("EDGELINK_")) return;

            this.emit("message", msg);
        });

        this._socket.on("error", (err) => this.emit("error", err));

        this._socket.bind(this.localPort, () => {
            this.isRunning = true;
        });
    }

    stop() {
        if (this._socket) {
            this._socket.close();
            this._socket = null;
        }
        this.isRunning = false;
    }
}

/**
 * UDP sender — send-only, no local port binding required.
 */
class EdgeLinkUdpSender {
    constructor() {
        this._socket = dgram.createSocket("udp4");
    }

    /**
     * @param {string} host
     * @param {number} port
     * @param {string} message
     * @returns {Promise<void>}
     */
    send(host, port, message) {
        return new Promise((resolve, reject) => {
            const buf = Buffer.from(message, "utf8");
            this._socket.send(buf, 0, buf.length, port, host, (err) => {
                if (err) reject(err);
                else resolve();
            });
        });
    }

    close() {
        this._socket.close();
    }
}

module.exports = { EdgeLinkUdpClient, EdgeLinkUdpSender };
