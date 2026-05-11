namespace ProxySwitch.Models;

public enum ProcessTrackingConfidence
{
    Root,
    Descendant,
    CorrelatedHigh,
    CorrelatedMedium,
    UserSelected
}

public sealed class ProcessSnapshot
{
    public int ProcessId { get; init; }
    public int? ParentProcessId { get; init; }
    public string Name { get; init; } = "";
    public string? ExecutablePath { get; init; }
    public string? CommandLine { get; init; }
    public DateTime? CreatedAt { get; init; }
    public int? SessionId { get; init; }
}

public sealed class HandoffCandidate
{
    public ProcessSnapshot Process { get; init; } = null!;
    public int Score { get; init; }
    public string Confidence { get; init; } = "low"; // "high", "medium", "low"
    public List<string> Reasons { get; init; } = [];
}
