using System.Text.RegularExpressions;
using SqlDataCollector.Infrastructure;

namespace SqlDataCollector.Services;

public static class CheckCategory
{
    public const string Metadata = "Metadata";
    public const string Hangfire = "Hangfire";
    public const string Target = "Target";
    public const string Source = "Source";
    public const string Alerts = "Alerts";
    public const string Destinations = "Destinations";
}

public static class CheckStatus
{
    public const string Ok = "Ok";
    public const string Missing = "Missing";
    public const string Excess = "Excess";
    public const string Warning = "Warning";
    public const string Error = "Error";
}

/// <summary>One permission evaluated with HAS_PERMS_BY_NAME. Securable null with class DATABASE = the connected database.</summary>
public sealed record PermissionProbe(string Permission, string SecurableClass, string? Securable = null)
{
    public static PermissionProbe Database(string permission) => new(permission, "DATABASE");
    public static PermissionProbe Schema(string permission, string schema) => new(permission, "SCHEMA", schema);
    public static PermissionProbe Object(string permission, string name) => new(permission, "OBJECT", name);

    public string Label => SecurableClass == "DATABASE" ? Permission : $"{Permission} on {Securable}";
}

/// <summary>
/// Required: every probe must be granted (else Missing). Forbidden: none may be granted (else Excess).
/// Advisory: like Required, but only a Warning (the need cannot be determined exactly, e.g. free SQL).
/// </summary>
public enum ProbeKind { Required, Forbidden, Advisory }

public sealed record ProbeGroup(string Item, ProbeKind Kind, IReadOnlyList<PermissionProbe> Probes, string? Hint = null);

/// <summary>
/// The process a check runs as. Web: the Web process itself (button on the Servers page).
/// Worker: a Worker executing the check job of one queue (or its startup self-check).
/// The flags come from that process's own configuration.
/// </summary>
public sealed record CheckContext(
    string ProcessRole,
    string? Queue,
    bool WebOnly,
    bool AutoCreateSchema,
    bool HangfirePrepareSchema,
    bool SharedHangfire,
    string HangfireSchema,
    string? ExportReadAsUser = null)
{
    public const string Web = "Web";
    public const string Worker = "Worker";

    public bool IsWeb => ProcessRole == Web;
    public bool WritesTarget => ProcessRole == Worker && Queue is CollectorQueues.Extract or CollectorQueues.System;
    public bool ReadsTarget => IsWeb || (ProcessRole == Worker && Queue == CollectorQueues.Export);
    public bool ReadsSources => IsWeb || (ProcessRole == Worker && Queue == CollectorQueues.Extract);
}

public enum SourceObjectKind { Table, Procedure, Query }

/// <summary>What an extract job reads on a source database.</summary>
public sealed record SourceObject(SourceObjectKind Kind, string? Name);

/// <summary>A target schema used by jobs and whether it already exists in the database.</summary>
public sealed record TargetSchema(string Name, bool Exists);

/// <summary>
/// The minimum permissions of sql/01_permissions_template.sql as rules, plus the permissions that must NOT be granted.
/// Pure functions (no SQL) so they are unit-testable; PermissionChecker evaluates the probes on the servers.
/// </summary>
public static partial class PermissionRules
{
    public const string MetadataSchema = "etl";

    private static readonly string[] Dml = ["SELECT", "INSERT", "UPDATE", "DELETE"];
    private static readonly string[] Writes = ["INSERT", "UPDATE", "DELETE", "ALTER"];

    public static IReadOnlyList<ProbeGroup> Metadata(CheckContext c)
    {
        var groups = new List<ProbeGroup>
        {
            new("Read/write etl tables", ProbeKind.Required, Dml.Select(p => PermissionProbe.Schema(p, MetadataSchema)).ToList(),
                "Template: db_datareader + db_datawriter on the metadata database.")
        };
        if (c.AutoCreateSchema)
            groups.Add(new("Install/upgrade the etl schema (AutoCreateSchema=true)", ProbeKind.Required,
                [PermissionProbe.Database("CREATE TABLE"), PermissionProbe.Database("CREATE VIEW"), PermissionProbe.Schema("ALTER", MetadataSchema)],
                "Grant db_ddladmin, or let the DBA run 001_schema.sql and set Collector:AutoCreateSchema=false."));
        return groups;
    }

