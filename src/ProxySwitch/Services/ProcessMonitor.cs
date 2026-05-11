using System.Management;
using ProxySwitch.Models;

namespace ProxySwitch.Services;

public class ProcessMonitor : IDisposable
{
    private readonly System.Windows.Forms.Timer _timer;
    private readonly Dictionary<int, Action> _tracked = new();
    private readonly Dictionary<string, Action<List<int>>> _browserTrackers = new();

    public ProcessMonitor()
    {
        _timer = new System.Windows.Forms.Timer { Interval = 2000 };
        _timer.Tick += OnTick;
    }

    public void TrackPid(int pid, Action onExited)
    {
        lock (_tracked)
        {
            _tracked[pid] = onExited;
        }
        if (!_timer.Enabled) _timer.Start();
    }

    public void TrackBrowser(string userDataDir, Action<List<int>> onChanged)
    {
        lock (_browserTrackers)
        {
            _browserTrackers[userDataDir] = onChanged;
        }
        if (!_timer.Enabled) _timer.Start();
    }

    public void StopTrackingBrowser(string userDataDir)
    {
        lock (_browserTrackers)
        {
            _browserTrackers.Remove(userDataDir);
        }
    }

    public void UntrackPid(int pid)
    {
        lock (_tracked)
        {
            _tracked.Remove(pid);
        }
    }

    public static bool IsAlive(int pid)
    {
        try
        {
            var p = System.Diagnostics.Process.GetProcessById(pid);
            return !p.HasExited;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Track a process tree for <paramref name="durationSeconds"/> after launch.
    /// Callbacks fire on the calling synchronization context (UI thread).
    /// </summary>
    public async Task TrackProcessTreeAsync(
        int rootPid,
        int durationSeconds,
        Action<TrackedProcess> onDescendant,
        CancellationToken ct = default)
    {
        var knownPids = new HashSet<int> { rootPid };
        var startTime = DateTime.Now;
        var syncContext = SynchronizationContext.Current;

        void Post(Action a)
        {
            if (syncContext != null)
                syncContext.Post(_ => a(), null);
            else
                a();
        }

        while (DateTime.Now - startTime < TimeSpan.FromSeconds(durationSeconds) && !ct.IsCancellationRequested)
        {
            var allProcesses = await Task.Run(() => GetProcessTable(), ct);

            // Build parent map for recursive lookup
            var parentMap = allProcesses.ToDictionary(p => p.ProcessId, p => p.ParentProcessId);

            // Find new descendants whose parent chain reaches rootPid
            foreach (var proc in allProcesses)
            {
                if (knownPids.Contains(proc.ProcessId)) continue;
                if (IsDescendantOf(proc.ProcessId, rootPid, parentMap))
                {
                    knownPids.Add(proc.ProcessId);
                    Post(() => onDescendant(proc));
                }
            }

            try { await Task.Delay(500, ct); }
            catch (OperationCanceledException) { break; }
        }
    }

    private static bool IsDescendantOf(int pid, int ancestorPid, Dictionary<int, int?> parentMap)
    {
        var visited = new HashSet<int>();
        int current = pid;
        while (parentMap.TryGetValue(current, out var parent) && parent.HasValue)
        {
            if (parent.Value == ancestorPid) return true;
            if (!visited.Add(parent.Value)) break; // cycle guard
            current = parent.Value;
        }
        return false;
    }

    private static List<TrackedProcess> GetProcessTable()
    {
        var result = new List<TrackedProcess>();
        try
        {
            using var searcher = new ManagementObjectSearcher(
                "SELECT ProcessId, ParentProcessId, Name, ExecutablePath, CommandLine, CreationDate FROM Win32_Process");
            foreach (ManagementObject obj in searcher.Get())
            {
                result.Add(new TrackedProcess
                {
                    ProcessId = Convert.ToInt32(obj["ProcessId"]),
                    ParentProcessId = obj["ParentProcessId"] != null ? Convert.ToInt32(obj["ParentProcessId"]) : null,
                    Name = obj["Name"]?.ToString() ?? "",
                    ExecutablePath = obj["ExecutablePath"]?.ToString(),
                    CommandLine = obj["CommandLine"]?.ToString(),
                    CreatedAt = ParseWmiDate(obj["CreationDate"]?.ToString())
                });
            }
        }
        catch { }
        return result;
    }

    private static DateTime? ParseWmiDate(string? wmiDate)
    {
        if (string.IsNullOrEmpty(wmiDate) || wmiDate.Length < 14) return null;
        if (DateTime.TryParseExact(wmiDate[..14], "yyyyMMddHHmmss",
            System.Globalization.CultureInfo.InvariantCulture,
            System.Globalization.DateTimeStyles.None, out var dt))
            return dt;
        return null;
    }

    private void OnTick(object? sender, EventArgs e)
    {
        // Check tracked PIDs (fast, stays on UI thread)
        List<(int Pid, Action Callback)> exited = new();
        lock (_tracked)
        {
            foreach (var (pid, callback) in _tracked)
            {
                if (!IsAlive(pid))
                    exited.Add((pid, callback));
            }
            foreach (var (pid, _) in exited)
            {
                _tracked.Remove(pid);
            }
        }
        // Fire callbacks outside lock
        foreach (var (_, cb) in exited)
        {
            try { cb(); } catch { }
        }

        // Refresh browser process groups — WMI is slow, run on background thread
        if (_browserTrackers.Count > 0)
        {
            _ = Task.Run(() =>
            {
                var browsers = GetBrowserProcesses();
                lock (_browserTrackers)
                {
                    foreach (var (userDataDir, callback) in _browserTrackers)
                    {
                        var pids = browsers
                            .Where(b => b.CommandLine.Contains(userDataDir, StringComparison.OrdinalIgnoreCase))
                            .Select(b => b.Pid)
                            .ToList();
                        try { callback(pids); } catch { }
                    }
                }
            });
        }

        if (_tracked.Count == 0 && _browserTrackers.Count == 0)
            _timer.Stop();
    }

    private static List<(int Pid, string CommandLine)> GetBrowserProcesses()
    {
        var result = new List<(int, string)>();
        try
        {
            using var searcher = new ManagementObjectSearcher(
                "SELECT ProcessId, CommandLine FROM Win32_Process WHERE Name LIKE 'chrome.exe' OR Name LIKE 'msedge.exe'");
            foreach (ManagementObject obj in searcher.Get())
            {
                int pid = Convert.ToInt32(obj["ProcessId"]);
                string? cmd = obj["CommandLine"]?.ToString() ?? "";
                result.Add((pid, cmd));
            }
        }
        catch { }
        return result;
    }

    public void Dispose()
    {
        _timer.Stop();
        _timer.Dispose();
    }
}
