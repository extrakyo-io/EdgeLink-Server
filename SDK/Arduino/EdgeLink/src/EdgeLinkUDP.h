#pragma once
#include <Arduino.h>
#include <Udp.h>

class EdgeLinkUDP {
public:
    using MessageCallback      = void (*)(const String& message, IPAddress remoteIP, uint16_t remotePort);
    // EDGELINK_STATUS event from EdgeLink Server (timeout-based for UDP).
    // Parameters: isConnected, endpoint (e.g. "UDPPort@192.168.1.50"), deviceId.
    using DeviceStatusCallback = void (*)(bool connected, const String& endpoint, const String& deviceId);

    explicit EdgeLinkUDP(UDP& udp);

    // Start listening on localPort (0 = send-only)
    bool begin(uint16_t localPort = 0);

    // Must be called in loop() — checks for incoming packets
    void loop();

    // Send a message to EdgeLink UDP port
    bool send(const char* host, uint16_t port, const String& message);
    bool send(IPAddress ip,     uint16_t port, const String& message);

    // Callbacks (EDGELINK_* control messages are filtered out of onMessage)
    void onMessage(MessageCallback cb);
    void onDeviceStatus(DeviceStatusCallback cb);

private:
    UDP&                 _udp;
    MessageCallback      _onMsg    = nullptr;
    DeviceStatusCallback _onStatus = nullptr;

    void _dispatchStatus(const String& line);
};
