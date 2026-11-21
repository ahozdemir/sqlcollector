using System.Net;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;
using Microsoft.Extensions.Configuration;
using SqlDataCollector.Export;
using SqlDataCollector.Infrastructure;
using SqlDataCollector.Security;

namespace SqlDataCollector.Tests.Unit;

public class LocalAdminSetupTests
{
    [Theory]
    [InlineData("short", "short", "at least 12")]
    [InlineData("LongEnough!2026", "Different!2026", "do not match")]
    [InlineData("aaaaaaaaaaaaaa", "aaaaaaaaaaaaaa", "too simple")]
    [InlineData("LongEnough!2026", "LongEnough!2026", null)]
    public void Password_Rules(string pwd, string confirm, string? error)
    {
        var result = LocalAdminStore.Validate(pwd, confirm);
        if (error is null) Assert.Null(result);
        else Assert.Contains(error, result);
    }

    [Fact]
    public void InitialPassword_OnlyFromTheServerItself()
    {
        Assert.True(LocalAdminStore.IsLocalRequest(IPAddress.Loopback));
        Assert.True(LocalAdminStore.IsLocalRequest(IPAddress.IPv6Loopback));
        Assert.False(LocalAdminStore.IsLocalRequest(IPAddress.Parse("10.1.2.3")));
        Assert.False(LocalAdminStore.IsLocalRequest(null));
    }
}

public class SecretProtectorTests
{
    private static SecretProtector With(string? key) =>
        new(new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { [SecretProtector.ConfigKey] = key }).Build());

    [Fact]
    public void RoundTrip_AndCiphertextDiffersEachTime()
    {
        var p = With(SecretProtector.NewKey());
        var a = p.Protect("{\"private_key\":\"secret\"}");
        Assert.StartsWith("v1:", a);
        Assert.DoesNotContain("secret", a);
        Assert.NotEqual(a, p.Protect("{\"private_key\":\"secret\"}"));
        Assert.Equal("{\"private_key\":\"secret\"}", p.Unprotect(a));
    }

    [Fact]
    public void WrongOrMissingKey_IsReported()
    {
        var stored = With(SecretProtector.NewKey()).Protect("x");
        Assert.Contains("differs", Assert.Throws<InvalidOperationException>(() => With(SecretProtector.NewKey()).Unprotect(stored)).Message);
        Assert.False(With(null).IsConfigured);
        Assert.False(With("dG9vLXNob3J0").IsConfigured);   // not 32 bytes
        Assert.Throws<InvalidOperationException>(() => With(null).Protect("x"));
    }
}

public class GcsKeyTests
{
    public static string FakeKeyJson(string type = "service_account", string? email = "sdc@proj.iam.gserviceaccount.com")
    {
        using var rsa = RSA.Create(2048);
        return JsonSerializer.Serialize(new Dictionary<string, string?>
        {
            ["type"] = type, ["project_id"] = "proj", ["private_key_id"] = "abc123", ["client_email"] = email, ["client_id"] = "1",
            ["private_key"] = rsa.ExportPkcs8PrivateKeyPem(), ["token_uri"] = "https://oauth2.googleapis.com/token"
        });
    }

    [Fact]
    public void ParseKey_AcceptsServiceAccountKeys_Only()
    {
        var info = GcsCredentialStore.ParseKey(FakeKeyJson(), "tester");
        Assert.Equal("sdc@proj.iam.gserviceaccount.com", info.ClientEmail);
        Assert.Equal("proj", info.ProjectId);
        Assert.Contains("not a service account", Assert.Throws<InvalidOperationException>(() => GcsCredentialStore.ParseKey(FakeKeyJson("authorized_user"), "t")).Message);
        Assert.Contains("client_email", Assert.Throws<InvalidOperationException>(() => GcsCredentialStore.ParseKey(FakeKeyJson(email: null), "t")).Message);
        Assert.Contains("not valid JSON", Assert.Throws<InvalidOperationException>(() => GcsCredentialStore.ParseKey("{nope", "t")).Message);
    }
}

