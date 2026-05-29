using System.Management;
using ProxySwitch.Models;

namespace ProxySwitch.Services;

public sealed class SessionManager : IDisposable
{
    private readonly List<LaunchSession> _sessions = new();
    private readonly Dictionary<string, Task<List<ProcessSnapshot>>> _baselines = new();
    private readonly Dictionary<string, CancellationTokenSource> _sessionCts = new();
    private readonly HashSet<string> _activeDescendantMonitors = new();
    private readonly Dictionary<string, DateTime> _lastChildRouteTime = new();
    private readonly Dictionary<string, SemaphoreSlim> _ipcSessionRecoveryLocks = new();
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
        var (routingStatus, routeKey) = ResolveInitialRoutingStatus(exePath, mode, proxyId, isPersistentRoute, sessionId);

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

        var isIpcManaged = routingStatus == "proxifyre-route-session-created" && _backend?.IsIpcAvailable == true;

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
            IsPersistentLaunch = isPersistentRoute,
            RouteKey = routeKey,
            IpcManaged = isIpcManaged
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
                            if (session.IpcManaged && _backend?.IpcClient != null)
                            {
                                _ = Task.Run(async () =>
                                {
                                    var createdAt = desc.CreatedAt ?? ProcessMonitor.GetProcessStartTime(desc.ProcessId) ?? DateTime.Now;
                                    var addResult = await AddPidToIpcSessionAsync(session, desc.ProcessId, createdAt);
                                    if (addResult.Success)
                                        _events.Add("IpcAddPidOk", $"{name}: descendant {desc.Name} (PID={desc.ProcessId}) added via IPC");
                                    else
                                        _events.Add("IpcAddPidFailed", $"{name}: descendant {desc.Name} IPC failed: {addResult.Error}");
                                });
                            }
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

            StartDescendantMonitor(session, cts);

