using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Google.Apis.Auth.OAuth2;
using SqlDataCollector.Export;

namespace SqlDataCollector.Tests.Unit;

/// <summary>GCS sign-in with Workload Identity Federation configurations (keyless) and service account keys.</summary>
public sealed class GcsCredentialsTests
{
    private const string Audience = "//iam.googleapis.com/projects/123456789/locations/global/workloadIdentityPools/onprem-pool/providers/adfs";
    private const string Impersonation = "https://iamcredentials.googleapis.com/v1/projects/-/serviceAccounts/sdc-export@my-project.iam.gserviceaccount.com:generateAccessToken";

    private static string Wif(object? credentialSource = null, string? tokenUrl = "https://sts.googleapis.com/v1/token", string? impersonation = Impersonation,
        string? audience = Audience, string? subjectTokenType = "urn:ietf:params:oauth:token-type:jwt")
    {
        var d = new Dictionary<string, object?> { ["type"] = "external_account", ["audience"] = audience, ["subject_token_type"] = subjectTokenType, ["token_url"] = tokenUrl,
            ["credential_source"] = credentialSource ?? new { file = @"C:\SqlHarbor\token\oidc.jwt" } };
        if (impersonation is not null) d["service_account_impersonation_url"] = impersonation;
        return JsonSerializer.Serialize(d.Where(kv => kv.Value is not null).ToDictionary());
    }

    private static string ServiceAccountKey() => JsonSerializer.Serialize(new Dictionary<string, string>
    {
        ["type"] = "service_account", ["project_id"] = "my-project", ["private_key_id"] = "abc123", ["client_email"] = "sdc@my-project.iam.gserviceaccount.com",
        ["client_id"] = "1", ["private_key"] = RSA.Create(2048).ExportPkcs8PrivateKeyPem(), ["token_uri"] = "https://oauth2.googleapis.com/token"
    });

    [Fact]
    public void WorkloadIdentityFederation_FileSourced_WithImpersonation()
    {
        var f = GcsCredentials.Inspect(Wif());
        Assert.Equal(GcsCredentialKind.WorkloadIdentityFederation, f.Kind);
        Assert.Equal("sdc-export@my-project.iam.gserviceaccount.com", f.Identity);
        Assert.Equal(Audience, f.Audience);
        Assert.Equal(@"token file C:\SqlHarbor\token\oidc.jwt", f.TokenSource);
        Assert.Equal("123456789", f.ProjectId);
        Assert.Null(f.KeyId);
    }

    [Fact]
    public void WorkloadIdentityFederation_DirectAccess_NamesThePoolAndProvider()
    {
        var f = GcsCredentials.Inspect(Wif(impersonation: null));
        Assert.Equal("federated principal of pool onprem-pool / provider adfs", f.Identity);
    }

    [Theory]
    [InlineData("url", "token URL http://169.254.169.254/metadata/identity/oauth2/token")]
    [InlineData("aws", "AWS instance identity")]
    public void WorkloadIdentityFederation_UrlAndAwsSources(string kind, string expected)
    {
        object source = kind == "url"
            ? new { url = "http://169.254.169.254/metadata/identity/oauth2/token?api-version=2018-02-01&resource=api://x", headers = new { Metadata = "True" } }
            : new { environment_id = "aws1", region_url = "http://169.254.169.254/latest/meta-data/placement/availability-zone" };
        Assert.Equal(expected, GcsCredentials.Inspect(Wif(source)).TokenSource);
    }

    [Theory]
    [InlineData("executable", "Executable-sourced credentials are not supported")]
    [InlineData("certificate", "X.509 certificate-sourced credentials are not supported")]
    [InlineData("other", "Unsupported credential_source")]
    public void WorkloadIdentityFederation_UnsupportedSources_AreRefused(string kind, string message)
    {
        object source = kind switch
        {
            "executable" => new { executable = new { command = "C:\\get-token.exe", timeout_millis = 5000 } },
            "certificate" => new { certificate = new { use_default_certificate_config = true } },
            _ => new { something = "x" }
        };
        Assert.Contains(message, Assert.Throws<InvalidOperationException>(() => GcsCredentials.Inspect(Wif(source))).Message);
    }

