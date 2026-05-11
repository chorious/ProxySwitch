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
            RootProcessId = result.ProcessId,
            Status = "running",
            RoutingStatus = mode == "direct" ? "direct" : "browser-arg"
        };

        if (result.ProcessId.HasValue)
        {
            session.Processes.Add(new TrackedProcess
            {
                ProcessId = result.ProcessId.Value,
                Name = Path.GetFileName(app.Exe),
                ExecutablePath = app.Exe,
                Role = "root",
                CreatedAt = DateTime.Now
            });
        }

        lock (_sessions) _sessions.Add(session);
        SessionsChanged?.Invoke();
        _events.Add("LaunchSucceeded", $"{app.Name} started, PID={result.ProcessId}");

        // Track browser by userDataDir
        if (!string.IsNullOrEmpty(app.UserDataDir))
        {
            _processMonitor.TrackBrowser(app.UserDataDir, pids =>
            {
                lock (session.Processes)
                {
                    // Remove processes no longer in the group
                    session.Processes.RemoveAll(p => p.Role == "descendant" && !pids.Contains(p.ProcessId));
                    // Add new ones
                    foreach (var pid in pids)
                    {
                        if (pid == session.RootProcessId) continue;
                        if (!session.Processes.Any(p => p.ProcessId == pid))
                        {
                            session.Processes.Add(new TrackedProcess
                            {
                                ProcessId = pid,
                                Name = Path.GetFileName(app.Exe),
                                Role = "descendant",
                                CreatedAt = DateTime.Now
                            });
                        }
                    }
                }
                if (pids.Count > 0)
                    _events.Add("ProcessDetected", $"{app.Name}: {pids.Count} processes");
                SessionsChanged?.Invoke();
            });

            // Also track root PID as fallback
            if (result.ProcessId.HasValue)
            {
                _processMonitor.TrackPid(result.ProcessId.Value, () =>
                {
                    if (session.Status == "running")
                    {
                        _processMonitor.StopTrackingBrowser(app.UserDataDir);
                        MarkProcessExited(session, result.ProcessId.Value);
                        if (!session.HasLiveProcesses)
                        {
                            session.Status = "exited";
                            session.ExitedAt = DateTime.Now;
                            _events.Add("ProcessExited", $"{app.Name} exited");
                        }
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
            RootProcessId = result.ProcessId,
            Status = "running",
            RoutingStatus = mode == "direct" ? "direct" : "profile-loaded"
        };

        if (result.ProcessId.HasValue)
        {
            session.Processes.Add(new TrackedProcess
            {
                ProcessId = result.ProcessId.Value,
                Name = Path.GetFileName(exePath),
                ExecutablePath = exePath,
                Role = "root",
                CreatedAt = DateTime.Now
            });
        }

        lock (_sessions) _sessions.Add(session);
        SessionsChanged?.Invoke();
        _events.Add("LaunchSucceeded", $"{name} started, PID={result.ProcessId}");

        // Track process tree for 10 seconds to catch descendants
        if (result.ProcessId.HasValue)
        {
            _ = _processMonitor.TrackProcessTreeAsync(
                result.ProcessId.Value,
                durationSeconds: 10,
                onDescendant: desc =>
                {
                    lock (session.Processes)
                    {
                        if (!session.Processes.Any(p => p.ProcessId == desc.ProcessId))
                        {
                            session.Processes.Add(desc);
                        }
                    }
                    _events.Add("ChildProcessDetected", $"{name}: child {desc.Name} (PID={desc.ProcessId})");
                    SessionsChanged?.Invoke();
                });

            // Track root PID liveness
            _processMonitor.TrackPid(result.ProcessId.Value, () =>
            {
                MarkProcessExited(session, result.ProcessId.Value);
                _events.Add("RootProcessExited", $"{name} root process exited");

                // Check for descendants
                if (session.HasLiveProcesses)
                {
                    session.Status = "running-via-child";
                    session.IsLauncherHandoffDetected = true;

                    var liveChild = session.Processes.FirstOrDefault(p => p.ExitedAt == null && p.Role == "descendant");
                    if (liveChild != null && !session.IsRoutingAssisted)
                    {
                        session.RoutingWarning = $"{liveChild.Name} is running but routing is unverified. Proxifier may need a separate rule.";
                        _events.Add("RoutingUnverified", $"{name}: {liveChild.Name} running, routing unverified");
                    }
                    else
                    {
                        _events.Add("LauncherHandoffDetected", $"{name}: launcher exited, child still running");
                    }
                }
                else
                {
                    session.Status = "exited";
                    session.ExitedAt = DateTime.Now;
                    _events.Add("SessionExited", $"{name} exited");
                }
                SessionsChanged?.Invoke();
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

    private static void MarkProcessExited(LaunchSession session, int pid)
    {
        lock (session.Processes)
        {
            var proc = session.Processes.FirstOrDefault(p => p.ProcessId == pid);
            if (proc != null)
                proc.ExitedAt = DateTime.Now;
        }
    }

    public void AssistRouting(LaunchSession session, string profileKey)
    {
        _launcher.LoadProxifierProfile(profileKey);
        session.IsRoutingAssisted = true;
        session.RoutingStatus = "assisted";
        session.RoutingWarning = null;
        _events.Add("AssistRuleAdded", $"{session.Name}: routing assisted via {profileKey}");
        SessionsChanged?.Invoke();
    }

    public void StopSession(LaunchSession session)
    {
        try
        {
            // Kill all tracked live processes
            foreach (var proc in session.Processes.Where(p => p.ExitedAt == null).ToList())
            {
                try
                {
                    if (ProcessMonitor.IsAlive(proc.ProcessId))
                    {
                        var p = System.Diagnostics.Process.GetProcessById(proc.ProcessId);
                        p.Kill();
                    }
                }
                catch { }
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
