using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using ProxySwitch.Models;

namespace ProxySwitch.Services;

/// <summary>
/// Scores candidate processes that may be handoff endpoints of a launcher
/// when the parent-child chain does NOT reach the launcher.
/// </summary>
public class HandoffScorer
{
    private static readonly HashSet<string> SystemBrokers = new(StringComparer.OrdinalIgnoreCase)
    {
        "explorer.exe", "svchost.exe", "RuntimeBroker.exe",
        "ApplicationFrameHost.exe", "ShellExperienceHost.exe",
        "SearchHost.exe", "StartMenuExperienceHost.exe",
        "SearchApp.exe", "TextInputHost.exe",
        "conhost.exe", "cmd.exe", "powershell.exe", "pwsh.exe",
        "msiexec.exe", "WerFault.exe", "consent.exe",
        "dllhost.exe", "rundll32.exe", "fontdrvhost.exe",
        "sihost.exe", "ctfmon.exe", "taskhostw.exe"
    };

    public List<HandoffCandidate> Score(
        string launcherExePath,
        DateTime launcherStartedAt,
        DateTime? rootExitedAt,
        IReadOnlyList<ProcessSnapshot> newProcesses)
    {
        var launcherName = Path.GetFileName(launcherExePath);
        var launcherDir = Path.GetDirectoryName(launcherExePath) ?? "";
        var launcherVendorRoot = GuessVendorRoot(launcherExePath);

        var candidates = new List<HandoffCandidate>();
        foreach (var proc in newProcesses)
        {
            int score = 0;
            var reasons = new List<string>();

            var procName = proc.Name;
            var procPath = proc.ExecutablePath ?? "";
            var procDir = string.IsNullOrEmpty(procPath) ? "" : (Path.GetDirectoryName(procPath) ?? "");

            // --- Negative signals first ---
            if (SystemBrokers.Contains(procName))
            {
                score -= 40;
                reasons.Add("system broker (-40)");
            }

            // --- Strong signals ---
            if (!string.IsNullOrEmpty(launcherDir) && !string.IsNullOrEmpty(procDir) &&
                string.Equals(procDir, launcherDir, StringComparison.OrdinalIgnoreCase))
            {
                score += 40;
                reasons.Add("same directory (+40)");
            }
            else if (!string.IsNullOrEmpty(launcherVendorRoot) && !string.IsNullOrEmpty(procDir) &&
                     procDir.StartsWith(launcherVendorRoot, StringComparison.OrdinalIgnoreCase))
            {
                score += 30;
                reasons.Add("same vendor root (+30)");
            }

            if (!string.IsNullOrEmpty(proc.CommandLine))
            {
                if (proc.CommandLine.Contains(launcherExePath, StringComparison.OrdinalIgnoreCase) ||
                    proc.CommandLine.Contains(launcherName, StringComparison.OrdinalIgnoreCase))
                {
                    score += 30;
                    reasons.Add("cmdline references launcher (+30)");
                }
            }

            // Time signals
            if (proc.CreatedAt.HasValue)
            {
                var dt = (proc.CreatedAt.Value - launcherStartedAt).TotalSeconds;
                if (dt >= -1 && dt <= 5)
                {
                    score += 25;
                    reasons.Add($"created {dt:F1}s after launch (+25)");
                }
                else if (dt > 15)
                {
                    score -= 20;
                    reasons.Add($"created {dt:F1}s after launch, too late (-20)");
                }
            }

            if (rootExitedAt.HasValue && proc.CreatedAt.HasValue)
            {
                var rootExitAfterCandidate = (rootExitedAt.Value - proc.CreatedAt.Value).TotalSeconds;
                if (rootExitAfterCandidate >= 0 && rootExitAfterCandidate <= 3)
                {
                    score += 20;
                    reasons.Add("launcher exited within 3s after candidate (+20)");
                }
            }

            // Metadata
            string? procCompany = null;
            string? launcherCompany = null;
            try
            {
                if (File.Exists(procPath)) procCompany = FileVersionInfo.GetVersionInfo(procPath).CompanyName;
                if (File.Exists(launcherExePath)) launcherCompany = FileVersionInfo.GetVersionInfo(launcherExePath).CompanyName;
            }
            catch { }

            if (!string.IsNullOrEmpty(procCompany) && !string.IsNullOrEmpty(launcherCompany) &&
                string.Equals(procCompany, launcherCompany, StringComparison.OrdinalIgnoreCase))
            {
                score += 15;
                reasons.Add($"same company '{procCompany}' (+15)");
            }

            // Name similarity (rough)
            if (LooksSimilar(launcherName, procName))
            {
                score += 10;
                reasons.Add("similar process name (+10)");
            }

            // Visible main window signal
            if (HasVisibleWindowWithTitle(proc.ProcessId))
            {
                score += 15;
                reasons.Add("visible main window (+15)");
            }

            // Confidence buckets
            string confidence;
            if (score >= 70) confidence = "high";
            else if (score >= 40) confidence = "medium";
            else confidence = "low";

            candidates.Add(new HandoffCandidate
            {
                Process = proc,
                Score = score,
                Confidence = confidence,
                Reasons = reasons
            });
        }

        // Sort by score descending
        candidates.Sort((a, b) => b.Score.CompareTo(a.Score));
        return candidates;
    }

    private static string GuessVendorRoot(string exePath)
    {
        // E.g. C:\Program Files\Vendor\Product\bin\app.exe → C:\Program Files\Vendor
        try
        {
            var dir = Path.GetDirectoryName(exePath);
            if (dir == null) return "";
            var parts = dir.Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries);
            // Drop drive letter, "Program Files", etc. and take vendor dir if present
            // Heuristic: if path contains "Program Files" or "Program Files (x86)", vendor = next segment
            for (int i = 0; i < parts.Length - 1; i++)
            {
                if (parts[i].StartsWith("Program Files", StringComparison.OrdinalIgnoreCase))
                {
                    var vendor = string.Join(Path.DirectorySeparatorChar, parts.Take(i + 2));
                    if (Path.IsPathRooted(vendor)) return vendor;
                    return parts[0] + Path.DirectorySeparatorChar + vendor;
                }
            }
            // Fallback: parent of exe directory
            return Path.GetDirectoryName(dir) ?? dir;
        }
        catch
        {
            return "";
        }
    }

    private static bool LooksSimilar(string a, string b)
    {
        if (string.IsNullOrEmpty(a) || string.IsNullOrEmpty(b)) return false;
        var aBase = Path.GetFileNameWithoutExtension(a).ToLowerInvariant();
        var bBase = Path.GetFileNameWithoutExtension(b).ToLowerInvariant();
        if (aBase.Length < 3 || bBase.Length < 3) return false;
        // Substring match either direction
        return aBase.Contains(bBase) || bBase.Contains(aBase);
    }

    // -- Visible window detection (P/Invoke) --

    [DllImport("user32.dll")]
    private static extern bool EnumWindows(EnumWindowsProc lpEnumFunc, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern bool IsWindowVisible(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern int GetWindowTextLength(IntPtr hWnd);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);

    private delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

    private static bool HasVisibleWindowWithTitle(int pid)
    {
        bool found = false;
        try
        {
            EnumWindows((hWnd, _) =>
            {
                GetWindowThreadProcessId(hWnd, out var procId);
                if (procId != (uint)pid) return true;
                if (!IsWindowVisible(hWnd)) return true;
                if (GetWindowTextLength(hWnd) <= 0) return true;
                found = true;
                return false; // stop enumeration
            }, IntPtr.Zero);
        }
        catch { }
        return found;
    }
}
