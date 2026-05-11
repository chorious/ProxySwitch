using System.Text.Json;
using System.Text.Json.Serialization;
using ProxySwitch.Models;
using ProxySwitch.Services;

namespace ProxySwitch;

public class SettingsForm : Form
{
    private ProxyConfig _config = new();
    private DataGridView _proxyGrid = null!;
    private DataGridView _appGrid = null!;
    private DataGridView _backendGrid = null!;

    private CheckBox _bkEnabled = null!;
    private ComboBox _bkType = null!;
    private TextBox _bkExe = null!;
    private TextBox _bkConfigPath = null!;
    private TextBox _bkServiceName = null!;
    private CheckBox _bkManageService = null!;
    private CheckBox _bkAutoRestart = null!;

    public SettingsForm()
    {
        Text = "ProxySwitch Settings";
        Size = new Size(820, 560);
        StartPosition = FormStartPosition.CenterScreen;
        FormBorderStyle = FormBorderStyle.Sizable;
        MinimumSize = new Size(680, 440);

        LoadConfig();
        BuildUI();
    }

    private void LoadConfig()
    {
        var path = Path.Combine(MainForm.RootPath, "config", "proxyswitch.json");
        try
        {
            var json = File.ReadAllText(path);
            _config = JsonSerializer.Deserialize<ProxyConfig>(json) ?? new ProxyConfig();
        }
        catch (Exception ex)
        {
            Logger.Error($"Settings load failed: {ex.Message}");
            _config = new ProxyConfig();
        }
    }

