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
    /// States: "disabled" / "not-configured" / "exe-missing" / "service-not-installed" /
    ///         "stopped" / "running"
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

        // Probe service existence — ServiceController ctor does NOT throw on missing service,
        // but reading Status will throw InvalidOperationException.
        bool serviceInstalled = false;
        bool serviceRunning = false;
        if (!string.IsNullOrEmpty(be.ServiceName))
        {
            try
            {
                using var sc = new ServiceController(be.ServiceName);
                _ = sc.Status; // probe
                serviceInstalled = true;
                serviceRunning = sc.Status == ServiceControllerStatus.Running;
            }
            catch (InvalidOperationException)
            {
                serviceInstalled = false;
            }
            catch (Exception ex)
            {
                Logger.Error($"ServiceController probe failed: {ex.Message}");
            }
        }

        bool processRunning = IsProcessRunning();

        if (!serviceInstalled && !processRunning)
            return new ProxiFyreStatus
            {
                State = "service-not-installed",
                Message = $"service '{be.ServiceName}' is not installed (run `{Path.GetFileName(be.Exe)} install` as admin)",
                ConfigPath = be.ConfigPath
            };

        var state = (processRunning || serviceRunning) ? "running" : "stopped";
        return new ProxiFyreStatus
        {
            State = state,
            ConfigPath = be.ConfigPath,
            ServiceRunning = serviceRunning,
            ProcessRunning = processRunning,
            ServiceInstalled = serviceInstalled,
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
    /// Creates a timestamped .bak each call, keeping only the 5 most recent.
    /// </summary>
    public string WriteConfig()
    {
        var path = _config.TransparentBackend.ConfigPath;
        if (string.IsNullOrEmpty(path))
            throw new InvalidOperationException("TransparentBackend.ConfigPath is not set.");

        Directory.CreateDirectory(Path.GetDirectoryName(path) ?? ".");

        // Timestamped backup of any existing config (every write, not just first).
        if (File.Exists(path))
        {
            try
            {
                var newJson = BuildConfigJson();
                var existing = File.ReadAllText(path);
                if (existing != newJson)
                {
                    var stamp = DateTime.Now.ToString("yyyyMMdd-HHmmss");
                    var bak = $"{path}.{stamp}.bak";
                    File.Copy(path, bak, overwrite: false);
                    _events.Add("ProxiFyreConfigBackup", $"saved {Path.GetFileName(bak)}");
                    PruneOldBackups(path, keep: 5);
                }
            }
            catch (Exception ex)
            {
                Logger.Error($"Backup failed: {ex.Message}");
            }
        }

        var tmp = path + ".tmp";
        File.WriteAllText(tmp, BuildConfigJson());
        File.Move(tmp, path, overwrite: true);
        _events.Add("ProxiFyreConfigWritten", $"{Path.GetFileName(path)} updated");
        Logger.Info($"ProxiFyre config written: {path}");
        return path;
    }

    private static void PruneOldBackups(string configPath, int keep)
    {
        try
        {
            var dir = Path.GetDirectoryName(configPath);
            var prefix = Path.GetFileName(configPath) + ".";
            if (string.IsNullOrEmpty(dir)) return;
            var baks = Directory.GetFiles(dir, prefix + "*.bak")
                .OrderByDescending(f => File.GetLastWriteTimeUtc(f))
                .Skip(keep);
            foreach (var b in baks)
            {
                try { File.Delete(b); } catch { }
            }
        }
        catch { }
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
    /// Restart path: try in-process ServiceController first (works when ProxySwitch
    /// is already admin); fall back to UAC-elevated helper. Falls through to
    /// NeedsManualRestart only if both paths fail or user declines UAC.
    /// </summary>
    public ApplyResult Apply(bool restartService)
    {
        var be = _config.TransparentBackend;
        if (!be.Enabled || be.Type != "proxifyre")
            return new ApplyResult { Success = false, Reason = "backend disabled" };

        try
        {
            var path = WriteConfig();

            if (!restartService)
            {
                _events.Add("ProxiFyreApplied", "config written (restart not requested)");
                return new ApplyResult { Success = true, ConfigPath = path };
            }

            // Try direct restart first if user opted into ManageService (no UAC popup).
            if (be.ManageService && RestartService())
            {
                _events.Add("ProxiFyreApplied", "config written + service restarted (in-process)");
                return new ApplyResult { Success = true, ConfigPath = path, ServiceRestarted = true };
            }

            // Fall back to UAC-elevated helper. Shows ONE prompt to the user.
            if (RestartServiceElevated())
            {
                _events.Add("ProxiFyreApplied", "config written + service restarted (UAC)");
                return new ApplyResult { Success = true, ConfigPath = path, ServiceRestarted = true };
            }

            // Either user clicked No on UAC, or the helper failed.
            _events.Add("ManualRestartRequired",
                $"config written; restart {be.ServiceName} manually to apply changes");
            return new ApplyResult
            {
                Success = true,
                ConfigPath = path,
                NeedsManualRestart = true
            };
        }
        catch (Exception ex)
        {
            _events.Add("ProxiFyreApplyFailed", ex.Message);
            Logger.Error($"ProxiFyre apply failed: {ex}");
            return new ApplyResult { Success = false, Reason = ex.Message };
        }
    }

    /// <summary>Open the generated app-config.json in the default text editor.</summary>
    public bool OpenConfigFile()
    {
        var path = _config.TransparentBackend.ConfigPath;
        if (string.IsNullOrEmpty(path) || !File.Exists(path)) return false;
        try
        {
            Process.Start(new ProcessStartInfo { FileName = path, UseShellExecute = true });
            return true;
        }
        catch (Exception ex) { Logger.Error($"OpenConfigFile failed: {ex.Message}"); return false; }
    }

    /// <summary>Open the ProxiFyre logs directory in Explorer.</summary>
    public bool OpenLogsFolder()
    {
        var exe = _config.TransparentBackend.Exe;
        if (string.IsNullOrEmpty(exe)) return false;
        var logsDir = Path.Combine(Path.GetDirectoryName(exe) ?? ".", "logs");
        if (!Directory.Exists(logsDir))
        {
            try { Directory.CreateDirectory(logsDir); } catch { }
        }
        try
        {
            Process.Start(new ProcessStartInfo { FileName = "explorer.exe", Arguments = $"\"{logsDir}\"", UseShellExecute = true });
            return true;
        }
        catch (Exception ex) { Logger.Error($"OpenLogsFolder failed: {ex.Message}"); return false; }
    }

    /// <summary>Open the ProxiFyre install folder in Explorer.</summary>
    public bool OpenBackendFolder()
    {
        var exe = _config.TransparentBackend.Exe;
        if (string.IsNullOrEmpty(exe)) return false;
        var dir = Path.GetDirectoryName(exe) ?? ".";
        if (!Directory.Exists(dir)) return false;
        try
        {
            Process.Start(new ProcessStartInfo { FileName = "explorer.exe", Arguments = $"\"{dir}\"", UseShellExecute = true });
            return true;
        }
        catch (Exception ex) { Logger.Error($"OpenBackendFolder failed: {ex.Message}"); return false; }
    }

    /// <summary>
    /// Restart the ProxiFyre Windows Service using ServiceController.
    /// Requires the calling process to already be elevated; returns false otherwise.
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
            Logger.Info($"Service {be.ServiceName} restarted via ServiceController");
            return true;
        }
        catch (Exception ex)
        {
            Logger.Error($"Service restart (in-process) failed: {ex.Message}");
            return false;
        }
    }

    /// <summary>
    /// Restart the ProxiFyre service by spawning an elevated helper process
    /// (cmd.exe + sc.exe) with `runas` verb. The OS shows a single UAC prompt;
    /// after the user accepts, the restart happens silently. Returns false if
    /// the user declined UAC or sc.exe failed.
    /// </summary>
    public bool RestartServiceElevated()
    {
        var name = _config.TransparentBackend.ServiceName;
        if (string.IsNullOrEmpty(name)) return false;

        try
        {
            // sc stop, wait, sc start. Hide window via cmd /c so user doesn't see
            // a black flash beyond the UAC prompt itself.
            var psi = new ProcessStartInfo
            {
                FileName = "cmd.exe",
                Arguments = $"/c sc.exe stop \"{name}\" >nul 2>&1 & timeout /t 1 /nobreak >nul & sc.exe start \"{name}\" >nul 2>&1",
                UseShellExecute = true,
                Verb = "runas",
                WindowStyle = ProcessWindowStyle.Hidden,
                CreateNoWindow = true
            };
            using var p = Process.Start(psi);
            if (p == null) return false;
            if (!p.WaitForExit(20000))
            {
                try { p.Kill(); } catch { }
                _events.Add("ServiceRestartTimeout", $"{name}: elevated restart did not finish in 20s");
                return false;
            }

            // Verify the service is actually running now.
            using var sc = new ServiceController(name);
            sc.Refresh();
            if (sc.Status == ServiceControllerStatus.Running)
            {
                Logger.Info($"Service {name} restarted via UAC-elevated helper");
                _events.Add("ServiceRestarted", $"{name}: restarted (elevated)");
                return true;
            }
            _events.Add("ServiceRestartFailed", $"{name}: still not running after elevated restart");
            return false;
        }
        catch (System.ComponentModel.Win32Exception ex) when ((uint)ex.NativeErrorCode == 0x800704C7)
        {
            // ERROR_CANCELLED — user clicked No on UAC prompt
            _events.Add("ServiceRestartDeclined", $"{name}: user declined UAC");
            return false;
        }
        catch (Exception ex)
        {
            Logger.Error($"RestartServiceElevated failed: {ex.Message}");
            _events.Add("ServiceRestartFailed", $"{name}: {ex.Message}");
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
    /// "disabled" | "not-configured" | "exe-missing" |
    /// "service-not-installed" | "stopped" | "running"
    /// </summary>
    public string State { get; set; } = "disabled";
    public string? Message { get; set; }
    public string? ConfigPath { get; set; }
    public bool ServiceInstalled { get; set; }
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
    /// <summary>
    /// True when caller asked for restart but ManageService is off — config
    /// was written, user must restart ProxiFyre manually for it to take effect.
    /// </summary>
    public bool NeedsManualRestart { get; set; }
}
