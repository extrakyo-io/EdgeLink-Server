#pragma once
#include <Arduino.h>
#include <Client.h>

class EdgeLinkTCP {
public:
    using MessageCallback = void (*)(const String& message);

    explicit EdgeLinkTCP(Client& client);

    // Connect to EdgeLink TCP Server port
    bool begin(const char* host, uint16_t port);

    // Must be called in loop() — handles PING/PONG and incoming messages
    void loop();

    // Send a message (newline appended automatically)
    bool send(const String& message);
    bool send(const char* message);

    // Callback for incoming business messages (EDGELINK_* internal messages filtered out)
    void onMessage(MessageCallback cb);

    bool isConnected() const;

    // Auto-reconnect after disconnect (default: enabled, 5000 ms)
    void setAutoReconnect(bool enable, uint32_t intervalMs = 5000);

private:
    Client&         _client;
    const char*     _host           = nullptr;
    uint16_t        _port           = 0;
    String          _rxBuf;
    MessageCallback _onMsg          = nullptr;
    bool            _autoReconnect  = true;
    uint32_t        _reconnectMs    = 5000;
    uint32_t        _lastAttempt    = 0;

    void _processLine(const String& line);
    void _reconnectIfNeeded();
};
