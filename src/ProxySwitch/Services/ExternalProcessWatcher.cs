using System.Management;
using ProxySwitch.Models;

namespace ProxySwitch.Services;

/// <summary>
/// Watches Windows process creation events (`__InstanceCreationEvent` on
/// `Win32_Process`) and raises <see cref="ProcessDetected"/> when a new
/// process matches a persistent <see cref="AppRoute"/>. Used for v0.7
/// "external launch" support: a user starts Obsidian from the Start Menu
/// and ProxySwitch still surfaces a session card.
///
/// Runs without admin: `__InstanceCreationEvent WITHIN N` is available to
/// the current user but slightly more CPU than ETW. We poll at 2s intervals
/// to balance latency vs cost. Re-call <see cref="RefreshWatchedSet"/> after
/// AppRoutes change (e.g. after Settings save).
/// </summary>
public class ExternalProcessWatcher : IDisposable
{
    private readonly ProxyConfig _config;
    private readonly EventStore _events;
    private ManagementEventWatcher? _watcher;
    private readonly object _setLock = new();
    private readonly HashSet<string> _watchedNames = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _watchedPaths = new(StringComparer.OrdinalIgnoreCase);

    public event Action<ExternalProcessHit>? ProcessDetected;

    public ExternalProcessWatcher(ProxyConfig config, EventStore events)
    {
        _config = config;
        _events = events;
    }

    public void Start()
    {
        RefreshWatchedSet();
        EnsureWatcher();
    }

    /// <summary>
    /// Recompute the watched exe-name / exe-path sets from current AppRoutes.
    /// Safe to call any time; starts the underlying WMI watcher lazily when
    /// the set becomes non-empty, stops it when empty.
    /// </summary>
    public void RefreshWatchedSet()
    {
        lock (_setLock)
        {
            _watchedNames.Clear();
            _watchedPaths.Clear();
            foreach (var route in _config.AppRoutes)
            {
                if (!route.IsPersistent || !route.Enabled) continue;
                var normalizedName = AppIdentityResolver.NormalizeProcessName(route.ProcessName);
                if (!string.IsNullOrEmpty(normalizedName)) _watchedNames.Add(normalizedName);
                if (!string.IsNullOrEmpty(route.ExePath)) _watchedPaths.Add(route.ExePath);
                if (!string.IsNullOrEmpty(route.ResolvedExePath)) _watchedPaths.Add(route.ResolvedExePath);
            }
        }
        EnsureWatcher();
    }

    private void EnsureWatcher()
    {
        bool hasWork;
        lock (_setLock) hasWork = _watchedNames.Count > 0 || _watchedPaths.Count > 0;

        if (hasWork && _watcher == null)
        {
            try
            {
                var query = new WqlEventQuery(
                    "__InstanceCreationEvent",
                    new TimeSpan(0, 0, 2),
                    "TargetInstance ISA 'Win32_Process'");
                _watcher = new ManagementEventWatcher(query);
                _watcher.EventArrived += OnEventArrived;
                _watcher.Start();
                _events.Add("ExternalWatcherStarted", $"watching {_watchedNames.Count} name(s) + {_watchedPaths.Count} path(s)");
            }
            catch (Exception ex)
            {
                Logger.Error($"ExternalProcessWatcher.EnsureWatcher: {ex.Message}");
            }
        }
        else if (!hasWork && _watcher != null)
        {
            StopWatcher();
            _events.Add("ExternalWatcherStopped", "no persistent routes to watch");
        }
    }

    private void StopWatcher()
    {
        try { _watcher?.Stop(); } catch { }
        try { _watcher?.Dispose(); } catch { }
        _watcher = null;
    }

    private void OnEventArrived(object sender, EventArrivedEventArgs e)
    {
        try
        {
            var inst = e.NewEvent["TargetInstance"] as ManagementBaseObject;
            if (inst == null) return;

            var name = inst["Name"]?.ToString() ?? "";
            var path = inst["ExecutablePath"]?.ToString() ?? "";
            var pidObj = inst["ProcessId"];
            var ppidObj = inst["ParentProcessId"];
            if (pidObj == null) return;
            int pid = Convert.ToInt32(pidObj);
            int? ppid = ppidObj != null ? Convert.ToInt32(ppidObj) : (int?)null;

            bool match;
            lock (_setLock)
            {
                match = (!string.IsNullOrEmpty(name) && _watchedNames.Contains(name))
                     || (!string.IsNullOrEmpty(path) && _watchedPaths.Contains(path));
            }
            if (!match) return;

            ProcessDetected?.Invoke(new ExternalProcessHit
            {
                ProcessId = pid,
                ParentProcessId = ppid,
                Name = name,
                ExecutablePath = path,
                CreatedAt = WmiTime.ParseDmtfDateTime(inst["CreationDate"]?.ToString()),
                SessionId = inst["SessionId"] != null ? Convert.ToInt32(inst["SessionId"]) : null
            });
        }
        catch (Exception ex)
        {
            Logger.Error($"ExternalProcessWatcher.OnEventArrived: {ex.Message}");
        }
    }

    public void Dispose() => StopWatcher();
}

public class ExternalProcessHit
{
    public int ProcessId { get; init; }
    public int? ParentProcessId { get; init; }
    public string Name { get; init; } = "";
    public string ExecutablePath { get; init; } = "";
    public DateTime? CreatedAt { get; init; }
    public int? SessionId { get; init; }
}
