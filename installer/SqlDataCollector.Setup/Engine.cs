using System.Diagnostics;
using System.IO.Compression;
using System.Reflection;
using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Win32;

namespace SqlDataCollector.Setup;

public sealed record InstallResult(bool Success, bool DatabaseReady, string Url, string LocalUrl, IReadOnlyList<string> Warnings);

/// <summary>
/// The installation steps, shared by the wizard and the silent mode. In dry-run mode every step that changes the machine is
/// only logged ("would …"); read-only checks still run and their failures become warnings.
/// </summary>
public sealed class Engine(SetupOptions o, Action<string> log, bool dryRun, string? payloadOverride = null)
{
    public const string UninstallKey = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\SqlDataCollector";
    private const string FirewallRule = "SqlDataCollector HTTPS";
    private readonly List<string> _warnings = [];

    private string AppExe => Path.Combine(o.AppDir, "SqlDataCollector.exe");
    private string ConfigFile => Path.Combine(o.AppDir, "appsettings.Production.json");

    public async Task<InstallResult> InstallAsync(CancellationToken ct)
    {
        Step("Checking the prerequisites");
        CheckPrerequisites();

        var upgrade = Native.ServiceExists(o.ServiceName) && File.Exists(ConfigFile);
        if (upgrade) Info($"An existing installation was found ({o.ServiceName}): binaries are replaced, its configuration and certificate are kept.");

        Step("Stopping the existing service");
        if (Native.ServiceExists(o.ServiceName)) await StopServiceAsync(ct);
        else Info("no existing service");

        Step($"Copying the application to {o.AppDir}");
        ExtractPayload(upgrade);

        Step($"Preparing the data folder {o.DataDir}");
        PrepareFolders();

        if (!upgrade)
        {
            Step("Writing appsettings.Production.json");
            WriteConfig();
        }

        if (o.InstallsWeb && !upgrade)
        {
            Step($"Creating a self-signed HTTPS certificate for {o.HostName}");
            await RunAppAsync(ct, "create-cert", "--host", o.HostName, "--dir", o.CertDir);
        }

        Step($"Writing the database scripts to {o.ScriptsDir}");
        var scriptArgs = new List<string> { "db-script", "--meta", o.MetadataDb, "--dwh", o.DwhDb, "--account", o.ServiceAccount, "--out", o.ScriptsDir };
        if (o.IsCentral) scriptArgs.AddRange(["--central", "--hangfire-db", o.HangfireDb!, "--hangfire-schema", o.HangfireSchema]);
        await RunAppAsync(ct, [.. scriptArgs]);

        var dbReady = upgrade;
        if (!upgrade && o.DatabaseAction == "Now")
        {
            Step($"Creating the databases and permissions on {o.SqlServer} (with {Environment.UserDomainName}\\{Environment.UserName})");
            var args = new List<string> { "db-install", "--server", o.SqlServer, "--meta", o.MetadataDb, "--dwh", o.DwhDb, "--account", o.ServiceAccount };
            if (o.TrustServerCertificate) args.Add("--trust-server-certificate");
            try { await RunAppAsync(ct, [.. args]); dbReady = true; }
            catch (Exception ex)
            {
                Warn($"The databases could not be created: {ex.Message} Run the scripts in {o.ScriptsDir} with a DBA account; the application shows a setup page until then.");
            }
            if (o.IsCentral) Warn($"Central Hangfire: give {Path.Combine(o.ScriptsDir, "03_hangfire_central.sql")} to the DBA of {o.HangfireServer}.");
        }
        else if (!upgrade) Info($"Database setup skipped (later): run the scripts in {o.ScriptsDir} in numeric order with a DBA account.");

        Step($"Granting \"Log on as a service\" to {o.ServiceAccount}");
        Do(() => Native.GrantLogonAsService(o.ServiceAccount), $"would grant SeServiceLogonRight to {o.ServiceAccount}");

        Step($"Registering the Windows service {o.ServiceName}");
        Do(() => Native.CreateOrUpdateService(o.ServiceName, "SqlHarbor", AppExe, o.ServiceAccount, o.ManagedServiceAccount ? null : o.ServicePassword),
            $"would create service {o.ServiceName} ({AppExe}) running as {o.ServiceAccount}");
        await RunAsync(ct, "sc.exe", "config", o.ServiceName, "start=", "delayed-auto");
        await RunAsync(ct, "sc.exe", "description", o.ServiceName, "Collects SQL Server data into the DWH and exports it to Google Cloud Storage.");
        await RunAsync(ct, "sc.exe", "failure", o.ServiceName, "reset=", "86400", "actions=", "restart/60000/restart/60000/restart/300000");
        await RunAsync(ct, "sc.exe", "failureflag", o.ServiceName, "1");

        if (o.InstallsWeb && o.OpenFirewall)
        {
            Step($"Opening TCP {o.HttpsPort} in the Windows firewall");
            await RunAsync(ct, ignoreExitCode: true, "netsh.exe", "advfirewall", "firewall", "delete", "rule", $"name={FirewallRule}");
            await RunAsync(ct, "netsh.exe", "advfirewall", "firewall", "add", "rule", $"name={FirewallRule}", "dir=in", "action=allow", "protocol=TCP", $"localport={o.HttpsPort}");
        }

        Step("Starting the service");
        await RunAsync(ct, "sc.exe", "start", o.ServiceName);
        if (o.InstallsWeb) dbReady = await WaitForHealthAsync(ct) && dbReady;

        Step("Registering the uninstaller");
        RegisterUninstall();

        if (o.InstallsWeb && o.HttpsPort != 0)
            Info($"For Kerberos sign-in a domain admin registers: setspn -S HTTP/{o.HostName} {o.ServiceAccount}");
        Info(dryRun ? "Dry run finished: nothing was changed." : "Installation finished.");
        return new InstallResult(true, dbReady, o.Url, o.LocalUrl, _warnings);
    }

