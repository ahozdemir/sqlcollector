using SqlDataCollector.Models;
using SqlDataCollector.Services;

namespace SqlDataCollector.Tests.Integration;

/// <summary>Records dependency-triggered launches; tests assert on them instead of a Hangfire queue.</summary>
public sealed class RecordingLauncher : IDependentJobLauncher
{
    private readonly List<(JobRef Job, string Name, string TriggeredBy)> _launched = [];

    public void Launch(JobRef job, string jobName, string triggeredBy)
    {
        lock (_launched) _launched.Add((job, jobName, triggeredBy));
    }

    public IReadOnlyList<(JobRef Job, string Name, string TriggeredBy)> For(JobRef job)
    {
        lock (_launched) return _launched.Where(l => l.Job == job).ToList();
    }
}
