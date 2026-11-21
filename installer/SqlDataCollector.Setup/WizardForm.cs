using System.Diagnostics;

namespace SqlDataCollector.Setup;

/// <summary>The installation wizard: mode → service account → databases → web/SSL → summary → progress → finish.</summary>
internal sealed class WizardForm : Form
{
    private readonly SetupOptions _o = new();
    private readonly string? _payload;
    private readonly bool _dryRun;
    private readonly string _logFile;
    private readonly List<Panel> _pages = [];
    private int _page;

    private readonly Label _title = new() { Dock = DockStyle.Top, Height = 44, Font = new Font("Segoe UI", 13, FontStyle.Bold), Padding = new Padding(16, 12, 0, 0) };
    private readonly Panel _body = new() { Dock = DockStyle.Fill, Padding = new Padding(20, 8, 20, 8) };
    private readonly Button _back = new() { Text = "< Back", Width = 90 };
    private readonly Button _next = new() { Text = "Next >", Width = 90 };
    private readonly Button _cancel = new() { Text = "Cancel", Width = 90 };

    // page controls
    private readonly RadioButton _embedded = new() { Text = "Embedded – one server: web interface, job execution and Hangfire in the metadata database", AutoSize = true, Checked = true };
    private readonly RadioButton _central = new() { Text = "Central – the organization's central Hangfire database (web and workers on separate servers possible)", AutoSize = true };
    private readonly CheckBox _roleWeb = new() { Text = "Web (user interface)", Checked = true, AutoSize = true };
    private readonly CheckBox _roleWorker = new() { Text = "Worker (executes jobs)", Checked = true, AutoSize = true };
    private readonly TextBox _account = new() { Width = 320, PlaceholderText = @"DOMAIN\svc-sqldatacollector" };
    private readonly TextBox _password = new() { Width = 320, UseSystemPasswordChar = true };
    private readonly CheckBox _gmsa = new() { Text = "Group managed service account (gMSA, no password)", AutoSize = true };
    private readonly TextBox _sqlServer = new() { Width = 320, Text = "localhost" };
    private readonly TextBox _metaDb = new() { Width = 320, Text = "SqlDataCollectorMeta" };
    private readonly TextBox _dwhDb = new() { Width = 320, Text = "DWH" };
    private readonly CheckBox _trust = new() { Text = "SQL Server uses a self-signed certificate (TrustServerCertificate)", Checked = true, AutoSize = true };
    private readonly RadioButton _dbNow = new() { Text = "Create the databases and permissions now (with my Windows account)", AutoSize = true, Checked = true };
    private readonly RadioButton _dbLater = new() { Text = "Later – only write the SQL scripts (for the DBA)", AutoSize = true };
    private readonly TextBox _hfServer = new() { Width = 320 };
    private readonly TextBox _hfDb = new() { Width = 320, Text = "HangfireDB" };
    private readonly TextBox _hfSchema = new() { Width = 320, Text = "hangfire_collector" };
    private readonly TextBox _secretKey = new() { Width = 320, PlaceholderText = "empty = new key (first server)" };
    private readonly TextBox _host = new() { Width = 320 };
    private readonly NumericUpDown _port = new() { Width = 100, Minimum = 1, Maximum = 65535, Value = 443 };
    private readonly CheckBox _firewall = new() { Text = "Open the port in the Windows firewall", Checked = true, AutoSize = true };
    private readonly TextBox _installDir = new() { Width = 420 };
    private readonly TextBox _dataDir = new() { Width = 420 };
    private readonly TextBox _summary = new() { Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Vertical, Dock = DockStyle.Fill, Font = new Font("Consolas", 9) };
    private readonly TextBox _progress = new() { Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Vertical, Dock = DockStyle.Fill, Font = new Font("Consolas", 9) };
    private readonly Label _finish = new() { Dock = DockStyle.Top, Height = 160 };
    private readonly CheckBox _openBrowser = new() { Text = "Open SqlDataCollector now", Checked = true, AutoSize = true, Top = 170 };
    private InstallResult? _result;

