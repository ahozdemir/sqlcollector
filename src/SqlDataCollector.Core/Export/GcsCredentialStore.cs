using System.Globalization;
using System.Text.Json;
using Google.Apis.Auth.OAuth2;
using Microsoft.Extensions.Options;
using SqlDataCollector.Infrastructure;
using SqlDataCollector.Options;

namespace SqlDataCollector.Export;

public enum GcsCredentialSource { Anonymous, File, StoredKey, ApplicationDefault }

public sealed record GcsKeyInfo(string ClientEmail, string? ProjectId, string? PrivateKeyId, string UploadedBy, DateTime UploadedAtUtc);

/// <summary>
/// Which credential GCS uploads use, in this order: Gcs:Anonymous (emulator) → Gcs:CredentialsPath (JSON file) →
/// the service account key uploaded on the Settings page (stored AES-GCM encrypted in the metadata database, so every Worker
/// can use it) → Application Default Credentials (e.g. Workload Identity Federation, keyless).
/// </summary>
public sealed class GcsCredentialStore(MetadataRepository repo, SecretProtector secrets, IOptions<GcsOptions> options)
{
    public const string KeySetting = "Gcs.ServiceAccountKey";
    public const string InfoSetting = "Gcs.ServiceAccountInfo";
    public const string StorageScope = "https://www.googleapis.com/auth/devstorage.read_write";

    public bool CanStoreKeys => secrets.IsConfigured;

    public async Task<(GcsCredentialSource Source, string Detail, GcsKeyInfo? Info)> DescribeAsync()
    {
        var o = options.Value;
        if (o.Anonymous) return (GcsCredentialSource.Anonymous, $"Unauthenticated (emulator) – Gcs:Anonymous{(string.IsNullOrEmpty(o.Endpoint) ? "" : $", endpoint {o.Endpoint}")}", null);
        if (!string.IsNullOrWhiteSpace(o.CredentialsPath)) return (GcsCredentialSource.File, $"Key file from configuration: {o.CredentialsPath}", null);
        var info = await GetInfoAsync();
        if (info is not null) return (GcsCredentialSource.StoredKey, $"Uploaded service account key: {info.ClientEmail}", info);
        return (GcsCredentialSource.ApplicationDefault, "Application Default Credentials (GOOGLE_APPLICATION_CREDENTIALS / Workload Identity Federation)", null);
    }

    /// <summary>Changes whenever the effective credential changes (cached clients are rebuilt).</summary>
    public async Task<string> GetVersionAsync()
    {
        var o = options.Value;
        if (o.Anonymous) return "anonymous";
        if (!string.IsNullOrWhiteSpace(o.CredentialsPath)) return "file:" + o.CredentialsPath;
        return (await GetInfoAsync()) is { } i ? "key:" + i.UploadedAtUtc.Ticks.ToString(CultureInfo.InvariantCulture) : "adc";
    }

    /// <summary>The credential to use; null for unauthenticated access.</summary>
    public async Task<GoogleCredential?> ResolveAsync(CancellationToken ct)
    {
        var o = options.Value;
        if (o.Anonymous) return null;
        if (!string.IsNullOrWhiteSpace(o.CredentialsPath))
            return (await CredentialFactory.FromFileAsync<ServiceAccountCredential>(o.CredentialsPath, ct)).ToGoogleCredential();
        var rows = await repo.GetSystemSettingsAsync();
        if (rows.TryGetValue(KeySetting, out var stored) && !string.IsNullOrWhiteSpace(stored))
            return CredentialFactory.FromJson<ServiceAccountCredential>(secrets.Unprotect(stored)).ToGoogleCredential();
        return await GoogleCredential.GetApplicationDefaultAsync(ct);
    }

    /// <summary>Validates a service account key JSON (type, client_email, private_key) and returns its public facts.</summary>
    public static GcsKeyInfo ParseKey(string json, string user)
    {
        JsonElement root;
        try { root = JsonDocument.Parse(json).RootElement; }
        catch (JsonException) { throw new InvalidOperationException("The file is not valid JSON."); }
        string? Get(string name) => root.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
        if (Get("type") != "service_account")
            throw new InvalidOperationException("This is not a service account key (\"type\": \"service_account\" expected). Create a JSON key for the service account in the Google Cloud console.");
        var email = Get("client_email");
        if (string.IsNullOrWhiteSpace(email) || string.IsNullOrWhiteSpace(Get("private_key")))
            throw new InvalidOperationException("The key has no client_email or private_key.");
        return new GcsKeyInfo(email, Get("project_id"), Get("private_key_id"), user, DateTime.UtcNow);
    }

    public async Task<GcsKeyInfo> SaveKeyAsync(string json, string user)
    {
        var info = ParseKey(json, user);
        CredentialFactory.FromJson<ServiceAccountCredential>(json);   // the Google library can read it
        await repo.SaveSecretSettingAsync(KeySetting, secrets.Protect(json), user, "GcsServiceAccountKey");
        await repo.SaveSecretSettingAsync(InfoSetting, JsonSerializer.Serialize(info), user, "GcsServiceAccountKey");
        return info;
    }

    public async Task RemoveKeyAsync(string user)
    {
        await repo.SaveSecretSettingAsync(KeySetting, null, user, "GcsServiceAccountKey");
        await repo.SaveSecretSettingAsync(InfoSetting, null, user, "GcsServiceAccountKey");
    }

    private async Task<GcsKeyInfo?> GetInfoAsync()
    {
        var rows = await repo.GetSystemSettingsAsync();
        return rows.TryGetValue(InfoSetting, out var v) && !string.IsNullOrWhiteSpace(v) ? JsonSerializer.Deserialize<GcsKeyInfo>(v) : null;
    }
}
