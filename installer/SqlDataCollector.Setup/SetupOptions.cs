using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace SqlDataCollector.Setup;

/// <summary>Everything the wizard asks (also the JSON file of a silent installation: Setup.exe /silent /config install.json).</summary>
public sealed partial class SetupOptions
{
    /// <summary>Embedded (Web + Worker + Hangfire in the metadata DB) or Central (organization's Hangfire database).</summary>
    public string Mode { get; set; } = "Embedded";
    public bool RoleWeb { get; set; } = true;
    public bool RoleWorker { get; set; } = true;

    public string InstallDir { get; set; } = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "SqlDataCollector");
    public string DataDir { get; set; } = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "SqlDataCollector");
    public string ServiceName { get; set; } = "SqlDataCollector";

    /// <summary>
    /// Export work folder (Collector:WorkDirectory): temporary export files and the optional local .csv.gz archive.
    /// Empty = &lt;DataDir&gt;\exports. Best on a fast local disk other than C:.
    /// </summary>
    public string? WorkDir { get; set; }

    /// <summary>DOMAIN\user or DOMAIN\gmsa$.</summary>
    public string ServiceAccount { get; set; } = "";
    public bool ManagedServiceAccount { get; set; }
    /// <summary>Never written to disk by the installer. Silent mode: preferably via /password-env.</summary>
    [JsonIgnore] public string? ServicePassword { get; set; }
    [JsonPropertyName("ServicePassword")] public string? ServicePasswordFromFile { set => ServicePassword = value; get => null; }

    public string SqlServer { get; set; } = "localhost";
    public string MetadataDb { get; set; } = "SqlDataCollectorMeta";
    public string DwhDb { get; set; } = "DWH";
    public bool TrustServerCertificate { get; set; } = true;
    /// <summary>Now = create databases and permissions with the installing user's account; Later = only write the SQL scripts.</summary>
    public string DatabaseAction { get; set; } = "Now";

    public string? HangfireServer { get; set; }
    public string? HangfireDb { get; set; }
    public string HangfireSchema { get; set; } = "hangfire_collector";
    /// <summary>Central mode, additional servers: the Secrets:Key of the first installation (empty = generate).</summary>
    public string? SecretKey { get; set; }

    public string HostName { get; set; } = (Environment.MachineName + DomainSuffix()).ToLowerInvariant();
    public int HttpsPort { get; set; } = 443;
    public bool OpenFirewall { get; set; } = true;
    public bool OpenBrowser { get; set; } = true;

    /// <summary>Tests only: allow running on a machine that is not joined to a domain.</summary>
    public bool SkipDomainCheck { get; set; }

    public bool IsCentral => Mode.Equals("Central", StringComparison.OrdinalIgnoreCase);
    public bool InstallsWeb => !IsCentral || RoleWeb;
    public bool InstallsWorker => !IsCentral || RoleWorker;
    public string Roles => IsCentral ? string.Join(",", new[] { RoleWeb ? "Web" : null, RoleWorker ? "Worker" : null }.Where(r => r is not null)) : "Web,Worker";
    public string AppDir => Path.Combine(InstallDir, "app");
    public string ScriptsDir => Path.Combine(InstallDir, "sql");
    public string CertDir => Path.Combine(DataDir, "certs");
    public string ExportDir => string.IsNullOrWhiteSpace(WorkDir) ? Path.Combine(DataDir, "exports") : WorkDir.Trim();
    /// <summary>The export folder is not inside the data folder: it needs its own permission and is purged separately.</summary>
    public bool ExportDirOutsideData => !IsInside(ExportDir, DataDir);
    public string Url => HttpsPort == 443 ? $"https://{HostName}" : $"https://{HostName}:{HttpsPort}";
    public string LocalUrl => HttpsPort == 443 ? "https://localhost" : $"https://localhost:{HttpsPort}";

    public static SetupOptions Load(string path) =>
        JsonSerializer.Deserialize<SetupOptions>(File.ReadAllText(path), new JsonSerializerOptions { PropertyNameCaseInsensitive = true, ReadCommentHandling = JsonCommentHandling.Skip })
        ?? throw new InvalidOperationException("Empty configuration file.");

    /// <summary>Input validation; returns the problems (empty = valid).</summary>
    public List<string> Validate()
    {
        var e = new List<string>();
        if (!Mode.Equals("Embedded", StringComparison.OrdinalIgnoreCase) && !IsCentral) e.Add("Mode must be Embedded or Central.");
        if (IsCentral && !RoleWeb && !RoleWorker) e.Add("Choose the Web and/or the Worker role.");
        if (!AccountPattern().IsMatch(ServiceAccount ?? "")) e.Add("The service account must be DOMAIN\\name (a domain account).");
        if (!ManagedServiceAccount && string.IsNullOrEmpty(ServicePassword)) e.Add("Enter the password of the service account (or select gMSA).");
        if (ManagedServiceAccount && !(ServiceAccount ?? "").EndsWith('$')) e.Add("A gMSA name ends with $ (DOMAIN\\name$).");
        if (string.IsNullOrWhiteSpace(SqlServer)) e.Add("Enter the SQL Server instance.");
        if (string.IsNullOrWhiteSpace(MetadataDb) || string.IsNullOrWhiteSpace(DwhDb)) e.Add("Enter the metadata and DWH database names.");
        else if (MetadataDb.Equals(DwhDb, StringComparison.OrdinalIgnoreCase)) e.Add("The metadata and DWH databases must be different.");
        if (DatabaseAction is not ("Now" or "Later")) e.Add("DatabaseAction must be Now or Later.");
        if (IsCentral && (string.IsNullOrWhiteSpace(HangfireServer) || string.IsNullOrWhiteSpace(HangfireDb))) e.Add("Central mode needs the Hangfire server and database.");
        if (InstallsWeb && (HttpsPort is < 1 or > 65535)) e.Add("Invalid HTTPS port.");
        if (InstallsWeb && !HostPattern().IsMatch(HostName ?? "")) e.Add("Invalid host name.");
        if (!string.IsNullOrWhiteSpace(SecretKey))
        {
            try { if (Convert.FromBase64String(SecretKey.Trim()).Length != 32) e.Add("The secret key must be 32 bytes (base64)."); }
            catch (FormatException) { e.Add("The secret key is not valid base64."); }
        }
        if (string.IsNullOrWhiteSpace(InstallDir) || string.IsNullOrWhiteSpace(DataDir)) e.Add("Enter the installation and data folders.");
        if (!string.IsNullOrWhiteSpace(WorkDir) && WorkDirProblem(WorkDir.Trim()) is { } w) e.Add(w);
        return e;
    }

    /// <summary>Why a folder cannot be the export work folder (null = fine): it must be a full local path.</summary>
    public static string? WorkDirProblem(string dir) =>
        dir.StartsWith(@"\\", StringComparison.Ordinal) || dir.StartsWith("//", StringComparison.Ordinal)
            ? @"The export work folder must be on a local disk, not a network share (\\server\share)."
            : !Path.IsPathFullyQualified(dir) || dir.IndexOfAny(Path.GetInvalidPathChars()) >= 0
                ? @"The export work folder must be a full path such as E:\SqlHarbor\work."
                : null;

    /// <summary>True when <paramref name="path"/> is <paramref name="folder"/> or below it.</summary>
    public static bool IsInside(string path, string folder)
    {
        static string Norm(string p) => Path.TrimEndingDirectorySeparator(Path.GetFullPath(p)) + Path.DirectorySeparatorChar;
        return Norm(path).StartsWith(Norm(folder), StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Collector:WorkDirectory of an existing appsettings.Production.json (an upgrade keeps the folder in use); null = not set.</summary>
    public static string? WorkDirectoryOf(string configJson)
    {
        try
        {
            var node = JsonNode.Parse(configJson, documentOptions: new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true });
            return node?["Collector"]?["WorkDirectory"] is JsonValue v && v.TryGetValue<string>(out var dir) && !string.IsNullOrWhiteSpace(dir) ? dir : null;
        }
        catch (JsonException) { return null; }
    }

    private static string DomainSuffix()
    {
        try
        {
            var d = System.Net.NetworkInformation.IPGlobalProperties.GetIPGlobalProperties().DomainName;
            return string.IsNullOrEmpty(d) ? "" : "." + d;
        }
        catch { return ""; }
    }

    [GeneratedRegex(@"^[^\\\s/]+\\[^\\\s/]+$")]
    private static partial Regex AccountPattern();

    [GeneratedRegex(@"^[A-Za-z0-9]([A-Za-z0-9\-\.]{0,251}[A-Za-z0-9])?$")]
    private static partial Regex HostPattern();
}
