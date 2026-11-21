using Dapper;
using SqlDataCollector.Export;
using SqlDataCollector.Infrastructure;
using SqlDataCollector.Models;
using SqlDataCollector.Privacy;

namespace SqlDataCollector.Tests.Integration;

/// <summary>Masking rules in exports and while collecting, the export guard and the personal data scan.</summary>
[Collection(IntegrationCollection.Name)]
[Trait("Category", "Integration")]
public sealed class PrivacyTests(CollectorFixture fx)
{
    private const string Dwh = "DWH";
    private const string Src = "SrcDb";
    private const string Meta = "DataCollectorMeta";

    private static string PeopleTable(string schema, string name) => $"""
        IF SCHEMA_ID('{schema}') IS NULL EXEC('CREATE SCHEMA {schema}');
        DROP TABLE IF EXISTS {schema}.{name};
        CREATE TABLE {schema}.{name}(Id int NOT NULL PRIMARY KEY, Name nvarchar(50), Email nvarchar(100), Tckn bigint, Iban varchar(34));
        INSERT {schema}.{name} VALUES
            (1, N'Ayşe', N'ayse.yilmaz@corp.com', 10000000146, 'TR330006100519786457841326'),
            (2, N'Bora', N'bora@corp.com',        10000000146, 'TR330006100519786457841326'),
            (3, N'Cem',  N'cem@example.org',      10000000146, 'TR330006100519786457841326'),
            (4, N'Deniz', NULL,                   NULL,        NULL);
        """;

    private async Task<int> RuleAsync(string name, string columns, MaskMethod method, MaskTarget target = MaskTarget.Exports, string scope = "*", int keep = 4) =>
        await fx.Repo.SaveMaskingRuleAsync(new MaskingRule
        {
            Name = name + "_" + Guid.NewGuid().ToString("N")[..6], ColumnPattern = columns, ScopePattern = scope,
            Method = method, KeepLast = keep, AppliesTo = target
        }, "tests");

    private async Task CleanupAsync(params int[] ruleIds)
    {
        foreach (var id in ruleIds) await fx.Repo.DeleteMaskingRuleAsync(id, "tests");
        await fx.ExecAsync(Meta, $"DELETE etl.SystemSetting WHERE Name = '{MaskingService.GuardSetting}';");
    }

    private async Task<ExportJobDef> ExportAsync(string schema, string table, string prefix)
    {
        var e = new ExportJobDef
        {
            Name = "pii_" + Guid.NewGuid().ToString("N")[..6], ExportMode = ExportMode.Full,
            SourceServerId = fx.TargetServerId, SourceDatabase = Dwh, SourceSchema = schema, SourceTable = table,
            Bucket = CollectorFixture.Bucket, ObjectNameTemplate = prefix + "/{Name}_{Part}.csv",
            CronExpression = "0 1 * * *", TimeZoneId = "UTC", UploadWhenEmpty = false, WriteManifest = false, CommandTimeoutSec = 300
        };
        await fx.Repo.SaveExportJobAsync(e, "tests");
        return e;
    }

    private Task<(string Status, string? Error)> LastExportAsync(ExportJobDef e) => QueryAsync<(string, string?)>(Meta,
        "SELECT TOP 1 Status, ErrorMessage FROM etl.ExportRun WHERE ExportJobId = @id ORDER BY ExportRunId DESC", new { id = e.ExportJobId });

    private async Task<T> QueryAsync<T>(string db, string sql, object? args = null)
    {
        await using var c = fx.Db(db);
        return await c.QuerySingleAsync<T>(sql, args);
    }

    [Fact]
    public async Task Export_MasksColumnsByRule_AndTheGuardStaysQuiet()
    {
        await fx.ExecAsync(Dwh, PeopleTable("pii", "People1"));
        var r1 = await RuleAsync("mail", "*Email*", MaskMethod.Email);
        var r2 = await RuleAsync("tckn", "Tckn", MaskMethod.Hash, scope: "DWH.pii.*");
        var r3 = await RuleAsync("iban", "Iban", MaskMethod.Partial);
        try
        {
            var e = await ExportAsync("pii", "People1", "pii-masked");
            await fx.RunExportAsync(e);

            var (status, error) = await LastExportAsync(e);
            Assert.Equal("Succeeded", status);
            Assert.Null(error);   // every personal data column is masked; Name is not personal data by pattern

            var csv = await fx.DownloadTextAsync(Assert.Single(await fx.ListObjectsAsync($"pii-masked/{e.Name}")).Name);
            var hash = Masker.Hash("10000000146", fx.Get<SecretProtector>().DeriveKey("masking")!);
            Assert.Contains($"1,Ayşe,a***@corp.com,{hash},**********************1326", csv);
            Assert.DoesNotContain("ayse.yilmaz", csv);
            Assert.DoesNotContain("10000000146", csv);
        }
        finally { await CleanupAsync(r1, r2, r3); }
    }