    public WizardForm(string? payload, bool dryRun, string logFile)
    {
        _payload = payload; _dryRun = dryRun; _logFile = logFile;
        Text = "SqlDataCollector setup" + (dryRun ? " (dry run)" : "");
        Size = new Size(820, 600);
        StartPosition = FormStartPosition.CenterScreen;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        Font = new Font("Segoe UI", 9);

        _host.Text = _o.HostName;
        _installDir.Text = _o.InstallDir;
        _dataDir.Text = _o.DataDir;

        var buttons = new FlowLayoutPanel { Dock = DockStyle.Bottom, Height = 48, FlowDirection = FlowDirection.RightToLeft, Padding = new Padding(10) };
        buttons.Controls.AddRange([_cancel, _next, _back]);
        Controls.Add(_body);
        Controls.Add(buttons);
        Controls.Add(_title);

        _pages.Add(Page("Installation mode",
            Note("SqlDataCollector collects data from SQL Server sources into a DWH database and exports it to Google Cloud Storage."),
            _embedded, _central, Indent(_roleWeb), Indent(_roleWorker),
            Note("Requirements: a Windows server joined to the Active Directory domain and a SQL Server. Everything else is included.")));
        _pages.Add(Page("Service account",
            Note("The Windows service runs as this domain account. It connects to SQL Server with Windows authentication (no SQL passwords)."),
            Row("Account", _account), Row("Password", _password), _gmsa,
            Note("The account gets \"Log on as a service\" on this computer. Grant it read access on the source databases later.")));
        _pages.Add(Page("Databases",
            Row("SQL Server", _sqlServer), Row("Metadata database", _metaDb), Row("DWH database", _dwhDb), _trust,
            _dbNow, _dbLater,
            Note("Central Hangfire (central mode only; its schema is installed by the DBA with the generated script):"),
            Row("Hangfire server", _hfServer), Row("Hangfire database", _hfDb), Row("Hangfire schema", _hfSchema), Row("Secret key", _secretKey)));
        _pages.Add(Page("Web address and HTTPS",
            Row("Host name", _host), Row("HTTPS port", _port), _firewall,
            Note("A self-signed certificate is created. Import your corporate certificate later in the application (Settings → HTTPS certificate)."),
            Row("Program folder", _installDir), Row("Data folder", _dataDir),
            Note("The data folder holds the HTTPS certificate and the temporary CSV files of exports.")));
        _pages.Add(Page("Summary", _summary));
        _pages.Add(Page("Installing", _progress));
        var finish = Page("Finished", _finish);
        finish.Controls.Add(_openBrowser);
        _pages.Add(finish);

        _central.CheckedChanged += (_, _) => UpdateEnabled();
        _gmsa.CheckedChanged += (_, _) => UpdateEnabled();
        _back.Click += (_, _) => Show(_page - 1);
        _next.Click += async (_, _) => await NextAsync();
        _cancel.Click += (_, _) => Close();
        UpdateEnabled();
        Show(0);
    }

    private void UpdateEnabled()
    {
        _roleWeb.Enabled = _roleWorker.Enabled = _central.Checked;
        _hfServer.Enabled = _hfDb.Enabled = _hfSchema.Enabled = _secretKey.Enabled = _central.Checked;
        _password.Enabled = !_gmsa.Checked;
    }

    private void Show(int page)
    {
        _page = Math.Clamp(page, 0, _pages.Count - 1);
        _body.Controls.Clear();
        _body.Controls.Add(_pages[_page]);
        _title.Text = _pages[_page].Tag as string;
        _back.Enabled = _page is > 0 and < 5;
        _next.Text = _page switch { 4 => "Install", 6 => "Finish", _ => "Next >" };
        _next.Enabled = _page != 5;
        _cancel.Enabled = _page < 5;
    }

    private async Task NextAsync()
    {
        if (_page == 6) { Finish(); return; }
        if (_page < 4)
        {
            Collect();
            var problems = _o.Validate().Where(p => PageOf(p) <= _page).ToList();
            if (problems.Count > 0) { MessageBox.Show(string.Join(Environment.NewLine, problems), Text, MessageBoxButtons.OK, MessageBoxIcon.Warning); return; }
            if (_page == 3) _summary.Text = Summary();
            Show(_page + 1);
            return;
        }
        // install
        Show(5);
        var engine = new Engine(_o, line => BeginInvoke(() =>
        {
            _progress.AppendText(line + Environment.NewLine);
            try { File.AppendAllText(_logFile, line + Environment.NewLine); } catch (IOException) { }
        }), _dryRun, _payload);
        try
        {
            _result = await Task.Run(() => engine.InstallAsync(CancellationToken.None));
            _finish.Text = (_dryRun ? "Dry run finished – nothing was changed.\n\n" : "SqlDataCollector is installed.\n\n")
                + $"Address: {_result.Url}\n"
                + (_result.DatabaseReady ? "" : "The database is not ready yet: run the scripts in " + _o.ScriptsDir + " (the application shows a setup page until then).\n")
                + "\nOn the first sign-in on this server choose \"Local administrator\" and set its password; then grant roles to AD users or groups (Permissions).\n"
                + "Upload the Google Cloud Storage key and import your HTTPS certificate on Settings.\n"
                + (_result.Warnings.Count > 0 ? $"\n{_result.Warnings.Count} warning(s): see {_logFile}" : "");
            Show(6);
        }
        catch (Exception ex)
        {
            _progress.AppendText(Environment.NewLine + "✖ " + ex.Message + Environment.NewLine);
            MessageBox.Show(ex.Message + $"\n\nLog: {_logFile}", Text, MessageBoxButtons.OK, MessageBoxIcon.Error);
            _cancel.Enabled = true;
            _back.Enabled = true;
        }
    }

