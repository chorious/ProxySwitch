namespace ProxySwitch.Models;

public sealed class LaunchSession
{
    public string Id { get; init; } = Guid.NewGuid().ToString("N")[..8];
    public string Name { get; init; } = "";
    public string ExePath { get; init; } = "";
    public string Mode { get; init; } = "direct"; // direct, proxy
    public string? ProxyId { get; init; }
    public string? UserDataDir { get; init; }
    public DateTime StartedAt { get; init; } = DateTime.Now;
    public DateTime? ExitedAt { get; set; }
    public int? RootProcessId { get; set; }
    public List<TrackedProcess> Processes { get; } = [];
    public string Status { get; set; } = "starting"; // starting, running, running-via-child, exited, failed
    public string RoutingStatus { get; set; } = "unknown"; // unknown, direct, browser-arg, profile-loaded, assisted, unverified
    public bool IsLauncherHandoffDetected { get; set; }
    public bool IsRoutingAssisted { get; set; }
    public string? RoutingWarning { get; set; }
    public string? LastError { get; set; }

    public int? LiveProcessId
    {
        get
        {
            // Return the most recently added live process, or root if still alive
            var live = Processes.Where(p => p.ExitedAt == null).ToList();
            if (live.Count == 0) return null;
            // Prefer descendants over root (launcher handoff)
            var descendant = live.LastOrDefault(p => p.Role == "descendant");
            return descendant?.ProcessId ?? live.LastOrDefault()?.ProcessId;
        }
    }

    public bool HasLiveProcesses => Processes.Any(p => p.ExitedAt == null);

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
