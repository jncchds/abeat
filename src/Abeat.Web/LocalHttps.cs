using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace Abeat.Web;

/// <summary>Self-signed certificate for serving ABeat over HTTPS on the LAN. Needed because ArcViewer
/// (an https page) may only fetch map zips from https origins; browsers block http LAN addresses as
/// mixed content. The certificate covers localhost, the machine name and all local IPv4 addresses and
/// is regenerated when those change. Browsers show a warning once per device until it is accepted.</summary>
public static class LocalHttps
{
    public static X509Certificate2 LoadOrCreate(string dataDir)
    {
        string path = Path.Combine(dataDir, "https-cert.pfx");
        var names = HostNames();
        var ips = LocalAddresses();
        if (File.Exists(path))
        {
            try
            {
                var existing = X509CertificateLoader.LoadPkcs12FromFile(path, null, X509KeyStorageFlags.Exportable);
                if (existing.NotAfter > DateTime.Now.AddDays(30) && Covers(existing, names, ips)) return existing;
            }
            catch (CryptographicException) { /* unreadable: recreate */ }
        }

        using var rsa = RSA.Create(2048);
        var req = new CertificateRequest($"CN=ABeat ({Environment.MachineName})", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        var san = new SubjectAlternativeNameBuilder();
        foreach (var n in names) san.AddDnsName(n);
        foreach (var ip in ips) san.AddIpAddress(ip);
        req.CertificateExtensions.Add(san.Build());
        req.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, false));
        req.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.DigitalSignature | X509KeyUsageFlags.KeyEncipherment, false));
        req.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension([new Oid("1.3.6.1.5.5.7.3.1")], false)); // server auth
        using var cert = req.CreateSelfSigned(DateTimeOffset.Now.AddDays(-1), DateTimeOffset.Now.AddYears(2));
        Directory.CreateDirectory(dataDir);
        File.WriteAllBytes(path, cert.Export(X509ContentType.Pfx));
        return X509CertificateLoader.LoadPkcs12FromFile(path, null, X509KeyStorageFlags.Exportable);
    }

    static List<string> HostNames() => new[] { "localhost", Environment.MachineName, Dns.GetHostName() }
        .Where(n => !string.IsNullOrWhiteSpace(n)).Distinct(StringComparer.OrdinalIgnoreCase).ToList();

    public static List<IPAddress> LocalAddresses() =>
        NetworkInterface.GetAllNetworkInterfaces()
            .Where(n => n.OperationalStatus == OperationalStatus.Up)
            .SelectMany(n => n.GetIPProperties().UnicastAddresses)
            .Select(a => a.Address)
            .Where(a => a.AddressFamily == AddressFamily.InterNetwork)
            .Append(IPAddress.Loopback)
            .Distinct()
            .ToList();

    static bool Covers(X509Certificate2 cert, List<string> names, List<IPAddress> ips)
    {
        var ext = cert.Extensions.OfType<X509SubjectAlternativeNameExtension>().FirstOrDefault();
        if (ext == null) return false;
        var dns = ext.EnumerateDnsNames().ToHashSet(StringComparer.OrdinalIgnoreCase);
        var addrs = ext.EnumerateIPAddresses().ToHashSet();
        return names.All(dns.Contains) && ips.All(addrs.Contains);
    }
}
