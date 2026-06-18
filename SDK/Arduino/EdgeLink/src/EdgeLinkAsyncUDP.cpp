#include "EdgeLinkAsyncUDP.h"

#ifdef EDGELINK_HAS_ASYNCUDP

EdgeLinkAsyncUDP::EdgeLinkAsyncUDP(AsyncUDP& udp) : _udp(udp) {}

bool EdgeLinkAsyncUDP::begin(uint16_t localPort) {
    _udp.onPacket([this](AsyncUDPPacket packet) {
        size_t len = packet.length();
        if (len == 0) return;
        String msg;
        msg.reserve(len);
        const uint8_t* data = packet.data();
        for (size_t i = 0; i < len; ++i) msg += (char)data[i];
        msg.trim();
        if (msg.length() == 0) return;

        if (msg.startsWith("EDGELINK_STATUS:")) { _dispatchStatus(msg); return; }
        if (msg.startsWith("EDGELINK_"))         return;   // other control prefixes — drop

        if (_onMsg) _onMsg(msg, packet.remoteIP(), packet.remotePort());
    });

    if (localPort == 0) return true;          // send-only mode
    return _udp.listen(localPort);
}

size_t EdgeLinkAsyncUDP::send(const char* host, uint16_t port, const String& message) {
    return _udp.writeTo(reinterpret_cast<const uint8_t*>(message.c_str()),
                        message.length(), host, port);
}

size_t EdgeLinkAsyncUDP::send(IPAddress ip, uint16_t port, const String& message) {
    return _udp.writeTo(reinterpret_cast<const uint8_t*>(message.c_str()),
                        message.length(), ip, port);
}

void EdgeLinkAsyncUDP::onMessage(MessageCallback cb)           { _onMsg    = cb; }
void EdgeLinkAsyncUDP::onDeviceStatus(DeviceStatusCallback cb) { _onStatus = cb; }

void EdgeLinkAsyncUDP::_dispatchStatus(const String& line) {
    if (!_onStatus) return;
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

void EdgeLinkAsyncUDP::close() { _udp.close(); }

#endif // EDGELINK_HAS_ASYNCUDP
