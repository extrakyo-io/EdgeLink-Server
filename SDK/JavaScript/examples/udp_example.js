"use strict";

const { EdgeLinkUdpSender } = require("../src");

const EDGELINK_HOST = "192.168.1.100";
const EDGELINK_PORT = 9002;

const sender = new EdgeLinkUdpSender();

console.log("[EdgeLink] Sending UDP packets every 3s. Ctrl+C to stop.");

setInterval(async () => {
    await sender.send(EDGELINK_HOST, EDGELINK_PORT, "id:NODE_01;temp:25.3;humidity:60.0");
    console.log("[EdgeLink] UDP sent");
}, 3000);
