using ProxySwitch.Models;

namespace ProxySwitch.Services;

public class AppEvent
{
    public DateTime Timestamp { get; } = DateTime.Now;
    public string Type { get; }
    public string Message { get; }

    public AppEvent(string type, string message)
    {
        Type = type;
        Message = message;
    }

    public override string ToString() => $"[{Timestamp:HH:mm:ss}] [{Type}] {Message}";
}

public class EventStore
{
    private readonly List<AppEvent> _events = new();
    private readonly object _lock = new();
    private const int MaxEvents = 200;

    public event Action? EventAdded;

    public void Add(string type, string message)
    {
        var evt = new AppEvent(type, message);
        lock (_lock)
        {
            _events.Add(evt);
            if (_events.Count > MaxEvents)
                _events.RemoveAt(0);
        }
        Logger.Info(evt.ToString());
        EventAdded?.Invoke();
    }

    public IReadOnlyList<AppEvent> GetRecent(int count = 50)
    {
        lock (_lock)
        {
            return _events.TakeLast(count).ToList().AsReadOnly();
        }
    }
}
