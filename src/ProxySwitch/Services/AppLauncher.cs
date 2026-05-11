using System.Diagnostics;
using ProxySwitch.Models;

namespace ProxySwitch.Services;

public class AppLauncher
{
    private readonly ProxyConfig _config;

    public AppLauncher(ProxyConfig config)
    {
        _config = config;
    }

    public LaunchResult LaunchBrowser(AppConfig app)
    {
        if (!File.Exists(app.Exe))
        {
            var err = $"Executable not found: {app.Exe}";
            Logger.Error(err);
            return new LaunchResult { Success = false, Error = err };
        }

        Directory.CreateDirectory(app.UserDataDir);
        var args = BuildBrowserArgs(app);

        try
        {
            var proc = Process.Start(new ProcessStartInfo
            {
                FileName = app.Exe,
                Arguments = args,
                UseShellExecute = true,
                WorkingDirectory = Path.GetDirectoryName(app.Exe) ?? string.Empty
            });
            Logger.Info($"Launched {app.Name}: {app.Exe} {args}");
            return new LaunchResult
            {
                Success = true,
                ProcessId = proc?.Id,
                Arguments = args
            };
        }
        catch (Exception ex)
        {
            Logger.Error($"Failed to launch {app.Name}: {ex.Message}");
            return new LaunchResult { Success = false, Error = ex.Message };
        }
    }

    public LaunchResult LaunchGeneric(string exePath)
    {
        if (!File.Exists(exePath))
        {
            var err = $"Executable not found: {exePath}";
            Logger.Error(err);
            return new LaunchResult { Success = false, Error = err };
        }

        try
        {
            var proc = Process.Start(new ProcessStartInfo
            {
                FileName = exePath,
                UseShellExecute = true,
                WorkingDirectory = Path.GetDirectoryName(exePath) ?? string.Empty
            });
            Logger.Info($"Launched generic app: {exePath}");
            return new LaunchResult
            {
                Success = true,
                ProcessId = proc?.Id,
                Arguments = ""
            };
        }
        catch (Exception ex)
        {
            Logger.Error($"Failed to launch {exePath}: {ex.Message}");
            return new LaunchResult { Success = false, Error = ex.Message };
        }
    }

    private string BuildBrowserArgs(AppConfig app)
    {
        var sb = new System.Text.StringBuilder();
        sb.Append($"--user-data-dir=\"{app.UserDataDir}\"");

        if (app.Mode == "browser-direct")
        {
            sb.Append(" --no-proxy-server");
        }
        else if (app.Mode == "browser-proxy" && !string.IsNullOrEmpty(app.ProxyId))
        {
            var proxy = _config.Proxies.FirstOrDefault(p => p.Id == app.ProxyId);
            if (proxy != null)
            {
                var scheme = proxy.Type.ToLowerInvariant() == "http" ? "http" : "socks5";
                sb.Append($" --proxy-server=\"{scheme}://{proxy.Host}:{proxy.Port}\"");
            }
        }

        return sb.ToString();
    }

    public void OpenProxifier()
    {
        if (!_config.Proxifier.Enabled) return;
        if (!File.Exists(_config.Proxifier.Exe))
        {
            Logger.Error($"Proxifier not found: {_config.Proxifier.Exe}");
            MessageBox.Show($"Proxifier not found:\n{_config.Proxifier.Exe}", "Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
            return;
        }

        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = _config.Proxifier.Exe,
                UseShellExecute = true
            });
            Logger.Info("Opened Proxifier");
        }
        catch (Exception ex)
        {
            Logger.Error($"Failed to open Proxifier: {ex.Message}");
        }
    }

    public void LoadProxifierProfile(string profileKey)
    {
        if (!_config.Proxifier.Enabled) return;
        if (!_config.Proxifier.Profiles.TryGetValue(profileKey, out var path))
        {
            Logger.Error($"Profile not found: {profileKey}");
            return;
        }
        if (!File.Exists(path))
        {
            Logger.Error($"Profile file not found: {path}");
            MessageBox.Show($"Profile file not found:\n{path}\n\nCreate it in Proxifier and export to this location.", "Profile Missing", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }

        if (!File.Exists(_config.Proxifier.Exe))
        {
            Logger.Error($"Proxifier not found: {_config.Proxifier.Exe}");
            return;
        }

        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = _config.Proxifier.Exe,
                Arguments = $"\"{path}\"",
                UseShellExecute = true
            });
            Logger.Info($"Loaded Proxifier profile: {profileKey}");
        }
        catch (Exception ex)
        {
            Logger.Error($"Failed to load profile {profileKey}: {ex.Message}");
        }
    }
}
