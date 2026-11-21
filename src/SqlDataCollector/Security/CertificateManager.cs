using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;
using SqlDataCollector.Alerts;

namespace SqlDataCollector.Security;

/// <summary>appsettings: "Https" section. Port 0 = HTTPS off (the Urls setting is used, e.g. plain HTTP in development).</summary>
public sealed class HttpsOptions
{
    public const string Section = "Https";

    /// <summary>HTTPS port served by Kestrel, e.g. 443. 0 = off.</summary>
    public int Port { get; set; }

    /// <summary>Optional plain HTTP port that only redirects to HTTPS. 0 = none.</summary>
    public int HttpRedirectPort { get; set; }

    /// <summary>Host name for the self-signed certificate created when none exists (default: machine name).</summary>
    public string? HostName { get; set; }

    /// <summary>Folder of the server certificate (default %ProgramData%\SqlDataCollector\certs). Only the service account and administrators should have access.</summary>
    public string? CertificateDirectory { get; set; }
}

public sealed record CertificateInfo(string Subject, string Issuer, string Thumbprint, DateTime NotBeforeUtc, DateTime NotAfterUtc,
    bool SelfSigned, string Source, IReadOnlyList<string> DnsNames)
{
    public int DaysLeft => (int)Math.Floor((NotAfterUtc - DateTime.UtcNow).TotalDays);
}

/// <summary>
/// The HTTPS server certificate: a PKCS#12 imported in the UI or created self-signed (stored DPAPI-protected, machine scope,
/// in the certificate folder), or a certificate of the Windows store LocalMachine\My (only its thumbprint is stored).
/// Kestrel asks for the current certificate on every TLS handshake, so a change applies without a restart.
/// </summary>
public sealed class CertificateManager(HttpsOptions options) : ICertificateStatus
{
    private const string PfxFile = "server.pfx.dpapi";
    private const string StoreFile = "server.store.json";
    private static readonly byte[] Entropy = "SqlDataCollector.Https.v1"u8.ToArray();
    private static readonly string ServerAuthOid = "1.3.6.1.5.5.7.3.1";

    private readonly Lock _lock = new();
    private X509Certificate2? _current;

    public bool Enabled => options.Port > 0;

