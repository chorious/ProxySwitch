using System.Net.Sockets;

namespace ProxySwitch.Services;

public class PortMonitor : IDisposable
{
    private readonly System.Windows.Forms.Timer _timer;
    private readonly Dictionary<string, bool> _status = new();
    private readonly List<(string Host, int Port)> _targets = new();

    public event Action? StatusChanged;

    public PortMonitor()
    {
        _timer = new System.Windows.Forms.Timer { Interval = 5000 };
        _timer.Tick += async (_, _) => await CheckAllAsync();
    }

    public void AddTarget(string host, int port)
    {
        var key = $"{host}:{port}";
        _targets.Add((host, port));
        _status[key] = false;
    }

    public void Start()
    {
        _ = CheckAllAsync();
    }

    public void StartPolling() => _timer.Start();
    public void StopPolling() => _timer.Stop();

    public bool IsOnline(string host, int port)
    {
        var key = $"{host}:{port}";
        return _status.TryGetValue(key, out var v) && v;
    }

    private async Task CheckAllAsync()
    {
        bool anyChanged = false;
        foreach (var (host, port) in _targets)
        {
            var key = $"{host}:{port}";
            bool was = _status.TryGetValue(key, out var v) && v;
            bool now = await CheckAsync(host, port);
            if (was != now) anyChanged = true;
            _status[key] = now;
        }
        if (anyChanged) StatusChanged?.Invoke();
    }

    private static async Task<bool> CheckAsync(string host, int port)
    {
        try
        {
            using var client = new TcpClient();
            using var cts = new CancellationTokenSource(1000);
            await client.ConnectAsync(host, port, cts.Token);
            return client.Connected;
        }
        catch
        {
            return false;
        }
    }

    public void Dispose()
    {
        _timer.Stop();
        _timer.Dispose();
    }
}
