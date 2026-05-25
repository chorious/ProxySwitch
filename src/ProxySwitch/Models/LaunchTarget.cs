namespace ProxySwitch.Models;

public sealed class LaunchTarget
{
    public string LaunchKind { get; init; } = "exe";
    public string DisplayName { get; init; } = "";
    public string ExePath { get; init; } = "";
    public string ProcessName { get; init; } = "";
    public string AppUserModelId { get; init; } = "";
    public string PackageFamilyName { get; init; } = "";
    public string PackageRelativeExePath { get; init; } = "";
    public string ShortcutPath { get; init; } = "";
    public string Arguments { get; init; } = "";
    public string WorkingDirectory { get; init; } = "";
}
