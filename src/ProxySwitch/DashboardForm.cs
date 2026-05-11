using System.Diagnostics;
using System.Text.Json;
using ProxySwitch.Controls;
using ProxySwitch.Models;
using ProxySwitch.Services;

namespace ProxySwitch;

public class DashboardForm : Form
{
    private readonly ProxyConfig _config;
    private readonly PortMonitor _monitor;
    private readonly SessionManager _sessions;
    private readonly EventStore _events;
    private readonly AppLauncher _launcher;
    private readonly RoutingHintService _hintService;
    private readonly ProxiFyreBackend? _backend;

    private FlowLayoutPanel _sessionPanel = null!;
    private ListBox _eventList = null!;
    private Label _proxyStatusLabel = null!;
    private Label _backendStatusLabel = null!;
    private Button _restartBackendBtn = null!;
    private System.Windows.Forms.Timer _refreshTimer = null!;
    private bool _correlatedDialogOpen;
    private readonly Queue<(LaunchSession Session, List<HandoffCandidate> Candidates)> _pendingCorrelated = new();

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

        BuildUI();

        _sessions.SessionsChanged += OnSessionsChanged;
        _sessions.CorrelatedCandidatesFound += OnCorrelatedCandidatesFound;
        _events.EventAdded += OnEventAdded;
        _monitor.StatusChanged += OnProxyStatusChanged;

