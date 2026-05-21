using System.Diagnostics;
using System.Text.Json;
using ProxySwitch.Controls;
using ProxySwitch.Models;
using ProxySwitch.Services;

namespace ProxySwitch;

public class DashboardForm : Form
{
    // Mutable: Settings save triggers Rebind() which swaps service references in-place
    // so the Dashboard stays open across config reloads (#6 in opus_review_v0.7.1).
    private ProxyConfig _config;
    private PortMonitor _monitor;
    private SessionManager _sessions;
    private EventStore _events;
    private AppLauncher _launcher;
    private RoutingHintService _hintService;
    private ProxiFyreBackend? _backend;

    private DataGridView _sessionGrid = null!;
    private ListBox _eventList = null!;
    private Label _proxyStatusLabel = null!;
    private Label _backendStatusLabel = null!;
    private Button _restartBackendBtn = null!;
    private FlowLayoutPanel _pinnedPanel = null!;
    private LaunchZoneControl _directZone = null!;
    private LaunchZoneControl _z10708 = null!;
    private LaunchZoneControl _z10808 = null!;
    private StatusStrip _statusStrip = null!;
    private ToolStripStatusLabel _statusBackend = null!;
    private ToolStripStatusLabel _statusSystem = null!;
    private ToolStripStatusLabel _statusVersion = null!;
    private System.Windows.Forms.Timer _refreshTimer = null!;
    private bool _correlatedDialogOpen;
    private readonly Queue<(LaunchSession Session, List<HandoffCandidate> Candidates)> _pendingCorrelated = new();

    // Cached fonts for the Actions cell. CellPainting fires per-cell at 1Hz;
    // allocating a fresh Font every call wastes GDI handles even when promptly
    // disposed. Reused by OnSessionCellMouseClick so the hit-test measures with
    // the same font that CellPainting drew — fixes opus review #2/#4.
    private readonly Font _actionsUnderlineFont = new(UI.Theme.BodyFont, FontStyle.Underline);
    private readonly Font _actionsItalicFont = new(UI.Theme.BodyFont, FontStyle.Italic);

    /// <summary>
    /// Raised when the user clicks the Settings tab. Handled by MainForm so the
    /// dialog opens against the runtime-services owner — same path as the tray
    /// menu's Settings item (which is responsible for ReloadRuntimeServices on
    /// DialogResult.OK).
    /// </summary>
    public event Action? SettingsRequested;

    public DashboardForm(ProxyConfig config, PortMonitor monitor, SessionManager sessions, EventStore events, AppLauncher launcher, ProxiFyreBackend? backend = null)
    {
        _config = config;
        _monitor = monitor;
        _sessions = sessions;
        _events = events;
        _launcher = launcher;
        _backend = backend;
        _hintService = new RoutingHintService(config);

        Text = "ProxySwitch Dashboard";
        Size = new Size(960, 680);
        StartPosition = FormStartPosition.CenterScreen;
        MinimumSize = new Size(720, 480);
        BackColor = UI.Theme.WindowBg;
        Font = UI.Theme.BodyFont;
        ForeColor = UI.Theme.TextPrimary;

        BuildUI();
        SubscribeEvents();

        _refreshTimer = new System.Windows.Forms.Timer { Interval = 1000 };
        _refreshTimer.Tick += (_, _) => RefreshUI();
        _refreshTimer.Start();
    }

    private void SubscribeEvents()
    {
        _sessions.SessionsChanged += OnSessionsChanged;
        _sessions.CorrelatedCandidatesFound += OnCorrelatedCandidatesFound;
        _events.EventAdded += OnEventAdded;
        _monitor.StatusChanged += OnProxyStatusChanged;
    }

    private void UnsubscribeEvents()
    {
        try { _sessions.SessionsChanged -= OnSessionsChanged; } catch { }
        try { _sessions.CorrelatedCandidatesFound -= OnCorrelatedCandidatesFound; } catch { }
        try { _events.EventAdded -= OnEventAdded; } catch { }
        try { _monitor.StatusChanged -= OnProxyStatusChanged; } catch { }
    }

    /// <summary>
    /// Swap service references after a Settings save without rebuilding the window.
    /// Pinned-apps panel and zone labels are rebuilt in-place because Settings can
    /// add/rename Apps and Proxies; session cards reuse via tag-based diffing.
    /// (#6 in opus_review_v0.7.1)
    /// </summary>
    public void Rebind(ProxyConfig config, PortMonitor monitor, SessionManager sessions, EventStore events, AppLauncher launcher, ProxiFyreBackend? backend)
    {
        _refreshTimer.Stop();
        UnsubscribeEvents();

        _config = config;
        _monitor = monitor;
        _sessions = sessions;
        _events = events;
        _launcher = launcher;
        _backend = backend;
        _hintService = new RoutingHintService(config);

        SubscribeEvents();

        RebuildPinnedApps();
        RebuildZoneLabels();
        UpdateProxyStatus();
        UpdateBackendStatus();
        UpdateRestartButton(_restartBackendBtn);
        RefreshSessions();
        RefreshEvents();

        _refreshTimer.Start();
    }

    private void RebuildPinnedApps()
    {
        _pinnedPanel.Controls.Clear();
        _pinnedPanel.BackColor = UI.Theme.WindowBg;
        foreach (var app in _config.Apps)
        {
            var btn = new UI.PillButton
            {
                Text = app.Name,
                AccentDot = AccentForApp(app)
            };
            var a = app;
            btn.Click += (_, _) => LaunchPinned(a);
            _pinnedPanel.Controls.Add(btn);
        }

        // "+ Add Pinned" trailing affordance — opens Settings so the user can
        // register a new app template. Routes through SettingsRequested so the
        // shared OpenSettings path (MainForm) is used — same as the top tab
        // strip Settings entry, ensuring ReloadRuntimeServices fires on OK.
        // (v0.8.1 opus review #1 — previously this bypassed OpenSettings.)
        var addBtn = new UI.PillButton { Text = "+ Add Pinned" };
        addBtn.Click += (_, _) => SettingsRequested?.Invoke();
        _pinnedPanel.Controls.Add(addBtn);
    }

