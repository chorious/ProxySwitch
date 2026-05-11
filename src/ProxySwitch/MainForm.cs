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
    private ToolStripMenuItem _statusItem = null!;
    private ToolStripMenuItem _launchItem = null!;
    private ToolStripMenuItem _profileItem = null!;
    private ToolStripMenuItem _p10708Item = null!;
    private ToolStripMenuItem _p10808Item = null!;

    private ProxyConfig _config = new();
    private PortMonitor _monitor = null!;
    private AppLauncher _launcher = null!;

    public static readonly string RootPath = @"E:\proxyswitch";

    public MainForm()
    {
        InitializeComponent();
        LoadConfig();
        SetupMonitor();
        BuildMenu();
        _tray.Visible = true;
        Logger.Info("ProxySwitch tray visible");
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
    }

    private void LoadConfig()
    {
        var path = Path.Combine(RootPath, "config", "proxyswitch.json");
        try
        {
            var json = File.ReadAllText(path);
            _config = JsonSerializer.Deserialize<ProxyConfig>(json) ?? new ProxyConfig();
            Logger.Info($"Config loaded from {path}, proxies={_config.Proxies.Count}, apps={_config.Apps.Count}");
        }
        catch (Exception ex)
        {
            Logger.Error($"Failed to load config: {ex.Message}");
            _config = new ProxyConfig();
        }
    }

    private void SetupMonitor()
    {
        _monitor = new PortMonitor();
        _monitor.StatusChanged += OnStatusChanged;

        foreach (var proxy in _config.Proxies)
        {
            _monitor.AddTarget(proxy.Host, proxy.Port);
        }

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
        UpdateStatusLabels();
        UpdateTrayIcon();
    }

    private void BuildMenu()
    {
        _menu = new ContextMenuStrip();

        // Status
        _statusItem = new ToolStripMenuItem("Status") { Enabled = false };
        _p10708Item = new ToolStripMenuItem("10708: Checking...") { Enabled = false };
        _p10808Item = new ToolStripMenuItem("10808: Checking...") { Enabled = false };
        _statusItem.DropDownItems.Add(_p10708Item);
        _statusItem.DropDownItems.Add(_p10808Item);
        _menu.Items.Add(_statusItem);
        _menu.Items.Add(new ToolStripSeparator());

        // Launch
        _launchItem = new ToolStripMenuItem("Launch");
        foreach (var app in _config.Apps)
        {
            var item = new ToolStripMenuItem(app.Name, null, (_, _) => _launcher.LaunchBrowser(app));
            _launchItem.DropDownItems.Add(item);
        }
        _menu.Items.Add(_launchItem);
        _menu.Items.Add(new ToolStripSeparator());

        // Proxifier Profile
        _profileItem = new ToolStripMenuItem("Proxifier Profile");
        if (_config.Proxifier.Enabled && _config.Proxifier.Profiles.Count > 0)
        {
            // Recent profile
            if (!string.IsNullOrEmpty(_config.LastProfile) &&
                _config.Proxifier.Profiles.ContainsKey(_config.LastProfile))
            {
                var recentName = _config.LastProfile switch
                {
                    "direct" => "Direct",
                    "all10708" => "All via 10708",
                    "all10808" => "All via 10808",
                    "mixed" => "Mixed Rules",
                    _ => _config.LastProfile
                };
                var recentItem = new ToolStripMenuItem($"Reload Last: {recentName}", null,
                    (_, _) => LoadProfile(_config.LastProfile!));
                _profileItem.DropDownItems.Add(recentItem);
                _profileItem.DropDownItems.Add(new ToolStripSeparator());
            }

            foreach (var kv in _config.Proxifier.Profiles)
            {
                var name = kv.Key switch
                {
                    "direct" => "Direct",
                    "all10708" => "All via 10708",
                    "all10808" => "All via 10808",
                    "mixed" => "Mixed Rules",
                    _ => kv.Key
                };
                var item = new ToolStripMenuItem(name, null, (_, _) => LoadProfile(kv.Key));
                _profileItem.DropDownItems.Add(item);
            }
        }
        else
        {
            _profileItem.Enabled = false;
        }
        _menu.Items.Add(_profileItem);
        _menu.Items.Add(new ToolStripSeparator());

        // Open Config
        _menu.Items.Add("Open Config Folder", null, (_, _) => OpenConfigFolder());

        // Open Proxifier
        if (_config.Proxifier.Enabled)
        {
            _menu.Items.Add("Open Proxifier", null, (_, _) => _launcher.OpenProxifier());
        }

        // Settings
        _menu.Items.Add(new ToolStripSeparator());
        _menu.Items.Add("Settings...", null, (_, _) =>
        {
            using var form = new SettingsForm();
            form.ShowDialog();
            // Reload config after settings closed
            LoadConfig();
            BuildMenu();
        });

        _menu.Items.Add(new ToolStripSeparator());
        _menu.Items.Add("Quit", null, (_, _) => Application.Exit());

        _tray.ContextMenuStrip = _menu;
        _launcher = new AppLauncher(_config);
        UpdateStatusLabels();
        UpdateTrayIcon();
    }

    private void UpdateStatusLabels()
    {
        foreach (var proxy in _config.Proxies)
        {
            bool online = _monitor.IsOnline(proxy.Host, proxy.Port);
            var label = $"{proxy.Port}: {(online ? "Online" : "Offline")}";
            if (proxy.Port == 10708) _p10708Item.Text = label;
            if (proxy.Port == 10808) _p10808Item.Text = label;
        }
    }

    private void UpdateTrayIcon()
    {
        bool p10708 = _config.Proxies.Any(p => p.Port == 10708 && _monitor.IsOnline(p.Host, p.Port));
        bool p10808 = _config.Proxies.Any(p => p.Port == 10808 && _monitor.IsOnline(p.Host, p.Port));

        _tray.Text = $"ProxySwitch | 10708: {(p10708 ? "ON" : "OFF")} | 10808: {(p10808 ? "ON" : "OFF")}";

        Color color = (p10708, p10808) switch
        {
            (true, true) => Color.FromArgb(34, 197, 94),   // green
            (true, false) => Color.FromArgb(34, 197, 94),  // green
            (false, true) => Color.FromArgb(59, 130, 246), // blue
            _ => Color.FromArgb(156, 163, 175)             // gray
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

    private static void OpenConfigFolder()
    {
        var path = Path.Combine(RootPath, "config");
        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = "explorer.exe",
                Arguments = $"\"{path}\"",
                UseShellExecute = true
            });
        }
        catch { }
    }

    private void LoadProfile(string key)
    {
        _launcher.LoadProxifierProfile(key);

        if (_config.LastProfile != key)
        {
            _config.LastProfile = key;
            SaveConfig();
            BuildMenu(); // refresh to show recent
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
            Logger.Info($"LastProfile updated: {_config.LastProfile}");
        }
        catch (Exception ex)
        {
            Logger.Error($"Save lastProfile failed: {ex.Message}");
        }
    }

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        _monitor?.Dispose();
        _tray?.Dispose();
        base.OnFormClosing(e);
    }
}
