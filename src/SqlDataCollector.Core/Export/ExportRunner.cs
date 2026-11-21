using System.Data;
using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;
using Hangfire;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Options;
using SqlDataCollector.Extract;
using SqlDataCollector.Infrastructure;
using SqlDataCollector.Models;
using SqlDataCollector.Options;
using SqlDataCollector.Services;

namespace SqlDataCollector.Export;

public static class ObjectNameTemplate
{
    public const string DefaultSingle = "exports/{Name}/dt={Date}/{Name}_{DateCompact}_{Time}_{Part}.csv";
    public const string DefaultAllTables = "exports/{Database}/{Schema}.{Table}/dt={Date}/{Table}_{DateCompact}_{Time}_{Part}.csv";

    public static string Render(string template, string name, string? database, string? schema, string? table, DateTime local, string part)
    {
        var inv = CultureInfo.InvariantCulture;
        return template
            .Replace("{Name}", Sanitize(name))
            .Replace("{Database}", Sanitize(database ?? ""))
            .Replace("{Schema}", Sanitize(schema ?? ""))
            .Replace("{Table}", Sanitize(table ?? name))
            .Replace("{DateCompact}", local.ToString("yyyyMMdd", inv))
            .Replace("{Date}", local.ToString("yyyy-MM-dd", inv))
            .Replace("{Time}", local.ToString("HHmmss", inv))
            .Replace("{Part}", part)
            .TrimStart('/');
    }

    /// <summary>Folder part of the template before any table-specific token (used for the run manifest).</summary>
    public static string CommonPrefix(string template, string name, string? database, DateTime local)
    {
        int cut = new[] { "{Schema}", "{Table}", "{Part}" }
            .Select(t => template.IndexOf(t, StringComparison.Ordinal)).Where(i => i >= 0).DefaultIfEmpty(template.Length).Min();
        var prefix = Render(template[..cut], name, database, null, null, local, "");
        int slash = prefix.LastIndexOf('/');
        return slash >= 0 ? prefix[..(slash + 1)] : "";
    }

    /// <summary>
    /// Final object name for the file format: Parquet files end in .parquet (a ".csv" / ".csv.gz" ending of the template is replaced),
    /// gzip-uploaded CSV files end in .gz.
    /// </summary>
    public static string WithExtension(string name, ExportJobDef job)
    {
        if (job.FileFormat == ExportFileFormat.Parquet)
        {
            foreach (var ext in new[] { ".csv.gz", ".csv", ".gz" })
                if (name.EndsWith(ext, StringComparison.OrdinalIgnoreCase)) { name = name[..^ext.Length]; break; }
            return name.EndsWith(".parquet", StringComparison.OrdinalIgnoreCase) ? name : name + ".parquet";
        }
        return job.Gzip && !name.EndsWith(".gz", StringComparison.OrdinalIgnoreCase) ? name + ".gz" : name;
    }

