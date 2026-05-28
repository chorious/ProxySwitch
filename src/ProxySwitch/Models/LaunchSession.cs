namespace ProxySwitch.Models;

public sealed class LaunchSession
{
    public string Id { get; init; } = Guid.NewGuid().ToString("N")[..8];
    public string Name { get; init; } = "";
    public string ExePath { get; init; } = "";
    public string Kind { get; init; } = "generic"; // "browser" or "generic"
    public string Mode { get; init; } = "direct";  // direct, proxy, browser-direct, browser-proxy
    public string? ProxyId { get; init; }
    public string? UserDataDir { get; init; }
    public DateTime StartedAt { get; init; } = DateTime.Now;
    public DateTime? ExitedAt { get; set; }
    public int? RootProcessId { get; set; }
    public DateTime? RootCreatedAt { get; set; }
    public List<TrackedProcess> Processes { get; } = [];
    public string Status { get; set; } = "starting";
    // starting / running / running-via-child / running-via-correlated / checking-correlated /
    // waiting-for-restart / exited / failed

    /// <summary>
    /// Routing semantics under the v0.5 "Routing Decoupled" model:
    ///   "direct"                     — no proxy intent, browser --no-proxy-server or generic direct
    ///   "browser-proxy-active"       — browser launched with --proxy-server, routing controlled by us
    ///   "external-routing-required"  — generic app + proxy intent, routing handled by external router (Clash Verge / v2ray)
    /// ProxySwitch NEVER claims routing is verified for the generic case.
    /// </summary>
    public string RoutingStatus { get; set; } = "direct";

    /// <summary>
    /// Whether root process exited but a descendant or correlated process is still alive.
    /// Pure observability — no routing implication.
    /// </summary>
    public bool IsLauncherHandoffDetected { get; set; }

    /// <summary>
    /// Whether this launch wrote a persistent (saved) AppRoute. Tmp launches set this
    /// to false. RouteDetectedChild reads this so a child of a tmp parent inherits
    /// tmp scope instead of silently being saved to proxyswitch.json.
    /// </summary>
    public bool IsPersistentLaunch { get; set; }

    public string? LastError { get; set; }

    /// <summary>
    /// Stable route identity set at session creation. Used for merge/attach
    /// decisions instead of repeatedly guessing from process state.
    /// </summary>
    public string RouteKey { get; set; } = "";

    /// <summary>
    /// The launch kind that created this session (exe, app-user-model-id, shortcut).
    /// Used to decide whether process confirmation heuristics apply.
    /// </summary>
    public string LaunchKind { get; set; } = "exe";

    /// <summary>
    /// For Store-app sessions: the AUMID used to launch. Empty for normal executables.
    /// </summary>
    public string AppUserModelId { get; set; } = "";

    /// <summary>
    /// True when the route is managed via IPC (named pipe) to ProxiFyre instead
    /// of being written to app-config.json. Store App sessions use this path.
    /// </summary>
    public bool IpcManaged { get; set; }

    /// <summary>
    /// True when the route's backend app name could not be resolved before launch.
    /// We must observe the real process and write its identity back into AppRoute
    /// before ProxiFyre can route it.
    /// </summary>
    public bool NeedsProcessConfirmation { get; set; }

    /// <summary>
    /// For Store App sessions: the derived runtime path root (e.g. C:\Users\...\AppData\Local\OpenAI\Codex\bin).
    /// Used by SessionSupervisor to auto-merge late-started or restarted Store App processes
    /// into the IPC session without relying on parent-child chain.
    /// </summary>
    public string? StoreAppRuntimeRoot { get; set; }

    public int? LiveProcessId
    {
        get
        {
            lock (Processes)
            {
                var live = Processes.Where(p => p.ExitedAt == null).ToList();
                if (live.Count == 0) return null;
                var root = live.FirstOrDefault(p => p.Role == "root");
                if (root != null) return root.ProcessId;
                return live.FirstOrDefault()?.ProcessId;
            }
        }
    }

    public bool HasLiveProcesses
    {
        get { lock (Processes) return Processes.Any(p => p.ExitedAt == null); }
    }

    public int LiveProcessCount
    {
        get { lock (Processes) return Processes.Count(p => p.ExitedAt == null); }
    }

    public TimeSpan? Duration => ExitedAt.HasValue
        ? ExitedAt.Value - StartedAt
        : DateTime.Now - StartedAt;

    public string DurationText
    {
        get
        {
            var d = Duration ?? TimeSpan.Zero;
            return $"{d.Hours:D2}:{d.Minutes:D2}:{d.Seconds:D2}";
        }
    }
}

public sealed class LaunchResult
{
    public bool Success { get; init; }
    public int? ProcessId { get; init; }
    public string Arguments { get; init; } = "";
    public string? Error { get; init; }
    public LaunchSession? Session { get; init; }
}
