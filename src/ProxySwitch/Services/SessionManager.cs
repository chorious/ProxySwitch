using ProxySwitch.Models;

namespace ProxySwitch.Services;

public sealed class SessionManager : IDisposable
{
    private readonly List<LaunchSession> _sessions = new();
    private readonly Dictionary<string, Task<List<ProcessSnapshot>>> _baselines = new();
    private readonly Dictionary<string, CancellationTokenSource> _sessionCts = new();
    private readonly AppLauncher _launcher;
    private readonly ProcessMonitor _processMonitor;
    private readonly EventStore _events;
    private readonly HandoffScorer _scorer;
    private readonly ProxyConfig _config;
    private readonly ProxiFyreBackend? _backend;

    public event Action? SessionsChanged;
    public event Action<LaunchSession, List<HandoffCandidate>>? CorrelatedCandidatesFound;

    public SessionManager(AppLauncher launcher, ProcessMonitor monitor, EventStore events, ProxyConfig config, ProxiFyreBackend? backend = null)
    {
        _launcher = launcher;
        _processMonitor = monitor;
        _events = events;
        _config = config;
        _backend = backend;
        _scorer = new HandoffScorer();
    }

    public IReadOnlyList<LaunchSession> GetSessions()
    {
        lock (_sessions) return _sessions.ToList().AsReadOnly();
    }

