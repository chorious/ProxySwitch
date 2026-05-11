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
    private readonly ProxifierProfileGenerator _profileGen;
    private readonly HandoffScorer _scorer;
    private readonly ProxyConfig _config;

    public event Action? SessionsChanged;
    /// <summary>
    /// Fired when root exited with no descendant alive, and correlated scoring
    /// returned only medium-confidence candidates that need user confirmation.
    /// </summary>
    public event Action<LaunchSession, List<HandoffCandidate>>? CorrelatedCandidatesFound;

    public SessionManager(AppLauncher launcher, ProcessMonitor monitor, EventStore events, ProxyConfig config)
    {
        _launcher = launcher;
        _processMonitor = monitor;
        _events = events;
        _config = config;
        _profileGen = new ProxifierProfileGenerator();
        _scorer = new HandoffScorer();
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
            RoutingStatus = mode == "browser-direct" ? "direct" : "browser-arg"
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

        // Browser: track by userDataDir + root PID liveness only
        // Do NOT use process tree tracking — browser sub-processes are not "handoff children"
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

    public LaunchResult LaunchGeneric(string exePath, string name, string mode, string? proxyId = null, bool profileLoaded = false)
    {
        _events.Add("SessionCreated", $"Session for {name}");
        _events.Add("LaunchStarted", $"Starting {name} in {mode} mode");

        // Capture process baseline BEFORE launching, for correlated handoff fallback.
        // Use the SYNC variant: calling .GetAwaiter().GetResult() on the async one
        // from the UI thread deadlocks (await continuation needs UI SyncContext,
        // UI thread is blocked on GetResult). GetFullProcessSnapshots is already
        // synchronous internally — no real benefit from the async wrapper here.
        var baseline = _processMonitor.CaptureProcessSnapshots();
        var baselineTask = Task.FromResult(baseline);

        var result = _launcher.LaunchGeneric(exePath);

        if (!result.Success)
        {
            _events.Add("LaunchFailed", $"{name}: {result.Error}");
            return result;
        }

        var rootCreatedAt = result.ProcessId.HasValue
            ? (ProcessMonitor.GetProcessStartTime(result.ProcessId.Value) ?? DateTime.Now)
            : DateTime.Now;

        var routingStatus = mode switch
        {
            "direct" => "direct",
            _ when profileLoaded => "profile-loaded",
            _ => "unverified"
        };

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

            // Track process tree (fire and forget but with exception handling)
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

            // Track root PID liveness
            _processMonitor.TrackPid(rootPid, () =>
            {
                MarkProcessExited(session, rootPid);
                _events.Add("RootProcessExited", $"{name} root process exited");

                if (session.HasLiveProcesses)
                {
                    session.Status = "running-via-child";
                    session.IsLauncherHandoffDetected = true;

                    var liveChild = session.Processes.FirstOrDefault(p => p.ExitedAt == null && p.Role == "descendant");
                    if (liveChild != null && !session.IsRoutingAssisted)
                    {
                        // Default to unverified; check generated profile asynchronously
                        // to avoid blocking the UI thread on XML IO.
                        session.RoutingStatus = "unverified";
                        session.RoutingWarning = $"{liveChild.Name} is running but Proxifier routing is unverified. Generated profile may need a rule for this exe.";
                        _events.Add("RoutingUnverified", $"{name}: {liveChild.Name} running, routing unverified");

                        var profileKey = ProxyIdToGeneratedKey(proxyId);
                        var childName = liveChild.Name;
                        if (profileKey != null)
                        {
                            _ = Task.Run(() =>
                            {
                                try
                                {
                                    if (IsChildInGeneratedRule(profileKey, childName))
                                    {
                                        // Upgrade to assisted if already covered
                                        session.RoutingStatus = "assisted";
                                        session.IsRoutingAssisted = true;
                                        session.RoutingWarning = null;
                                        _events.Add("LauncherHandoffDetected", $"{name}: child {childName} already in assist rule");
                                        SessionsChanged?.Invoke();
                                    }
                                }
                                catch (Exception ex)
                                {
                                    Logger.Error($"IsChildInGeneratedRule check failed for {name}: {ex.Message}");
                                }
                            });
                        }
                    }
                    else
                    {
                        _events.Add("LauncherHandoffDetected", $"{name}: launcher exited, child still running");
                    }
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
                            session.Status = "exited";
                            session.ExitedAt = DateTime.Now;
                            _events.Add("SessionExited", $"{name} exited (correlated check failed)");
                            SessionsChanged?.Invoke();
                        }
                    });
                }
            });

            // Also track descendants for exit detection (added in onDescendant callback)
            _ = Task.Run(async () =>
            {
                try
                {
                    await MonitorDescendantsAsync(session, cts.Token);
                }
                catch (OperationCanceledException) { /* expected on RemoveSession/Dispose */ }
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
        // Poll every 2s for descendant/correlated process exits.
        // Exits when:
        //  - no more live processes (session naturally exited)
        //  - session status is terminal
        //  - cancellation requested (RemoveSession or Dispose)
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

    /// <summary>
    /// Run correlated handoff detection after root exits with no descendant alive.
    /// </summary>
    private async Task TryCorrelatedHandoffAsync(LaunchSession session, Task<List<ProcessSnapshot>> baselineTask)
    {
        var baseline = await baselineTask;
        var baselinePids = baseline.Select(p => p.ProcessId).ToHashSet();
        var rootExitedAt = session.Processes.FirstOrDefault(p => p.Role == "root")?.ExitedAt ?? DateTime.Now;

        // Give some extra time for slow handoff (e.g. UAC, COM)
        await Task.Delay(3000);

        var current = await _processMonitor.CaptureProcessSnapshotsAsync();
        var newProcesses = current
            .Where(p => !baselinePids.Contains(p.ProcessId))
            .Where(p => p.ProcessId != session.RootProcessId) // exclude launcher itself
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

        // Filter to alive processes only
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
            // Multiple high-confidence: ask user to pick (treat as medium for confirmation)
            _events.Add("CorrelatedCandidatesFound", $"{session.Name}: {high.Count} high-confidence candidates, user confirmation needed");
            CorrelatedCandidatesFound?.Invoke(session, high);
            // Leave session in checking-correlated state; user confirms or ignores
            return;
        }

        if (medium.Count > 0)
        {
            _events.Add("CorrelatedCandidatesFound", $"{session.Name}: {medium.Count} medium-confidence candidates, user confirmation needed");
            CorrelatedCandidatesFound?.Invoke(session, medium);
            return;
        }

        // Only low-confidence candidates → finalize
        FinalizeAsExited(session, $"only low-confidence candidates ({candidates.Count}), ignored");
    }

    private void FinalizeAsExited(LaunchSession session, string reason)
    {
        session.Status = "exited";
        session.ExitedAt = DateTime.Now;
        _events.Add("SessionExited", $"{session.Name} exited ({reason})");
        SessionsChanged?.Invoke();
    }

    /// <summary>
    /// Attach a correlated handoff candidate to the session. Updates state and tracks the new PID.
    /// </summary>
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
        session.RoutingStatus = "unverified";
        session.RoutingWarning =
            $"⚠ Correlated handoff (inferred, not proven): {proc.Name} matched score {candidate.Score}. " +
            $"Proxifier may need a separate rule for this exe — routing is unverified.";

        var verb = autoAttached ? "auto-attached" : "user-attached";
        _events.Add("CorrelatedHandoffAttached", $"{session.Name}: {verb} {proc.Name} (PID={proc.ProcessId}, score={candidate.Score}, confidence={candidate.Confidence})");

        // Track the new PID for exit
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

    /// <summary>
    /// User decided to ignore correlated candidates. Finalize session as exited.
    /// </summary>
    public void IgnoreCorrelated(LaunchSession session)
    {
        _events.Add("CorrelatedHandoffIgnored", $"{session.Name}: user ignored correlated candidates");
        FinalizeAsExited(session, "user ignored correlated candidates");
    }

    private static string? ProxyIdToGeneratedKey(string? proxyId) => proxyId switch
    {
        "p10708" => "generated10708",
        "p10808" => "generated10808",
        _ => null
    };

    private static string? ProxyIdToRuleName(string? proxyId) => proxyId switch
    {
        "p10708" => "ProxySwitch_10708_AssistedApps",
        "p10808" => "ProxySwitch_10808_AssistedApps",
        _ => null
    };

    private bool IsChildInGeneratedRule(string profileKey, string exeName)
    {
        if (!_config.Proxifier.Profiles.TryGetValue(profileKey, out var path)) return false;
        if (!File.Exists(path)) return false;
        var ruleName = profileKey switch
        {
            "generated10708" => "ProxySwitch_10708_AssistedApps",
            "generated10808" => "ProxySwitch_10808_AssistedApps",
            _ => null
        };
        if (ruleName == null) return false;
        return _profileGen.RuleContains(path, ruleName, exeName);
    }

    /// <summary>
    /// Add child.exe (and optionally launcher.exe) to the generated Proxifier profile for the session's proxy.
    /// Then load the generated profile so Proxifier picks up the new rule.
    /// Returns true if the rule was added or already present and profile loaded.
    /// </summary>
    public bool AddAssistRule(LaunchSession session)
    {
        var profileKey = ProxyIdToGeneratedKey(session.ProxyId);
        var ruleName = ProxyIdToRuleName(session.ProxyId);
        if (profileKey == null || ruleName == null)
        {
            _events.Add("AssistRuleFailed", $"{session.Name}: no proxy mapping for {session.ProxyId}");
            return false;
        }
        if (!_config.Proxifier.Profiles.TryGetValue(profileKey, out var profilePath))
        {
            _events.Add("AssistRuleFailed", $"{session.Name}: profile key '{profileKey}' not in config");
            return false;
        }

        var liveChild = session.Processes.FirstOrDefault(p =>
            p.ExitedAt == null && (p.Role == "descendant" || p.Role == "correlated"));
        if (liveChild == null)
        {
            _events.Add("AssistRuleFailed", $"{session.Name}: no live descendant or correlated process to add");
            return false;
        }

        var launcherName = Path.GetFileName(session.ExePath);
        var childName = liveChild.Name;

        try
        {
            var changed = _profileGen.AddApplicationToRule(profilePath, ruleName, launcherName, childName);
            if (changed)
            {
                _events.Add("AssistRuleAdded", $"{session.Name}: added {childName} to {ruleName} in {Path.GetFileName(profilePath)}");
            }
            else
            {
                _events.Add("AssistRuleSuggested", $"{session.Name}: {childName} already in {ruleName}");
            }

            // Load the generated profile
            _launcher.LoadProxifierProfile(profileKey);
            _events.Add("GeneratedProfileLoaded", $"{session.Name}: loaded {Path.GetFileName(profilePath)}");

            session.IsRoutingAssisted = true;
            session.RoutingStatus = "assisted";
            session.RoutingWarning = null;
            SessionsChanged?.Invoke();
            return true;
        }
        catch (FileNotFoundException ex)
        {
            _events.Add("AssistRuleFailed", $"{session.Name}: {ex.Message}");
            MessageBox.Show(ex.Message, "Generated Profile Missing",
                MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return false;
        }
        catch (Exception ex)
        {
            _events.Add("AssistRuleFailed", $"{session.Name}: {ex.Message}");
            Logger.Error($"AddAssistRule failed: {ex}");
            MessageBox.Show($"Failed to add assist rule:\n{ex.Message}", "Error",
                MessageBoxButtons.OK, MessageBoxIcon.Error);
            return false;
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
        // Cancel the descendant monitor task
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
        // Cancel all session monitors
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
        // ProcessMonitor is owned by MainForm, do not dispose here
    }
}