    [Theory]
    [InlineData("https://evil.example.com/v1/token", null, "token_url must be an https address of Google")]
    [InlineData("http://sts.googleapis.com/v1/token", null, "token_url must be an https address of Google")]
    [InlineData("https://sts.googleapis.com.evil.com/v1/token", null, "token_url must be an https address of Google")]
    [InlineData(null, null, "has no token_url")]
    [InlineData("https://sts.googleapis.com/v1/token", "https://evil.example.com/sa:generateAccessToken", "service_account_impersonation_url must be an https address of Google")]
    public void WorkloadIdentityFederation_TokensGoOnlyToGoogle(string? tokenUrl, string? impersonation, string message) =>
        Assert.Contains(message, Assert.Throws<InvalidOperationException>(() => GcsCredentials.Inspect(Wif(tokenUrl: tokenUrl, impersonation: impersonation ?? Impersonation))).Message);

    [Theory]
    [InlineData(null, "urn:ietf:params:oauth:token-type:jwt", "no valid audience")]
    [InlineData("https://example.com/aud", "urn:ietf:params:oauth:token-type:jwt", "no valid audience")]
    [InlineData(Audience, null, "no subject_token_type")]
    public void WorkloadIdentityFederation_NeedsAudienceAndTokenType(string? audience, string? tokenType, string message) =>
        Assert.Contains(message, Assert.Throws<InvalidOperationException>(() => GcsCredentials.Inspect(Wif(audience: audience, subjectTokenType: tokenType))).Message);

    [Theory]
    [InlineData("""{"type":"authorized_user","client_id":"x","client_secret":"y","refresh_token":"z"}""", "personal gcloud login")]
    [InlineData("""{"type":"external_account_authorized_user","audience":"x"}""", "workforce sign-in of a person")]
    [InlineData("""{"type":"something"}""", "Not a supported Google credential file")]
    [InlineData("[1,2]", "JSON object expected")]
    [InlineData("not json", "not valid JSON")]
    [InlineData("""{"type":"service_account","client_email":"a@b"}""", "no client_email or private_key")]
    public void OtherFiles_AreRefusedWithAClearMessage(string json, string message) =>
        Assert.Contains(message, Assert.Throws<InvalidOperationException>(() => GcsCredentials.Inspect(json)).Message);

    [Fact]
    public void ServiceAccountKeys_StillWork()
    {
        var json = ServiceAccountKey();
        var f = GcsCredentials.Inspect(json);
        Assert.Equal((GcsCredentialKind.ServiceAccountKey, "sdc@my-project.iam.gserviceaccount.com", "my-project", "abc123"), (f.Kind, f.Identity, f.ProjectId, f.KeyId));
        Assert.IsType<ServiceAccountCredential>(GcsCredentials.Create(json).UnderlyingCredential);
        var info = GcsCredentialStore.ParseKey(json, "admin");
        Assert.False(info.IsWorkloadIdentityFederation);
    }

    [Fact]
    public void KeyInfoOfOlderVersions_ReadsAsServiceAccountKey()
    {
        var old = """{"ClientEmail":"sdc@p.iam.gserviceaccount.com","ProjectId":"p","PrivateKeyId":"k","UploadedBy":"admin","UploadedAtUtc":"2026-11-01T10:00:00Z"}""";
        var info = JsonSerializer.Deserialize<GcsKeyInfo>(old)!;
        Assert.Equal(GcsCredentialKind.ServiceAccountKey, info.Kind);
        Assert.False(info.IsWorkloadIdentityFederation);
    }

    [Fact]
    public void ParseKey_DescribesAWorkloadIdentityFederationConfiguration()
    {
        var info = GcsCredentialStore.ParseKey(Wif(), "admin");
        Assert.True(info.IsWorkloadIdentityFederation);
        Assert.Equal("sdc-export@my-project.iam.gserviceaccount.com", info.ClientEmail);
        Assert.Equal(Audience, info.Audience);
        var roundTrip = JsonSerializer.Deserialize<GcsKeyInfo>(JsonSerializer.Serialize(info))!;
        Assert.Equal(info.Kind, roundTrip.Kind);
    }

    // ------------------------------------------------------------------ end to end: external token → STS → impersonation