    private void BuildUI()
    {
        var tabs = new TabControl
        {
            Dock = DockStyle.Fill,
            Padding = new Point(12, 4)
        };

        // Proxies tab
        var proxyTab = new TabPage("Proxies");
        _proxyGrid = new DataGridView
        {
            Dock = DockStyle.Fill,
            AutoGenerateColumns = false,
            AllowUserToAddRows = true,
            AllowUserToDeleteRows = true,
            SelectionMode = DataGridViewSelectionMode.FullRowSelect,
            ColumnHeadersHeightSizeMode = DataGridViewColumnHeadersHeightSizeMode.AutoSize
        };
        _proxyGrid.Columns.Add(new DataGridViewTextBoxColumn { DataPropertyName = "Id", HeaderText = "ID", Width = 80 });
        _proxyGrid.Columns.Add(new DataGridViewTextBoxColumn { DataPropertyName = "Name", HeaderText = "Name", Width = 160 });
        _proxyGrid.Columns.Add(new DataGridViewTextBoxColumn { DataPropertyName = "Type", HeaderText = "Type", Width = 80 });
        _proxyGrid.Columns.Add(new DataGridViewTextBoxColumn { DataPropertyName = "Host", HeaderText = "Host", Width = 110 });
        _proxyGrid.Columns.Add(new DataGridViewTextBoxColumn { DataPropertyName = "Port", HeaderText = "Port", Width = 60 });
        proxyTab.Controls.Add(_proxyGrid);
        tabs.TabPages.Add(proxyTab);

        // Apps tab
        var appTab = new TabPage("Apps");
        _appGrid = new DataGridView
        {
            Dock = DockStyle.Fill,
            AutoGenerateColumns = false,
            AllowUserToAddRows = true,
            AllowUserToDeleteRows = true,
            SelectionMode = DataGridViewSelectionMode.FullRowSelect
        };
        _appGrid.Columns.Add(new DataGridViewTextBoxColumn { DataPropertyName = "Id", HeaderText = "ID", Width = 100 });
        _appGrid.Columns.Add(new DataGridViewTextBoxColumn { DataPropertyName = "Name", HeaderText = "Name", Width = 140 });
        _appGrid.Columns.Add(new DataGridViewTextBoxColumn { DataPropertyName = "Exe", HeaderText = "Executable Path", Width = 280 });
        _appGrid.Columns.Add(new DataGridViewTextBoxColumn { DataPropertyName = "Mode", HeaderText = "Mode", Width = 100 });
        _appGrid.Columns.Add(new DataGridViewTextBoxColumn { DataPropertyName = "ProxyId", HeaderText = "Proxy ID", Width = 80 });
        _appGrid.Columns.Add(new DataGridViewTextBoxColumn { DataPropertyName = "UserDataDir", HeaderText = "User Data Dir", Width = 200 });
        appTab.Controls.Add(_appGrid);
        tabs.TabPages.Add(appTab);

        // Routing Backends tab (Clash Verge / v2ray etc.)
        var backendTab = new TabPage("Routing Backends");
        _backendGrid = new DataGridView
        {
            Dock = DockStyle.Fill,
            AutoGenerateColumns = false,
            AllowUserToAddRows = true,
            AllowUserToDeleteRows = true,
            SelectionMode = DataGridViewSelectionMode.FullRowSelect
        };
        _backendGrid.Columns.Add(new DataGridViewTextBoxColumn { DataPropertyName = "Id", HeaderText = "ID", Width = 140 });
        _backendGrid.Columns.Add(new DataGridViewTextBoxColumn { DataPropertyName = "Name", HeaderText = "Name", Width = 140 });
        _backendGrid.Columns.Add(new DataGridViewTextBoxColumn { DataPropertyName = "ProxyId", HeaderText = "Proxy ID", Width = 80 });
        _backendGrid.Columns.Add(new DataGridViewTextBoxColumn { DataPropertyName = "AppPath", HeaderText = "App Path", Width = 200 });
        _backendGrid.Columns.Add(new DataGridViewTextBoxColumn { DataPropertyName = "ConfigPath", HeaderText = "Config Path", Width = 200 });
        _backendGrid.Columns.Add(new DataGridViewTextBoxColumn { DataPropertyName = "RuleFormat", HeaderText = "Rule Format", Width = 90 });
        backendTab.Controls.Add(_backendGrid);
        tabs.TabPages.Add(backendTab);

        // Transparent Backend tab (ProxiFyre)
        var transparentTab = new TabPage("Transparent Backend");
        transparentTab.Padding = new Padding(12);

        var tLayout = new TableLayoutPanel
        {
            Dock = DockStyle.Top,
            AutoSize = true,
            ColumnCount = 2,
            RowCount = 7
        };
        tLayout.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 180f));
        tLayout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100f));

        var be = _config.TransparentBackend;

        tLayout.Controls.Add(new Label { Text = "Enabled:", AutoSize = true, Anchor = AnchorStyles.Left }, 0, 0);
        _bkEnabled = new CheckBox { Text = "Use transparent backend", AutoSize = true, Checked = be.Enabled };
        tLayout.Controls.Add(_bkEnabled, 1, 0);

        tLayout.Controls.Add(new Label { Text = "Type:", AutoSize = true, Anchor = AnchorStyles.Left }, 0, 1);
        _bkType = new ComboBox { Dock = DockStyle.Fill, DropDownStyle = ComboBoxStyle.DropDownList };
        _bkType.Items.AddRange(new object[] { "none", "proxifyre" });
        _bkType.SelectedItem = string.IsNullOrEmpty(be.Type) ? "none" : be.Type;
        tLayout.Controls.Add(_bkType, 1, 1);

        tLayout.Controls.Add(new Label { Text = "ProxiFyre exe:", AutoSize = true, Anchor = AnchorStyles.Left }, 0, 2);
        _bkExe = new TextBox { Dock = DockStyle.Fill, Text = be.Exe };
        tLayout.Controls.Add(_bkExe, 1, 2);

        tLayout.Controls.Add(new Label { Text = "Config path (app-config.json):", AutoSize = true, Anchor = AnchorStyles.Left }, 0, 3);
        _bkConfigPath = new TextBox { Dock = DockStyle.Fill, Text = be.ConfigPath };
        tLayout.Controls.Add(_bkConfigPath, 1, 3);

        tLayout.Controls.Add(new Label { Text = "Service name:", AutoSize = true, Anchor = AnchorStyles.Left }, 0, 4);
        _bkServiceName = new TextBox { Dock = DockStyle.Fill, Text = be.ServiceName };
        tLayout.Controls.Add(_bkServiceName, 1, 4);

        tLayout.Controls.Add(new Label { Text = "Manage service:", AutoSize = true, Anchor = AnchorStyles.Left }, 0, 5);
        _bkManageService = new CheckBox { Text = "Restart service from ProxySwitch (requires admin)", AutoSize = true, Checked = be.ManageService };
        tLayout.Controls.Add(_bkManageService, 1, 5);

        tLayout.Controls.Add(new Label { Text = "Auto restart on config change:", AutoSize = true, Anchor = AnchorStyles.Left }, 0, 6);
        _bkAutoRestart = new CheckBox { AutoSize = true, Checked = be.AutoRestartOnConfigChange };
        tLayout.Controls.Add(_bkAutoRestart, 1, 6);

        transparentTab.Controls.Add(tLayout);

        var help = new Label
        {
            Text = "ProxiFyre is open source: https://github.com/wiresock/proxifyre\n" +
                   "Requires Windows Packet Filter driver (separate install). Run ProxiFyre.exe as administrator.\n" +
                   "ProxySwitch writes app-config.json based on AppRoutes; it does NOT install drivers or auto-elevate.",
            Dock = DockStyle.Bottom,
            AutoSize = true,
            ForeColor = Color.DimGray,
            Padding = new Padding(0, 12, 0, 0)
        };
        transparentTab.Controls.Add(help);

        tabs.TabPages.Add(transparentTab);

        Controls.Add(tabs);

        // Button panel
        var btnPanel = new FlowLayoutPanel
        {
            Dock = DockStyle.Bottom,
            FlowDirection = FlowDirection.RightToLeft,
            Padding = new Padding(8),
            AutoSize = true
        };
        var saveBtn = new Button { Text = "Save", AutoSize = true };
        saveBtn.Click += OnSave;
        var cancelBtn = new Button { Text = "Cancel", AutoSize = true };
        cancelBtn.Click += (_, _) => Close();
        btnPanel.Controls.Add(cancelBtn);
        btnPanel.Controls.Add(saveBtn);
        Controls.Add(btnPanel);

        BindData();
    }

    private void BindData()
    {
        _proxyGrid.DataSource = new BindingSource { DataSource = _config.Proxies };
        _appGrid.DataSource = new BindingSource { DataSource = _config.Apps };
        _backendGrid.DataSource = new BindingSource { DataSource = _config.RoutingBackends };
    }

    private void OnSave(object? sender, EventArgs e)
    {
        _proxyGrid.EndEdit();
        _appGrid.EndEdit();
        _backendGrid.EndEdit();

        // Push transparent backend form fields back into the model
        var be = _config.TransparentBackend;
        be.Enabled = _bkEnabled.Checked;
        be.Type = _bkType.SelectedItem?.ToString() ?? "none";
        be.Exe = _bkExe.Text;
        be.ConfigPath = _bkConfigPath.Text;
        be.ServiceName = _bkServiceName.Text;
        be.ManageService = _bkManageService.Checked;
        be.AutoRestartOnConfigChange = _bkAutoRestart.Checked;

        var path = Path.Combine(MainForm.RootPath, "config", "proxyswitch.json");
        var options = new JsonSerializerOptions
        {
            WriteIndented = true,
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
        };

        try
        {
            var json = JsonSerializer.Serialize(_config, options);
            File.WriteAllText(path, json);
            Logger.Info("Config saved from settings");
            Close();
        }
        catch (Exception ex)
        {
            Logger.Error($"Save config failed: {ex.Message}");
            MessageBox.Show($"Failed to save config:\n{ex.Message}", "Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }
}