    public LaunchResult LaunchBrowser(AppConfig app, string mode, string? proxyId = null)
    {
        _events.Add("SessionCreated", $"Session for {app.Name}");
        _events.Add("LaunchStarted", $"Starting {app.Name} ({mode})");

        var result = _launcher.LaunchBrowser(app);

        if (!result.Success)
        {
            _events.Add("LaunchFailed", $"{app.Name}: {result.Error}");
            return result;
        }

        var rootCreatedAt = result.ProcessId.HasValue
            ? (ProcessMonitor.GetProcessStartTime(result.ProcessId.Value) ?? DateTime.Now)
            : DateTime.Now;

        var session = new LaunchSession
        {
            Name = app.Name,
            ExePath = app.Exe,
            Kind = "browser",
            Mode = mode,
            ProxyId = proxyId,
            UserDataDir = app.UserDataDir,
            RootProcessId = result.ProcessId,
            RootCreatedAt = rootCreatedAt,
            Status = "running",
            RoutingStatus = mode == "browser-direct" ? "direct" : "browser-proxy-active"
        };

        if (result.ProcessId.HasValue)
        {
            session.Processes.Add(new TrackedProcess
            {
                ProcessId = result.ProcessId.Value,
                Name = Path.GetFileName(app.Exe),
                ExecutablePath = app.Exe,
                Role = "root",
                CreatedAt = rootCreatedAt
            });
        }

        lock (_sessions) _sessions.Add(session);
        SessionsChanged?.Invoke();
        _events.Add("LaunchSucceeded", $"{app.Name} started, PID={result.ProcessId}");

        if (!string.IsNullOrEmpty(app.UserDataDir))
        {
            _processMonitor.TrackBrowser(app.UserDataDir, pids =>
            {
                lock (session.Processes)
                {
                    session.Processes.RemoveAll(p => p.Role == "descendant" && !pids.Contains(p.ProcessId));
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

            if (result.ProcessId.HasValue)
            {
                _processMonitor.TrackPid(result.ProcessId.Value, () =>
                {
                    if (session.Status != "running") return;
                    _processMonitor.StopTrackingBrowser(app.UserDataDir);
                    MarkProcessExited(session, result.ProcessId.Value);
                    // Browser session: root exit = session exit. No handoff logic.
                    session.Status = "exited";
                    session.ExitedAt = DateTime.Now;
                    _events.Add("SessionExited", $"{app.Name} exited");
                    SessionsChanged?.Invoke();
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
        _events.Add("LaunchStarted", $"Starting {name} ({mode})");

        // Capture process baseline (synchronously) for correlated handoff fallback.
        // Must complete BEFORE Process.Start so launcher is not yet in baseline.
        var baseline = _processMonitor.CaptureProcessSnapshots();
        var baselineTask = Task.FromResult(baseline);

        // Ensure ProxiFyre route BEFORE launch so the new process is matched.
        // Returns the routing label that should appear on the session card.
        string routingStatus = ResolveInitialRoutingStatus(exePath, mode, proxyId);

        var result = _launcher.LaunchGeneric(exePath);

        if (!result.Success)
        {
            _events.Add("LaunchFailed", $"{name}: {result.Error}");
            return result;
        }

        var rootCreatedAt = result.ProcessId.HasValue
            ? (ProcessMonitor.GetProcessStartTime(result.ProcessId.Value) ?? DateTime.Now)
            : DateTime.Now;

        // routingStatus was decided before launch (depends on backend availability)
        var session = new LaunchSession
        {
            Name = name,
            ExePath = exePath,
            Kind = "generic",
            Mode = mode,
            ProxyId = proxyId,
            RootProcessId = result.ProcessId,
            RootCreatedAt = rootCreatedAt,
            Status = "running",
            RoutingStatus = routingStatus
        };

        if (result.ProcessId.HasValue)
        {
            session.Processes.Add(new TrackedProcess
            {
                ProcessId = result.ProcessId.Value,
                Name = Path.GetFileName(exePath),
                ExecutablePath = exePath,
                Role = "root",
                CreatedAt = rootCreatedAt
            });
        }

        lock (_sessions) _sessions.Add(session);
        lock (_baselines) _baselines[session.Id] = baselineTask;
        var cts = new CancellationTokenSource();
        lock (_sessionCts) _sessionCts[session.Id] = cts;
        SessionsChanged?.Invoke();
        _events.Add("LaunchSucceeded", $"{name} started, PID={result.ProcessId}");

        if (result.ProcessId.HasValue)
        {
            var rootPid = result.ProcessId.Value;

            _ = Task.Run(async () =>
            {
                try
                {
                    await _processMonitor.TrackProcessTreeAsync(
                        rootPid, rootCreatedAt, durationSeconds: 10,
                        onDescendant: desc =>
                        {
                            lock (session.Processes)
                            {
                                if (!session.Processes.Any(p => p.ProcessId == desc.ProcessId))
                                {
                                    desc.Role = "descendant";
                                    session.Processes.Add(desc);
                                }
                            }
                            _events.Add("ChildProcessDetected", $"{name}: child {desc.Name} (PID={desc.ProcessId})");
                            SessionsChanged?.Invoke();
                        });
                }
                catch (Exception ex)
                {
                    Logger.Error($"TrackProcessTreeAsync for {name} failed: {ex.Message}");
                }
            });

            _processMonitor.TrackPid(rootPid, () =>
            {
                MarkProcessExited(session, rootPid);
                _events.Add("RootProcessExited", $"{name} root process exited");

                if (session.HasLiveProcesses)
                {
                    session.Status = "running-via-child";
                    session.IsLauncherHandoffDetected = true;
                    _events.Add("LauncherHandoffDetected", $"{name}: launcher exited, child still running");
                    SessionsChanged?.Invoke();
                }
                else
                {
                    // No descendant alive. Try correlated handoff before marking exited.
                    session.Status = "checking-correlated";
                    _events.Add("CheckingCorrelatedHandoff", $"{name}: looking for correlated handoff process");
                    SessionsChanged?.Invoke();

                    _ = Task.Run(async () =>
                    {
                        try
                        {
                            await TryCorrelatedHandoffAsync(session, baselineTask);
                        }
                        catch (Exception ex)
                        {
                            Logger.Error($"TryCorrelatedHandoffAsync for {name} failed: {ex.Message}");
                            FinalizeAsExited(session, "correlated check failed");
                        }
                    });
                }
            });

            _ = Task.Run(async () =>
            {
                try
                {
                    await MonitorDescendantsAsync(session, cts.Token);
                }
                catch (OperationCanceledException) { }
                catch (Exception ex) { Logger.Error($"MonitorDescendants for {name} failed: {ex.Message}"); }
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

    private async Task MonitorDescendantsAsync(LaunchSession session, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested
               && session.HasLiveProcesses
               && session.Status != "exited"
               && session.Status != "failed")
        {
            try { await Task.Delay(2000, ct); }
            catch (OperationCanceledException) { break; }

            bool anyChanged = false;
            lock (session.Processes)
            {
                foreach (var proc in session.Processes
                    .Where(p => p.ExitedAt == null && (p.Role == "descendant" || p.Role == "correlated"))
                    .ToList())
                {
                    if (!ProcessMonitor.IsAlive(proc.ProcessId))
                    {
                        proc.ExitedAt = DateTime.Now;
                        anyChanged = true;
                    }
                }
            }

            if (anyChanged)
            {
                _events.Add("ProcessExited", $"{session.Name}: tracked process exited");
                if (!session.HasLiveProcesses && session.Status is "running-via-child" or "running-via-correlated")
                {
                    session.Status = "exited";
                    session.ExitedAt = DateTime.Now;
                    _events.Add("SessionExited", $"{session.Name} all processes exited");
                }
                SessionsChanged?.Invoke();
            }
        }
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

    private async Task TryCorrelatedHandoffAsync(LaunchSession session, Task<List<ProcessSnapshot>> baselineTask)
    {
        var baseline = await baselineTask;
        var baselinePids = baseline.Select(p => p.ProcessId).ToHashSet();
        var rootExitedAt = session.Processes.FirstOrDefault(p => p.Role == "root")?.ExitedAt ?? DateTime.Now;

        await Task.Delay(3000);

        var current = await _processMonitor.CaptureProcessSnapshotsAsync();
        var newProcesses = current
            .Where(p => !baselinePids.Contains(p.ProcessId))
            .Where(p => p.ProcessId != session.RootProcessId)
            .Where(p => p.CreatedAt == null || p.CreatedAt >= session.StartedAt.AddSeconds(-1))
            .ToList();

        if (newProcesses.Count == 0)
        {
            FinalizeAsExited(session, "no correlated handoff found");
            return;
        }

        var candidates = _scorer.Score(
            launcherExePath: session.ExePath,
            launcherStartedAt: session.StartedAt,
            rootExitedAt: rootExitedAt,
            newProcesses: newProcesses);

        candidates = candidates.Where(c => ProcessMonitor.IsAlive(c.Process.ProcessId)).ToList();

        var high = candidates.Where(c => c.Confidence == "high").ToList();
        var medium = candidates.Where(c => c.Confidence == "medium").ToList();

        if (high.Count == 1)
        {
            AttachCorrelated(session, high[0], ProcessTrackingConfidence.CorrelatedHigh, autoAttached: true);
            return;
        }

        if (high.Count > 1)
        {
            _events.Add("CorrelatedCandidatesFound", $"{session.Name}: {high.Count} high-confidence candidates, user confirmation needed");
            CorrelatedCandidatesFound?.Invoke(session, high);
            return;
        }

        if (medium.Count > 0)
        {
            _events.Add("CorrelatedCandidatesFound", $"{session.Name}: {medium.Count} medium-confidence candidates, user confirmation needed");
            CorrelatedCandidatesFound?.Invoke(session, medium);
            return;
        }

        FinalizeAsExited(session, $"only low-confidence candidates ({candidates.Count}), ignored");
    }

    private void FinalizeAsExited(LaunchSession session, string reason)
    {
        session.Status = "exited";
        session.ExitedAt = DateTime.Now;
        _events.Add("SessionExited", $"{session.Name} exited ({reason})");
        SessionsChanged?.Invoke();
    }

    public void AttachCorrelated(LaunchSession session, HandoffCandidate candidate, ProcessTrackingConfidence confidence, bool autoAttached)
    {
        var proc = new TrackedProcess
        {
            ProcessId = candidate.Process.ProcessId,
            ParentProcessId = candidate.Process.ParentProcessId,
            Name = candidate.Process.Name,
            ExecutablePath = candidate.Process.ExecutablePath,
            CommandLine = candidate.Process.CommandLine,
            CreatedAt = candidate.Process.CreatedAt,
            Role = "correlated",
            Confidence = confidence,
            CorrelationScore = candidate.Score
        };
        proc.CorrelationReasons.AddRange(candidate.Reasons);

        lock (session.Processes)
        {
            if (!session.Processes.Any(p => p.ProcessId == proc.ProcessId))
                session.Processes.Add(proc);
        }

        session.Status = "running-via-correlated";
        session.IsLauncherHandoffDetected = true;
        // Routing semantics: still external-routing-required — we do not claim to control routing.

        var verb = autoAttached ? "auto-attached" : "user-attached";
        _events.Add("CorrelatedHandoffAttached", $"{session.Name}: {verb} {proc.Name} (PID={proc.ProcessId}, score={candidate.Score}, confidence={candidate.Confidence})");

        _processMonitor.TrackPid(proc.ProcessId, () =>
        {
            MarkProcessExited(session, proc.ProcessId);
            _events.Add("ProcessExited", $"{session.Name}: correlated process {proc.Name} exited");
            if (!session.HasLiveProcesses)
            {
                FinalizeAsExited(session, "correlated process exited");
            }
            else
            {
                SessionsChanged?.Invoke();
            }
        });

        SessionsChanged?.Invoke();
    }

    public void IgnoreCorrelated(LaunchSession session)
    {
        _events.Add("CorrelatedHandoffIgnored", $"{session.Name}: user ignored correlated candidates");
        FinalizeAsExited(session, "user ignored correlated candidates");
    }

    /// <summary>
    /// Decide what RoutingStatus the session should start with based on mode and
    /// whether the ProxiFyre backend is configured and able to write a route.
    /// </summary>
    private string ResolveInitialRoutingStatus(string exePath, string mode, string? proxyId)
    {
        if (mode == "direct" || string.IsNullOrEmpty(proxyId))
            return "direct";

        // No backend configured → fall back to v0.5 "external router" semantics
        if (_backend == null || !_config.TransparentBackend.Enabled || _config.TransparentBackend.Type != "proxifyre")
            return "external-routing-required";

        var status = _backend.GetStatus();
        switch (status.State)
        {
            case "not-configured":
            case "exe-missing":
            case "service-not-installed":
                _events.Add("BackendNotReady", $"ProxiFyre: {status.State} — {status.Message}");
                return "external-routing-required";
            case "stopped":
                _events.Add("BackendNotReady", $"ProxiFyre service is installed but not running — start it manually");
                return "external-routing-required";
        }

        // status.State == "running"
        try
        {
            _backend.EnsureRoute(exePath, proxyId, source: "drop-zone");
            var apply = _backend.Apply(restartService: _config.TransparentBackend.AutoRestartOnConfigChange);
            if (!apply.Success)
            {
                _events.Add("BackendApplyFailed", apply.Reason ?? "unknown");
                return "proxifyre-route-failed";
            }
            if (apply.ServiceRestarted) return "proxifyre-route-active";
            if (apply.NeedsManualRestart) return "proxifyre-route-needs-restart";
            // No restart requested → config written but old rules still active in memory
            return "proxifyre-route-pending";
        }
        catch (Exception ex)
        {
            Logger.Error($"ResolveInitialRoutingStatus failed for {exePath}: {ex.Message}");
            _events.Add("BackendApplyFailed", ex.Message);
            return "proxifyre-route-failed";
        }
    }

    public string RouteDetectedChild(LaunchSession session)
    {
        if (_backend == null || !_config.TransparentBackend.Enabled || _config.TransparentBackend.Type != "proxifyre")
            return session.RoutingStatus;
        if (string.IsNullOrEmpty(session.ProxyId)) return session.RoutingStatus;

        var liveChild = session.Processes.FirstOrDefault(p =>
            p.ExitedAt == null && (p.Role == "descendant" || p.Role == "correlated"));
        if (liveChild == null || string.IsNullOrEmpty(liveChild.ExecutablePath))
            return session.RoutingStatus;

        try
        {
            _backend.EnsureRoute(liveChild.ExecutablePath, session.ProxyId, source: "child-detected");
            var apply = _backend.Apply(restartService: _config.TransparentBackend.AutoRestartOnConfigChange);
            if (!apply.Success)
                session.RoutingStatus = "proxifyre-route-failed";
            else if (apply.ServiceRestarted)
                session.RoutingStatus = "proxifyre-route-active";
            else if (apply.NeedsManualRestart)
                session.RoutingStatus = "proxifyre-route-needs-restart";
            else
                session.RoutingStatus = "proxifyre-route-pending";

            _events.Add("ChildRouteAdded", $"{session.Name}: routed {liveChild.Name} via {session.ProxyId}");
            SessionsChanged?.Invoke();
            return session.RoutingStatus;
        }
        catch (Exception ex)
        {
            Logger.Error($"RouteDetectedChild failed: {ex.Message}");
            _events.Add("BackendApplyFailed", ex.Message);
            session.RoutingStatus = "proxifyre-route-failed";
            SessionsChanged?.Invoke();
            return session.RoutingStatus;
        }
    }

    public void StopSession(LaunchSession session)
    {
        try
        {
            List<int> toKill;
            lock (session.Processes)
            {
                toKill = session.Processes.Where(p => p.ExitedAt == null).Select(p => p.ProcessId).ToList();
            }
            foreach (var pid in toKill)
            {
                try
                {
                    if (ProcessMonitor.IsAlive(pid))
                    {
                        var p = System.Diagnostics.Process.GetProcessById(pid);
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
        CancellationTokenSource? cts = null;
        lock (_sessionCts)
        {
            if (_sessionCts.TryGetValue(session.Id, out cts))
                _sessionCts.Remove(session.Id);
        }
        try { cts?.Cancel(); cts?.Dispose(); } catch { }
        lock (_baselines) _baselines.Remove(session.Id);
        SessionsChanged?.Invoke();
    }

    public void Dispose()
    {
        List<CancellationTokenSource> all;
        lock (_sessionCts)
        {
            all = _sessionCts.Values.ToList();
            _sessionCts.Clear();
        }
        foreach (var cts in all)
        {
            try { cts.Cancel(); cts.Dispose(); } catch { }
        }
    }
}
