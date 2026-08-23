using System;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading.Tasks;
using UnityEditor;
using UnityEngine;

[CustomEditor(typeof(EdgeLinkManager))]
public class EdgeLinkManagerEditor : Editor
{
    // 全部走 SerializedProperty,不直接寫 target 的欄位。
    //
    // 先前是 `m.serverUrl = EditorGUILayout.TextField(...)` 這種直接賦值,靠結尾的
    // `if (GUI.changed) EditorUtility.SetDirty(m)` 補髒標記 —— 存檔是會生效,但:
    //   • Undo 完全無效:沒有 Undo.RecordObject,Ctrl+Z revert 不掉 Inspector 的修改。
    //   • prefab override 不成立:直接寫欄位不會登記成對 prefab 的覆寫,
    //     藍色 override 標記與右鍵 Revert 都不會出現。
    //   • 同一個檔案裡的「套用 Mask ID」按鈕反而有做 Undo.RecordObject,兩套做法並存。
    // PropertyField 把這三件事都免費處理掉。
    private SerializedProperty _serverUrl, _password, _maskId;
    private SerializedProperty _protocol, _tcpHost, _tcpPort, _tcpListenPort;
    private SerializedProperty _udpLocalPort, _udpTargetHost, _udpTargetPort;
    private SerializedProperty _deviceIdKey, _deviceTimeoutSeconds;

    private string[] maskIds     = null;
    private int      selectedIdx = 0;
    private string   statusMsg   = "";
    private bool     isFetching  = false;

    private static readonly HttpClient http = CreateHttpClient();

    private void OnEnable()
    {
        _serverUrl            = serializedObject.FindProperty("serverUrl");
        _password             = serializedObject.FindProperty("password");
        _maskId               = serializedObject.FindProperty("maskId");
        _protocol             = serializedObject.FindProperty("protocol");
        _tcpHost              = serializedObject.FindProperty("tcpHost");
        _tcpPort              = serializedObject.FindProperty("tcpPort");
        _tcpListenPort        = serializedObject.FindProperty("tcpListenPort");
        _udpLocalPort         = serializedObject.FindProperty("udpLocalPort");
        _udpTargetHost        = serializedObject.FindProperty("udpTargetHost");
        _udpTargetPort        = serializedObject.FindProperty("udpTargetPort");
        _deviceIdKey          = serializedObject.FindProperty("deviceIdKey");
        _deviceTimeoutSeconds = serializedObject.FindProperty("deviceTimeoutSeconds");
    }

