using System.Diagnostics;
using System.ServiceProcess;
using System.Text.Json;
using System.Text.Json.Serialization;
using ProxySwitch.Models;

namespace ProxySwitch.Services;

/// <summary>
/// Generates and applies ProxiFyre's `app-config.json` from ProxySwitch's
/// internal AppRoute model. Does NOT install drivers, does NOT auto-elevate.
/// </summary>
public class ProxiFyreBackend
{
    private readonly ProxyConfig _config;
    private readonly EventStore _events;

    public ProxiFyreBackend(ProxyConfig config, EventStore events)
    {
        _config = config;
        _events = events;
    }

    /// <summary>
    /// Combined backend status for the Dashboard panel.
    /// </summary>
    public ProxiFyreStatus GetStatus()
    {
        var be = _config.TransparentBackend;
        if (!be.Enabled || be.Type != "proxifyre")
            return new ProxiFyreStatus { State = "disabled" };

        if (string.IsNullOrEmpty(be.Exe))
            return new ProxiFyreStatus { State = "not-configured", Message = "exe path not set" };
        if (!File.Exists(be.Exe))
            return new ProxiFyreStatus { State = "exe-missing", Message = $"exe not found: {be.Exe}" };
        if (string.IsNullOrEmpty(be.ConfigPath))
            return new ProxiFyreStatus { State = "not-configured", Message = "config path not set" };

        bool processRunning = IsProcessRunning();
        bool serviceRunning = false;
        try
        {
            using var sc = new ServiceController(be.ServiceName);
            serviceRunning = sc.Status == ServiceControllerStatus.Running;
        }
        catch { /* service may not exist */ }

        var state = (processRunning || serviceRunning) ? "running" : "stopped";
        return new ProxiFyreStatus
        {
            State = state,
            ConfigPath = be.ConfigPath,
            ServiceRunning = serviceRunning,
            ProcessRunning = processRunning,
            ManagedService = be.ManageService
        };
    }

    private bool IsProcessRunning()
    {
        try
        {
            var name = Path.GetFileNameWithoutExtension(_config.TransparentBackend.Exe);
            if (string.IsNullOrEmpty(name)) return false;
            return Process.GetProcessesByName(name).Length > 0;
        }
        catch { return false; }
    }

    /// <summary>
    /// Build the ProxiFyre app-config.json content from current proxies + appRoutes.
    /// Same-proxy AppRoutes are merged into one ProxiFyre rule (appNames list).
    /// </summary>
    public string BuildConfigJson()
    {
        var endpointByProxyId = _config.Proxies.ToDictionary(p => p.Id, p => $"{p.Host}:{p.Port}");

        var grouped = _config.AppRoutes
            .Where(r => r.Enabled && endpointByProxyId.ContainsKey(r.ProxyId))
            .GroupBy(r => r.ProxyId);

        var rules = new List<ProxiFyreRule>();
        foreach (var g in grouped)
        {
            var apps = new List<string>();
            foreach (var route in g)
            {
                // ExePath preferred (full path match in ProxiFyre); fallback to ProcessName.
                if (!string.IsNullOrEmpty(route.ExePath))
                    apps.Add(route.ExePath);
                else if (!string.IsNullOrEmpty(route.ProcessName))
                    apps.Add(route.ProcessName);
            }
            apps = apps.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            if (apps.Count == 0) continue;

            rules.Add(new ProxiFyreRule
            {
                AppNames = apps,
                Socks5ProxyEndpoint = endpointByProxyId[g.Key],
                SupportedProtocols = new() { "TCP", "UDP" }
            });
        }

        var root = new ProxiFyreRoot
        {
            LogLevel = "Error",
            BypassLan = true,
            Proxies = rules
        };

        var options = new JsonSerializerOptions
        {
            WriteIndented = true,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
        };
        return JsonSerializer.Serialize(root, options);
    }

    /// <summary>
    /// Atomically write the generated config to disk. Returns the final path.
    /// </summary>
    public string WriteConfig()
    {
        var path = _config.TransparentBackend.ConfigPath;
        if (string.IsNullOrEmpty(path))
            throw new InvalidOperationException("TransparentBackend.ConfigPath is not set.");

        Directory.CreateDirectory(Path.GetDirectoryName(path) ?? ".");

        // One-time backup of any pre-existing user config.
        var bak = path + ".bak";
        if (File.Exists(path) && !File.Exists(bak))
        {
            try { File.Copy(path, bak); }
            catch (Exception ex) { Logger.Error($"Backup failed: {ex.Message}"); }
        }

        var tmp = path + ".tmp";
        File.WriteAllText(tmp, BuildConfigJson());
        File.Move(tmp, path, overwrite: true);
        _events.Add("ProxiFyreConfigWritten", $"{Path.GetFileName(path)} updated");
        Logger.Info($"ProxiFyre config written: {path}");
        return path;
    }

