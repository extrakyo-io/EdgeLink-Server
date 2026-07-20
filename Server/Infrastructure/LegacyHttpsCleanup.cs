using System.Diagnostics;
using System.Security.Cryptography.X509Certificates;

namespace EdgeLink.Infrastructure;

/// <summary>
/// 移除舊版 HTTPS 支援在機器上留下的東西。
///
/// EdgeLink 曾經會產生一張自簽憑證(CN=EdgeLink),用一個**編譯期常數**當私鑰密碼
/// (該常數就寫在公開的 MIT repo 裡),把它匯入 LocalMachine\My 與 **LocalMachine\Root**
/// (機器層級的信任錨),再用 netsh 綁到 HTTPS 埠。SAN 涵蓋 localhost、機器名與所有
/// 本機 IP。
///
/// 也就是說:任何能讀取安裝目錄的本機使用者都能取出私鑰,而這張憑證是機器**無條件
/// 信任**的 —— 等於在該機器上取得一個可以對任意站台做 MITM 的簽章能力。
///
/// 因此單純把產生憑證的程式碼刪掉是不夠的:已經安裝過的機器上,那張憑證和它的信任
/// 關係還在。這個類別負責把它清乾淨,由 --install 與 --uninstall 兩條路徑呼叫。
/// </summary>
public static class LegacyHttpsCleanup
{
    private const string LegacySubject = "CN=EdgeLink";
    private static readonly int[] LegacyPorts = { 8443 };

    /// <summary>
    /// 需要系統管理員權限,且必須在互動式 session 執行(服務帳號下 netsh/憑證存放區
    /// 的操作不一定有權限)。失敗只警告不中斷 —— 這是清理,不該擋住安裝/移除本身。
    /// </summary>
    public static void Run(int[]? extraPorts = null)
    {
        Console.WriteLine("[Cleanup] 移除舊版 HTTPS 憑證與埠繫結…");

        foreach (int port in LegacyPorts.Concat(extraPorts ?? Array.Empty<int>()).Distinct())
        {
            RunTool("netsh", $"http delete sslcert ipport=0.0.0.0:{port}");
            RunTool("netsh", $"http delete urlacl url=https://+:{port}/");
        }

        RemoveFromStore(StoreName.Root, "Trusted Root");
        RemoveFromStore(StoreName.My,   "個人");

        foreach (string f in new[] { "server.pfx", "server.cer" })
        {
            string p = Path.Combine(AppPaths.DataDir, f);
            try
            {
                if (File.Exists(p)) { File.Delete(p); Console.WriteLine($"[Cleanup] 已刪除 Data/{f}"); }
            }
            catch (Exception ex) { Console.WriteLine($"[Cleanup] 刪除 Data/{f} 失敗:{ex.Message}"); }
        }
    }

    private static void RemoveFromStore(StoreName storeName, string label)
    {
        try
        {
            using var store = new X509Store(storeName, StoreLocation.LocalMachine);
            store.Open(OpenFlags.ReadWrite);

            // 用 subject 比對而非 thumbprint:憑證每次產生都不同,而且我們已經不再
            // 保有原始的 pfx 可以算出 thumbprint。
            var doomed = store.Certificates
                .Where(c => string.Equals(c.Subject, LegacySubject, StringComparison.OrdinalIgnoreCase))
                .ToArray();

            foreach (var c in doomed)
            {
                store.Remove(c);
                Console.WriteLine($"[Cleanup] 已從「{label}」存放區移除 {c.Subject}({c.Thumbprint})");
            }
            if (doomed.Length == 0) Console.WriteLine($"[Cleanup] 「{label}」存放區沒有殘留憑證");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[Cleanup] 清理「{label}」存放區失敗:{ex.Message}");
        }
    }

    private static void RunTool(string exe, string args)
    {
        try
        {
            var p = Process.Start(new ProcessStartInfo(exe, args)
            {
                UseShellExecute        = false,
                RedirectStandardOutput = true,
                RedirectStandardError  = true,
                CreateNoWindow         = true,
            });
            p?.WaitForExit(10_000);
        }
        catch (Exception ex) { Console.WriteLine($"[Cleanup] {exe} {args}:{ex.Message}"); }
    }
}
