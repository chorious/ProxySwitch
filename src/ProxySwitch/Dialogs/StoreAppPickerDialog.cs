using ProxySwitch.Models;
using ProxySwitch.Services;

namespace ProxySwitch.Dialogs;

public sealed class StoreAppPickerDialog : Form
{
    private readonly ProxyConfig _config;
    private readonly StoreAppCatalog _catalog = new();
    private ListBox _appList = null!;
    private ComboBox _proxyCombo = null!;
    private Button _okBtn = null!;
    private List<StoreAppCatalogEntry> _entries = new();

    public LaunchTarget? SelectedTarget { get; private set; }
    public string? SelectedProxyId { get; private set; }

    public StoreAppPickerDialog(ProxyConfig config)
    {
        _config = config;
        Text = "Add Store App";
        Size = new Size(480, 520);
        StartPosition = FormStartPosition.CenterParent;
        BackColor = UI.Theme.WindowBg;
        Font = UI.Theme.BodyFont;
        ForeColor = UI.Theme.TextPrimary;
        FormBorderStyle = FormBorderStyle.Sizable;
        MinimizeBox = false;
        MaximizeBox = false;
        BuildUI();
        _ = LoadAppsAsync();
    }

    private void BuildUI()
    {
        var layout = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            RowCount = 4,
            ColumnCount = 1,
            Padding = new Padding(12)
        };
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 28f));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100f));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 40f));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 44f));

        layout.Controls.Add(new Label
        {
            Text = "Select a Start Menu / Store app:",
            Dock = DockStyle.Fill,
            ForeColor = UI.Theme.TextPrimary,
            Font = UI.Theme.BodySemibold
        }, 0, 0);

        _appList = new ListBox
        {
            Dock = DockStyle.Fill,
            BorderStyle = BorderStyle.FixedSingle,
            BackColor = UI.Theme.PanelBg,
            ForeColor = UI.Theme.TextPrimary,
            Font = UI.Theme.BodyFont,
            IntegralHeight = false
        };
        layout.Controls.Add(_appList, 0, 1);

        var proxyRow = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            FlowDirection = FlowDirection.LeftToRight,
            WrapContents = false
        };
        proxyRow.Controls.Add(new Label
        {
            Text = "Proxy:",
            AutoSize = true,
            TextAlign = ContentAlignment.MiddleLeft,
            Margin = new Padding(0, 8, 8, 0)
        });
        _proxyCombo = new ComboBox
        {
            DropDownStyle = ComboBoxStyle.DropDownList,
            Width = 200,
            BackColor = UI.Theme.PanelBg,
            ForeColor = UI.Theme.TextPrimary
        };
        foreach (var p in _config.Proxies)
            _proxyCombo.Items.Add(new ProxyComboItem(p.Id, $"{p.Name} ({p.Port})"));
        if (_proxyCombo.Items.Count > 0) _proxyCombo.SelectedIndex = 0;
        proxyRow.Controls.Add(_proxyCombo);
        layout.Controls.Add(proxyRow, 0, 2);

        var buttonRow = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            FlowDirection = FlowDirection.RightToLeft
        };
        var cancelBtn = new Button { Text = "Cancel", AutoSize = true, DialogResult = DialogResult.Cancel };
        _okBtn = new Button { Text = "Add", AutoSize = true, DialogResult = DialogResult.OK, Enabled = false };
        _okBtn.Click += OnOk;
        buttonRow.Controls.Add(cancelBtn);
        buttonRow.Controls.Add(_okBtn);
        layout.Controls.Add(buttonRow, 0, 3);

        Controls.Add(layout);
        AcceptButton = _okBtn;
        CancelButton = cancelBtn;
    }

    private async Task LoadAppsAsync()
    {
        _appList.Items.Clear();
        _appList.Items.Add("Loading...");
        try
        {
            var entries = await Task.Run(() => _catalog.GetStartApps());
            _entries = entries.OrderBy(e => e.DisplayName, StringComparer.CurrentCultureIgnoreCase).ToList();
            _appList.Items.Clear();
            foreach (var e in _entries)
                _appList.Items.Add(e.DisplayName);
            if (_entries.Count > 0) _okBtn.Enabled = true;
        }
        catch (Exception ex)
        {
            _appList.Items.Clear();
            _appList.Items.Add($"Error: {ex.Message}");
        }
    }

    private void OnOk(object? sender, EventArgs e)
    {
        var idx = _appList.SelectedIndex;
        if (idx < 0 || idx >= _entries.Count) return;

        var entry = _entries[idx];
        var proxyItem = _proxyCombo.SelectedItem as ProxyComboItem;

        var details = _catalog.ResolvePackageDetails(entry.AppId);

        SelectedTarget = new LaunchTarget
        {
            LaunchKind = "app-user-model-id",
            DisplayName = entry.DisplayName,
            AppUserModelId = entry.AppId,
            PackageFamilyName = details?.PackageFamilyName ?? "",
            PackageRelativeExePath = details?.PackageRelativeExePath ?? ""
        };
        SelectedProxyId = proxyItem?.ProxyId;
    }

    private sealed class ProxyComboItem
    {
        public string ProxyId { get; }
        public string Label { get; }
        public ProxyComboItem(string proxyId, string label) { ProxyId = proxyId; Label = label; }
        public override string ToString() => Label;
    }
}