public class DbSetupScriptTests
{
    [Fact]
    public void Embedded_CreatesDatabases_Login_Permissions_AndReader()
    {
        var scripts = DbSetup.Scripts(new DbSetupOptions("Sdc]Meta", "DWH", @"CORP\svc-sdc"));
        Assert.Equal(["01_databases_and_permissions.sql", "02_etl_schema.sql"], scripts.Select(s => s.FileName));
        var sql = scripts[0].Sql;
        Assert.Contains("IF DB_ID(N'Sdc]Meta') IS NULL CREATE DATABASE [Sdc]]Meta];", sql);   // quoted identifier and literal
        Assert.Contains(@"CREATE LOGIN [CORP\svc-sdc] FROM WINDOWS", sql);
        Assert.Contains(@"ALTER ROLE db_ddladmin ADD MEMBER [CORP\svc-sdc]", sql);
        Assert.Contains(@"GRANT ALTER ANY DATASPACE TO [CORP\svc-sdc]", sql);
        Assert.Contains("CREATE USER [sdc_export_reader] WITHOUT LOGIN", sql);
        Assert.Contains(@"GRANT IMPERSONATE ON USER::[sdc_export_reader] TO [CORP\svc-sdc]", sql);
        Assert.StartsWith("USE [Sdc]]Meta];", scripts[1].Sql);
        Assert.Contains("etl.ExtractJob", scripts[1].Sql);
    }

    [Fact]
    public void Central_AddsTheHangfireScriptForTheDba()
    {
        var scripts = DbSetup.Scripts(new DbSetupOptions("Meta", "DWH", @"CORP\gmsa$", Central: true, HangfireDb: "HangfireDB", HangfireSchema: "hf_sdc"));
        var hf = Assert.Single(scripts, s => s.FileName == "03_hangfire_central.sql").Sql;
        Assert.Contains("USE [HangfireDB];", hf);
        Assert.Contains("[hf_sdc]", hf);
        Assert.Contains(@"GRANT SELECT, INSERT, UPDATE, DELETE ON SCHEMA::[hf_sdc] TO [CORP\gmsa$]", hf);
    }

    [Theory]
    [InlineData("Same", "same", @"CORP\x", "must be different")]
    [InlineData("Meta", "DWH", "justname", "DOMAIN")]
    [InlineData("", "DWH", null, "metadata database")]
    public void Options_AreValidated(string meta, string dwh, string? account, string error) =>
        Assert.Contains(error, Assert.Throws<ArgumentException>(() => DbSetup.Scripts(new DbSetupOptions(meta, dwh, account))).Message);
}

public sealed class CertificateManagerTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "sdc-cert-" + Guid.NewGuid().ToString("N")[..8]);

    private CertificateManager Manager() => new(new HttpsOptions { Port = 443, HostName = "sdc.test", CertificateDirectory = _dir });

    [Fact]
    public void SelfSigned_IsCreated_StoredProtected_AndReloaded()
    {
        if (!OperatingSystem.IsWindows()) return;
        var info = Manager().EnsureCertificate();
        Assert.True(info.SelfSigned);
        Assert.Contains("sdc.test", info.DnsNames);
        Assert.Contains("localhost", info.DnsNames);
        Assert.InRange(info.DaysLeft, 720, 731);
        var file = Assert.Single(Directory.GetFiles(_dir));
        Assert.DoesNotContain("sdc.test", File.ReadAllText(file));        // DPAPI-protected, not a readable PFX
        Assert.Equal(info.Thumbprint, Manager().Info!.Thumbprint);        // a new process loads the same certificate
        Assert.True(Manager().Current!.HasPrivateKey);
    }

    [Fact]
    public void Pkcs12_IsImported_WithItsPassword_AndBadFilesAreRejected()
    {
        if (!OperatingSystem.IsWindows()) return;
        var m = Manager();
        using var cert = CertificateManager.CreateSelfSigned("import.test");
        var info = m.ImportPfx(cert.Export(X509ContentType.Pkcs12, "P@ss-1234"), "P@ss-1234");
        Assert.Equal(cert.Thumbprint, info.Thumbprint);
        Assert.Equal(cert.Thumbprint, m.Current!.Thumbprint);

        Assert.Contains("wrong password", Assert.Throws<InvalidOperationException>(() => m.ImportPfx(cert.Export(X509ContentType.Pkcs12, "a"), "b")).Message);
        Assert.Throws<InvalidOperationException>(() => m.ImportPfx(cert.Export(X509ContentType.Cert), null));   // no private key / not PKCS#12

        using var rsa = RSA.Create(2048);
        var req = new CertificateRequest("CN=client", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        req.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension([new Oid("1.3.6.1.5.5.7.3.2")], false));   // client auth only
        using var client = req.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddYears(1));
        Assert.Contains("server authentication", Assert.Throws<InvalidOperationException>(() => m.ImportPfx(client.Export(X509ContentType.Pkcs12), null)).Message);
        Assert.Equal(cert.Thumbprint, m.Current!.Thumbprint);   // a rejected file does not replace the certificate
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch { /* best effort */ }
    }
}
