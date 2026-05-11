using System.Diagnostics;
using System.Text.Json;
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

        zonePanel.Controls.Add(CreateZone("Direct", "Drop app here", "direct", Color.FromArgb(200, 200, 200)), 0, 0);
        zonePanel.Controls.Add(CreateZone("10708", "Drop app here", "proxy-10708", Color.FromArgb(34, 197, 94)), 1, 0);
        zonePanel.Controls.Add(CreateZone("10808", "Drop app here", "proxy-10808", Color.FromArgb(59, 130, 246)), 2, 0);
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

    private Panel CreateZone(string title, string subtitle, string mode, Color color)
    {
        var panel = new Panel
        {
            Dock = DockStyle.Fill,
            BackColor = Color.FromArgb(30, color),
            BorderStyle = BorderStyle.FixedSingle,
            Margin = new Padding(4),
            AllowDrop = true
        };

        var lbl = new Label
        {
            Text = $"{title}\n{subtitle}",
            Dock = DockStyle.Fill,
            TextAlign = ContentAlignment.MiddleCenter,
            Font = new Font(Font.FontFamily, 11f, FontStyle.Bold),
            ForeColor = color
        };
        panel.Controls.Add(lbl);

        panel.DragEnter += (s, e) =>
        {
            if (e.Data?.GetDataPresent(DataFormats.FileDrop) == true)
                e.Effect = DragDropEffects.Copy;
        };

        panel.DragDrop += (s, e) =>
        {
            var files = e.Data?.GetData(DataFormats.FileDrop) as string[];
            if (files?.Length > 0)
            {
                var exe = files.FirstOrDefault(f => f.EndsWith(".exe", StringComparison.OrdinalIgnoreCase));
                if (exe != null)
                    HandleDrop(exe, mode);
            }
        };

        return panel;
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
            }
            else
            {
                var result = MessageBox.Show(
                    $"No matching Proxifier profile found for {mode}.\n" +
                    $"ProxySwitch will start the app but traffic routing depends on current Proxifier rules.\n\n" +
                    $"Run {name} anyway?",
                    "Confirm Launch",
                    MessageBoxButtons.YesNo,
                    MessageBoxIcon.Warning);

                if (result != DialogResult.Yes) return;
            }
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
        // Only rebuild if count changed to avoid flicker
        if (_sessionPanel.Controls.Count == sessions.Count)
        {
            for (int i = 0; i < sessions.Count; i++)
                UpdateSessionCard(_sessionPanel.Controls[i], sessions[i]);
            return;
        }

        _sessionPanel.Controls.Clear();
        foreach (var session in sessions)
        {
            _sessionPanel.Controls.Add(CreateSessionCard(session));
        }
    }

    private Panel CreateSessionCard(LaunchSession session)
    {
        var card = new Panel
        {
            Width = _sessionPanel.Width - 30,
            Height = 60,
            Margin = new Padding(4),
            BorderStyle = BorderStyle.FixedSingle,
            BackColor = GetSessionColor(session.Status)
        };

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
            Text = $"{session.Status}\n{session.DurationText}",
            Dock = DockStyle.Fill
        };
        layout.Controls.Add(statusLbl, 1, 0);

        // PID / Process count
        var pidLbl = new Label
        {
            Text = session.ProcessIds.Count > 0
                ? $"Processes: {session.ProcessIds.Count}"
                : $"PID: {session.MainProcessId}",
            Dock = DockStyle.Fill
        };
        layout.Controls.Add(pidLbl, 2, 0);

        // Actions
        var btnPanel = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.LeftToRight };
        if (session.Status == "running")
        {
            var stopBtn = new Button { Text = "Stop", AutoSize = true, Height = 24 };
            stopBtn.Click += (_, _) =>
            {
                if (MessageBox.Show($"Stop {session.Name}?", "Confirm", MessageBoxButtons.YesNo) == DialogResult.Yes)
                    _sessions.StopSession(session);
            };
            btnPanel.Controls.Add(stopBtn);
        }
        else
        {
            var removeBtn = new Button { Text = "Remove", AutoSize = true, Height = 24 };
            removeBtn.Click += (_, _) => _sessions.RemoveSession(session);
            btnPanel.Controls.Add(removeBtn);
        }
        layout.Controls.Add(btnPanel, 3, 0);

        card.Controls.Add(layout);
        return card;
    }

    private void UpdateSessionCard(Control control, LaunchSession session)
    {
        if (control is not Panel card) return;
        card.BackColor = GetSessionColor(session.Status);

        var layout = card.Controls[0] as TableLayoutPanel;
        if (layout == null) return;

        var nameLbl = layout.GetControlFromPosition(0, 0) as Label;
        if (nameLbl != null) nameLbl.Text = $"{session.Name}\n{session.Mode}";

        var statusLbl = layout.GetControlFromPosition(1, 0) as Label;
        if (statusLbl != null) statusLbl.Text = $"{session.Status}\n{session.DurationText}";

        var pidLbl = layout.GetControlFromPosition(2, 0) as Label;
        if (pidLbl != null)
            pidLbl.Text = session.ProcessIds.Count > 0
                ? $"Processes: {session.ProcessIds.Count}"
                : $"PID: {session.MainProcessId}";
    }

    private static Color GetSessionColor(string status) => status switch
    {
        "running" => Color.FromArgb(220, 252, 231), // light green
        "exited" => Color.FromArgb(243, 244, 246),  // light gray
        "failed" => Color.FromArgb(254, 226, 226),  // light red
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
        _events.EventAdded -= OnEventAdded;
        _monitor.StatusChanged -= OnProxyStatusChanged;
        base.OnFormClosing(e);
    }
}
