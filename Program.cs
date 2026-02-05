//==============================================================================
// BCI GESTURE CONTROL ORCHESTRATOR v2.0
// Overhauled with improvements from Wrapper application
// Extensive debugging and logging added
//==============================================================================

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace BciOrchestrator
{
    //==========================================================================
    // CONFIGURATION CLASSES
    //==========================================================================

    public class WebSocketConfig
    {
        public int Port { get; set; } = 8080;
        public string Host { get; set; } = "127.0.0.1";
        public bool AllowLAN { get; set; } = false;
        public int ReconnectTimeoutMs { get; set; } = 5000;
        public int PingIntervalMs { get; set; } = 30000;
        public int BufferSize { get; set; } = 8192;
    }

    public class UdpReceiverConfig
    {
        public string Executable { get; set; } = "./UdpReceiver.exe";
        public int Port { get; set; } = 7400;
        public ThresholdsConfig Thresholds { get; set; } = new();
    }

    public class ThresholdsConfig
    {
        public double OnThreshold { get; set; } = 0.6;
        public double OffThreshold { get; set; } = 0.5;
        public int DebounceMs { get; set; } = 150;
        public int RateHz { get; set; } = 15;
    }

    public class EmotivOSCConfig
    {
        public bool Enabled { get; set; } = true;
        public int ExpectedPort { get; set; } = 7400;
        public int HealthCheckIntervalMs { get; set; } = 10000;
        public int TimeoutMs { get; set; } = 30000;
    }

    public class LoggingConfig
    {
        public string Level { get; set; } = "INFO"; // DEBUG, INFO, WARN, ERROR
        public string LogFile { get; set; } = "./logs/orchestrator.log";
        public int MaxLogSizeMB { get; set; } = 50;
        public bool RotateDaily { get; set; } = true;
        public bool LogToConsole { get; set; } = true;
        public bool LogToFile { get; set; } = true;
        public bool IncludeStackTrace { get; set; } = true;
    }

    public class HealthConfig
    {
        public int HeartbeatIntervalMs { get; set; } = 3000;
        public int MaxRestarts { get; set; } = 3;
        public int RestartCooldownMs { get; set; } = 30000;
        public int MetricsReportIntervalMs { get; set; } = 60000;
    }

    public class OrchestratorConfig
    {
        public WebSocketConfig WebSocket { get; set; } = new();
        public UdpReceiverConfig UdpReceiver { get; set; } = new();
        public EmotivOSCConfig EmotivOSC { get; set; } = new();
        public Dictionary<string, string> ActionMap { get; set; } = new()
        {
            ["push"] = "moveForward",
            ["pull"] = "moveBackward",
            ["left"] = "turnLeft",
            ["right"] = "turnRight",
            ["lift"] = "jump",
            ["drop"] = "crouch",
            ["neutral"] = "idle"
        };
        public LoggingConfig Logging { get; set; } = new();
        public HealthConfig Health { get; set; } = new();
        public bool UseIntegratedWebSocket { get; set; } = true; // Use C# WS server instead of Node.js
        public bool TestMode { get; set; } = false;
    }

    //==========================================================================
    // MESSAGE STRUCTURES
    //==========================================================================

    public class BrainEvent
    {
        [JsonPropertyName("ts")]
        public long Ts { get; set; }
        
        [JsonPropertyName("type")]
        public string Type { get; set; } = "mental_command";
        
        [JsonPropertyName("action")]
        public string Action { get; set; } = "neutral";
        
        [JsonPropertyName("confidence")]
        public double Confidence { get; set; }
        
        [JsonPropertyName("durationMs")]
        public int DurationMs { get; set; }
        
        [JsonPropertyName("source")]
        public string Source { get; set; } = "emotiv-osc";
        
        [JsonPropertyName("raw")]
        public Dictionary<string, object> Raw { get; set; } = new();
    }

    public class StateSnapshot
    {
        [JsonPropertyName("active")]
        public string Active { get; set; } = "neutral";
        
        [JsonPropertyName("confidence")]
        public double Confidence { get; set; }
        
        [JsonPropertyName("durationMs")]
        public int DurationMs { get; set; }
        
        [JsonPropertyName("source")]
        public string Source { get; set; } = "none";
        
        [JsonPropertyName("timestamp")]
        public long Timestamp { get; set; }
        
        [JsonPropertyName("uptime")]
        public long UptimeMs { get; set; }
        
        [JsonPropertyName("stats")]
        public OrchestratorStats Stats { get; set; } = new();
    }

    public class OrchestratorStats
    {
        [JsonPropertyName("totalPackets")]
        public long TotalPackets { get; set; }
        
        [JsonPropertyName("totalActions")]
        public long TotalActions { get; set; }
        
        [JsonPropertyName("avgConfidence")]
        public double AvgConfidence { get; set; }
        
        [JsonPropertyName("connectedClients")]
        public int ConnectedClients { get; set; }
        
        [JsonPropertyName("packetsPerSecond")]
        public double PacketsPerSecond { get; set; }
    }

    //==========================================================================
    // LOGGER CLASS - EXTENSIVE DEBUGGING SUPPORT
    //==========================================================================

    public enum LogLevel
    {
        DEBUG = 0,
        INFO = 1,
        WARN = 2,
        ERROR = 3
    }

    public class Logger
    {
        private readonly LoggingConfig _config;
        private readonly object _lock = new();
        private readonly string _logDirectory;
        private string _currentLogFile;
        private DateTime _currentLogDate;
        private long _currentLogSize;
        private readonly string _componentName;

        public Logger(LoggingConfig config, string componentName = "ORCHESTRATOR")
        {
            _config = config;
            _componentName = componentName;
            _logDirectory = Path.GetDirectoryName(config.LogFile) ?? "./logs";
            _currentLogDate = DateTime.Today;
            _currentLogFile = GetLogFileName();
            
            Directory.CreateDirectory(_logDirectory);
        }

        private string GetLogFileName()
        {
            if (_config.RotateDaily)
            {
                string baseName = Path.GetFileNameWithoutExtension(_config.LogFile);
                string extension = Path.GetExtension(_config.LogFile);
                return Path.Combine(_logDirectory, $"{baseName}_{DateTime.Today:yyyy-MM-dd}{extension}");
            }
            return _config.LogFile;
        }

        private LogLevel ParseLevel(string level)
        {
            return level.ToUpperInvariant() switch
            {
                "DEBUG" => LogLevel.DEBUG,
                "INFO" => LogLevel.INFO,
                "WARN" => LogLevel.WARN,
                "ERROR" => LogLevel.ERROR,
                _ => LogLevel.INFO
            };
        }

        private bool ShouldLog(LogLevel level)
        {
            return level >= ParseLevel(_config.Level);
        }

        public void Log(LogLevel level, string message, string? context = null, Exception? ex = null)
        {
            if (!ShouldLog(level)) return;

            string timestamp = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff");
            string levelStr = level.ToString().PadRight(5);
            string contextStr = string.IsNullOrEmpty(context) ? "" : $"[{context}] ";
            string fullMessage = $"[{timestamp}] [{levelStr}] [{_componentName}] {contextStr}{message}";

            if (ex != null && _config.IncludeStackTrace)
            {
                fullMessage += $"\n  Exception: {ex.GetType().Name}: {ex.Message}";
                fullMessage += $"\n  StackTrace: {ex.StackTrace}";
                if (ex.InnerException != null)
                {
                    fullMessage += $"\n  InnerException: {ex.InnerException.Message}";
                }
            }

            lock (_lock)
            {
                // Console output with colors
                if (_config.LogToConsole)
                {
                    ConsoleColor color = level switch
                    {
                        LogLevel.DEBUG => ConsoleColor.Gray,
                        LogLevel.INFO => ConsoleColor.White,
                        LogLevel.WARN => ConsoleColor.Yellow,
                        LogLevel.ERROR => ConsoleColor.Red,
                        _ => ConsoleColor.White
                    };

                    Console.ForegroundColor = color;
                    Console.WriteLine(fullMessage);
                    Console.ResetColor();
                }

                // File output
                if (_config.LogToFile)
                {
                    WriteToFile(fullMessage);
                }
            }
        }

        private void WriteToFile(string message)
        {
            try
            {
                // Check for log rotation
                if (_config.RotateDaily && DateTime.Today != _currentLogDate)
                {
                    _currentLogDate = DateTime.Today;
                    _currentLogFile = GetLogFileName();
                    _currentLogSize = 0;
                }

                // Check for size-based rotation
                if (_currentLogSize > _config.MaxLogSizeMB * 1024 * 1024)
                {
                    string rotatedFile = _currentLogFile.Replace(".log", $"_{DateTime.Now:HHmmss}.log");
                    if (File.Exists(_currentLogFile))
                    {
                        File.Move(_currentLogFile, rotatedFile);
                    }
                    _currentLogSize = 0;
                }

                File.AppendAllText(_currentLogFile, message + Environment.NewLine);
                _currentLogSize += Encoding.UTF8.GetByteCount(message) + Environment.NewLine.Length;
            }
            catch
            {
                // Silently fail to avoid infinite error loop
            }
        }

        // Convenience methods
        public void Debug(string message, string? context = null) => Log(LogLevel.DEBUG, message, context);
        public void Info(string message, string? context = null) => Log(LogLevel.INFO, message, context);
        public void Warn(string message, string? context = null, Exception? ex = null) => Log(LogLevel.WARN, message, context, ex);
        public void Error(string message, string? context = null, Exception? ex = null) => Log(LogLevel.ERROR, message, context, ex);

        // Structured logging for debugging
        public void LogPacket(string direction, string protocol, string data, string? endpoint = null)
        {
            if (!ShouldLog(LogLevel.DEBUG)) return;
            string endpointStr = endpoint != null ? $" [{endpoint}]" : "";
            Debug($"PACKET {direction}{endpointStr} ({protocol}): {data.Substring(0, Math.Min(data.Length, 200))}", "NETWORK");
        }

        public void LogStateChange(string from, string to, double confidence, string reason)
        {
            Info($"STATE CHANGE: {from} -> {to} (conf={confidence:F2}, reason={reason})", "STATE");
        }

        public void LogMetrics(OrchestratorStats stats)
        {
            Info($"METRICS: packets={stats.TotalPackets}, actions={stats.TotalActions}, " +
                 $"avgConf={stats.AvgConfidence:F2}, clients={stats.ConnectedClients}, " +
                 $"pps={stats.PacketsPerSecond:F1}", "METRICS");
        }

        public void LogStartup(string component, bool success, string? details = null)
        {
            string status = success ? "✓ STARTED" : "✗ FAILED";
            string detailStr = details != null ? $" - {details}" : "";
            if (success)
                Info($"{status}: {component}{detailStr}", "STARTUP");
            else
                Error($"{status}: {component}{detailStr}", "STARTUP");
        }
    }

    //==========================================================================
    // INTEGRATED WEBSOCKET SERVER (from Wrapper improvements)
    //==========================================================================

    public class IntegratedWebSocketServer
    {
        private HttpListener? _httpListener;
        private readonly List<WebSocket> _clients = new();
        private readonly object _clientsLock = new();
        private readonly Logger _logger;
        private readonly OrchestratorConfig _config;
        private CancellationToken _ct;
        private Func<StateSnapshot>? _getStateSnapshot;
        
        // Metrics
        private long _messagesSent = 0;
        private long _messagesReceived = 0;
        private long _connectionCount = 0;

        public int ConnectedClients
        {
            get
            {
                lock (_clientsLock)
                {
                    return _clients.Count(c => c.State == WebSocketState.Open);
                }
            }
        }

        public IntegratedWebSocketServer(Logger logger, OrchestratorConfig config)
        {
            _logger = logger;
            _config = config;
        }

        public void SetStateProvider(Func<StateSnapshot> provider)
        {
            _getStateSnapshot = provider;
        }

        public async Task StartAsync(CancellationToken ct)
        {
            _ct = ct;
            string host = _config.WebSocket.Host;
            int port = _config.WebSocket.Port;

            _httpListener = new HttpListener();
            
            // Handle both localhost and LAN scenarios
            if (_config.WebSocket.AllowLAN || host == "0.0.0.0")
            {
                _httpListener.Prefixes.Add($"http://+:{port}/");
            }
            else
            {
                _httpListener.Prefixes.Add($"http://{host}:{port}/");
            }

            try
            {
                _httpListener.Start();
                _logger.Info($"HTTP/WebSocket server listening on {host}:{port}", "WS-SERVER");
                _logger.Info($"  WebSocket: ws://{host}:{port}/stream", "WS-SERVER");
                _logger.Info($"  HTTP GET:  http://{host}:{port}/state", "WS-SERVER");
                _logger.Info($"  HTTP GET:  http://{host}:{port}/healthz", "WS-SERVER");
                _logger.Info($"  HTTP POST: http://{host}:{port}/broadcast", "WS-SERVER");
            }
            catch (HttpListenerException ex)
            {
                _logger.Error($"Failed to start HTTP listener: {ex.Message}", "WS-SERVER", ex);
                _logger.Warn("You may need to run as Administrator or reserve the URL with: " +
                           $"netsh http add urlacl url=http://+:{port}/ user=Everyone", "WS-SERVER");
                throw;
            }

            // Start ping task to keep connections alive
            _ = Task.Run(() => PingClientsAsync(ct), ct);

            // Main request loop
            while (!ct.IsCancellationRequested)
            {
                try
                {
                    HttpListenerContext context = await _httpListener.GetContextAsync();
                    _ = Task.Run(() => HandleRequestAsync(context), ct);
                }
                catch (HttpListenerException) when (ct.IsCancellationRequested)
                {
                    break;
                }
                catch (ObjectDisposedException)
                {
                    break;
                }
                catch (Exception ex)
                {
                    _logger.Error($"Error accepting request: {ex.Message}", "WS-SERVER", ex);
                }
            }

            _httpListener.Stop();
            _logger.Info("WebSocket server stopped", "WS-SERVER");
        }

        private async Task HandleRequestAsync(HttpListenerContext context)
        {
            string path = context.Request.Url?.AbsolutePath ?? "/";
            string method = context.Request.HttpMethod;
            string clientIP = context.Request.RemoteEndPoint?.ToString() ?? "unknown";

            _logger.Debug($"Request: {method} {path} from {clientIP}", "WS-SERVER");

            try
            {
                // WebSocket upgrade request
                if (context.Request.IsWebSocketRequest && path == "/stream")
                {
                    await HandleWebSocketAsync(context);
                    return;
                }

                // HTTP endpoints
                switch (path)
                {
                    case "/state":
                        if (method == "GET")
                            await HandleStateRequestAsync(context);
                        else
                            SendMethodNotAllowed(context);
                        break;

                    case "/healthz":
                        if (method == "GET")
                            await HandleHealthCheckAsync(context);
                        else
                            SendMethodNotAllowed(context);
                        break;

                    case "/broadcast":
                        if (method == "POST")
                            await HandleBroadcastRequestAsync(context);
                        else
                            SendMethodNotAllowed(context);
                        break;

                    case "/metrics":
                        if (method == "GET")
                            await HandleMetricsRequestAsync(context);
                        else
                            SendMethodNotAllowed(context);
                        break;

                    default:
                        await SendInfoPageAsync(context);
                        break;
                }
            }
            catch (Exception ex)
            {
                _logger.Error($"Error handling request: {ex.Message}", "WS-SERVER", ex);
                try
                {
                    context.Response.StatusCode = 500;
                    context.Response.Close();
                }
                catch { }
            }
        }

        private async Task HandleWebSocketAsync(HttpListenerContext context)
        {
            WebSocketContext? wsContext = null;
            
            try
            {
                wsContext = await context.AcceptWebSocketAsync(subProtocol: null);
                var ws = wsContext.WebSocket;
                
                _connectionCount++;
                _logger.Info($"WebSocket client connected (total connections: {_connectionCount})", "WS-SERVER");

                lock (_clientsLock)
                {
                    _clients.Add(ws);
                }

                // Send initial state
                var initialState = _getStateSnapshot?.Invoke();
                if (initialState != null)
                {
                    string json = JsonSerializer.Serialize(initialState);
                    await SendToClientAsync(ws, json);
                    _logger.Debug($"Sent initial state to client", "WS-SERVER");
                }

                // Receive loop
                var buffer = new byte[_config.WebSocket.BufferSize];
                while (ws.State == WebSocketState.Open && !_ct.IsCancellationRequested)
                {
                    try
                    {
                        var result = await ws.ReceiveAsync(new ArraySegment<byte>(buffer), _ct);

                        if (result.MessageType == WebSocketMessageType.Close)
                        {
                            _logger.Debug("Client requested close", "WS-SERVER");
                            break;
                        }

                        if (result.MessageType == WebSocketMessageType.Text)
                        {
                            string message = Encoding.UTF8.GetString(buffer, 0, result.Count);
                            _messagesReceived++;
                            _logger.Debug($"Received from client: {message}", "WS-SERVER");
                            
                            // Handle client commands if needed
                            await HandleClientMessageAsync(ws, message);
                        }
                    }
                    catch (WebSocketException ex) when (ex.WebSocketErrorCode == WebSocketError.ConnectionClosedPrematurely)
                    {
                        _logger.Debug("Client disconnected prematurely", "WS-SERVER");
                        break;
                    }
                }

                // Graceful close
                if (ws.State == WebSocketState.Open || ws.State == WebSocketState.CloseReceived)
                {
                    await ws.CloseAsync(WebSocketCloseStatus.NormalClosure, "Server closing", CancellationToken.None);
                }
            }
            catch (Exception ex)
            {
                _logger.Warn($"WebSocket client error: {ex.Message}", "WS-SERVER", ex);
            }
            finally
            {
                if (wsContext != null)
                {
                    lock (_clientsLock)
                    {
                        _clients.Remove(wsContext.WebSocket);
                    }
                    try { wsContext.WebSocket.Dispose(); } catch { }
                }
                _logger.Info($"WebSocket client disconnected (remaining: {ConnectedClients})", "WS-SERVER");
            }
        }

        private async Task HandleClientMessageAsync(WebSocket ws, string message)
        {
            // Handle ping/pong
            if (message == "ping")
            {
                await SendToClientAsync(ws, "pong");
                return;
            }

            // Handle state request
            if (message == "state")
            {
                var state = _getStateSnapshot?.Invoke();
                if (state != null)
                {
                    string json = JsonSerializer.Serialize(state);
                    await SendToClientAsync(ws, json);
                }
                return;
            }

            // Log unknown messages
            _logger.Debug($"Unknown client message: {message}", "WS-SERVER");
        }

        private async Task HandleStateRequestAsync(HttpListenerContext context)
        {
            var state = _getStateSnapshot?.Invoke() ?? new StateSnapshot();
            string json = JsonSerializer.Serialize(state);
            await SendJsonResponseAsync(context, json);
        }

        private async Task HandleHealthCheckAsync(HttpListenerContext context)
        {
            var health = new
            {
                status = "ok",
                timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                connectedClients = ConnectedClients
            };
            string json = JsonSerializer.Serialize(health);
            await SendJsonResponseAsync(context, json);
        }

        private async Task HandleBroadcastRequestAsync(HttpListenerContext context)
        {
            using var reader = new StreamReader(context.Request.InputStream);
            string json = await reader.ReadToEndAsync();

            _logger.Debug($"Broadcast request: {json.Substring(0, Math.Min(json.Length, 100))}", "WS-SERVER");

            await BroadcastAsync(json);

            context.Response.StatusCode = 200;
            context.Response.Close();
        }

        private async Task HandleMetricsRequestAsync(HttpListenerContext context)
        {
            var metrics = new
            {
                messagesSent = _messagesSent,
                messagesReceived = _messagesReceived,
                totalConnections = _connectionCount,
                activeConnections = ConnectedClients
            };
            string json = JsonSerializer.Serialize(metrics);
            await SendJsonResponseAsync(context, json);
        }

        private async Task SendInfoPageAsync(HttpListenerContext context)
        {
            string info = $@"BCI Orchestrator WebSocket Server v2.0

Endpoints:
  WebSocket: ws://{_config.WebSocket.Host}:{_config.WebSocket.Port}/stream
  GET  /state   - Current state JSON
  GET  /healthz - Health check
  GET  /metrics - Server metrics
  POST /broadcast - Broadcast message to all clients

Connected Clients: {ConnectedClients}
Total Connections: {_connectionCount}
Messages Sent: {_messagesSent}
Messages Received: {_messagesReceived}";

            byte[] body = Encoding.UTF8.GetBytes(info);
            context.Response.StatusCode = 200;
            context.Response.ContentType = "text/plain";
            context.Response.ContentLength64 = body.Length;
            await context.Response.OutputStream.WriteAsync(body);
            context.Response.Close();
        }

        private async Task SendJsonResponseAsync(HttpListenerContext context, string json)
        {
            byte[] body = Encoding.UTF8.GetBytes(json);
            context.Response.StatusCode = 200;
            context.Response.ContentType = "application/json";
            context.Response.ContentEncoding = Encoding.UTF8;
            context.Response.ContentLength64 = body.Length;
            await context.Response.OutputStream.WriteAsync(body);
            context.Response.Close();
        }

        private void SendMethodNotAllowed(HttpListenerContext context)
        {
            context.Response.StatusCode = 405;
            context.Response.Close();
        }

        public async Task BroadcastAsync(string message)
        {
            var buffer = Encoding.UTF8.GetBytes(message);
            var segment = new ArraySegment<byte>(buffer);

            List<WebSocket> clientsCopy;
            lock (_clientsLock)
            {
                clientsCopy = _clients.Where(c => c.State == WebSocketState.Open).ToList();
            }

            var tasks = clientsCopy.Select(async client =>
            {
                try
                {
                    await client.SendAsync(segment, WebSocketMessageType.Text, true, CancellationToken.None);
                    Interlocked.Increment(ref _messagesSent);
                }
                catch (Exception ex)
                {
                    _logger.Debug($"Failed to send to client: {ex.Message}", "WS-SERVER");
                }
            });

            await Task.WhenAll(tasks);
            _logger.Debug($"Broadcasted to {clientsCopy.Count} clients", "WS-SERVER");
        }

        public async Task BroadcastAsync(BrainEvent brainEvent)
        {
            string json = JsonSerializer.Serialize(brainEvent);
            await BroadcastAsync(json);
        }

        private async Task SendToClientAsync(WebSocket ws, string message)
        {
            if (ws.State != WebSocketState.Open) return;

            var buffer = Encoding.UTF8.GetBytes(message);
            await ws.SendAsync(new ArraySegment<byte>(buffer), WebSocketMessageType.Text, true, CancellationToken.None);
            Interlocked.Increment(ref _messagesSent);
        }

        private async Task PingClientsAsync(CancellationToken ct)
        {
            while (!ct.IsCancellationRequested)
            {
                await Task.Delay(_config.WebSocket.PingIntervalMs, ct);

                List<WebSocket> deadClients = new();

                lock (_clientsLock)
                {
                    foreach (var client in _clients)
                    {
                        if (client.State != WebSocketState.Open)
                        {
                            deadClients.Add(client);
                        }
                    }

                    foreach (var dead in deadClients)
                    {
                        _clients.Remove(dead);
                        try { dead.Dispose(); } catch { }
                    }
                }

                if (deadClients.Count > 0)
                {
                    _logger.Debug($"Cleaned up {deadClients.Count} dead connections", "WS-SERVER");
                }
            }
        }

        public void Stop()
        {
            _httpListener?.Stop();
            
            lock (_clientsLock)
            {
                foreach (var client in _clients)
                {
                    try { client.Dispose(); } catch { }
                }
                _clients.Clear();
            }
        }
    }

    //==========================================================================
    // UDP RECEIVER (Integrated - from Wrapper improvements)
    //==========================================================================

    public class IntegratedUdpReceiver
    {
        private readonly Logger _logger;
        private readonly OrchestratorConfig _config;
        private UdpClient? _udpClient;
        private CancellationToken _ct;

        // State management (from Wrapper)
        private string _activeAction = "neutral";
        private string _candidateAction = "neutral";
        private DateTime _candidateSince = DateTime.UtcNow;
        private double _lastConfidence = 0.0;
        private DateTime _activeSince = DateTime.UtcNow;
        private string _lastSource = "none";

        // Rate limiting
        private DateTime _lastBroadcast = DateTime.MinValue;
        private string _lastBroadcastJson = "";

        // Metrics
        private long _packetsReceived = 0;
        private long _packetsProcessed = 0;
        private long _stateChanges = 0;

        // Event for broadcasting
        public event Func<BrainEvent, Task>? OnBrainEvent;

        public string ActiveAction => _activeAction;
        public double LastConfidence => _lastConfidence;
        public DateTime ActiveSince => _activeSince;
        public string LastSource => _lastSource;
        public long PacketsReceived => _packetsReceived;
        public long StateChanges => _stateChanges;

        public IntegratedUdpReceiver(Logger logger, OrchestratorConfig config)
        {
            _logger = logger;
            _config = config;
        }

        public async Task StartAsync(CancellationToken ct)
        {
            _ct = ct;
            int port = _config.UdpReceiver.Port;

            try
            {
                _udpClient = new UdpClient(port);
                _logger.Info($"UDP Receiver listening on port {port}", "UDP-RECV");
                _logger.Info($"  On threshold: {_config.UdpReceiver.Thresholds.OnThreshold}", "UDP-RECV");
                _logger.Info($"  Off threshold: {_config.UdpReceiver.Thresholds.OffThreshold}", "UDP-RECV");
                _logger.Info($"  Debounce: {_config.UdpReceiver.Thresholds.DebounceMs}ms", "UDP-RECV");
                _logger.Info($"  Rate limit: {_config.UdpReceiver.Thresholds.RateHz}Hz", "UDP-RECV");
            }
            catch (SocketException ex)
            {
                _logger.Error($"Failed to bind UDP port {port}: {ex.Message}", "UDP-RECV", ex);
                throw;
            }

            int intervalMs = Math.Max(1, 1000 / _config.UdpReceiver.Thresholds.RateHz);

            while (!ct.IsCancellationRequested)
            {
                try
                {
                    if (_udpClient.Available > 0)
                    {
                        var result = await _udpClient.ReceiveAsync(ct);
                        await ProcessPacketAsync(result.Buffer);
                    }
                    else
                    {
                        await Task.Delay(1, ct);
                    }
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                catch (Exception ex)
                {
                    _logger.Error($"Error receiving UDP packet: {ex.Message}", "UDP-RECV", ex);
                }
            }

            _udpClient.Close();
            _logger.Info("UDP Receiver stopped", "UDP-RECV");
        }

        private async Task ProcessPacketAsync(byte[] buffer)
        {
            _packetsReceived++;
            
            string action;
            double confidence;

            // Try to parse as OSC first
            if (TryParseOsc(buffer, out action, out confidence))
            {
                _lastSource = "osc";
                _logger.Debug($"Parsed OSC: action={action}, conf={confidence:F2}", "UDP-RECV");
            }
            // Fall back to CSV format
            else if (TryParseCsv(buffer, out action, out confidence))
            {
                _lastSource = "csv";
                _logger.Debug($"Parsed CSV: action={action}, conf={confidence:F2}", "UDP-RECV");
            }
            else
            {
                _logger.Debug($"Failed to parse packet: {Encoding.UTF8.GetString(buffer)}", "UDP-RECV");
                return;
            }

            _lastConfidence = confidence;
            _packetsProcessed++;

            // Apply filters (from Wrapper)
            await ApplyFiltersAndBroadcastAsync(action, confidence);
        }

        private bool TryParseOsc(byte[] buffer, out string action, out double confidence)
        {
            action = "";
            confidence = 0;

            try
            {
                if (buffer.Length < 8) return false;

                // OSC address pattern starts with '/'
                if (buffer[0] != '/') return false;

                // Find end of address (null-terminated, padded to 4 bytes)
                int addressEnd = Array.IndexOf(buffer, (byte)0);
                if (addressEnd < 0) return false;

                string address = Encoding.ASCII.GetString(buffer, 0, addressEnd);
                _logger.Debug($"OSC address: {address}", "UDP-RECV");

                // Skip to type tag (after address padding)
                int typeTagStart = ((addressEnd + 4) / 4) * 4;
                if (typeTagStart >= buffer.Length) return false;

                // Extract action from address (e.g., "/com/push" -> "push")
                string[] parts = address.Split('/');
                if (parts.Length >= 3)
                {
                    action = parts[^1].ToLowerInvariant();
                }

                // Look for float argument (confidence)
                // Type tag should be ",f" for float
                int dataStart = typeTagStart;
                while (dataStart < buffer.Length && buffer[dataStart] != 0)
                {
                    dataStart++;
                }
                dataStart = ((dataStart + 4) / 4) * 4;

                if (dataStart + 4 <= buffer.Length)
                {
                    // OSC floats are big-endian
                    byte[] floatBytes = new byte[4];
                    Array.Copy(buffer, dataStart, floatBytes, 0, 4);
                    if (BitConverter.IsLittleEndian)
                    {
                        Array.Reverse(floatBytes);
                    }
                    confidence = BitConverter.ToSingle(floatBytes, 0);
                }

                return !string.IsNullOrEmpty(action);
            }
            catch (Exception ex)
            {
                _logger.Debug($"OSC parse error: {ex.Message}", "UDP-RECV");
                return false;
            }
        }

        private bool TryParseCsv(byte[] buffer, out string action, out double confidence)
        {
            action = "";
            confidence = 0;

            try
            {
                string text = Encoding.UTF8.GetString(buffer).Trim();
                var parts = text.Split(',');

                if (parts.Length != 2) return false;

                action = parts[0].Trim().ToLowerInvariant();
                
                if (!double.TryParse(parts[1].Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out confidence))
                {
                    return false;
                }

                return !string.IsNullOrEmpty(action);
            }
            catch
            {
                return false;
            }
        }

        private async Task ApplyFiltersAndBroadcastAsync(string action, double confidence)
        {
            var thresholds = _config.UdpReceiver.Thresholds;
            string previousActive = _activeAction;

            // Determine desired state based on thresholds (hysteresis)
            string desired = _activeAction;

            if (confidence >= thresholds.OnThreshold && action != "neutral")
            {
                desired = action;
            }
            else if (confidence < thresholds.OffThreshold)
            {
                desired = "neutral";
            }
            else if (_activeAction != "neutral" && action == _activeAction)
            {
                // Keep current action in hysteresis zone
                desired = _activeAction;
            }

            // Debounce logic
            if (desired != _candidateAction)
            {
                _candidateAction = desired;
                _candidateSince = DateTime.UtcNow;
                _logger.Debug($"New candidate: {_candidateAction} (conf={confidence:F2})", "UDP-RECV");
            }
            else if ((DateTime.UtcNow - _candidateSince).TotalMilliseconds >= thresholds.DebounceMs)
            {
                // Candidate has been stable long enough
                if (_candidateAction != _activeAction)
                {
                    string previousAction = _activeAction;
                    _activeAction = _candidateAction;
                    _activeSince = DateTime.UtcNow;
                    _stateChanges++;

                    _logger.LogStateChange(previousAction, _activeAction, confidence, 
                        $"debounced after {thresholds.DebounceMs}ms");
                }
            }

            // Rate limiting for broadcasts
            int intervalMs = Math.Max(1, 1000 / thresholds.RateHz);
            if ((DateTime.UtcNow - _lastBroadcast).TotalMilliseconds < intervalMs)
            {
                return;
            }

            // Only broadcast if state changed or it's been a while
            if (_activeAction != previousActive || (DateTime.UtcNow - _lastBroadcast).TotalSeconds > 1)
            {
                var brainEvent = new BrainEvent
                {
                    Ts = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                    Type = "mental_command",
                    Action = MapAction(_activeAction),
                    Confidence = confidence,
                    DurationMs = (int)(DateTime.UtcNow - _activeSince).TotalMilliseconds,
                    Source = _lastSource,
                    Raw = new Dictionary<string, object>
                    {
                        ["rawAction"] = action,
                        ["rawConfidence"] = confidence,
                        ["filtered"] = true
                    }
                };

                string json = JsonSerializer.Serialize(brainEvent);

                // Deduplicate identical messages
                if (json != _lastBroadcastJson)
                {
                    _lastBroadcastJson = json;
                    _lastBroadcast = DateTime.UtcNow;

                    if (OnBrainEvent != null)
                    {
                        await OnBrainEvent(brainEvent);
                    }
                }
            }
        }

        private string MapAction(string action)
        {
            if (_config.ActionMap.TryGetValue(action, out string? mapped))
            {
                return mapped;
            }
            return action;
        }

        public void Stop()
        {
            _udpClient?.Close();
        }

        public void InjectTestPacket(string action, double confidence)
        {
            _logger.Info($"Injecting test packet: {action},{confidence:F2}", "UDP-RECV");
            _ = ProcessPacketAsync(Encoding.UTF8.GetBytes($"{action},{confidence:F2}"));
        }
    }

    //==========================================================================
    // METRICS REPORTER
    //==========================================================================

    public class MetricsReporter
    {
        private readonly Logger _logger;
        private readonly BciOrchestrator _orchestrator;
        private long _lastPacketCount = 0;
        private long _lastActionCount = 0;
        private DateTime _lastReportTime = DateTime.UtcNow;

        public MetricsReporter(Logger logger, BciOrchestrator orchestrator)
        {
            _logger = logger;
            _orchestrator = orchestrator;
        }

        public async Task RunAsync(CancellationToken ct, int intervalMs)
        {
            while (!ct.IsCancellationRequested)
            {
                await Task.Delay(intervalMs, ct);
                Report();
            }
        }

        public void Report()
        {
            var stats = _orchestrator.GetStats();
            var now = DateTime.UtcNow;
            var elapsed = (now - _lastReportTime).TotalSeconds;

            if (elapsed > 0)
            {
                stats.PacketsPerSecond = (stats.TotalPackets - _lastPacketCount) / elapsed;
            }

            _logger.LogMetrics(stats);

            _lastPacketCount = stats.TotalPackets;
            _lastActionCount = stats.TotalActions;
            _lastReportTime = now;
        }
    }

    //==========================================================================
    // TEST COMMAND INTERFACE
    //==========================================================================

    public class TestCommandInterface
    {
        private readonly Logger _logger;
        private readonly IntegratedUdpReceiver _receiver;
        private UdpClient? _testUdp;

        public TestCommandInterface(Logger logger, IntegratedUdpReceiver receiver)
        {
            _logger = logger;
            _receiver = receiver;
        }

        public async Task RunInteractiveAsync(int targetPort, CancellationToken ct)
        {
            _testUdp = new UdpClient();
            _logger.Info("Test mode active. Commands: action,confidence (e.g., push,0.85) or 'quit'", "TEST");

            while (!ct.IsCancellationRequested)
            {
                Console.Write("Test> ");
                string? input = Console.ReadLine();

                if (string.IsNullOrWhiteSpace(input)) continue;

                input = input.Trim().ToLower();

                switch (input)
                {
                    case "quit":
                    case "exit":
                        _logger.Info("Exiting test mode", "TEST");
                        return;

                    case "help":
                        PrintHelp();
                        continue;

                    case "status":
                        PrintStatus();
                        continue;

                    case "sequence":
                        await RunTestSequenceAsync(targetPort);
                        continue;
                }

                // Parse action,confidence
                var parts = input.Split(',');
                if (parts.Length != 2)
                {
                    _logger.Warn("Invalid format. Use: action,confidence (e.g., push,0.85)", "TEST");
                    continue;
                }

                if (!double.TryParse(parts[1].Trim(), out double confidence) || confidence < 0 || confidence > 1)
                {
                    _logger.Warn("Confidence must be between 0.0 and 1.0", "TEST");
                    continue;
                }

                await InjectAsync(parts[0].Trim(), confidence, targetPort);
            }

            _testUdp.Close();
        }

        private void PrintHelp()
        {
            Console.WriteLine(@"
Test Commands:
  action,confidence  - Inject a mental command (e.g., push,0.85)
  sequence          - Run automated test sequence
  status            - Show current state
  help              - Show this help
  quit/exit         - Exit test mode

Actions: push, pull, left, right, lift, drop, neutral
Confidence: 0.0 to 1.0
");
        }

        private void PrintStatus()
        {
            Console.WriteLine($@"
Current State:
  Active: {_receiver.ActiveAction}
  Confidence: {_receiver.LastConfidence:F2}
  Source: {_receiver.LastSource}
  Packets: {_receiver.PacketsReceived}
  State Changes: {_receiver.StateChanges}
");
        }

        private async Task InjectAsync(string action, double confidence, int port)
        {
            string payload = $"{action},{confidence:F2}";
            byte[] data = Encoding.UTF8.GetBytes(payload);

            try
            {
                await _testUdp!.SendAsync(data, data.Length, "127.0.0.1", port);
                _logger.Info($"✓ Injected: {payload}", "TEST");
            }
            catch (Exception ex)
            {
                _logger.Error($"Injection failed: {ex.Message}", "TEST", ex);
            }
        }

        private async Task RunTestSequenceAsync(int port)
        {
            _logger.Info("Running automated test sequence...", "TEST");

            var sequence = new (string action, double conf, int delayMs)[]
            {
                ("neutral", 0.3, 1000),
                ("push", 0.85, 2000),
                ("neutral", 0.4, 500),
                ("left", 0.75, 2000),
                ("neutral", 0.3, 500),
                ("right", 0.80, 2000),
                ("neutral", 0.4, 500),
                ("pull", 0.70, 2000),
                ("neutral", 0.3, 1000),
                ("lift", 0.90, 500),
                ("neutral", 0.3, 2000)
            };

            foreach (var (action, conf, delay) in sequence)
            {
                await InjectAsync(action, conf, port);
                await Task.Delay(delay);
            }

            _logger.Info("Test sequence complete", "TEST");
        }
    }

    //==========================================================================
    // MAIN ORCHESTRATOR CLASS
    //==========================================================================

    public class BciOrchestrator
    {
        private OrchestratorConfig _config = new();
        private Logger _logger = null!;
        private IntegratedWebSocketServer? _wsServer;
        private IntegratedUdpReceiver? _udpReceiver;
        private MetricsReporter? _metricsReporter;
        private TestCommandInterface? _testInterface;

        private CancellationTokenSource _cts = new();
        private DateTime _startTime;
        private bool _isShuttingDown = false;

        // External process management (if not using integrated components)
        private Process? _nodeServerProcess;
        private Process? _externalUdpReceiverProcess;
        private readonly Dictionary<string, int> _restartCounts = new();
        private readonly Dictionary<string, DateTime> _lastRestartAttempt = new();

        public static async Task Main(string[] args)
        {
            Console.WriteLine("═══════════════════════════════════════════════════════");
            Console.WriteLine("  BCI GESTURE CONTROL ORCHESTRATOR v2.0");
            Console.WriteLine("  Comprehensive BCI Signal Processing & Distribution");
            Console.WriteLine("═══════════════════════════════════════════════════════");
            Console.WriteLine();

            var orchestrator = new BciOrchestrator();

            // Parse command line
            orchestrator.ParseArgs(args);

            // Setup graceful shutdown
            Console.CancelKeyPress += (sender, e) =>
            {
                e.Cancel = true;
                orchestrator._logger?.Info("Received CTRL+C, initiating shutdown...", "MAIN");
                orchestrator._isShuttingDown = true;
                orchestrator._cts.Cancel();
            };

            AppDomain.CurrentDomain.ProcessExit += (sender, e) =>
            {
                orchestrator._isShuttingDown = true;
                orchestrator._cts.Cancel();
                orchestrator.Cleanup();
            };

            try
            {
                // Initialize
                if (!orchestrator.Initialize())
                {
                    Console.WriteLine("Initialization failed. Exiting.");
                    return;
                }

                // Start all components
                if (!await orchestrator.StartAsync())
                {
                    orchestrator._logger.Error("Failed to start required components", "MAIN");
                    orchestrator.Cleanup();
                    return;
                }

                // Run main loop
                await orchestrator.RunAsync();
            }
            catch (Exception ex)
            {
                orchestrator._logger?.Error($"Fatal error: {ex.Message}", "MAIN", ex);
            }
            finally
            {
                orchestrator.Cleanup();
                orchestrator._logger?.Info("Orchestrator shutdown complete", "MAIN");
            }
        }

        private void ParseArgs(string[] args)
        {
            for (int i = 0; i < args.Length; i++)
            {
                switch (args[i].ToLower())
                {
                    case "--help":
                    case "-h":
                        PrintHelp();
                        Environment.Exit(0);
                        break;

                    case "--config":
                    case "-c":
                        if (i + 1 < args.Length)
                        {
                            LoadConfigFromFile(args[++i]);
                        }
                        break;

                    case "--test-mode":
                    case "-t":
                        _config.TestMode = true;
                        break;

                    case "--debug":
                    case "-d":
                        _config.Logging.Level = "DEBUG";
                        break;

                    case "--allow-lan":
                        _config.WebSocket.AllowLAN = true;
                        _config.WebSocket.Host = "0.0.0.0";
                        break;

                    case "--port":
                    case "-p":
                        if (i + 1 < args.Length && int.TryParse(args[++i], out int port))
                        {
                            _config.WebSocket.Port = port;
                        }
                        break;

                    case "--udp-port":
                        if (i + 1 < args.Length && int.TryParse(args[++i], out int udpPort))
                        {
                            _config.UdpReceiver.Port = udpPort;
                        }
                        break;

                    case "--use-node":
                        _config.UseIntegratedWebSocket = false;
                        break;
                }
            }
        }

        private bool Initialize()
        {
            // Load configuration
            try
            {
                if (File.Exists("./appsettings.json"))
                {
                    string json = File.ReadAllText("./appsettings.json");
                    var loaded = JsonSerializer.Deserialize<OrchestratorConfig>(json);
                    if (loaded != null)
                    {
                        // Merge with command line overrides
                        loaded.Logging.Level = _config.Logging.Level;
                        loaded.TestMode = _config.TestMode;
                        loaded.WebSocket.AllowLAN = _config.WebSocket.AllowLAN;
                        if (_config.WebSocket.Host == "0.0.0.0")
                            loaded.WebSocket.Host = "0.0.0.0";
                        _config = loaded;
                    }
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Warning: Failed to load config: {ex.Message}");
            }

            // Initialize logger
            _logger = new Logger(_config.Logging);
            _logger.Info("═══════════════════════════════════════════════════════", "INIT");
            _logger.Info("  INITIALIZING BCI ORCHESTRATOR", "INIT");
            _logger.Info("═══════════════════════════════════════════════════════", "INIT");

            // Log configuration
            _logger.Info($"Configuration:", "INIT");
            _logger.Info($"  WebSocket: {_config.WebSocket.Host}:{_config.WebSocket.Port}", "INIT");
            _logger.Info($"  UDP Port: {_config.UdpReceiver.Port}", "INIT");
            _logger.Info($"  Log Level: {_config.Logging.Level}", "INIT");
            _logger.Info($"  Test Mode: {_config.TestMode}", "INIT");
            _logger.Info($"  Integrated WS: {_config.UseIntegratedWebSocket}", "INIT");

            // Create log directory
            Directory.CreateDirectory(Path.GetDirectoryName(_config.Logging.LogFile) ?? "./logs");

            // Initialize components
            _startTime = DateTime.UtcNow;
            _restartCounts["wsServer"] = 0;
            _restartCounts["udpReceiver"] = 0;

            _logger.LogStartup("Logger", true, $"Level={_config.Logging.Level}");

            return true;
        }

        private void LoadConfigFromFile(string path)
        {
            if (!File.Exists(path))
            {
                Console.WriteLine($"Config file not found: {path}");
                return;
            }

            string json = File.ReadAllText(path);
            var loaded = JsonSerializer.Deserialize<OrchestratorConfig>(json);
            if (loaded != null)
            {
                _config = loaded;
            }
        }

        private async Task<bool> StartAsync()
        {
            _logger.Info("Starting components...", "STARTUP");

            // Start integrated UDP receiver
            _udpReceiver = new IntegratedUdpReceiver(_logger, _config);
            _udpReceiver.OnBrainEvent += async (brainEvent) =>
            {
                if (_wsServer != null)
                {
                    await _wsServer.BroadcastAsync(brainEvent);
                }
            };

            var udpTask = Task.Run(() => _udpReceiver.StartAsync(_cts.Token));
            await Task.Delay(500); // Give it time to bind
            _logger.LogStartup("UDP Receiver", true, $"Port {_config.UdpReceiver.Port}");

            // Start WebSocket server (integrated or Node.js)
            if (_config.UseIntegratedWebSocket)
            {
                _wsServer = new IntegratedWebSocketServer(_logger, _config);
                _wsServer.SetStateProvider(GetStateSnapshot);
                
                var wsTask = Task.Run(() => _wsServer.StartAsync(_cts.Token));
                await Task.Delay(500);
                _logger.LogStartup("WebSocket Server (Integrated)", true, $"Port {_config.WebSocket.Port}");
            }
            else
            {
                if (!await StartNodeWebSocketServerAsync())
                {
                    _logger.LogStartup("WebSocket Server (Node.js)", false);
                    return false;
                }
                _logger.LogStartup("WebSocket Server (Node.js)", true, $"Port {_config.WebSocket.Port}");
            }

            // Start metrics reporter
            _metricsReporter = new MetricsReporter(_logger, this);
            _ = Task.Run(() => _metricsReporter.RunAsync(_cts.Token, _config.Health.MetricsReportIntervalMs));
            _logger.LogStartup("Metrics Reporter", true);

            // Start test interface if in test mode
            if (_config.TestMode)
            {
                _testInterface = new TestCommandInterface(_logger, _udpReceiver);
                _ = Task.Run(() => _testInterface.RunInteractiveAsync(_config.UdpReceiver.Port, _cts.Token));
                _logger.LogStartup("Test Interface", true);
            }

            _logger.Info("═══════════════════════════════════════════════════════", "STARTUP");
            _logger.Info("  ALL COMPONENTS STARTED SUCCESSFULLY", "STARTUP");
            _logger.Info("═══════════════════════════════════════════════════════", "STARTUP");

            return true;
        }

        private async Task<bool> StartNodeWebSocketServerAsync()
        {
            _logger.Info("Starting Node.js WebSocket server...", "NODE");

            if (!File.Exists("./local_server.js"))
            {
                _logger.Error("local_server.js not found", "NODE");
                return false;
            }

            try
            {
                var startInfo = new ProcessStartInfo
                {
                    FileName = "node",
                    ArgumentList = { "./local_server.js" },
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    CreateNoWindow = true,
                    WorkingDirectory = Directory.GetCurrentDirectory()
                };

                startInfo.Environment["WS_PORT"] = _config.WebSocket.Port.ToString();
                startInfo.Environment["WS_HOST"] = _config.WebSocket.Host;

                _nodeServerProcess = Process.Start(startInfo);

                if (_nodeServerProcess == null)
                {
                    return false;
                }

                _nodeServerProcess.OutputDataReceived += (s, e) =>
                {
                    if (!string.IsNullOrWhiteSpace(e.Data))
                        _logger.Debug($"[NODE] {e.Data}", "NODE");
                };
                _nodeServerProcess.ErrorDataReceived += (s, e) =>
                {
                    if (!string.IsNullOrWhiteSpace(e.Data))
                        _logger.Warn($"[NODE-ERR] {e.Data}", "NODE");
                };
                _nodeServerProcess.EnableRaisingEvents = true;
                _nodeServerProcess.Exited += OnNodeServerExited;

                _nodeServerProcess.BeginOutputReadLine();
                _nodeServerProcess.BeginErrorReadLine();

                await Task.Delay(1000); // Wait for Node to start
                return !_nodeServerProcess.HasExited;
            }
            catch (Exception ex)
            {
                _logger.Error($"Failed to start Node.js: {ex.Message}", "NODE", ex);
                return false;
            }
        }

        private void OnNodeServerExited(object? sender, EventArgs e)
        {
            if (_isShuttingDown) return;

            int exitCode = _nodeServerProcess?.ExitCode ?? -1;
            _logger.Warn($"Node.js server exited with code {exitCode}", "NODE");

            // Attempt restart
            _ = Task.Run(async () =>
            {
                await Task.Delay(_config.Health.RestartCooldownMs);
                if (!_isShuttingDown && _restartCounts["wsServer"] < _config.Health.MaxRestarts)
                {
                    _restartCounts["wsServer"]++;
                    _logger.Info($"Attempting restart {_restartCounts["wsServer"]}/{_config.Health.MaxRestarts}", "NODE");
                    await StartNodeWebSocketServerAsync();
                }
            });
        }

        private async Task RunAsync()
        {
            _logger.Info("Orchestrator running. Press Ctrl+C to stop.", "MAIN");

            while (!_cts.Token.IsCancellationRequested)
            {
                try
                {
                    await Task.Delay(1000, _cts.Token);

                    // Health checks
                    if (!_config.UseIntegratedWebSocket && _nodeServerProcess?.HasExited == true)
                    {
                        _logger.Warn("Node.js process not running", "HEALTH");
                    }
                }
                catch (OperationCanceledException)
                {
                    break;
                }
            }
        }

        public StateSnapshot GetStateSnapshot()
        {
            return new StateSnapshot
            {
                Active = _udpReceiver?.ActiveAction ?? "neutral",
                Confidence = _udpReceiver?.LastConfidence ?? 0,
                DurationMs = _udpReceiver != null 
                    ? (int)(DateTime.UtcNow - _udpReceiver.ActiveSince).TotalMilliseconds 
                    : 0,
                Source = _udpReceiver?.LastSource ?? "none",
                Timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                UptimeMs = (long)(DateTime.UtcNow - _startTime).TotalMilliseconds,
                Stats = GetStats()
            };
        }

        public OrchestratorStats GetStats()
        {
            return new OrchestratorStats
            {
                TotalPackets = _udpReceiver?.PacketsReceived ?? 0,
                TotalActions = _udpReceiver?.StateChanges ?? 0,
                AvgConfidence = _udpReceiver?.LastConfidence ?? 0,
                ConnectedClients = _wsServer?.ConnectedClients ?? 0
            };
        }

        private void Cleanup()
        {
            _logger?.Info("Cleaning up...", "SHUTDOWN");

            _isShuttingDown = true;
            _cts.Cancel();

            // Stop integrated components
            _wsServer?.Stop();
            _udpReceiver?.Stop();

            // Stop external processes
            StopProcess("Node.js", _nodeServerProcess);
            StopProcess("UDP Receiver", _externalUdpReceiverProcess);

            _logger?.Info("Cleanup complete", "SHUTDOWN");
        }

        private void StopProcess(string name, Process? process)
        {
            if (process == null || process.HasExited) return;

            _logger?.Debug($"Stopping {name}...", "SHUTDOWN");

            try
            {
                process.CloseMainWindow();
                if (!process.WaitForExit(5000))
                {
                    process.Kill(entireProcessTree: true);
                }
                process.Dispose();
            }
            catch (Exception ex)
            {
                _logger?.Debug($"Error stopping {name}: {ex.Message}", "SHUTDOWN");
            }
        }

        private void PrintHelp()
        {
            Console.WriteLine(@"
BCI Gesture Control Orchestrator v2.0

Usage: BciOrchestrator [options]

Options:
  -h, --help          Show this help message
  -c, --config PATH   Load configuration from PATH
  -t, --test-mode     Run in test mode (interactive command injection)
  -d, --debug         Enable debug logging
  -p, --port PORT     WebSocket server port (default: 8080)
  --udp-port PORT     UDP receiver port (default: 7400)
  --allow-lan         Allow LAN connections (bind to 0.0.0.0)
  --use-node          Use external Node.js WebSocket server

Configuration:
  Edit appsettings.json to customize behavior

Example:
  BciOrchestrator --debug --test-mode
  BciOrchestrator --config production.json --allow-lan

For more information, see the README.md file.
");
        }
    }
}