            // Launch-first / apply-async (plan_opus_v0.7): if we picked launching status,
            // drive the UAC + sc restart in the background so the user isn't waiting 13s
            // before the app appears. cts is shared with descendant monitors so Stop /
            // RemoveSession cancels routing as well.
            if (session.RoutingStatus == "proxifyre-route-launching")
            {
                _ = Task.Run(() => ApplyRoutingInBackgroundAsync(session, cts.Token));
            }
            else if (session.IpcManaged && _backend?.IpcClient != null)
            {
                StartIpcRoutingForSession(session, result.ProcessId, rootCreatedAt);
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

    public LaunchResult LaunchTarget(LaunchTarget target, string name, string? proxyId = null, bool isPersistentRoute = true)
    {
        _events.Add("SessionCreated", $"Session for {name}");
        _events.Add("LaunchStarted", $"Starting {name} ({target.LaunchKind})");

        var sessionId = Guid.NewGuid().ToString("N")[..8];

        var (routingStatus, routeKey) = ResolveInitialRoutingStatusForTarget(target, proxyId, isPersistentRoute, sessionId);

        var result = _launcher.LaunchTarget(target);

        if (!result.Success)
        {
            if (!isPersistentRoute && _backend != null && !string.IsNullOrEmpty(proxyId)
                && routingStatus.StartsWith("proxifyre-", StringComparison.Ordinal))
            {
                try
                {
                    if (target.LaunchKind == "exe" && !string.IsNullOrEmpty(target.ExePath))
                    {
                        if (_backend.RemoveRouteByExe(target.ExePath, proxyId))
                        {
                            _backend.WriteConfig();
                            _events.Add("AppRouteRolledBackFlushed", $"{name}: route removed from app-config.json after failed launch");
                        }
                    }
                    else if (target.LaunchKind == "app-user-model-id")
                    {
                        var route = _config.AppRoutes.FirstOrDefault(r =>
                            r.ProxyId == proxyId && r.LaunchKind == "app-user-model-id" && r.AppUserModelId == target.AppUserModelId);
                        if (route != null)
                        {
                            _backend.RemoveRoute(route.Id);
                            _backend.WriteConfig();
                            _events.Add("AppRouteRolledBackFlushed", $"{name}: AUMID route removed from app-config.json after failed launch");
                        }
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

        var isIpcManaged = routingStatus == "proxifyre-route-session-created" && _backend?.IsIpcAvailable == true;

        var session = new LaunchSession
        {
            Id = sessionId,
            Name = name,
            ExePath = target.ExePath,
            Kind = "generic",
            Mode = proxyId == null ? "direct" : "proxy",
            ProxyId = proxyId,
            RootProcessId = result.ProcessId,
            RootCreatedAt = rootCreatedAt,
            Status = "running",
            RoutingStatus = routingStatus,
            IsPersistentLaunch = isPersistentRoute,
            RouteKey = routeKey,
            LaunchKind = target.LaunchKind,
            AppUserModelId = target.AppUserModelId,
            NeedsProcessConfirmation = routingStatus == "proxifyre-route-needs-process-confirmation",
            IpcManaged = isIpcManaged
        };

        if (result.ProcessId.HasValue)
        {
            session.Processes.Add(new TrackedProcess
            {
                ProcessId = result.ProcessId.Value,
                Name = !string.IsNullOrEmpty(target.ProcessName) ? target.ProcessName : Path.GetFileName(target.ExePath),
                ExecutablePath = target.ExePath,
                Role = "root",
                CreatedAt = rootCreatedAt
            });
        }

        AddSession(session);

        // IPC path: create session unconditionally; add PID only if available.
        if (session.IpcManaged && _backend?.IpcClient != null)
        {
            var proxy = _config.Proxies.FirstOrDefault(p => p.Id == proxyId);
            var endpoint = proxy != null ? $"{proxy.Host}:{proxy.Port}" : "";
            var ipcRootPid = result.ProcessId;
            var ipcRootCreated = rootCreatedAt;

            _events.Add("IpcRoutePlan",
                $"{name}: session={session.Id} endpoint={endpoint} rootPid={ipcRootPid?.ToString() ?? "none"} {WmiTime.Describe(ipcRootCreated)} routeKey={routeKey}");

            _ = Task.Run(async () =>
            {
                try
                {
                    var createResult = await _backend.IpcClient.CreateSessionAsync(session.Id, endpoint);
                    if (!createResult.Success)
                    {
                        Logger.Error($"IPC CreateSession failed: {createResult.Error}");
                        _events.Add("IpcCreateSessionFailed", $"{name}: {createResult.Error}");
                        session.RoutingStatus = "proxifyre-route-failed";
                        SessionsChanged?.Invoke();
                        return;
                    }

                    _events.Add("IpcCreateSessionOk", $"{name}: IPC session created for {endpoint}");

                    // --- Store App: discover real processes beyond the AUMID activation PID ---
                    if (target.LaunchKind == "app-user-model-id")
                    {
                        _ = Task.Run(async () =>
                        {
                            try
                            {
                                await DiscoverStoreAppProcessesAsync(session, target, endpoint);
                            }
                            catch (Exception ex)
                            {
                                Logger.Error($"Store app discovery for {name} failed: {ex.Message}");
                            }
                        });
                    }
                    // -------------------------------------------------------------------------

                    if (ipcRootPid.HasValue)
                    {
                        _events.Add("IpcAddPidRequest",
                            $"{name}: root PID={ipcRootPid.Value} {WmiTime.Describe(ipcRootCreated)} endpoint={endpoint}");
                        var addResult = await AddPidToIpcSessionAsync(session, ipcRootPid.Value, ipcRootCreated);
                        if (!addResult.Success)
                        {
                            Logger.Error($"IPC AddPid failed: {addResult.Error}");
                            _events.Add("IpcAddPidFailed", $"{name}: {addResult.Error}");
                            session.RoutingStatus = "proxifyre-route-failed";
                            SessionsChanged?.Invoke();
                            return;
                        }

                        session.RoutingStatus = "proxifyre-route-active";
                        _events.Add("IpcAddPidOk", $"{name}: PID {ipcRootPid.Value} routed via IPC to {endpoint}");
                        SessionsChanged?.Invoke();
                        try { RouteActivated?.Invoke(session); }
                        catch (Exception ex) { Logger.Error($"RouteActivated handler: {ex.Message}"); }
                    }
                }
                catch (Exception ex)
                {
                    Logger.Error($"IPC routing for {name} failed: {ex.Message}");
                    _events.Add("IpcRoutingFailed", $"{name}: {ex.Message}");
                    session.RoutingStatus = "proxifyre-route-failed";
                    SessionsChanged?.Invoke();
                }
            });
        }

        // If the AUMID activation returned a PID and we need confirmation,
        // query it immediately. Some Store Apps return the real app PID directly.
        bool confirmedFromRootPid = false;
        if (session.NeedsProcessConfirmation && result.ProcessId.HasValue)
        {
            var hit = QueryProcessHit(result.ProcessId.Value);
            if (hit != null && IsSafeToConfirm(hit, session))
                confirmedFromRootPid = TryConfirmRouteFromProcess(session, hit);
        }

        if (!confirmedFromRootPid && session.RoutingStatus == "proxifyre-route-launching")
        {
            CancellationTokenSource? cts;
            lock (_sessionCts) _sessionCts.TryGetValue(session.Id, out cts);
            var token = cts?.Token ?? CancellationToken.None;
            _ = Task.Run(() => ApplyRoutingInBackgroundAsync(session, token));
        }

        return new LaunchResult
        {
            Success = true,
            ProcessId = result.ProcessId,
            Arguments = result.Arguments,
            Session = session
        };
    }

    private void StartIpcRoutingForSession(LaunchSession session, int? rootPid, DateTime rootCreatedAt)
    {
        if (_backend?.IpcClient == null)
            return;

        var endpoint = GetIpcEndpoint(session);
        if (string.IsNullOrEmpty(endpoint))
        {
            _events.Add("IpcCreateSessionFailed", $"{session.Name}: no proxy endpoint for {session.ProxyId}");
            session.RoutingStatus = "proxifyre-route-failed";
            SessionsChanged?.Invoke();
            return;
        }

        _events.Add("IpcRoutePlan",
            $"{session.Name}: session={session.Id} endpoint={endpoint} rootPid={rootPid?.ToString() ?? "none"} {WmiTime.Describe(rootCreatedAt)} routeKey={session.RouteKey}");

        _ = Task.Run(async () =>
        {
            try
            {
                var createResult = await _backend.IpcClient.CreateSessionAsync(session.Id, endpoint);
                if (!createResult.Success)
                {
                    Logger.Error($"IPC CreateSession failed: {createResult.Error}");
                    _events.Add("IpcCreateSessionFailed", $"{session.Name}: {createResult.Error}");
                    session.RoutingStatus = "proxifyre-route-failed";
                    SessionsChanged?.Invoke();
                    return;
                }

                _events.Add("IpcCreateSessionOk", $"{session.Name}: IPC session created for {endpoint}");

                if (rootPid.HasValue)
                {
                    var addResult = await AddPidToIpcSessionAsync(session, rootPid.Value, rootCreatedAt);
                    if (addResult.Success)
                    {
                        _events.Add("IpcAddPidOk", $"{session.Name}: root PID {rootPid.Value} added via IPC");
                        if (session.Status is not "exited" and not "failed")
                        {
                            session.RoutingStatus = "proxifyre-route-active";
                            SessionsChanged?.Invoke();
                            try { RouteActivated?.Invoke(session); }
                            catch (Exception ex) { Logger.Error($"RouteActivated handler: {ex.Message}"); }
                        }
                    }
                    else
                    {
                        _events.Add("IpcAddPidFailed", $"{session.Name}: root PID {rootPid.Value} IPC failed: {addResult.Error}");
                    }
                }
            }
            catch (Exception ex)
            {
                Logger.Error($"IPC routing for {session.Name} failed: {ex.Message}");
                _events.Add("IpcRoutingFailed", $"{session.Name}: {ex.Message}");
                session.RoutingStatus = "proxifyre-route-failed";
                SessionsChanged?.Invoke();
            }
        });
    }

    #region Store App Process Discovery

    private record StoreAppCandidate(int ProcessId, string Name, string ExecutablePath, DateTime CreatedAt, int Score);

    /// <summary>
    /// After AUMID activation, discover the real Store App process(es) by name + path
    /// and attach them to the IPC session. Derives StoreAppRuntimeRoot from the
    /// best-matching candidate so late/restarted processes can be merged later.
    /// </summary>
    private async Task DiscoverStoreAppProcessesAsync(LaunchSession session, LaunchTarget target, string endpoint)
    {
        // 1) Derive the exe name to search for
        var exeName = !string.IsNullOrEmpty(target.PackageRelativeExePath)
            ? Path.GetFileName(target.PackageRelativeExePath)
            : AppIdentityResolver.NormalizeProcessName(target.DisplayName);

        if (string.IsNullOrEmpty(exeName)) return;

        _events.Add("StoreAppDiscoveryStart",
            $"{session.Name}: exeName={exeName} endpoint={endpoint} aumid={target.AppUserModelId} pfn={target.PackageFamilyName}");

        // 2) Wait for the real process to spin up (AUMID activation is async)
        await Task.Delay(2500);
        if (session.Status is "exited" or "failed") return;

        // 3) WMI query all processes matching the exe name
        var candidates = QueryStoreAppCandidates(exeName, target, session.StartedAt);
        if (candidates.Count == 0)
        {
            _events.Add("StoreAppDiscoveryEmpty", $"{session.Name}: no {exeName} processes found after AUMID launch");
            return;
        }

        foreach (var cand in candidates.OrderByDescending(c => c.Score))
        {
            _events.Add("StoreAppCandidate",
                $"{session.Name}: PID={cand.ProcessId} name={cand.Name} score={cand.Score} {WmiTime.Describe(cand.CreatedAt)} path={cand.ExecutablePath}");
        }

        // 4) Score candidates and derive runtime root from the best match
        var best = candidates.OrderByDescending(c => c.Score).First();
        var runtimeRoot = DeriveStoreAppRuntimeRoot(best.ExecutablePath);
        if (!string.IsNullOrEmpty(runtimeRoot))
        {
            session.StoreAppRuntimeRoot = runtimeRoot;
            _events.Add("StoreAppRuntimeRoot", $"{session.Name}: runtime root = {runtimeRoot}");
        }

        UpdateStoreAppRouteFromCandidate(session, best);

        // 5) Attach all viable candidates to the IPC session
        int attached = 0;
        var viableCandidates = candidates.Where(c => c.Score >= 0).ToList();
        if (viableCandidates.Count == 0)
        {
            _events.Add("StoreAppDiscoveryNoViable",
                $"{session.Name}: {candidates.Count} candidate(s), all had negative score");
        }

        foreach (var cand in viableCandidates)
        {
            if (cand.ProcessId == session.RootProcessId && session.Processes.Any(p => p.ProcessId == cand.ProcessId))
                continue; // already attached

            _events.Add("IpcAddPidRequest",
                $"{session.Name}: discovered PID={cand.ProcessId} score={cand.Score} {WmiTime.Describe(cand.CreatedAt)} endpoint={endpoint}");
            var addResult = await AddPidToIpcSessionAsync(session, cand.ProcessId, cand.CreatedAt);
            if (addResult.Success)
            {
                attached++;
                lock (session.Processes)
                {
                    if (!session.Processes.Any(p => p.ProcessId == cand.ProcessId))
                    {
                        session.Processes.Add(new TrackedProcess
                        {
                            ProcessId = cand.ProcessId,
                            Name = cand.Name,
                            ExecutablePath = cand.ExecutablePath,
                            Role = "descendant",
                            CreatedAt = cand.CreatedAt
                        });
                    }
                }
                _events.Add("IpcAddPidOk", $"{session.Name}: discovered {cand.Name} (PID={cand.ProcessId}) at {cand.ExecutablePath}");
            }
            else
            {
                _events.Add("IpcAddPidFailed", $"{session.Name}: discovered PID {cand.ProcessId} IPC add failed: {addResult.Error}");
            }
        }

        if (attached > 0 && session.RoutingStatus == "proxifyre-route-session-created")
        {
            session.RoutingStatus = "proxifyre-route-active";
            SessionsChanged?.Invoke();
            try { RouteActivated?.Invoke(session); }
            catch (Exception ex) { Logger.Error($"RouteActivated handler: {ex.Message}"); }
        }

        _events.Add("StoreAppDiscoveryComplete", $"{session.Name}: attached {attached}/{candidates.Count} discovered processes");
    }

    private void UpdateStoreAppRouteFromCandidate(LaunchSession session, StoreAppCandidate candidate)
    {
        if (string.IsNullOrEmpty(session.ProxyId))
            return;

        var route = _config.AppRoutes.FirstOrDefault(r =>
            r.ProxyId == session.ProxyId &&
            r.LaunchKind == "app-user-model-id" &&
            (!string.IsNullOrEmpty(session.RouteKey)
                ? _resolver.GetStableRouteKey(r) == session.RouteKey
                : r.AppUserModelId == session.AppUserModelId));

        if (route == null)
            return;

        var changed = false;
        if (!string.IsNullOrEmpty(candidate.Name) &&
            !string.Equals(route.ProcessName, candidate.Name, StringComparison.OrdinalIgnoreCase))
        {
            route.ProcessName = candidate.Name;
            changed = true;
        }

        if (!string.IsNullOrEmpty(candidate.ExecutablePath) &&
            !string.Equals(route.ResolvedExePath, candidate.ExecutablePath, StringComparison.OrdinalIgnoreCase))
        {
            route.ResolvedExePath = candidate.ExecutablePath;
            route.ResolvedAt = DateTime.Now;
            changed = true;
        }

        if (!changed)
            return;

        try
        {
            _backend?.SaveSwitchConfig();
            _backend?.WriteConfig();
            _events.Add("StoreAppRouteUpdated",
                $"{session.Name}: saved process={candidate.Name} exe={candidate.ExecutablePath}");
        }
        catch (Exception ex)
        {
            Logger.Error($"UpdateStoreAppRouteFromCandidate failed: {ex.Message}");
        }
    }

    private List<StoreAppCandidate> QueryStoreAppCandidates(string exeName, LaunchTarget target, DateTime sessionStartedAt)
    {
        var result = new List<StoreAppCandidate>();
        try
        {
            using var searcher = new ManagementObjectSearcher(
                $"SELECT ProcessId, Name, ExecutablePath, CreationDate FROM Win32_Process WHERE Name = '{exeName.Replace("'", "''")}'");
            foreach (ManagementObject obj in searcher.Get())
            {
                var pid = Convert.ToInt32(obj["ProcessId"]);
                var name = obj["Name"]?.ToString() ?? "";
                var path = obj["ExecutablePath"]?.ToString() ?? "";
                var createdAt = WmiTime.ParseDmtfDateTime(obj["CreationDate"]?.ToString()) ?? DateTime.Now;

                int score = 0;

                // --- Negative signals ---
                if (IsLikelyBrokerProcess(new ExternalProcessHit { Name = name, ExecutablePath = path }))
                {
                    score -= 50;
                }
                // Exclude CLI Codex (npm global install)
                if (path.Contains(@"\AppData\Roaming\npm\", StringComparison.OrdinalIgnoreCase))
                {
                    score -= 100;
                }

                // --- Positive signals ---
                // Under WindowsApps (UWP/MSIX install location)
                if (path.Contains(@"\WindowsApps\", StringComparison.OrdinalIgnoreCase))
                {
                    score += 40;
                }
                // Under AppData\Local\Packages (MSIX runtime)
                if (path.Contains(@"\AppData\Local\Packages\", StringComparison.OrdinalIgnoreCase))
                {
                    score += 35;
                }
                // Known Codex runtime path (heuristic fallback)
                if (path.Contains(@"\AppData\Local\OpenAI\Codex\", StringComparison.OrdinalIgnoreCase))
                {
                    score += 35;
                }
                // Created near session start time
                var dt = (createdAt - sessionStartedAt).TotalSeconds;
                if (dt >= -5 && dt <= 10)
                {
                    score += 25;
                }
                else if (dt < -300)
                {
                    score -= 10; // slightly penalize very old processes unless strongly matched by path
                }

                result.Add(new StoreAppCandidate(pid, name, path, createdAt, score));
            }
        }
        catch (Exception ex)
        {
            Logger.Error($"QueryStoreAppCandidates failed: {ex.Message}");
        }
        return result;
    }

    private async Task<IpcResult> AddPidToIpcSessionAsync(LaunchSession session, int pid, DateTime createdAt, CancellationToken ct = default)
    {
        if (_backend?.IpcClient == null)
            return new IpcResult { Success = false, Error = "IPC client unavailable" };

        var result = await _backend.IpcClient.AddPidAsync(session.Id, pid, createdAt, ct);
        if (result.Success || !IsIpcSessionNotFound(result))
            return result;

        _events.Add("IpcSessionMissing",
            $"{session.Name}: session {session.Id} missing while adding PID {pid}; recreating and replaying live PIDs");

        if (!await RecreateIpcSessionAndReplayAsync(session, result.Error ?? "session not found", ct))
            return result;

        var retry = await _backend.IpcClient.AddPidAsync(session.Id, pid, createdAt, ct);
        if (retry.Success)
            _events.Add("IpcAddPidRecovered", $"{session.Name}: PID {pid} added after IPC session recovery");
        return retry;
    }

    private async Task<bool> RecreateIpcSessionAndReplayAsync(LaunchSession session, string reason, CancellationToken ct = default)
    {
        if (_backend?.IpcClient == null)
        {
            _events.Add("IpcSessionRecoverFailed", $"{session.Name}: IPC client unavailable");
            return false;
        }

        var endpoint = GetIpcEndpoint(session);
        if (string.IsNullOrEmpty(endpoint))
        {
            _events.Add("IpcSessionRecoverFailed", $"{session.Name}: no proxy endpoint for {session.ProxyId}");
            return false;
        }

        var gate = GetIpcRecoveryLock(session.Id);
        await gate.WaitAsync(ct);
        try
        {
            var create = await _backend.IpcClient.CreateSessionAsync(session.Id, endpoint, ct);
            if (!create.Success)
            {
                _events.Add("IpcSessionRecoverFailed", $"{session.Name}: createSession failed: {create.Error}");
                return false;
            }

            List<TrackedProcess> live;
            lock (session.Processes)
                live = session.Processes.Where(p => p.ExitedAt == null).ToList();

            var replayed = 0;
            foreach (var proc in live)
            {
                var createdAt = proc.CreatedAt
                    ?? ProcessMonitor.GetProcessStartTime(proc.ProcessId)
                    ?? DateTime.Now;

                var replay = await _backend.IpcClient.AddPidAsync(session.Id, proc.ProcessId, createdAt, ct);
                if (replay.Success)
                {
                    replayed++;
                }
                else
                {
                    _events.Add("IpcReplayPidFailed",
                        $"{session.Name}: PID {proc.ProcessId} ({proc.Name}) replay failed: {replay.Error}");
                }
            }

            _events.Add("IpcSessionRecreated",
                $"{session.Name}: session={session.Id} endpoint={endpoint} replayed={replayed}/{live.Count} reason={reason}");

            if (replayed > 0 && session.Status is not "exited" and not "failed")
            {
                session.RoutingStatus = "proxifyre-route-active";
                SessionsChanged?.Invoke();
            }

            return true;
        }
        finally
        {
            gate.Release();
        }
    }

    private SemaphoreSlim GetIpcRecoveryLock(string sessionId)
    {
        lock (_ipcSessionRecoveryLocks)
        {
            if (!_ipcSessionRecoveryLocks.TryGetValue(sessionId, out var gate))
            {
                gate = new SemaphoreSlim(1, 1);
                _ipcSessionRecoveryLocks[sessionId] = gate;
            }
            return gate;
        }
    }

    private string GetIpcEndpoint(LaunchSession session)
    {
        if (string.IsNullOrEmpty(session.ProxyId)) return "";
        var proxy = _config.Proxies.FirstOrDefault(p => p.Id == session.ProxyId);
        return proxy == null ? "" : $"{proxy.Host}:{proxy.Port}";
    }

    private static bool IsIpcSessionNotFound(IpcResult result)
        => !result.Success &&
           result.Error?.Contains("session not found", StringComparison.OrdinalIgnoreCase) == true;

    private static string? DeriveStoreAppRuntimeRoot(string? exePath)
    {
        if (string.IsNullOrEmpty(exePath)) return null;
        try
        {
            var dir = Path.GetDirectoryName(exePath);
            if (dir == null) return null;
            // Go up one more level to capture version/hash subdirectories
            var parent = Path.GetDirectoryName(dir);
            return parent ?? dir;
        }
        catch { return null; }
    }

    #endregion

    private (string RoutingStatus, string RouteKey) ResolveInitialRoutingStatusForTarget(LaunchTarget target, string? proxyId, bool isPersistentRoute, string sessionId)
    {
        if (string.IsNullOrEmpty(proxyId))
            return ("direct", "");

        if (_backend == null || !_config.TransparentBackend.Enabled || _config.TransparentBackend.Type != "proxifyre")
            return ("external-routing-required", "");

        var status = _backend.GetStatus();
        switch (status.State)
        {
            case "not-configured":
            case "exe-missing":
            case "service-not-installed":
            case "disabled":
                _events.Add("BackendNotReady", $"ProxiFyre: {status.State} — {status.Message}");
                return ("external-routing-required", "");
        }

        // AUMID / Store App path: use IPC when available, fail hard otherwise.
        // Never fall back to legacy child-route config-file behaviour for Store Apps.
        if (target.LaunchKind == "app-user-model-id")
        {
            if (_backend.IsIpcAvailable)
            {
                try
                {
                    var source = isPersistentRoute ? "drop-zone-set" : "drop-zone-tmp";
                    var result = _backend.EnsureRoute(target, proxyId, source: source, isPersistent: isPersistentRoute, sessionId: sessionId, ipcManaged: true);
                    _backend.WriteConfig();
                    return ("proxifyre-route-session-created", result.RouteKey);
                }
                catch (Exception ex)
                {
                    Logger.Error($"ResolveInitialRoutingStatusForTarget IPC failed for {target.DisplayName}: {ex.Message}");
                    _events.Add("BackendApplyFailed", ex.Message);
                    return ("proxifyre-route-failed", "");
                }
            }

            _events.Add("IpcUnavailable",
                $"{target.DisplayName}: ProxiFyre IPC unavailable for Store App routing. Please upgrade/restart ProxiFyre.");
            return ("proxifyre-route-failed", "");
        }

        if (_backend.IsIpcAvailable)
        {
            try
            {
                var source = isPersistentRoute ? "drop-zone-set" : "drop-zone-tmp";
                var result = _backend.EnsureRoute(target, proxyId, source: source, isPersistent: isPersistentRoute, sessionId: sessionId, ipcManaged: true);
                _backend.WriteConfig();
                return ("proxifyre-route-session-created", result.RouteKey);
            }
            catch (Exception ex)
            {
                Logger.Error($"ResolveInitialRoutingStatusForTarget IPC failed for {target.DisplayName}: {ex.Message}");
                _events.Add("BackendApplyFailed", ex.Message);
                return ("proxifyre-route-failed", "");
            }
        }

        // Generic exe path fallback: legacy config-file + restart
        try
        {
            var source = isPersistentRoute ? "drop-zone-set" : "drop-zone-tmp";
            var result = _backend.EnsureRoute(target.ExePath ?? "", proxyId, source: source, isPersistent: isPersistentRoute, sessionId: sessionId);
            _backend.WriteConfig();
            return ("proxifyre-route-launching", result.RouteKey);
        }
        catch (Exception ex)
        {
            Logger.Error($"ResolveInitialRoutingStatusForTarget failed for {target.DisplayName}: {ex.Message}");
            _events.Add("BackendApplyFailed", ex.Message);
            return ("proxifyre-route-failed", "");
        }
    }

    /// <summary>
    /// For Store App sessions whose backend app name could not be resolved before
    /// launch, observe a merged process and write its identity back into the
    /// matching AppRoute. Then save config, rewrite ProxiFyre config, and start
    /// the apply/restart flow. Returns true if the route was updated.
    /// </summary>
    private bool TryConfirmRouteFromProcess(LaunchSession session, ExternalProcessHit hit)
    {
        if (session.IpcManaged) return false;
        if (!session.NeedsProcessConfirmation) return false;

        // Find the matching AppRoute by route key or by proxyId + AUMID.
        var route = _config.AppRoutes.FirstOrDefault(r =>
            r.ProxyId == session.ProxyId &&
            r.MatchKind == "app-user-model-id" &&
            (!string.IsNullOrEmpty(session.RouteKey)
                ? _resolver.GetStableRouteKey(r) == session.RouteKey
                : r.AppUserModelId == session.AppUserModelId));

        if (route == null) return false;

        bool changed = false;

        if (!string.IsNullOrEmpty(hit.Name) && string.IsNullOrEmpty(route.ProcessName))
        {
            route.ProcessName = hit.Name;
            changed = true;
        }
        else if (!string.IsNullOrEmpty(hit.Name) && !string.IsNullOrEmpty(route.ProcessName)
                 && !string.Equals(route.ProcessName, hit.Name, StringComparison.OrdinalIgnoreCase))
        {
            _events.Add("StoreAppProcessMismatch",
                $"{session.Name}: observed process {hit.Name} does not match existing {route.ProcessName}");
            return false;
        }

        if (!string.IsNullOrEmpty(hit.ExecutablePath) && string.IsNullOrEmpty(route.ResolvedExePath))
        {
            route.ResolvedExePath = hit.ExecutablePath;
            route.ResolvedAt = DateTime.Now;
            changed = true;
        }

        if (!changed) return false;

        _events.Add("StoreAppProcessConfirmed",
            $"{session.Name}: confirmed process {hit.Name} (exe={hit.ExecutablePath}) for AUMID route");

        session.NeedsProcessConfirmation = false;
        session.RoutingStatus = "proxifyre-route-launching";
        SessionsChanged?.Invoke();

        try
        {
            _backend?.SaveSwitchConfig();
            _backend?.WriteConfig();
        }
        catch (Exception ex)
        {
            Logger.Error($"TryConfirmRouteFromProcess config write failed: {ex.Message}");
            session.RoutingStatus = "proxifyre-route-failed";
            SessionsChanged?.Invoke();
            return false;
        }

        CancellationTokenSource? cts;
        lock (_sessionCts) _sessionCts.TryGetValue(session.Id, out cts);
        var token = cts?.Token ?? CancellationToken.None;
        _ = Task.Run(() => ApplyRoutingInBackgroundAsync(session, token));

        return true;
    }

    /// <summary>
    /// Query WMI for a single PID and convert the result into an ExternalProcessHit.
    /// Returns null if the process no longer exists or WMI fails.
    /// </summary>
    private static ExternalProcessHit? QueryProcessHit(int pid)
    {
        try
        {
            using var searcher = new ManagementObjectSearcher(
                $"SELECT ProcessId, ParentProcessId, Name, ExecutablePath, CreationDate, SessionId FROM Win32_Process WHERE ProcessId = {pid}");
            foreach (ManagementObject obj in searcher.Get())
            {
                return new ExternalProcessHit
                {
                    ProcessId = Convert.ToInt32(obj["ProcessId"]),
                    ParentProcessId = obj["ParentProcessId"] != null ? Convert.ToInt32(obj["ParentProcessId"]) : null,
                    Name = obj["Name"]?.ToString() ?? "",
                    ExecutablePath = obj["ExecutablePath"]?.ToString() ?? "",
                    CreatedAt = WmiTime.ParseDmtfDateTime(obj["CreationDate"]?.ToString()),
                    SessionId = obj["SessionId"] != null ? Convert.ToInt32(obj["SessionId"]) : null
                };
            }
        }
        catch (Exception ex)
        {
            Logger.Error($"QueryProcessHit({pid}) failed: {ex.Message}");
        }
        return null;
    }

    private static readonly HashSet<string> _knownBrokerNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "ApplicationFrameHost.exe",
        "explorer.exe",
        "RuntimeBroker.exe",
        "ShellExperienceHost.exe"
    };

    private static bool IsLikelyBrokerProcess(ExternalProcessHit hit)
    {
        if (string.IsNullOrEmpty(hit.Name)) return true;

        var currentProc = System.Diagnostics.Process.GetCurrentProcess();
        if (string.Equals(hit.Name, currentProc.ProcessName + ".exe", StringComparison.OrdinalIgnoreCase))
            return true;

        if (_knownBrokerNames.Contains(hit.Name)) return true;

        // Prefer processes under WindowsApps
        if (!string.IsNullOrEmpty(hit.ExecutablePath)
            && hit.ExecutablePath.StartsWith(@"C:\Program Files\WindowsApps\", StringComparison.OrdinalIgnoreCase))
            return false;

        // Reject if no exe path at all
        if (string.IsNullOrEmpty(hit.ExecutablePath)) return true;

        return false;
    }

    private bool IsSafeToConfirm(ExternalProcessHit hit, LaunchSession session)
    {
        if (string.IsNullOrEmpty(hit.Name))
        {
            _events.Add("StoreAppProcessCandidateSkipped", $"{session.Name}: empty process name, skipping");
            return false;
        }

        if (IsLikelyBrokerProcess(hit))
        {
            _events.Add("StoreAppProcessCandidateSkipped", $"{session.Name}: {hit.Name} looks like a broker, skipping");
            return false;
        }

        return true;
    }

    private bool IsPendingStoreAppConfirmationCandidate(ExternalProcessHit hit, LaunchSession session, DateTime? processStartTime)
    {
        if (!session.NeedsProcessConfirmation) return false;
        if (!string.Equals(session.LaunchKind, "app-user-model-id", StringComparison.OrdinalIgnoreCase)) return false;
        if (!IsSafeToConfirm(hit, session)) return false;

        var createdAt = hit.CreatedAt ?? processStartTime;
        if (createdAt.HasValue)
        {
            if (createdAt.Value < session.StartedAt.AddSeconds(-2)) return false;
            if (createdAt.Value > session.StartedAt.AddSeconds(30)) return false;
        }

        return !string.IsNullOrEmpty(hit.ExecutablePath)
            && hit.ExecutablePath.StartsWith(@"C:\Program Files\WindowsApps\", StringComparison.OrdinalIgnoreCase);
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

    /// <summary>
    /// Callback fired when a merged (late or restarted) child process exits.
    /// Mirrors the root-exit logic: if other live processes remain, transition
    /// to running-via-child; otherwise begin grace window or finalize.
    /// </summary>
    private void OnMergedProcessExited(LaunchSession session, int pid)
    {
        if (session.Status is "exited" or "failed") return;
        MarkProcessExited(session, pid);
        _events.Add("ProcessExited", $"{session.Name}: merged process exited (PID={pid})");

        if (session.HasLiveProcesses)
        {
            session.Status = "running-via-child";
            session.IsLauncherHandoffDetected = true;
            _events.Add("LauncherHandoffDetected", $"{session.Name}: merged process exited, others still running");
            SessionsChanged?.Invoke();
        }
        else
        {
            BeginGraceWindowOrFinalize(session, "all merged processes exited");
        }
    }

    private void StartDescendantMonitor(LaunchSession session, CancellationTokenSource cts)
    {
        lock (_activeDescendantMonitors)
        {
            if (!_activeDescendantMonitors.Add(session.Id)) return;
        }

        _ = Task.Run(async () =>
        {
            try { await MonitorDescendantsAsync(session, cts.Token); }
            catch (OperationCanceledException) { }
            catch (Exception ex) { Logger.Error($"MonitorDescendants for {session.Name} failed: {ex.Message}"); }
            finally
            {
                lock (_activeDescendantMonitors)
                {
                    _activeDescendantMonitors.Remove(session.Id);
                }
            }
        });
    }

    /// <summary>
    /// After a grace-window reattach, MonitorDescendantsAsync may have exited
    /// because HasLiveProcesses was false. Start exactly one replacement monitor
    /// for this session.
    /// </summary>
    private void RestartMonitorIfNeeded(LaunchSession session)
    {
        CancellationTokenSource? cts;
        lock (_sessionCts)
        {
            _sessionCts.TryGetValue(session.Id, out cts);
            if (cts == null || cts.IsCancellationRequested)
            {
                cts = new CancellationTokenSource();
                _sessionCts[session.Id] = cts;
            }
        }

        StartDescendantMonitor(session, cts);
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
    /// For IPC-managed sessions, sends CloseSession over the named pipe instead.
    /// We do NOT restart the ProxiFyre service here — that would mean one UAC
    /// prompt every time a tmp session exits. The on-disk file is clean, but
    /// the live service still holds the removed rule until restart, so we
    /// mark the backend "stale" — Dashboard relabels the Restart button.
    /// </summary>
    private void CleanupSessionRoutes(LaunchSession session)
    {
        if (_backend == null) return;

        // IPC-managed sessions: close the session via named pipe.
        // No config file cleanup needed — they were never written to disk.
        if (session.IpcManaged && _backend.IpcClient != null)
        {
            _ = Task.Run(async () =>
            {
                try
                {
                    var result = await _backend.IpcClient.CloseSessionAsync(session.Id);
                    if (result.Success)
                        _events.Add("IpcSessionClosed", $"{session.Name}: IPC session closed");
                    else
                        _events.Add("IpcSessionCloseFailed", $"{session.Name}: {result.Error}");
                }
                catch (Exception ex)
                {
                    Logger.Error($"IPC CloseSession failed: {ex.Message}");
                }
            });
            return;
        }

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
    ///
    /// Lock discipline: expensive work (WMI query, backend EnsureRoute/WriteConfig/
    /// ApplyRouting) happens OUTSIDE the _sessions lock so session reads and UI
    /// refresh are not blocked.
    /// </summary>
    private bool TryMergeActiveSession(ExternalProcessHit hit)
    {
        // Pre-fetch expensive data once, outside the lock.
        var processStartTime = ProcessMonitor.GetProcessStartTime(hit.ProcessId);
        var parentMap = hit.ParentProcessId.HasValue ? ProcessMonitor.GetParentMap() : null;

        // Phase 1: decide which session (if any) should receive this process.
        // Only cheap, lock-safe checks here; the heavy WMI walk uses the pre-built map.
        (LaunchSession Session, string Role)? match = null;

        lock (_sessions)
        {
            foreach (var session in _sessions)
            {
                if (session.Status is "exited" or "failed") continue;
                if (string.IsNullOrEmpty(session.ProxyId)) continue;

                // Check if this PID is already tracked
                List<TrackedProcess> processSnapshot;
                lock (session.Processes)
                {
                    if (session.Processes.Any(p => p.ProcessId == hit.ProcessId)) return true;
                    processSnapshot = session.Processes.ToList();
                }

                string? role = null;

                // 1) Parent chain match (using pre-built map)
                if (hit.ParentProcessId.HasValue && parentMap != null)
                {
                    var livePids = processSnapshot
                        .Where(p => p.ExitedAt == null)
                        .Select(p => p.ProcessId)
                        .ToHashSet();
                    if (ProcessMonitor.IsAncestorOfAny(hit.ProcessId, livePids, parentMap))
                        role = "descendant";
                }

                // 2) Route identity match (strict stable-key comparison)
                // Only merge if the hit matches the SAME route the session was created for.
                if (role == null)
                {
                    var hitRoute = _config.AppRoutes.FirstOrDefault(r =>
                        r.ProxyId == session.ProxyId && r.Enabled &&
                        (!string.IsNullOrEmpty(hit.ExecutablePath) &&
                         (string.Equals(r.ExePath, hit.ExecutablePath, StringComparison.OrdinalIgnoreCase)
                          || string.Equals(r.ResolvedExePath, hit.ExecutablePath, StringComparison.OrdinalIgnoreCase))));
                    if (hitRoute != null)
                    {
                        var hitRouteKey = _resolver.GetStableRouteKey(hitRoute);
                        if (session.RouteKey == hitRouteKey) role = "descendant";
                    }
                }

                // 3) Process name match
                if (role == null)
                {
                    var route = _config.AppRoutes.FirstOrDefault(r =>
                        r.ProxyId == session.ProxyId && r.Enabled &&
                        string.Equals(AppIdentityResolver.NormalizeProcessName(r.ProcessName), hit.Name, StringComparison.OrdinalIgnoreCase));
                    if (route != null)
                    {
                        var routeKey = _resolver.GetStableRouteKey(route);
                        if (session.RouteKey == routeKey) role = "descendant";
                    }
                }

                // 4) Same directory heuristic — only when route key is unavailable
                if (role == null && !string.IsNullOrEmpty(hit.ExecutablePath) && string.IsNullOrEmpty(session.RouteKey))
                {
                    var hitDir = Path.GetDirectoryName(hit.ExecutablePath);
                    var sessionDir = Path.GetDirectoryName(session.ExePath);
                    if (!string.IsNullOrEmpty(hitDir) && !string.IsNullOrEmpty(sessionDir)
                        && string.Equals(hitDir, sessionDir, StringComparison.OrdinalIgnoreCase))
                    {
                        role = "descendant";
                    }
                }

                // 5) Pending Store App confirmation candidate.
                // AUMID-only routes intentionally have a route key, but before
                // confirmation they may have no exe path or process name to match.
                // SessionSupervisor feeds all new process events here, so keep this
                // narrow: close to launch time, non-broker, WindowsApps executable.
                if (role == null && IsPendingStoreAppConfirmationCandidate(hit, session, processStartTime))
                {
                    role = "store-app-candidate";
                }

                // 6) Store App runtime path-root match for IPC-managed sessions.
                //    This catches late-started or restarted Store App processes that
                //    are not descendants in the parent chain but live under the
                //    same runtime directory (e.g. AppData\Local\OpenAI\Codex\bin\...).
                if (role == null && session.IpcManaged && !string.IsNullOrEmpty(session.StoreAppRuntimeRoot) && !string.IsNullOrEmpty(hit.ExecutablePath))
                {
                    if (hit.ExecutablePath.StartsWith(session.StoreAppRuntimeRoot, StringComparison.OrdinalIgnoreCase))
                    {
                        role = "descendant";
                    }
                }

                if (role != null)
                {
                    match = (session, role);
                    break;
                }
            }
        }

        if (match == null) return false;

        var matchedSession = match.Value.Session;
        var matchedRole = match.Value.Role;

        // Phase 2: mutate the session under a brief lock.
        bool isRestart;
        bool shouldEnsureChildRoute = false;
        string eventName;
        string eventMessage;
        lock (_sessions)
        {
            // Re-validate: the session may have changed since we released the lock.
            if (matchedSession.Status is "exited" or "failed") return false;
            lock (matchedSession.Processes)
            {
                if (matchedSession.Processes.Any(p => p.ProcessId == hit.ProcessId)) return true;

                shouldEnsureChildRoute = !string.IsNullOrEmpty(hit.ExecutablePath) &&
                    !matchedSession.Processes.Any(p =>
                        string.Equals(p.ExecutablePath, hit.ExecutablePath, StringComparison.OrdinalIgnoreCase));

                var rootCreatedAt = processStartTime ?? DateTime.Now;
                isRestart = matchedSession.Status == "waiting-for-restart";
                matchedSession.Processes.Add(new TrackedProcess
                {
                    ProcessId = hit.ProcessId,
                    ParentProcessId = hit.ParentProcessId,
                    Name = hit.Name,
                    ExecutablePath = hit.ExecutablePath,
                    Role = isRestart ? "restarted-child" : matchedRole,
                    CreatedAt = rootCreatedAt
                });
            }

            if (isRestart)
            {
                matchedSession.Status = "running";
                eventName = "ProcessReattached";
                eventMessage = $"{matchedSession.Name}: {hit.Name} (PID={hit.ProcessId}) reattached during grace window";
            }
            else
            {
                eventName = "ProcessMerged";
                eventMessage = $"{matchedSession.Name}: {hit.Name} (PID={hit.ProcessId}) merged into existing session ({matchedRole})";
            }
        }

        // Register exit tracking and raise notifications outside _sessions lock.
        _processMonitor.TrackPid(hit.ProcessId, () => OnMergedProcessExited(matchedSession, hit.ProcessId));
        _events.Add(eventName, eventMessage);
        if (isRestart)
            RestartMonitorIfNeeded(matchedSession);
        SessionsChanged?.Invoke();

        // Phase 2.5: Store App process confirmation. If this session was launched
        // via AUMID and we haven't confirmed the real process yet, try to capture
        // it from the merged process. If confirmation succeeds it already writes
        // config and starts apply/restart, so skip Phase 3 child-route work.
        bool confirmed = false;
        if (matchedSession.NeedsProcessConfirmation && IsSafeToConfirm(hit, matchedSession))
            confirmed = TryConfirmRouteFromProcess(matchedSession, hit);

        // IPC Phase: for IPC-managed sessions, send every merged PID to ProxiFyre
        // via named pipe. No config write, no restart, no persistence.
        if (matchedSession.IpcManaged && _backend?.IpcClient != null)
        {
            var childCreatedAt = hit.CreatedAt ?? processStartTime ?? DateTime.Now;
            _events.Add("IpcAddPidRequest",
                $"{matchedSession.Name}: merged child PID={hit.ProcessId} role={matchedRole} {WmiTime.Describe(childCreatedAt)} path={hit.ExecutablePath}");
            _ = Task.Run(async () =>
            {
                try
                {
                    var ipcResult = await AddPidToIpcSessionAsync(matchedSession, hit.ProcessId, childCreatedAt);
                    if (ipcResult.Success)
                    {
                        _events.Add("IpcAddPidOk", $"{matchedSession.Name}: child {hit.Name} (PID={hit.ProcessId}) added via IPC");
                        if (matchedSession.RoutingStatus == "proxifyre-route-session-created")
                        {
                            matchedSession.RoutingStatus = "proxifyre-route-active";
                            SessionsChanged?.Invoke();
                            try { RouteActivated?.Invoke(matchedSession); }
                            catch (Exception ex) { Logger.Error($"RouteActivated handler: {ex.Message}"); }
                        }
                    }
                    else
                    {
                        _events.Add("IpcAddPidFailed", $"{matchedSession.Name}: child {hit.Name} (PID={hit.ProcessId}) IPC failed: {ipcResult.Error}");
                    }
                }
                catch (Exception ex)
                {
                    Logger.Error($"IPC AddPid for merged child failed: {ex.Message}");
                }
            });
        }

        // Phase 3: legacy config-file backend work (EnsureRoute + WriteConfig + ApplyRouting)
        // completely outside the lock so we don't block session reads or UI refresh.
        if (!confirmed &&
            !string.IsNullOrEmpty(hit.ExecutablePath) &&
            _backend != null &&
            !matchedSession.IpcManaged &&
            shouldEnsureChildRoute &&
            ShouldAutoRouteMergedChild(matchedSession))
        {
            bool shouldRoute;
            var debounceKey = $"{matchedSession.Id}|{hit.ExecutablePath?.ToLowerInvariant() ?? ""}|{matchedSession.ProxyId}";
            lock (_lastChildRouteTime)
            {
                var now = DateTime.Now;
                shouldRoute = !_lastChildRouteTime.TryGetValue(debounceKey, out var last) || (now - last).TotalSeconds >= 2;
                if (shouldRoute) _lastChildRouteTime[debounceKey] = now;
            }
            if (shouldRoute)
            {
                try
                {
                    var childResult = _backend.EnsureRoute(hit.ExecutablePath ?? "", matchedSession.ProxyId!,
                        source: "child-detected",
                        isPersistent: matchedSession.IsPersistentLaunch,
                        sessionId: matchedSession.IsPersistentLaunch ? null : matchedSession.Id);
                    if (childResult.Changed)
                    {
                        _backend.WriteConfig();
                        CancellationTokenSource? cts;
                        lock (_sessionCts) _sessionCts.TryGetValue(matchedSession.Id, out cts);
                        var token = cts?.Token ?? CancellationToken.None;
                        _ = Task.Run(() => ApplyRoutingInBackgroundAsync(matchedSession, token));
                        _events.Add("ChildRouteAdded", $"{matchedSession.Name}: child {hit.Name} routed via {matchedSession.ProxyId}");
                    }
                    else
                    {
                        _events.Add("ChildRouteSkipped", $"{matchedSession.Name}: child {hit.Name} route unchanged (duplicate)");
                    }
                }
                catch (Exception ex)
                {
                    Logger.Error($"Child route ensure failed: {ex.Message}");
                }
            }
        }

        return true;
    }

    private static bool ShouldAutoRouteMergedChild(LaunchSession session)
    {
        // Browser sessions already carry explicit proxy command-line args.
        // For the LEGACY config-file path, Store App children must NOT be auto-
        // persisted as global appNames rules (they pollute every matching process).
        // Store Apps launched via AUMID are now IPC-managed (PID-isolated), so
        // their children bypass this method entirely and are sent via named pipe.
        return string.Equals(session.Kind, "generic", StringComparison.OrdinalIgnoreCase)
            && string.Equals(session.LaunchKind, "exe", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Guess the stable route key for an existing session by looking at its
    /// tracked processes and matching against known routes.
    /// </summary>
    private string GuessSessionRouteKey(LaunchSession session)
    {
        TrackedProcess? liveProcess;
        lock (session.Processes)
        {
            liveProcess = session.Processes.FirstOrDefault(p => p.ExitedAt == null);
        }
        var exePath = liveProcess?.ExecutablePath ?? session.ExePath;
        var name = liveProcess?.Name ?? Path.GetFileName(session.ExePath);

        var route = _config.AppRoutes.FirstOrDefault(r =>
            r.ProxyId == session.ProxyId && r.Enabled &&
            (string.Equals(r.ExePath, exePath, StringComparison.OrdinalIgnoreCase)
             || string.Equals(r.ResolvedExePath, exePath, StringComparison.OrdinalIgnoreCase)
             || string.Equals(AppIdentityResolver.NormalizeProcessName(r.ProcessName), name, StringComparison.OrdinalIgnoreCase)));

        if (route != null) return _resolver.GetStableRouteKey(route);

        // Fallback: derive from session itself
        var identity = string.IsNullOrEmpty(session.ExePath)
            ? AppIdentityResolver.NormalizeProcessName(name)
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
             || (!string.IsNullOrEmpty(hit.ExecutablePath) && string.Equals(r.ResolvedExePath, hit.ExecutablePath, StringComparison.OrdinalIgnoreCase))
             || (!string.IsNullOrEmpty(hit.Name) && string.Equals(AppIdentityResolver.NormalizeProcessName(r.ProcessName), hit.Name, StringComparison.OrdinalIgnoreCase))));
        if (route == null) return false;

        var routeKey = _resolver.GetStableRouteKey(route);
        var rootCreatedAt = ProcessMonitor.GetProcessStartTime(hit.ProcessId) ?? DateTime.Now;

        // If an active or waiting-for-restart session already has this route key,
        // merge into it instead of creating a duplicate card.
        LaunchSession? existingSession = null;
        lock (_sessions)
        {
            existingSession = _sessions.FirstOrDefault(s =>
                s.Status is not "exited" and not "failed" &&
                s.RouteKey == routeKey);
        }

        if (existingSession != null)
        {
            lock (existingSession.Processes)
            {
                if (!existingSession.Processes.Any(p => p.ProcessId == hit.ProcessId))
                {
                    existingSession.Processes.Add(new TrackedProcess
                    {
                        ProcessId = hit.ProcessId,
                        ParentProcessId = hit.ParentProcessId,
                        Name = hit.Name,
                        ExecutablePath = hit.ExecutablePath,
                        Role = "root",
                        CreatedAt = rootCreatedAt
                    });
                }
            }
            if (existingSession.Status == "waiting-for-restart")
            {
                existingSession.Status = "running";
                _events.Add("ProcessReattached", $"{existingSession.Name}: {hit.Name} (PID={hit.ProcessId}) reattached during grace window");
            }
            else
            {
                _events.Add("ProcessMerged", $"{existingSession.Name}: {hit.Name} (PID={hit.ProcessId}) merged into existing external session");
            }
            _processMonitor.TrackPid(hit.ProcessId, () => OnMergedProcessExited(existingSession, hit.ProcessId));
            if (existingSession.IpcManaged && _backend?.IpcClient != null)
            {
                _ = Task.Run(async () =>
                {
                    var addResult = await AddPidToIpcSessionAsync(existingSession, hit.ProcessId, rootCreatedAt);
                    if (addResult.Success)
                        _events.Add("IpcAddPidOk", $"{existingSession.Name}: external PID {hit.ProcessId} added via IPC");
                    else
                        _events.Add("IpcAddPidFailed", $"{existingSession.Name}: external PID {hit.ProcessId} IPC failed: {addResult.Error}");
                });
            }
            SessionsChanged?.Invoke();
            return true;
        }

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
            RoutingStatus = route.IpcManaged && _backend?.IsIpcAvailable == true
                ? "proxifyre-route-session-created"
                : ResolveExternalRoutingStatus(),
            RouteKey = routeKey,
            IpcManaged = route.IpcManaged && _backend?.IsIpcAvailable == true
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
                        if (session.IpcManaged && _backend?.IpcClient != null)
                        {
                            _ = Task.Run(async () =>
                            {
                                var createdAt = desc.CreatedAt ?? ProcessMonitor.GetProcessStartTime(desc.ProcessId) ?? DateTime.Now;
                                var addResult = await AddPidToIpcSessionAsync(session, desc.ProcessId, createdAt);
                                if (addResult.Success)
                                    _events.Add("IpcAddPidOk", $"{sessionName}: external descendant {desc.Name} (PID={desc.ProcessId}) added via IPC");
                                else
                                    _events.Add("IpcAddPidFailed", $"{sessionName}: external descendant {desc.Name} IPC failed: {addResult.Error}");
                            });
                        }
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
        StartDescendantMonitor(session, cts);

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

        if (session.IpcManaged && _backend?.IpcClient != null)
            StartIpcRoutingForSession(session, hit.ProcessId, rootCreatedAt);

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
    private (string RoutingStatus, string RouteKey) ResolveInitialRoutingStatus(string exePath, string mode, string? proxyId, bool isPersistentRoute, string sessionId)
    {
        if (mode == "direct" || string.IsNullOrEmpty(proxyId))
            return ("direct", "");

        // No backend configured → fall back to v0.5 "external router" semantics
        if (_backend == null || !_config.TransparentBackend.Enabled || _config.TransparentBackend.Type != "proxifyre")
            return ("external-routing-required", "");

        var status = _backend.GetStatus();
        // Genuine "can't proceed" cases — exe / service / driver missing.
        switch (status.State)
        {
            case "not-configured":
            case "exe-missing":
            case "service-not-installed":
            case "disabled":
                _events.Add("BackendNotReady", $"ProxiFyre: {status.State} — {status.Message}");
                return ("external-routing-required", "");
        }

        // Launch-first / apply-async (plan_opus_v0.7): write config NOW, but defer the
        // service restart (UAC + sc + 5-15s wait) to ApplyRoutingInBackgroundAsync after
        // the app has already started. App's first connections may race the route — that
        // is a ProxiFyre design limit; for tmp/persistent "try it" semantics it's fine.
        if (_backend.IsIpcAvailable)
        {
            try
            {
                var source = isPersistentRoute ? "drop-zone-set" : "drop-zone-tmp";
                var result = _backend.EnsureRoute(exePath, proxyId, source: source, isPersistent: isPersistentRoute, sessionId: sessionId, ipcManaged: true);
                _backend.WriteConfig();
                return ("proxifyre-route-session-created", result.RouteKey);
            }
            catch (Exception ex)
            {
                Logger.Error($"ResolveInitialRoutingStatus IPC failed for {exePath}: {ex.Message}");
                _events.Add("BackendApplyFailed", ex.Message);
                return ("proxifyre-route-failed", "");
            }
        }

        try
        {
            var source = isPersistentRoute ? "drop-zone-set" : "drop-zone-tmp";
            var result = _backend.EnsureRoute(exePath, proxyId, source: source, isPersistent: isPersistentRoute, sessionId: sessionId);
            _backend.WriteConfig();
            return ("proxifyre-route-launching", result.RouteKey);
        }
        catch (Exception ex)
        {
            Logger.Error($"ResolveInitialRoutingStatus failed for {exePath}: {ex.Message}");
            _events.Add("BackendApplyFailed", ex.Message);
            return ("proxifyre-route-failed", "");
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

        if (session.IpcManaged)
        {
            _events.Add("IpcChildrenIncludeReplayQueued",
                $"{session.Name}: live children are routed by default; replaying IPC session {session.Id}");
            _ = Task.Run(async () =>
            {
                try
                {
                    await RecreateIpcSessionAndReplayAsync(session, "manual child include/replay");
                    _events.Add("IpcChildrenIncluded", $"{session.Name}: live children are routed by default via IPC session {session.Id}");
                }
                catch (Exception ex)
                {
                    Logger.Error($"RouteDetectedChildren IPC replay failed: {ex.Message}");
                    _events.Add("IpcSessionRecoverFailed", $"{session.Name}: {ex.Message}");
                    session.RoutingStatus = "proxifyre-route-failed";
                    SessionsChanged?.Invoke();
                }
            });
            return session.RoutingStatus;
        }

        try
        {
            // Inherit the parent launch's persistence so a child of a tmp parent
            // doesn't get silently saved (#4 in GPT review v0.7).
            var childPersistent = session.IsPersistentLaunch;
            int routed = 0;
            foreach (var path in exePaths.Distinct(StringComparer.OrdinalIgnoreCase))
            {
                if (string.IsNullOrEmpty(path)) continue;
                var childResult = _backend.EnsureRoute(
                    path,
                    session.ProxyId,
                    source: "child-detected",
                    isPersistent: childPersistent,
                    sessionId: childPersistent ? null : session.Id);
                if (childResult.Changed) routed++;
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

    public void AddSession(LaunchSession session)
    {
        lock (_sessions)
        {
            _sessions.Add(session);
        }
        var cts = new CancellationTokenSource();
        lock (_sessionCts) _sessionCts[session.Id] = cts;
        SessionsChanged?.Invoke();

        var rootPid = session.RootProcessId;
        if (rootPid.HasValue)
        {
            var sessionName = session.Name;
            var rootCreatedAt = session.RootCreatedAt ?? DateTime.Now;

            _ = Task.Run(async () =>
            {
                try
                {
                    await _processMonitor.TrackProcessTreeAsync(
                        rootPid.Value, rootCreatedAt, durationSeconds: 10,
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

                            if (session.IpcManaged && _backend?.IpcClient != null)
                            {
                                var childCreatedAt = desc.CreatedAt ?? DateTime.Now;
                                var childPid = desc.ProcessId;
                                var childName = desc.Name;
                                _events.Add("IpcAddPidRequest",
                                    $"{sessionName}: descendant PID={childPid} {WmiTime.Describe(childCreatedAt)} path={desc.ExecutablePath}");
                                _ = Task.Run(async () =>
                                {
                                    try
                                    {
                                        var ipcResult = await AddPidToIpcSessionAsync(session, childPid, childCreatedAt);
                                        if (ipcResult.Success)
                                        {
                                            _events.Add("IpcAddPidOk", $"{sessionName}: child {childName} (PID={childPid}) added via IPC");
                                            if (session.RoutingStatus == "proxifyre-route-session-created")
                                            {
                                                session.RoutingStatus = "proxifyre-route-active";
                                                SessionsChanged?.Invoke();
                                                try { RouteActivated?.Invoke(session); }
                                                catch (Exception ex) { Logger.Error($"RouteActivated handler: {ex.Message}"); }
                                            }
                                        }
                                        else
                                        {
                                            _events.Add("IpcAddPidFailed", $"{sessionName}: child {childName} (PID={childPid}) IPC failed: {ipcResult.Error}");
                                        }
                                    }
                                    catch (Exception ex)
                                    {
                                        Logger.Error($"IPC AddPid for descendant failed: {ex.Message}");
                                    }
                                });
                            }

                            if (session.NeedsProcessConfirmation)
                            {
                                var hit = new ExternalProcessHit
                                {
                                    ProcessId = desc.ProcessId,
                                    ParentProcessId = desc.ParentProcessId,
                                    Name = desc.Name,
                                    ExecutablePath = desc.ExecutablePath ?? ""
                                };
                                if (IsSafeToConfirm(hit, session))
                                    TryConfirmRouteFromProcess(session, hit);
                            }
                        });
                }
                catch (Exception ex)
                {
                    Logger.Error($"TrackProcessTreeAsync for {sessionName} failed: {ex.Message}");
                }
            });

            _processMonitor.TrackPid(rootPid.Value, () =>
            {
                if (session.Status is "exited" or "failed") return;
                MarkProcessExited(session, rootPid.Value);
                _events.Add("RootProcessExited", $"{sessionName} root process exited");

                if (session.HasLiveProcesses)
                {
                    session.Status = "running-via-child";
                    session.IsLauncherHandoffDetected = true;
                    _events.Add("LauncherHandoffDetected", $"{sessionName}: launcher exited, child still running");
                    SessionsChanged?.Invoke();
                }
                else
                {
                    BeginGraceWindowOrFinalize(session, "root process exited");
                }
            });
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
