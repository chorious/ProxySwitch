using ProxySwitch.Models;

namespace ProxySwitch.Services;

public sealed class SessionManager : IDisposable
{
    private readonly List<LaunchSession> _sessions = new();
    private readonly AppLauncher _launcher;
    private readonly ProcessMonitor _processMonitor;
    private readonly EventStore _events;

    public event Action? SessionsChanged;

    public SessionManager(AppLauncher launcher, ProcessMonitor monitor, EventStore events)
    {
        _launcher = launcher;
        _processMonitor = monitor;
        _events = events;
    }

    public IReadOnlyList<LaunchSession> GetSessions()
    {
        lock (_sessions) return _sessions.ToList().AsReadOnly();
    }

    public LaunchResult LaunchBrowser(AppConfig app, string mode, string? proxyId = null)
    {
        _events.Add("SessionCreated", $"Session for {app.Name}");
        _events.Add("LaunchStarted", $"Starting {app.Name} in {mode} mode");

        var result = _launcher.LaunchBrowser(app);

        if (!result.Success)
        {
            _events.Add("LaunchFailed", $"{app.Name}: {result.Error}");
            return result;
        }

        var session = new LaunchSession
        {
            Name = app.Name,
            ExePath = app.Exe,
            Mode = mode,
            ProxyId = proxyId,
            UserDataDir = app.UserDataDir,
            MainProcessId = result.ProcessId,
            Status = "running"
        };

        lock (_sessions) _sessions.Add(session);
        SessionsChanged?.Invoke();
        _events.Add("LaunchSucceeded", $"{app.Name} started, PID={result.ProcessId}");

        // Track browser by userDataDir
        if (!string.IsNullOrEmpty(app.UserDataDir))
        {
            _processMonitor.TrackBrowser(app.UserDataDir, pids =>
            {
                lock (session.ProcessIds)
                {
                    session.ProcessIds.Clear();
                    session.ProcessIds.AddRange(pids);
                }
                if (pids.Count > 0)
                    _events.Add("ProcessDetected", $"{app.Name}: {pids.Count} processes");
                SessionsChanged?.Invoke();
            });

            // Also track main PID as fallback
            if (result.ProcessId.HasValue)
            {
                _processMonitor.TrackPid(result.ProcessId.Value, () =>
                {
                    if (session.Status == "running")
                    {
                        _processMonitor.StopTrackingBrowser(app.UserDataDir);
                        session.Status = "exited";
                        session.ExitedAt = DateTime.Now;
                        _events.Add("ProcessExited", $"{app.Name} exited");
                        SessionsChanged?.Invoke();
                    }
                });
            }
        }

        return new LaunchResult
        {
            Success = true,
            ProcessId = result.ProcessId,
            Arguments = result.Arguments,
            Session = session
        };
    }

    public LaunchResult LaunchGeneric(string exePath, string name, string mode, string? proxyId = null)
    {
        _events.Add("SessionCreated", $"Session for {name}");
        _events.Add("LaunchStarted", $"Starting {name} in {mode} mode");

        var result = _launcher.LaunchGeneric(exePath);

        if (!result.Success)
        {
            _events.Add("LaunchFailed", $"{name}: {result.Error}");
            return result;
        }

        var session = new LaunchSession
        {
            Name = name,
            ExePath = exePath,
            Mode = mode,
            ProxyId = proxyId,
            MainProcessId = result.ProcessId,
            Status = "running"
        };

        lock (_sessions) _sessions.Add(session);
        SessionsChanged?.Invoke();
        _events.Add("LaunchSucceeded", $"{name} started, PID={result.ProcessId}");

        if (result.ProcessId.HasValue)
        {
            _processMonitor.TrackPid(result.ProcessId.Value, () =>
            {
                if (session.Status == "running")
                {
                    session.Status = "exited";
                    session.ExitedAt = DateTime.Now;
                    _events.Add("ProcessExited", $"{name} exited");
                    SessionsChanged?.Invoke();
                }
            });
        }

        return new LaunchResult
        {
            Success = true,
            ProcessId = result.ProcessId,
            Arguments = result.Arguments,
            Session = session
        };
    }

    public void StopSession(LaunchSession session)
    {
        try
        {
            if (session.MainProcessId.HasValue && ProcessMonitor.IsAlive(session.MainProcessId.Value))
            {
                var p = System.Diagnostics.Process.GetProcessById(session.MainProcessId.Value);
                p.Kill();
            }
            if (!string.IsNullOrEmpty(session.UserDataDir))
                _processMonitor.StopTrackingBrowser(session.UserDataDir);
        }
        catch (Exception ex)
        {
            Logger.Error($"Failed to stop session {session.Name}: {ex.Message}");
        }
    }

    public void RemoveSession(LaunchSession session)
    {
        lock (_sessions)
        {
            _sessions.Remove(session);
        }
        SessionsChanged?.Invoke();
    }

    public void Dispose()
    {
        // ProcessMonitor is owned by MainForm, do not dispose here
    }
}
