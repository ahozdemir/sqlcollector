using System.Globalization;
using System.Text.Json;
using Google.Apis.Auth.OAuth2;
using Microsoft.Extensions.Options;
using SqlDataCollector.Infrastructure;
using SqlDataCollector.Options;

namespace SqlDataCollector.Export;

public enum GcsCredentialSource { Anonymous, File, StoredKey, ApplicationDefault }

/// <summary>The uploaded credential (stored as JSON; Kind/Audience/TokenSource are absent in keys saved by older versions).</summary>
public sealed record GcsKeyInfo(string ClientEmail, string? ProjectId, string? PrivateKeyId, string UploadedBy, DateTime UploadedAtUtc,
    GcsCredentialKind Kind = GcsCredentialKind.ServiceAccountKey, string? Audience = null, string? TokenSource = null)
{
    public bool IsWorkloadIdentityFederation => Kind == GcsCredentialKind.WorkloadIdentityFederation;
}

/// <summary>
/// Which credential GCS uploads use, in this order: Gcs:Anonymous (emulator) → Gcs:CredentialsPath (JSON file) →
/// the credential file uploaded on the Settings page (stored AES-GCM encrypted in the metadata database, so every Worker
/// can use it) → Application Default Credentials. Credential files are service account keys or keyless Workload Identity
/// Federation configurations (<see cref="GcsCredentials"/>).
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
        if (!string.IsNullOrWhiteSpace(o.CredentialsPath)) return (GcsCredentialSource.File, $"Credential file from configuration: {o.CredentialsPath}", null);
        var info = await GetInfoAsync();
        if (info is not null)
            return (GcsCredentialSource.StoredKey, info.IsWorkloadIdentityFederation
                ? $"Uploaded Workload Identity Federation configuration: {info.ClientEmail}" : $"Uploaded service account key: {info.ClientEmail}", info);
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
            return GcsCredentials.Create(await File.ReadAllTextAsync(o.CredentialsPath, ct));
        var rows = await repo.GetSystemSettingsAsync();
        if (rows.TryGetValue(KeySetting, out var stored) && !string.IsNullOrWhiteSpace(stored))
            return GcsCredentials.Create(secrets.Unprotect(stored));
        return await GoogleCredential.GetApplicationDefaultAsync(ct);
    }

    /// <summary>Validates a credential file (service account key or Workload Identity Federation configuration) and returns its public facts.</summary>
    public static GcsKeyInfo ParseKey(string json, string user)
    {
        var f = GcsCredentials.Inspect(json);
        return new GcsKeyInfo(f.Identity, f.ProjectId, f.KeyId, user, DateTime.UtcNow, f.Kind, f.Audience, f.TokenSource);
    }

    public async Task<GcsKeyInfo> SaveKeyAsync(string json, string user)
    {
        var info = ParseKey(json, user);
        GcsCredentials.Create(json);   // the Google library can read it
        await repo.SaveSecretSettingAsync(KeySetting, secrets.Protect(json), user, "GcsServiceAccountKey");
        await repo.SaveSecretSettingAsync(InfoSetting, JsonSerializer.Serialize(info), user, "GcsServiceAccountKey");
        return info;
    }

    public async Task RemoveKeyAsync(string user)
    {
        await repo.SaveSecretSettingAsync(KeySetting, null, user, "GcsServiceAccountKey");
        await repo.SaveSecretSettingAsync(InfoSetting, null, user, "GcsServiceAccountKey");
    }

    /// <summary>The uploaded credential's public facts, also when the configuration takes precedence over it.</summary>
    public Task<GcsKeyInfo?> GetStoredInfoAsync() => GetInfoAsync();

    private async Task<GcsKeyInfo?> GetInfoAsync()
    {
        var rows = await repo.GetSystemSettingsAsync();
        return rows.TryGetValue(InfoSetting, out var v) && !string.IsNullOrWhiteSpace(v) ? JsonSerializer.Deserialize<GcsKeyInfo>(v) : null;
    }
}
