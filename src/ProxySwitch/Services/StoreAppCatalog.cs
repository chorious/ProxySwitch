using System.Diagnostics;
using System.Text.Json;

namespace ProxySwitch.Services;

public sealed class StoreAppCatalogEntry
{
    public string AppId { get; init; } = "";
    public string DisplayName { get; init; } = "";
    public string? PackageFamilyName { get; init; }
    public string? InstallLocation { get; init; }
    public string? PackageRelativeExePath { get; init; }
}

public sealed class StoreAppCatalog
{
    public IReadOnlyList<StoreAppCatalogEntry> GetStartApps()
    {
        var entries = new List<StoreAppCatalogEntry>();
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = "powershell.exe",
                Arguments = "-NoProfile -Command \"Get-StartApps | Select-Object Name,AppID | ConvertTo-Json -Compress\"",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };
            using var proc = Process.Start(psi);
            if (proc == null) return entries;
            var stdoutTask = Task.Run(() => proc.StandardOutput.ReadToEnd());
            var stderrTask = Task.Run(() => proc.StandardError.ReadToEnd());
            var exited = proc.WaitForExit(15000);
            if (!exited)
            {
                try { proc.Kill(); } catch { }
                Logger.Error("StoreAppCatalog.GetStartApps: PowerShell timed out after 15s");
                return entries;
            }
            if (!stdoutTask.Wait(TimeSpan.FromSeconds(2))) return entries;
            if (proc.ExitCode != 0) return entries;

            var json = stdoutTask.Result;
            if (string.IsNullOrWhiteSpace(json)) return entries;

            // PowerShell may return a single object or an array.
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.ValueKind == JsonValueKind.Array)
            {
                foreach (var el in doc.RootElement.EnumerateArray())
                {
                    var appId = el.GetProperty("AppID").GetString() ?? "";
                    if (string.IsNullOrEmpty(appId)) continue;
                    entries.Add(new StoreAppCatalogEntry
                    {
                        AppId = appId,
                        DisplayName = el.GetProperty("Name").GetString() ?? appId
                    });
                }
            }
            else if (doc.RootElement.ValueKind == JsonValueKind.Object)
            {
                var appId = doc.RootElement.GetProperty("AppID").GetString() ?? "";
                if (!string.IsNullOrEmpty(appId))
                {
                    entries.Add(new StoreAppCatalogEntry
                    {
                        AppId = appId,
                        DisplayName = doc.RootElement.GetProperty("Name").GetString() ?? appId
                    });
                }
            }
        }
        catch (Exception ex)
        {
            Logger.Error($"StoreAppCatalog.GetStartApps failed: {ex.Message}");
        }
        return entries;
    }

    public StoreAppCatalogEntry? ResolvePackageDetails(string appId)
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = "powershell.exe",
                Arguments = $"-NoProfile -Command \"Get-AppxPackage | Where-Object {{ $_.PackageFamilyName -like '*{appId.Split('!')[0]}*' }} | Select-Object Name,PackageFamilyName,InstallLocation | ConvertTo-Json -Compress\"",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };
            using var proc = Process.Start(psi);
            if (proc == null) return null;
            var stdoutTask = Task.Run(() => proc.StandardOutput.ReadToEnd());
            var stderrTask = Task.Run(() => proc.StandardError.ReadToEnd());
            var exited = proc.WaitForExit(15000);
            if (!exited)
            {
                try { proc.Kill(); } catch { }
                Logger.Error($"StoreAppCatalog.ResolvePackageDetails: PowerShell timed out after 15s for {appId}");
                return null;
            }
            if (!stdoutTask.Wait(TimeSpan.FromSeconds(2))) return null;
            if (proc.ExitCode != 0) return null;

            var json = stdoutTask.Result;
            if (string.IsNullOrWhiteSpace(json)) return null;

            using var doc = JsonDocument.Parse(json);
            JsonElement el;
            if (doc.RootElement.ValueKind == JsonValueKind.Array)
            {
                el = doc.RootElement.EnumerateArray().FirstOrDefault();
                if (el.ValueKind == JsonValueKind.Undefined) return null;
            }
            else
            {
                el = doc.RootElement;
            }

            var installLocation = el.TryGetProperty("InstallLocation", out var loc) ? loc.GetString() : null;
            string? packageRelativeExePath = null;
            if (!string.IsNullOrEmpty(installLocation) && Directory.Exists(installLocation))
            {
                try
                {
                    var exeFiles = Directory.GetFiles(installLocation, "*.exe", SearchOption.AllDirectories);
                    if (exeFiles.Length > 0)
                    {
                        var firstExe = exeFiles[0];
                        packageRelativeExePath = firstExe.Substring(installLocation.Length).TrimStart(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                    }
                }
                catch { }
            }

            return new StoreAppCatalogEntry
            {
                AppId = appId,
                DisplayName = el.GetProperty("Name").GetString() ?? appId,
                PackageFamilyName = el.TryGetProperty("PackageFamilyName", out var pfn) ? pfn.GetString() : null,
                InstallLocation = installLocation,
                PackageRelativeExePath = packageRelativeExePath
            };
        }
        catch (Exception ex)
        {
            Logger.Error($"StoreAppCatalog.ResolvePackageDetails failed for {appId}: {ex.Message}");
            return null;
        }
    }
}
