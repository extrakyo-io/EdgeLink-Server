/*
 * EdgeLink UDP 範例 — 溫度感測器
 *
 * 功能：
 *   - 每 3 秒以 UDP 傳送感測資料到 EdgeLink Server
 *   - 預設純送（LOCAL_PORT = 0），如需接收 EdgeLink 推回來的封包，
 *     把 LOCAL_PORT 改成非 0 值（例如 4210）即可
 *
 * 注意：UDP 無連線狀態，EdgeLink 不會對 UDP 裝置發送 PING/PONG。
 *
 * 適用：ESP32 / ESP8266 (WiFi)
 */

#include <WiFi.h>
#include <WiFiUdp.h>
#include <EdgeLinkUDP.h>

// ── 設定 ──────────────────────────────────
const char* WIFI_SSID     = "your-ssid";
const char* WIFI_PASSWORD = "your-password";

const char*    EDGELINK_HOST  = "192.168.1.100";  // EdgeLink Server IP
const uint16_t EDGELINK_PORT  = 9002;             // UDP Port (EdgeLink 監聽的 UDP Port)
// 純送 sensor 資料就保留 0；要接收 EdgeLink 推回來的封包再改成例如 4210
const uint16_t LOCAL_PORT     = 0;
// ─────────────────────────────────────────

WiFiUDP     wifiUdp;
EdgeLinkUDP edgelink(wifiUdp);

// LOCAL_PORT > 0 才會觸發。一般業務訊息（已過濾掉 EDGELINK_* 控制訊息）
void onMessage(const String& msg, IPAddress remoteIP, uint16_t remotePort) {
    Serial.print("[EdgeLink UDP] From ");
    Serial.print(remoteIP);
    Serial.print(":");
    Serial.print(remotePort);
    Serial.print(" → ");
    Serial.println(msg);
}

// 上游設備在 EdgeLink Server 上下線通知（30s timeout-based）。
// 需在 EdgeLink Server 把這台 Arduino 設成某 UDP port 的 forward target 才會收到。
void onDeviceStatus(bool connected, const String& endpoint, const String& deviceId) {
    Serial.print("[EdgeLink UDP] Device ");
    Serial.print(connected ? "▲ ONLINE  " : "▼ OFFLINE ");
    Serial.print(deviceId);
    Serial.print("  @ ");
    Serial.println(endpoint);
}

void setup() {
    Serial.begin(115200);

    Serial.print("Connecting to WiFi");
    WiFi.begin(WIFI_SSID, WIFI_PASSWORD);
    while (WiFi.status() != WL_CONNECTED) {
        delay(500);
        Serial.print(".");
    }
    Serial.println();
    Serial.print("WiFi connected, IP: ");
    Serial.println(WiFi.localIP());

    edgelink.begin(LOCAL_PORT);
    edgelink.onMessage(onMessage);
    edgelink.onDeviceStatus(onDeviceStatus);
    Serial.println("EdgeLink UDP ready");
}

void loop() {
    // 檢查是否有收到 UDP 封包
    edgelink.loop();

    // 每 3 秒傳送一筆資料
    static uint32_t lastSend = 0;
    if (millis() - lastSend >= 3000) {
        float temperature = readTemperature();
        float humidity    = readHumidity();

        String msg = "id:ESP32_01;temp:" + String(temperature, 1)
                   + ";humidity:"        + String(humidity, 1);

        edgelink.send(EDGELINK_HOST, EDGELINK_PORT, msg);
        Serial.print("[EdgeLink UDP] Sent: ");
        Serial.println(msg);

        lastSend = millis();
    }
}

// ── 感測器讀取（替換成實際函式）──────────────
float readTemperature() {
    return 25.3;
}

float readHumidity() {
    return 60.0;
}
