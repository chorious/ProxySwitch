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