        _refreshTimer = new System.Windows.Forms.Timer { Interval = 1000 };
        _refreshTimer.Tick += (_, _) => RefreshUI();
        _refreshTimer.Start();
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
            AccentColor = Color.FromArgb(120, 120, 120),
            Dock = DockStyle.Fill,
            Margin = new Padding(4)
        };
        directZone.FileDropped += path => HandleDrop(path, "direct");
        zonePanel.Controls.Add(directZone, 0, 0);

        var z10708Label = LabelForProxy("p10708", "10708");
        var z10708 = new LaunchZoneControl
        {
            Title = z10708Label,
            Subtitle = "External router decides",
            Mode = "proxy-10708",
            AccentColor = Color.FromArgb(34, 197, 94),
            Dock = DockStyle.Fill,
            Margin = new Padding(4)
        };
        z10708.FileDropped += path => HandleDrop(path, "proxy-10708");
        zonePanel.Controls.Add(z10708, 1, 0);

        var z10808Label = LabelForProxy("p10808", "10808");
        var z10808 = new LaunchZoneControl
        {
            Title = z10808Label,
            Subtitle = "External router decides",
            Mode = "proxy-10808",
            AccentColor = Color.FromArgb(59, 130, 246),
            Dock = DockStyle.Fill,
            Margin = new Padding(4)
        };
        z10808.FileDropped += path => HandleDrop(path, "proxy-10808");
        zonePanel.Controls.Add(z10808, 2, 0);
        mainLayout.Controls.Add(zonePanel, 0, 0);

        // Row 1: Pinned Apps
        var pinnedPanel = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            FlowDirection = FlowDirection.LeftToRight,
            AutoScroll = true,
            WrapContents = true
        };
        foreach (var app in _config.Apps)
        {
            var btn = new Button
            {
                Text = app.Name,
                AutoSize = true,
                Padding = new Padding(8, 4, 8, 4),
                Margin = new Padding(4)
            };
            var a = app;
            btn.Click += (_, _) => LaunchPinned(a);
            pinnedPanel.Controls.Add(btn);
        }
        mainLayout.Controls.Add(pinnedPanel, 0, 1);

        // Row 2: Sessions
        var sessionGroup = new GroupBox { Text = "Sessions", Dock = DockStyle.Fill, Padding = new Padding(4) };
        _sessionPanel = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            FlowDirection = FlowDirection.TopDown,
            AutoScroll = true,
            WrapContents = false
        };
        sessionGroup.Controls.Add(_sessionPanel);
        mainLayout.Controls.Add(sessionGroup, 0, 2);

        // Row 3: Proxy Status + Events
        var bottomPanel = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 2,
            RowCount = 1
        };
        bottomPanel.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 35f));
        bottomPanel.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 65f));

        var proxyGroup = new GroupBox { Text = "Proxies & Backend", Dock = DockStyle.Fill };
        var proxyLayout = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 3,
            Padding = new Padding(4)
        };
        proxyLayout.RowStyles.Add(new RowStyle(SizeType.Percent, 55f));
        proxyLayout.RowStyles.Add(new RowStyle(SizeType.Percent, 30f));
        proxyLayout.RowStyles.Add(new RowStyle(SizeType.Absolute, 28f));

        _proxyStatusLabel = new Label
        {
            Dock = DockStyle.Fill,
            Text = "Checking...",
        };
        proxyLayout.Controls.Add(_proxyStatusLabel, 0, 0);

        _backendStatusLabel = new Label
        {
            Dock = DockStyle.Fill,
            Text = "Backend: —",
            Font = new Font(Font.FontFamily, 8.5f),
            ForeColor = Color.DimGray
        };
        proxyLayout.Controls.Add(_backendStatusLabel, 0, 1);

        // Restart ProxiFyre button — visible whenever backend type is proxifyre + enabled
        var restartBackendBtn = new Button
        {
            Text = "Restart ProxiFyre (UAC)",
            AutoSize = false,
            Height = 24,
            Anchor = AnchorStyles.Left | AnchorStyles.Top,
            Visible = false
        };
        restartBackendBtn.Click += (_, _) =>
        {
            if (_backend == null) return;
            var ok = _backend.RestartServiceElevated();
            UpdateBackendStatus();
            UpdateRestartButton(restartBackendBtn);
            if (!ok)
                _events.Add("RestartFailed", "ProxiFyre restart failed or declined");
        };
        _restartBackendBtn = restartBackendBtn;
        proxyLayout.Controls.Add(restartBackendBtn, 0, 2);

        proxyGroup.Controls.Add(proxyLayout);
        bottomPanel.Controls.Add(proxyGroup, 0, 0);

        var eventGroup = new GroupBox { Text = "Events", Dock = DockStyle.Fill };
        _eventList = new ListBox
        {
            Dock = DockStyle.Fill,
            Font = new Font("Consolas", 9f)
        };
        eventGroup.Controls.Add(_eventList);
        bottomPanel.Controls.Add(eventGroup, 1, 0);

        mainLayout.Controls.Add(bottomPanel, 0, 3);

        Controls.Add(mainLayout);
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

    private void HandleDrop(string exePath, string mode)
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
            // YesNo+Cancel = three-way choice:
            //   Yes    → save the route into proxyswitch.json (next ProxySwitch start auto-applies it)
            //   No     → just this session (tmp, in-memory only)
            //   Cancel → don't launch
            var result = MessageBox.Show(
                $"Launch {name} via {label}?\n\n" +
                $"Yes  = Save route permanently (auto-applies next time ProxySwitch starts)\n" +
                $"No   = Just this session (route disappears when you close ProxySwitch)\n" +
                $"Cancel = don't launch\n\n" +
                $"⚠ For launcher-style apps (Steam, Epic, game clients), child processes are " +
                $"not auto-tracked — restart-after-handoff still needed.",
                "Confirm Launch",
                MessageBoxButtons.YesNoCancel,
                MessageBoxIcon.Question);

            if (result == DialogResult.Cancel) return;
            isPersistent = (result == DialogResult.Yes);
        }

        _sessions.LaunchGeneric(exePath, name, mode, proxyId, isPersistent);
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
    }

    private void RefreshSessions()
    {
        var sessions = _sessions.GetSessions();
        bool structureMatches = _sessionPanel.Controls.Count == sessions.Count;
        if (structureMatches)
        {
            for (int i = 0; i < sessions.Count; i++)
            {
                var expectedTag = GetSessionTag(sessions[i]);
                if ((_sessionPanel.Controls[i].Tag as string) != expectedTag)
                {
                    structureMatches = false;
                    break;
                }
            }
        }

        if (structureMatches)
        {
            for (int i = 0; i < sessions.Count; i++)
                UpdateSessionCard(_sessionPanel.Controls[i], sessions[i]);
            return;
        }

        _sessionPanel.Controls.Clear();
        foreach (var session in sessions)
        {
            var card = CreateSessionCard(session);
            card.Tag = GetSessionTag(session);
            _sessionPanel.Controls.Add(card);
        }
    }

    private static string GetSessionTag(LaunchSession session)
    {
        return $"{session.Status}|{session.RoutingStatus}|{session.LiveProcessCount}";
    }

    private Panel CreateSessionCard(LaunchSession session)
    {
        var card = new Panel
        {
            Width = _sessionPanel.Width - 30,
            Height = 64,
            Margin = new Padding(4),
            BorderStyle = BorderStyle.FixedSingle,
            BackColor = GetSessionColor(session.Status)
        };

        var layout = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 5,
            RowCount = 1,
            Padding = new Padding(4)
        };
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 24f));
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 18f));
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 22f));
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 16f));
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 20f));

        var nameLbl = new Label
        {
            Text = $"{session.Name}\n{session.Mode}",
            Dock = DockStyle.Fill,
            Font = new Font(Font.FontFamily, 9f, FontStyle.Bold)
        };
        layout.Controls.Add(nameLbl, 0, 0);

        var statusLbl = new Label
        {
            Text = $"{FormatStatus(session)}\n{session.DurationText}",
            Dock = DockStyle.Fill
        };
        layout.Controls.Add(statusLbl, 1, 0);

        var routingLbl = new Label
        {
            Text = $"Routing\n{FormatRouting(session)}",
            Dock = DockStyle.Fill,
            ForeColor = RoutingColor(session.RoutingStatus),
            Font = new Font(Font.FontFamily, 8.5f)
        };
        layout.Controls.Add(routingLbl, 2, 0);

        var liveCount = session.LiveProcessCount;
        var pidLbl = new Label
        {
            Text = liveCount > 1
                ? $"Processes: {liveCount}"
                : $"PID: {session.LiveProcessId?.ToString() ?? session.RootProcessId?.ToString() ?? "?"}",
            Dock = DockStyle.Fill
        };
        layout.Controls.Add(pidLbl, 3, 0);

        var btnPanel = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.LeftToRight };
        var isLive = session.Status is "running" or "running-via-child" or "running-via-correlated";
        var isChecking = session.Status == "checking-correlated";

        if (isLive)
        {
            var stopBtn = new Button { Text = "Stop", AutoSize = true, Height = 24 };
            stopBtn.Click += (_, _) =>
            {
                if (MessageBox.Show($"Stop {session.Name}?", "Confirm", MessageBoxButtons.YesNo) == DialogResult.Yes)
                    _sessions.StopSession(session);
            };
            btnPanel.Controls.Add(stopBtn);

            // Route Child — only when backend is reachable AND not already failed.
            // proxifyre-route-failed gets a Retry / Copy Hint pair instead.
            bool backendReachable = _backend != null
                && _config.TransparentBackend.Enabled
                && session.RoutingStatus is "proxifyre-route-active"
                                          or "proxifyre-route-pending"
                                          or "proxifyre-route-needs-restart";
            bool hasLiveChild = session.Processes.Any(p => p.ExitedAt == null && p.Role != "root");

            if (backendReachable && hasLiveChild)
            {
                var routeBtn = new Button { Text = "Route Child", AutoSize = true, Height = 24 };
                routeBtn.Click += (_, _) =>
                {
                    var child = session.Processes.FirstOrDefault(p => p.ExitedAt == null && p.Role != "root");
                    if (child == null) return;
                    if (MessageBox.Show(
                            $"Add {child.Name} to ProxiFyre route for {session.ProxyId}?\n\n" +
                            $"ProxiFyre routing is executable-based. Existing connections may not move — restart the app if routing does not change.",
                            "Route Detected Child", MessageBoxButtons.YesNo, MessageBoxIcon.Question) == DialogResult.Yes)
                    {
                        _sessions.RouteDetectedChild(session);
                    }
                };
                btnPanel.Controls.Add(routeBtn);
            }
            // route-failed: offer Retry + Copy Hint as escape hatch
            else if (session.RoutingStatus == "proxifyre-route-failed")
            {
                var retryBtn = new Button { Text = "Retry", AutoSize = true, Height = 24 };
                retryBtn.Click += (_, _) =>
                {
                    if (string.IsNullOrEmpty(session.ProxyId)) return;
                    // re-run the route through SessionManager to reset RoutingStatus
                    _sessions.RouteDetectedChild(session);
                };
                btnPanel.Controls.Add(retryBtn);
                var hintBtn2 = new Button { Text = "Copy Rule Hint", AutoSize = true, Height = 24 };
                hintBtn2.Click += (_, _) => CopyRuleHint(session);
                btnPanel.Controls.Add(hintBtn2);
            }
            // Fallback Copy Rule Hint when backend is not in play
            else if (session.RoutingStatus == "external-routing-required")
            {
                var hintBtn = new Button { Text = "Copy Rule Hint", AutoSize = true, Height = 24 };
                hintBtn.Click += (_, _) => CopyRuleHint(session);
                btnPanel.Controls.Add(hintBtn);
            }
        }
        else if (isChecking)
        {
            btnPanel.Controls.Add(new Label { Text = "Detecting...", AutoSize = true, ForeColor = Color.SteelBlue });
        }
        else
        {
            var removeBtn = new Button { Text = "Remove", AutoSize = true, Height = 24 };
            removeBtn.Click += (_, _) => _sessions.RemoveSession(session);
            btnPanel.Controls.Add(removeBtn);
        }
        layout.Controls.Add(btnPanel, 4, 0);

        card.Controls.Add(layout);
        return card;
    }

    private static string FormatStatus(LaunchSession session) => session.Status switch
    {
        "running" => "Running",
        "running-via-child" => "Via child",
        "running-via-correlated" => "Via correlated",
        "checking-correlated" => "Checking...",
        "exited" => "Exited",
        "failed" => "Failed",
        _ => session.Status
    };

    private static string FormatRouting(LaunchSession session) => session.RoutingStatus switch
    {
        "direct" => "Direct",
        "browser-proxy-active" => "Browser proxy",
        "external-routing-required" => "External router",
        "proxifyre-route-active" => "ProxiFyre active",
        "proxifyre-route-pending" => "ProxiFyre pending",
        "proxifyre-route-needs-restart" => "Restart needed",
        "proxifyre-route-failed" => "ProxiFyre failed",
        _ => session.RoutingStatus
    };

    private static Color RoutingColor(string routingStatus) => routingStatus switch
    {
        "browser-proxy-active" => Color.FromArgb(22, 101, 52),
        "proxifyre-route-active" => Color.FromArgb(22, 101, 52),
        "proxifyre-route-pending" => Color.FromArgb(146, 64, 14),
        "proxifyre-route-needs-restart" => Color.FromArgb(180, 83, 9),
        "proxifyre-route-failed" => Color.Firebrick,
        "external-routing-required" => Color.FromArgb(146, 64, 14),
        _ => Color.DimGray
    };

    private void UpdateSessionCard(Control control, LaunchSession session)
    {
        if (control is not Panel card) return;
        card.BackColor = GetSessionColor(session.Status);

        var layout = card.Controls[0] as TableLayoutPanel;
        if (layout == null) return;

        var nameLbl = layout.GetControlFromPosition(0, 0) as Label;
        if (nameLbl != null) nameLbl.Text = $"{session.Name}\n{session.Mode}";

        var statusLbl = layout.GetControlFromPosition(1, 0) as Label;
        if (statusLbl != null) statusLbl.Text = $"{FormatStatus(session)}\n{session.DurationText}";

        var routingLbl = layout.GetControlFromPosition(2, 0) as Label;
        if (routingLbl != null)
        {
            routingLbl.Text = $"Routing\n{FormatRouting(session)}";
            routingLbl.ForeColor = RoutingColor(session.RoutingStatus);
        }

        var pidLbl = layout.GetControlFromPosition(3, 0) as Label;
        if (pidLbl != null)
        {
            var liveCount = session.LiveProcessCount;
            pidLbl.Text = liveCount > 1
                ? $"Processes: {liveCount}"
                : $"PID: {session.LiveProcessId?.ToString() ?? session.RootProcessId?.ToString() ?? "?"}";
        }
    }

    private static Color GetSessionColor(string status) => status switch
    {
        "running" => Color.FromArgb(220, 252, 231),
        "running-via-child" => Color.FromArgb(254, 249, 195),
        "running-via-correlated" => Color.FromArgb(254, 215, 170),
        "checking-correlated" => Color.FromArgb(219, 234, 254),
        "exited" => Color.FromArgb(243, 244, 246),
        "failed" => Color.FromArgb(254, 226, 226),
        _ => Color.White
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
        foreach (var evt in _events.GetRecent(30))
            _eventList.Items.Add(evt.ToString());
        if (_eventList.Items.Count > 0)
            _eventList.TopIndex = _eventList.Items.Count - 1;
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
        _sessions.SessionsChanged -= OnSessionsChanged;
        _sessions.CorrelatedCandidatesFound -= OnCorrelatedCandidatesFound;
        _events.EventAdded -= OnEventAdded;
        _monitor.StatusChanged -= OnProxyStatusChanged;
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
