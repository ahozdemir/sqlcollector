using System.Text.Json;
using System.Text.RegularExpressions;
using Google.Apis.Auth.OAuth2;

namespace SqlDataCollector.Export;

/// <summary>What a Google credential file is: a service account key, or a keyless Workload Identity Federation configuration.</summary>
public enum GcsCredentialKind { ServiceAccountKey, WorkloadIdentityFederation }

/// <summary>Public facts of a credential file (never the private key or a token).</summary>
public sealed record GcsCredentialFacts(
    GcsCredentialKind Kind,
    string Identity,          // service account e-mail (key, or the impersonated account of a WIF configuration) or the federated principal
    string? ProjectId,
    string? KeyId,
    string? Audience,         // WIF: //iam.googleapis.com/projects/…/workloadIdentityPools/…/providers/…
    string? TokenSource);     // WIF: where the external token comes from (file, URL, AWS)

/// <summary>
/// Reads Google credential files: service account keys ("type": "service_account") and Workload Identity Federation
/// configurations ("type": "external_account", created with <c>gcloud iam workload-identity-pools create-cred-config</c>).
/// <see cref="Inspect"/> validates a file before it is stored (Google's endpoints only, supported token sources);
/// <see cref="Create"/> turns a stored file into a credential.
/// </summary>
public static partial class GcsCredentials
{
    public const string ServiceAccountType = "service_account";
    public const string ExternalAccountType = "external_account";

    /// <summary>Validates a credential file and returns its public facts; throws <see cref="InvalidOperationException"/> with a clear message.</summary>
    public static GcsCredentialFacts Inspect(string json)
    {
        JsonElement root;
        try { root = JsonDocument.Parse(json, new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true }).RootElement.Clone(); }
        catch (JsonException) { throw new InvalidOperationException("The file is not valid JSON."); }
        if (root.ValueKind != JsonValueKind.Object) throw new InvalidOperationException("The file is not a Google credential file (JSON object expected).");
        string? Get(JsonElement e, string name) => e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