    [Theory]
    [InlineData("file", true)]
    [InlineData("file", false)]
    [InlineData("url", true)]
    public async Task Create_ExchangesTheExternalTokenForAGoogleToken(string source, bool impersonate)
    {
        await using var google = await FakeGoogle.StartAsync();
        var tokenFile = Path.Combine(Path.GetTempPath(), $"sdc-oidc-{Guid.NewGuid():N}.jwt");
        await File.WriteAllTextAsync(tokenFile, "external-oidc-token");
        try
        {
            object credentialSource = source == "file" ? new { file = tokenFile } : new { url = google.Url + "idp/token", headers = new { Metadata = "True" } };
            // endpoints of the fake: Create does not re-validate (Inspect, run before a file is stored, allows only *.googleapis.com)
            var json = Wif(credentialSource, tokenUrl: google.Url + "v1/token",
                impersonation: impersonate ? google.Url + "v1/projects/-/serviceAccounts/sdc-export@my-project.iam.gserviceaccount.com:generateAccessToken" : null);
            Assert.Throws<InvalidOperationException>(() => GcsCredentials.Inspect(json));

            var credential = GcsCredentials.Create(json);
            Assert.IsAssignableFrom<ExternalAccountCredential>(credential.UnderlyingCredential);
            var token = await ((ITokenAccess)credential.CreateScoped(GcsCredentialStore.StorageScope)).GetAccessTokenForRequestAsync();

            Assert.Equal(impersonate ? "impersonated-google-token" : "sts-google-token", token);
            var sts = Assert.Single(google.StsRequests);
            Assert.Equal("urn:ietf:params:oauth:grant-type:token-exchange", sts["grant_type"]);
            Assert.Equal("external-oidc-token", sts["subject_token"]);
            Assert.Equal("urn:ietf:params:oauth:token-type:jwt", sts["subject_token_type"]);
            Assert.Equal(Audience, sts["audience"]);
            if (source == "url") Assert.Equal("True", Assert.Single(google.IdpHeaders));
            if (impersonate) Assert.Equal("Bearer sts-google-token", Assert.Single(google.ImpersonationAuthorization));
            else Assert.Empty(google.ImpersonationAuthorization);
        }
        finally { File.Delete(tokenFile); }
    }

    /// <summary>A local stand-in for Google STS, the IAM credentials API and an identity provider's token URL.</summary>
    private sealed class FakeGoogle : IAsyncDisposable
    {
        private readonly HttpListener _listener = new();
        private readonly CancellationTokenSource _stop = new();
        private Task _loop = Task.CompletedTask;
        public string Url { get; private set; } = "";
        public ConcurrentQueue<Dictionary<string, string>> StsRequests { get; } = new();
        public ConcurrentQueue<string> ImpersonationAuthorization { get; } = new();
        public ConcurrentQueue<string> IdpHeaders { get; } = new();

        public static Task<FakeGoogle> StartAsync()
        {
            var f = new FakeGoogle();
            var probe = new TcpListener(IPAddress.Loopback, 0);
            probe.Start();
            var port = ((IPEndPoint)probe.LocalEndpoint).Port;
            probe.Stop();
            f.Url = $"http://localhost:{port}/";
            f._listener.Prefixes.Add(f.Url);
            f._listener.Start();
            f._loop = Task.Run(f.ServeAsync);
            return Task.FromResult(f);
        }

        private async Task ServeAsync()
        {
            while (!_stop.IsCancellationRequested)
            {
                HttpListenerContext ctx;
                try { ctx = await _listener.GetContextAsync(); }
                catch (Exception) when (_stop.IsCancellationRequested) { return; }
                catch (HttpListenerException) { return; }
                var path = ctx.Request.Url!.AbsolutePath;
                using var reader = new StreamReader(ctx.Request.InputStream, Encoding.UTF8);
                var body = await reader.ReadToEndAsync();
                string response;
                if (path == "/v1/token")
                {
                    StsRequests.Enqueue(body.Split('&').Select(p => p.Split('=', 2)).ToDictionary(p => WebUtility.UrlDecode(p[0]), p => WebUtility.UrlDecode(p.ElementAtOrDefault(1) ?? "")));
                    response = """{"access_token":"sts-google-token","issued_token_type":"urn:ietf:params:oauth:token-type:access_token","token_type":"Bearer","expires_in":3600}""";
                }
                else if (path.EndsWith(":generateAccessToken", StringComparison.Ordinal))
                {
                    ImpersonationAuthorization.Enqueue(ctx.Request.Headers["Authorization"] ?? "");
                    response = $$"""{"accessToken":"impersonated-google-token","expireTime":"{{DateTime.UtcNow.AddHours(1):yyyy-MM-ddTHH:mm:ssZ}}"}""";
                }
                else if (path == "/idp/token")
                {
                    IdpHeaders.Enqueue(ctx.Request.Headers["Metadata"] ?? "");
                    response = "external-oidc-token";
                }
                else { ctx.Response.StatusCode = 404; response = "{}"; }
                var bytes = Encoding.UTF8.GetBytes(response);
                ctx.Response.ContentType = path == "/idp/token" ? "text/plain" : "application/json";
                await ctx.Response.OutputStream.WriteAsync(bytes);
                ctx.Response.Close();
            }
        }

        public async ValueTask DisposeAsync()
        {
            await _stop.CancelAsync();
            _listener.Stop();
            try { await _loop; } catch { /* stopping */ }
            _listener.Close();
        }
    }
}
