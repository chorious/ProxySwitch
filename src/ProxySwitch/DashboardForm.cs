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

        Text = "ProxySwitch Dashboard";
        Size = new Size(900, 650);
        StartPosition = FormStartPosition.CenterScreen;
        MinimumSize = new Size(700, 450);

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
        mainLayout.RowStyles.Add(new RowStyle(SizeType.Absolute, 100f));   // Launch zones
        mainLayout.RowStyles.Add(new RowStyle(SizeType.Absolute, 50f));    // Pinned apps
        mainLayout.RowStyles.Add(new RowStyle(SizeType.Percent, 60f));     // Sessions
        mainLayout.RowStyles.Add(new RowStyle(SizeType.Percent, 40f));     // Events + Proxy status

        // Row 0: Launch Zones
        var zonePanel = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 3,
            RowCount = 1
        };
        for (int i = 0; i < 3; i++)
            zonePanel.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 33.33f));

        var directZone = new LaunchZoneControl { Title = "Direct", Mode = "direct", AccentColor = Color.FromArgb(200, 200, 200), Dock = DockStyle.Fill, Margin = new Padding(4) };
        directZone.FileDropped += path => HandleDrop(path, "direct");
        zonePanel.Controls.Add(directZone, 0, 0);

        var z10708 = new LaunchZoneControl { Title = "10708", Mode = "proxy-10708", AccentColor = Color.FromArgb(34, 197, 94), Dock = DockStyle.Fill, Margin = new Padding(4) };
        z10708.FileDropped += path => HandleDrop(path, "proxy-10708");
        zonePanel.Controls.Add(z10708, 1, 0);

        var z10808 = new LaunchZoneControl { Title = "10808", Mode = "proxy-10808", AccentColor = Color.FromArgb(59, 130, 246), Dock = DockStyle.Fill, Margin = new Padding(4) };
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
        var sessionGroup = new GroupBox
        {
            Text = "Sessions",
            Dock = DockStyle.Fill,
            Padding = new Padding(4)
        };
        _sessionPanel = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            FlowDirection = FlowDirection.TopDown,
            AutoScroll = true,
            WrapContents = false
        };
        sessionGroup.Controls.Add(_sessionPanel);
        mainLayout.Controls.Add(sessionGroup, 0, 2);

        // Row 3: Proxy Status + Events (side by side)
        var bottomPanel = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 2,
            RowCount = 1
        };
        bottomPanel.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 35f));
        bottomPanel.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 65f));

        // Proxy Status
        var proxyGroup = new GroupBox { Text = "Proxies", Dock = DockStyle.Fill };
        _proxyStatusLabel = new Label
        {
            Dock = DockStyle.Fill,
            Text = "Checking...",
            Padding = new Padding(4)
        };
        proxyGroup.Controls.Add(_proxyStatusLabel);
        bottomPanel.Controls.Add(proxyGroup, 0, 0);

        // Events
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

    private void HandleDrop(string exePath, string mode)
    {
        var name = Path.GetFileNameWithoutExtension(exePath);
        var proxyId = mode switch
        {
            "proxy-10708" => "p10708",
            "proxy-10808" => "p10808",
            _ => null
        };

        bool profileLoaded = false;

        // Try to auto-load matching Proxifier profile before launching
        if (mode != "direct" && _config.Proxifier.Enabled)
        {
            var profileKey = proxyId switch
            {
                "p10708" => "all10708",
                "p10808" => "all10808",
                _ => null
            };

            if (!string.IsNullOrEmpty(profileKey) &&
                _config.Proxifier.Profiles.TryGetValue(profileKey, out var profilePath) &&
                File.Exists(profilePath))
            {
                _launcher.LoadProxifierProfile(profileKey);
                profileLoaded = true;
            }
            else
            {
                var result = MessageBox.Show(
                    $"No matching Proxifier profile found for {mode}.\n" +
                    $"ProxySwitch will start the app but traffic routing is unverified.\n\n" +
                    $"⚠ Launcher apps (Steam / Epic / game clients) start child processes that Proxifier rules do not auto-apply to. " +
                    $"Confirm your Proxifier profile covers the target.\n\n" +
                    $"Run {name} anyway?",
                    "Confirm Launch",
                    MessageBoxButtons.YesNo,
                    MessageBoxIcon.Warning);

                if (result != DialogResult.Yes) return;
            }
        }

        _sessions.LaunchGeneric(exePath, name, mode, proxyId, profileLoaded);
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
        // Tag includes status + warning presence + assist state so card rebuilds when any change
        var hasWarning = !string.IsNullOrEmpty(session.RoutingWarning) && !session.IsRoutingAssisted;
        return $"{session.Status}|{hasWarning}|{session.IsRoutingAssisted}";
    }

    private Panel CreateSessionCard(LaunchSession session)
    {
        var hasWarning = !string.IsNullOrEmpty(session.RoutingWarning) && !session.IsRoutingAssisted;
        var card = new Panel
        {
            Width = _sessionPanel.Width - 30,
            Height = hasWarning ? 100 : 60,
            Margin = new Padding(4),
            BorderStyle = BorderStyle.FixedSingle,
            BackColor = GetSessionColor(session.Status)
        };

        var rootLayout = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = hasWarning ? 2 : 1
        };
        rootLayout.RowStyles.Add(new RowStyle(SizeType.Absolute, 56f));
        if (hasWarning) rootLayout.RowStyles.Add(new RowStyle(SizeType.Percent, 100f));

        var layout = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 4,
            RowCount = 1,
            Padding = new Padding(4)
        };
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 30f));
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 25f));
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 25f));
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 20f));

        // Name + Mode
        var nameLbl = new Label
        {
            Text = $"{session.Name}\n{session.Mode}",
            Dock = DockStyle.Fill,
            Font = new Font(Font.FontFamily, 9f, FontStyle.Bold)
        };
        layout.Controls.Add(nameLbl, 0, 0);

        // Status + Duration
        var statusLbl = new Label
        {
            Text = $"{FormatStatus(session)}\n{session.DurationText}",
            Dock = DockStyle.Fill
        };
        layout.Controls.Add(statusLbl, 1, 0);

        // PID / Process count
        var liveCount = session.Processes.Count(p => p.ExitedAt == null);
        var pidLbl = new Label
        {
            Text = liveCount > 1
                ? $"Processes: {liveCount}\n{session.RoutingStatus}"
                : $"PID: {session.LiveProcessId?.ToString() ?? session.RootProcessId?.ToString() ?? "?"}\n{session.RoutingStatus}",
            Dock = DockStyle.Fill
        };
        layout.Controls.Add(pidLbl, 2, 0);

        // Actions
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
        }
        else if (isChecking)
        {
            var checkingLbl = new Label { Text = "Detecting...", AutoSize = true, ForeColor = Color.SteelBlue };
            btnPanel.Controls.Add(checkingLbl);
        }
        else
        {
            var removeBtn = new Button { Text = "Remove", AutoSize = true, Height = 24 };
            removeBtn.Click += (_, _) => _sessions.RemoveSession(session);
            btnPanel.Controls.Add(removeBtn);
        }
        layout.Controls.Add(btnPanel, 3, 0);

        rootLayout.Controls.Add(layout, 0, 0);

        // Assist warning row
        if (hasWarning)
        {
            var warnPanel = new TableLayoutPanel
            {
                Dock = DockStyle.Fill,
                ColumnCount = 2,
                RowCount = 1,
                BackColor = Color.FromArgb(254, 243, 199),
                Padding = new Padding(4)
            };
            warnPanel.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 70f));
            warnPanel.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 30f));

            var warnLbl = new Label
            {
                Text = "⚠ " + session.RoutingWarning,
                Dock = DockStyle.Fill,
                ForeColor = Color.FromArgb(146, 64, 14),
                TextAlign = ContentAlignment.MiddleLeft
            };
            warnPanel.Controls.Add(warnLbl, 0, 0);

            var addRuleBtn = new Button { Text = "Add Rule", AutoSize = true, Height = 24, Anchor = AnchorStyles.Right };
            addRuleBtn.Click += (_, _) => HandleAddRule(session);
            warnPanel.Controls.Add(addRuleBtn, 1, 0);

            rootLayout.Controls.Add(warnPanel, 0, 1);
        }

        card.Controls.Add(rootLayout);
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

    private void HandleAddRule(LaunchSession session)
    {
        var liveChild = session.Processes.FirstOrDefault(p =>
            p.ExitedAt == null && (p.Role == "descendant" || p.Role == "correlated"));
        if (liveChild == null) return;

        var launcherName = Path.GetFileName(session.ExePath);
        var proxyLabel = session.ProxyId switch
        {
            "p10708" => "10708",
            "p10808" => "10808",
            _ => session.ProxyId ?? "?"
        };

        var result = MessageBox.Show(
            $"Add {launcherName} and {liveChild.Name} to the {proxyLabel} assist rule?\n\n" +
            $"This will update the generated-{proxyLabel}.ppx Proxifier profile (rule: ProxySwitch_{proxyLabel}_AssistedApps) " +
            $"to include both executables, then load the generated profile.\n\n" +
            $"⚠ The rule is executable-based, NOT PID-isolated. Other instances of {liveChild.Name} elsewhere may also be routed through {proxyLabel}.\n\n" +
            $"Prerequisite: You must have already created the generated profile in Proxifier with the rule name 'ProxySwitch_{proxyLabel}_AssistedApps'.",
            "Add Assist Rule",
            MessageBoxButtons.YesNo,
            MessageBoxIcon.Question);

        if (result == DialogResult.Yes)
        {
            _sessions.AddAssistRule(session);
        }
    }

    private void UpdateSessionCard(Control control, LaunchSession session)
    {
        if (control is not Panel card) return;
        card.BackColor = GetSessionColor(session.Status);

        // card.Controls[0] is rootLayout (TableLayoutPanel), [0,0] is the main layout
        var rootLayout = card.Controls[0] as TableLayoutPanel;
        if (rootLayout == null) return;

        var layout = rootLayout.GetControlFromPosition(0, 0) as TableLayoutPanel;
        if (layout == null) return;

        var nameLbl = layout.GetControlFromPosition(0, 0) as Label;
        if (nameLbl != null) nameLbl.Text = $"{session.Name}\n{session.Mode}";

        var statusLbl = layout.GetControlFromPosition(1, 0) as Label;
        if (statusLbl != null) statusLbl.Text = $"{FormatStatus(session)}\n{session.DurationText}";

        var pidLbl = layout.GetControlFromPosition(2, 0) as Label;
        if (pidLbl != null)
        {
            var liveCount = session.Processes.Count(p => p.ExitedAt == null);
            pidLbl.Text = liveCount > 1
                ? $"Processes: {liveCount}\n{session.RoutingStatus}"
                : $"PID: {session.LiveProcessId?.ToString() ?? session.RootProcessId?.ToString() ?? "?"}\n{session.RoutingStatus}";
        }
    }

    private static Color GetSessionColor(string status) => status switch
    {
        "running" => Color.FromArgb(220, 252, 231),               // light green
        "running-via-child" => Color.FromArgb(254, 249, 195),     // light yellow
        "running-via-correlated" => Color.FromArgb(254, 215, 170), // light orange
        "checking-correlated" => Color.FromArgb(219, 234, 254),   // light blue
        "exited" => Color.FromArgb(243, 244, 246),                // light gray
        "failed" => Color.FromArgb(254, 226, 226),                // light red
        _ => Color.White
    };

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
                lines.Add($"{proxy.Port}: {status.Status.ToUpper()} ({latency}, checked {lastCheck})");
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

        // Reentrancy guard: queue if a dialog is already open
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
                // X close, Cancel, or explicit Ignore → all finalize as exited.
                // Leaving session in "checking-correlated" indefinitely is worse UX.
                _sessions.IgnoreCorrelated(session);
            }
        }
        finally
        {
            _correlatedDialogOpen = false;
            // Process next queued, if any
            if (_pendingCorrelated.Count > 0)
            {
                var next = _pendingCorrelated.Dequeue();
                BeginInvoke(() => ShowCorrelatedDialog(next.Session, next.Candidates));
            }
        }
    }
}