    /// <summary>
    /// Add or update an AppRoute for the given exe + proxy. Returns true if
    /// the route list actually changed.
    /// </summary>
    public bool EnsureRoute(string exePath, string proxyId, string source = "drop-zone")
    {
        if (string.IsNullOrEmpty(exePath) || string.IsNullOrEmpty(proxyId)) return false;

        var procName = Path.GetFileName(exePath);
        var existing = _config.AppRoutes.FirstOrDefault(r =>
            string.Equals(r.ExePath, exePath, StringComparison.OrdinalIgnoreCase) &&
            r.ProxyId == proxyId);

        if (existing != null)
        {
            if (existing.Enabled) return false;
            existing.Enabled = true;
            _events.Add("AppRouteUpdated", $"{procName} -> {proxyId} re-enabled");
            return true;
        }

        var route = new AppRoute
        {
            Id = $"{Path.GetFileNameWithoutExtension(exePath)}-{proxyId}-{Guid.NewGuid().ToString("N")[..6]}",
            Name = $"{procName} via {proxyId}",
            ExePath = exePath,
            ProcessName = procName,
            ProxyId = proxyId,
            Enabled = true,
            Source = source
        };
        _config.AppRoutes.Add(route);
        _events.Add("AppRouteAdded", $"{procName} -> {proxyId} (source: {source})");
        return true;
    }

    /// <summary>
    /// Regenerate config + optionally restart the backend.
    /// Returns ApplyResult describing what happened.
    /// </summary>
    public ApplyResult Apply(bool restartService)
    {
        var be = _config.TransparentBackend;
        if (!be.Enabled || be.Type != "proxifyre")
            return new ApplyResult { Success = false, Reason = "backend disabled" };

        try
        {
            var path = WriteConfig();

            if (restartService && be.ManageService)
            {
                if (RestartService())
                {
                    _events.Add("ProxiFyreApplied", "config written + service restarted");
                    return new ApplyResult { Success = true, ConfigPath = path, ServiceRestarted = true };
                }
                _events.Add("ProxiFyreApplied", "config written, service restart FAILED");
                return new ApplyResult { Success = false, ConfigPath = path, Reason = "service restart failed" };
            }

            _events.Add("ProxiFyreApplied", "config written (service not restarted)");
            return new ApplyResult { Success = true, ConfigPath = path };
        }
        catch (Exception ex)
        {
            _events.Add("ProxiFyreApplyFailed", ex.Message);
            Logger.Error($"ProxiFyre apply failed: {ex}");
            return new ApplyResult { Success = false, Reason = ex.Message };
        }
    }

    /// <summary>
    /// Restart the ProxiFyre Windows Service using ServiceController.
    /// Requires admin privileges; will return false on access denied.
    /// </summary>
    public bool RestartService()
    {
        var be = _config.TransparentBackend;
        if (!be.ManageService || string.IsNullOrEmpty(be.ServiceName)) return false;

        try
        {
            using var sc = new ServiceController(be.ServiceName);
            if (sc.Status != ServiceControllerStatus.Stopped &&
                sc.Status != ServiceControllerStatus.StopPending)
            {
                sc.Stop();
                sc.WaitForStatus(ServiceControllerStatus.Stopped, TimeSpan.FromSeconds(10));
            }
            sc.Start();
            sc.WaitForStatus(ServiceControllerStatus.Running, TimeSpan.FromSeconds(10));
            Logger.Info($"Service {be.ServiceName} restarted");
            return true;
        }
        catch (Exception ex)
        {
            Logger.Error($"Service restart failed: {ex.Message}");
            return false;
        }
    }

    // ---- ProxiFyre JSON shape (matches their README schema) ----

    private class ProxiFyreRoot
    {
        [JsonPropertyName("logLevel")]
        public string LogLevel { get; set; } = "Error";

        [JsonPropertyName("bypassLan")]
        public bool BypassLan { get; set; } = true;

        [JsonPropertyName("proxies")]
        public List<ProxiFyreRule> Proxies { get; set; } = new();
    }

    private class ProxiFyreRule
    {
        [JsonPropertyName("appNames")]
        public List<string> AppNames { get; set; } = new();

        [JsonPropertyName("socks5ProxyEndpoint")]
        public string Socks5ProxyEndpoint { get; set; } = "";

        [JsonPropertyName("username")]
        public string? Username { get; set; }

        [JsonPropertyName("password")]
        public string? Password { get; set; }

        [JsonPropertyName("supportedProtocols")]
        public List<string> SupportedProtocols { get; set; } = new() { "TCP", "UDP" };
    }
}

public class ProxiFyreStatus
{
    /// <summary>
    /// "disabled" | "not-configured" | "exe-missing" | "stopped" | "running"
    /// </summary>
    public string State { get; set; } = "disabled";
    public string? Message { get; set; }
    public string? ConfigPath { get; set; }
    public bool ServiceRunning { get; set; }
    public bool ProcessRunning { get; set; }
    public bool ManagedService { get; set; }
}

public class ApplyResult
{
    public bool Success { get; set; }
    public string? Reason { get; set; }
    public string? ConfigPath { get; set; }
    public bool ServiceRestarted { get; set; }
}
