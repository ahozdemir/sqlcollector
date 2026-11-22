using System.Text.RegularExpressions;

namespace SqlDataCollector.Tests.Unit;

/// <summary>The Help page exists in English and Turkish: every "?" link and every table-of-contents entry must find its topic in both.</summary>
public class HelpTopicsTests
{
    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "SqlCollector.sln"))) dir = dir.Parent;
        return dir?.FullName ?? throw new InvalidOperationException("Repository root (SqlCollector.sln) not found.");
    }

    private static string Pages => Path.Combine(RepoRoot(), "src", "SqlDataCollector", "Components");

    private static HashSet<string> Ids(string file) =>
        Regex.Matches(File.ReadAllText(file), "id=\"([a-z0-9-]+)\"").Select(m => m.Groups[1].Value).ToHashSet();

    private static void Missing(string where, IEnumerable<string> missing)
    {
        var list = missing.ToList();
        Assert.True(list.Count == 0, $"Missing in {where}: {string.Join(", ", list)}");
    }

    [Fact]
    public void EveryHelpLinkTopic_ExistsInEnglishAndTurkish()
    {
        var english = Ids(Path.Combine(Pages, "Pages", "Help.razor"));
        var turkish = Ids(Path.Combine(Pages, "Pages", "HelpTr.razor"));
        var topics = Directory.EnumerateFiles(Pages, "*.razor", SearchOption.AllDirectories)
            .SelectMany(f => Regex.Matches(File.ReadAllText(f), "HelpLink Topic=\"([a-z0-9-]+)\"").Select(m => m.Groups[1].Value))
            .ToHashSet();

        Assert.NotEmpty(topics);
        Missing("English Help", topics.Where(t => !english.Contains(t)));
        Missing("Turkish Help", topics.Where(t => !turkish.Contains(t)));
    }

    [Fact]
    public void Guide_HasTheSameScenariosInBothLanguages_ExistingScreenshots_AndValidLinks()
    {
        var pages = Path.Combine(Pages, "Pages");
        var guideEn = File.ReadAllText(Path.Combine(pages, "GuideEn.razor"));
        var guideTr = File.ReadAllText(Path.Combine(pages, "GuideTr.razor"));
        var scenarios = Regex.Matches(File.ReadAllText(Path.Combine(pages, "Guide.razor")), "\\(\"([a-z0-9-]+)\", \"").Select(m => m.Groups[1].Value).ToList();
        var en = Ids(Path.Combine(pages, "GuideEn.razor"));
        var tr = Ids(Path.Combine(pages, "GuideTr.razor"));

        Assert.NotEmpty(scenarios);
        Missing("English guide", scenarios.Where(s => !en.Contains(s)));
        Missing("Turkish guide", scenarios.Where(s => !tr.Contains(s)));
        // the page indexes the card summaries by scenario id: a missing one breaks the whole Guide page (HTTP 500)
        var guidePage = File.ReadAllText(Path.Combine(pages, "Guide.razor"));
        foreach (var (name, dict) in new[] { ("English card summaries", "SummaryEn"), ("Turkish card summaries", "SummaryTr") })
        {
            var block = Regex.Match(guidePage, dict + @"\s*=\s*new\(\)\s*\{(?<body>.*?)\};", RegexOptions.Singleline).Groups["body"].Value;
            var keys = Regex.Matches(block, "\\[\"([a-z0-9-]+)\"\\]").Select(m => m.Groups[1].Value).ToHashSet();
            Assert.NotEmpty(keys);
            Missing(name, scenarios.Where(s => !keys.Contains(s)));
        }

        var wwwroot = Path.Combine(RepoRoot(), "src", "SqlDataCollector", "wwwroot");
        var reference = Ids(Path.Combine(pages, "Help.razor")).Concat(Ids(Path.Combine(pages, "HelpTr.razor"))).ToHashSet();
        foreach (var (name, text) in new[] { ("English guide", guideEn), ("Turkish guide", guideTr) })
        {
            Missing($"{name}: screenshots", Regex.Matches(text, "Src=\"(guide/[^\"]+)\"").Select(m => m.Groups[1].Value)
                .Where(src => !File.Exists(Path.Combine(wwwroot, src.Replace('/', Path.DirectorySeparatorChar)))));
            Missing($"{name}: reference links", Regex.Matches(text, "href=\"/help#([a-z0-9-]+)\"").Select(m => m.Groups[1].Value).Where(id => !reference.Contains(id)));
            Missing($"{name}: scenario links", Regex.Matches(text, "href=\"/guide#([a-z0-9-]+)\"").Select(m => m.Groups[1].Value).Where(id => !scenarios.Contains(id)));
        }
    }

    [Fact]
    public void EveryContentsEntry_PointsToAnExistingSection()
    {
        var help = File.ReadAllText(Path.Combine(Pages, "Pages", "Help.razor"));
        var english = Ids(Path.Combine(Pages, "Pages", "Help.razor"));
        var turkish = Ids(Path.Combine(Pages, "Pages", "HelpTr.razor"));

        string Block(string name) => Regex.Match(help, name + @" =\s*\[(.*?)\];", RegexOptions.Singleline).Groups[1].Value;
        var tocEn = Regex.Matches(Block("Toc"), "\\(\"([a-z0-9-]+)\"").Select(m => m.Groups[1].Value).ToList();
        var tocTr = Regex.Matches(Block("TocTr"), "\\(\"([a-z0-9-]+)\"").Select(m => m.Groups[1].Value).ToList();

        Assert.NotEmpty(tocEn);
        Assert.NotEmpty(tocTr);
        Missing("English sections", tocEn.Where(t => !english.Contains(t)));
        Missing("Turkish sections", tocTr.Where(t => !turkish.Contains(t)));
        Missing("Turkish contents", tocEn.Where(t => !tocTr.Contains(t)));   // every English topic also has a Turkish entry
    }
}
