namespace SqlDataCollector.Infrastructure;

/// <summary>
/// Hangfire queue names. Prefixed so that, on a Hangfire storage shared with other applications,
/// no foreign Hangfire server ever dequeues our jobs (it would fail with "type not found").
/// A process that should execute our jobs must list these queues in BackgroundJobServerOptions.Queues.
/// </summary>
public static class CollectorQueues
{
    public const string Extract = "sdc_extract";
    public const string Export = "sdc_export";
    public const string System = "sdc_system";

    /// <summary>Priority order used when a Worker serves everything.</summary>
    public static readonly string[] All = [Export, Extract, System];
}
