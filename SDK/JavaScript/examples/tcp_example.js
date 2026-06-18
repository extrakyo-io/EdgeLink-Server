"use strict";

const { EdgeLinkClient } = require("../src");

const EDGELINK_HOST = "192.168.1.100";
const EDGELINK_PORT = 9001;

const client = new EdgeLinkClient(EDGELINK_HOST, EDGELINK_PORT);

client.on("connected",    ()    => console.log("[EdgeLink] Connected"));
client.on("disconnected", ()    => console.log("[EdgeLink] Disconnected"));
client.on("message",      (msg) => console.log(`[EdgeLink] Received: ${msg}`));
client.on("error",        (err) => console.error(`[EdgeLink] Error: ${err.message}`));

client.setAutoReconnect(true, 5000);
client.connect();

// 每 3 秒傳送一筆資料
setInterval(() => {
    if (!client.isConnected) return;
    client.send("id:NODE_01;temp:25.3;humidity:60.0");
    console.log("[EdgeLink] Sent sensor data");
}, 3000);
