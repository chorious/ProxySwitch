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
    public int? MainProcessId { get; set; }
    public List<int> ProcessIds { get; } = [];
    public string Status { get; set; } = "starting"; // starting, running, exited, failed
    public string? LastError { get; set; }

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
