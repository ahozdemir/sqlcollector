using System.Collections.Concurrent;
using System.Runtime.Versioning;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Security.Principal;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authentication.Negotiate;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.Extensions.Options;
using SqlDataCollector.Infrastructure;
using SqlDataCollector.Models;

namespace SqlDataCollector.Security;

public sealed class LocalAdminOptions
{
    public const string Section = "LocalAdmin";
    public bool Enabled { get; set; } = true;
    public string Username { get; set; } = "admin";
    /// <summary>Generated with the "SqlDataCollector hash-password" command.</summary>
    public string? PasswordHash { get; set; }
}

public static class AppClaims
{
    /// <summary>The user's principals known to the app (the user itself + matching AD groups).</summary>
    public const string Principal = "sdc:principal";
    public const string AuthKind = "sdc:auth";        // ad | local
    public const string CheckedAt = "sdc:checked";
    public const string LocalAdminPrincipal = "local:admin";
}

/// <summary>PBKDF2-SHA256 password hash: pbkdf2-sha256$iter$salt$hash</summary>
public static class PasswordHasher
{
    private const int Iterations = 210_000;

    public static string Hash(string password)
    {
        var salt = RandomNumberGenerator.GetBytes(16);
        var hash = Rfc2898DeriveBytes.Pbkdf2(password, salt, Iterations, HashAlgorithmName.SHA256, 32);
        return $"pbkdf2-sha256${Iterations}${Convert.ToBase64String(salt)}${Convert.ToBase64String(hash)}";
    }

    public static bool Verify(string password, string? stored)
    {
        if (string.IsNullOrWhiteSpace(stored)) return false;
        var parts = stored.Split('$');
        if (parts.Length != 4 || parts[0] != "pbkdf2-sha256" || !int.TryParse(parts[1], out var iter)) return false;
        var salt = Convert.FromBase64String(parts[2]);
        var expected = Convert.FromBase64String(parts[3]);
        var actual = Rfc2898DeriveBytes.Pbkdf2(password, salt, iter, HashAlgorithmName.SHA256, expected.Length);
        return CryptographicOperations.FixedTimeEquals(actual, expected);
    }
}

/// <summary>Simple brute-force protection for local admin login (5 failures → 15 min lockout).</summary>
public sealed class LoginThrottle
{
    private readonly ConcurrentDictionary<string, (int Failures, DateTime Until)> _state = new();

    public bool IsLocked(string key) => _state.TryGetValue(key, out var s) && s.Until > DateTime.UtcNow;

    public void Fail(string key) => _state.AddOrUpdate(key, (1, DateTime.MinValue), (_, s) =>
    {
        var f = s.Failures + 1;
        return f >= 5 ? (0, DateTime.UtcNow.AddMinutes(15)) : (f, DateTime.MinValue);
    });

    public void Success(string key) => _state.TryRemove(key, out _);
}

/// <summary>AD user + groups → application roles and principal claims.</summary>
public sealed class PrincipalBuilder(MetadataRepository repo, ILogger<PrincipalBuilder> logger)
{
    public static readonly TimeSpan RecheckInterval = TimeSpan.FromMinutes(10);

    public async Task<ClaimsPrincipal?> BuildForAdAsync(ClaimsPrincipal windowsUser)
    {
        var userName = windowsUser.Identity?.Name;
        if (string.IsNullOrWhiteSpace(userName)) return null;

        var groups = GetGroupNames(windowsUser);
        var known = await repo.GetKnownPrincipalsAsync();
        var principals = new List<string> { userName };
        principals.AddRange(groups.Where(known.Contains));
        return await BuildAsync(userName, principals.Distinct(StringComparer.OrdinalIgnoreCase).ToList(), "ad");
    }

    public static ClaimsPrincipal BuildForLocalAdmin(string userName)
    {
        var id = new ClaimsIdentity(CookieAuthenticationDefaults.AuthenticationScheme, ClaimTypes.Name, ClaimTypes.Role);
        id.AddClaim(new Claim(ClaimTypes.Name, $"local\\{userName}"));
        id.AddClaim(new Claim(ClaimTypes.Role, AppRoles.Admin));
        id.AddClaim(new Claim(AppClaims.Principal, AppClaims.LocalAdminPrincipal));
        id.AddClaim(new Claim(AppClaims.AuthKind, "local"));
        id.AddClaim(new Claim(AppClaims.CheckedAt, DateTime.UtcNow.ToString("O")));
        return new ClaimsPrincipal(id);
    }

    /// <summary>Recomputes roles from the stored principals (periodic cookie validation).</summary>
    public async Task<ClaimsPrincipal?> RefreshAsync(ClaimsPrincipal current)
    {
        if (current.FindFirst(AppClaims.AuthKind)?.Value == "local") return current;
        var userName = current.Identity?.Name;
        if (userName is null) return null;
        var principals = current.FindAll(AppClaims.Principal).Select(c => c.Value).ToList();
        return await BuildAsync(userName, principals, "ad");
    }

