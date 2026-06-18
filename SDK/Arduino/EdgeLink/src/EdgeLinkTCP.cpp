#include "EdgeLinkTCP.h"

EdgeLinkTCP::EdgeLinkTCP(Client& client) : _client(client) {}

bool EdgeLinkTCP::begin(const char* host, uint16_t port) {
    _host = host;
    _port = port;
    _rxBuf = "";
    _lastAttempt = millis();
    return _client.connect(host, port);
}

void EdgeLinkTCP::loop() {
    if (!_client.connected()) {
        _reconnectIfNeeded();
        return;
    }

    while (_client.available()) {
        char c = (char)_client.read();
        if (c == '\n') {
            _rxBuf.trim();
            if (_rxBuf.length() > 0) {
                _processLine(_rxBuf);
            }
            _rxBuf = "";
        } else if (c != '\r') {
            _rxBuf += c;
        }
    }
}

bool EdgeLinkTCP::send(const String& message) {
    if (!_client.connected()) return false;
    _client.print(message);
    if (!message.endsWith("\n")) _client.print('\n');
    return true;
}

bool EdgeLinkTCP::send(const char* message) {
    return send(String(message));
}

void EdgeLinkTCP::onMessage(MessageCallback cb) {
    _onMsg = cb;
}

bool EdgeLinkTCP::isConnected() const {
    return _client.connected();
}

void EdgeLinkTCP::setAutoReconnect(bool enable, uint32_t intervalMs) {
    _autoReconnect = enable;
    _reconnectMs   = intervalMs;
}

void EdgeLinkTCP::_processLine(const String& line) {
    if (line.startsWith("EDGELINK_PING:")) {
        // Must respond with PONG or the server will disconnect after ~15s
        String hex = line.substring(14);
        _client.print("EDGELINK_PONG:");
        _client.print(hex);
        _client.print('\n');
        return;
    }
    if (line.startsWith("EDGELINK_")) return;

    if (_onMsg) _onMsg(line);
}

void EdgeLinkTCP::_reconnectIfNeeded() {
    if (!_autoReconnect || _host == nullptr) return;
    uint32_t now = millis();
    if (now - _lastAttempt < _reconnectMs) return;
    _lastAttempt = now;
    _rxBuf = "";
    _client.connect(_host, _port);
}
