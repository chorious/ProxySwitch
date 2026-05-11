using System.Diagnostics;
using System.Text.Json;
using ProxySwitch.Models;
using ProxySwitch.Services;
using System.Drawing.Drawing2D;

namespace ProxySwitch;

public class MainForm : Form
{
    private NotifyIcon _tray = null!;
    private ContextMenuStrip _menu = null!;

    private ProxyConfig _config = new();
    private PortMonitor _monitor = null!;
    private AppLauncher _launcher = null!;
    private EventStore _events = null!;
    private ProcessMonitor _processMonitor = null!;
    private SessionManager _sessionManager = null!;
    private DashboardForm? _dashboard;

    public static readonly string RootPath = @"E:\proxyswitch";

    public MainForm()
    {
        InitializeComponent();
        LoadConfig();
        SetupServices();
        BuildMenu();
        _tray.Visible = true;
        Logger.Info("ProxySwitch v0.3 started");
    }

    private void InitializeComponent()
    {
        Text = "ProxySwitch";
        ShowInTaskbar = false;
        WindowState = FormWindowState.Minimized;
        Size = new Size(1, 1);
        FormBorderStyle = FormBorderStyle.None;
        AllowTransparency = true;
        Opacity = 0;

        _tray = new NotifyIcon
        {
            Icon = SystemIcons.Shield,
            Text = "ProxySwitch",
            Visible = false
        };
        _tray.MouseDoubleClick += (_, e) =>
        {
            if (e.Button == MouseButtons.Left) ShowDashboard();
        };
    }

    private void LoadConfig()
    {
        var path = Path.Combine(RootPath, "config", "proxyswitch.json");
        try
        {
            var json = File.ReadAllText(path);
            _config = JsonSerializer.Deserialize<ProxyConfig>(json) ?? new ProxyConfig();
            Logger.Info($"Config loaded, proxies={_config.Proxies.Count}, apps={_config.Apps.Count}");
        }
        catch (Exception ex)
        {
            Logger.Error($"Failed to load config: {ex.Message}");
            _config = new ProxyConfig();
        }
    }

    private void SetupServices()
    {
        _events = new EventStore();
        _monitor = new PortMonitor();
        _monitor.StatusChanged += OnStatusChanged;

        foreach (var proxy in _config.Proxies)
        {
            _monitor.AddTarget(proxy.Host, proxy.Port, proxy.Id);
        }

        _launcher = new AppLauncher(_config);
        _processMonitor = new ProcessMonitor();
        _sessionManager = new SessionManager(_launcher, _processMonitor, _events);

        _monitor.Start();
        _monitor.StartPolling();
    }

    private void OnStatusChanged()
    {
        if (InvokeRequired)
        {
            Invoke(OnStatusChanged);
            return;
        }
        UpdateTrayIcon();
    }

