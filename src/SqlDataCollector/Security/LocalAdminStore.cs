using System.Net;
using Microsoft.Extensions.Options;
using SqlDataCollector.Infrastructure;

namespace SqlDataCollector.Security;

/// <summary>
/// The local admin's password hash: set on the first sign-in (from the server itself) and stored in the metadata database
/// (etl.SystemSetting), so the Web process never writes its configuration file. LocalAdmin:PasswordHash in appsettings is
/// only a fallback (pre-set installations, test environments).
/// </summary>
public sealed class LocalAdminStore(MetadataRepository repo, IOptions<LocalAdminOptions> options)
{
    public const string HashKey = "LocalAdmin.PasswordHash";
    public const int MinLength = 12;

    public bool Enabled => options.Value.Enabled;
    public string Username => options.Value.Username;

    public async Task<string?> GetHashAsync()
    {
        var rows = await repo.GetSystemSettingsAsync();
        return rows.TryGetValue(HashKey, out var h) && !string.IsNullOrWhiteSpace(h) ? h
            : string.IsNullOrWhiteSpace(options.Value.PasswordHash) ? null : options.Value.PasswordHash;
    }

    /// <summary>True while no password exists yet: the sign-in page offers "set the password" (only from the server itself).</summary>
    public async Task<bool> NeedsInitialPasswordAsync() => Enabled && await GetHashAsync() is null;

    public Task SetPasswordAsync(string password, string user) =>
        repo.SaveSecretSettingAsync(HashKey, PasswordHasher.Hash(password), user, "LocalAdminPassword");

    /// <summary>Password rules; null = valid.</summary>
    public static string? Validate(string? password, string? confirm)
    {
        if (string.IsNullOrEmpty(password) || password.Length < MinLength) return $"The password must be at least {MinLength} characters.";
        if (password != confirm) return "The passwords do not match.";
        if (password.Distinct().Count() < 4) return "The password is too simple.";
        return null;
    }

    /// <summary>The initial password may only be set from the server itself (otherwise anyone reaching the URL first could take over).</summary>
    public static bool IsLocalRequest(IPAddress? remote) => remote is not null && IPAddress.IsLoopback(remote);
}