    [Fact]
    public async Task Guard_WarnsByDefault_AndBlockStopsTheUploadAndRemovesTheFiles()
    {
        await fx.ExecAsync(Dwh, PeopleTable("pii", "People2"));
        try
        {
            var e = await ExportAsync("pii", "People2", "pii-guard");
            await fx.RunExportAsync(e);
            var (status, error) = await LastExportAsync(e);
            Assert.Equal("Succeeded", status);
            Assert.Contains("Data protection warning", error);
            Assert.Contains("Email (e-mail address", error);
            Assert.Contains("Tckn (national ID", error);
            Assert.Contains("Iban (IBAN", error);

            await fx.Get<MaskingService>().SaveGuardAsync(PrivacyGuardMode.Block, "tests");
            var blocked = await ExportAsync("pii", "People2", "pii-blocked");
            var ex = await Assert.ThrowsAnyAsync<Exception>(() => fx.RunExportAsync(blocked));
            Assert.Contains("blocked by the data protection guard", ex.Message);
            Assert.Equal("Failed", (await LastExportAsync(blocked)).Status);
            Assert.Empty(await fx.ListObjectsAsync("pii-blocked/"));
            Assert.Empty(Directory.Exists(fx.WorkDirectory) ? Directory.GetFiles(fx.WorkDirectory, $"{blocked.Name}*", SearchOption.AllDirectories) : []);
        }
        finally { await CleanupAsync(); }
    }

    [Fact]
    public async Task Extract_MasksWhileCollecting_TheDwhHoldsMaskedValues()
    {
        await fx.ExecAsync(Src, PeopleTable("dbo", "PiiSource"));
        var r1 = await RuleAsync("collect_mail", "Email", MaskMethod.Email, MaskTarget.ExtractAndExports, scope: "SrcDb.dbo.PiiSource");
        var r2 = await RuleAsync("collect_tckn", "Tckn", MaskMethod.Hash, MaskTarget.ExtractAndExports, scope: "SrcDb.dbo.PiiSource");
        var r3 = await RuleAsync("collect_iban", "Iban", MaskMethod.Null, MaskTarget.ExtractAndExports, scope: "SrcDb.dbo.PiiSource");
        var job = await fx.SaveAsync(fx.NewExtractJob("PiiCollected", j => { j.SourceTable = "PiiSource"; j.TargetSchema = "piic"; }));
        try
        {
            await fx.RunExtractAsync(job);
            Assert.Equal("a***@corp.com", await fx.ScalarAsync<string>(Dwh, "SELECT Email FROM piic.PiiCollected WHERE Id = 1"));
            Assert.Equal(Masker.Hash("10000000146", fx.Get<SecretProtector>().DeriveKey("masking")!),
                await fx.ScalarAsync<string>(Dwh, "SELECT Tckn FROM piic.PiiCollected WHERE Id = 1"));
            Assert.Equal(0, await fx.ScalarAsync<int>(Dwh, "SELECT COUNT(*) FROM piic.PiiCollected WHERE Iban IS NOT NULL"));
            Assert.Equal("Ayşe", await fx.ScalarAsync<string>(Dwh, "SELECT Name FROM piic.PiiCollected WHERE Id = 1"));
            // the hashed bigint column is text in the target; the NULLed one keeps its type
            Assert.Equal("nvarchar(64)", await fx.ScalarAsync<string>(Dwh,
                "SELECT CONCAT(t.name, '(', c.max_length / 2, ')') FROM sys.columns c JOIN sys.types t ON t.user_type_id = c.user_type_id WHERE c.object_id = OBJECT_ID('piic.PiiCollected') AND c.name = 'Tckn'"));
            Assert.Equal("varchar", await fx.ScalarAsync<string>(Dwh,
                "SELECT t.name FROM sys.columns c JOIN sys.types t ON t.user_type_id = c.user_type_id WHERE c.object_id = OBJECT_ID('piic.PiiCollected') AND c.name = 'Iban'"));
        }
        finally
        {
            await fx.Repo.DeleteExtractJobAsync(job.ExtractJobId, "tests");
            await CleanupAsync(r1, r2, r3);
        }
    }

    [Fact]
    public async Task Scan_FindsUnmaskedPersonalData_AndShowsTheCoveringRule()
    {
        await fx.ExecAsync(Src, PeopleTable("dbo", "PiiScanSource"));
        var job = await fx.SaveAsync(fx.NewExtractJob("PiiScanned", j => { j.SourceTable = "PiiScanSource"; j.TargetSchema = "piis"; }));
        int? rule = null;
        try
        {
            await fx.RunExtractAsync(job);
            var scanner = fx.Get<PrivacyScanner>();
            var findings = (await scanner.ScanAsync(fx.TargetServerId, Dwh)).Where(f => f.Table == "PiiScanned").ToList();
            Assert.Equal(["Email", "Iban", "Tckn"], findings.Select(f => f.Column).Order().ToList());
            Assert.All(findings, f => Assert.Null(f.CoveredBy));
            Assert.Equal(PiiKind.NationalId, findings.Single(f => f.Column == "Tckn").Kind);
            Assert.DoesNotContain("ayse.yilmaz@corp.com", findings.Select(f => f.Example));   // examples are partly hidden

            rule = await RuleAsync("scan_mail", "Email", MaskMethod.Email, scope: "DWH.piis.*");
            findings = (await scanner.ScanAsync(fx.TargetServerId, Dwh)).Where(f => f.Table == "PiiScanned").ToList();
            Assert.NotNull(findings.Single(f => f.Column == "Email").CoveredBy);
            Assert.Null(findings.Single(f => f.Column == "Tckn").CoveredBy);
        }
        finally
        {
            await fx.Repo.DeleteExtractJobAsync(job.ExtractJobId, "tests");
            if (rule is { } id) await CleanupAsync(id);
        }
    }
}
