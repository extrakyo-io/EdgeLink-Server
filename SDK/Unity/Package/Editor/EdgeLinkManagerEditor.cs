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
    private string[] maskIds     = null;
    private int      selectedIdx = 0;
    private string   statusMsg   = "";
    private bool     isFetching  = false;

    private static readonly HttpClient http = CreateHttpClient();

    public override void OnInspectorGUI()
    {
        var m  = (EdgeLinkManager)target;
        var so = new SerializedObject(m);
        so.Update();

        // ── Server ───────────────────────────────────────
        EditorGUILayout.LabelField("Server", EditorStyles.boldLabel);
        m.serverUrl = EditorGUILayout.TextField("URL",      m.serverUrl);
        m.password  = EditorGUILayout.PasswordField("Password", m.password);
        m.maskId    = EditorGUILayout.TextField("Mask ID",  m.maskId);

        EditorGUILayout.Space(8);

        // ── 連線 ─────────────────────────────────────────
        EditorGUILayout.LabelField("連線", EditorStyles.boldLabel);
        m.protocol = (EdgeLinkManager.Protocol)EditorGUILayout.EnumPopup("Protocol", m.protocol);

        EditorGUI.indentLevel++;
        switch (m.protocol)
        {
            case EdgeLinkManager.Protocol.TCP:
                m.tcpHost = EditorGUILayout.TextField("Host", m.tcpHost);
                m.tcpPort = EditorGUILayout.IntField("Port", m.tcpPort);
                break;
            case EdgeLinkManager.Protocol.TCPListener:
                m.tcpListenPort = EditorGUILayout.IntField("Local Port", m.tcpListenPort);
                break;
            case EdgeLinkManager.Protocol.UDP:
                m.udpLocalPort = EditorGUILayout.IntField("Local Port", m.udpLocalPort);
                break;
        }
        EditorGUI.indentLevel--;

        so.ApplyModifiedProperties();

        EditorGUILayout.Space(8);

        // ── 設備偵測 ──────────────────────────────────────
        EditorGUILayout.LabelField("設備偵測", EditorStyles.boldLabel);
        m.deviceIdKey          = EditorGUILayout.TextField(
            new GUIContent("Device Id Key", "訊息中代表設備 ID 的欄位名稱，留空則不追蹤 Timeout"),
            m.deviceIdKey);
        m.deviceTimeoutSeconds = EditorGUILayout.FloatField(
            new GUIContent("Device Timeout (s)", "超過幾秒沒收到訊息視為設備離線（0 = 停用）"),
            m.deviceTimeoutSeconds);

        EditorGUILayout.Space(12);

        // ── 遮罩瀏覽工具 ──────────────────────────────────
        EditorGUILayout.LabelField("遮罩瀏覽工具", EditorStyles.boldLabel);

        using (new EditorGUI.DisabledScope(isFetching))
        {
            if (GUILayout.Button(isFetching ? "載入中..." : "拉取遮罩清單"))
                _ = FetchMasksAsync(m.serverUrl, m.password);
        }

        if (!string.IsNullOrEmpty(statusMsg))
            EditorGUILayout.HelpBox(statusMsg, MessageType.None);

        if (maskIds != null && maskIds.Length > 0)
        {
            EditorGUILayout.Space(4);
            selectedIdx = EditorGUILayout.Popup("選擇遮罩", selectedIdx, maskIds);
            if (GUILayout.Button("套用 Mask ID"))
            {
                Undo.RecordObject(m, "Set EdgeLink Mask ID");
                m.maskId  = maskIds[selectedIdx];
                statusMsg = $"Mask ID 已設為：{m.maskId}";
                EditorUtility.SetDirty(m);
                Repaint();
            }
        }

        if (GUI.changed) EditorUtility.SetDirty(m);
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
        // Unity Mono 的 HttpClientHandler.ServerCertificateCustomValidationCallback setter
        // 會丟 NotImplementedException；用全域 ServicePointManager 當 fallback。
        try { handler.ServerCertificateCustomValidationCallback = (msg, cert, chain, errors) => true; }
        catch (NotImplementedException)
        {
            System.Net.ServicePointManager.ServerCertificateValidationCallback = (s, c, ch, e) => true;
        }
        return new HttpClient(handler);
    }

    [Serializable] private class MaskListResponse { public string[] maskTypes; }
}