    public override void OnInspectorGUI()
    {
        serializedObject.Update();

        // ── Server ───────────────────────────────────────
        EditorGUILayout.LabelField("Server", EditorStyles.boldLabel);
        EditorGUILayout.PropertyField(_serverUrl, new GUIContent("URL"));

        // PasswordField 沒有 SerializedProperty 版本,自己接 —— 但要用
        // DelayedTextField 之外的方式時仍需明確把值寫回 property,
        // 這樣 undo / override 才會跟著走。
        EditorGUI.BeginChangeCheck();
        string pw = EditorGUILayout.PasswordField("Password", _password.stringValue);
        if (EditorGUI.EndChangeCheck()) _password.stringValue = pw;

        EditorGUILayout.PropertyField(_maskId, new GUIContent("Mask ID"));

        EditorGUILayout.Space(8);

        // ── 連線 ─────────────────────────────────────────
        EditorGUILayout.LabelField("連線", EditorStyles.boldLabel);
        EditorGUILayout.PropertyField(_protocol, new GUIContent("Protocol"));

        EditorGUI.indentLevel++;
        switch ((EdgeLinkManager.Protocol)_protocol.enumValueIndex)
        {
            case EdgeLinkManager.Protocol.TCP:
                EditorGUILayout.PropertyField(_tcpHost, new GUIContent("Host"));
                EditorGUILayout.PropertyField(_tcpPort, new GUIContent("Port"));
                break;
            case EdgeLinkManager.Protocol.TCPListener:
                EditorGUILayout.PropertyField(_tcpListenPort, new GUIContent("Local Port"));
                break;
            case EdgeLinkManager.Protocol.UDP:
                EditorGUILayout.PropertyField(_udpLocalPort, new GUIContent("Local Port"));
                // 送出的目的地與收資料的埠不是同一個:EdgeLink 的 UDP 埠是
                // 「聽 remotePort、轉發到 localPort」,要送進去得打它的監聽埠。
                EditorGUILayout.PropertyField(_udpTargetHost,
                    new GUIContent("Target Host", "要送資料才需要;留空表示只收不送"));
                EditorGUILayout.PropertyField(_udpTargetPort,
                    new GUIContent("Target Port", "EdgeLink 該 UDP 埠的『監聽埠』,與上面的 Local Port 不同"));
                break;
        }
        EditorGUI.indentLevel--;

        EditorGUILayout.Space(8);

        // ── 設備偵測 ──────────────────────────────────────
        EditorGUILayout.LabelField("設備偵測", EditorStyles.boldLabel);
        EditorGUILayout.PropertyField(_deviceIdKey,
            new GUIContent("Device Id Key", "訊息中代表設備 ID 的欄位名稱，留空則不追蹤 Timeout"));
        EditorGUILayout.PropertyField(_deviceTimeoutSeconds,
            new GUIContent("Device Timeout (s)", "超過幾秒沒收到訊息視為設備離線（0 = 停用）"));

        EditorGUILayout.Space(12);

        // ── 遮罩瀏覽工具 ──────────────────────────────────
        EditorGUILayout.LabelField("遮罩瀏覽工具", EditorStyles.boldLabel);

        using (new EditorGUI.DisabledScope(isFetching))
        {
            if (GUILayout.Button(isFetching ? "載入中..." : "拉取遮罩清單"))
                _ = FetchMasksAsync(_serverUrl.stringValue, _password.stringValue);
        }

        if (!string.IsNullOrEmpty(statusMsg))
            EditorGUILayout.HelpBox(statusMsg, MessageType.None);

        if (maskIds != null && maskIds.Length > 0)
        {
            EditorGUILayout.Space(4);
            selectedIdx = EditorGUILayout.Popup("選擇遮罩", selectedIdx, maskIds);
            if (GUILayout.Button("套用 Mask ID"))
            {
                // 透過 property 寫入 —— undo 與 prefab override 都由
                // ApplyModifiedProperties 一併處理,不必自己 RecordObject + SetDirty。
                _maskId.stringValue = maskIds[selectedIdx];
                statusMsg = $"Mask ID 已設為：{maskIds[selectedIdx]}";
                Repaint();
            }
        }

        serializedObject.ApplyModifiedProperties();
    }

    private async Task FetchMasksAsync(string serverUrl, string password)
    {
        isFetching = true;
        statusMsg  = "";
        Repaint();
        try
        {
            if (!await LoginAsync(serverUrl, password)) { statusMsg = "登入失敗，請確認 URL 與密碼。"; return; }
            var resp = await http.GetAsync($"{serverUrl.TrimEnd('/')}/api/masks");
            resp.EnsureSuccessStatusCode();
            var parsed = JsonUtility.FromJson<MaskListResponse>(await resp.Content.ReadAsStringAsync());
            maskIds    = parsed?.maskTypes ?? Array.Empty<string>();
            selectedIdx = 0;
            statusMsg  = $"共找到 {maskIds.Length} 個遮罩";
        }
        catch (Exception ex) { statusMsg = $"錯誤：{ex.Message}"; }
        finally { isFetching = false; Repaint(); }
    }

    private async Task<bool> LoginAsync(string serverUrl, string password)
    {
        string body    = $"{{\"password\":\"{EscapeJson(password)}\"}}";
        var    content = new StringContent(body, Encoding.UTF8, "application/json");
        var    resp    = await http.PostAsync($"{serverUrl.TrimEnd('/')}/api/auth/login", content);
        return resp.IsSuccessStatusCode;
    }

    private static string EscapeJson(string s) => s.Replace("\\", "\\\\").Replace("\"", "\\\"");

    private static HttpClient CreateHttpClient()
    {
        var handler = new HttpClientHandler
        {
            CookieContainer = new CookieContainer(),
            UseCookies      = true,
        };
        // 這裡刻意不裝任何憑證驗證回呼 —— 用系統預設的驗證。
        //
        // 先前是 `=> true`(接受任何憑證),而 Mono 的 fallback 走的是
        // ServicePointManager.ServerCertificateValidationCallback,那是**行程全域**的：
        // 等於關掉整個 Unity Editor 的憑證驗證,不只這一個 HttpClient。
        // 這個編輯器面板會帶著管理密碼登入,搭配 HTTPS 反向代理時形同對中間人無防禦。
        // v2.4.3 的變更紀錄宣稱已移除,但只改到 Runtime,Editor 這支被漏掉了。
        return new HttpClient(handler);
    }

    [Serializable] private class MaskListResponse { public string[] maskTypes; }
}
