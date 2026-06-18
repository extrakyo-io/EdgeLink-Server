#include "EdgeLinkUDP.h"

EdgeLinkUDP::EdgeLinkUDP(UDP& udp) : _udp(udp) {}

bool EdgeLinkUDP::begin(uint16_t localPort) {
    if (localPort == 0) return true;
    return _udp.begin(localPort) == 1;
}

void EdgeLinkUDP::loop() {
    int size = _udp.parsePacket();
    if (size <= 0) return;

    String msg;
    msg.reserve(size);
    while (_udp.available()) {
        msg += (char)_udp.read();
    }
    msg.trim();
    if (msg.length() == 0) return;

    if (msg.startsWith("EDGELINK_STATUS:")) { _dispatchStatus(msg); return; }
    if (msg.startsWith("EDGELINK_"))         return;   // other control prefixes — drop

    if (_onMsg) _onMsg(msg, _udp.remoteIP(), _udp.remotePort());
}

bool EdgeLinkUDP::send(const char* host, uint16_t port, const String& message) {
    if (!_udp.beginPacket(host, port)) return false;
    _udp.print(message);
    return _udp.endPacket() == 1;
}

bool EdgeLinkUDP::send(IPAddress ip, uint16_t port, const String& message) {
    if (!_udp.beginPacket(ip, port)) return false;
    _udp.print(message);
    return _udp.endPacket() == 1;
}

void EdgeLinkUDP::onMessage(MessageCallback cb) {
    _onMsg = cb;
}

void EdgeLinkUDP::onDeviceStatus(DeviceStatusCallback cb) {
    _onStatus = cb;
}

void EdgeLinkUDP::_dispatchStatus(const String& line) {
    if (!_onStatus) return;
    // line: "EDGELINK_STATUS:STATUS:protocol@ip" or "...:protocol@ip:deviceId"
    String body  = line.substring(16);
    int    sep   = body.indexOf(':');
    if (sep < 0) return;
    String stat  = body.substring(0, sep);
    String rest  = body.substring(sep + 1);
    bool   conn  = stat.equalsIgnoreCase("CONNECTED");
    int    dsep  = rest.lastIndexOf(':');
    String ep    = dsep >= 0 ? rest.substring(0, dsep)      : rest;
    String devId = dsep >= 0 ? rest.substring(dsep + 1)     : "";
    _onStatus(conn, ep, devId);
}
