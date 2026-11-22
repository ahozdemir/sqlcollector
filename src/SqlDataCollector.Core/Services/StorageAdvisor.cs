using System.Globalization;
using SqlDataCollector.Models;

namespace SqlDataCollector.Services;

/// <summary>A storage suggestion with the estimated size of the target table and a reason for the UI.</summary>
public sealed record StorageAdvice(TargetStorage Storage, long? EstimatedRows, string Reason);

/// <summary>
/// Suggests the target storage of a new table from the size it will reach – not only from the source today:
/// Append keeps one copy per run for the retention period, the other modes hold about one copy of the source.
/// A growth headroom keeps tables just below the threshold from outgrowing their layout soon after they are created.
/// </summary>
public static class StorageAdvisor
{
    /// <summary>From this many rows in the target a columnstore loads clearly faster and is much smaller (measured: 4× / 12×).</summary>
    public const long ColumnstoreThreshold = 1_000_000;
    /// <summary>
    /// Merge rewrites changed rows, and an update in a columnstore is a delete plus an insert (measured: about 2× slower than a
    /// PAGE compressed rowstore, 4× slower than an uncompressed one). Only from this size do the smaller table and faster scans outweigh it.
    /// </summary>
    public const long MergeColumnstoreThreshold = 10_000_000;
    /// <summary>Growth headroom: a table of 800,000 rows already gets a columnstore (800,000 × 1.25 = 1,000,000).</summary>
    public const double GrowthBuffer = 1.25;
    /// <summary>Planning horizon of an Append job that keeps its loads forever.</summary>
    public const int KeepForeverDays = 365;

    public static StorageAdvice Suggest(long? sourceRows, LoadMode mode, int? retentionDays, double runsPerDay,
        IReadOnlyCollection<string>? unsupportedColumns = null)
    {
        if (unsupportedColumns is { Count: > 0 })
            return new(TargetStorage.Rowstore, null,
                $"Rowstore: a columnstore cannot hold the xml / sql_variant column(s) {string.Join(", ", unsupportedColumns)}.");
        if (sourceRows is not { } rows || rows < 0)
            return new(TargetStorage.Rowstore, null, "Rowstore: the size of the source is unknown (a view or a query) – choose Columnstore yourself for large data.");

        double estimate;
        string detail;
        if (mode == LoadMode.Append)
        {
            var perDay = Math.Max(runsPerDay, 1.0 / 7);   // at least one load a week
            var days = retentionDays is > 0 ? retentionDays.Value : KeepForeverDays;
            var copies = Math.Max(1, perDay * days);
            estimate = rows * copies;
            detail = $"{Format(rows)} source rows × {Format(perDay)} load(s) a day × {days} days kept{(retentionDays is > 0 ? "" : " (kept forever: one year planned)")}";
        }
        else
        {
            estimate = rows;
            detail = mode == LoadMode.Incremental
                ? $"at least the {Format(rows)} source rows, growing with every change"
                : $"one copy of the {Format(rows)} source rows";
        }

        var est = (long)Math.Min(estimate, long.MaxValue / 2);
        if (mode == LoadMode.Merge)
            return estimate * GrowthBuffer >= MergeColumnstoreThreshold
                ? new(TargetStorage.Columnstore, est,
                    $"Columnstore: the target will hold about {Format(est)} rows ({detail}); with 25 % growth headroom that is over {Format(MergeColumnstoreThreshold)}, " +
                    "where the smaller table and faster scans outweigh the slower updates of Merge.")
                : new(TargetStorage.Rowstore, est,
                    $"Rowstore: Merge updates changed rows, and updates are about 2× slower in a columnstore; it pays off only from {Format(MergeColumnstoreThreshold)} rows " +
                    $"(this target: about {Format(est)}, {detail}).");
        return estimate * GrowthBuffer >= ColumnstoreThreshold
            ? new(TargetStorage.Columnstore, est,
                $"Columnstore: the target will hold about {Format(est)} rows ({detail}); with 25 % growth headroom that is over {Format(ColumnstoreThreshold)}.")
            : new(TargetStorage.Rowstore, est,
                $"Rowstore: the target will hold about {Format(est)} rows ({detail}); even with 25 % growth headroom that is under {Format(ColumnstoreThreshold)}.");
    }

    /// <summary>Runs per day of a schedule (counted over the next week); jobs started by other jobs count as one run a day.</summary>
    public static double RunsPerDay(JobTriggerMode mode, string? cron, string? timeZoneId)
    {
        if (mode == JobTriggerMode.AfterJobs || string.IsNullOrWhiteSpace(cron)) return 1;
        const int cap = 7 * 288;   // every 5 minutes for a week is plenty for an estimate
        var (ok, _, next) = JobRegistrar.Preview(cron, timeZoneId ?? "UTC", cap);
        if (!ok || next.Count == 0) return 1;
        var until = next[0].AddDays(7);
        return Math.Max(next.Count(n => n < until), 1) / 7.0;
    }

    /// <summary>"950,000", "13.5 M", "0.14" (runs per day).</summary>
    public static string Format(double value) => value switch
    {
        >= 1_000_000 => (value / 1_000_000).ToString("0.#", CultureInfo.InvariantCulture) + " M",
        >= 10 => value.ToString("N0", CultureInfo.InvariantCulture),
        _ => value.ToString(value % 1 == 0 ? "0" : "0.##", CultureInfo.InvariantCulture)
    };

    /// <summary>Column names of a column list whose type a columnstore cannot hold.</summary>
    public static IReadOnlyList<string> UnsupportedColumns(IEnumerable<(string Name, string TypeDeclaration)> columns) =>
        columns.Where(c => c.TypeDeclaration.StartsWith("xml", StringComparison.OrdinalIgnoreCase)
                        || c.TypeDeclaration.StartsWith("sql_variant", StringComparison.OrdinalIgnoreCase))
               .Select(c => c.Name).ToList();
}
