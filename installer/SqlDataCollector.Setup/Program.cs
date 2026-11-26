using System.Text;

namespace SqlDataCollector.Setup;

/// <summary>
/// SqlHarbor-Setup.exe
///   (no arguments)                         installation wizard
///   /silent /config install.json [/password-env NAME] [/log file] [/dryrun] [/payload dir|zip]
///   /uninstall [/silent] [/purge]          remove the service, firewall rule and program files (data and databases are kept unless /purge)
/// Exit code 0 = success.
/// </summary>
internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        string? Arg(string name) { var i = Array.FindIndex(args, a => a.Equals("/" + name, StringComparison.OrdinalIgnoreCase)); return i >= 0 && i + 1 < args.Length ? args[i + 1] : null; }
        bool Flag(string name) => args.Any(a => a.Equals("/" + name, StringComparison.OrdinalIgnoreCase));

        var silent = Flag("silent");
        var logFile = Arg("log") ?? Path.Combine(Path.GetTempPath(), $"SqlHarbor-Setup-{DateTime.Now:yyyyMMdd-HHmmss}.log");

        if (Flag("uninstall"))
        {
            var o = UninstallOptions();
            if (!silent)
            {
                ApplicationConfiguration.Initialize();
                if (MessageBox.Show($"Remove SqlHarbor (service {o.ServiceName}, {o.AppDir})?\nThe databases and {o.DataDir} are kept.",
                        "SqlHarbor", MessageBoxButtons.OKCancel, MessageBoxIcon.Question) != DialogResult.OK) return 1;
            }
            return Run(logFile, silent, log => new Engine(o, log, Flag("dryrun")).UninstallAsync(Flag("purge"), CancellationToken.None));
        }

        if (silent)
        {
            var configPath = Arg("config");
            if (configPath is null) { File.WriteAllText(logFile, "/silent needs /config <file.json>"); return 2; }
            SetupOptions o;
            try { o = SetupOptions.Load(configPath); }
            catch (Exception ex) { File.WriteAllText(logFile, "Invalid configuration: " + ex.Message); return 2; }
            if (Arg("password-env") is { } env) o.ServicePassword = Environment.GetEnvironmentVariable(env);
            return Run(logFile, true, log => new Engine(o, log, Flag("dryrun"), Arg("payload")).InstallAsync(CancellationToken.None));
        }

        ApplicationConfiguration.Initialize();
        Application.Run(new WizardForm(Arg("payload"), Flag("dryrun"), logFile));
        return 0;
    }

    private static int Run(string logFile, bool silent, Func<Action<string>, Task> work)
    {
        var sb = new StringBuilder();
        void Log(string line) { lock (sb) { sb.AppendLine(line); File.AppendAllText(logFile, line + Environment.NewLine); } }
        try
        {
            work(Log).GetAwaiter().GetResult();
            return 0;
        }
        catch (Exception ex)
        {
            Log("✖ " + ex.Message);
            if (!silent) MessageBox.Show(ex.Message, "SqlHarbor setup", MessageBoxButtons.OK, MessageBoxIcon.Error);
            return 1;
        }
    }

    /// <summary>Paths of the installed instance from its uninstall registry entry.</summary>
    private static SetupOptions UninstallOptions()
    {
        var o = new SetupOptions();
        using var key = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(Engine.UninstallKey);
        if (key?.GetValue("InstallLocation") is string dir) o.InstallDir = dir;
        if (key?.GetValue("ServiceName") is string svc) o.ServiceName = svc;
        if (key?.GetValue("DataDir") is string data) o.DataDir = data;
        if (key?.GetValue("ExportDir") is string export) o.WorkDir = export;
        return o;
    }
}
