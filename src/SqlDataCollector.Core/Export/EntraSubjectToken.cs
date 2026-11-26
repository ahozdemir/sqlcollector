using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;
using Azure.Core;
using Azure.Identity;
using Google.Apis.Auth.OAuth2;

namespace SqlDataCollector.Export;

/// <summary>
/// Settings of the Entra ID token source of a Workload Identity Federation configuration – an extension of Google's format:
/// <c>"credential_source": { "entra": { "resource": "api://sqlharbor-gcs", … } }</c>. The application gets the Entra ID token itself
/// (Azure.Identity) and Google's STS exchanges it: no token file, no scheduled task.
/// <list type="bullet">
/// <item>Managed identity (Azure VM, Azure Arc-enabled server, App Service): only <c>resource</c>; optional <c>managed_identity_client_id</c>
/// for a user-assigned identity.</item>
/// <item>App registration with a certificate: <c>tenant_id</c>, <c>client_id</c>, <c>certificate_thumbprint</c> (certificate with private key in
/// LocalMachine\My or CurrentUser\My, readable by the service account).</item>
/// </list>
/// </summary>
public sealed record EntraTokenSource(string Resource, string? ManagedIdentityClientId, string? TenantId, string? ClientId, string? CertificateThumbprint)
{
    public bool UsesCertificate => CertificateThumbprint is not null;

    /// <summary>The scope requested from Entra ID: the resource the Google provider trusts as audience.</summary>
    public string Scope => Resource.TrimEnd('/') + "/.default";

    public string Describe() => UsesCertificate
        ? $"Entra ID app {ClientId} (certificate {CertificateThumbprint![..8]}…) for {Resource}"
        : $"Entra ID managed identity{(ManagedIdentityClientId is null ? "" : " " + ManagedIdentityClientId)} for {Resource}";

    /// <summary>Reads and validates the "entra" block; throws <see cref="InvalidOperationException"/> with a clear message.</summary>
    public static EntraTokenSource Parse(JsonElement entra)
    {
        string? Get(string name) => entra.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(v.GetString()) ? v.GetString()!.Trim() : null;
        var resource = Get("resource");
        if (resource is null || !(Guid.TryParse(resource, out _) || Uri.TryCreate(resource, UriKind.Absolute, out _)))
            throw new InvalidOperationException("The Entra ID token source needs \"resource\": the Application ID URI (e.g. api://sqlharbor-gcs) or client id that the Google provider accepts as audience.");
        var miClientId = Get("managed_identity_client_id");
        if (miClientId is not null && !Guid.TryParse(miClientId, out _))
            throw new InvalidOperationException("managed_identity_client_id must be the client id (GUID) of the user-assigned managed identity.");
        var tenant = Get("tenant_id");
        var client = Get("client_id");
        var thumb = Get("certificate_thumbprint")?.Replace(" ", "").ToUpperInvariant();
        if (tenant is null && client is null && thumb is null) return new EntraTokenSource(resource, miClientId, null, null, null);
        if (tenant is null || client is null || thumb is null)
            throw new InvalidOperationException("An Entra ID app registration needs tenant_id, client_id and certificate_thumbprint (leave all three out for a managed identity).");
        if (miClientId is not null)
            throw new InvalidOperationException("Use either a managed identity or an app registration with a certificate, not both.");
        if (thumb.Length != 40 || !thumb.All(Uri.IsHexDigit))
            throw new InvalidOperationException("certificate_thumbprint must be the 40 hex characters of the certificate's SHA-1 thumbprint.");
        if (!Guid.TryParse(client, out _)) throw new InvalidOperationException("client_id must be the application (client) id (GUID) of the app registration.");
        return new EntraTokenSource(resource, null, tenant, client, thumb);
    }

    /// <summary>The Azure.Identity credential for this source (certificate looked up in the Windows certificate stores).</summary>
    public TokenCredential CreateCredential()
    {
        if (!UsesCertificate)
            return new ManagedIdentityCredential(ManagedIdentityClientId is null ? ManagedIdentityId.SystemAssigned : ManagedIdentityId.FromUserAssignedClientId(ManagedIdentityClientId));
        return new ClientCertificateCredential(TenantId, ClientId, FindCertificate(CertificateThumbprint!));
    }

    private static X509Certificate2 FindCertificate(string thumbprint)
    {
        foreach (var location in new[] { StoreLocation.LocalMachine, StoreLocation.CurrentUser })
        {
            using var store = new X509Store(StoreName.My, location);
            try { store.Open(OpenFlags.ReadOnly | OpenFlags.OpenExistingOnly); }
            catch (CryptographicException) { continue; }
            var found = store.Certificates.Find(X509FindType.FindByThumbprint, thumbprint, validOnly: false);
            if (found.Count == 0) continue;
            var cert = found[0];
            if (!cert.HasPrivateKey)
                throw new InvalidOperationException($"The certificate {thumbprint} in {location}\\My has no private key the service account can use.");
            return cert;
        }
        throw new InvalidOperationException($"Certificate {thumbprint} was not found in LocalMachine\\My or CurrentUser\\My of the account the service runs as.");
    }
}

/// <summary>Hands the Entra ID access token to Google's STS (subject token of a programmatic external account credential).</summary>
public sealed class EntraSubjectTokenProvider(TokenCredential credential, string scope) : ProgrammaticExternalAccountCredential.ISubjectTokenProvider
{
    public async Task<string> GetSubjectTokenAsync(ProgrammaticExternalAccountCredential caller, CancellationToken taskCancellationToken)
    {
        try { return (await credential.GetTokenAsync(new TokenRequestContext([scope]), taskCancellationToken)).Token; }
        catch (AuthenticationFailedException ex)
        {
            throw new InvalidOperationException($"Could not get an Entra ID token for {scope}: {ex.Message}", ex);
        }
    }
}
