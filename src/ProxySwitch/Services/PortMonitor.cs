using System.Diagnostics;
using System.Net.Sockets;
using ProxySwitch.Models;

namespace ProxySwitch.Services;

public class PortMonitor : IDisposable
{
    private readonly System.Windows.Forms.Timer _timer;
    private readonly Dictionary<string, ProxyRuntimeStatus> _status = new();
    private readonly List<(string Host, int Port, string Id)> _targets = new();

    public event Action? StatusChanged;

    public PortMonitor()
    {
        _timer = new System.Windows.Forms.Timer { Interval = 30000 };
        _timer.Tick += async (_, _) => await CheckAllAsync();
    }

    public void AddTarget(string host, int port, string id)
    {
        var key = $"{host}:{port}";
        _targets.Add((host, port, id));
        _status[key] = new ProxyRuntimeStatus { Id = id, Status = "unknown" };
    }

    public void Start()
    {
        _ = CheckAllAsync();
    }

    public void StartPolling() => _timer.Start();
    public void StopPolling() => _timer.Stop();

    public void ManualRefresh()
    {
        _ = CheckAllAsync();
    }

    public ProxyRuntimeStatus? GetStatus(string host, int port)
    {
        var key = $"{host}:{port}";
        return _status.TryGetValue(key, out var s) ? s : null;
    }

    public bool IsOnline(string host, int port)
    {
        var key = $"{host}:{port}";
        return _status.TryGetValue(key, out var s) && s.Status == "online";
    }

    public int? GetLatency(string host, int port)
    {
        var key = $"{host}:{port}";
        return _status.TryGetValue(key, out var s) ? s.LatencyMs : null;
    }

    private async Task CheckAllAsync()
    {
        bool anyChanged = false;
        foreach (var (host, port, id) in _targets)
        {
            var key = $"{host}:{port}";
            var wasOnline = _status.TryGetValue(key, out var prev) && prev.Status == "online";
            var (nowOnline, latencyMs) = await CheckAsync(host, port);

            var status = _status[key];
            status.Id = id;
            status.LastCheckedAt = DateTime.Now;
            status.LatencyMs = latencyMs;

            var newStatus = nowOnline ? "online" : "offline";
            if (status.Status != newStatus)
            {
                status.Status = newStatus;
                status.LastChangedAt = DateTime.Now;
                anyChanged = true;
            }
        }
        if (anyChanged) StatusChanged?.Invoke();
    }

    private static async Task<(bool, int?)> CheckAsync(string host, int port)
    {
        var sw = Stopwatch.StartNew();
        try
        {
            using var client = new TcpClient();
            using var cts = new CancellationTokenSource(1000);
            await client.ConnectAsync(host, port, cts.Token);
            sw.Stop();
            return (client.Connected, (int)sw.ElapsedMilliseconds);
        }
        catch
        {
            sw.Stop();
            return (false, null);
        }
    }

    public void Dispose()
    {
        _timer.Stop();
        _timer.Dispose();
    }
}