    private static Color AccentForApp(Models.AppConfig app)
    {
        if (string.IsNullOrEmpty(app.ProxyId)) return UI.Theme.AccentDirect;
        return app.ProxyId switch
        {
            "p10708" => UI.Theme.AccentClash,
            "p10808" => UI.Theme.AccentV2ray,
            _ => UI.Theme.AccentDirect
        };
    }

    private void RebuildZoneLabels()
    {
        // LaunchZoneControl uses UserPaint + custom OnPaint over Title — Invalidate
        // triggers the repaint with the new text.
        _z10708.Title = LabelForProxy("p10708", "10708");
        _z10708.Invalidate();
        _z10808.Title = LabelForProxy("p10808", "10808");
        _z10808.Invalidate();
        _directZone.Invalidate();
    }

    private void BuildUI()
    {
        var mainLayout = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            RowCount = 4,
            ColumnCount = 1,
            Padding = new Padding(8)
        };
        mainLayout.RowStyles.Add(new RowStyle(SizeType.Absolute, 100f));
        mainLayout.RowStyles.Add(new RowStyle(SizeType.Absolute, 50f));
        mainLayout.RowStyles.Add(new RowStyle(SizeType.Percent, 60f));
        mainLayout.RowStyles.Add(new RowStyle(SizeType.Percent, 40f));

