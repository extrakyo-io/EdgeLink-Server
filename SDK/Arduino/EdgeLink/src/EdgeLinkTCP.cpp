#include "EdgeLinkTCP.h"

EdgeLinkTCP::EdgeLinkTCP(Client& client) : _client(client) {}

bool EdgeLinkTCP::begin(const char* host, uint16_t port) {
    _host = host;
    _port = port;
    _rxBuf = "";
    _rxOverflow = false;
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
            _rxOverflow = false;
        } else if (c != '\r') {
            // 對端若一直不送換行,_rxBuf 會無限成長。在 MCU 上這比桌面端嚴重得多:
            // Uno 只有 2 KB SRAM,String 每次擴充都重新配置,heap 很快碎裂/耗盡而重開機。
            // 超過上限就進入丟棄模式,直到下一個 '\n' 才恢復 —— 丟掉的是壞掉的那一行,
            // 而不是整個連線。
            if (_rxOverflow) {
                // 已在丟棄模式,直接吃掉這個字元(等 '\n' 才復原)
            } else if (_rxBuf.length() >= kMaxLineLen) {
                _rxOverflow = true;
                _rxBuf = "";          // 立刻放掉記憶體,不要抱著壞掉的半行
            } else {
                _rxBuf += c;
            }
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
    _rxOverflow = false;
    _client.connect(_host, _port);
}
