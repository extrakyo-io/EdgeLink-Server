#pragma once

// AsyncUDP support — ESP32 built-in <AsyncUDP.h> or ESP8266 ESPAsyncUDP library.
// This whole class only compiles when one of those headers is available.
#if __has_include(<AsyncUDP.h>) || __has_include(<ESPAsyncUDP.h>)

#include <Arduino.h>
#if __has_include(<AsyncUDP.h>)
  #include <AsyncUDP.h>
#else
  #include <ESPAsyncUDP.h>
#endif

#define EDGELINK_HAS_ASYNCUDP 1

class EdgeLinkAsyncUDP {
public:
    using MessageCallback      = void (*)(const String& message, IPAddress remoteIP, uint16_t remotePort);
    // EDGELINK_STATUS event from EdgeLink Server (timeout-based for UDP).
    // Parameters: isConnected, endpoint (e.g. "UDPPort@192.168.1.50"), deviceId.
    using DeviceStatusCallback = void (*)(bool connected, const String& endpoint, const String& deviceId);

    explicit EdgeLinkAsyncUDP(AsyncUDP& udp);

    // Start listening on localPort (0 = send-only). Returns false if listen() fails.
    // Unlike sync UDP, no loop() is needed — incoming packets fire the callback directly.
    bool begin(uint16_t localPort = 0);

    // Send a UTF-8 message. Returns bytes written (0 = failure).
    size_t send(const char* host, uint16_t port, const String& message);
    size_t send(IPAddress  ip,    uint16_t port, const String& message);

    // Callbacks run in AsyncUDP's task (not loop()) — keep handlers short, no Serial.print spam.
    // EDGELINK_* control messages are filtered out of onMessage.
    void onMessage(MessageCallback cb);
    void onDeviceStatus(DeviceStatusCallback cb);

    void close();

private:
    AsyncUDP&            _udp;
    MessageCallback      _onMsg    = nullptr;
    DeviceStatusCallback _onStatus = nullptr;

    void _dispatchStatus(const String& line);
};

#endif // AsyncUDP available
