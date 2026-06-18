using System.Diagnostics;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace EdgeLink.Infrastructure;

public static class CertificateHelper
{
    private static readonly string CertPath  = Path.Combine(AppPaths.DataDir, "server.pfx");
    private const string           CertPass  = "edgelink-cert";
    private const string           AppId     = "{7b9d6b4a-3c22-4f5e-9a11-df6a32e5b902}";

    private const X509KeyStorageFlags KeyFlags =
        X509KeyStorageFlags.MachineKeySet |
        X509KeyStorageFlags.PersistKeySet |
        X509KeyStorageFlags.Exportable;

    public static X509Certificate2 GetOrCreate()
    {
        if (File.Exists(CertPath))
        {
            try { return new X509Certificate2(CertPath, CertPass, KeyFlags); }
            catch (Exception ex)
            {
                AppLogger.Warning($"[Cert] Existing cert unreadable ({ex.Message}), regenerating.");
                File.Delete(CertPath);
            }
        }

        AppLogger.Log("[Cert] Generating self-signed certificate...");
        var cert = CreateSelfSigned();
        byte[] pfx = cert.Export(X509ContentType.Pfx, CertPass);
        File.WriteAllBytes(CertPath, pfx);
        AppLogger.Log("[Cert] Saved to Data/server.pfx (valid 10 years)");
        // Reload with persistent machine key so HTTP.sys can find the private key
        return new X509Certificate2(pfx, CertPass, KeyFlags);
    }

    private static X509Certificate2 CreateSelfSigned()
    {
        using var rsa = RSA.Create(2048);
        var req = new CertificateRequest("CN=EdgeLink", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        req.CertificateExtensions.Add(new X509KeyUsageExtension(
            X509KeyUsageFlags.DigitalSignature | X509KeyUsageFlags.KeyEncipherment, false));
        req.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension(
            new OidCollection { new Oid("1.3.6.1.5.5.7.3.1") }, false));

        var san = new SubjectAlternativeNameBuilder();
        san.AddDnsName("localhost");
        san.AddDnsName(Environment.MachineName);
        san.AddIpAddress(System.Net.IPAddress.Loopback);
        san.AddIpAddress(System.Net.IPAddress.IPv6Loopback);

        // Add all current local network IPs so browsers trust the cert on LAN
        foreach (var addr in System.Net.Dns.GetHostAddresses(Environment.MachineName))
        {
            if (addr.AddressFamily is System.Net.Sockets.AddressFamily.InterNetwork
                                   or System.Net.Sockets.AddressFamily.InterNetworkV6)
                san.AddIpAddress(addr);
        }

        req.CertificateExtensions.Add(san.Build());
        return req.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddYears(10));
    }

    // Installs cert to LocalMachine store and registers netsh sslcert binding.
    // Must be called from an interactive admin session (e.g. during --install).
    public static void EnsureHttpsBound(X509Certificate2 cert, int port)
    {
        var certPath  = Path.GetFullPath(CertPath);
        var thumbprint = cert.Thumbprint.ToLowerInvariant();

        // Import-PfxCertificate correctly sets up machine key containers + ACLs
        // which X509Store.Add() does not reliably do.
        var cerPath = Path.ChangeExtension(certPath, ".cer");
        var script = $@"
$pass = ConvertTo-SecureString '{CertPass}' -AsPlainText -Force
$cert = Import-PfxCertificate -FilePath '{certPath}' -CertStoreLocation Cert:\LocalMachine\My -Password $pass -Exportable
# Export public cert and trust it on this machine
$certBytes = $cert.Export([System.Security.Cryptography.X509Certificates.X509ContentType]::Cert)
[System.IO.File]::WriteAllBytes('{cerPath}', $certBytes)
Import-Certificate -FilePath '{cerPath}' -CertStoreLocation Cert:\LocalMachine\Root | Out-Null
netsh http delete sslcert ipport=0.0.0.0:{port} | Out-Null
netsh http add sslcert ipport=0.0.0.0:{port} certhash={thumbprint} appid='{AppId}'
";
        var tmp = Path.Combine(Path.GetTempPath(), "edgelink-cert-setup.ps1");
        File.WriteAllText(tmp, script, System.Text.Encoding.UTF8);

        try
        {
            var psi = new ProcessStartInfo("powershell",
                $"-NoProfile -NonInteractive -ExecutionPolicy Bypass -File \"{tmp}\"")
            {
                UseShellExecute        = false,
                CreateNoWindow         = true,
                RedirectStandardOutput = true,
                RedirectStandardError  = true,
            };
            var p = Process.Start(psi)!;
            string stdout = p.StandardOutput.ReadToEnd();
            string stderr = p.StandardError.ReadToEnd();
            p.WaitForExit(15_000);

            if (p.ExitCode != 0)
            {
                AppLogger.Warning($"[Cert] Setup script failed (exit {p.ExitCode}): {stderr.Trim()}");
                throw new Exception($"Cert setup failed: {stderr.Trim()}");
            }
            AppLogger.Log($"[Cert] HTTPS cert bound to port {port} (thumbprint: {thumbprint})");
        }
        finally { try { File.Delete(tmp); } catch { } }
    }

    private static void RunNetsh(string args)
    {
        try
        {
            var psi = new ProcessStartInfo("netsh", args)
            {
                UseShellExecute        = false,
                CreateNoWindow         = true,
                RedirectStandardOutput = true,
                RedirectStandardError  = true,
            };
            var p = Process.Start(psi)!;
            string output = p.StandardOutput.ReadToEnd();
            p.WaitForExit(5000);
            if (p.ExitCode != 0)
                AppLogger.Warning($"[Cert] netsh '{args}' exited {p.ExitCode}: {output.Trim()}");
            else
                AppLogger.Log($"[Cert] netsh '{args}': OK");
        }
        catch (Exception ex) { AppLogger.Warning($"[Cert] netsh '{args}': {ex.Message}"); }
    }
}