    public async Task UninstallAsync(bool purgeData, CancellationToken ct)
    {
        Step($"Stopping and removing the service {o.ServiceName}");
        if (Native.ServiceExists(o.ServiceName))
        {
            await StopServiceAsync(ct);
            await RunAsync(ct, "sc.exe", "delete", o.ServiceName);
        }
        Step("Removing the firewall rule");
        await RunAsync(ct, ignoreExitCode: true, "netsh.exe", "advfirewall", "firewall", "delete", "rule", $"name={FirewallRule}");
        Step($"Removing {o.AppDir}");
        Do(() => { if (Directory.Exists(o.AppDir)) Directory.Delete(o.AppDir, true); }, $"would delete {o.AppDir}");
        if (purgeData) Do(() => { if (Directory.Exists(o.DataDir)) Directory.Delete(o.DataDir, true); }, $"would delete {o.DataDir}");
        else Info($"Kept: {o.DataDir} (certificate, export archive) and the databases.");
        Do(() => Registry.LocalMachine.DeleteSubKeyTree(UninstallKey, false), "would remove the uninstall entry");
        Info("SqlDataCollector was removed.");
    }

    // ------------------------------------------------------------------ steps

    private void CheckPrerequisites()
    {
        if (!Native.IsAdministrator()) Fail("Run the installer as administrator.", allowInDryRun: true);
        var problems = o.Validate();
        if (problems.Count > 0) throw new InvalidOperationException(string.Join(Environment.NewLine, problems));

        var domain = Native.JoinedDomain();
        if (domain is null)
            Fail("This computer is not joined to an Active Directory domain. The services must run as a domain account and users sign in with AD, " +
                 "which requires a domain member server.", allowInDryRun: true, allowSkip: o.SkipDomainCheck);
        else Info($"domain: {domain}");

        try { Native.ResolveAccount(o.ServiceAccount); Info($"account {o.ServiceAccount} exists"); }
        catch (IdentityNotMappedException) { Fail($"The account {o.ServiceAccount} was not found in the domain.", allowInDryRun: true, allowSkip: o.SkipDomainCheck); }
        catch (SystemException ex)   // e.g. "The trust relationship between this workstation and the primary domain failed."
        { Fail($"The account {o.ServiceAccount} could not be checked against the domain: {ex.Message}", allowInDryRun: true, allowSkip: o.SkipDomainCheck); }

        if (!o.ManagedServiceAccount)
        {
            try { Native.ValidateCredentials(o.ServiceAccount, o.ServicePassword!); Info("password verified"); }
            catch (InvalidOperationException ex) { Fail(ex.Message, allowInDryRun: true, allowSkip: o.SkipDomainCheck); }
        }
        if (PayloadStream() is null) Fail("The installer contains no application package (build it with scripts\\dev.ps1 installer, or pass /payload).");
    }

