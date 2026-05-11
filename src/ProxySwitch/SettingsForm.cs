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
    private TextBox _proxifierPath = null!;

    public SettingsForm()
    {
        Text = "ProxySwitch Settings";
        Size = new Size(720, 520);
        StartPosition = FormStartPosition.CenterScreen;
        FormBorderStyle = FormBorderStyle.Sizable;
        MinimumSize = new Size(600, 400);

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

        // Proxy tab
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
        _proxyGrid.Columns.Add(new DataGridViewTextBoxColumn { DataPropertyName = "Name", HeaderText = "Name", Width = 120 });
        _proxyGrid.Columns.Add(new DataGridViewTextBoxColumn { DataPropertyName = "Type", HeaderText = "Type", Width = 80 });
        _proxyGrid.Columns.Add(new DataGridViewTextBoxColumn { DataPropertyName = "Host", HeaderText = "Host", Width = 100 });
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

        // Proxifier tab
        var proxTab = new TabPage("Proxifier");
        proxTab.Padding = new Padding(12);
        var proxLayout = new TableLayoutPanel
        {
            Dock = DockStyle.Top,
            AutoSize = true,
            ColumnCount = 2,
            RowCount = 1
        };
        proxLayout.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        proxLayout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100f));
        proxLayout.Controls.Add(new Label { Text = "Proxifier exe:", AutoSize = true, Anchor = AnchorStyles.Left }, 0, 0);
        _proxifierPath = new TextBox { Dock = DockStyle.Fill, Text = _config.Proxifier.Exe };
        proxLayout.Controls.Add(_proxifierPath, 1, 0);
        proxTab.Controls.Add(proxLayout);
        tabs.TabPages.Add(proxTab);

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
    }

    private void OnSave(object? sender, EventArgs e)
    {
        _proxyGrid.EndEdit();
        _appGrid.EndEdit();

        _config.Proxifier.Exe = _proxifierPath.Text;

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