    public string Directory => string.IsNullOrWhiteSpace(options.CertificateDirectory)
        ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "SqlDataCollector", "certs")
        : options.CertificateDirectory;

    public string DefaultHostName => string.IsNullOrWhiteSpace(options.HostName) ? Environment.MachineName.ToLowerInvariant() : options.HostName.Trim();

    /// <summary>The certificate Kestrel serves (loaded once, cached until the next change).</summary>
    public X509Certificate2? Current
    {
        get
        {
            lock (_lock)
            {
                _current ??= Load();
                return _current;
            }
        }
    }

    public CertificateInfo? Info => Current is { } c ? Describe(c, File.Exists(Path.Combine(Directory, StoreFile)) ? "Windows certificate store" : "Certificate file") : null;

    (string Subject, DateTime NotAfterUtc)? ICertificateStatus.Current => Enabled && Current is { } c ? (c.Subject, c.NotAfter.ToUniversalTime()) : null;

    /// <summary>On startup: makes sure HTTPS has a certificate (creates a self-signed one for the host name if none exists).</summary>
    public CertificateInfo EnsureCertificate()
    {
        if (Info is { } existing) return existing;
        return CreateAndUseSelfSigned(DefaultHostName);
    }

    public CertificateInfo CreateAndUseSelfSigned(string hostName)
    {
        using var cert = CreateSelfSigned(hostName);
        return ImportPfx(cert.Export(X509ContentType.Pkcs12), null);
    }

    /// <summary>Imports a PKCS#12 (.pfx / .p12) with its private key and uses it from now on.</summary>
    public CertificateInfo ImportPfx(byte[] pfx, string? password)
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("HTTPS certificate management requires Windows.");
        X509Certificate2 cert;
        try { cert = LoadPkcs12(pfx, password, X509KeyStorageFlags.Exportable); }
        catch (CryptographicException ex) { throw new InvalidOperationException($"The file could not be read as PKCS#12 (wrong password, not a .pfx/.p12, or its key cannot be stored for this account): {ex.Message}", ex); }
        using (cert)
        {
            Validate(cert);
            // re-export without password and protect with DPAPI (machine scope) + folder permissions
            var protectedBytes = ProtectedData.Protect(cert.Export(X509ContentType.Pkcs12), Entropy, DataProtectionScope.LocalMachine);
            System.IO.Directory.CreateDirectory(Directory);
            File.WriteAllBytes(Path.Combine(Directory, PfxFile), protectedBytes);
            File.Delete(Path.Combine(Directory, StoreFile));
        }
        return Reload();
    }

    /// <summary>Uses a certificate of LocalMachine\My (the service account needs read access to its private key).</summary>
    public CertificateInfo UseStoreCertificate(string thumbprint)
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("HTTPS certificate management requires Windows.");
        using var cert = FindInStore(thumbprint) ?? throw new InvalidOperationException($"Certificate {thumbprint} was not found in LocalMachine\\My.");
        Validate(cert);
        System.IO.Directory.CreateDirectory(Directory);
        File.WriteAllText(Path.Combine(Directory, StoreFile), JsonSerializer.Serialize(new { thumbprint = cert.Thumbprint }));
        File.Delete(Path.Combine(Directory, PfxFile));
        return Reload();
    }

    /// <summary>Server certificates of LocalMachine\My that have a private key and are currently valid.</summary>
    public IReadOnlyList<CertificateInfo> ListStoreCertificates()
    {
        if (!OperatingSystem.IsWindows()) return [];
        using var store = new X509Store(StoreName.My, StoreLocation.LocalMachine);
        store.Open(OpenFlags.ReadOnly | OpenFlags.OpenExistingOnly);
        var now = DateTime.Now;
        return store.Certificates.Cast<X509Certificate2>()
            .Where(c => c.HasPrivateKey && c.NotBefore <= now && c.NotAfter > now && IsServerCertificate(c))
            .Select(c => Describe(c, "Windows certificate store"))
            .OrderBy(c => c.Subject).ToList();
    }

    public CertificateInfo Reload()
    {
        lock (_lock)
        {
            var old = _current;
            _current = Load() ?? throw new InvalidOperationException("No certificate could be loaded.");
            old?.Dispose();
            return Describe(_current, File.Exists(Path.Combine(Directory, StoreFile)) ? "Windows certificate store" : "Certificate file");
        }
    }

    /// <summary>Self-signed server certificate for the host name, the machine name and localhost (2 years, RSA 2048).</summary>
    public static X509Certificate2 CreateSelfSigned(string hostName, int years = 2)
    {
        using var rsa = RSA.Create(2048);
        var req = new CertificateRequest($"CN={hostName}", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        var san = new SubjectAlternativeNameBuilder();
        foreach (var name in new[] { hostName, Environment.MachineName.ToLowerInvariant(), "localhost" }.Distinct(StringComparer.OrdinalIgnoreCase))
            san.AddDnsName(name);
        san.AddIpAddress(System.Net.IPAddress.Loopback);
        req.CertificateExtensions.Add(san.Build());
        req.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, true));
        req.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.DigitalSignature | X509KeyUsageFlags.KeyEncipherment, true));
        req.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension([new Oid(ServerAuthOid)], false));
        req.CertificateExtensions.Add(new X509SubjectKeyIdentifierExtension(req.PublicKey, false));
        return req.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddYears(years));
    }

    public static CertificateInfo Describe(X509Certificate2 c, string source)
    {
        var dns = c.Extensions.OfType<X509SubjectAlternativeNameExtension>().SelectMany(e => e.EnumerateDnsNames()).ToList();
        return new CertificateInfo(c.Subject, c.Issuer, c.Thumbprint, c.NotBefore.ToUniversalTime(), c.NotAfter.ToUniversalTime(),
            c.SubjectName.RawData.AsSpan().SequenceEqual(c.IssuerName.RawData), source, dns);
    }

    private X509Certificate2? Load()
    {
        if (!OperatingSystem.IsWindows()) return null;
        var storeRef = Path.Combine(Directory, StoreFile);
        if (File.Exists(storeRef))
        {
            var tp = JsonDocument.Parse(File.ReadAllText(storeRef)).RootElement.GetProperty("thumbprint").GetString();
            return tp is null ? null : FindInStore(tp);
        }
        var file = Path.Combine(Directory, PfxFile);
        if (!File.Exists(file)) return null;
        var pfx = ProtectedData.Unprotect(File.ReadAllBytes(file), Entropy, DataProtectionScope.LocalMachine);
        return LoadPkcs12(pfx, null, X509KeyStorageFlags.DefaultKeySet);
    }

    /// <summary>
    /// The private key goes to the key store of the account running the process (a Windows service loads its profile);
    /// the machine key store is only the fallback, since writing it requires administrator rights.
    /// </summary>
    private static X509Certificate2 LoadPkcs12(byte[] pfx, string? password, X509KeyStorageFlags extra)
    {
        try { return X509CertificateLoader.LoadPkcs12(pfx, password, X509KeyStorageFlags.UserKeySet | extra); }
        catch (CryptographicException first)
        {
            try { return X509CertificateLoader.LoadPkcs12(pfx, password, X509KeyStorageFlags.MachineKeySet | extra); }
            catch (CryptographicException) { throw first; }
        }
    }

    [SupportedOSPlatform("windows")]
    private static X509Certificate2? FindInStore(string thumbprint)
    {
        using var store = new X509Store(StoreName.My, StoreLocation.LocalMachine);
        store.Open(OpenFlags.ReadOnly | OpenFlags.OpenExistingOnly);
        var found = store.Certificates.Find(X509FindType.FindByThumbprint, thumbprint.Replace(" ", "").Trim(), validOnly: false);
        return found.Count > 0 ? found[0] : null;
    }

    private static void Validate(X509Certificate2 cert)
    {
        if (!cert.HasPrivateKey) throw new InvalidOperationException("The certificate has no private key (export it as .pfx/.p12 with the private key).");
        if (cert.NotAfter <= DateTime.Now) throw new InvalidOperationException($"The certificate expired on {cert.NotAfter:yyyy-MM-dd}.");
        if (!IsServerCertificate(cert)) throw new InvalidOperationException("The certificate is not valid for server authentication (Enhanced Key Usage).");
        try
        {
            using var key = (AsymmetricAlgorithm?)cert.GetRSAPrivateKey() ?? cert.GetECDsaPrivateKey();
            if (key is null) throw new InvalidOperationException("Only RSA and ECDSA certificates are supported.");
        }
        catch (CryptographicException ex)
        {
            throw new InvalidOperationException($"The private key cannot be used by this account ({Environment.UserName}): {ex.Message} " +
                "For a store certificate grant the service account read access (certlm.msc → Manage Private Keys).", ex);
        }
    }

    private static bool IsServerCertificate(X509Certificate2 c)
    {
        var eku = c.Extensions.OfType<X509EnhancedKeyUsageExtension>().FirstOrDefault();
        return eku is null || eku.EnhancedKeyUsages.Cast<Oid>().Any(o => o.Value == ServerAuthOid);
    }

    private static void EnsureWindows()
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("HTTPS certificate management requires Windows.");
    }
}
