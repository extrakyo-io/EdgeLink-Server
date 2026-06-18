/*
 * EdgeLink TCP 範例 — 溫度感測器
 *
 * 功能：
 *   - 連線到 EdgeLink Server 的 TCP Server Port
 *   - 每 3 秒傳送一筆感測資料
 *   - 自動回應 EDGELINK_PING (否則約 15 秒後會被斷線)
 *   - 斷線後自動重連
 *
 * 適用：ESP32 / ESP8266 (WiFi)，或換成 EthernetClient 可用於 Arduino Uno + Ethernet Shield
 */

#include <WiFi.h>
#include <EdgeLinkTCP.h>

// ── 設定 ──────────────────────────────────
const char* WIFI_SSID     = "your-ssid";
const char* WIFI_PASSWORD = "your-password";

const char*    EDGELINK_HOST = "192.168.1.100";  // EdgeLink Server IP
const uint16_t EDGELINK_PORT = 9001;             // TCP Server Port
// ─────────────────────────────────────────

WiFiClient   wifiClient;
EdgeLinkTCP  edgelink(wifiClient);

void onMessage(const String& msg) {
    // 收到來自後端系統的回應
    Serial.print("[EdgeLink] Received: ");
    Serial.println(msg);
}

void setup() {
    Serial.begin(115200);

    // 連線 WiFi
    Serial.print("Connecting to WiFi");
    WiFi.begin(WIFI_SSID, WIFI_PASSWORD);
    while (WiFi.status() != WL_CONNECTED) {
        delay(500);
        Serial.print(".");
    }
    Serial.println();
    Serial.print("WiFi connected, IP: ");
    Serial.println(WiFi.localIP());

    // 設定 EdgeLink
    edgelink.onMessage(onMessage);
    edgelink.setAutoReconnect(true, 5000);  // 斷線後 5 秒重連

    if (edgelink.begin(EDGELINK_HOST, EDGELINK_PORT)) {
        Serial.println("Connected to EdgeLink");
    } else {
        Serial.println("Connection failed, will retry automatically...");
    }
}

void loop() {
    // 必須在 loop 中呼叫，處理 PING/PONG 與接收訊息
    edgelink.loop();

    // 每 3 秒傳送一筆資料
    static uint32_t lastSend = 0;
    if (millis() - lastSend >= 3000 && edgelink.isConnected()) {
        float temperature = readTemperature();
        float humidity    = readHumidity();

        String msg = "id:ESP32_01;temp:" + String(temperature, 1)
                   + ";humidity:"        + String(humidity, 1);

        edgelink.send(msg);
        Serial.print("[EdgeLink] Sent: ");
        Serial.println(msg);

        lastSend = millis();
    }
}

// ── 感測器讀取（替換成實際函式）──────────────
float readTemperature() {
    return 25.3;  // 替換：DHT.readTemperature() 等
}

float readHumidity() {
    return 60.0;  // 替換：DHT.readHumidity() 等
}