    private void BuildMenu()
    {
        _menu = new ContextMenuStrip();

        // Open Dashboard
        _menu.Items.Add("Open Dashboard", null, (_, _) => ShowDashboard());
        _menu.Items.Add(new ToolStripSeparator());

        // Quick Launch (pinned apps)
        if (_config.Apps.Count > 0)
        {
            var quick = new ToolStripMenuItem("Quick Launch");
            foreach (var app in _config.Apps)
            {
                var a = app;
                quick.DropDownItems.Add(a.Name, null, (_, _) => _sessionManager.LaunchBrowser(a, a.Mode, a.ProxyId));
            }
            _menu.Items.Add(quick);
            _menu.Items.Add(new ToolStripSeparator());
        }

        // Proxy Status
        var statusItem = new ToolStripMenuItem("Proxy Status") { Enabled = false };
        foreach (var proxy in _config.Proxies)
        {
            var s = _monitor.GetStatus(proxy.Host, proxy.Port);
            var latency = s?.LatencyMs.HasValue == true ? $"{s.LatencyMs}ms" : "timeout";
            var text = $"{proxy.Name}: {(s?.Status ?? "unknown")} ({latency})";
            statusItem.DropDownItems.Add(text, null, null);
        }
        _menu.Items.Add(statusItem);
        _menu.Items.Add(new ToolStripSeparator());

        // Proxifier Profile
        if (_config.Proxifier.Enabled && _config.Proxifier.Profiles.Count > 0)
        {
            var prof = new ToolStripMenuItem("Proxifier Profile");
            if (!string.IsNullOrEmpty(_config.LastProfile) &&
                _config.Proxifier.Profiles.ContainsKey(_config.LastProfile))
            {
                prof.DropDownItems.Add($"Reload Last: {_config.LastProfile}", null,
                    (_, _) => LoadProfile(_config.LastProfile!));
                prof.DropDownItems.Add(new ToolStripSeparator());
            }
            foreach (var kv in _config.Proxifier.Profiles)
            {
                var name = kv.Key;
                prof.DropDownItems.Add(name, null, (_, _) => LoadProfile(kv.Key));
            }
            _menu.Items.Add(prof);
            _menu.Items.Add(new ToolStripSeparator());
        }

        // Tools
        _menu.Items.Add("Open Proxifier", null, (_, _) => _launcher.OpenProxifier());
        _menu.Items.Add("Settings...", null, (_, _) =>
        {
            using var form = new SettingsForm();
            form.ShowDialog();
            LoadConfig();
            BuildMenu();
        });
        _menu.Items.Add(new ToolStripSeparator());
        _menu.Items.Add("Quit", null, (_, _) => Application.Exit());

        _tray.ContextMenuStrip = _menu;
        UpdateTrayIcon();
    }

    private void ShowDashboard()
    {
        if (_dashboard == null || _dashboard.IsDisposed)
        {
            _dashboard = new DashboardForm(_config, _monitor, _sessionManager, _events, _launcher);
            _dashboard.FormClosed += (_, _) => _dashboard = null;
            _dashboard.Show();
        }
        else
        {
            _dashboard.WindowState = FormWindowState.Normal;
            _dashboard.BringToFront();
            _dashboard.Activate();
        }
    }

    private void UpdateTrayIcon()
    {
        bool p10708 = _config.Proxies.Any(p => p.Port == 10708 && _monitor.IsOnline(p.Host, p.Port));
        bool p10808 = _config.Proxies.Any(p => p.Port == 10808 && _monitor.IsOnline(p.Host, p.Port));

        _tray.Text = $"ProxySwitch | 10708: {(p10708 ? "ON" : "OFF")} | 10808: {(p10808 ? "ON" : "OFF")}";

        Color color = (p10708, p10808) switch
        {
            (true, true) => Color.FromArgb(34, 197, 94),
            (true, false) => Color.FromArgb(34, 197, 94),
            (false, true) => Color.FromArgb(59, 130, 246),
            _ => Color.FromArgb(156, 163, 175)
        };

        _tray.Icon?.Dispose();
        _tray.Icon = CreateStatusIcon(color);
    }

    private static Icon CreateStatusIcon(Color color)
    {
        using var bmp = new Bitmap(16, 16);
        using (var g = Graphics.FromImage(bmp))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.Clear(Color.Transparent);
            using var brush = new SolidBrush(color);
            g.FillEllipse(brush, 1, 1, 14, 14);
        }
        var hIcon = bmp.GetHicon();
        return Icon.FromHandle(hIcon);
    }

    private void LoadProfile(string key)
    {
        _launcher.LoadProxifierProfile(key);
        _events.Add("ProfileLoaded", $"Loaded profile: {key}");

        if (_config.LastProfile != key)
        {
            _config.LastProfile = key;
            SaveConfig();
            BuildMenu();
        }
    }

    private void SaveConfig()
    {
        var path = Path.Combine(RootPath, "config", "proxyswitch.json");
        var options = new JsonSerializerOptions
        {
            WriteIndented = true,
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull
        };
        try
        {
            var json = JsonSerializer.Serialize(_config, options);
            File.WriteAllText(path, json);
        }
        catch (Exception ex)
        {
            Logger.Error($"Save config failed: {ex.Message}");
        }
    }

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        _monitor?.Dispose();
        _processMonitor?.Dispose();
        _sessionManager?.Dispose();
        _tray?.Dispose();
        base.OnFormClosing(e);
    }
}