    private async Task<ClaimsPrincipal?> BuildAsync(string userName, List<string> principals, string kind)
    {
        var set = new HashSet<string>(principals, StringComparer.OrdinalIgnoreCase);
        var roles = (await repo.GetRoleAssignmentsAsync())
            .Where(r => set.Contains(r.PrincipalName)).Select(r => r.Role).Distinct().ToList();
        if (roles.Count == 0)
        {
            logger.LogWarning("Login denied: no role assigned to {User}. Principals: {P}", userName, string.Join(", ", principals));
            return null;
        }

        var id = new ClaimsIdentity(CookieAuthenticationDefaults.AuthenticationScheme, ClaimTypes.Name, ClaimTypes.Role);
        id.AddClaim(new Claim(ClaimTypes.Name, userName));
        foreach (var r in roles) id.AddClaim(new Claim(ClaimTypes.Role, r));
        foreach (var p in principals) id.AddClaim(new Claim(AppClaims.Principal, p));
        id.AddClaim(new Claim(AppClaims.AuthKind, kind));
        id.AddClaim(new Claim(AppClaims.CheckedAt, DateTime.UtcNow.ToString("O")));
        return new ClaimsPrincipal(id);
    }

    private static IReadOnlyList<string> GetGroupNames(ClaimsPrincipal user)
    {
        if (OperatingSystem.IsWindows() && user.Identity is WindowsIdentity wi)
            return GetWindowsGroups(wi);
        // Linux/macOS: with Negotiate + LDAP configured, groups arrive as role claims
        return user.FindAll(ClaimTypes.Role).Select(c => c.Value).ToList();
    }

    [SupportedOSPlatform("windows")]
    private static IReadOnlyList<string> GetWindowsGroups(WindowsIdentity wi)
    {
        if (wi.Groups is null) return [];
        var result = new List<string>();
        foreach (var sid in wi.Groups)
        {
            try { result.Add(((NTAccount)sid.Translate(typeof(NTAccount))).Value); }
            catch (IdentityNotMappedException) { }
        }
        return result;
    }
}

public static class AuthEndpoints
{
    public static void MapAuthEndpoints(this WebApplication app)
    {
        // AD login: authenticate with Negotiate (Kerberos/NTLM), then issue the app cookie
        app.MapGet("/auth/windows", async (HttpContext ctx, PrincipalBuilder builder, string? returnUrl) =>
        {
            var result = await ctx.AuthenticateAsync(NegotiateDefaults.AuthenticationScheme);
            if (!result.Succeeded || result.Principal?.Identity?.IsAuthenticated != true)
                return Results.Challenge(authenticationSchemes: [NegotiateDefaults.AuthenticationScheme]);

            var principal = await builder.BuildForAdAsync(result.Principal);
            if (principal is null)
                return Results.Redirect("/denied?user=" + Uri.EscapeDataString(result.Principal.Identity!.Name ?? ""));

            await ctx.SignInAsync(CookieAuthenticationDefaults.AuthenticationScheme, principal);
            return Results.LocalRedirect(SafeReturn(returnUrl));
        }).AllowAnonymous();

        app.MapPost("/auth/local", async (HttpContext ctx, IOptions<LocalAdminOptions> opt, LoginThrottle throttle,
            MetadataRepository repo, ILoggerFactory lf, IAntiforgery antiforgery) =>
        {
            try { await antiforgery.ValidateRequestAsync(ctx); }
            catch (AntiforgeryValidationException) { return Results.Redirect("/login?error=expired"); }

            var form = await ctx.Request.ReadFormAsync();
            var user = form["username"].ToString();
            var pass = form["password"].ToString();
            var returnUrl = form["returnUrl"].ToString();
            var key = ctx.Connection.RemoteIpAddress?.ToString() ?? "?";
            var o = opt.Value;

            if (!o.Enabled || string.IsNullOrWhiteSpace(o.PasswordHash))
                return Results.Redirect("/login?error=disabled");
            if (throttle.IsLocked(key))
                return Results.Redirect("/login?error=locked");

            if (!string.Equals(user, o.Username, StringComparison.OrdinalIgnoreCase) || !PasswordHasher.Verify(pass, o.PasswordHash))
            {
                throttle.Fail(key);
                lf.CreateLogger("Auth").LogWarning("Local admin login failed: user={User}, ip={Ip}", user, key);
                await Task.Delay(750);
                return Results.Redirect("/login?error=invalid");
            }

            throttle.Success(key);
            await ctx.SignInAsync(CookieAuthenticationDefaults.AuthenticationScheme, PrincipalBuilder.BuildForLocalAdmin(o.Username));
            await repo.AuditAsync($"local\\{o.Username}", "Login", "Auth", null, new { ip = key });
            return Results.LocalRedirect(SafeReturn(returnUrl));
        }).AllowAnonymous();

        app.MapPost("/auth/logout", async (HttpContext ctx) =>
        {
            await ctx.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
            return Results.Redirect("/login?loggedOut=1");
        });
    }