    public static IReadOnlyList<ProbeGroup> Hangfire(CheckContext c)
    {
        var groups = new List<ProbeGroup>
        {
            new("Read/write Hangfire tables", ProbeKind.Required, Dml.Select(p => PermissionProbe.Schema(p, c.HangfireSchema)).ToList(),
                "Generate the DBA script with: SqlDataCollector hangfire-install-script.")
        };
        if (c.HangfirePrepareSchema)
            groups.Add(new("Install/upgrade the Hangfire schema (HangfirePrepareSchema=true)", ProbeKind.Required,
                [PermissionProbe.Database("CREATE TABLE"), PermissionProbe.Schema("ALTER", c.HangfireSchema)],
                "Grant DDL, or let the DBA install the schema and set Collector:HangfirePrepareSchema=false (recommended for a shared database)."));
        if (c.SharedHangfire)
            groups.Add(new("No rights outside our schema (shared Hangfire database)", ProbeKind.Forbidden,
                [PermissionProbe.Database("INSERT"), PermissionProbe.Database("UPDATE"), PermissionProbe.Database("DELETE"),
                 PermissionProbe.Database("ALTER"), PermissionProbe.Database("CONTROL")],
                "Other applications' Hangfire schemas live in this database: grant DML on our schema only."));
        return groups;
    }

    public static IReadOnlyList<ProbeGroup> Target(CheckContext c, IReadOnlyList<TargetSchema> schemas, bool partitionDaily)
    {
        var groups = new List<ProbeGroup>();
        if (c.WritesTarget)
        {
            groups.Add(new("Load and clean up data", ProbeKind.Required, SchemaProbes(Dml, schemas),
                "Template: db_datareader + db_datawriter on the target database."));
            var ddl = new List<PermissionProbe> { PermissionProbe.Database("CREATE TABLE") };
            ddl.AddRange(schemas.Where(s => s.Exists).Select(s => PermissionProbe.Schema("ALTER", s.Name)));
            if (schemas.Any(s => !s.Exists)) ddl.Add(PermissionProbe.Database("CREATE SCHEMA"));
            groups.Add(new("Create and alter target tables", ProbeKind.Required, ddl,
                "Template: db_ddladmin (CREATE/ALTER TABLE, TRUNCATE ... WITH (PARTITIONS))."));
            if (partitionDaily)
                groups.Add(new("Daily partitions (CREATE/SPLIT/MERGE PARTITION)", ProbeKind.Required,
                    [PermissionProbe.Database("ALTER ANY DATASPACE")], "Template: GRANT ALTER ANY DATASPACE."));
        }
        if (c.ReadsTarget)
        {
            var read = SchemaProbes(["SELECT"], schemas).ToList();
            if (c.IsWeb) read.Add(PermissionProbe.Database("VIEW DEFINITION"));
            groups.Add(new(c.IsWeb ? "Read for the UI (lists, preview, check target)" : "Read collected data for the export", ProbeKind.Required, read,
                c.IsWeb ? "Template: db_datareader + VIEW DEFINITION." : "Template: db_datareader."));
        }
        if (c.IsWeb && c.WebOnly)
        {
            var forbidden = new List<PermissionProbe>
            {
                PermissionProbe.Database("INSERT"), PermissionProbe.Database("UPDATE"), PermissionProbe.Database("DELETE"),
                PermissionProbe.Database("CREATE TABLE"), PermissionProbe.Database("ALTER")
            };
            forbidden.AddRange(schemas.Where(s => s.Exists).SelectMany(s => Writes.Select(p => PermissionProbe.Schema(p, s.Name))));
            groups.Add(new("No write access (Web role)", ProbeKind.Forbidden, forbidden,
                "The Web role only reads target databases; cleanup and recovery run in Workers."));
        }
        return groups;
    }

    /// <summary>Rights of the export reader user itself (evaluated while impersonating it).</summary>
    public static IReadOnlyList<ProbeGroup> ExportReader(string user, IReadOnlyList<TargetSchema> schemas)
    {
        var forbidden = new List<PermissionProbe>
        {
            PermissionProbe.Database("INSERT"), PermissionProbe.Database("UPDATE"), PermissionProbe.Database("DELETE"),
            PermissionProbe.Database("ALTER"), PermissionProbe.Database("CREATE TABLE"), PermissionProbe.Database("CONTROL")
        };
        forbidden.AddRange(schemas.Where(s => s.Exists).SelectMany(s => Writes.Select(p => PermissionProbe.Schema(p, s.Name))));
        return
        [
            new($"Export reader '{user}' can read", ProbeKind.Required, SchemaProbes(["SELECT"], schemas),
                $"ALTER ROLE db_datareader ADD MEMBER [{user}];"),
            new($"Export reader '{user}' has no write access", ProbeKind.Forbidden, forbidden,
                "Export SQL is written by Editors and runs as this user: grant read only.")
        ];
    }

