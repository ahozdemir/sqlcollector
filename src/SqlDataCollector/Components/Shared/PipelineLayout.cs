using System.Globalization;
using SqlDataCollector.Models;

namespace SqlDataCollector.Components.Shared;

/// <summary>A job as the pipeline view needs it: last outcome, trigger and (exports) destination.</summary>
public sealed record PipeJob(JobRef Ref, string Name, bool IsActive, bool AfterJobs, string? LastStatus, string? QualityStatus,
    DateTime? LastStartUtc, long? Rows, string? Destination, string? DestinationProvider);

public sealed record PipeNode(string Id, string Label, string FullName, string Sub, string Tone, string? Href, double X, double Y);
public sealed record PipeEdge(string From, string To, string Tone, bool Dashed);
public sealed record PipeColumn(double X, string Title);
public sealed record PipeGraph(IReadOnlyList<PipeNode> Nodes, IReadOnlyList<PipeEdge> Edges, IReadOnlyList<PipeColumn> Columns,
    double Width, double Height, int HiddenJobs);

/// <summary>
/// Lays out jobs that run after other jobs, the jobs they wait for and every export with its destination as columns:
/// a job's column is the length of its longest upstream chain, destinations come last. Rows within a column follow the
/// average row of a node's upstreams (fewer crossing edges). Pure, unit tested.
/// </summary>
public static class PipelineLayout
{
    public const double NodeW = 210, NodeH = 52, GapX = 64, GapY = 18, Pad = 16, Header = 26;
    private const int MaxLabel = 28;

    public static PipeGraph Build(IReadOnlyList<PipeJob> jobs, IReadOnlyList<(JobRef Job, JobRef Upstream)> deps, Func<DateTime, string> time)
    {
        var byRef = jobs.ToDictionary(j => j.Ref);
        var edges = deps.Where(d => byRef.ContainsKey(d.Job) && byRef.ContainsKey(d.Upstream)).Distinct().ToList();
        var included = new HashSet<JobRef>(edges.SelectMany(e => new[] { e.Job, e.Upstream }));
        foreach (var j in jobs.Where(j => j.Ref.Kind == JobRef.Export)) included.Add(j.Ref);

        // column = longest upstream chain (loops cannot be saved; the visited set only guards against bad data)
        var depth = new Dictionary<JobRef, int>();
        int Depth(JobRef r, HashSet<JobRef> path)
        {
            if (depth.TryGetValue(r, out var d)) return d;
            if (!path.Add(r)) return 0;
            var ups = edges.Where(e => e.Job == r).Select(e => e.Upstream).ToList();
            d = ups.Count == 0 ? 0 : ups.Max(u => Depth(u, path)) + 1;
            path.Remove(r);
            return depth[r] = d;
        }
        foreach (var r in included) Depth(r, []);

        var layers = included.GroupBy(r => depth[r]).OrderBy(g => g.Key).Select(g => g.ToList()).ToList();
        var row = new Dictionary<JobRef, int>();
        foreach (var layer in layers)
        {
            var ordered = layer
                .OrderBy(r => edges.Where(e => e.Job == r && row.ContainsKey(e.Upstream)).Select(e => (double)row[e.Upstream]).DefaultIfEmpty(double.MaxValue).Average())
                .ThenBy(r => r.Kind == JobRef.Export ? 1 : 0)
                .ThenBy(r => byRef[r].Name, StringComparer.OrdinalIgnoreCase)
                .ToList();
            for (int i = 0; i < ordered.Count; i++) row[ordered[i]] = i;
            layer.Clear(); layer.AddRange(ordered);
        }

        // waiting: started by other jobs, and one of them has not (yet) succeeded
        var tone = new Dictionary<JobRef, string>();
        var sub = new Dictionary<JobRef, string>();
        foreach (var r in included)
        {
            var j = byRef[r];
            var blocking = j.AfterJobs
                ? edges.Where(e => e.Job == r).Select(e => byRef[e.Upstream]).Where(u => !Succeeded(u)).Select(u => u.Name).ToList()
                : [];
            if (!j.IsActive) { tone[r] = "muted"; sub[r] = "inactive"; }
            else if (j.LastStatus == "Running") { tone[r] = "run"; sub[r] = Describe(j, time); }
            else if (blocking.Count > 0) { tone[r] = "wait"; sub[r] = Shorten("Waiting for " + string.Join(", ", blocking), 34); }
            else { tone[r] = Tone(j); sub[r] = Describe(j, time); }
        }

        var nodes = new List<PipeNode>();
        var columns = new List<PipeColumn>();
        double ColX(int c) => Pad + c * (NodeW + GapX);
        double RowY(int i) => Pad + Header + i * (NodeH + GapY);
        for (int c = 0; c < layers.Count; c++)
        {
            columns.Add(new PipeColumn(ColX(c), c == 0 ? "Starts on its schedule" : "Runs after the jobs on the left"));
            foreach (var r in layers[c])
            {
                var j = byRef[r];
                nodes.Add(new PipeNode(Id(r), Shorten(j.Name, MaxLabel), j.Name, sub[r], tone[r],
                    r.Kind == JobRef.Extract ? $"/extract/{r.Id}" : $"/export/{r.Id}", ColX(c), RowY(row[r])));
            }
        }

        // destinations: one node per destination, next to the exports that upload to it
        var pipeEdges = edges.Select(e => new PipeEdge(Id(e.Upstream), Id(e.Job), tone[e.Upstream], tone[e.Job] == "wait")).ToList();
        var exports = included.Where(r => r.Kind == JobRef.Export && byRef[r].Destination is not null).ToList();
        if (exports.Count > 0)
        {
            int dc = layers.Count;
            columns.Add(new PipeColumn(ColX(dc), "Destinations"));
            var dests = exports.GroupBy(r => byRef[r].Destination!, StringComparer.OrdinalIgnoreCase)
                .Select(g => (Name: g.Key, Provider: byRef[g.First()].DestinationProvider, Avg: g.Average(r => (double)row[r]), Exports: g.ToList()))
                .OrderBy(d => d.Avg).ThenBy(d => d.Name, StringComparer.OrdinalIgnoreCase).ToList();
            double lastY = double.MinValue;
            foreach (var d in dests)
            {
                var y = Math.Max(Pad + Header + d.Avg * (NodeH + GapY), lastY + NodeH + GapY);
                lastY = y;
                var id = "d:" + d.Name;
                nodes.Add(new PipeNode(id, Shorten(d.Name, MaxLabel), d.Name, Provider(d.Provider), "dest", null, ColX(dc), Math.Round(y, 1)));
                pipeEdges.AddRange(d.Exports.Select(r => new PipeEdge(Id(r), id, tone[r], tone[r] == "wait")));
            }
        }

        double width = nodes.Count == 0 ? 0 : nodes.Max(n => n.X) + NodeW + Pad;
        double height = nodes.Count == 0 ? 0 : nodes.Max(n => n.Y) + NodeH + Pad;
        return new PipeGraph(nodes, pipeEdges, columns, width, height, jobs.Count - included.Count);
    }