    private static string SafeReturn(string? url) =>
        !string.IsNullOrEmpty(url) && url.StartsWith('/') && !url.StartsWith("//") && !url.StartsWith("/\\") ? url : "/";

    /// <summary>The cookie is re-validated periodically: role/permission changes apply within 10 minutes.</summary>
    public static async Task ValidatePrincipalAsync(CookieValidatePrincipalContext ctx)
    {
        var checkedAt = ctx.Principal?.FindFirst(AppClaims.CheckedAt)?.Value;
        if (DateTime.TryParse(checkedAt, null, System.Globalization.DateTimeStyles.RoundtripKind, out var at)
            && DateTime.UtcNow - at < PrincipalBuilder.RecheckInterval)
            return;

        var builder = ctx.HttpContext.RequestServices.GetRequiredService<PrincipalBuilder>();
        var refreshed = ctx.Principal is null ? null : await builder.RefreshAsync(ctx.Principal);
        if (refreshed is null)
        {
            ctx.RejectPrincipal();
            await ctx.HttpContext.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
            return;
        }
        ctx.ReplacePrincipal(refreshed);
        ctx.ShouldRenew = true;
    }
}

/// <summary>
/// Determines which servers/databases the signed-in user may use as source or target.
/// Admins see everything; others according to etl.ServerPermission rows.
/// Used for filtering in the UI and for server-side checks on save.
/// </summary>
public sealed class AccessService(AuthenticationStateProvider auth, MetadataRepository repo)
{
    private ClaimsPrincipal? _user;
    private IReadOnlyList<ServerPermissionDef>? _perms;

    public async Task<ClaimsPrincipal> UserAsync() => _user ??= (await auth.GetAuthenticationStateAsync()).User;

    public async Task<string> UserNameAsync() => (await UserAsync()).Identity?.Name ?? "?";

    public async Task<bool> IsAdminAsync() => (await UserAsync()).IsInRole(AppRoles.Admin);

    public async Task<bool> CanEditAsync()
    {
        var u = await UserAsync();
        return u.IsInRole(AppRoles.Admin) || u.IsInRole(AppRoles.Editor);
    }

    private async Task<IReadOnlyList<ServerPermissionDef>> MyPermissionsAsync()
    {
        if (_perms is not null) return _perms;
        var principals = new HashSet<string>((await UserAsync()).FindAll(AppClaims.Principal).Select(c => c.Value), StringComparer.OrdinalIgnoreCase);
        _perms = (await repo.GetServerPermissionsAsync()).Where(p => principals.Contains(p.PrincipalName)).ToList();
        return _perms;
    }

    /// <param name="usage">Source | Target | Any</param>
    public async Task<IReadOnlyList<SqlServerDef>> AllowedServersAsync(string usage)
    {
        var servers = (await repo.GetServersAsync()).Where(s => s.IsActive).Where(s => usage switch
        {
            "Source" => s.IsSource,
            "Target" => s.IsTarget,
            _ => true
        }).ToList();
        if (await IsAdminAsync()) return servers;
        var perms = await MyPermissionsAsync();
        return servers.Where(s => perms.Any(p => p.SqlServerId == s.SqlServerId && Matches(p, usage))).ToList();
    }

    public async Task<bool> CanUseAsync(int serverId, string database, string usage)
    {
        var server = (await AllowedServersAsync(usage)).FirstOrDefault(s => s.SqlServerId == serverId);
        if (server is null) return false;
        if (await IsAdminAsync()) return true;
        return (await MyPermissionsAsync()).Any(p => p.SqlServerId == serverId && Matches(p, usage)
            && (p.DatabaseName is null || string.Equals(p.DatabaseName, database, StringComparison.OrdinalIgnoreCase)));
    }

    public async Task<IReadOnlyList<string>> FilterDatabasesAsync(int serverId, IEnumerable<string> databases, string usage)
    {
        var list = new List<string>();
        foreach (var db in databases)
            if (await CanUseAsync(serverId, db, usage)) list.Add(db);
        return list;
    }

    public async Task<bool> CanEditExtractAsync(ExtractJobDef j) =>
        await CanEditAsync() && await CanUseAsync(j.SourceServerId, j.SourceDatabase, "Source")
                             && await CanUseAsync(j.TargetServerId, j.TargetDatabase, "Target");

    public async Task<bool> CanEditExportAsync(ExportJobDef e) =>
        await CanEditAsync() && await CanUseAsync(e.SourceServerId, e.SourceDatabase, "Any");

    private static bool Matches(ServerPermissionDef p, string usage) => usage switch
    {
        "Source" => p.CanSource,
        "Target" => p.CanTarget,
        _ => p.CanSource || p.CanTarget
    };
}
