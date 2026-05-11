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

    private FlowLayoutPanel _sessionPanel = null!;
    private ListBox _eventList = null!;
    private Label _proxyStatusLabel = null!;
    private System.Windows.Forms.Timer _refreshTimer = null!;
    private bool _correlatedDialogOpen;
    private readonly Queue<(LaunchSession Session, List<HandoffCandidate> Candidates)> _pendingCorrelated = new();

    public DashboardForm(ProxyConfig config, PortMonitor monitor, SessionManager sessions, EventStore events, AppLauncher launcher)
    {
        _config = config;
        _monitor = monitor;
        _sessions = sessions;
        _events = events;
        _launcher = launcher;
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

        var proxyGroup = new GroupBox { Text = "Proxies", Dock = DockStyle.Fill };
        _proxyStatusLabel = new Label
        {
            Dock = DockStyle.Fill,
            Text = "Checking...",
            Padding = new Padding(4)
        };
        proxyGroup.Controls.Add(_proxyStatusLabel);
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

        if (mode != "direct" && proxyId != null)
        {
            var label = LabelForProxy(proxyId, proxyId);
            var result = MessageBox.Show(
                $"Launch {name} with {label} intent.\n\n" +
                $"ProxySwitch will start the app and monitor the session, but it does NOT route the traffic itself. " +
                $"Routing is handled by your external router (e.g. Clash Verge / v2ray).\n\n" +
                $"⚠ For launcher-style apps (Steam, Epic, game clients), child processes spawned afterwards are " +
                $"not automatically covered by the router's rules unless those rules match the child executable too.\n\n" +
                $"Continue?",
                "Confirm Launch",
                MessageBoxButtons.YesNo,
                MessageBoxIcon.Information);

            if (result != DialogResult.Yes) return;
        }

        _sessions.LaunchGeneric(exePath, name, mode, proxyId);
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

            // Copy Rule Hint — only meaningful when proxy intent is set
            if (session.RoutingStatus == "external-routing-required")
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
        _ => session.RoutingStatus
    };

    private static Color RoutingColor(string routingStatus) => routingStatus switch
    {
        "browser-proxy-active" => Color.FromArgb(22, 101, 52),
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
