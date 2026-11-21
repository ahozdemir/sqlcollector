using Cronos;
using Hangfire;
using SqlDataCollector.Export;
using SqlDataCollector.Extract;
using SqlDataCollector.Infrastructure;
using SqlDataCollector.Models;

namespace SqlDataCollector.Services;

/// <summary>
/// Writes job definitions to Hangfire storage as recurring jobs / removes them / enqueues them immediately.
/// The Web role calls this on "Save" and "Run now"; the Hangfire Server in the Worker role
/// reads the same storage and picks the job up (no code is sent, only the method invocation).
/// </summary>
public sealed class JobRegistrar(IRecurringJobManager recurring, IBackgroundJobClient client)
{
    public const string ExtractPrefix = "extract-";
    public const string ExportPrefix = "export-";

    public static string ExtractId(int id) => ExtractPrefix + id;
    public static string ExportId(int id) => ExportPrefix + id;

    public void Register(ExtractJobDef j)
    {
        var id = ExtractId(j.ExtractJobId);
        if (!j.IsActive || j.TriggerMode == JobTriggerMode.AfterJobs) { recurring.RemoveIfExists(id); return; }   // AfterJobs: started by DependencyService
        int jobId = j.ExtractJobId; string name = j.Name;
        recurring.AddOrUpdate<ExtractRunner>(id, CollectorQueues.Extract, r => r.RunAsync(jobId, name, null, CancellationToken.None),
            j.CronExpression, new RecurringJobOptions { TimeZone = TimeZones.Resolve(j.TimeZoneId) });
    }

    public void Register(ExportJobDef e)
    {
        var id = ExportId(e.ExportJobId);
        if (!e.IsActive || e.TriggerMode == JobTriggerMode.AfterJobs) { recurring.RemoveIfExists(id); return; }
        int jobId = e.ExportJobId; string name = e.Name;
        recurring.AddOrUpdate<ExportRunner>(id, CollectorQueues.Export, r => r.RunAsync(jobId, name, null, CancellationToken.None),
            e.CronExpression, new RecurringJobOptions { TimeZone = TimeZones.Resolve(e.TimeZoneId) });
    }

    public void RemoveExtract(int id) => recurring.RemoveIfExists(ExtractId(id));
    public void RemoveExport(int id) => recurring.RemoveIfExists(ExportId(id));

    public string RunExtractNow(ExtractJobDef j, string user)
    {
        int jobId = j.ExtractJobId; string name = j.Name;
        return client.Enqueue<ExtractRunner>(CollectorQueues.Extract, r => r.RunAsync(jobId, name, user, CancellationToken.None));
    }

    public string RunExportNow(ExportJobDef e, string user)
    {
        int jobId = e.ExportJobId; string name = e.Name;
        return client.Enqueue<ExportRunner>(CollectorQueues.Export, r => r.RunAsync(jobId, name, user, CancellationToken.None));
    }

    /// <summary>Validates a cron expression and returns the next run times (in the job's time zone).</summary>
    public static (bool Ok, string? Error, IReadOnlyList<DateTimeOffset> Next) Preview(string cron, string tzId, int count = 5)
    {
        try
        {
            var tz = TimeZones.Resolve(tzId);
            var parts = cron.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries).Length;
            var expr = CronExpression.Parse(cron.Trim(), parts == 6 ? CronFormat.IncludeSeconds : CronFormat.Standard);
            var list = new List<DateTimeOffset>();
            var from = DateTimeOffset.UtcNow;
            for (int i = 0; i < count; i++)
            {
                var next = expr.GetNextOccurrence(from, tz);
                if (next is null) break;
                list.Add(next.Value);
                from = next.Value;
            }
            return (true, null, list);
        }
        catch (Exception ex)
        {
            return (false, ex.Message, []);
        }
    }
}