    public static string Sanitize(string s) =>
        new(s.Select(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_' or '.' ? c : '_').ToArray());
}

/// <summary>
/// Produces CSV files from the collected data and uploads them to Google Cloud Storage.
/// AllTables mode exports every extract target table of a database, one CSV set per table,
/// incrementally per table (= what was collected since the previous run).
/// </summary>
public sealed class ExportRunner(
    MetadataRepository repo,
    SqlConnectionFactory factory,
    GcsUploader uploader,
    Privacy.MaskingService masking,
    DependencyService dependencies,
    IOptions<CollectorOptions> options,
    ILogger<ExportRunner> logger)
{
    private sealed record Dataset(List<CsvPart> Parts, IReadOnlyList<string> Columns, double WriteSec, double UploadSec,
        IReadOnlyList<string> Masked, IReadOnlyList<string> PrivacyWarnings)
    {
        public long Rows => Parts.Sum(p => p.Rows);
        public long Bytes => Parts.Sum(p => p.Bytes);
    }

    private sealed class PartialExportException(string message) : Exception(message);

    [JobDisplayName("Export #{0}: {1}")]
    [AutomaticRetry(Attempts = 3, DelaysInSeconds = [300, 900, 1800], OnAttemptsExceeded = AttemptsExceededAction.Fail)]
    public async Task RunAsync(int exportJobId, string jobName, string? triggeredBy, CancellationToken ct)
    {
        try { await RunCoreAsync(exportJobId, triggeredBy, ct); }
        finally { await dependencies.TryEvaluateAsync(CancellationToken.None); }   // starts jobs that run "after" this one
    }

    private async Task RunCoreAsync(int exportJobId, string? triggeredBy, CancellationToken ct)
    {
        var job = await repo.GetExportJobAsync(exportJobId);
        if (job is null) { logger.LogWarning("Export job {Id} not found (may have been deleted).", exportJobId); return; }
        if (!job.IsActive && triggeredBy is null) { logger.LogInformation("Export job {Name} is inactive; skipped.", job.Name); return; }

        var server = await repo.GetServerAsync(job.SourceServerId);
        if (server is not { IsActive: true })
            throw new InvalidOperationException($"Export source server is inactive or missing (Id={job.SourceServerId}).");

        await using var jobLock = await AppLock.TryAcquireAsync(factory.MetadataConnectionString, LockNames.Export(exportJobId), 0, ct);
        if (jobLock is null) { logger.LogWarning("Export job {Name} is already running; this trigger was skipped.", job.Name); return; }

        var runId = await repo.StartExportRunAsync(exportJobId, triggeredBy);
        var tz = TimeZones.Resolve(job.TimeZoneId);
        var nowUtc = DateTime.UtcNow;
        var local = TimeZoneInfo.ConvertTimeFromUtc(nowUtc, tz);
        // Day folders: WorkDirectory\yyyy-MM-dd\<job>_<run>\... (kept as a local archive when KeepLocalFiles=true)
        var workDir = Path.Combine(options.Value.WorkDirectory, local.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
            $"{ObjectNameTemplate.Sanitize(job.Name)}_{runId}");
        var sw = Stopwatch.StartNew();

        try
        {
            // Incremental/AllTables: always read from the primary so a lagging readable secondary cannot skip rows
            bool primary = job.ExportMode is ExportMode.Incremental or ExportMode.AllTables;
            // Every export read (free SQL, column lists, AllTables) runs as the read-only Collector:ExportReadAsUser
            await using var session = await factory.OpenExportReadAsync(server, job.SourceDatabase, forWrite: primary, ct);
            var conn = session.Connection;

            if (job.ExportMode == ExportMode.AllTables)
            {
                await RunAllTablesAsync(conn, job, runId, local, nowUtc, workDir, sw, ct);
                return;
            }

            if (job.ExportMode != ExportMode.Query &&
                !await SqlHelpers.TableExistsAsync(conn, job.SourceSchema ?? "stage", job.SourceTable!, ct))
            {
                await repo.CompleteExportRunAsync(runId, exportJobId, "Skipped", 0, 0, 0, null, null, null, null);
                logger.LogWarning("Export {Name}: source table {Schema}.{Table} does not exist yet; skipped.", job.Name, job.SourceSchema ?? "stage", job.SourceTable);
                return;
            }

            long? lo = null, hi = null;
            if (job.ExportMode == ExportMode.Incremental)
            {
                lo = await repo.GetLastExportedLoadIdAsync(exportJobId);
                hi = await repo.GetSafeMaxLoadIdAsync(job.SourceServerId, job.SourceDatabase, job.SourceSchema ?? "stage", job.SourceTable!);
                if (hi < lo) hi = lo;
            }

            await using var cmd = await BuildCommandAsync(conn, job, job.SourceSchema ?? "stage", job.SourceTable, lo, hi, local, nowUtc, ct);
            var data = await WriteAndUploadAsync(cmd, job, workDir, ObjectNameTemplate.Sanitize(job.Name),
                job.SourceSchema, job.SourceTable, local, ct);

            if (data.Parts.Count == 0)
            {
                await repo.CompleteExportRunAsync(runId, exportJobId, "Skipped", 0, 0, 0, lo, hi, null, hi);
                logger.LogInformation("Export {Name}: no rows to export; nothing uploaded.", job.Name);
                return;
            }

            if (job.WriteManifest)
                await UploadManifestAsync(job, runId, local, nowUtc, new
                {
                    loadIdFrom = lo, loadIdTo = hi, totalRows = data.Rows, columns = data.Columns,
                    masked = data.Masked,
                    files = data.Parts.Select(p => new { objectName = p.ObjectName, rows = p.Rows, bytes = p.Bytes })
                }, ObjectNameTemplate.CommonPrefix(job.ObjectNameTemplate, job.Name, job.SourceDatabase, local), ct);

            await repo.CompleteExportRunAsync(runId, exportJobId, "Succeeded", data.Rows, data.Parts.Count, data.Bytes, lo, hi,
                string.Join(Environment.NewLine, data.Parts.Select(p => $"gs://{job.Bucket}/{p.ObjectName}")),
                job.ExportMode == ExportMode.Incremental ? hi : null,
                error: data.PrivacyWarnings.Count > 0 ? string.Join(Environment.NewLine, data.PrivacyWarnings) : null);

            logger.LogInformation("Export {Name}: {Rows:N0} rows, {Files} file(s), {Bytes:N0} bytes, {Sec:N1} s (read+CSV{Gz} {Write:N2} s, upload {Upload:N2} s)",
                job.Name, data.Rows, data.Parts.Count, data.Bytes, sw.Elapsed.TotalSeconds, job.Gzip ? "+gzip" : "", data.WriteSec, data.UploadSec);
        }
        catch (PartialExportException)
        {
            throw; // run already recorded as PartiallyFailed; Hangfire retries only the remaining tables
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Export {Name} failed (RunId={RunId}).", job.Name, runId);
            await repo.FailExportRunAsync(runId, "Failed", MetadataRepository.Truncate(ex));
            throw;
        }
        finally
        {
            if (!options.Value.KeepLocalFiles)
            {
                try { if (Directory.Exists(workDir)) Directory.Delete(workDir, recursive: true); }
                catch (Exception ex) { logger.LogWarning(ex, "Could not delete work directory {Dir}", workDir); }
            }
            CleanupOldDayFolders();
        }
    }

    // ------------------------------------------------------------------ AllTables

    private async Task RunAllTablesAsync(SqlConnection conn, ExportJobDef job, long runId, DateTime local, DateTime nowUtc,
        string workDir, Stopwatch sw, CancellationToken ct)
    {
        var tables = await repo.GetCollectedTablesAsync(job.SourceServerId, job.SourceDatabase, job.SourceSchema);
        var states = (await repo.GetExportTableStatesAsync(job.ExportJobId))
            .ToDictionary(s => $"{s.SchemaName}.{s.TableName}", s => s.LastExportedLoadId, StringComparer.OrdinalIgnoreCase);

        var summary = new List<object>();
        var objectNames = new List<string>();
        var errors = new StringBuilder();
        var privacyWarnings = new List<string>();
        long totalRows = 0, totalBytes = 0;
        int totalFiles = 0, ok = 0, failed = 0, skipped = 0;

        foreach (var t in tables)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                if (!await SqlHelpers.TableExistsAsync(conn, t.SchemaName, t.TableName, ct))
                {
                    skipped++;
                    summary.Add(new { table = t.Key, status = "Skipped", reason = "table not created yet" });
                    continue;
                }

                long lo = states.GetValueOrDefault(t.Key, 0);
                long hi = await repo.GetSafeMaxLoadIdAsync(job.SourceServerId, job.SourceDatabase, t.SchemaName, t.TableName);
                if (hi < lo) hi = lo;
                if (hi == lo && !job.UploadWhenEmpty)
                {
                    skipped++;
                    summary.Add(new { table = t.Key, status = "Skipped", reason = "no new loads", loadIdFrom = lo, loadIdTo = hi });
                    continue;
                }

                await using var cmd = await BuildCommandAsync(conn, job, t.SchemaName, t.TableName, lo, hi, local, nowUtc, ct);
                var data = await WriteAndUploadAsync(cmd, job, Path.Combine(workDir, ObjectNameTemplate.Sanitize(t.Key)),
                    ObjectNameTemplate.Sanitize(t.TableName), t.SchemaName, t.TableName, local, ct);

                // Per-table watermark is committed right away: a failure on a later table does not re-export this one
                await repo.SaveExportTableStateAsync(job.ExportJobId, t.SchemaName, t.TableName, hi);

                ok++;
                totalRows += data.Rows; totalBytes += data.Bytes; totalFiles += data.Parts.Count;
                privacyWarnings.AddRange(data.PrivacyWarnings);
                objectNames.AddRange(data.Parts.Select(p => $"gs://{job.Bucket}/{p.ObjectName}"));
                summary.Add(new
                {
                    table = t.Key, status = "Succeeded", rows = data.Rows, loadIdFrom = lo, loadIdTo = hi, columns = data.Columns,
                    masked = data.Masked,
                    files = data.Parts.Select(p => new { objectName = p.ObjectName, rows = p.Rows, bytes = p.Bytes })
                });
                logger.LogInformation("Export {Name} / {Table}: {Rows:N0} rows, {Files} file(s) (LoadId {Lo}..{Hi}], read+CSV {Write:N2} s, upload {Upload:N2} s",
                    job.Name, t.Key, data.Rows, data.Parts.Count, lo, hi, data.WriteSec, data.UploadSec);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                failed++;
                errors.AppendLine($"{t.Key}: {ex.Message}");
                summary.Add(new { table = t.Key, status = "Failed", error = ex.Message });
                logger.LogError(ex, "Export {Name} / {Table} failed; continuing with the next table.", job.Name, t.Key);
            }
        }

        if (job.WriteManifest && (ok > 0 || failed > 0))
        {
            try
            {
                await UploadManifestAsync(job, runId, local, nowUtc,
                    new { tables = summary, totalRows, totalFiles, succeeded = ok, failed, skipped },
                    ObjectNameTemplate.CommonPrefix(job.ObjectNameTemplate, job.Name, job.SourceDatabase, local), ct);
            }
            catch (Exception ex) { errors.AppendLine($"manifest: {ex.Message}"); failed++; }
        }

        var status = failed == 0 ? (ok == 0 ? "Skipped" : "Succeeded") : (ok == 0 ? "Failed" : "PartiallyFailed");
        var head = $"{tables.Count} table(s): {ok} exported, {skipped} skipped, {failed} failed";
        await repo.CompleteExportRunAsync(runId, job.ExportJobId, status, totalRows, totalFiles, totalBytes, null, null,
            head + Environment.NewLine + string.Join(Environment.NewLine, objectNames),
            advanceStateTo: null,
            error: failed > 0 || privacyWarnings.Count > 0 ? (errors + string.Join(Environment.NewLine, privacyWarnings)).Trim() : null);

        logger.LogInformation("Export {Name}: {Head}; {Rows:N0} rows, {Files} file(s), {Bytes:N0} bytes, {Sec:N1} s",
            job.Name, head, totalRows, totalFiles, totalBytes, sw.Elapsed.TotalSeconds);

        if (failed > 0) throw new PartialExportException($"Export {job.Name}: {head}.{Environment.NewLine}{errors}");
    }

