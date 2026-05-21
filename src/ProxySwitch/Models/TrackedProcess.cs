namespace ProxySwitch.Models;

public sealed class TrackedProcess
{
    public int ProcessId { get; init; }
    public int? ParentProcessId { get; init; }
    public string Name { get; init; } = "";
    public string? ExecutablePath { get; init; }
    public string? CommandLine { get; init; }
    public DateTime? CreatedAt { get; init; }
    public DateTime? ExitedAt { get; set; }
    public string Role { get; set; } = "descendant"; // "root", "descendant", "correlated", "restarted-child"
    public ProcessTrackingConfidence Confidence { get; set; } = ProcessTrackingConfidence.Descendant;
    public int CorrelationScore { get; set; }
    public List<string> CorrelationReasons { get; } = [];
}
