using System.Globalization;
using SqlDataCollector.Options;

namespace SqlDataCollector.Export;

/// <summary>
/// The export Worker's local archive of uploaded files: <c>&lt;WorkDirectory&gt;\yyyy-MM-dd\&lt;job&gt;[\&lt;schema.table&gt;]\</c>.
/// Files are moved here only after a successful upload (CSV gzip-compressed). A full export (whole table or query) run again on the
/// same day replaces that day's files; an incremental export adds its new files next to the earlier ones, so nothing is lost.
/// The newest 1–3 day folders are kept (Settings → Local export archive; default 1).
/// </summary>
public static class ExportArchive
{
    public const string DaysKey = "Export.LocalArchiveDays";
    public const int MinDays = 1, MaxDays = 3, DefaultDays = 1;
    public const string DayFormat = "yyyy-MM-dd";

    public static int Clamp(int days) => Math.Clamp(days, MinDays, MaxDays);

    /// <summary>Days to keep: the Settings page value, else Collector:LocalFileRetentionDays; always 1–3.</summary>
    public static int Days(IReadOnlyDictionary<string, string?> settings, CollectorOptions options) =>
        Clamp(settings.TryGetValue(DaysKey, out var v) && int.TryParse(v, NumberStyles.Integer, CultureInfo.InvariantCulture, out var d) ? d : options.LocalFileRetentionDays);

    public static string DayFolder(string root, DateTime localDay) => Path.Combine(root, localDay.ToString(DayFormat, CultureInfo.InvariantCulture));

    /// <summary>
    /// Moves uploaded files into the archive folder. <paramref name="replace"/> (full exports): the folder's earlier files of the
    /// same day are removed first. Otherwise (incremental) the files are added with <paramref name="runTag"/> in front of the name.
    /// </summary>
    public static IReadOnlyList<string> Store(IEnumerable<string> files, string folder, bool replace, string runTag)
    {
        if (replace && Directory.Exists(folder)) Directory.Delete(folder, recursive: true);
        Directory.CreateDirectory(folder);
        var stored = new List<string>();
        foreach (var file in files)
        {
            var target = Path.Combine(folder, replace ? Path.GetFileName(file) : $"{runTag}_{Path.GetFileName(file)}");
            File.Move(file, target, overwrite: true);
            stored.Add(target);
        }
        return stored;
    }

    /// <summary>
    /// Keeps the newest <paramref name="keepDays"/> day folders that hold archived files and deletes older day folders, so at least
    /// one earlier day stays until a newer day has files. Without any archived file, day folders before <paramref name="today"/>
    /// (left-overs of interrupted runs) are removed. Returns the deleted folders; folders that cannot be deleted are skipped.
    /// </summary>
    public static IReadOnlyList<string> Cleanup(string root, int keepDays, DateTime today, Action<string, Exception>? onError = null)
    {
        if (!Directory.Exists(root)) return [];
        var days = Directory.EnumerateDirectories(root)
            .Select(d => (Dir: d, Ok: DateTime.TryParseExact(Path.GetFileName(d), DayFormat, CultureInfo.InvariantCulture, DateTimeStyles.None, out var day), Day: day))
            .Where(x => x.Ok)
            .OrderByDescending(x => x.Day)
            .ToList();
        var keep = days.Where(x => Directory.EnumerateFiles(x.Dir, "*", SearchOption.AllDirectories).Any()).Take(Clamp(keepDays)).ToList();
        var oldestKept = keep.Count > 0 ? keep[^1].Day : today.Date;
        var deleted = new List<string>();
        foreach (var (dir, _, day) in days.Where(x => x.Day < oldestKept))
        {
            try { Directory.Delete(dir, recursive: true); deleted.Add(dir); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { onError?.Invoke(dir, ex); }
        }
        return deleted;
    }
}
