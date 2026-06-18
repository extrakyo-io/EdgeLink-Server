/*
 * EdgeLink AsyncUDP 範例 — 溫度感測器（ESP32）
 *
 * 與 udp_sensor.ino 的差異：
 *   - 使用 ESP32 內建的 AsyncUDP（callback driven），不需在 loop() 裡呼叫 edgelink.loop()
 *   - 收封包是中斷式的，loop() 可以全心處理感測 / 業務邏輯
 *
 * 預設純送（LOCAL_PORT = 0），如需接收 EdgeLink 推回來的封包，
 * 把 LOCAL_PORT 改成非 0 值（例如 4210）即可。
 *
 * 適用：ESP32（內建 AsyncUDP）。ESP8266 需安裝 ESPAsyncUDP library。
 */

#include <WiFi.h>
#include <AsyncUDP.h>
#include <EdgeLinkAsyncUDP.h>

// ── 設定 ──────────────────────────────────
const char* WIFI_SSID     = "your-ssid";
const char* WIFI_PASSWORD = "your-password";

const char*    EDGELINK_HOST = "192.168.1.100";   // EdgeLink Server IP
const uint16_t EDGELINK_PORT = 9002;              // EdgeLink 監聽的 UDP Port
// 純送 sensor 資料就保留 0；要接收 EdgeLink 推回來的封包再改成例如 4210
const uint16_t LOCAL_PORT    = 0;
// ─────────────────────────────────────────

AsyncUDP         asyncUdp;
EdgeLinkAsyncUDP edgelink(asyncUdp);

// LOCAL_PORT > 0 時才會觸發。一般業務訊息（已過濾掉 EDGELINK_* 控制訊息）
// callback 在 AsyncUDP task 內執行（非 loop()），盡量短、不要呼叫 Serial.print 大量輸出，
// 必要時用 queue 傳回主執行緒處理。
void onMessage(const String& msg, IPAddress remoteIP, uint16_t remotePort) {
    Serial.print("[EdgeLink AsyncUDP] From ");
    Serial.print(remoteIP);
    Serial.print(":");
    Serial.print(remotePort);
    Serial.print(" → ");
    Serial.println(msg);
}

// 上游設備在 EdgeLink Server 上下線通知（30s timeout-based）。
// 需在 EdgeLink Server 把這台 ESP32 設成某 UDP port 的 forward target 才會收到。
// 同樣在 AsyncUDP task 內執行。
void onDeviceStatus(bool connected, const String& endpoint, const String& deviceId) {
    Serial.print("[EdgeLink AsyncUDP] Device ");
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

    edgelink.onMessage(onMessage);
    edgelink.onDeviceStatus(onDeviceStatus);
    if (!edgelink.begin(LOCAL_PORT)) {
        Serial.println("AsyncUDP listen failed");
        while (true) delay(1000);
    }
    Serial.println("EdgeLink AsyncUDP ready");
}

void loop() {
    // 不需 edgelink.loop()。AsyncUDP 是 callback driven。

    static uint32_t lastSend = 0;
    if (millis() - lastSend >= 3000) {
        float temperature = readTemperature();
        float humidity    = readHumidity();

        String msg = "id:ESP32_01;temp:" + String(temperature, 1)
                   + ";humidity:"        + String(humidity, 1);

        edgelink.send(EDGELINK_HOST, EDGELINK_PORT, msg);
        Serial.print("[EdgeLink AsyncUDP] Sent: ");
        Serial.println(msg);

        lastSend = millis();
    }
}

// ── 感測器讀取（替換成實際函式）──────────────
float readTemperature() { return 25.3; }
float readHumidity()    { return 60.0; }
