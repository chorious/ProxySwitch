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
    /// Uses two-stage WMI query: first cheap (ProcessId, ParentProcessId, CreationDate)
    /// to find candidate descendants, then per-candidate full field query.
    /// PID reuse guard: descendant's CreatedAt must be ≥ rootCreatedAt (with 1s tolerance).
    /// Callbacks fire on the calling synchronization context (UI thread).
    /// </summary>
    public async Task TrackProcessTreeAsync(
        int rootPid,
        DateTime rootCreatedAt,
        int durationSeconds,
        Action<TrackedProcess> onDescendant,
        CancellationToken ct = default)
    {
        var knownPids = new HashSet<int> { rootPid };
        var startTime = DateTime.Now;
        var syncContext = SynchronizationContext.Current;
        var tolerance = rootCreatedAt.AddSeconds(-1); // 1s tolerance for clock drift

        void Post(Action a)
        {
            if (syncContext != null)
                syncContext.Post(_ => { try { a(); } catch (Exception ex) { Logger.Error($"Descendant callback failed: {ex.Message}"); } }, null);
            else
            {
                try { a(); } catch (Exception ex) { Logger.Error($"Descendant callback failed: {ex.Message}"); }
            }
        }

        while (DateTime.Now - startTime < TimeSpan.FromSeconds(durationSeconds) && !ct.IsCancellationRequested)
        {
            try
            {
                var lightTable = await Task.Run(() => GetLightProcessTable(), ct);

                // Build parent map for ancestry walk
                var parentMap = lightTable.ToDictionary(p => p.ProcessId, p => p.ParentProcessId);
                var createdAtMap = lightTable.ToDictionary(p => p.ProcessId, p => p.CreatedAt);

                // Find new descendants
                var newCandidates = new List<int>();
                foreach (var proc in lightTable)
                {
                    if (knownPids.Contains(proc.ProcessId)) continue;
                    // PID reuse guard
                    if (proc.CreatedAt.HasValue && proc.CreatedAt.Value < tolerance) continue;
                    if (IsDescendantOf(proc.ProcessId, rootPid, parentMap, createdAtMap, tolerance))
                    {
                        newCandidates.Add(proc.ProcessId);
                    }
                }

                if (newCandidates.Count > 0)
                {
                    // Stage 2: fetch full info only for candidates
                    var fullInfo = await Task.Run(() => GetFullInfoFor(newCandidates), ct);
                    foreach (var proc in fullInfo)
                    {
                        knownPids.Add(proc.ProcessId);
                        Post(() => onDescendant(proc));
                    }
                }
            }
            catch (OperationCanceledException) { break; }
            catch (Exception ex) { Logger.Error($"TrackProcessTreeAsync iteration failed: {ex.Message}"); }

            try { await Task.Delay(500, ct); }
            catch (OperationCanceledException) { break; }
        }
    }

    internal static bool IsDescendantOf(
        int pid, int ancestorPid,
        Dictionary<int, int?> parentMap,
        Dictionary<int, DateTime?> createdAtMap,
        DateTime ancestorTolerance)
    {
        var visited = new HashSet<int> { pid };
        int current = pid;
        while (parentMap.TryGetValue(current, out var parent) && parent.HasValue)
        {
            if (parent.Value == ancestorPid) return true;
            if (!visited.Add(parent.Value)) break; // cycle guard
            // PID reuse: if parent process was created AFTER current child, parent map is stale
            if (createdAtMap.TryGetValue(parent.Value, out var parentCreated) &&
                parentCreated.HasValue &&
                createdAtMap.TryGetValue(current, out var currentCreated) &&
                currentCreated.HasValue &&
                parentCreated.Value > currentCreated.Value)
            {
                return false; // stale parent reference
            }
            current = parent.Value;
        }
        return false;
    }

    /// <summary>
    /// Walk the parent chain of <paramref name="pid"/> up to <paramref name="maxDepth"/>
    /// levels using WMI. Returns true if any ancestor PID is in <paramref name="ancestorPids"/>.
    /// </summary>
    internal static bool IsAncestorOfAny(int pid, HashSet<int> ancestorPids, int maxDepth = 5)
    {
        try
        {
            var parentMap = new Dictionary<int, int?>();
            using var searcher = new ManagementObjectSearcher("SELECT ProcessId, ParentProcessId FROM Win32_Process");
            foreach (ManagementObject obj in searcher.Get())
            {
                parentMap[Convert.ToInt32(obj["ProcessId"])] = obj["ParentProcessId"] != null ? Convert.ToInt32(obj["ParentProcessId"]) : null;
            }

            var visited = new HashSet<int> { pid };
            int current = pid;
            int depth = 0;
            while (parentMap.TryGetValue(current, out var parent) && parent.HasValue && depth < maxDepth)
            {
                if (ancestorPids.Contains(parent.Value)) return true;
                if (!visited.Add(parent.Value)) break; // cycle guard
                current = parent.Value;
                depth++;
            }
            return false;
        }
        catch { return false; }
    }

    private static List<TrackedProcess> GetLightProcessTable()
    {
        var result = new List<TrackedProcess>();
        try
        {
            using var searcher = new ManagementObjectSearcher(
                "SELECT ProcessId, ParentProcessId, CreationDate FROM Win32_Process");
            foreach (ManagementObject obj in searcher.Get())
            {
                result.Add(new TrackedProcess
                {
                    ProcessId = Convert.ToInt32(obj["ProcessId"]),
                    ParentProcessId = obj["ParentProcessId"] != null ? Convert.ToInt32(obj["ParentProcessId"]) : null,
                    CreatedAt = ParseWmiDate(obj["CreationDate"]?.ToString())
                });
            }
        }
        catch { }
        return result;
    }

    private static List<TrackedProcess> GetFullInfoFor(List<int> pids)
    {
        var result = new List<TrackedProcess>();
        if (pids.Count == 0) return result;

        try
        {
            // Build WHERE clause with PID list
            var pidClause = string.Join(" OR ", pids.Select(p => $"ProcessId = {p}"));
            using var searcher = new ManagementObjectSearcher(
                $"SELECT ProcessId, ParentProcessId, Name, ExecutablePath, CommandLine, CreationDate FROM Win32_Process WHERE {pidClause}");
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
        catch (Exception ex) { Logger.Error($"GetFullInfoFor failed: {ex.Message}"); }
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

    public static DateTime? GetProcessStartTime(int pid)
    {
        try
        {
            var p = System.Diagnostics.Process.GetProcessById(pid);
            return p.StartTime;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Capture a snapshot of all currently running processes with full metadata.
    /// Used as baseline before launch and as candidate pool after launch window.
    /// </summary>
    public async Task<List<ProcessSnapshot>> CaptureProcessSnapshotsAsync(CancellationToken ct = default)
    {
        return await Task.Run(() => GetFullProcessSnapshots(), ct);
    }

    /// <summary>
    /// Synchronous variant — safe to call from the UI thread without deadlock.
    /// WMI query blocks for ~200-500ms inline. Used by LaunchGeneric where
    /// the baseline MUST be captured before Process.Start.
    /// </summary>
    public List<ProcessSnapshot> CaptureProcessSnapshots() => GetFullProcessSnapshots();

    private static List<ProcessSnapshot> GetFullProcessSnapshots()
    {
        var result = new List<ProcessSnapshot>();
        try
        {
            using var searcher = new ManagementObjectSearcher(
                "SELECT ProcessId, ParentProcessId, Name, ExecutablePath, CommandLine, CreationDate, SessionId FROM Win32_Process");
            foreach (ManagementObject obj in searcher.Get())
            {
                result.Add(new ProcessSnapshot
                {
                    ProcessId = Convert.ToInt32(obj["ProcessId"]),
                    ParentProcessId = obj["ParentProcessId"] != null ? Convert.ToInt32(obj["ParentProcessId"]) : null,
                    Name = obj["Name"]?.ToString() ?? "",
                    ExecutablePath = obj["ExecutablePath"]?.ToString(),
                    CommandLine = obj["CommandLine"]?.ToString(),
                    CreatedAt = ParseWmiDate(obj["CreationDate"]?.ToString()),
                    SessionId = obj["SessionId"] != null ? Convert.ToInt32(obj["SessionId"]) : null
                });
            }
        }
        catch (Exception ex) { Logger.Error($"GetFullProcessSnapshots failed: {ex.Message}"); }
        return result;
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
            try { cb(); } catch (Exception ex) { Logger.Error($"OnExited callback failed: {ex.Message}"); }
        }

        // Refresh browser process groups — WMI is slow, run on background thread
        if (_browserTrackers.Count > 0)
        {
            _ = Task.Run(() =>
            {
                try
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
                            try { callback(pids); } catch (Exception ex) { Logger.Error($"Browser callback failed: {ex.Message}"); }
                        }
                    }
                }
                catch (Exception ex) { Logger.Error($"Browser tracker failed: {ex.Message}"); }
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