        // Row 0: Launch Zones (named after the actual routing backend)
        var zonePanel = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 3,
            RowCount = 1
        };
        for (int i = 0; i < 3; i++)
            zonePanel.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 33.33f));

        var directZone = new LaunchZoneControl
        {
            Title = "Direct",
            Subtitle = "Drop app — no proxy",
            Mode = "direct",
            IconKind = UI.IconRenderer.IconKind.Direct,
            AccentColor = UI.Theme.AccentDirect,
            Dock = DockStyle.Fill,
            Margin = new Padding(4)
        };
        directZone.FileDropped += path => HandleDrop(path, "direct");
        zonePanel.Controls.Add(directZone, 0, 0);
        _directZone = directZone;

        var z10708Label = LabelForProxy("p10708", "10708");
        var z10708 = new LaunchZoneControl
        {
            Title = z10708Label,
            Subtitle = "Drop app — route through ProxiFyre",
            Mode = "proxy-10708",
            IconKind = UI.IconRenderer.IconKind.Clash,
            AccentColor = UI.Theme.AccentClash,
            Dock = DockStyle.Fill,
            Margin = new Padding(4)
        };
        z10708.FileDropped += path => HandleDrop(path, "proxy-10708");
        zonePanel.Controls.Add(z10708, 1, 0);
        _z10708 = z10708;

        var z10808Label = LabelForProxy("p10808", "10808");
        var z10808 = new LaunchZoneControl
        {
            Title = z10808Label,
            Subtitle = "Drop app — route through ProxiFyre",
            Mode = "proxy-10808",
            IconKind = UI.IconRenderer.IconKind.V2ray,
            AccentColor = UI.Theme.AccentV2ray,
            Dock = DockStyle.Fill,
            Margin = new Padding(4)
        };
        z10808.FileDropped += path => HandleDrop(path, "proxy-10808");
        zonePanel.Controls.Add(z10808, 2, 0);
        _z10808 = z10808;
        mainLayout.Controls.Add(zonePanel, 0, 0);

        // Row 1: Pinned Apps
        _pinnedPanel = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            FlowDirection = FlowDirection.LeftToRight,
            AutoScroll = true,
            WrapContents = true
        };
        RebuildPinnedApps();
        mainLayout.Controls.Add(_pinnedPanel, 0, 1);

        // Row 2: Sessions — DataGridView (Stitch design moves from FlowLayoutPanel
        // cards to a dense ops-console table).
        _sessionGrid = BuildSessionGrid();
        mainLayout.Controls.Add(BuildSection("Sessions", _sessionGrid), 0, 2);

        // Row 3: Proxy Status + Events
        var bottomPanel = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 2,
            RowCount = 1,
            BackColor = UI.Theme.WindowBg
        };
        bottomPanel.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 35f));
        bottomPanel.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 65f));

        var proxyLayout = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 3,
            Padding = new Padding(4),
            BackColor = UI.Theme.PanelBg
        };
        proxyLayout.RowStyles.Add(new RowStyle(SizeType.Percent, 55f));
        proxyLayout.RowStyles.Add(new RowStyle(SizeType.Percent, 30f));
        proxyLayout.RowStyles.Add(new RowStyle(SizeType.Absolute, 28f));

        _proxyStatusLabel = new Label
        {
            Dock = DockStyle.Fill,
            Text = "Checking...",
            ForeColor = UI.Theme.TextPrimary,
            Font = UI.Theme.BodyFont
        };
        proxyLayout.Controls.Add(_proxyStatusLabel, 0, 0);

        _backendStatusLabel = new Label
        {
            Dock = DockStyle.Fill,
            Text = "Backend: —",
            Font = UI.Theme.StatusLabel,
            ForeColor = UI.Theme.TextSecondary
        };
        proxyLayout.Controls.Add(_backendStatusLabel, 0, 1);

        // Restart ProxiFyre button — visible whenever backend type is proxifyre + enabled
        var restartBackendBtn = new Button
        {
            Text = "Restart ProxiFyre (UAC)",
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            Anchor = AnchorStyles.Left | AnchorStyles.Top,
            Visible = false
        };
        restartBackendBtn.Click += async (_, _) =>
        {
            if (_backend == null) return;
            // RestartServiceElevated waits up to 20s for the elevated helper and another
            // 10s for the service Running transition. That belongs on a background thread
            // (#5 in GPT review v0.7).
            restartBackendBtn.Enabled = false;
            var originalText = restartBackendBtn.Text;
            restartBackendBtn.Text = "Restarting...";
            try
            {
                var ok = await Task.Run(() => _backend.RestartServiceElevated());
                if (!ok)
                    _events.Add("RestartFailed", "ProxiFyre restart failed or declined");
            }
            catch (Exception ex)
            {
                Logger.Error($"Restart click handler failed: {ex.Message}");
                _events.Add("RestartFailed", $"unexpected: {ex.Message}");
            }
            finally
            {
                restartBackendBtn.Enabled = true;
                UpdateBackendStatus();
                UpdateRestartButton(restartBackendBtn);
            }
        };
        _restartBackendBtn = restartBackendBtn;
        proxyLayout.Controls.Add(restartBackendBtn, 0, 2);

        bottomPanel.Controls.Add(BuildSection("Proxies & Backend", proxyLayout), 0, 0);

        _eventList = new ListBox
        {
            Dock = DockStyle.Fill,
            Font = UI.Theme.ConsoleFont,
            BorderStyle = BorderStyle.None,
            BackColor = UI.Theme.EventsBg,
            ForeColor = UI.Theme.EventsFg,
            DrawMode = DrawMode.OwnerDrawFixed,
            ItemHeight = 18,
            IntegralHeight = false
        };
        _eventList.DrawItem += OnEventDrawItem;
        bottomPanel.Controls.Add(BuildSection("Events", _eventList), 1, 0);

        mainLayout.Controls.Add(bottomPanel, 0, 3);

        Controls.Add(mainLayout);

        // Bottom StatusStrip — Stitch design includes a status footer with backend
        // state, system state, and version. Add AFTER mainLayout so dock layout
        // gives StatusStrip the bottom slice and mainLayout fills the rest.
        _statusBackend = new ToolStripStatusLabel("Backend: —")
        {
            ForeColor = UI.Theme.TextSecondary,
            Font = UI.Theme.StatusLabel
        };
        _statusSystem = new ToolStripStatusLabel("System: —")
        {
            ForeColor = UI.Theme.TextSecondary,
            Font = UI.Theme.StatusLabel,
            Spring = true,
            TextAlign = ContentAlignment.MiddleCenter
        };
        _statusVersion = new ToolStripStatusLabel("v0.8.2")
        {
            ForeColor = UI.Theme.TextSecondary,
            Font = UI.Theme.StatusLabel,
            Alignment = ToolStripItemAlignment.Right
        };
        _statusStrip = new StatusStrip
        {
            BackColor = UI.Theme.SurfaceContainerLow,
            SizingGrip = false,
            Padding = new Padding(8, 0, 8, 0)
        };
        _statusStrip.Items.AddRange(new ToolStripItem[] { _statusBackend, _statusSystem, _statusVersion });
        Controls.Add(_statusStrip);

        // Header bar with standalone Settings button (replaces the four-tab
        // TopTabStrip — Launch/Sessions/Settings/Logs were not true tabs in a
        // single-page dashboard, so the strip created false expectations).
        var headerPanel = new Panel
        {
            Dock = DockStyle.Top,
            Height = 40,
            BackColor = UI.Theme.WindowBg
        };
        var settingsBtn = new Button
        {
            Text = "⚙",
            Font = new Font("Segoe UI", 12f),
            Size = new Size(32, 32),
            FlatStyle = FlatStyle.Flat,
            Anchor = AnchorStyles.Top | AnchorStyles.Right,
            BackColor = UI.Theme.WindowBg,
            ForeColor = UI.Theme.TextSecondary,
            Cursor = Cursors.Hand
        };
        settingsBtn.FlatAppearance.BorderSize = 0;
        settingsBtn.Click += (_, _) => SettingsRequested?.Invoke();
        headerPanel.Controls.Add(settingsBtn);
        settingsBtn.Location = new Point(headerPanel.Width - 36, 4);
        Controls.Add(headerPanel);

        UpdateProxyStatus();
        RefreshSessions();
        RefreshEvents();
    }

    private string LabelForProxy(string proxyId, string fallback)
    {
        var p = _config.Proxies.FirstOrDefault(x => x.Id == proxyId);
        if (p != null && !string.IsNullOrEmpty(p.Name)) return p.Name;
        var backend = _config.RoutingBackends.FirstOrDefault(b => b.ProxyId == proxyId);
        if (backend != null && !string.IsNullOrEmpty(backend.Name)) return $"{backend.Name} {p?.Port ?? 0}";
        return fallback;
    }

    private async void HandleDrop(string exePath, string mode)
    {
        var name = Path.GetFileNameWithoutExtension(exePath);
        var proxyId = mode switch
        {
            "proxy-10708" => "p10708",
            "proxy-10808" => "p10808",
            _ => null
        };

        bool isPersistent = false;

        if (mode != "direct" && proxyId != null)
        {
            var label = LabelForProxy(proxyId, proxyId);
            // PR5a: typed three-way dialog replaces YesNoCancel MessageBox. Same
            // semantics — Persistent saves to proxyswitch.json + app-config.json
            // (may UAC-restart ProxiFyre); SessionOnly writes app-config.json
            // and removes the route when the session exits.
            using var dlg = new LaunchConfirmDialog(label);
            dlg.ShowDialog(this);

            if (dlg.Choice == LaunchChoice.Cancel) return;
            isPersistent = (dlg.Choice == LaunchChoice.Persistent);
        }

        // Run LaunchGeneric on a background thread — it does WMI snapshots, file IO,
        // optional UAC service restart, and ServiceController waits. None of that
        // belongs on the UI thread. SessionsChanged event listeners use InvokeRequired
        // to marshal back to the UI thread, so background-thread invocation is safe.
        try
        {
            await Task.Run(() => _sessions.LaunchGeneric(exePath, name, mode, proxyId, isPersistent));
        }
        catch (Exception ex)
        {
            Logger.Error($"HandleDrop background launch failed: {ex.Message}");
        }
    }

    private void LaunchPinned(AppConfig app)
    {
        _sessions.LaunchBrowser(app, app.Mode, app.ProxyId);
    }

    private void OnSessionsChanged()
    {
        if (InvokeRequired) { Invoke(OnSessionsChanged); return; }
        RefreshSessions();
    }

    private void OnEventAdded()
    {
        if (InvokeRequired) { Invoke(OnEventAdded); return; }
        RefreshEvents();
    }

    private void OnProxyStatusChanged()
    {
        if (InvokeRequired) { Invoke(OnProxyStatusChanged); return; }
        UpdateProxyStatus();
    }

    private void RefreshUI()
    {
        RefreshSessions();
        UpdateProxyStatus();
        UpdateBackendStatus();
        UpdateRestartButton(_restartBackendBtn);
        UpdateStatusStrip();
    }

    private void UpdateStatusStrip()
    {
        if (_statusStrip == null || _statusStrip.IsDisposed) return;

        // Backend state mapping (short labels for the bottom strip — full text is
        // in the Proxies & Backend panel).
        string backendText;
        Color backendColor = UI.Theme.TextSecondary;
        if (_backend == null)
        {
            backendText = "Backend: —";
        }
        else
        {
            var st = _backend.GetStatus();
            switch (st.State)
            {
                case "running":
                    backendText = "Backend: Connected";
                    backendColor = UI.Theme.StatusActiveGreen;
                    break;
                case "stopped":
                    backendText = "Backend: Stopped";
                    backendColor = UI.Theme.StatusPendingAmber;
                    break;
                case "service-not-installed":
                    backendText = "Backend: Not Installed";
                    backendColor = UI.Theme.StatusFailedRed;
                    break;
                case "exe-missing":
                    backendText = "Backend: Exe Missing";
                    backendColor = UI.Theme.StatusFailedRed;
                    break;
                case "not-configured":
                    backendText = "Backend: Not Configured";
                    backendColor = UI.Theme.StatusPendingAmber;
                    break;
                case "disabled":
                    backendText = "Backend: Disabled";
                    break;
                default:
                    backendText = $"Backend: {st.State}";
                    break;
            }
        }
        _statusBackend.Text = backendText;
        _statusBackend.ForeColor = backendColor;

        // System state — derive from session counts + backend running.
        var sessions = _sessions.GetSessions();
        int active = sessions.Count(s => s.Status is "running" or "running-via-child" or "running-via-correlated" or "waiting-for-restart");
        string systemText = active > 0
            ? $"System: Routed · {active} active session{(active == 1 ? "" : "s")}"
            : "System: Idle";
        _statusSystem.Text = systemText;
        _statusSystem.ForeColor = active > 0 ? UI.Theme.StatusActiveGreen : UI.Theme.TextSecondary;
    }

    /// <summary>
    /// Build a Stitch-style section: a single-line header label on top of the
    /// content, separated by a thin divider. Replaces the legacy GroupBox look —
    /// the title-overlapping-border style was the most "dated" element of the
    /// v0.7.3 visual.
    /// </summary>
    private static Panel BuildSection(string title, Control content)
    {
        var outer = new Panel
        {
            Dock = DockStyle.Fill,
            BackColor = UI.Theme.PanelBg,
            Padding = new Padding(0),
            Margin = new Padding(4)
        };

        var header = new Label
        {
            Text = title,
            Font = UI.Theme.SectionHeader,
            ForeColor = UI.Theme.TextPrimary,
            BackColor = UI.Theme.PanelBg,
            Dock = DockStyle.Top,
            Height = 28,
            TextAlign = ContentAlignment.MiddleLeft,
            Padding = new Padding(10, 4, 8, 0)
        };

        var divider = new Panel
        {
            Dock = DockStyle.Top,
            Height = 1,
            BackColor = UI.Theme.BorderSubtle
        };

        var body = new Panel
        {
            Dock = DockStyle.Fill,
            BackColor = UI.Theme.PanelBg,
            Padding = new Padding(4)
        };
        content.Dock = DockStyle.Fill;
        body.Controls.Add(content);

        // Add order matters: Fill first, then Tops in reverse-stack order.
        outer.Controls.Add(body);
        outer.Controls.Add(divider);
        outer.Controls.Add(header);
        return outer;
    }

    private void UpdateRestartButton(Button btn)
    {
        if (btn == null) return;
        var be = _config.TransparentBackend;
        // Show the button whenever backend is enabled and exe path is reasonable —
        // user can always trigger a restart, including when service is currently stopped.
        btn.Visible = _backend != null
            && be.Enabled
            && be.Type == "proxifyre"
            && !string.IsNullOrEmpty(be.Exe);

        // When live service holds rules that were already removed from disk, the
        // user is one restart away from a clean state. Relabel + tint to make that
        // explicit. AutoSize handles width — no manual Width writes (#9 in opus_review_v0.7.1).
        if (btn.Visible && _backend != null && _backend.HasStaleLiveRoutes)
        {
            btn.Text = "Restart ProxiFyre — unload stale rules (UAC)";
            btn.BackColor = Color.FromArgb(254, 215, 170);
        }
        else if (btn.Visible)
        {
            btn.Text = "Restart ProxiFyre (UAC)";
            btn.BackColor = SystemColors.Control;
        }
    }

    private DataGridView BuildSessionGrid()
    {
        var grid = new DataGridView
        {
            Dock = DockStyle.Fill,
            AutoGenerateColumns = false,
            ReadOnly = true,
            AllowUserToAddRows = false,
            AllowUserToDeleteRows = false,
            AllowUserToResizeRows = false,
            AllowUserToResizeColumns = true,
            AllowUserToOrderColumns = false,
            SelectionMode = DataGridViewSelectionMode.FullRowSelect,
            MultiSelect = false,
            RowHeadersVisible = false,
            BorderStyle = BorderStyle.None,
            GridColor = UI.Theme.BorderSubtle,
            BackgroundColor = UI.Theme.PanelBg,
            Font = UI.Theme.BodyFont,
            EnableHeadersVisualStyles = false,
            CellBorderStyle = DataGridViewCellBorderStyle.SingleHorizontal,
            ColumnHeadersBorderStyle = DataGridViewHeaderBorderStyle.Single,
            ColumnHeadersHeightSizeMode = DataGridViewColumnHeadersHeightSizeMode.DisableResizing,
            ColumnHeadersHeight = 32,
            RowTemplate = { Height = 36 }
        };
        grid.ColumnHeadersDefaultCellStyle.BackColor = UI.Theme.SurfaceContainerLow;
        grid.ColumnHeadersDefaultCellStyle.ForeColor = UI.Theme.TextSecondary;
        grid.ColumnHeadersDefaultCellStyle.Font = UI.Theme.BodySemibold;
        grid.ColumnHeadersDefaultCellStyle.Padding = new Padding(8, 4, 8, 4);
        grid.ColumnHeadersDefaultCellStyle.SelectionBackColor = UI.Theme.SurfaceContainerLow;
        grid.ColumnHeadersDefaultCellStyle.SelectionForeColor = UI.Theme.TextSecondary;
        grid.DefaultCellStyle.BackColor = UI.Theme.PanelBg;
        grid.DefaultCellStyle.ForeColor = UI.Theme.TextPrimary;
        // Disable selection highlight (we color rows by status instead).
        grid.DefaultCellStyle.SelectionBackColor = UI.Theme.PanelBg;
        grid.DefaultCellStyle.SelectionForeColor = UI.Theme.TextPrimary;
        grid.DefaultCellStyle.Padding = new Padding(8, 4, 8, 4);
        grid.DefaultCellStyle.Alignment = DataGridViewContentAlignment.MiddleLeft;

        grid.Columns.Add(new DataGridViewTextBoxColumn
        {
            Name = "App", HeaderText = "App", FillWeight = 22,
            AutoSizeMode = DataGridViewAutoSizeColumnMode.Fill
        });
        // App name in bold semibold — set on the column default cell style.
        grid.Columns["App"]!.DefaultCellStyle.Font = UI.Theme.BodySemibold;

        grid.Columns.Add(new DataGridViewTextBoxColumn
        {
            Name = "Status", HeaderText = "Status", FillWeight = 18,
            AutoSizeMode = DataGridViewAutoSizeColumnMode.Fill
        });
        grid.Columns.Add(new DataGridViewTextBoxColumn
        {
            Name = "Routing", HeaderText = "Routing", FillWeight = 22,
            AutoSizeMode = DataGridViewAutoSizeColumnMode.Fill
        });
        grid.Columns.Add(new DataGridViewTextBoxColumn
        {
            Name = "PID", HeaderText = "PID", FillWeight = 12,
            AutoSizeMode = DataGridViewAutoSizeColumnMode.Fill
        });
        grid.Columns.Add(new DataGridViewTextBoxColumn
        {
            Name = "Actions", HeaderText = "Actions", FillWeight = 26,
            AutoSizeMode = DataGridViewAutoSizeColumnMode.Fill
        });

        grid.CellPainting += OnSessionCellPainting;
        grid.CellMouseClick += OnSessionCellMouseClick;
        grid.CellMouseMove += OnSessionCellMouseMove;
        return grid;
    }

    private void RefreshSessions()
    {
        if (_sessionGrid == null || _sessionGrid.IsDisposed) return;
        var sessions = _sessions.GetSessions();

        // Full clear+rebuild only when row count differs — otherwise update in place
        // so cells don't flash mid-scroll and selection isn't lost.
        if (_sessionGrid.Rows.Count != sessions.Count)
        {
            _sessionGrid.Rows.Clear();
            foreach (var s in sessions)
                AddSessionRow(s);
            return;
        }
        for (int i = 0; i < sessions.Count; i++)
            UpdateSessionRow(i, sessions[i]);
    }

    private void AddSessionRow(Models.LaunchSession session)
    {
        var idx = _sessionGrid.Rows.Add(
            session.Name,
            $"{FormatStatus(session)} · {session.DurationText}",
            FormatRouting(session),
            ComputePidText(session),
            ComputeActionsText(session));
        _sessionGrid.Rows[idx].Tag = session;
    }

    private void UpdateSessionRow(int rowIdx, Models.LaunchSession session)
    {
        var row = _sessionGrid.Rows[rowIdx];
        // Re-attach in case the order shifted (RemoveSession reorders the list).
        row.Tag = session;
        row.Cells["App"].Value = session.Name;
        row.Cells["Status"].Value = $"{FormatStatus(session)} · {session.DurationText}";
        row.Cells["Routing"].Value = FormatRouting(session);
        row.Cells["PID"].Value = ComputePidText(session);
        row.Cells["Actions"].Value = ComputeActionsText(session);
        _sessionGrid.InvalidateRow(rowIdx);
    }

    private static string ComputePidText(Models.LaunchSession session)
    {
        var liveCount = session.LiveProcessCount;
        if (liveCount > 1) return $"Procs: {liveCount}";
        var pid = session.LiveProcessId ?? session.RootProcessId;
        return pid.HasValue ? $"PID {pid.Value}" : "—";
    }

    /// <summary>
    /// Compute the action labels for a session row. Order is meaningful — it
    /// drives the rendered text segments and the click hit-test in
    /// OnSessionCellMouseClick. Mirrors the logic that used to be in CreateSessionCard.
    /// </summary>
    private List<string> ComputeActionSegments(Models.LaunchSession session)
    {
        var list = new List<string>();
        bool isLive = session.Status is "running" or "running-via-child" or "running-via-correlated" or "waiting-for-restart";
        bool isChecking = session.Status == "checking-correlated";

        if (isLive)
        {
            list.Add("Stop");
            bool backendReachable = _backend != null
                && _config.TransparentBackend.Enabled
                && session.RoutingStatus is "proxifyre-route-active"
                                          or "proxifyre-route-pending"
                                          or "proxifyre-route-needs-restart"
                                          or "proxifyre-route-launching"
                                          or "proxifyre-route-restarting";
            bool hasLiveChild = session.Processes.Any(p => p.ExitedAt == null && p.Role != "root");

            if (backendReachable && hasLiveChild)
                list.Add("Route Child");
            else if (session.RoutingStatus == "proxifyre-route-failed")
            {
                list.Add("Retry");
                list.Add("Copy Hint");
            }
            else if (session.RoutingStatus == "external-routing-required")
                list.Add("Copy Hint");
        }
        else if (isChecking)
        {
            list.Add("Detecting…");
        }
        else
        {
            list.Add("Remove");
        }
        return list;
    }

    private string ComputeActionsText(Models.LaunchSession session)
        => string.Join("   ·   ", ComputeActionSegments(session));

    /// <summary>
    /// Custom-paint each cell so row backgrounds reflect session.Status and the
    /// Routing column text uses the routing-status palette. Actions cell text is
    /// drawn in PrimaryBlue underline style (link-like) — but the actual hit-test
    /// happens in OnSessionCellMouseClick, not here.
    /// </summary>
    private void OnSessionCellPainting(object? sender, DataGridViewCellPaintingEventArgs e)
    {
        if (e.RowIndex < 0 || e.ColumnIndex < 0) return;
        if (e.RowIndex >= _sessionGrid.Rows.Count) return;
        var row = _sessionGrid.Rows[e.RowIndex];
        var session = row.Tag as Models.LaunchSession;
        if (session == null) return;

        // Background — tint per session.Status.
        var bg = UI.Theme.SessionRowBg(session.Status);
        using (var brush = new SolidBrush(bg))
            e.Graphics!.FillRectangle(brush, e.CellBounds);
        // Bottom border (very subtle 1px between rows).
        using (var pen = new Pen(UI.Theme.BorderSubtle, 1f))
            e.Graphics.DrawLine(pen,
                e.CellBounds.Left, e.CellBounds.Bottom - 1,
                e.CellBounds.Right, e.CellBounds.Bottom - 1);

        // Text rendering: pick color per column, then let TextRenderer draw it.
        var colName = _sessionGrid.Columns[e.ColumnIndex].Name;
        var text = e.Value?.ToString() ?? "";
        var font = e.CellStyle?.Font ?? UI.Theme.BodyFont;
        var fore = UI.Theme.TextPrimary;
        var flags = TextFormatFlags.SingleLine | TextFormatFlags.VerticalCenter | TextFormatFlags.Left | TextFormatFlags.EndEllipsis;

        switch (colName)
        {
            case "App":
                font = UI.Theme.BodySemibold;
                fore = UI.Theme.TextPrimary;
                break;
            case "Status":
                fore = UI.Theme.TextPrimary;
                break;
            case "Routing":
                fore = UI.Theme.RoutingColor(session.RoutingStatus);
                font = UI.Theme.BodySemibold;
                break;
            case "PID":
                fore = UI.Theme.TextSecondary;
                font = UI.Theme.ConsoleFont;
                break;
            case "Actions":
                // Render in PrimaryBlue with underline (link-like). "Detecting…"
                // is shown faded gray instead of blue. Fonts are cached fields
                // (opus review #4); no per-paint allocation.
                if (session.Status == "checking-correlated")
                {
                    fore = UI.Theme.StatusInfoBlue;
                    font = _actionsItalicFont;
                }
                else
                {
                    fore = UI.Theme.PrimaryBlue;
                    font = _actionsUnderlineFont;
                }
                break;
        }

        var pad = e.CellStyle?.Padding ?? new Padding(8, 4, 8, 4);
        var textRect = new Rectangle(
            e.CellBounds.X + pad.Left,
            e.CellBounds.Y + pad.Top,
            e.CellBounds.Width - pad.Horizontal,
            e.CellBounds.Height - pad.Vertical);
        TextRenderer.DrawText(e.Graphics!, text, font, textRect, fore, flags);

        e.Handled = true;
    }

    private void OnSessionCellMouseClick(object? sender, DataGridViewCellMouseEventArgs e)
    {
        if (e.RowIndex < 0 || e.ColumnIndex < 0) return;
        if (_sessionGrid.Columns[e.ColumnIndex].Name != "Actions") return;
        var row = _sessionGrid.Rows[e.RowIndex];
        var session = row.Tag as Models.LaunchSession;
        if (session == null) return;

        var segments = ComputeActionSegments(session);
        if (segments.Count == 0) return;

        // Mouse e.Location is relative to the cell. Walk through segment widths
        // to find which segment the click landed on. Use the cached underline
        // font so the hit-test measures with the same font CellPainting drew —
        // mixing fonts (sep measured with BodyFont, segments with underline)
        // would let click position drift between segments (opus review #2).
        var pad = _sessionGrid.DefaultCellStyle.Padding;
        int x = pad.Left;
        // The "    ·    " separator used in ComputeActionsText.
        const string sep = "   ·   ";
        var underlineFont = _actionsUnderlineFont;
        var sepWidth = TextRenderer.MeasureText(sep, underlineFont).Width;

        string? hit = null;
        for (int i = 0; i < segments.Count; i++)
        {
            var w = TextRenderer.MeasureText(segments[i], underlineFont).Width;
            var left = x;
            var right = x + w;
            if (e.Location.X >= left && e.Location.X <= right)
            {
                hit = segments[i];
                break;
            }
            x = right + sepWidth;
        }

        if (hit == null) return;
        DispatchSessionAction(session, hit);
    }

    private void OnSessionCellMouseMove(object? sender, DataGridViewCellMouseEventArgs e)
    {
        if (e.ColumnIndex < 0 || e.RowIndex < 0) return;
        var col = _sessionGrid.Columns[e.ColumnIndex].Name;
        _sessionGrid.Cursor = (col == "Actions") ? Cursors.Hand : Cursors.Default;
    }

    private async void DispatchSessionAction(Models.LaunchSession session, string action)
    {
        try
        {
            switch (action)
            {
                case "Stop":
                    if (MessageBox.Show($"Stop {session.Name}?", "Confirm",
                            MessageBoxButtons.YesNo) != DialogResult.Yes) return;
                    await Task.Run(() => _sessions.StopSession(session));
                    break;

                case "Route Child":
                    await HandleRouteChildAction(session);
                    break;

                case "Retry":
                    if (string.IsNullOrEmpty(session.ProxyId)) return;
                    await Task.Run(() => _sessions.RouteDetectedChild(session));
                    break;

                case "Copy Hint":
                    CopyRuleHint(session);
                    break;

                case "Remove":
                    _sessions.RemoveSession(session);
                    break;

                case "Detecting…":
                    // no-op
                    break;
            }
        }
        catch (Exception ex)
        {
            Logger.Error($"DispatchSessionAction({action}) failed: {ex.Message}");
        }
    }

    private async Task HandleRouteChildAction(Models.LaunchSession session)
    {
        // Build the candidate list once. Group by ExePath — ProxiFyre matches by
        // executable path, so multiple PIDs of the same exe collapse into one rule.
        var liveChildren = session.Processes
            .Where(p => p.ExitedAt == null && p.Role != "root")
            .Where(p => !string.IsNullOrEmpty(p.ExecutablePath))
            .ToList();
        var distinctPaths = liveChildren
            .Select(p => p.ExecutablePath!)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (distinctPaths.Count == 0) return;

        IReadOnlyList<string> toRoute;
        if (distinctPaths.Count == 1)
        {
            var only = liveChildren[0];
            if (MessageBox.Show(
                    $"Add {only.Name} to ProxiFyre route for {session.ProxyId}?\n\n" +
                    $"ProxiFyre routing is executable-based. Existing connections may not move — restart the app if routing does not change.",
                    "Route Detected Child", MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes)
                return;
            toRoute = new[] { only.ExecutablePath! };
        }
        else
        {
            using var dlg = new RouteChildDialog(session, liveChildren);
            if (dlg.ShowDialog(this) != DialogResult.OK) return;
            if (dlg.SelectedExePaths.Count == 0) return;
            toRoute = dlg.SelectedExePaths;
        }

        await Task.Run(() => _sessions.RouteDetectedChildren(session, toRoute));
    }

    private static string FormatStatus(LaunchSession session) => session.Status switch
    {
        "running" => "Running",
        "running-via-child" => "Via child",
        "running-via-correlated" => "Via correlated",
        "checking-correlated" => "Checking...",
        "waiting-for-restart" => "Waiting for restart",
        "exited" => "Exited",
        "failed" => "Failed",
        _ => session.Status
    };

    private static string FormatRouting(LaunchSession session) => session.RoutingStatus switch
    {
        "direct" => "Direct",
        "browser-proxy-active" => "Browser proxy",
        "external-routing-required" => "External router",
        "proxifyre-route-launching" => "Setting up...",
        "proxifyre-route-restarting" => "Applying (UAC)",
        "proxifyre-route-active" => "ProxiFyre active",
        "proxifyre-route-pending" => "ProxiFyre pending",
        "proxifyre-route-needs-restart" => "Restart needed",
        "proxifyre-route-failed" => "ProxiFyre failed",
        _ => session.RoutingStatus
    };

    private void CopyRuleHint(LaunchSession session)
    {
        try
        {
            var hint = _hintService.BuildHintForSession(session);
            Clipboard.SetText(hint);
            _events.Add("RuleHintCopied", $"{session.Name}: rule hint copied to clipboard");
            MessageBox.Show(
                $"Rule hint copied to clipboard.\n\n" +
                $"Paste it into your external router's rules section ({GetRuleFormatFor(session)}). " +
                $"ProxySwitch does NOT apply this automatically.",
                "Rule Hint Copied", MessageBoxButtons.OK, MessageBoxIcon.Information);
        }
        catch (Exception ex)
        {
            Logger.Error($"Copy rule hint failed: {ex.Message}");
            MessageBox.Show($"Failed to copy hint:\n{ex.Message}", "Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    private string GetRuleFormatFor(LaunchSession session)
    {
        var backend = _config.RoutingBackends.FirstOrDefault(b => b.ProxyId == session.ProxyId);
        return backend?.RuleFormat?.ToLowerInvariant() switch
        {
            "v2ray" => "v2ray routing.rules",
            _ => "Clash rules"
        };
    }

    private void RefreshEvents()
    {
        _eventList.Items.Clear();
        // Store AppEvent objects (not strings) so OnEventDrawItem can color by Type.
        foreach (var evt in _events.GetRecent(30))
            _eventList.Items.Add(evt);
        if (_eventList.Items.Count > 0)
            _eventList.TopIndex = _eventList.Items.Count - 1;
    }

    /// <summary>
    /// Dark-console paint for Event list — timestamp dim, [Type] colored per
    /// EventLevelColor heuristic, message bright. Matches the Stitch design
    /// where the Events panel is the only dark surface in the dashboard.
    /// </summary>
    private void OnEventDrawItem(object? sender, DrawItemEventArgs e)
    {
        if (e.Index < 0 || e.Index >= _eventList.Items.Count) return;
        var g = e.Graphics;

        // Background — solid EventsBg. We deliberately ignore the selection
        // highlight (this list is read-only and selection has no semantic).
        using (var bg = new SolidBrush(UI.Theme.EventsBg))
            g.FillRectangle(bg, e.Bounds);

        var item = _eventList.Items[e.Index];
        if (item is not Services.AppEvent evt)
        {
            // Fallback for string items (shouldn't happen post-PR3).
            TextRenderer.DrawText(g, item?.ToString() ?? "", UI.Theme.ConsoleFont,
                e.Bounds, UI.Theme.EventsFg,
                TextFormatFlags.SingleLine | TextFormatFlags.VerticalCenter | TextFormatFlags.Left);
            return;
        }

        var tsText = $"[{evt.Timestamp:HH:mm:ss}] ";
        var typeText = $"[{evt.Type}] ";
        var msgText = evt.Message;

        var flags = TextFormatFlags.SingleLine | TextFormatFlags.VerticalCenter | TextFormatFlags.Left | TextFormatFlags.NoPrefix;
        int x = e.Bounds.X + 6;
        var typeColor = UI.Theme.EventLevelColor(evt.Type);
        var tsSize = TextRenderer.MeasureText(tsText, UI.Theme.ConsoleFont);
        var typeSize = TextRenderer.MeasureText(typeText, UI.Theme.ConsoleFont);

        TextRenderer.DrawText(g, tsText, UI.Theme.ConsoleFont,
            new Rectangle(x, e.Bounds.Y, tsSize.Width, e.Bounds.Height),
            UI.Theme.TextSecondary, flags);
        x += tsSize.Width;

        TextRenderer.DrawText(g, typeText, UI.Theme.ConsoleFont,
            new Rectangle(x, e.Bounds.Y, typeSize.Width, e.Bounds.Height),
            typeColor, flags);
        x += typeSize.Width;

        var msgRect = new Rectangle(x, e.Bounds.Y, e.Bounds.Right - x - 6, e.Bounds.Height);
        TextRenderer.DrawText(g, msgText, UI.Theme.ConsoleFont, msgRect,
            UI.Theme.EventsFg, flags | TextFormatFlags.EndEllipsis);
    }

    private void UpdateProxyStatus()
    {
        var lines = new List<string>();
        foreach (var proxy in _config.Proxies)
        {
            var status = _monitor.GetStatus(proxy.Host, proxy.Port);
            if (status != null)
            {
                var latency = status.LatencyMs.HasValue ? $"{status.LatencyMs}ms" : "timeout";
                var lastCheck = status.LastCheckedAt?.ToString("HH:mm:ss") ?? "never";
                lines.Add($"{proxy.Name}: {status.Status.ToUpper()} ({latency}, checked {lastCheck})");
            }
        }
        _proxyStatusLabel.Text = string.Join("\n", lines);
    }

    private void UpdateBackendStatus()
    {
        if (_backend == null)
        {
            _backendStatusLabel.Text = "Backend: not initialized";
            _backendStatusLabel.ForeColor = Color.DimGray;
            return;
        }
        var s = _backend.GetStatus();
        var prefix = _config.TransparentBackend.Type switch
        {
            "proxifyre" => "ProxiFyre",
            _ => "Backend"
        };
        var shortPath = string.IsNullOrEmpty(s.ConfigPath) ? "" : Path.GetFileName(s.ConfigPath);
        switch (s.State)
        {
            case "disabled":
                _backendStatusLabel.Text = $"{prefix}: disabled (generic apps fall back to external router)";
                _backendStatusLabel.ForeColor = Color.DimGray;
                break;
            case "not-configured":
                _backendStatusLabel.Text = $"{prefix}: not configured — {s.Message}";
                _backendStatusLabel.ForeColor = Color.FromArgb(146, 64, 14);
                break;
            case "exe-missing":
                _backendStatusLabel.Text = $"{prefix}: exe missing — {s.Message}";
                _backendStatusLabel.ForeColor = Color.Firebrick;
                break;
            case "service-not-installed":
                _backendStatusLabel.Text = $"{prefix}: service not installed (run admin: ProxiFyre.exe install)";
                _backendStatusLabel.ForeColor = Color.Firebrick;
                break;
            case "running":
                var mode = _config.TransparentBackend.ManageService ? "managed" : "manual";
                _backendStatusLabel.Text = $"{prefix}: running — {mode} mode  ({shortPath})";
                _backendStatusLabel.ForeColor = Color.FromArgb(22, 101, 52);
                break;
            case "stopped":
                _backendStatusLabel.Text = $"{prefix}: stopped — start service for routing to take effect  ({shortPath})";
                _backendStatusLabel.ForeColor = Color.FromArgb(180, 83, 9);
                break;
            default:
                _backendStatusLabel.Text = $"{prefix}: {s.State}";
                _backendStatusLabel.ForeColor = Color.DimGray;
                break;
        }
    }

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        _refreshTimer?.Stop();
        _refreshTimer?.Dispose();
        _actionsUnderlineFont?.Dispose();
        _actionsItalicFont?.Dispose();
        UnsubscribeEvents();
        base.OnFormClosing(e);
    }

    private void OnCorrelatedCandidatesFound(LaunchSession session, List<HandoffCandidate> candidates)
    {
        if (InvokeRequired) { Invoke(() => OnCorrelatedCandidatesFound(session, candidates)); return; }

        if (_correlatedDialogOpen)
        {
            _pendingCorrelated.Enqueue((session, candidates));
            return;
        }

        ShowCorrelatedDialog(session, candidates);
    }

    private void ShowCorrelatedDialog(LaunchSession session, List<HandoffCandidate> candidates)
    {
        _correlatedDialogOpen = true;
        try
        {
            using var dlg = new CorrelatedHandoffDialog(session, candidates);
            var result = dlg.ShowDialog(this);
            if (result == DialogResult.OK && dlg.SelectedCandidate != null)
            {
                _sessions.AttachCorrelated(session, dlg.SelectedCandidate, ProcessTrackingConfidence.UserSelected, autoAttached: false);
            }
            else
            {
                _sessions.IgnoreCorrelated(session);
            }
        }
        finally
        {
            _correlatedDialogOpen = false;
            if (_pendingCorrelated.Count > 0)
            {
                var next = _pendingCorrelated.Dequeue();
                BeginInvoke(() => ShowCorrelatedDialog(next.Session, next.Candidates));
            }
        }
    }
}