    private void ExtractPayload(bool upgrade)
    {
        using var payload = PayloadStream()!;
        using var zip = new ZipArchive(payload, ZipArchiveMode.Read);
        if (dryRun) { Info($"would extract {zip.Entries.Count} files ({zip.Entries.Sum(e => e.Length) / 1024 / 1024:N0} MB)"); return; }

        var keepConfig = upgrade && File.Exists(ConfigFile) ? File.ReadAllText(ConfigFile) : null;
        if (Directory.Exists(o.AppDir)) Directory.Delete(o.AppDir, true);
        Directory.CreateDirectory(o.AppDir);
        zip.ExtractToDirectory(o.AppDir, overwriteFiles: true);
        if (keepConfig is not null) File.WriteAllText(ConfigFile, keepConfig);

        // the uninstaller: a copy of this setup program
        var setupDir = Path.Combine(o.InstallDir, "setup");
        Directory.CreateDirectory(setupDir);
        if (Environment.ProcessPath is { } self && !Path.GetDirectoryName(self)!.Equals(setupDir, StringComparison.OrdinalIgnoreCase))
            File.Copy(self, Path.Combine(setupDir, Path.GetFileName(self)), overwrite: true);
        Info($"{zip.Entries.Count} files");
    }

    private void PrepareFolders()
    {
        Do(() =>
        {
            foreach (var d in new[] { o.DataDir, o.CertDir, o.ExportDir, o.ScriptsDir }) Directory.CreateDirectory(d);
            var account = Native.ResolveAccount(o.ServiceAccount);
            // data folder: the service account may write; the certificate folder: only SYSTEM, Administrators and the service account
            Grant(new DirectoryInfo(o.DataDir), account, FileSystemRights.Modify);
            var certs = new DirectoryInfo(o.CertDir);
            var acl = new DirectorySecurity();
            acl.SetAccessRuleProtection(true, false);
            foreach (var sid in new[] { new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null), new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null) })
                acl.AddAccessRule(new FileSystemAccessRule(sid, FileSystemRights.FullControl, InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit, PropagationFlags.None, AccessControlType.Allow));
            acl.AddAccessRule(new FileSystemAccessRule(account, FileSystemRights.Modify, InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit, PropagationFlags.None, AccessControlType.Allow));
            certs.SetAccessControl(acl);
        }, $"would create {o.DataDir} (exports, certs) and grant {o.ServiceAccount} access");
    }

    private static void Grant(DirectoryInfo dir, SecurityIdentifier sid, FileSystemRights rights)
    {
        var acl = dir.GetAccessControl();
        acl.AddAccessRule(new FileSystemAccessRule(sid, rights, InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit, PropagationFlags.None, AccessControlType.Allow));
        dir.SetAccessControl(acl);
    }

    /// <summary>appsettings.Production.json: overrides of the base appsettings.json for this server.</summary>
    public JsonObject BuildConfig()
    {
        string Cs(string server, string db) =>
            $"Server={server};Database={db};Integrated Security=true;Encrypt=true;TrustServerCertificate={o.TrustServerCertificate.ToString().ToLowerInvariant()};Application Name=SqlDataCollector";
        var queues = o.IsCentral ? (o.RoleWorker ? "sdc_extract,sdc_system,sdc_export" : "") : "sdc_export,sdc_extract,sdc_system";
        var config = new JsonObject
        {
            ["ConnectionStrings"] = new JsonObject
            {
                ["Metadata"] = Cs(o.SqlServer, o.MetadataDb),
                ["Hangfire"] = o.IsCentral ? Cs(o.HangfireServer!, o.HangfireDb!) : ""
            },
            ["Collector"] = new JsonObject
            {
                ["Roles"] = o.Roles,
                ["Queues"] = queues,
                ["HangfireSchema"] = o.HangfireSchema,
                ["HangfirePrepareSchema"] = !o.IsCentral,
                ["AutoCreateSchema"] = true,
                ["WorkDirectory"] = o.ExportDir,
                ["ExportReadAsUser"] = "sdc_export_reader"
            },
            ["Secrets"] = new JsonObject { ["Key"] = string.IsNullOrWhiteSpace(o.SecretKey) ? Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)) : o.SecretKey.Trim() },
            ["Setup"] = new JsonObject { ["ScriptsDirectory"] = o.ScriptsDir },
            ["LocalAdmin"] = new JsonObject { ["Enabled"] = true, ["PasswordHash"] = "" }
        };
        if (o.InstallsWeb)
        {
            config["Https"] = new JsonObject { ["Port"] = o.HttpsPort, ["HostName"] = o.HostName, ["CertificateDirectory"] = o.CertDir };
            config["Alerts"] = new JsonObject { ["PublicUrl"] = o.Url };
        }
        return config;
    }

    private void WriteConfig()
    {
        var json = BuildConfig().ToJsonString(new JsonSerializerOptions { WriteIndented = true });
        Do(() => File.WriteAllText(ConfigFile, json), "would write appsettings.Production.json:\n" + Redact(json));
        if (o.IsCentral && string.IsNullOrWhiteSpace(o.SecretKey))
            Warn("Central mode: copy Secrets:Key from appsettings.Production.json to the other servers of this installation (it encrypts the GCS key).");
    }

    private static string Redact(string json) =>
        System.Text.RegularExpressions.Regex.Replace(json, "(\"Key\"\\s*:\\s*\")[^\"]+", "$1***");

    private async Task<bool> WaitForHealthAsync(CancellationToken ct)
    {
        if (dryRun) { Info($"would wait for {o.LocalUrl}/health"); return true; }
        using var handler = new HttpClientHandler { ServerCertificateCustomValidationCallback = (_, _, _, _) => true };   // localhost, self-signed
        using var http = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(5) };
        var until = DateTime.UtcNow.AddMinutes(2);
        while (DateTime.UtcNow < until)
        {
            try
            {
                using var resp = await http.GetAsync($"{o.LocalUrl}/health", ct);
                var body = await resp.Content.ReadAsStringAsync(ct);
                if (resp.IsSuccessStatusCode) { Info("the application answers: " + body); return true; }
                if ((int)resp.StatusCode == 503 && body.Contains("setup-required", StringComparison.Ordinal))
                {
                    Warn("The application runs but the database is not ready yet: " + body);
                    return false;
                }
            }
            catch (HttpRequestException) { }
            catch (TaskCanceledException) when (!ct.IsCancellationRequested) { }
            await Task.Delay(2000, ct);
        }
        Warn($"The application did not answer at {o.LocalUrl}/health within 2 minutes; see the Windows event log (Application, source SqlDataCollector).");
        return false;
    }

    private void RegisterUninstall()
    {
        var setupExe = Path.Combine(o.InstallDir, "setup", Path.GetFileName(Environment.ProcessPath ?? "SqlHarbor-Setup.exe"));
        Do(() =>
        {
            using var key = Registry.LocalMachine.CreateSubKey(UninstallKey);
            key.SetValue("DisplayName", "SqlHarbor");
            key.SetValue("DisplayVersion", Assembly.GetExecutingAssembly().GetName().Version?.ToString(3) ?? "1.0.0");
            key.SetValue("Publisher", "SqlHarbor");
            key.SetValue("InstallLocation", o.InstallDir);
            key.SetValue("UninstallString", $"\"{setupExe}\" /uninstall");
            key.SetValue("NoModify", 1, RegistryValueKind.DWord);
            key.SetValue("NoRepair", 1, RegistryValueKind.DWord);
            key.SetValue("ServiceName", o.ServiceName);
            key.SetValue("DataDir", o.DataDir);
        }, $"would register the uninstaller ({setupExe} /uninstall)");
    }

    private async Task StopServiceAsync(CancellationToken ct)
    {
        await RunAsync(ct, ignoreExitCode: true, "sc.exe", "stop", o.ServiceName);
        if (dryRun) return;
        for (int i = 0; i < 30; i++)
        {
            var (_, output) = await ExecAsync("sc.exe", ["query", o.ServiceName], ct);
            if (output.Contains("STOPPED", StringComparison.OrdinalIgnoreCase)) return;
            await Task.Delay(1000, ct);
        }
        Warn("The service did not stop within 30 seconds.");
    }

    // ------------------------------------------------------------------ helpers

    private Stream? PayloadStream()
    {
        if (payloadOverride is not null)
        {
            if (File.Exists(payloadOverride)) return File.OpenRead(payloadOverride);
            if (Directory.Exists(payloadOverride))
            {
                var ms = new MemoryStream();
                ZipFile.CreateFromDirectory(payloadOverride, ms);
                ms.Position = 0;
                return ms;
            }
            return null;
        }
        return Assembly.GetExecutingAssembly().GetManifestResourceStream("payload.zip");
    }

    private Task RunAppAsync(CancellationToken ct, params string[] args) =>
        dryRun ? Log($"would run: SqlDataCollector.exe {string.Join(' ', args)}") : RunAsync(ct, AppExe, args);

    private Task RunAsync(CancellationToken ct, string file, params string[] args) => RunAsync(ct, false, file, args);

    private async Task RunAsync(CancellationToken ct, bool ignoreExitCode, string file, params string[] args)
    {
        if (dryRun) { Info($"would run: {Path.GetFileName(file)} {string.Join(' ', args)}"); return; }
        var (code, output) = await ExecAsync(file, args, ct);
        foreach (var line in output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Take(40)) Info(line);
        if (code != 0 && !ignoreExitCode) throw new InvalidOperationException($"{Path.GetFileName(file)} {args.FirstOrDefault()} failed (exit code {code}).");
    }

    private static async Task<(int Code, string Output)> ExecAsync(string file, IReadOnlyList<string> args, CancellationToken ct)
    {
        var psi = new ProcessStartInfo(file) { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true };
        foreach (var a in args) psi.ArgumentList.Add(a);
        using var p = Process.Start(psi) ?? throw new InvalidOperationException($"{file} could not be started.");
        var stdout = p.StandardOutput.ReadToEndAsync(ct);
        var stderr = p.StandardError.ReadToEndAsync(ct);
        await p.WaitForExitAsync(ct);
        return (p.ExitCode, (await stdout) + (await stderr));
    }

    private void Do(Action action, string dryRunText)
    {
        if (dryRun) { Info(dryRunText); return; }
        action();
    }

    private void Fail(string message, bool allowInDryRun = false, bool allowSkip = false)
    {
        if ((dryRun && allowInDryRun) || allowSkip) { Warn(message); return; }
        throw new InvalidOperationException(message);
    }

    private void Step(string text) => log($"▶ {text}");
    private void Info(string text) => log($"   {text}");
    private Task Log(string text) { Info(text); return Task.CompletedTask; }
    private void Warn(string text) { _warnings.Add(text); log($"   ⚠ {text}"); }
}