        switch (Get(root, "type"))
        {
            case ServiceAccountType:
                var email = Get(root, "client_email");
                if (string.IsNullOrWhiteSpace(email) || string.IsNullOrWhiteSpace(Get(root, "private_key")))
                    throw new InvalidOperationException("The key has no client_email or private_key.");
                return new GcsCredentialFacts(GcsCredentialKind.ServiceAccountKey, email, Get(root, "project_id"), Get(root, "private_key_id"), null, null);

            case ExternalAccountType:
                return InspectExternalAccount(root, Get);

            case "external_account_authorized_user":
                throw new InvalidOperationException("This is a workforce sign-in of a person (external_account_authorized_user). A service needs a workload identity " +
                    "configuration (\"type\": \"external_account\") or a service account key.");
            case "authorized_user":
                throw new InvalidOperationException("This is a personal gcloud login (authorized_user), not a credential for a service. Use a Workload Identity Federation " +
                    "configuration or a service account key.");
            default:
                throw new InvalidOperationException("Not a supported Google credential file: expected a Workload Identity Federation configuration (\"type\": \"external_account\") " +
                    "or a service account key (\"type\": \"service_account\").");
        }
    }

    private static GcsCredentialFacts InspectExternalAccount(JsonElement root, Func<JsonElement, string, string?> get)
    {
        var audience = get(root, "audience");
        if (string.IsNullOrWhiteSpace(audience) || !audience.StartsWith("//iam.googleapis.com/", StringComparison.Ordinal))
            throw new InvalidOperationException("The Workload Identity Federation configuration has no valid audience (//iam.googleapis.com/projects/…/workloadIdentityPools/…/providers/…).");
        if (string.IsNullOrWhiteSpace(get(root, "subject_token_type")))
            throw new InvalidOperationException("The Workload Identity Federation configuration has no subject_token_type.");
        // the external token and the Google tokens may only be sent to Google
        RequireGoogleEndpoint(get(root, "token_url"), "token_url", required: true);
        var impersonation = get(root, "service_account_impersonation_url");
        RequireGoogleEndpoint(impersonation, "service_account_impersonation_url", required: false);

        if (!root.TryGetProperty("credential_source", out var source) || source.ValueKind != JsonValueKind.Object)
            throw new InvalidOperationException("The Workload Identity Federation configuration has no credential_source.");
        string tokenSource;
        if (get(source, "file") is { Length: > 0 } file) tokenSource = $"token file {file}";
        else if (get(source, "url") is { Length: > 0 } url)
        {
            if (!Uri.TryCreate(url, UriKind.Absolute, out var u) || (u.Scheme != Uri.UriSchemeHttps && u.Scheme != Uri.UriSchemeHttp))
                throw new InvalidOperationException($"The token URL of the credential_source is not a valid http(s) address: {url}");
            tokenSource = $"token URL {u.GetLeftPart(UriPartial.Path)}";
        }
        else if (get(source, "environment_id") is { } env && env.StartsWith("aws", StringComparison.OrdinalIgnoreCase)) tokenSource = "AWS instance identity";
        else if (source.TryGetProperty("executable", out _))
            throw new InvalidOperationException("Executable-sourced credentials are not supported by Google's .NET client library. Let a scheduled task write the " +
                "token to a file and use a file-sourced configuration (gcloud … create-cred-config --credential-source-file), or use a URL-sourced one.");
        else if (source.TryGetProperty("certificate", out _))
            throw new InvalidOperationException("X.509 certificate-sourced credentials are not supported by Google's .NET client library. Use a file- or URL-sourced " +
                "token (OIDC or SAML from your identity provider) instead.");
        else
            throw new InvalidOperationException("Unsupported credential_source: expected \"file\", \"url\" or an AWS environment.");

        var m = ImpersonatedAccount().Match(impersonation ?? "");
        var pool = PoolAndProvider().Match(audience);
        var identity = m.Success ? Uri.UnescapeDataString(m.Groups[1].Value)
            : pool.Success ? $"federated principal of pool {pool.Groups[2].Value} / provider {pool.Groups[3].Value}" : "federated principal";
        var project = pool.Success && pool.Groups[1].Value.Length > 0 ? pool.Groups[1].Value : null;
        return new GcsCredentialFacts(GcsCredentialKind.WorkloadIdentityFederation, identity, project, null, audience, tokenSource);
    }

    private static void RequireGoogleEndpoint(string? url, string field, bool required)
    {
        if (string.IsNullOrWhiteSpace(url))
        {
            if (required) throw new InvalidOperationException($"The Workload Identity Federation configuration has no {field}.");
            return;
        }
        if (!Uri.TryCreate(url, UriKind.Absolute, out var u) || u.Scheme != Uri.UriSchemeHttps
            || !(u.Host.Equals("googleapis.com", StringComparison.OrdinalIgnoreCase) || u.Host.EndsWith(".googleapis.com", StringComparison.OrdinalIgnoreCase)))
            throw new InvalidOperationException($"The {field} must be an https address of Google (*.googleapis.com), not {url}.");
    }

    /// <summary>The credential of a stored (already inspected) file: a service account key or a Workload Identity Federation configuration.</summary>
    public static GoogleCredential Create(string json)
    {
        string? type;
        try { type = JsonDocument.Parse(json, new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true }).RootElement.GetProperty("type").GetString(); }
        catch (Exception ex) when (ex is JsonException or KeyNotFoundException or InvalidOperationException) { throw new InvalidOperationException("The Google credential file has no type."); }
        return type switch
        {
            ServiceAccountType => CredentialFactory.FromJson<ServiceAccountCredential>(json).ToGoogleCredential(),
            ExternalAccountType => CredentialFactory.FromJson(json, ExternalAccountType),
            _ => throw new InvalidOperationException($"Unsupported Google credential type '{type}'.")
        };
    }

    [GeneratedRegex(@"/serviceAccounts/([^/:]+):generateAccessToken", RegexOptions.IgnoreCase)]
    private static partial Regex ImpersonatedAccount();

    [GeneratedRegex(@"^//iam\.googleapis\.com/(?:projects/([^/]+)/)?locations/[^/]+/(?:workloadIdentityPools|workforcePools)/([^/]+)/providers/([^/]+)")]
    private static partial Regex PoolAndProvider();
}