    private void Finish()
    {
        if (_openBrowser.Checked && _result is not null && !_dryRun)
            Process.Start(new ProcessStartInfo($"{_result.LocalUrl}/login") { UseShellExecute = true });
        Close();
    }

    private void Collect()
    {
        _o.Mode = _central.Checked ? "Central" : "Embedded";
        _o.RoleWeb = _roleWeb.Checked; _o.RoleWorker = _roleWorker.Checked;
        _o.ServiceAccount = _account.Text.Trim();
        _o.ManagedServiceAccount = _gmsa.Checked;
        _o.ServicePassword = _gmsa.Checked ? null : _password.Text;
        _o.SqlServer = _sqlServer.Text.Trim(); _o.MetadataDb = _metaDb.Text.Trim(); _o.DwhDb = _dwhDb.Text.Trim();
        _o.TrustServerCertificate = _trust.Checked;
        _o.DatabaseAction = _dbNow.Checked ? "Now" : "Later";
        _o.HangfireServer = _hfServer.Text.Trim(); _o.HangfireDb = _hfDb.Text.Trim(); _o.HangfireSchema = _hfSchema.Text.Trim();
        _o.SecretKey = string.IsNullOrWhiteSpace(_secretKey.Text) ? null : _secretKey.Text.Trim();
        _o.HostName = _host.Text.Trim(); _o.HttpsPort = (int)_port.Value; _o.OpenFirewall = _firewall.Checked;
        _o.InstallDir = _installDir.Text.Trim(); _o.DataDir = _dataDir.Text.Trim();
    }

    /// <summary>On which page a validation message belongs (messages of later pages are shown when that page is left).</summary>
    private static int PageOf(string problem) => problem switch
    {
        _ when problem.StartsWith("Mode", StringComparison.Ordinal) || problem.Contains("role", StringComparison.Ordinal) => 0,
        _ when problem.Contains("account", StringComparison.OrdinalIgnoreCase) || problem.Contains("password", StringComparison.OrdinalIgnoreCase) || problem.Contains("gMSA", StringComparison.Ordinal) => 1,
        _ when problem.Contains("port", StringComparison.OrdinalIgnoreCase) || problem.Contains("host", StringComparison.OrdinalIgnoreCase) || problem.Contains("folder", StringComparison.OrdinalIgnoreCase) => 3,
        _ => 2
    };

    private string Summary() =>
        $"""
        Mode:              {_o.Mode}{(_o.IsCentral ? $" (roles {_o.Roles})" : "")}
        Service account:   {_o.ServiceAccount}{(_o.ManagedServiceAccount ? " (gMSA)" : "")}
        SQL Server:        {_o.SqlServer}   metadata: {_o.MetadataDb}   DWH: {_o.DwhDb}
        Databases:         {(_o.DatabaseAction == "Now" ? "create now" : "later (scripts only)")}
        {(_o.IsCentral ? $"Central Hangfire:  {_o.HangfireServer} / {_o.HangfireDb} / schema {_o.HangfireSchema} (script for the DBA)" : "Hangfire:          embedded in the metadata database")}
        Web:               {(_o.InstallsWeb ? $"{_o.Url} (self-signed certificate{(_o.OpenFirewall ? ", firewall rule" : "")})" : "not on this server")}
        Program folder:    {_o.InstallDir}
        Data folder:       {_o.DataDir}
        Service:           {_o.ServiceName} (automatic, delayed start; restarts on failure)
        """;

    // ------------------------------------------------------------------ layout helpers

    private static Panel Page(string title, params Control[] controls)
    {
        var flow = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.TopDown, WrapContents = false, AutoScroll = true, Tag = title };
        foreach (var c in controls)
        {
            if (c.Dock == DockStyle.Fill) { var p = new Panel { Dock = DockStyle.Fill, Tag = title }; p.Controls.Add(c); return p; }
            c.Margin = new Padding(c.Margin.Left, 4, 0, 4);
            flow.Controls.Add(c);
        }
        return flow;
    }

    private static Control Row(string label, Control input)
    {
        var p = new FlowLayoutPanel { AutoSize = true, FlowDirection = FlowDirection.LeftToRight, WrapContents = false, Margin = Padding.Empty };
        p.Controls.Add(new Label { Text = label, Width = 150, TextAlign = ContentAlignment.MiddleLeft, Height = 24 });
        p.Controls.Add(input);
        return p;
    }

    private static Control Indent(Control c) { c.Margin = new Padding(24, 0, 0, 0); return c; }

    private static Label Note(string text) => new() { Text = text, AutoSize = true, MaximumSize = new Size(740, 0), ForeColor = Color.DimGray };
}
