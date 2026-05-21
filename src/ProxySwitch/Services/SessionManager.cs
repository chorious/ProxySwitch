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
    private readonly AppIdentityResolver _resolver;

    public event Action? SessionsChanged;
    public event Action<LaunchSession, List<HandoffCandidate>>? CorrelatedCandidatesFound;
    /// <summary>Fires when a session's RoutingStatus transitions to proxifyre-route-active
    /// in the async path (drop-zone launch / RouteDetectedChildren). MainForm uses this to
    /// pop a tray balloon. Not raised for external attach or browser sessions.</summary>
    public event Action<LaunchSession>? RouteActivated;

    public SessionManager(AppLauncher launcher, ProcessMonitor monitor, EventStore events, ProxyConfig config, ProxiFyreBackend? backend = null, AppIdentityResolver? resolver = null)
    {
        _launcher = launcher;
        _processMonitor = monitor;
        _events = events;
        _config = config;
        _backend = backend;
        _resolver = resolver ?? new AppIdentityResolver();
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

    public LaunchResult LaunchGeneric(string exePath, string name, string mode, string? proxyId = null, bool isPersistentRoute = true)
    {
        _events.Add("SessionCreated", $"Session for {name}");
        _events.Add("LaunchStarted", $"Starting {name} ({mode})");

        // Preflight: exe must exist, otherwise we waste a UAC prompt + write a route
        // for an exe that can't even start. Fail early.
        if (!File.Exists(exePath))
        {
            _events.Add("LaunchFailed", $"{name}: executable not found ({exePath})");
            return new LaunchResult { Success = false, Error = $"Executable not found: {exePath}" };
        }

        // Capture process baseline (synchronously) for correlated handoff fallback.
        // Must complete BEFORE Process.Start so launcher is not yet in baseline.
        var baseline = _processMonitor.CaptureProcessSnapshots();
        var baselineTask = Task.FromResult(baseline);

        // Pre-generate sessionId so tmp routes can be bound to this future session
        // BEFORE we call Apply / restart service.
        var sessionId = Guid.NewGuid().ToString("N")[..8];

        // Ensure ProxiFyre route BEFORE launch so the new process is matched.
        // Returns the routing label that should appear on the session card.
        string routingStatus = ResolveInitialRoutingStatus(exePath, mode, proxyId, isPersistentRoute, sessionId);

        var result = _launcher.LaunchGeneric(exePath);

        if (!result.Success)
        {
            // Rollback: the route we just added for an app that didn't actually launch.
            // Persistent routes are intentionally left in place (user explicitly chose
            // to save them — they'll re-try next time), tmp routes are removed.
            if (!isPersistentRoute && _backend != null && !string.IsNullOrEmpty(proxyId)
                && routingStatus.StartsWith("proxifyre-", StringComparison.Ordinal))
            {
                try
                {
                    if (_backend.RemoveRouteByExe(exePath, proxyId))
                    {
                        // The route is gone from _config.AppRoutes; rewrite app-config.json
                        // so the on-disk file matches. Without this, a failed drag-drop
                        // can leave a dead route in app-config.json (#3 in GPT review v0.7).
                        _backend.WriteConfig();
                        _events.Add("AppRouteRolledBackFlushed", $"{Path.GetFileName(exePath)}: route removed from app-config.json after failed launch");
                        // If pre-launch Apply already restarted the service, the live
                        // service has the about-to-be-rolled-back rule loaded. Mark stale
                        // so the Dashboard surfaces "restart to unload" rather than
                        // pretending the rollback is fully effective.
                        if (routingStatus == "proxifyre-route-active")
                            _backend.MarkStaleLiveRoutes($"launch of {name} failed after route was applied to live service");
                    }
                }
                catch (Exception ex) { Logger.Error($"Rollback after launch fail: {ex.Message}"); }
            }
            _events.Add("LaunchFailed", $"{name}: {result.Error}");
            return result;
        }

        var rootCreatedAt = result.ProcessId.HasValue
            ? (ProcessMonitor.GetProcessStartTime(result.ProcessId.Value) ?? DateTime.Now)
            : DateTime.Now;

        // routingStatus was decided before launch (depends on backend availability)
        var session = new LaunchSession
        {
            Id = sessionId,
            Name = name,
            ExePath = exePath,
            Kind = "generic",
            Mode = mode,
            ProxyId = proxyId,
            RootProcessId = result.ProcessId,
            RootCreatedAt = rootCreatedAt,
            Status = "running",
            RoutingStatus = routingStatus,
            IsPersistentLaunch = isPersistentRoute
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
                // User-initiated Stop or earlier failure already finalized — don't re-process.
                if (session.Status is "exited" or "failed") return;

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
                            BeginGraceWindowOrFinalize(session, "correlated check failed");
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

            // Launch-first / apply-async (plan_opus_v0.7): if we picked launching status,
            // drive the UAC + sc restart in the background so the user isn't waiting 13s
            // before the app appears. cts is shared with descendant monitors so Stop /
            // RemoveSession cancels routing as well.
            if (session.RoutingStatus == "proxifyre-route-launching")
            {
                _ = Task.Run(() => ApplyRoutingInBackgroundAsync(session, cts.Token));
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
                    BeginGraceWindowOrFinalize(session, "all tracked processes exited");
                }
                else
                {
                    SessionsChanged?.Invoke();
                }
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
        if (session.Status is "exited" or "failed") return; // stopped before we got here
        var baseline = await baselineTask;
        var baselinePids = baseline.Select(p => p.ProcessId).ToHashSet();
        var rootExitedAt = session.Processes.FirstOrDefault(p => p.Role == "root")?.ExitedAt ?? DateTime.Now;

        await Task.Delay(3000);
        if (session.Status is "exited" or "failed") return; // stopped during the 3s window

        var current = await _processMonitor.CaptureProcessSnapshotsAsync();
        var newProcesses = current
            .Where(p => !baselinePids.Contains(p.ProcessId))
            .Where(p => p.ProcessId != session.RootProcessId)
            .Where(p => p.CreatedAt == null || p.CreatedAt >= session.StartedAt.AddSeconds(-1))
            .ToList();

        if (newProcesses.Count == 0)
        {
            BeginGraceWindowOrFinalize(session, "no correlated handoff found");
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

        BeginGraceWindowOrFinalize(session, $"only low-confidence candidates ({candidates.Count}), ignored");
    }

    private void FinalizeAsExited(LaunchSession session, string reason)
    {
        session.Status = "exited";
        session.ExitedAt = DateTime.Now;
        _events.Add("SessionExited", $"{session.Name} exited ({reason})");
        CleanupSessionRoutes(session);
        SessionsChanged?.Invoke();
    }

    /// <summary>
    /// If sessionSupervisor is enabled, enter the grace-window state instead of
    /// immediately finalizing. This lets restarted processes reattach within the
    /// configured window. If disabled, finalizes immediately.
    /// </summary>
    private void BeginGraceWindowOrFinalize(LaunchSession session, string reason)
    {
        if (!_config.SessionSupervisor.Enabled)
        {
            FinalizeAsExited(session, reason);
            return;
        }

        session.Status = "waiting-for-restart";
        _events.Add("SessionWaitingForRestart", $"{session.Name}: waiting for restart ({_config.SessionSupervisor.RestartGraceSeconds}s grace window)");
        SessionsChanged?.Invoke();

        var graceSeconds = _config.SessionSupervisor.RestartGraceSeconds;
        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(TimeSpan.FromSeconds(graceSeconds));
                if (session.Status == "waiting-for-restart")
                {
                    FinalizeAsExited(session, $"grace window expired ({reason})");
                }
            }
            catch (Exception ex)
            {
                Logger.Error($"Grace window task failed: {ex.Message}");
            }
        });
    }

    /// <summary>
    /// When a session ends, remove any session-only routes bound to it and
    /// rewrite ProxiFyre's app-config.json so they don't linger across runs.
    /// We do NOT restart the ProxiFyre service here — that would mean one UAC
    /// prompt every time a tmp session exits. The on-disk file is clean, but
    /// the live service still holds the removed rule until restart, so we
    /// mark the backend "stale" — Dashboard relabels the Restart button.
    /// </summary>
    private void CleanupSessionRoutes(LaunchSession session)
    {
        if (_backend == null) return;
        if (!_backend.RemoveTmpRoutesForSession(session.Id)) return;
        try
        {
            _backend.WriteConfig();
            _events.Add("TmpRoutesCleared", $"{session.Name}: session-only routes removed from app-config.json (live service still holds them — restart ProxiFyre to fully unload)");
            _backend.MarkStaleLiveRoutes($"session {session.Name} ended");
        }
        catch (Exception ex)
        {
            Logger.Error($"CleanupSessionRoutes failed: {ex.Message}");
        }
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
            if (session.Status is "exited" or "failed") return;
            MarkProcessExited(session, proc.ProcessId);
            _events.Add("ProcessExited", $"{session.Name}: correlated process {proc.Name} exited");
            if (!session.HasLiveProcesses)
            {
                BeginGraceWindowOrFinalize(session, "correlated process exited");
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
    /// Unified entry point for ALL process-creation events (from ExternalProcessWatcher
    /// or SessionSupervisor). Prevents races by serializing the decision:
    /// 1) Skip if ProxySwitch launched it.
    /// 2) Try merge into an existing active session by stable route key.
    /// 3) Fall back to external attach (new session for persistent route).
    /// </summary>
    public void HandleProcessStarted(ExternalProcessHit hit)
    {
        var myPid = System.Diagnostics.Process.GetCurrentProcess().Id;
        if (hit.ParentProcessId == myPid) return;

        if (TryMergeActiveSession(hit)) return;
        AttachExternalLaunch(hit);
    }

    /// <summary>
    /// Try to add a newly-created process to an existing active session.
    /// Priority: parent chain > route identity > process name > directory match.
    /// Returns true if merged.
    /// </summary>
    private bool TryMergeActiveSession(ExternalProcessHit hit)
    {
        lock (_sessions)
        {
            foreach (var session in _sessions)
            {
                if (session.Status is "exited" or "failed") continue;
                if (string.IsNullOrEmpty(session.ProxyId)) continue;

                // Check if this PID is already tracked
                if (session.Processes.Any(p => p.ProcessId == hit.ProcessId)) return true;

                string? role = null;

                // 1) Parent chain match
                if (hit.ParentProcessId.HasValue && session.Processes.Any(p => p.ProcessId == hit.ParentProcessId.Value && p.ExitedAt == null))
                {
                    role = "descendant";
                }

                // 2) Route identity match (ExePath / ResolvedExePath)
                if (role == null)
                {
                    var route = _config.AppRoutes.FirstOrDefault(r =>
                        r.ProxyId == session.ProxyId && r.Enabled &&
                        (!string.IsNullOrEmpty(hit.ExecutablePath) &&
                         (string.Equals(r.ExePath, hit.ExecutablePath, StringComparison.OrdinalIgnoreCase)
                          || string.Equals(r.ResolvedExePath, hit.ExecutablePath, StringComparison.OrdinalIgnoreCase))));
                    if (route != null) role = "descendant";
                }

                // 3) Process name match
                if (role == null)
                {
                    var route = _config.AppRoutes.FirstOrDefault(r =>
                        r.ProxyId == session.ProxyId && r.Enabled &&
                        string.Equals(AppIdentityResolver.NormalizeProcessName(r.ProcessName), hit.Name, StringComparison.OrdinalIgnoreCase));
                    if (route != null)
                    {
                        var sessionRouteKey = GuessSessionRouteKey(session);
                        var routeKey = _resolver.GetStableRouteKey(route);
                        if (sessionRouteKey == routeKey) role = "descendant";
                    }
                }

                // 4) Same directory heuristic
                if (role == null && !string.IsNullOrEmpty(hit.ExecutablePath))
                {
                    var hitDir = Path.GetDirectoryName(hit.ExecutablePath);
                    var sessionDir = Path.GetDirectoryName(session.ExePath);
                    if (!string.IsNullOrEmpty(hitDir) && !string.IsNullOrEmpty(sessionDir)
                        && string.Equals(hitDir, sessionDir, StringComparison.OrdinalIgnoreCase))
                    {
                        role = "descendant";
                    }
                }

                if (role != null)
                {
                    var rootCreatedAt = ProcessMonitor.GetProcessStartTime(hit.ProcessId) ?? DateTime.Now;
                    var isRestart = session.Status == "waiting-for-restart";
                    session.Processes.Add(new TrackedProcess
                    {
                        ProcessId = hit.ProcessId,
                        ParentProcessId = hit.ParentProcessId,
                        Name = hit.Name,
                        ExecutablePath = hit.ExecutablePath,
                        Role = isRestart ? "restarted-child" : role,
                        CreatedAt = rootCreatedAt
                    });

                    if (isRestart)
                    {
                        session.Status = "running";
                        _events.Add("ProcessReattached", $"{session.Name}: {hit.Name} (PID={hit.ProcessId}) reattached during grace window");
                    }
                    else
                    {
                        _events.Add("ProcessMerged", $"{session.Name}: {hit.Name} (PID={hit.ProcessId}) merged into existing session ({role})");
                    }
                    SessionsChanged?.Invoke();
                    return true;
                }
            }
        }
        return false;
    }

    /// <summary>
    /// Guess the stable route key for an existing session by looking at its
    /// tracked processes and matching against known routes.
    /// </summary>
    private string GuessSessionRouteKey(LaunchSession session)
    {
        var liveProcess = session.Processes.FirstOrDefault(p => p.ExitedAt == null);
        var exePath = liveProcess?.ExecutablePath ?? session.ExePath;
        var name = liveProcess?.Name ?? Path.GetFileName(session.ExePath);

        var route = _config.AppRoutes.FirstOrDefault(r =>
            r.ProxyId == session.ProxyId && r.Enabled &&
            (string.Equals(r.ExePath, exePath, StringComparison.OrdinalIgnoreCase)
             || string.Equals(AppIdentityResolver.NormalizeProcessName(r.ProcessName), name, StringComparison.OrdinalIgnoreCase)));

        if (route != null) return _resolver.GetStableRouteKey(route);

        // Fallback: derive from session itself
        var identity = string.IsNullOrEmpty(session.ExePath)
            ? AppIdentityResolver.NormalizeProcessName(Path.GetFileName(session.ExePath))
            : session.ExePath;
        return $"path:{session.ProxyId}:{identity}";
    }

    /// <summary>
    /// External launch attach: an app matching a persistent AppRoute started
    /// outside of ProxySwitch (Start Menu / shortcut / another tool). We
    /// surface it on the Dashboard so the user can see ProxyFyre is routing it.
    /// We do NOT call EnsureRoute or Apply — the persistent route is already
    /// in app-config.json, and ProxiFyre is matching it (assuming the service
    /// is running). Returns true if a new session was attached, false if the
    /// PID is already tracked or no matching route was found.
    /// </summary>
    public bool AttachExternalLaunch(ExternalProcessHit hit)
    {
        // Skip apps ProxySwitch itself just launched. Two checks: PPID against
        // our own PID (covers Process.Start), and PID against tracked sessions.
        var myPid = System.Diagnostics.Process.GetCurrentProcess().Id;
        if (hit.ParentProcessId == myPid) return false;

        // Find which persistent AppRoute caused the match. We can't trust
        // ExePath alone (rare apps don't expose it via WMI); fall back to Name.
        // Normalize process names to include .exe so bare names like "Claude"
        // match WMI events "Claude.exe".
        var route = _config.AppRoutes.FirstOrDefault(r =>
            r.IsPersistent && r.Enabled &&
            ((!string.IsNullOrEmpty(hit.ExecutablePath) && string.Equals(r.ExePath, hit.ExecutablePath, StringComparison.OrdinalIgnoreCase))
             || (!string.IsNullOrEmpty(hit.Name) && string.Equals(AppIdentityResolver.NormalizeProcessName(r.ProcessName), hit.Name, StringComparison.OrdinalIgnoreCase))));
        if (route == null) return false;

        var rootCreatedAt = ProcessMonitor.GetProcessStartTime(hit.ProcessId) ?? DateTime.Now;

        // Build the session OUTSIDE the lock — no shared state — so we hold
        // the lock for the shortest possible critical section.
        var session = new LaunchSession
        {
            Name = $"{hit.Name} (external)",
            ExePath = string.IsNullOrEmpty(hit.ExecutablePath) ? hit.Name : hit.ExecutablePath,
            Kind = "external",
            Mode = "proxy",
            ProxyId = route.ProxyId,
            RootProcessId = hit.ProcessId,
            RootCreatedAt = rootCreatedAt,
            Status = "running",
            // External launches always match a persistent route (that's how we noticed them).
            IsPersistentLaunch = true,
            // Persistent route exists in app-config.json + service is running → presumed active.
            // If ProxiFyre is stopped, the route is just not actually intercepting; we don't
            // claim active in that case.
            RoutingStatus = ResolveExternalRoutingStatus()
        };
        session.Processes.Add(new TrackedProcess
        {
            ProcessId = hit.ProcessId,
            ParentProcessId = hit.ParentProcessId,
            Name = hit.Name,
            ExecutablePath = hit.ExecutablePath,
            Role = "root",
            CreatedAt = rootCreatedAt
        });

        // Single lock spans dedup-check + add. Two WMI firings for the same PID
        // can race the previous two-block design (#3 in opus_review_v0.7.1).
        lock (_sessions)
        {
            if (_sessions.Any(s => s.RootProcessId == hit.ProcessId
                                || s.Processes.Any(p => p.ProcessId == hit.ProcessId)))
                return false;
            _sessions.Add(session);
        }

        var cts = new CancellationTokenSource();
        lock (_sessionCts) _sessionCts[session.Id] = cts;
        _events.Add("ExternalProcessAttached", $"{hit.Name} (PID={hit.ProcessId}) matched persistent route '{route.Name}'");
        SessionsChanged?.Invoke();

        var rootPid = hit.ProcessId;
        var sessionName = session.Name;

        // Discover descendants within a 10s window (catches launcher → child handoff
        // for external apps too). Same pattern as LaunchGeneric. #8 in opus_review_v0.7.1.
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
                        _events.Add("ChildProcessDetected", $"{sessionName}: child {desc.Name} (PID={desc.ProcessId})");
                        SessionsChanged?.Invoke();
                    });
            }
            catch (Exception ex)
            {
                Logger.Error($"TrackProcessTreeAsync for {sessionName} failed: {ex.Message}");
            }
        });

        // Long-running descendant exit watcher. Without this, the session card
        // never auto-transitions to exited when a launcher's children die.
        _ = Task.Run(async () =>
        {
            try { await MonitorDescendantsAsync(session, cts.Token); }
            catch (OperationCanceledException) { }
            catch (Exception ex) { Logger.Error($"MonitorDescendants for {sessionName} failed: {ex.Message}"); }
        });

        // Root exit: if descendants are alive, transition to running-via-child;
        // otherwise finalize. External sessions skip correlated-handoff detection
        // (no baseline — we didn't trigger the launch).
        _processMonitor.TrackPid(rootPid, () =>
        {
            if (session.Status is "exited" or "failed") return;
            MarkProcessExited(session, rootPid);
            _events.Add("RootProcessExited", $"{sessionName}: external root process exited");

            if (session.HasLiveProcesses)
            {
                session.Status = "running-via-child";
                session.IsLauncherHandoffDetected = true;
                _events.Add("LauncherHandoffDetected", $"{sessionName}: launcher exited, child still running");
                SessionsChanged?.Invoke();
            }
            else
            {
                BeginGraceWindowOrFinalize(session, "external process exited");
            }
        });

        return true;
    }

    private string ResolveExternalRoutingStatus()
    {
        if (_backend == null) return "external-routing-required";
        var st = _backend.GetStatus();
        // Mirror ResolveInitialRoutingStatus: states where restart can't help
        // collapse to external-routing-required so the UI doesn't tell the user
        // "restart to fix" when restart is useless (backend off / exe missing / etc.).
        switch (st.State)
        {
            case "disabled":
            case "not-configured":
            case "exe-missing":
            case "service-not-installed":
                return "external-routing-required";
            case "running":
                return "proxifyre-route-active";
            case "stopped":
            default:
                return "proxifyre-route-needs-restart";
        }
    }

    /// <summary>
    /// Decide what RoutingStatus the session should start with based on mode and
    /// whether the ProxiFyre backend is configured and able to write a route.
    /// </summary>
    private string ResolveInitialRoutingStatus(string exePath, string mode, string? proxyId, bool isPersistentRoute, string sessionId)
    {
        if (mode == "direct" || string.IsNullOrEmpty(proxyId))
            return "direct";

        // No backend configured → fall back to v0.5 "external router" semantics
        if (_backend == null || !_config.TransparentBackend.Enabled || _config.TransparentBackend.Type != "proxifyre")
            return "external-routing-required";

        var status = _backend.GetStatus();
        // Genuine "can't proceed" cases — exe / service / driver missing.
        switch (status.State)
        {
            case "not-configured":
            case "exe-missing":
            case "service-not-installed":
            case "disabled":
                _events.Add("BackendNotReady", $"ProxiFyre: {status.State} — {status.Message}");
                return "external-routing-required";
        }

        // Launch-first / apply-async (plan_opus_v0.7): write config NOW, but defer the
        // service restart (UAC + sc + 5-15s wait) to ApplyRoutingInBackgroundAsync after
        // the app has already started. App's first connections may race the route — that
        // is a ProxiFyre design limit; for tmp/persistent "try it" semantics it's fine.
        try
        {
            var source = isPersistentRoute ? "drop-zone-set" : "drop-zone-tmp";
            _backend.EnsureRoute(exePath, proxyId, source: source, isPersistent: isPersistentRoute, sessionId: sessionId);
            _backend.WriteConfig();
            return "proxifyre-route-launching";
        }
        catch (Exception ex)
        {
            Logger.Error($"ResolveInitialRoutingStatus failed for {exePath}: {ex.Message}");
            _events.Add("BackendApplyFailed", ex.Message);
            return "proxifyre-route-failed";
        }
    }

    /// <summary>
    /// Background routing task fired after a generic app is already launched (drop-zone)
    /// or a child route was added (Route Child). Drives session.RoutingStatus through
    /// launching → restarting → active/failed. Honors AutoRestart=off (stays at pending).
    /// Exits silently if the session was stopped mid-flight — see plan_opus_v0.7.
    /// </summary>
    private async Task ApplyRoutingInBackgroundAsync(LaunchSession session, CancellationToken ct)
    {
        try
        {
            if (_backend == null) return;
            var be = _config.TransparentBackend;

            // AutoRestart off → stay at pending; user manually restarts to apply.
            if (!be.AutoRestartOnConfigChange)
            {
                if (ct.IsCancellationRequested || session.Status is "exited" or "failed") return;
                session.RoutingStatus = "proxifyre-route-pending";
                _events.Add("RouteWritten", $"{session.Name}: config written; restart ProxiFyre manually to apply");
                SessionsChanged?.Invoke();
                return;
            }

            if (ct.IsCancellationRequested || session.Status is "exited" or "failed") return;

            // Phase 1: signal UAC about to pop / sc starting
            session.RoutingStatus = "proxifyre-route-restarting";
            _events.Add("RouteApplying", $"{session.Name}: restarting ProxiFyre to activate route (UAC)");
            SessionsChanged?.Invoke();

            // Phase 2: actual restart on Task.Run threadpool (cmd.exe + sc.exe + WaitForExit
            // + WaitForStatus block this thread for up to ~30s).
            bool success;
            if (be.ManageService)
            {
                success = await Task.Run(() => _backend.RestartService(), ct);
                if (!success && !ct.IsCancellationRequested)
                    success = await Task.Run(() => _backend.RestartServiceElevated(), ct);
            }
            else
            {
                success = await Task.Run(() => _backend.RestartServiceElevated(), ct);
            }

            if (ct.IsCancellationRequested || session.Status is "exited" or "failed") return;

            // Phase 3: terminal status
            if (success)
            {
                session.RoutingStatus = "proxifyre-route-active";
                _events.Add("RouteActivated", $"{session.Name}: ProxiFyre route active");
                SessionsChanged?.Invoke();
                try { RouteActivated?.Invoke(session); }
                catch (Exception ex) { Logger.Error($"RouteActivated handler: {ex.Message}"); }
            }
            else
            {
                session.RoutingStatus = "proxifyre-route-failed";
                _events.Add("RouteFailed", $"{session.Name}: ProxiFyre restart failed — click Retry");
                SessionsChanged?.Invoke();
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            Logger.Error($"ApplyRoutingInBackgroundAsync for {session.Name}: {ex.Message}");
            if (session.Status is not "exited" and not "failed")
            {
                session.RoutingStatus = "proxifyre-route-failed";
                SessionsChanged?.Invoke();
            }
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

        return RouteDetectedChildren(session, new[] { liveChild.ExecutablePath });
    }

    /// <summary>
    /// Route N distinct child executables in one Apply (one UAC prompt instead of N).
    /// Used when a launcher session has multiple non-root live processes and the
    /// user picked which to route via <see cref="RouteChildDialog"/>. See #4 in
    /// opus_review_v0.7.1.
    /// </summary>
    public string RouteDetectedChildren(LaunchSession session, IReadOnlyList<string> exePaths)
    {
        if (_backend == null || !_config.TransparentBackend.Enabled || _config.TransparentBackend.Type != "proxifyre")
            return session.RoutingStatus;
        if (string.IsNullOrEmpty(session.ProxyId) || exePaths == null || exePaths.Count == 0)
            return session.RoutingStatus;

        try
        {
            // Inherit the parent launch's persistence so a child of a tmp parent
            // doesn't get silently saved (#4 in GPT review v0.7).
            var childPersistent = session.IsPersistentLaunch;
            int routed = 0;
            foreach (var path in exePaths.Distinct(StringComparer.OrdinalIgnoreCase))
            {
                if (string.IsNullOrEmpty(path)) continue;
                _backend.EnsureRoute(
                    path,
                    session.ProxyId,
                    source: "child-detected",
                    isPersistent: childPersistent,
                    sessionId: childPersistent ? null : session.Id);
                routed++;
            }

            // Write config now, but defer service restart to the background task
            // so the click handler returns immediately (plan_opus_v0.7).
            _backend.WriteConfig();

            var scope = childPersistent ? "persistent" : "session-only";
            _events.Add("ChildRouteAdded", $"{session.Name}: routed {routed} child exe(s) via {session.ProxyId} ({scope})");

            session.RoutingStatus = "proxifyre-route-launching";
            SessionsChanged?.Invoke();

            // Reuse the session's existing CTS (created in LaunchGeneric) so Stop /
            // RemoveSession cancels this routing pass too.
            CancellationTokenSource? cts;
            lock (_sessionCts) _sessionCts.TryGetValue(session.Id, out cts);
            var token = cts?.Token ?? CancellationToken.None;
            _ = Task.Run(() => ApplyRoutingInBackgroundAsync(session, token));

            return session.RoutingStatus;
        }
        catch (Exception ex)
        {
            Logger.Error($"RouteDetectedChildren failed: {ex.Message}");
            _events.Add("BackendApplyFailed", ex.Message);
            session.RoutingStatus = "proxifyre-route-failed";
            SessionsChanged?.Invoke();
            return session.RoutingStatus;
        }
    }

    public void StopSession(LaunchSession session)
    {
        // Idempotent: don't re-process an already-finalized session.
        if (session.Status is "exited" or "failed") return;

        // Cancel the descendant-monitor loop so it doesn't race with us.
        CancellationTokenSource? cts;
        lock (_sessionCts) _sessionCts.TryGetValue(session.Id, out cts);
        try { cts?.Cancel(); } catch { }

        // Mark exited up front. Concurrent TrackPid callbacks check Status and no-op.
        // This is what makes Stop feel responsive — without it we'd wait for the
        // 2s ProcessMonitor poll plus 3s correlated-detection delay before the
        // session card showed "exited".
        session.Status = "exited";
        session.ExitedAt = DateTime.Now;
        _events.Add("SessionStopping", $"{session.Name}: user requested stop");
        SessionsChanged?.Invoke();

        List<TrackedProcess> toKill;
        lock (session.Processes)
        {
            toKill = session.Processes.Where(p => p.ExitedAt == null).ToList();
        }
        int killed = 0, failed = 0;
        foreach (var tp in toKill)
        {
            try
            {
                if (!ProcessMonitor.IsAlive(tp.ProcessId))
                {
                    tp.ExitedAt = DateTime.Now;
                    continue;
                }
                using var p = System.Diagnostics.Process.GetProcessById(tp.ProcessId);
                // entireProcessTree=true catches untracked grandchildren that ProxySwitch
                // didn't manage to enumerate. Falls back to single-process kill if the OS
                // doesn't support tree-kill in this scenario.
                try { p.Kill(entireProcessTree: true); }
                catch (InvalidOperationException) { try { p.Kill(); } catch { } }
                tp.ExitedAt = DateTime.Now;
                killed++;
            }
            catch (Exception ex)
            {
                failed++;
                Logger.Error($"StopSession: kill {tp.Name} (PID={tp.ProcessId}) failed: {ex.Message}");
                _events.Add("StopProcessFailed", $"{session.Name}: failed to kill {tp.Name} (PID {tp.ProcessId}) — {ex.Message}");
            }
        }

        if (!string.IsNullOrEmpty(session.UserDataDir))
        {
            try { _processMonitor.StopTrackingBrowser(session.UserDataDir); } catch { }
        }

        var summary = failed > 0
            ? $"{session.Name}: stopped (killed {killed}, {failed} failed)"
            : $"{session.Name}: stopped (killed {killed})";
        _events.Add("SessionExited", summary);

        // Same cleanup natural-exit would do: drop tmp routes for this session and
        // rewrite app-config.json. Will MarkStaleLiveRoutes if anything was removed.
        CleanupSessionRoutes(session);
        SessionsChanged?.Invoke();
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
