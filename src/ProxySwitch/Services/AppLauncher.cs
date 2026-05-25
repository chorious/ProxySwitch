using System.Diagnostics;
using System.Runtime.InteropServices;
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

    public LaunchResult LaunchTarget(LaunchTarget target)
    {
        switch (target.LaunchKind)
        {
            case "exe":
                return LaunchGeneric(target.ExePath);

            case "shortcut":
                try
                {
                    var psi = new ProcessStartInfo
                    {
                        FileName = target.ShortcutPath,
                        Arguments = target.Arguments,
                        WorkingDirectory = string.IsNullOrEmpty(target.WorkingDirectory)
                            ? (string.IsNullOrEmpty(target.ExePath) ? string.Empty : Path.GetDirectoryName(target.ExePath) ?? string.Empty)
                            : target.WorkingDirectory,
                        UseShellExecute = true
                    };
                    var proc = Process.Start(psi);
                    Logger.Info($"Launched shortcut: {target.ShortcutPath}");
                    return new LaunchResult { Success = true, ProcessId = proc?.Id };
                }
                catch (Exception ex)
                {
                    Logger.Error($"Failed to launch shortcut {target.ShortcutPath}: {ex.Message}");
                    return new LaunchResult { Success = false, Error = ex.Message };
                }

            case "app-user-model-id":
                return LaunchByAumid(target.AppUserModelId);

            default:
                return new LaunchResult { Success = false, Error = $"Unknown launch kind: {target.LaunchKind}" };
        }
    }

    [ComImport]
    [Guid("2e941141-7f97-4756-ba1d-9decde894a3d")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IApplicationActivationManager
    {
        int ActivateApplication(
            [MarshalAs(UnmanagedType.LPWStr)] string appUserModelId,
            [MarshalAs(UnmanagedType.LPWStr)] string arguments,
            ActivateOptions options,
            out uint processId);
    }

    [Flags]
    private enum ActivateOptions
    {
        None = 0,
        DesignMode = 1,
        NoErrorUI = 2,
        NoSplashScreen = 4
    }

    private static LaunchResult LaunchByAumid(string aumid)
    {
        try
        {
            var type = Type.GetTypeFromCLSID(new Guid("45BA127D-10A8-46EA-8AB7-56EA9078943C"));
            if (type == null)
                return new LaunchResult { Success = false, Error = "IApplicationActivationManager not available" };

            var manager = (IApplicationActivationManager?)Activator.CreateInstance(type);
            if (manager == null)
                return new LaunchResult { Success = false, Error = "Failed to create activation manager" };

            uint pid;
            int hr = manager.ActivateApplication(aumid, "", ActivateOptions.None, out pid);
            if (hr < 0)
            {
                var ex = Marshal.GetExceptionForHR(hr);
                return new LaunchResult { Success = false, Error = ex?.Message ?? $"Activation failed (0x{hr:X8})" };
            }

            Logger.Info($"Launched Store app by AUMID: {aumid} (PID={pid})");
            return new LaunchResult { Success = true, ProcessId = (int)pid };
        }
        catch (Exception ex)
        {
            Logger.Error($"Failed to launch Store app {aumid}: {ex.Message}");
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

    /// <summary>
    /// Open the routing backend's app (Clash Verge / v2ray GUI) for the given proxy.
    /// ProxySwitch does NOT modify their config — user does it themselves.
    /// </summary>
    public bool OpenRoutingApp(string proxyId)
    {
        var backend = _config.RoutingBackends.FirstOrDefault(b => b.ProxyId == proxyId);
        if (backend == null || string.IsNullOrEmpty(backend.AppPath))
        {
            MessageBox.Show(
                $"No routing backend app configured for {proxyId}.\n\n" +
                "Open Settings → Routing Backends and set the App Path for this proxy.",
                "Routing Backend Not Configured", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return false;
        }
        if (!File.Exists(backend.AppPath))
        {
            MessageBox.Show(
                $"Routing backend app not found:\n{backend.AppPath}",
                "Not Found", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return false;
        }
        try
        {
            Process.Start(new ProcessStartInfo { FileName = backend.AppPath, UseShellExecute = true });
            Logger.Info($"Opened routing app: {backend.Name} ({backend.AppPath})");
            return true;
        }
        catch (Exception ex)
        {
            Logger.Error($"Failed to open routing app {backend.Name}: {ex.Message}");
            return false;
        }
    }

    /// <summary>
    /// Open the routing backend's config folder / file in Explorer.
    /// </summary>
    public bool OpenRoutingConfig(string proxyId)
    {
        var backend = _config.RoutingBackends.FirstOrDefault(b => b.ProxyId == proxyId);
        if (backend == null || string.IsNullOrEmpty(backend.ConfigPath))
        {
            MessageBox.Show(
                $"No config path configured for {proxyId}.\n\n" +
                "Open Settings → Routing Backends and set the Config Path for this proxy.",
                "Routing Config Not Configured", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return false;
        }

        var expanded = Environment.ExpandEnvironmentVariables(backend.ConfigPath);
        if (!Directory.Exists(expanded) && !File.Exists(expanded))
        {
            MessageBox.Show(
                $"Config path does not exist:\n{expanded}",
                "Not Found", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return false;
        }
        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = "explorer.exe",
                Arguments = $"\"{expanded}\"",
                UseShellExecute = true
            });
            Logger.Info($"Opened routing config: {backend.Name} ({expanded})");
            return true;
        }
        catch (Exception ex)
        {
            Logger.Error($"Failed to open routing config {backend.Name}: {ex.Message}");
            return false;
        }
    }
}
