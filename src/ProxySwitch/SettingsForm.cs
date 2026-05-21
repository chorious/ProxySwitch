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
    private DataGridView _routeGrid = null!;

    private CheckBox _bkEnabled = null!;
    private ComboBox _bkType = null!;
    private TextBox _bkExe = null!;
    private TextBox _bkConfigPath = null!;
    private TextBox _bkServiceName = null!;
    private CheckBox _bkManageService = null!;
    private CheckBox _bkAutoRestart = null!;

    // PR4: SplitContainer left rail replaces the legacy TabControl. Nav items
    // toggle Visibility on the content panels in _contentPanels; the field
    // grids (_proxyGrid, etc.) are owned by their parent content Panel.
    private readonly List<NavItem> _navItems = new();
    private readonly Dictionary<string, Panel> _contentPanels = new();
    private string _activeTab = "proxies";

    public SettingsForm()
    {
        Text = "ProxySwitch Settings";
        Size = new Size(960, 600);
        StartPosition = FormStartPosition.CenterScreen;
        FormBorderStyle = FormBorderStyle.Sizable;
        MinimumSize = new Size(820, 480);
        BackColor = UI.Theme.WindowBg;
        Font = UI.Theme.BodyFont;
        ForeColor = UI.Theme.TextPrimary;

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
        // Build the 5 content panels first so the nav rail can reference them
        // when wiring SetActiveTab.
        BuildProxiesPanel();
        BuildAppsPanel();
        BuildRoutingBackendsPanel();
        BuildAppRoutesPanel();
        BuildTransparentBackendPanel();

        // SplitContainer: fixed 200px left rail, content fills the rest. We use
        // FixedPanel.Panel1 + IsSplitterFixed = true so the user can't drag the
        // splitter — the rail width is a design constant, not a preference.
        //
        // Two SplitContainer initialization traps avoided here:
        //   1. Don't set Panel1MinSize/Panel2MinSize in the initializer — they
        //      trigger validation against the *current* Width (150 by default
        //      before docking), which fails for 200 + 380 > 150.
        //   2. Don't set SplitterDistance before adding to the form — it would
        //      validate against Width=150 too. Defer to after Controls.Add.
        var split = new SplitContainer
        {
            Dock = DockStyle.Fill,
            Orientation = Orientation.Vertical,
            FixedPanel = FixedPanel.Panel1,
            IsSplitterFixed = true,
            SplitterWidth = 1,
            BackColor = UI.Theme.OutlineVariant,
        };
        split.Panel1.BackColor = UI.Theme.PanelBg;
        split.Panel2.BackColor = UI.Theme.PanelBg;

        // Left rail
        var rail = BuildLeftRail();
        split.Panel1.Controls.Add(rail);

        // Content host — wraps the 5 panels (one visible at a time per SetActiveTab).
        var contentHost = new Panel
        {
            Dock = DockStyle.Fill,
            BackColor = UI.Theme.PanelBg,
            Padding = new Padding(24, 20, 24, 16),
        };
        foreach (var panel in _contentPanels.Values)
        {
            panel.Dock = DockStyle.Fill;
            panel.Visible = false;
            contentHost.Controls.Add(panel);
        }
        split.Panel2.Controls.Add(contentHost);

        // Bottom button bar
        var btnBar = BuildButtonBar();

        // Add bottom dock LAST so it claims the bottom slice; split fills the rest.
        Controls.Add(split);
        Controls.Add(btnBar);

        // Now that split is parented and Dock=Fill has stretched it to ClientSize.Width,
        // setting SplitterDistance validates against the real width (not the default 150).
        split.SplitterDistance = 200;

        SetActiveTab(_activeTab);
        BindData();
    }

    /// <summary>
    /// Left rail: section title block on top + 5 nav items below. The active
    /// item paints with PrimaryBlue background and OnPrimary text/icon.
    /// </summary>
    private Control BuildLeftRail()
    {
        var rail = new Panel
        {
            Dock = DockStyle.Fill,
            BackColor = UI.Theme.PanelBg,
            Padding = new Padding(16, 20, 16, 16),
        };

        var stack = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 6,
            BackColor = UI.Theme.PanelBg,
        };
        stack.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100f));
        stack.RowStyles.Add(new RowStyle(SizeType.Absolute, 64f));   // title block
        for (int i = 0; i < 5; i++)
            stack.RowStyles.Add(new RowStyle(SizeType.Absolute, 48f)); // 44 nav + 4 gap

        // Title block
        var titleHost = new Panel { Dock = DockStyle.Fill, BackColor = UI.Theme.PanelBg };
        titleHost.Controls.Add(new Label
        {
            Text = "Configuration",
            Font = new Font(UI.Theme.BodySemibold.FontFamily, 11f, FontStyle.Bold),
            ForeColor = UI.Theme.TextPrimary,
            BackColor = UI.Theme.PanelBg,
            AutoSize = true,
            Location = new Point(0, 0),
        });
        titleHost.Controls.Add(new Label
        {
            Text = "Network Rules",
            Font = UI.Theme.BodyFont,
            ForeColor = UI.Theme.TextSecondary,
            BackColor = UI.Theme.PanelBg,
            AutoSize = true,
            Location = new Point(0, 22),
        });
        stack.Controls.Add(titleHost, 0, 0);

        // 5 nav items — icons are reused from IconRenderer's existing kinds.
        // Stitch's "DNS Settings" item is intentionally omitted (no DNS concept
        // in ProxySwitch — see PR3b roadmap line 81).
        var defs = new (string Key, string Label, UI.IconRenderer.IconKind Icon)[]
        {
            ("proxies", "Proxies", UI.IconRenderer.IconKind.V2ray),
            ("apps", "Apps", UI.IconRenderer.IconKind.Direct),
            ("routing-backends", "Routing Backends", UI.IconRenderer.IconKind.Clash),
            ("app-routes", "App Routes", UI.IconRenderer.IconKind.Save),
            ("transparent-backend", "Transparent Backend", UI.IconRenderer.IconKind.Info),
        };
        int row = 1;
        foreach (var d in defs)
        {
            var item = new NavItem(d.Key, d.Label, d.Icon)
            {
                Dock = DockStyle.Fill,
                Margin = new Padding(0, 2, 0, 2),
            };
            var key = d.Key;
            item.ItemClicked += (_, _) => SetActiveTab(key);
            _navItems.Add(item);
            stack.Controls.Add(item, 0, row++);
        }

        rail.Controls.Add(stack);
        return rail;
    }

    private FlowLayoutPanel BuildButtonBar()
    {
        var bar = new FlowLayoutPanel
        {
            Dock = DockStyle.Bottom,
            FlowDirection = FlowDirection.RightToLeft,
            Padding = new Padding(20, 12, 20, 16),
            BackColor = UI.Theme.WindowBg,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
        };
        var applyBtn = new UI.PillButton
        {
            Text = "Apply Settings",
            Variant = UI.PillButton.PillVariant.Primary,
            Padding = new Padding(18, 6, 18, 6),
        };
        applyBtn.Click += OnSave;

        var cancelBtn = new UI.PillButton
        {
            Text = "Cancel",
            Variant = UI.PillButton.PillVariant.Default,
            Padding = new Padding(18, 6, 18, 6),
        };
        cancelBtn.Click += (_, _) => { DialogResult = DialogResult.Cancel; Close(); };

        bar.Controls.Add(applyBtn);
        bar.Controls.Add(cancelBtn);
        return bar;
    }

    private void SetActiveTab(string key)
    {
        _activeTab = key;
        foreach (var nav in _navItems)
            nav.IsActive = string.Equals(nav.Key, key, StringComparison.Ordinal);
        foreach (var (k, panel) in _contentPanels)
            panel.Visible = string.Equals(k, key, StringComparison.Ordinal);
    }

    /// <summary>
    /// Apply the Stitch grid palette: subtle horizontal dividers, alternating
    /// row tint, semibold headers in TextSecondary. Mirrors Dashboard's session
    /// grid styling so the two surfaces feel consistent.
    /// </summary>
    private static void ApplyGridTheme(DataGridView grid)
    {
        grid.BackgroundColor = UI.Theme.PanelBg;
        grid.GridColor = UI.Theme.BorderSubtle;
        grid.BorderStyle = BorderStyle.None;
        grid.CellBorderStyle = DataGridViewCellBorderStyle.SingleHorizontal;
        grid.ColumnHeadersBorderStyle = DataGridViewHeaderBorderStyle.Single;
        grid.EnableHeadersVisualStyles = false;
        grid.RowHeadersVisible = false;
        grid.AllowUserToResizeRows = false;
        grid.ColumnHeadersHeightSizeMode = DataGridViewColumnHeadersHeightSizeMode.DisableResizing;
        grid.ColumnHeadersHeight = 30;
        grid.RowTemplate.Height = 32;

        grid.ColumnHeadersDefaultCellStyle.BackColor = UI.Theme.SurfaceContainerLow;
        grid.ColumnHeadersDefaultCellStyle.ForeColor = UI.Theme.TextSecondary;
        grid.ColumnHeadersDefaultCellStyle.Font = UI.Theme.BodySemibold;
        grid.ColumnHeadersDefaultCellStyle.Padding = new Padding(6, 4, 6, 4);
        grid.ColumnHeadersDefaultCellStyle.SelectionBackColor = UI.Theme.SurfaceContainerLow;
        grid.ColumnHeadersDefaultCellStyle.SelectionForeColor = UI.Theme.TextSecondary;

        grid.DefaultCellStyle.BackColor = UI.Theme.PanelBg;
        grid.DefaultCellStyle.ForeColor = UI.Theme.TextPrimary;
        grid.DefaultCellStyle.SelectionBackColor = UI.Theme.HoverTint;
        grid.DefaultCellStyle.SelectionForeColor = UI.Theme.TextPrimary;
        grid.DefaultCellStyle.Padding = new Padding(6, 4, 6, 4);
        // Single-line cells with EndEllipsis (DataGridView default for non-wrap).
        // WrapMode=NotSet would let some Fill columns auto-grow row height when
        // their content overflows — pin to False to keep rows uniform 32px.
        // (v0.8.2 patch)
        grid.DefaultCellStyle.WrapMode = DataGridViewTriState.False;

        grid.AlternatingRowsDefaultCellStyle.BackColor = UI.Theme.SurfaceContainerLow;
        grid.AlternatingRowsDefaultCellStyle.ForeColor = UI.Theme.TextPrimary;
        grid.AlternatingRowsDefaultCellStyle.SelectionBackColor = UI.Theme.HoverTint;
        grid.AlternatingRowsDefaultCellStyle.SelectionForeColor = UI.Theme.TextPrimary;
        grid.AlternatingRowsDefaultCellStyle.WrapMode = DataGridViewTriState.False;
    }

    /// <summary>
    /// Layout each content panel as: 64px title block on top + DataGridView (or
    /// nested layout) filling the rest. Title block is always two stacked labels.
    /// </summary>
    private static Panel BuildContentTitle(string title, string subtitle)
    {
        var p = new Panel { Dock = DockStyle.Top, Height = 56, BackColor = UI.Theme.PanelBg };
        p.Controls.Add(new Label
        {
            Text = title,
            Font = new Font(UI.Theme.BodySemibold.FontFamily, 14f, FontStyle.Bold),
            ForeColor = UI.Theme.TextPrimary,
            BackColor = UI.Theme.PanelBg,
            AutoSize = true,
            Location = new Point(0, 0),
        });
        p.Controls.Add(new Label
        {
            Text = subtitle,
            Font = UI.Theme.BodyFont,
            ForeColor = UI.Theme.TextSecondary,
            BackColor = UI.Theme.PanelBg,
            AutoSize = true,
            Location = new Point(0, 28),
        });
        return p;
    }

    private void BuildProxiesPanel()
    {
        var p = new Panel { BackColor = UI.Theme.PanelBg };
        _proxyGrid = new DataGridView
        {
            Dock = DockStyle.Fill,
            AutoGenerateColumns = false,
            AllowUserToAddRows = true,
            AllowUserToDeleteRows = true,
            SelectionMode = DataGridViewSelectionMode.FullRowSelect,
        };
        _proxyGrid.Columns.Add(new DataGridViewTextBoxColumn { DataPropertyName = "Id", HeaderText = "ID", Width = 80, MinimumWidth = 60 });
        _proxyGrid.Columns.Add(new DataGridViewTextBoxColumn { DataPropertyName = "Name", HeaderText = "Name", Width = 160, MinimumWidth = 100 });
        _proxyGrid.Columns.Add(new DataGridViewTextBoxColumn { DataPropertyName = "Type", HeaderText = "Type", Width = 80, MinimumWidth = 60 });
        _proxyGrid.Columns.Add(new DataGridViewTextBoxColumn { DataPropertyName = "Host", HeaderText = "Host", Width = 110, MinimumWidth = 80 });
        _proxyGrid.Columns.Add(new DataGridViewTextBoxColumn { DataPropertyName = "Port", HeaderText = "Port", Width = 60, MinimumWidth = 50 });
        ApplyGridTheme(_proxyGrid);

        p.Controls.Add(_proxyGrid);
        p.Controls.Add(BuildContentTitle("Proxies", "Upstream proxy endpoints used by routing backends."));
        _contentPanels["proxies"] = p;
    }

    private void BuildAppsPanel()
    {
        var p = new Panel { BackColor = UI.Theme.PanelBg };
        _appGrid = new DataGridView
        {
            Dock = DockStyle.Fill,
            AutoGenerateColumns = false,
            AllowUserToAddRows = true,
            AllowUserToDeleteRows = true,
            SelectionMode = DataGridViewSelectionMode.FullRowSelect,
        };
        _appGrid.Columns.Add(new DataGridViewTextBoxColumn { DataPropertyName = "Id", HeaderText = "ID", Width = 90, MinimumWidth = 70 });
        _appGrid.Columns.Add(new DataGridViewTextBoxColumn { DataPropertyName = "Name", HeaderText = "Name", FillWeight = 20, AutoSizeMode = DataGridViewAutoSizeColumnMode.Fill, MinimumWidth = 100 });
        _appGrid.Columns.Add(new DataGridViewTextBoxColumn { DataPropertyName = "Exe", HeaderText = "Executable Path", FillWeight = 40, AutoSizeMode = DataGridViewAutoSizeColumnMode.Fill, MinimumWidth = 140 });
        _appGrid.Columns.Add(new DataGridViewTextBoxColumn { DataPropertyName = "Mode", HeaderText = "Mode", Width = 100, MinimumWidth = 80 });
        _appGrid.Columns.Add(new DataGridViewTextBoxColumn { DataPropertyName = "ProxyId", HeaderText = "Proxy ID", Width = 70, MinimumWidth = 60 });
        _appGrid.Columns.Add(new DataGridViewTextBoxColumn { DataPropertyName = "UserDataDir", HeaderText = "User Data Dir", FillWeight = 30, AutoSizeMode = DataGridViewAutoSizeColumnMode.Fill, MinimumWidth = 100 });
        ApplyGridTheme(_appGrid);

        p.Controls.Add(_appGrid);
        p.Controls.Add(BuildContentTitle("Apps", "Pinned application templates available on the Dashboard."));
        _contentPanels["apps"] = p;
    }

    private void BuildRoutingBackendsPanel()
    {
        var p = new Panel { BackColor = UI.Theme.PanelBg };
        _backendGrid = new DataGridView
        {
            Dock = DockStyle.Fill,
            AutoGenerateColumns = false,
            AllowUserToAddRows = true,
            AllowUserToDeleteRows = true,
            SelectionMode = DataGridViewSelectionMode.FullRowSelect,
        };
        _backendGrid.Columns.Add(new DataGridViewTextBoxColumn { DataPropertyName = "Id", HeaderText = "ID", FillWeight = 20, AutoSizeMode = DataGridViewAutoSizeColumnMode.Fill, MinimumWidth = 100 });
        _backendGrid.Columns.Add(new DataGridViewTextBoxColumn { DataPropertyName = "Name", HeaderText = "Name", FillWeight = 18, AutoSizeMode = DataGridViewAutoSizeColumnMode.Fill, MinimumWidth = 100 });
        _backendGrid.Columns.Add(new DataGridViewTextBoxColumn { DataPropertyName = "ProxyId", HeaderText = "Proxy ID", Width = 70, MinimumWidth = 60 });
        _backendGrid.Columns.Add(new DataGridViewTextBoxColumn { DataPropertyName = "AppPath", HeaderText = "App Path", FillWeight = 24, AutoSizeMode = DataGridViewAutoSizeColumnMode.Fill, MinimumWidth = 120 });
        _backendGrid.Columns.Add(new DataGridViewTextBoxColumn { DataPropertyName = "ConfigPath", HeaderText = "Config Path", FillWeight = 26, AutoSizeMode = DataGridViewAutoSizeColumnMode.Fill, MinimumWidth = 120 });
        _backendGrid.Columns.Add(new DataGridViewTextBoxColumn { DataPropertyName = "RuleFormat", HeaderText = "Rule Format", Width = 90, MinimumWidth = 70 });
        ApplyGridTheme(_backendGrid);

        p.Controls.Add(_backendGrid);
        p.Controls.Add(BuildContentTitle("Routing Backends", "External routers (Clash Verge, v2ray) and their rule formats."));
        _contentPanels["routing-backends"] = p;
    }

    private void BuildAppRoutesPanel()
    {
        var p = new Panel { BackColor = UI.Theme.PanelBg };
        _routeGrid = new DataGridView
        {
            Dock = DockStyle.Fill,
            AutoGenerateColumns = false,
            AllowUserToAddRows = false,    // adds happen via drag-drop on Dashboard
            AllowUserToDeleteRows = true,
            SelectionMode = DataGridViewSelectionMode.FullRowSelect,
        };
        _routeGrid.Columns.Add(new DataGridViewTextBoxColumn { DataPropertyName = "Name", HeaderText = "Name", FillWeight = 18, AutoSizeMode = DataGridViewAutoSizeColumnMode.Fill, MinimumWidth = 100 });
        _routeGrid.Columns.Add(new DataGridViewTextBoxColumn { DataPropertyName = "MatchKind", HeaderText = "Match Kind", Width = 90, MinimumWidth = 80 });
        _routeGrid.Columns.Add(new DataGridViewTextBoxColumn { DataPropertyName = "ExePath", HeaderText = "Executable", FillWeight = 28, AutoSizeMode = DataGridViewAutoSizeColumnMode.Fill, MinimumWidth = 120 });
        _routeGrid.Columns.Add(new DataGridViewTextBoxColumn { DataPropertyName = "ResolvedExePath", HeaderText = "Resolved Path", FillWeight = 22, AutoSizeMode = DataGridViewAutoSizeColumnMode.Fill, MinimumWidth = 100, ReadOnly = true });
        _routeGrid.Columns.Add(new DataGridViewTextBoxColumn { DataPropertyName = "ProcessName", HeaderText = "Process", FillWeight = 14, AutoSizeMode = DataGridViewAutoSizeColumnMode.Fill, MinimumWidth = 80 });
        _routeGrid.Columns.Add(new DataGridViewTextBoxColumn { DataPropertyName = "ProxyId", HeaderText = "Proxy", Width = 70, MinimumWidth = 60 });
        _routeGrid.Columns.Add(new DataGridViewCheckBoxColumn { DataPropertyName = "Enabled", HeaderText = "Enabled", Width = 60, MinimumWidth = 55 });
        _routeGrid.Columns.Add(new DataGridViewCheckBoxColumn { DataPropertyName = "IsPersistent", HeaderText = "Saved", Width = 60, MinimumWidth = 55, ReadOnly = true });
        _routeGrid.Columns.Add(new DataGridViewTextBoxColumn { DataPropertyName = "Source", HeaderText = "Source", FillWeight = 12, AutoSizeMode = DataGridViewAutoSizeColumnMode.Fill, MinimumWidth = 80, ReadOnly = true });
        ApplyGridTheme(_routeGrid);

        p.Controls.Add(_routeGrid);
        p.Controls.Add(BuildContentTitle("App Routes", "Per-app routing rules driving ProxiFyre. Add new routes by dropping apps on the Dashboard."));
        _contentPanels["app-routes"] = p;
    }

    private void BuildTransparentBackendPanel()
    {
        var p = new Panel { BackColor = UI.Theme.PanelBg };

        var tLayout = new TableLayoutPanel
        {
            Dock = DockStyle.Top,
            AutoSize = true,
            ColumnCount = 2,
            RowCount = 7,
            BackColor = UI.Theme.PanelBg,
        };
        tLayout.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 200f));
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

        var help = new Label
        {
            Text = "ProxiFyre is open source: https://github.com/wiresock/proxifyre\n" +
                   "Requires Windows Packet Filter driver (separate install). Run ProxiFyre.exe as administrator.\n" +
                   "ProxySwitch writes app-config.json based on AppRoutes. It does NOT install drivers or services. " +
                   "If Auto-restart is enabled it may request UAC to restart the configured ProxiFyre service.",
            Dock = DockStyle.Bottom,
            AutoSize = true,
            ForeColor = UI.Theme.TextSecondary,
            Padding = new Padding(0, 12, 0, 0),
        };

        p.Controls.Add(help);
        p.Controls.Add(tLayout);
        p.Controls.Add(BuildContentTitle("Transparent Backend", "ProxiFyre process-routing backend configuration."));
        _contentPanels["transparent-backend"] = p;
    }

    private void BindData()
    {
        _proxyGrid.DataSource = new BindingSource { DataSource = _config.Proxies };
        _appGrid.DataSource = new BindingSource { DataSource = _config.Apps };
        _backendGrid.DataSource = new BindingSource { DataSource = _config.RoutingBackends };
        _routeGrid.DataSource = new BindingSource { DataSource = _config.AppRoutes };
    }

    private void OnSave(object? sender, EventArgs e)
    {
        _proxyGrid.EndEdit();
        _appGrid.EndEdit();
        _backendGrid.EndEdit();
        _routeGrid.EndEdit();

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
            // Atomic write: tmp + Move so a crash mid-save can't truncate proxyswitch.json
            // (#6 in GPT review v0.7). Matches ProxiFyreBackend.SaveSwitchConfig.
            var tmp = path + ".tmp";
            File.WriteAllText(tmp, json);
            File.Move(tmp, path, overwrite: true);
            Logger.Info("Config saved from settings");
            // Only OK means the caller should reload runtime services (#7 in GPT review v0.7).
            DialogResult = DialogResult.OK;
            Close();
        }
        catch (Exception ex)
        {
            Logger.Error($"Save config failed: {ex.Message}");
            MessageBox.Show($"Failed to save config:\n{ex.Message}", "Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    /// <summary>
    /// Left-rail nav item: icon + label, with PrimaryBlue rounded background
    /// when active. Click raises ItemClicked; the parent SettingsForm dispatches
    /// to SetActiveTab(Key). Drawn by hand so we can apply Theme tokens uniformly
    /// (no native button chrome).
    /// </summary>
    private sealed class NavItem : Panel
    {
        public string Key { get; }
        public string Label { get; }
        public UI.IconRenderer.IconKind Icon { get; }
        public event EventHandler? ItemClicked;

        private bool _isActive;
        public bool IsActive
        {
            get => _isActive;
            set
            {
                if (_isActive == value) return;
                _isActive = value;
                Invalidate();
            }
        }

        public NavItem(string key, string label, UI.IconRenderer.IconKind icon)
        {
            Key = key; Label = label; Icon = icon;
            Cursor = Cursors.Hand;
            DoubleBuffered = true;
            BackColor = UI.Theme.PanelBg;
            SetStyle(ControlStyles.ResizeRedraw | ControlStyles.OptimizedDoubleBuffer
                   | ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint, true);
        }

        protected override void OnMouseClick(MouseEventArgs e)
        {
            base.OnMouseClick(e);
            ItemClicked?.Invoke(this, EventArgs.Empty);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;

            if (_isActive)
            {
                using var path = RoundedRect(new RectangleF(0, 0, Width, Height), 6f);
                using var brush = new SolidBrush(UI.Theme.PrimaryBlue);
                g.FillPath(brush, path);
            }

            var iconColor = _isActive ? UI.Theme.OnPrimary : UI.Theme.TextSecondary;
            var iconBounds = new RectangleF(12, (Height - 20) / 2f, 20, 20);
            UI.IconRenderer.Draw(g, iconBounds, Icon, iconColor);

            var foreColor = _isActive ? UI.Theme.OnPrimary : UI.Theme.TextPrimary;
            var font = _isActive ? UI.Theme.BodySemibold : UI.Theme.BodyFont;
            var labelRect = new Rectangle(44, 0, Width - 44 - 8, Height);
            TextRenderer.DrawText(g, Label, font, labelRect, foreColor,
                TextFormatFlags.SingleLine | TextFormatFlags.VerticalCenter | TextFormatFlags.Left);
        }

        private static System.Drawing.Drawing2D.GraphicsPath RoundedRect(RectangleF r, float radius)
        {
            var path = new System.Drawing.Drawing2D.GraphicsPath();
            float d = radius * 2;
            path.StartFigure();
            path.AddArc(r.Left, r.Top, d, d, 180, 90);
            path.AddArc(r.Right - d, r.Top, d, d, 270, 90);
            path.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
            path.AddArc(r.Left, r.Bottom - d, d, d, 90, 90);
            path.CloseFigure();
            return path;
        }
    }
}
