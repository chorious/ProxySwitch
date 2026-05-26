using System.IO.Pipes;
using System.Text;
using System.Text.Json;

namespace ProxySwitch.Services;

/// <summary>
/// Client for the ProxiFyre named-pipe IPC protocol.
/// Allows ProxySwitch to create sessions and add/remove PIDs at runtime
/// without restarting the ProxiFyre service.
/// </summary>
public sealed class ProxiFyreIpcClient : IDisposable
{
    private readonly string _pipeName = @"ProxySwitch.ProxiFyre.SessionRoute";
    private readonly TimeSpan _connectTimeout = TimeSpan.FromSeconds(3);

    /// <summary>
    /// Application-layer shared token. Must match the token expected by
    /// ProxiFyre's SessionRouteServer. Prevents arbitrary interactive-user
    /// processes from sending addPid/removePid to the high-privilege service.
    /// </summary>
    private const string SharedToken = "e2b0c4d6-8f3a-4e5b-9c1d-7a6b5c4d3e2f";

    /// <summary>
    /// Probes whether the ProxiFyre IPC server is reachable.
    /// </summary>
    public bool IsAvailable()
    {
        try
        {
            using var pipe = new NamedPipeClientStream(".", _pipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
            pipe.Connect(500);
            return pipe.IsConnected;
        }
        catch { return false; }
    }

    /// <summary>
    /// Creates a new session on the ProxiFyre side.
    /// </summary>
    public async Task<IpcResult> CreateSessionAsync(string sessionId, string endpoint, CancellationToken ct = default)
    {
        return await SendAsync(new
        {
            action = "createSession",
            sessionId,
            endpoint,
            token = SharedToken
        }, ct);
    }

    /// <summary>
    /// Adds a PID to a session. The creation time MUST be the real process creation time
    /// (from ProcessMonitor.GetProcessStartTime or WMI), not DateTime.UtcNow.
    /// </summary>
    public async Task<IpcResult> AddPidAsync(string sessionId, int pid, DateTime createdAtUtc, CancellationToken ct = default)
    {
        long fileTime = createdAtUtc.ToFileTimeUtc();
        return await SendAsync(new
        {
            action = "addPid",
            sessionId,
            pid,
            createdAtFileTime = fileTime,
            token = SharedToken
        }, ct);
    }

    /// <summary>
    /// Removes a PID from a session.
    /// </summary>
    public async Task<IpcResult> RemovePidAsync(string sessionId, int pid, CancellationToken ct = default)
    {
        return await SendAsync(new
        {
            action = "removePid",
            sessionId,
            pid,
            token = SharedToken
        }, ct);
    }

    /// <summary>
    /// Closes a session and removes all its PIDs from ProxiFyre routing.
    /// </summary>
    public async Task<IpcResult> CloseSessionAsync(string sessionId, CancellationToken ct = default)
    {
        return await SendAsync(new
        {
            action = "closeSession",
            sessionId,
            token = SharedToken
        }, ct);
    }

    private async Task<IpcResult> SendAsync(object message, CancellationToken ct)
    {
        var json = JsonSerializer.Serialize(message);
        try
        {
            await using var pipe = new NamedPipeClientStream(".", _pipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
            await pipe.ConnectAsync(_connectTimeout, ct);

            using var writer = new StreamWriter(pipe, Encoding.UTF8, bufferSize: 4096, leaveOpen: true) { AutoFlush = false };
            using var reader = new StreamReader(pipe, Encoding.UTF8, detectEncodingFromByteOrderMarks: false, bufferSize: 4096, leaveOpen: true);

            await writer.WriteLineAsync(json);
            await writer.FlushAsync();

            var response = await reader.ReadLineAsync(ct);
            if (string.IsNullOrEmpty(response))
                return new IpcResult { Success = false, Error = "empty response" };

            using var doc = JsonDocument.Parse(response);
            var root = doc.RootElement;
            return new IpcResult
            {
                Success = root.GetProperty("success").GetBoolean(),
                Error = root.TryGetProperty("error", out var e) ? e.GetString() : null
            };
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (TimeoutException)
        {
            return new IpcResult { Success = false, Error = "pipe connection timeout" };
        }
        catch (Exception ex)
        {
            return new IpcResult { Success = false, Error = ex.Message };
        }
    }

    public void Dispose() { }
}

public sealed class IpcResult
{
    public bool Success { get; set; }
    public string? Error { get; set; }
}