    public static IReadOnlyList<ProbeGroup> Source(CheckContext c, IReadOnlyList<SourceObject> objects)
    {
        if (!c.ReadsSources) return [];
        var groups = new List<ProbeGroup>();

        var exact = objects.Where(o => o.Kind != SourceObjectKind.Query && o.Name is not null)
            .Select(o => PermissionProbe.Object(o.Kind == SourceObjectKind.Procedure ? "EXECUTE" : "SELECT", o.Name!))
            .Distinct().ToList();
        if (exact.Count > 0)
            groups.Add(new("Read source tables/views and execute procedures", ProbeKind.Required, exact,
                "Template: db_datareader (+ GRANT EXECUTE for stored procedure jobs)."));
        if (objects.Any(o => o.Kind == SourceObjectKind.Query))
            groups.Add(new("Free SQL queries (database-wide read)", ProbeKind.Advisory, [PermissionProbe.Database("SELECT")],
                "Free SQL cannot be checked object by object; without database-wide SELECT make sure every object the queries use is granted."));

        var forbidden = new List<PermissionProbe>
        {
            PermissionProbe.Database("INSERT"), PermissionProbe.Database("UPDATE"), PermissionProbe.Database("DELETE"),
            PermissionProbe.Database("ALTER"), PermissionProbe.Database("CREATE TABLE"), PermissionProbe.Database("CONTROL")
        };
        forbidden.AddRange(objects.Where(o => o.Kind == SourceObjectKind.Table && o.Name is not null)
            .Select(o => SchemaOf(o.Name!)).Distinct(StringComparer.OrdinalIgnoreCase)
            .SelectMany(s => Writes.Select(p => PermissionProbe.Schema(p, s))));
        groups.Add(new("No write or DDL permissions (security boundary)", ProbeKind.Forbidden, forbidden.Distinct().ToList(),
            "Editors can run free SQL on sources: the service account's permissions are the security boundary. Grant read only."));
        return groups;
    }

    /// <summary>
    /// Evaluates a group. results[i] belongs to group.Probes[i]: true = granted, false = not granted,
    /// null = securable not found or not visible to the login (treated as not granted).
    /// </summary>
    public static (string Status, string? Detail) Evaluate(ProbeGroup group, IReadOnlyList<bool?> results, bool isSysadmin = false, bool isDbOwner = false)
    {
        if (results.Count != group.Probes.Count) throw new ArgumentException("One result per probe is required.", nameof(results));
        var pairs = group.Probes.Zip(results).ToList();

        if (group.Kind == ProbeKind.Forbidden)
        {
            var granted = pairs.Where(p => p.Second == true).Select(p => p.First.Label).ToList();
            if (granted.Count == 0) return (CheckStatus.Ok, null);
            var who = isSysadmin ? " The login is a member of sysadmin." : isDbOwner ? " The login is a member of db_owner." : "";
            return (CheckStatus.Excess, $"Granted but not needed: {string.Join(", ", granted)}.{who} {group.Hint}".Trim());
        }

        var missing = pairs.Where(p => p.Second != true)
            .Select(p => p.Second is null && p.First.SecurableClass != "DATABASE" ? $"{p.First.Label} (not found or not visible)" : p.First.Label)
            .ToList();
        if (missing.Count == 0) return (CheckStatus.Ok, null);
        return (group.Kind == ProbeKind.Advisory ? CheckStatus.Warning : CheckStatus.Missing,
            $"Missing: {string.Join(", ", missing)}. {group.Hint}".Trim());
    }

    /// <summary>Classifies what an extract job reads: a table/view, a stored procedure (EXEC ...) or free SQL.</summary>
    public static SourceObject SourceObjectOf(Models.ExtractJobDef job)
    {
        if (job.SourceType == Models.SourceType.Table && !string.IsNullOrWhiteSpace(job.SourceTable))
            return new(SourceObjectKind.Table, SqlIdent.Quote(job.SourceSchema ?? "dbo", job.SourceTable));
        var m = ExecPattern().Match(job.SourceQuery ?? "");
        return m.Success ? new(SourceObjectKind.Procedure, m.Groups["name"].Value) : new(SourceObjectKind.Query, null);
    }

    private static IReadOnlyList<PermissionProbe> SchemaProbes(IEnumerable<string> permissions, IReadOnlyList<TargetSchema> schemas)
    {
        var existing = schemas.Where(s => s.Exists).ToList();
        var list = new List<PermissionProbe>();
        foreach (var p in permissions)
        {
            list.AddRange(existing.Select(s => PermissionProbe.Schema(p, s.Name)));
            // a schema that does not exist yet is created by the first load: the permission must come from the database level
            if (existing.Count < schemas.Count || schemas.Count == 0) list.Add(PermissionProbe.Database(p));
        }
        return list;
    }

    private static string SchemaOf(string quotedName)
    {
        var i = quotedName.IndexOf("].[", StringComparison.Ordinal);
        return i > 0 ? quotedName[1..i].Replace("]]", "]") : "dbo";
    }

    // EXEC dbo.usp_X ... | EXECUTE [dbo].[usp_X] | EXEC @rc = dbo.usp_X
    [GeneratedRegex(@"^\s*EXEC(?:UTE)?\s+(?:@\w+\s*=\s*)?(?<name>(?:\[[^\]]+\]|\w+)(?:\.(?:\[[^\]]+\]|\w+)){0,2})", RegexOptions.IgnoreCase)]
    private static partial Regex ExecPattern();
}
