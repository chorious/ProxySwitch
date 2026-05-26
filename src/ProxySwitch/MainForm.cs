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
    private ProxiFyreBackend? _backend;
    private ExternalProcessWatcher? _externalWatcher;
    private SessionSupervisor? _sessionSupervisor;
    private DashboardForm? _dashboard;

    public static readonly string RootPath = @"E:\proxyswitch";

    public MainForm()
    {
        InitializeComponent();
        LoadConfig();
        SetupServices();
        BuildMenu();
        _tray.Visible = true;
        try { _tray.ShowBalloonTip(3000, "ProxySwitch", "已启动 · 双击图标打开主窗口", ToolTipIcon.Info); } catch { }
        Logger.Info("ProxySwitch v0.8.2 started");
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
            Logger.Info($"Config loaded, proxies={_config.Proxies.Count}, apps={_config.Apps.Count}, backends={_config.RoutingBackends.Count}");
        }
        catch (Exception ex)
        {
            Logger.Error($"Failed to load config: {ex.Message}");
            _config = new ProxyConfig();
        }
    }

    private void SetupServices()
    {
        _events ??= new EventStore();   // keep EventStore across reloads so history isn't lost
        _monitor = new PortMonitor();
        _monitor.StatusChanged += OnStatusChanged;

        foreach (var proxy in _config.Proxies)
        {
            _monitor.AddTarget(proxy.Host, proxy.Port, proxy.Id);
        }

        _launcher = new AppLauncher(_config);
        _processMonitor = new ProcessMonitor();
        var resolver = new AppIdentityResolver();
        _backend = new ProxiFyreBackend(_config, _events, resolver);
        _backend.IpcClient = new ProxiFyreIpcClient();
        _sessionManager = new SessionManager(_launcher, _processMonitor, _events, _config, _backend, resolver);
        _sessionManager.RouteActivated += OnRouteActivated;

        // One-time migration: existing WindowsApps routes -> msix-package (Fix #1).
        // No-op for routes already migrated; safe to call on every startup.
        var migrated = AppIdentityResolver.MigrateWindowsAppsRoutes(_config.AppRoutes, _events);
        if (migrated > 0)
        {
            try { _backend.SaveSwitchConfig(); }
            catch { }
        }

        _sessionSupervisor = new SessionSupervisor(_sessionManager, _events);
        _sessionManager.SessionsChanged += () => _sessionSupervisor.RefreshWatcherState(_sessionManager.GetSessions());

        // Refresh ProxiFyre's app-config.json on every startup whenever the backend is
        // enabled — drops leftover tmp routes from last run, and (critically) writes an
        // empty rules block when the user has deleted all AppRoutes since last launch.
        // Without this, app-config.json would still hold the previously-deleted routes,
        // and the service would keep matching them after its next restart. We do NOT
        // restart the service here — no UAC popup on startup.
        if (_config.TransparentBackend.Enabled
            && _config.TransparentBackend.Type == "proxifyre")
        {
            try
            {
                _backend.WriteConfig();
                var persistent = _config.AppRoutes.Count(r => r.IsPersistent);
                _events.Add("StartupConfigRefresh", $"wrote {persistent} persistent route(s) into ProxiFyre config");
            }
            catch (Exception ex)
            {
                Logger.Error($"Startup config refresh failed: {ex.Message}");
            }
        }

        _monitor.Start();
        _monitor.StartPolling();

        // Start watching for external app starts that match persistent routes.
        _externalWatcher = new ExternalProcessWatcher(_config, _events);
        _externalWatcher.ProcessDetected += hit =>
        {
            try { _sessionManager.HandleProcessStarted(hit); }
            catch (Exception ex) { Logger.Error($"HandleProcessStarted failed: {ex.Message}"); }
        };
        _externalWatcher.Start();
    }

    /// <summary>
    /// Dispose runtime services that hold a reference to the current config snapshot.
    /// Used when reloading after Settings — services keep references to _config, so
    /// changing _config alone isn't enough.
    /// </summary>
    private void TearDownServices()
    {
        try { _monitor?.StopPolling(); } catch { }
        try { _monitor?.Dispose(); } catch { }
        try { _processMonitor?.Dispose(); } catch { }
        if (_sessionManager != null)
        {
            try { _sessionManager.RouteActivated -= OnRouteActivated; } catch { }
        }
        try { _sessionManager?.Dispose(); } catch { }
        try { _externalWatcher?.Dispose(); } catch { }
        try { _sessionSupervisor?.Dispose(); } catch { }
        try { _backend?.IpcClient?.Dispose(); } catch { }
        _monitor = null!;
        _processMonitor = null!;
        _sessionManager = null!;
        _externalWatcher = null;
        _launcher = null!;
        _backend = null;
    }

    /// <summary>
    /// Hot reload runtime state after Settings save. Tears down old monitors /
    /// launcher / backend / session manager, rebuilds them against the freshly-
    /// loaded _config, then rebuilds the tray menu. If Dashboard is open, it
    /// gets rebound in-place (no close + reopen — #6 in opus_review_v0.7.1).
    /// EventStore survives so the user keeps their event history.
    /// </summary>
    private void ReloadRuntimeServices()
    {
        TearDownServices();
        LoadConfig();
        SetupServices();
        BuildMenu();
        if (_dashboard != null && !_dashboard.IsDisposed)
        {
            try { _dashboard.Rebind(_config, _monitor, _sessionManager, _events, _launcher, _backend); }
            catch (Exception ex) { Logger.Error($"Dashboard.Rebind failed: {ex.Message}"); }
        }
        _events?.Add("RuntimeReloaded", "services rebuilt after settings change");
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

    /// <summary>
    /// Tray balloon when a drop-zone-launched session reaches active state. Fired
    /// from SessionManager.ApplyRoutingInBackgroundAsync on the background thread —
    /// marshal back via Invoke. Not fired for external attach or browser sessions.
    /// (plan_opus_v0.7)
    /// </summary>
    private void OnRouteActivated(Models.LaunchSession session)
    {
        if (InvokeRequired)
        {
            Invoke(() => OnRouteActivated(session));
            return;
        }
        try
        {
            var proxy = _config.Proxies.FirstOrDefault(p => p.Id == session.ProxyId);
            var portLabel = proxy != null ? $"Port {proxy.Port}" : "ProxiFyre";
            var backendLabel = proxy?.Name ?? "ProxiFyre";
            _tray.ShowBalloonTip(
                5000,
                $"{portLabel} hijack active",
                $"{session.Name} is now routed via {backendLabel}",
                ToolTipIcon.Info);
        }
        catch (Exception ex) { Logger.Error($"OnRouteActivated balloon failed: {ex.Message}"); }
    }

    private void BuildMenu()
    {
        _menu = new ContextMenuStrip();

        _menu.Items.Add("Open Dashboard", null, (_, _) => ShowDashboard());
        _menu.Items.Add(new ToolStripSeparator());

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

        // Routing backends (Clash Verge / v2ray): open app / open config
        if (_config.RoutingBackends.Count > 0)
        {
            var routing = new ToolStripMenuItem("Routing");
            foreach (var backend in _config.RoutingBackends)
            {
                var b = backend;
                var sub = new ToolStripMenuItem(b.Name);
                sub.DropDownItems.Add("Open App", null, (_, _) => _launcher.OpenRoutingApp(b.ProxyId));
                sub.DropDownItems.Add("Open Config", null, (_, _) => _launcher.OpenRoutingConfig(b.ProxyId));
                routing.DropDownItems.Add(sub);
            }
            _menu.Items.Add(routing);
            _menu.Items.Add(new ToolStripSeparator());
        }

        // Transparent backend (ProxiFyre) — surface file/folder open + status
        if (_backend != null && _config.TransparentBackend.Enabled && _config.TransparentBackend.Type == "proxifyre")
        {
            var prox = new ToolStripMenuItem("ProxiFyre");
            prox.DropDownItems.Add("Restart Service (UAC)", null, (_, _) =>
            {
                if (MessageBox.Show(
                        $"Restart ProxiFyre service?\n\nWindows will show a UAC prompt. " +
                        $"After you confirm, the service stops and restarts so any pending config changes take effect.",
                        "Restart ProxiFyre", MessageBoxButtons.YesNo, MessageBoxIcon.Question) == DialogResult.Yes)
                {
                    var ok = _backend.RestartServiceElevated();
                    if (ok)
                        MessageBox.Show("ProxiFyre service restarted.", "Done", MessageBoxButtons.OK, MessageBoxIcon.Information);
                    else
                        MessageBox.Show("Restart failed or was cancelled. Check the Events feed in Dashboard.", "Failed", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                }
            });
            prox.DropDownItems.Add(new ToolStripSeparator());
            prox.DropDownItems.Add("Open Config", null, (_, _) => _backend.OpenConfigFile());
            prox.DropDownItems.Add("Open Logs", null, (_, _) => _backend.OpenLogsFolder());
            prox.DropDownItems.Add("Open Folder", null, (_, _) => _backend.OpenBackendFolder());
            _menu.Items.Add(prox);
            _menu.Items.Add(new ToolStripSeparator());
        }

        _menu.Items.Add("Settings...", null, (_, _) => OpenSettings());
        _menu.Items.Add(new ToolStripSeparator());
        _menu.Items.Add("Quit", null, (_, _) => Application.Exit());

        _tray.ContextMenuStrip = _menu;
        UpdateTrayIcon();
    }

    /// <summary>
    /// Open the Settings dialog and trigger ReloadRuntimeServices on OK. Shared
    /// by the tray-menu Settings item and Dashboard's Settings tab (PR3b). Only
    /// DialogResult.OK reloads — Cancel / X-close stays a no-op
    /// (#7 in GPT review v0.7).
    ///
    /// Owner is optional: tray menu has no meaningful owner (MainForm is a 1×1
    /// hidden window), but Dashboard passes itself so the dialog stays Z-ordered
    /// above the Dashboard and inherits taskbar/Alt-Tab behavior
    /// (v0.8.1 opus review #3).
    /// </summary>
    private void OpenSettings(IWin32Window? owner = null)
    {
        using var form = new SettingsForm(_backend);
        if (form.ShowDialog(owner) == DialogResult.OK)
            ReloadRuntimeServices();
    }

    private void ShowDashboard()
    {
        if (_dashboard == null || _dashboard.IsDisposed)
        {
            _dashboard = new DashboardForm(_config, _monitor, _sessionManager, _events, _launcher, _backend);
            // Pass the Dashboard as owner so Settings opens above it (taskbar /
            // Alt-Tab / center-on-parent behave correctly — opus review #3).
            _dashboard.SettingsRequested += () => OpenSettings(_dashboard);
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
        int total = _config.Proxies.Count;
        int online = 0;
        var compactParts = new List<string>();
        foreach (var p in _config.Proxies)
        {
            bool isOn = _monitor.IsOnline(p.Host, p.Port);
            if (isOn) online++;
            compactParts.Add($"{p.Port} {(isOn ? "ON" : "OFF")}");
        }

        // NotifyIcon.Text is limited to 63 chars. Use compact per-proxy list if it fits,
        // otherwise fall back to an aggregate summary.
        var compact = $"ProxySwitch | {string.Join(" | ", compactParts)}";
        _tray.Text = compact.Length <= 63
            ? compact
            : $"ProxySwitch | {online}/{total} proxies online";

        Color color = total == 0
            ? Color.FromArgb(156, 163, 175)
            : online == total
                ? Color.FromArgb(34, 197, 94)
                : online > 0
                    ? Color.FromArgb(59, 130, 246)
                    : Color.FromArgb(156, 163, 175);

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

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        // Final cleanup of any tmp routes before disposing services. We don't
        // restart ProxiFyre here — that would mean a UAC prompt on app close.
        // Next ProxySwitch startup will WriteConfig with only persistent routes
        // again, which is when ProxiFyre service can be restarted to fully drop
        // the in-memory tmp rules. The on-disk file is clean immediately.
        try
        {
            if (_backend != null
                && _config.TransparentBackend.Enabled
                && _config.TransparentBackend.Type == "proxifyre"
                && _config.AppRoutes.Any(r => !r.IsPersistent))
            {
                var removed = _config.AppRoutes.RemoveAll(r => !r.IsPersistent);
                if (removed > 0)
                {
                    _events?.Add("TmpRoutesPurged", $"removed {removed} session-only route(s) on exit");
                    // Off-UI-thread + 2s timeout so a slow disk (AV scan, SMB, sleeping
                    // disk) can't block app shutdown. Loss is acceptable — next startup
                    // re-derives app-config.json from persistent routes anyway.
                    // (#7 in opus_review_v0.7.1)
                    var t = Task.Run(() =>
                    {
                        try
                        {
                            _backend.WriteConfig();
                            _backend.MarkStaleLiveRoutes("ProxySwitch closed with active tmp routes");
                        }
                        catch (Exception ex) { Logger.Error($"Exit WriteConfig: {ex.Message}"); }
                    });
                    if (!t.Wait(TimeSpan.FromSeconds(2)))
                        Logger.Error("Exit WriteConfig timed out (2s) — leaving for next startup to clean up");
                }
            }
        }
        catch (Exception ex) { Logger.Error($"Exit cleanup failed: {ex.Message}"); }

        _monitor?.Dispose();
        _processMonitor?.Dispose();
        _sessionManager?.Dispose();
        _externalWatcher?.Dispose();
        _backend?.IpcClient?.Dispose();
        _tray?.Dispose();
        base.OnFormClosing(e);
    }
}
