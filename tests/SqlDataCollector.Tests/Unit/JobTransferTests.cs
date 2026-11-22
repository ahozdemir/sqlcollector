using SqlDataCollector.Models;
using SqlDataCollector.Services;

namespace SqlDataCollector.Tests.Unit;

public class JobTransferTests
{
    private static JobPackage SamplePackage() => new()
    {
        ExportedAtUtc = new DateTime(2026, 11, 22, 10, 0, 0, DateTimeKind.Utc), ExportedBy = "tester", AppVersion = "1.7.0",
        ExtractJobs =
        [
            new ExtractJobSpec
            {
                Name = "erp_orders", SourceServer = "ERP", SourceDatabase = "ERP", SourceSchema = "dbo", SourceTable = "Orders",
                LoadMode = LoadMode.Merge, KeyColumns = "OrderId", TargetStorage = TargetStorage.Columnstore, TargetServer = "DWH", TargetDatabase = "SdcDwh", TargetTable = "Orders",
                TriggerMode = JobTriggerMode.Schedule, CronExpression = "*/15 * * * *", TimeZoneId = "UTC", RetentionDays = 30,
                QualityChecks = [new QualityCheckSpec { CheckType = QualityCheckTypes.Duplicates, ColumnName = "OrderId", Threshold = 0 }]
            }
        ],
        ExportJobs =
        [
            new ExportJobSpec
            {
                Name = "orders_nightly", ExportMode = ExportMode.Full, SourceServer = "DWH", SourceDatabase = "SdcDwh", SourceSchema = "stage",
                SourceTable = "Orders", Destination = "s3-prod", FileFormat = ExportFileFormat.Parquet, TriggerMode = JobTriggerMode.AfterJobs,
                RunsAfter = [new JobRefSpec { Kind = JobRef.Extract, Name = "erp_orders" }]
            }
        ]
    };

    [Fact]
    public void Json_RoundTrip_UsesNamesForEnums_AndOmitsNulls()
    {
        var json = JobPackageJson.Serialize(SamplePackage());

        Assert.Contains("\"format\": \"SqlDataCollector.Jobs\"", json);
        Assert.Contains("\"loadMode\": \"Merge\"", json);
        Assert.Contains("\"fileFormat\": \"Parquet\"", json);
        Assert.Contains("\"targetStorage\": \"Columnstore\"", json);
        Assert.Contains("\"triggerMode\": \"AfterJobs\"", json);
        Assert.DoesNotContain("\"sourceQuery\"", json);   // null: not written
        Assert.DoesNotContain("secret", json, StringComparison.OrdinalIgnoreCase);

        var back = JobPackageJson.Deserialize(json);
        var x = Assert.Single(back.ExtractJobs);
        Assert.Equal("erp_orders", x.Name);
        Assert.Equal(LoadMode.Merge, x.LoadMode);
        Assert.Equal(TargetStorage.Columnstore, x.TargetStorage);
        Assert.Equal(30, x.RetentionDays);
        Assert.Equal(QualityCheckTypes.Duplicates, Assert.Single(x.QualityChecks).CheckType);
        var e = Assert.Single(back.ExportJobs);
        Assert.Equal("s3-prod", e.Destination);
        Assert.Equal("erp_orders", Assert.Single(e.RunsAfter).Name);
        Assert.Equal(new[] { "DWH", "ERP" }, back.ServerNames());
        Assert.Equal(new[] { "s3-prod" }, back.DestinationNames());
    }

    [Theory]
    [InlineData("not json")]
    [InlineData("{\"format\":\"Something.Else\",\"version\":1}")]
    [InlineData("{\"format\":\"SqlDataCollector.Jobs\",\"version\":99}")]
    [InlineData("{\"format\":\"SqlDataCollector.Jobs\",\"version\":1,\"extractJobs\":[{\"name\":\"a\",\"loadMode\":\"Sideways\"}]}")]
    public void Json_RejectsOtherFiles_WithAReadableMessage(string json) =>
        Assert.Throws<InvalidDataException>(() => JobPackageJson.Deserialize(json));

    [Fact]
    public void Json_ToleratesHandEditedFiles()
    {
        var p = JobPackageJson.Deserialize("""
            // comments and trailing commas are fine
            { "format": "SqlDataCollector.Jobs", "version": 1,
              "extractJobs": [ { "name": null, "runsAfter": null, "qualityChecks": [ null ] }, null, ],
              "exportJobs": [ { "name": "e", "runsAfter": [ { "name": null } ] } ] }
            """);
        Assert.Equal("", Assert.Single(p.ExtractJobs).Name);
        Assert.Empty(p.ExtractJobs[0].QualityChecks);
        Assert.Equal("", Assert.Single(p.ExportJobs[0].RunsAfter).Name);
    }

