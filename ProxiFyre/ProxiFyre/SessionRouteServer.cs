using Newtonsoft.Json;
using Socksifier;
using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Pipes;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace ProxiFyre
{
    /// <summary>
    /// Provides a named-pipe IPC server that allows ProxySwitch to manage
    /// PID-based proxy routes at runtime without restarting the ProxiFyre service.
    /// </summary>
    public sealed class SessionRouteServer : IDisposable
    {
        private readonly Socksifier.Socksifier _socksify;
        private readonly NLog.Logger _logger = NLog.LogManager.GetCurrentClassLogger();
        private readonly CancellationTokenSource _cts = new CancellationTokenSource();
        private Task _listenerTask;

        /// <summary>
        /// Application-layer shared token. Must match the token sent by ProxySwitch's
        /// ProxiFyreIpcClient. Prevents arbitrary interactive-user processes from
        /// sending addPid/removePid to the high-privilege service even if the pipe
        /// ACL allows the connection.
        /// </summary>
        private const string ExpectedToken = "e2b0c4d6-8f3a-4e5b-9c1d-7a6b5c4d3e2f";

        /// <summary>
        /// In-memory session registry. Protected by _sessionLock.
        /// </summary>
        private readonly Dictionary<string, SessionState> _sessions = new Dictionary<string, SessionState>();
        private readonly object _sessionLock = new object();

        /// <summary>
        /// Maps endpoint strings (e.g. "127.0.0.1:1080") to proxy handles.
        /// Set once at startup via SetProxyMap.
        /// </summary>
        private Dictionary<string, IntPtr> _proxyMap = new Dictionary<string, IntPtr>();

        private class SessionState
        {
            public string Endpoint { get; set; } = "";
            public IntPtr ProxyHandle { get; set; } = IntPtr.Zero;
            public HashSet<uint> Pids { get; set; } = new HashSet<uint>();
        }

        public SessionRouteServer(Socksifier.Socksifier socksify)
        {
            _socksify = socksify ?? throw new ArgumentNullException(nameof(socksify));
        }

        /// <summary>
        /// Sets the endpoint-to-proxy-handle mapping built during service startup.
        /// </summary>
        public void SetProxyMap(Dictionary<string, IntPtr> map)
        {
            _proxyMap = map ?? new Dictionary<string, IntPtr>();
        }

        /// <summary>
        /// Starts the named-pipe listener on a background thread.
        /// </summary>
        public void Start()
        {
            _listenerTask = Task.Factory.StartNew(
                ListenLoop,
                _cts.Token,
                TaskCreationOptions.LongRunning,
                TaskScheduler.Default);
        }

        /// <summary>
        /// Stops listening and disposes resources.
        /// </summary>
        public void Dispose()
        {
            _cts.Cancel();

            // Unblock the synchronous WaitForConnection() in ListenLoop
            // so the listener thread can observe cancellation promptly.
            try
            {
                using (var dummy = new NamedPipeClientStream(
                    ".", "ProxySwitch.ProxiFyre.SessionRoute",
                    PipeDirection.InOut, PipeOptions.Asynchronous))
                {
                    dummy.Connect(100);
                }
            }
            catch { /* best effort */ }

            try
            {
                // Remove all PID routes before shutting down
                lock (_sessionLock)
                {
                    foreach (var session in _sessions.Values)
                    {
                        foreach (var pid in session.Pids)
                        {
                            try { _socksify.RemoveProcessId(pid); }
                            catch (Exception ex)
                            {
                                _logger.Warn($"Failed to remove PID {pid} during dispose: {ex.Message}");
                            }
                        }
                    }
                    _sessions.Clear();
                }
                _socksify.ClearPidRoutes();
            }
            catch (Exception ex)
            {
                _logger.Error($"Exception during SessionRouteServer dispose: {ex.Message}");
            }

            try
            {
                if (_listenerTask != null && !_listenerTask.IsCompleted)
                    _listenerTask.Wait(TimeSpan.FromSeconds(3));
            }
            catch { /* best effort */ }

            _cts.Dispose();
        }

        private void ListenLoop()
        {
            while (!_cts.IsCancellationRequested)
            {
                try
                {
                    var pipe = new NamedPipeServerStream(
                        "ProxySwitch.ProxiFyre.SessionRoute",
                        PipeDirection.InOut,
                        NamedPipeServerStream.MaxAllowedServerInstances,
                        PipeTransmissionMode.Message,
                        PipeOptions.Asynchronous,
                        4096,
                        4096,
                        CreatePipeSecurity());

                    pipe.WaitForConnection();
                    if (_cts.IsCancellationRequested)
                    {
                        try { pipe.Dispose(); } catch { }
                        break;
                    }

                    var connectedPipe = pipe;
                    _ = Task.Run(() => HandleConnection(connectedPipe));
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                catch (IOException ex)
                {
                    _logger.Warn($"SessionRouteServer pipe IO error: {ex.Message}");
                    Thread.Sleep(100);
                }
                catch (Exception ex)
                {
                    _logger.Error($"SessionRouteServer listener error: {ex.GetType().Name} 0x{ex.HResult:X8}: {ex.Message}");
                    Thread.Sleep(500);
                }
            }
        }

        /// <summary>
        /// Creates a PipeSecurity object restricting access to:
        /// - All interactive users (non-elevated ProxySwitch)
        /// - Built-in Administrators
        /// - LocalSystem
        /// </summary>
        private static PipeSecurity CreatePipeSecurity()
        {
            var ps = new PipeSecurity();

            // Allow all interactive users (covers non-elevated ProxySwitch)
            ps.AddAccessRule(new PipeAccessRule(
                new SecurityIdentifier(WellKnownSidType.InteractiveSid, null),
                PipeAccessRights.ReadWrite,
                AccessControlType.Allow));

            // Allow LocalSystem to create additional pipe instances (required for concurrent listener loop)
            ps.AddAccessRule(new PipeAccessRule(
                new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null),
                PipeAccessRights.ReadWrite | PipeAccessRights.CreateNewInstance,
                AccessControlType.Allow));

            // Allow Built-in Administrators
            ps.AddAccessRule(new PipeAccessRule(
                new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null),
                PipeAccessRights.FullControl,
                AccessControlType.Allow));

            return ps;
        }

        private void HandleConnection(NamedPipeServerStream pipe)
        {
            try
            {
                using (var reader = new StreamReader(pipe, Encoding.UTF8, detectEncodingFromByteOrderMarks: false, bufferSize: 4096, leaveOpen: true))
                using (var writer = new StreamWriter(pipe, Encoding.UTF8, bufferSize: 4096, leaveOpen: true) { AutoFlush = false })
                {
                    // Read one line = one JSON message
                    var json = reader.ReadLine();

                    // If we're shutting down, the dummy unblock connection may arrive
                    // here with an empty read. Stay silent to avoid misleading log noise.
                    if (_cts.IsCancellationRequested)
                        return;

                    if (string.IsNullOrWhiteSpace(json))
                    {
                        writer.WriteLine(@"{""success"":false,""error"":""empty request""}");
                        writer.Flush();
                        return;
                    }

                    var response = ProcessMessage(json);
                    writer.WriteLine(response);
                    writer.Flush();
                    try { pipe.WaitForPipeDrain(); } catch { /* best effort */ }
                }
            }
            catch (IOException ex)
            {
                _logger.Debug($"SessionRouteServer client disconnected: {ex.Message}");
            }
            catch (Exception ex)
            {
                _logger.Error($"SessionRouteServer handle error: {ex.Message}");
            }
            finally
            {
                try { pipe.Dispose(); } catch { /* best effort */ }
            }
        }

        private string ProcessMessage(string json)
        {
            try
            {
                dynamic msg = JsonConvert.DeserializeObject(json);
                if (msg == null)
                    return @"{""success"":false,""error"":""invalid json""}";

                string token = msg.token;
                if (token != ExpectedToken)
                    return @"{""success"":false,""error"":""unauthorized""}";

                string action = msg.action;
                switch (action)
                {
                    case "createSession":
                        return DoCreateSession(msg);
                    case "addPid":
                        return DoAddPid(msg);
                    case "removePid":
                        return DoRemovePid(msg);
                    case "closeSession":
                        return DoCloseSession(msg);
                    default:
                        return @"{""success"":false,""error"":""unknown action""}";
                }
            }
            catch (Exception ex)
            {
                return $"{{\"success\":false,\"error\":\"{EscapeJson(ex.Message)}\"}}";
            }
        }

        private string DoCreateSession(dynamic msg)
        {
            string sessionId = msg.sessionId;
            string endpoint = msg.endpoint;

            if (string.IsNullOrWhiteSpace(sessionId))
                return @"{""success"":false,""error"":""missing sessionId""}";
            if (string.IsNullOrWhiteSpace(endpoint))
                return @"{""success"":false,""error"":""missing endpoint""}";

            if (!_proxyMap.TryGetValue(endpoint, out var handle))
                return @"{""success"":false,""error"":""proxy endpoint not found""}";

            lock (_sessionLock)
            {
                _sessions[sessionId] = new SessionState
                {
                    Endpoint = endpoint,
                    ProxyHandle = handle
                };
            }

            _logger.Info($"Session created: {sessionId} -> {endpoint}");
            return @"{""success"":true}";
        }

        private string DoAddPid(dynamic msg)
        {
            string sessionId = msg.sessionId;
            uint pid = (uint)msg.pid;
            long createdAtFileTime = (long)msg.createdAtFileTime;

            if (string.IsNullOrWhiteSpace(sessionId))
                return @"{""success"":false,""error"":""missing sessionId""}";

            lock (_sessionLock)
            {
                if (!_sessions.TryGetValue(sessionId, out var state))
                    return @"{""success"":false,""error"":""session not found""}";

                bool ok = _socksify.AssociateProcessIdToProxy(pid, state.ProxyHandle, createdAtFileTime);
                if (ok)
                {
                    state.Pids.Add(pid);
                    _logger.Info($"Session {sessionId}: added PID {pid}");
                }
                else
                {
                    _logger.Warn($"Session {sessionId}: failed to add PID {pid}");
                }

                return $"{{\"success\":{ok.ToString().ToLower()}}}";
            }
        }

        private string DoRemovePid(dynamic msg)
        {
            string sessionId = msg.sessionId;
            uint pid = (uint)msg.pid;

            if (string.IsNullOrWhiteSpace(sessionId))
                return @"{""success"":false,""error"":""missing sessionId""}";

            lock (_sessionLock)
            {
                if (!_sessions.TryGetValue(sessionId, out var state))
                    return @"{""success"":false,""error"":""session not found""}";

                bool ok = _socksify.RemoveProcessId(pid);
                state.Pids.Remove(pid);
                _logger.Info($"Session {sessionId}: removed PID {pid}");
                return $"{{\"success\":{ok.ToString().ToLower()}}}";
            }
        }

        private string DoCloseSession(dynamic msg)
        {
            string sessionId = msg.sessionId;

            if (string.IsNullOrWhiteSpace(sessionId))
                return @"{""success"":false,""error"":""missing sessionId""}";

            lock (_sessionLock)
            {
                if (!_sessions.TryGetValue(sessionId, out var state))
                    return @"{""success"":false,""error"":""session not found""}";

                foreach (var pid in state.Pids)
                {
                    try
                    {
                        _socksify.RemoveProcessId(pid);
                    }
                    catch (Exception ex)
                    {
                        _logger.Warn($"Session {sessionId}: error removing PID {pid}: {ex.Message}");
                    }
                }

                _sessions.Remove(sessionId);
                _logger.Info($"Session closed: {sessionId} ({state.Pids.Count} PIDs removed)");
            }

            return @"{""success"":true}";
        }

        private static string EscapeJson(string s)
        {
            if (string.IsNullOrEmpty(s))
                return s;
            return s.Replace("\\", "\\\\")
                    .Replace("\"", "\\\"")
                    .Replace("\n", "\\n")
                    .Replace("\r", "\\r")
                    .Replace("\t", "\\t");
        }
    }
}
