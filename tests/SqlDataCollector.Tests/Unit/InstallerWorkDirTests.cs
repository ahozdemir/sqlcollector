using SqlDataCollector.Setup;

namespace SqlDataCollector.Tests.Unit;

/// <summary>Setup: the export work folder (Collector:WorkDirectory) can be chosen; empty keeps &lt;data folder&gt;\exports.</summary>
public sealed class InstallerWorkDirTests
{
    private static SetupOptions Valid(string? workDir) => new()
    {
        ServiceAccount = @"CORP\svc-sdc$", ManagedServiceAccount = true, SkipDomainCheck = true,
        DataDir = @"C:\ProgramData\SqlDataCollector", HostName = "sdc.corp.local", WorkDir = workDir
    };

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Empty_UsesTheExportsFolderOfTheDataFolder(string? workDir)
    {
        var o = Valid(workDir);
        Assert.Equal(@"C:\ProgramData\SqlDataCollector\exports", o.ExportDir);
        Assert.False(o.ExportDirOutsideData);
        Assert.Empty(o.Validate());
    }

    [Fact]
    public void ChosenFolder_IsUsed_AndNeedsItsOwnPermission()
    {
        var o = Valid(@" E:\SqlHarbor\work ");
        Assert.Equal(@"E:\SqlHarbor\work", o.ExportDir);
        Assert.True(o.ExportDirOutsideData);
        Assert.Empty(o.Validate());
    }

    [Theory]
    [InlineData(@"C:\ProgramData\SqlDataCollector\exports2", false)]
    [InlineData(@"c:\programdata\sqldatacollector\", false)]
    [InlineData(@"C:\ProgramData\SqlDataCollectorWork", true)]   // a sibling with the same prefix is outside
    [InlineData(@"D:\work", true)]
    public void InsideOrOutsideTheDataFolder(string workDir, bool outside) =>
        Assert.Equal(outside, Valid(workDir).ExportDirOutsideData);

    [Theory]
    [InlineData(@"\\fileserver\exports", "network share")]
    [InlineData("//fileserver/exports", "network share")]
    [InlineData(@"exports", "full path")]
    [InlineData(@"E:exports", "full path")]
    public void NetworkSharesAndRelativePaths_AreRefused(string workDir, string message)
    {
        var problem = Assert.Single(Valid(workDir).Validate());
        Assert.Contains(message, problem);
        Assert.Contains("folder", problem);   // the wizard shows it on the folders page
    }

    [Theory]
    [InlineData("""{ "Collector": { "Roles": "Web,Worker", "WorkDirectory": "E:\\SqlHarbor\\work" } }""", @"E:\SqlHarbor\work")]
    [InlineData("{ // comment\n  \"Collector\": { \"WorkDirectory\": \"D:\\\\x\", }, }", @"D:\x")]
    [InlineData("""{ "Collector": { "WorkDirectory": "" } }""", null)]
    [InlineData("""{ "Collector": { "Roles": "Web" } }""", null)]
    [InlineData("""{ "Collector": { "WorkDirectory": 5 } }""", null)]
    [InlineData("not json", null)]
    public void Upgrade_ReadsTheFolderInUse(string json, string? expected) =>
        Assert.Equal(expected, SetupOptions.WorkDirectoryOf(json));

    [Fact]
    public void SilentJson_SetsTheFolder()
    {
        var path = Path.Combine(Path.GetTempPath(), $"sdc-install-{Guid.NewGuid():N}.json");
        File.WriteAllText(path, """{ "Mode": "Embedded", "ServiceAccount": "CORP\\svc", "WorkDir": "E:\\SqlHarbor\\work" }""");
        try { Assert.Equal(@"E:\SqlHarbor\work", SetupOptions.Load(path).ExportDir); }
        finally { File.Delete(path); }
    }
}
