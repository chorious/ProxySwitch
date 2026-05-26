using System.Management;
using ProxySwitch.Models;

namespace ProxySwitch.Services;

/// <summary>
/// Long-lived process supervision while any LaunchSession is active.
/// Maintains a WMI __InstanceCreationEvent watcher that feeds ALL process
/// creation events into SessionManager.HandleProcessStarted. This catches
/// late children (spawned after the 10s TrackProcessTreeAsync window) and
/// restarted processes during the grace window.
///
/// The watcher is started lazily when the first session becomes active and
/// stopped when the last session exits or finalizes.
/// </summary>
public class SessionSupervisor : IDisposable
{
    private readonly SessionManager _sessionManager;
    private readonly EventStore _events;
    private ManagementEventWatcher? _watcher;
    private readonly object _lock = new();

    public SessionSupervisor(SessionManager sessionManager, EventStore events)
    {
        _sessionManager = sessionManager;
        _events = events;
    }

    /// <summary>
    /// Call whenever session count or status changes. Starts the watcher if
    /// any session is active; stops it when none are.
    /// </summary>
    public void RefreshWatcherState(IReadOnlyList<LaunchSession> sessions)
    {
        bool shouldRun = sessions.Any(s =>
            s.Status is "starting" or "running" or "running-via-child"
            or "running-via-correlated" or "waiting-for-restart");

        lock (_lock)
        {
            if (shouldRun && _watcher == null)
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
                    _events.Add("SessionSupervisorStarted", "watching for late children / restarts");
                }
                catch (Exception ex)
                {
                    Logger.Error($"SessionSupervisor.RefreshWatcherState start failed: {ex.Message}");
                }
            }
            else if (!shouldRun && _watcher != null)
            {
                StopWatcher();
                _events.Add("SessionSupervisorStopped", "no active sessions");
            }
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

            _sessionManager.HandleProcessStarted(new ExternalProcessHit
            {
                ProcessId = pid,
                ParentProcessId = ppid,
                Name = name,
                ExecutablePath = path,
                CreatedAt = ParseWmiDate(inst["CreationDate"]?.ToString()),
                SessionId = inst["SessionId"] != null ? Convert.ToInt32(inst["SessionId"]) : null
            });
        }
        catch (Exception ex)
        {
            Logger.Error($"SessionSupervisor.OnEventArrived: {ex.Message}");
        }
    }

    private static DateTime? ParseWmiDate(string? wmiDate)
    {
        if (string.IsNullOrEmpty(wmiDate) || wmiDate.Length < 14) return null;
        if (DateTime.TryParseExact(wmiDate[..14], "yyyyMMddHHmmss",
            System.Globalization.CultureInfo.InvariantCulture,
            System.Globalization.DateTimeStyles.None, out var dt))
            return dt;
        return null;
    }

    public void Dispose() => StopWatcher();
}
