using System.Diagnostics;
using System.Text.Json;
using ProxySwitch.Models;

namespace ProxySwitch.Services;

public class AppIdentityResolver
{
    /// <summary>
    /// Ensure process names include .exe suffix to match WMI Win32_Process.Name.
    /// Current config stores bare names like "Claude"; WMI reports "Claude.exe".
    /// </summary>
    public static string NormalizeProcessName(string name)
    {
        if (string.IsNullOrEmpty(name)) return "";
        var trimmed = name.Trim();
        if (trimmed.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) return trimmed;
        return trimmed + ".exe";
    }

    /// <summary>
    /// Resolve an MSIX package to its current InstallLocation, then combine with
    /// the relative exe path. Returns null if the package is not found or the
    /// resolved file does not exist.
    /// Uses powershell.exe (no new NuGet dependencies).
    /// </summary>
    private static string? ResolveMsixPath(string packageFamilyName, string relativeExePath)
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = "powershell.exe",
                Arguments = $"-NoProfile -Command \"& {{ Get-AppxPackage | Where-Object {{ $_.PackageFamilyName -eq '{packageFamilyName}' }} | Select-Object -Property InstallLocation | ConvertTo-Json -Compress }}\"",
                RedirectStandardOutput = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };
            using var proc = Process.Start(psi);
            if (proc == null) return null;
            var readTask = Task.Run(() => proc.StandardOutput.ReadToEnd());
            var exited = proc.WaitForExit(5000);
            if (!exited) { try { proc.Kill(); } catch { } return null; }
            if (!readTask.Wait(TimeSpan.FromSeconds(2))) return null;
            var json = readTask.Result;
            if (proc.ExitCode != 0) return null;

            using var doc = JsonDocument.Parse(json);
            string? installLocation = null;
            if (doc.RootElement.ValueKind == JsonValueKind.Array)
            {
                var first = doc.RootElement.EnumerateArray().FirstOrDefault();
                if (first.ValueKind == JsonValueKind.Object)
                    installLocation = first.GetProperty("InstallLocation").GetString();
            }
            else if (doc.RootElement.ValueKind == JsonValueKind.Object)
            {
                installLocation = doc.RootElement.GetProperty("InstallLocation").GetString();
            }

            if (string.IsNullOrEmpty(installLocation)) return null;
            var fullPath = Path.Combine(installLocation, relativeExePath);
            return File.Exists(fullPath) ? fullPath : null;
        }
        catch { return null; }
    }

    /// <summary>
    /// Given a dropped executable path, detect if it lives under WindowsApps and
    /// try to resolve its PackageFamilyName and relative exe path.
    /// Returns true if MSIX identity was resolved.
    /// </summary>
    public static bool TryResolveMsixIdentity(string exePath, out string packageFamilyName, out string relativeExePath)
    {
        packageFamilyName = "";
        relativeExePath = "";
        if (string.IsNullOrEmpty(exePath)) return false;

        var windowsApps = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "WindowsApps");
        var fullWindowsApps = Path.GetFullPath(windowsApps);
        var fullExe = Path.GetFullPath(exePath);
        if (!fullExe.StartsWith(fullWindowsApps, StringComparison.OrdinalIgnoreCase)) return false;

        try
        {
            // Walk up from exe to find the package root (the folder under WindowsApps)
            var relativeToWindowsApps = fullExe.Substring(fullWindowsApps.Length).TrimStart(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            var firstSep = relativeToWindowsApps.IndexOfAny(new[] { Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar });
            if (firstSep <= 0) return false;
            var packageFolderName = relativeToWindowsApps.Substring(0, firstSep);

            // Query by package name (not PackageFamilyName) — the folder name is usually the package name + suffix
            // We use a broader query and filter by InstallLocation containing the folder name
            var psi = new ProcessStartInfo
            {
                FileName = "powershell.exe",
                Arguments = $"-NoProfile -Command \"& {{ Get-AppxPackage | Where-Object {{ $_.InstallLocation -like '*{packageFolderName}*' }} | Select-Object -Property PackageFamilyName,InstallLocation | ConvertTo-Json -Compress }}\"",
                RedirectStandardOutput = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };
            using var proc = Process.Start(psi);
            if (proc == null) return false;
            var readTask = Task.Run(() => proc.StandardOutput.ReadToEnd());
            var exited = proc.WaitForExit(5000);
            if (!exited) { try { proc.Kill(); } catch { } return false; }
            if (!readTask.Wait(TimeSpan.FromSeconds(2))) return false;
            var json = readTask.Result;
            if (proc.ExitCode != 0) return false;

            using var doc = JsonDocument.Parse(json);
            JsonElement target = default;
            if (doc.RootElement.ValueKind == JsonValueKind.Array)
            {
                target = doc.RootElement.EnumerateArray().FirstOrDefault();
            }
            else if (doc.RootElement.ValueKind == JsonValueKind.Object)
            {
                target = doc.RootElement;
            }

            if (target.ValueKind != JsonValueKind.Object) return false;
            var pfn = target.GetProperty("PackageFamilyName").GetString();
            var installLoc = target.GetProperty("InstallLocation").GetString();
            if (string.IsNullOrEmpty(pfn) || string.IsNullOrEmpty(installLoc)) return false;

            // Compute relative path from InstallLocation to exe
            var installLocFull = Path.GetFullPath(installLoc);
            if (!fullExe.StartsWith(installLocFull, StringComparison.OrdinalIgnoreCase)) return false;
            var rel = fullExe.Substring(installLocFull.Length).TrimStart(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

            packageFamilyName = pfn;
            relativeExePath = rel;
            return true;
        }
        catch { return false; }
    }

    /// <summary>
    /// Return the best backend match string for a given route.
    /// For msix-package, resolves the current install location; falls back to process name.
    /// For path, validates the file still exists; falls back to process name.
    /// </summary>
    public string ResolveBackendAppName(AppRoute route)
    {
        switch (route.MatchKind)
        {
            case "msix-package":
                var resolved = ResolveMsixPath(route.PackageFamilyName, route.PackageRelativeExePath);
                if (!string.IsNullOrEmpty(resolved))
                {
                    route.ResolvedExePath = resolved;
                    route.ResolvedAt = DateTime.Now;
                    return resolved;
                }
                return NormalizeProcessName(route.ProcessName);

            case "process-name":
                return NormalizeProcessName(route.ProcessName);

            case "path":
            default:
                if (!string.IsNullOrEmpty(route.ExePath) && File.Exists(route.ExePath))
                    return route.ExePath;
                return NormalizeProcessName(route.ProcessName);
        }
    }

    /// <summary>
    /// Generate a stable route key for session deduplication.
    /// Uses the route's declared identity, not its currently-resolved path.
    /// </summary>
    public string GetStableRouteKey(AppRoute route)
    {
        var kind = route.MatchKind;
        var proxyId = route.ProxyId;
        var identity = kind switch
        {
            "msix-package" => $"{route.PackageFamilyName}/{route.PackageRelativeExePath}",
            "process-name" => NormalizeProcessName(route.ProcessName),
            _ => route.ExePath
        };
        return $"{kind}:{proxyId}:{identity}";
    }

    /// <summary>
    /// One-time migration: for any persistent route whose MatchKind is "path" and
    /// whose ExePath lives under WindowsApps, attempt to resolve the MSIX package
    /// identity and convert the route to "msix-package". Mutates routes in-place.
    /// </summary>
    public static int MigrateWindowsAppsRoutes(List<AppRoute> routes, EventStore events)
    {
        int migrated = 0;
        var windowsApps = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "WindowsApps");
        var fullWindowsApps = Path.GetFullPath(windowsApps);

        foreach (var route in routes)
        {
            if (route.MatchKind != "path") continue;
            if (string.IsNullOrEmpty(route.ExePath)) continue;
            var fullExe = Path.GetFullPath(route.ExePath);
            if (!fullExe.StartsWith(fullWindowsApps, StringComparison.OrdinalIgnoreCase)) continue;

            if (TryResolveMsixIdentity(route.ExePath, out var pfn, out var relExe))
            {
                route.MatchKind = "msix-package";
                route.PackageFamilyName = pfn;
                route.PackageRelativeExePath = relExe;
                route.ResolvedExePath = fullExe;
                route.ResolvedAt = DateTime.Now;
                events.Add("AppRouteMigrated", $"{route.Name}: path -> msix-package ({pfn})");
                migrated++;
            }
        }
        return migrated;
    }
}