    private static ExtractJobDef Extract(Action<ExtractJobDef>? configure = null)
    {
        var j = new ExtractJobDef
        {
            Name = " orders ", SourceServerId = 1, SourceDatabase = "ERP", SourceSchema = "dbo", SourceTable = "Orders",
            TargetServerId = 2, TargetDatabase = "DWH", TargetSchema = "stage", TargetTable = "Orders", CronExpression = "0 2 * * *", TimeZoneId = "UTC"
        };
        configure?.Invoke(j);
        return j;
    }

    [Fact]
    public void ValidateExtract_ChecksTheEditorRules_AndNormalizes()
    {
        var ok = Extract(j => { j.LoadMode = LoadMode.Replace; j.RetentionDays = 10; j.WatermarkColumn = "x"; j.BatchSize = 5; });
        Assert.Empty(JobDefinitionRules.ValidateExtract(ok));
        Assert.Equal("orders", ok.Name);
        Assert.Null(ok.RetentionDays);       // Replace keeps only the latest load
        Assert.Null(ok.WatermarkColumn);     // no watermark outside Incremental/Merge
        Assert.Equal(10000, ok.BatchSize);

        Assert.Contains(JobDefinitionRules.ValidateExtract(Extract(j => j.LoadMode = LoadMode.Merge)), e => e.Contains("key column"));
        Assert.Contains(JobDefinitionRules.ValidateExtract(Extract(j => j.LoadMode = LoadMode.Incremental)), e => e.Contains("watermark"));
        Assert.Contains(JobDefinitionRules.ValidateExtract(Extract(j => j.CronExpression = "every night")), e => e.StartsWith("Invalid schedule"));
        Assert.Empty(JobDefinitionRules.ValidateExtract(Extract(j => { j.CronExpression = "bad"; j.TriggerMode = JobTriggerMode.AfterJobs; })));
        Assert.Contains(JobDefinitionRules.ValidateExtract(Extract(j => j.NotifyEmails = "a@b.com; nope")), e => e.Contains("nope"));

        var query = Extract(j => { j.SourceType = SourceType.Query; j.SourceQuery = "SELECT 1 AS x"; j.FilterClause = "x > 1"; });
        Assert.Empty(JobDefinitionRules.ValidateExtract(query));
        Assert.Null(query.SourceTable);
        Assert.Null(query.FilterClause);
    }

    [Fact]
    public void ValidateExport_ChecksModeTemplateAndFormat()
    {
        ExportJobDef Export(Action<ExportJobDef> c)
        {
            var e = new ExportJobDef { Name = "e", SourceDatabase = "DWH", SourceSchema = "stage", SourceTable = "Orders", ExportMode = ExportMode.Full, CronExpression = "0 3 * * *", TimeZoneId = "UTC" };
            c(e);
            return e;
        }
        Assert.Empty(JobDefinitionRules.ValidateExport(Export(_ => { })));
        Assert.Contains(JobDefinitionRules.ValidateExport(Export(e => { e.ExportMode = ExportMode.AllTables; e.ObjectNameTemplate = "x/{Part}.csv"; })), m => m.Contains("{Table}"));
        Assert.Contains(JobDefinitionRules.ValidateExport(Export(e => e.ParquetCompression = "Lz4")), m => m.Contains("Lz4"));
        Assert.Contains(JobDefinitionRules.ValidateExport(Export(e => e.SourceTable = null)), m => m.Contains("Table is required"));

        var all = Export(e => { e.ExportMode = ExportMode.AllTables; e.ObjectNameTemplate = "x/{Table}/{Part}.csv"; e.SourceSchema = " "; });
        Assert.Empty(JobDefinitionRules.ValidateExport(all));
        Assert.Null(all.SourceTable);
        Assert.Null(all.SourceSchema);
    }

    [Fact]
    public void TargetConflict_RebuildAndMergeNeedTheirOwnTable()
    {
        var merge = Extract(j => { j.ExtractJobId = 1; j.Name = "m"; j.LoadMode = LoadMode.Merge; });
        var append = Extract(j => { j.ExtractJobId = 2; j.Name = "a"; });
        var elsewhere = Extract(j => { j.ExtractJobId = 3; j.Name = "b"; j.TargetTable = "Other"; });

        Assert.Contains("also written by: a", JobDefinitionRules.TargetConflict(merge, [merge, append]));
        Assert.Contains("merges into this target table", JobDefinitionRules.TargetConflict(append, [merge, append]));
        Assert.Null(JobDefinitionRules.TargetConflict(merge, [merge, elsewhere]));

        // two new jobs (no ids yet) of one package that write to the same table
        var newMerge = Extract(j => { j.Name = "n1"; j.LoadMode = LoadMode.Merge; });
        var newAppend = Extract(j => j.Name = "n2");
        Assert.Contains("also written by: n2", JobDefinitionRules.TargetConflict(newMerge, [newMerge, newAppend]));
        Assert.Null(JobDefinitionRules.TargetConflict(newMerge, [newMerge]));
    }
}