    public static string Id(JobRef r) => $"{(r.Kind == JobRef.Extract ? "x" : "e")}:{r.Id}";

    private static bool Succeeded(PipeJob j) => j.LastStatus is "Succeeded" or "Skipped" && j.QualityStatus is not "Failed";

    private static string Tone(PipeJob j) => j.LastStatus switch
    {
        null => "muted",
        "Failed" or "CleanupPending" or "Abandoned" => "err",
        "PartiallyFailed" => "warn",
        _ => j.QualityStatus is "Failed" ? "err" : j.QualityStatus is "Warning" ? "warn" : "ok"
    };

    private static string Describe(PipeJob j, Func<DateTime, string> time)
    {
        if (j.LastStatus is null) return "never run";
        var status = j.QualityStatus is "Failed" or "Warning" && j.LastStatus == "Succeeded" ? $"Quality {j.QualityStatus.ToLowerInvariant()}" : j.LastStatus;
        var parts = new List<string> { status };
        if (j.LastStartUtc is { } s) parts.Add(time(s));
        if (j.Rows is { } r and > 0) parts.Add(Rows(r));
        return Shorten(string.Join(" · ", parts), 34);
    }

    private static string Rows(long r) => r >= 1_000_000 ? (r / 1_000_000d).ToString("0.#", CultureInfo.InvariantCulture) + " M rows"
        : r >= 10_000 ? (r / 1_000d).ToString("0", CultureInfo.InvariantCulture) + "k rows" : r.ToString("N0", CultureInfo.InvariantCulture) + " rows";

    private static string Provider(string? p) => p switch
    {
        StorageProviders.S3 => "Amazon S3 / compatible",
        StorageProviders.AzureBlob => "Azure Blob Storage",
        _ => "Google Cloud Storage"
    };

    public static string Shorten(string s, int max) => s.Length <= max ? s : s[..(max - 1)] + "…";
}