    // ------------------------------------------------------------------ helpers

    /// <summary>Removes local day folders older than LocalFileRetentionDays (runs on the machine that holds the files).</summary>
    private void CleanupOldDayFolders()
    {
        var o = options.Value;
        if (!Directory.Exists(o.WorkDirectory)) return;
        var cutoff = DateTime.Today.AddDays(-(o.KeepLocalFiles ? Math.Max(1, o.LocalFileRetentionDays) : 1));
        foreach (var dir in Directory.EnumerateDirectories(o.WorkDirectory))
        {
            if (!DateTime.TryParseExact(Path.GetFileName(dir), "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var day)) continue;
            if (day >= cutoff) continue;
            try { Directory.Delete(dir, recursive: true); }
            catch (Exception ex) { logger.LogWarning(ex, "Could not delete old export folder {Dir}", dir); }
        }
    }

    private async Task<Dataset> WriteAndUploadAsync(SqlCommand cmd, ExportJobDef job, string dir, string baseName,
        string? schema, string? table, DateTime local, CancellationToken ct)
    {
        await using var reader = await cmd.ExecuteReaderAsync(CommandBehavior.SequentialAccess, ct);
        while (reader.FieldCount == 0 && await reader.NextResultAsync(ct)) { }
        if (reader.FieldCount == 0) throw new InvalidOperationException("The export query returned no result set.");

        var format = new CsvFormat(job.Delimiter, job.QuoteMode, job.NullValue,
            job.LineEnding == "CRLF" ? "\r\n" : "\n", job.Utf8Bom, job.Gzip);

        // data protection: masking rules for exported files, and the guard for likely personal data nobody masks
        var policy = await masking.GetPolicyAsync();
        var objectName = table is null ? $"{job.SourceDatabase}.(query)" : $"{job.SourceDatabase}.{schema}.{table}";
        var names = Enumerable.Range(0, reader.FieldCount).Select(reader.GetName).ToList();
        var masks = policy.ForExport(objectName, names);
        var sampler = policy.Guard == Privacy.PrivacyGuardMode.Off ? null
            : new Privacy.PiiSampler(names, masks.Select((m, i) => m is not null || SqlIdent.IsTechnical(names[i])).ToList(),
                Enumerable.Range(0, reader.FieldCount).Select(i => reader.GetFieldType(i) == typeof(string)).ToList());

        List<CsvPart> parts;
        IReadOnlyList<string> columns;
        var swWrite = Stopwatch.StartNew();
        bool parquet = job.FileFormat == ExportFileFormat.Parquet;
        await using (IExportPartWriter writer = parquet
            ? new ParquetPartWriter(dir, baseName, ParquetCompression(job.ParquetCompression), job.MaxRowsPerFile, options.Value.ParquetRowGroupSize, reader, masks, sampler)
            : new CsvPartWriter(dir, baseName, format, job.MaxRowsPerFile, reader, masks, sampler))
        {
            while (await reader.ReadAsync(ct)) await writer.WriteRowAsync(reader, ct);
            if (writer.TotalRows == 0 && job.UploadWhenEmpty) await writer.EnsureAtLeastOnePartAsync(ct);
            await writer.CompleteAsync(ct);
            parts = writer.Parts;
            columns = writer.Header;
        }
        var writeSec = swWrite.Elapsed.TotalSeconds;

        var warnings = new List<string>();
        if (sampler?.Findings() is { Count: > 0 } findings)
        {
            var list = string.Join(", ", findings.Select(f => $"{f.Column} ({Privacy.PiiDetector.Describe(f.Kind)}, {f.Ratio:P0} of sampled values)"));
            if (policy.Guard == Privacy.PrivacyGuardMode.Block)
            {
                // nothing was uploaded yet: remove the local files too
                foreach (var p in parts) try { File.Delete(p.LocalPath); } catch (IOException) { }
                throw new InvalidOperationException(
                    $"Export blocked by the data protection guard: likely personal data without a masking rule in {objectName}: {list}. " +
                    "Add a masking rule (Admin → Data protection) or change the guard mode.");
            }
            warnings.Add($"Data protection warning – likely personal data without a masking rule in {objectName}: {list}.");
            logger.LogWarning("Export {Name}: likely personal data without a masking rule in {Object}: {List}", job.Name, objectName, list);
        }

        var swUpload = Stopwatch.StartNew();
        foreach (var part in parts)
        {
            var name = ObjectNameTemplate.Render(job.ObjectNameTemplate, job.Name, job.SourceDatabase, schema, table, local, part.Number.ToString("0000"));
            part.ObjectName = ObjectNameTemplate.WithExtension(name, job);
            var contentType = parquet ? "application/vnd.apache.parquet" : job.Gzip ? "application/gzip" : "text/csv; charset=utf-8";
            await uploader.UploadFileAsync(job.Bucket, part.ObjectName, part.LocalPath, contentType, ct);
        }
        var uploadSec = swUpload.Elapsed.TotalSeconds;

        // Local CSV archive is always gzip-compressed, independent of what was uploaded (plain CSV by default).
        // Compressed only after a successful upload so the upload is never delayed. Parquet is already compressed.
        if (options.Value.KeepLocalFiles && !parquet)
            foreach (var part in parts.Where(p => !p.LocalPath.EndsWith(".gz", StringComparison.OrdinalIgnoreCase)))
                await GzipInPlaceAsync(part.LocalPath, ct);

        return new Dataset(parts, columns, writeSec, uploadSec, masks.Where(m => m is not null).Select(m => m!.Describe()).ToList(), warnings);
    }

    private static Parquet.CompressionMethod ParquetCompression(string? name) => name?.ToLowerInvariant() switch
    {
        "zstd" => Parquet.CompressionMethod.Zstd,
        "gzip" => Parquet.CompressionMethod.Gzip,
        "none" => Parquet.CompressionMethod.None,
        _ => Parquet.CompressionMethod.Snappy
    };

    /// <summary>Compresses file.csv to file.csv.gz (via a temp file) and deletes the original.</summary>
    private static async Task GzipInPlaceAsync(string path, CancellationToken ct)
    {
        var target = path + ".gz";
        var temp = target + ".tmp";
        await using (var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 20, useAsync: true))
        await using (var output = new FileStream(temp, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 20, useAsync: true))
        await using (var gz = new System.IO.Compression.GZipStream(output, System.IO.Compression.CompressionLevel.Optimal))
            await input.CopyToAsync(gz, 1 << 20, ct);
        File.Move(temp, target, overwrite: true);
        File.Delete(path);
    }

    private async Task UploadManifestAsync(ExportJobDef job, long runId, DateTime local, DateTime nowUtc, object body, string prefix, CancellationToken ct)
    {
        var inv = CultureInfo.InvariantCulture;
        var name = $"{prefix}_manifests/{ObjectNameTemplate.Sanitize(job.Name)}_{local.ToString("yyyyMMdd_HHmmss", inv)}.json";
        var manifest = new
        {
            job = job.Name, exportJobId = job.ExportJobId, exportRunId = runId, mode = job.ExportMode.ToString(),
            database = job.SourceDatabase, exportedAtUtc = nowUtc, runDateLocal = local.ToString("yyyy-MM-dd", inv),
            uploadedGzip = job.Gzip, delimiter = job.Delimiter, content = body
        };
        await uploader.UploadTextAsync(job.Bucket, name,
            JsonSerializer.Serialize(manifest, new JsonSerializerOptions { WriteIndented = true }), "application/json", ct);
    }

    private static async Task<SqlCommand> BuildCommandAsync(SqlConnection conn, ExportJobDef job, string schema, string? table,
        long? lo, long? hi, DateTime local, DateTime nowUtc, CancellationToken ct)
    {
        var cmd = conn.CreateCommand();
        cmd.CommandTimeout = job.CommandTimeoutSec;

        if (job.ExportMode == ExportMode.Query)
        {
            cmd.CommandText = job.SourceQuery!;
            cmd.Parameters.Add(new SqlParameter("@RunDate", SqlDbType.Date) { Value = local.Date });
            cmd.Parameters.Add(new SqlParameter("@RunAtUtc", SqlDbType.DateTime2) { Value = nowUtc });
            return cmd;
        }

        // AllTables ignores ColumnList (it applies to a single table)
        var cols = job.ExportMode != ExportMode.AllTables && !string.IsNullOrWhiteSpace(job.ColumnList)
            ? job.ColumnList
            : job.IncludeTechnicalColumns ? "*" : await DataColumnListAsync(conn, schema, table!, ct);

        cmd.CommandText = $"SELECT {cols} FROM {SqlIdent.Quote(schema, table!)}";
        if (lo is not null && hi is not null)
        {
            cmd.CommandText += $" WHERE {SqlIdent.Quote(SqlIdent.LoadIdColumn)} > @lo AND {SqlIdent.Quote(SqlIdent.LoadIdColumn)} <= @hi";
            cmd.Parameters.Add(new SqlParameter("@lo", SqlDbType.BigInt) { Value = lo.Value });
            cmd.Parameters.Add(new SqlParameter("@hi", SqlDbType.BigInt) { Value = hi.Value });
        }
        return cmd;
    }

    private static async Task<string> DataColumnListAsync(SqlConnection conn, string schema, string table, CancellationToken ct)
    {
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT name FROM sys.columns WHERE object_id = OBJECT_ID(@n, N'U') ORDER BY column_id;";
        cmd.Parameters.AddWithValue("@n", SqlIdent.Quote(schema, table));
        var cols = new List<string>();
        await using (var r = await cmd.ExecuteReaderAsync(ct))
            while (await r.ReadAsync(ct))
            {
                var c = r.GetString(0);
                if (!SqlIdent.IsTechnical(c)) cols.Add(SqlIdent.Quote(c));
            }
        if (cols.Count == 0) throw new InvalidOperationException($"Export source table not found or has no columns: {schema}.{table}");
        return string.Join(", ", cols);
    }
}
