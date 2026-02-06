//==============================================================================
// BCI GESTURE CONTROL ORCHESTRATOR v2.1
// Fixed startup logging and test mode
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
        public string Level { get; set; } = "INFO";
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
        public bool UseIntegratedWebSocket { get; set; } = true;
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
    // SIMPLE CONSOLE LOGGER (with immediate flush)
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
        private readonly string _componentName;

        public Logger(LoggingConfig config, string componentName = "ORCHESTRATOR")
        {
            _config = config;
            _componentName = componentName;
            _logDirectory = Path.GetDirectoryName(config.LogFile) ?? "./logs";
            _currentLogDate = DateTime.Today;
            _currentLogFile = GetLogFileName();

            try
            {
                Directory.CreateDirectory(_logDirectory);
            }
            catch { }
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
                if (ex.StackTrace != null)
                    fullMessage += $"\n  StackTrace: {ex.StackTrace}";
            }

            lock (_lock)
            {
                if (_config.LogToConsole)
                {
                    ConsoleColor originalColor = Console.ForegroundColor;
                    Console.ForegroundColor = level switch
                    {
                        LogLevel.DEBUG => ConsoleColor.Gray,
                        LogLevel.INFO => ConsoleColor.White,
                        LogLevel.WARN => ConsoleColor.Yellow,
                        LogLevel.ERROR => ConsoleColor.Red,
                        _ => ConsoleColor.White
                    };
                    Console.WriteLine(fullMessage);
                    Console.ForegroundColor = originalColor;
                    Console.Out.Flush(); // Force immediate output
                }

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
                if (_config.RotateDaily && DateTime.Today != _currentLogDate)
                {
                    _currentLogDate = DateTime.Today;
                    _currentLogFile = GetLogFileName();
                }
                File.AppendAllText(_currentLogFile, message + Environment.NewLine);
            }
            catch { }
        }

        public void Debug(string message, string? context = null) => Log(LogLevel.DEBUG, message, context);
        public void Info(string message, string? context = null) => Log(LogLevel.INFO, message, context);
        public void Warn(string message, string? context = null, Exception? ex = null) => Log(LogLevel.WARN, message, context, ex);
        public void Error(string message, string? context = null, Exception? ex = null) => Log(LogLevel.ERROR, message, context, ex);

        public void LogStateChange(string from, string to, double confidence, string reason)
        {
            Info($"STATE CHANGE: {from} -> {to} (conf={confidence:F2}, reason={reason})", "STATE");
        }

        public void LogMetrics(OrchestratorStats stats)
        {
            Info($"METRICS: packets={stats.TotalPackets}, actions={stats.TotalActions}, clients={stats.ConnectedClients}", "METRICS");
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
    // INTEGRATED WEBSOCKET SERVER
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
        private long _messagesSent = 0;
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

            try
            {
                if (_config.WebSocket.AllowLAN || host == "0.0.0.0")
                {
                    _httpListener.Prefixes.Add($"http://+:{port}/");
                }
                else
                {
                    _httpListener.Prefixes.Add($"http://{host}:{port}/");
                }

                _httpListener.Start();
                _logger.Info($"WebSocket server started on ws://{host}:{port}/stream", "WS-SERVER");
            }
            catch (HttpListenerException ex)
            {
                _logger.Error($"Failed to start HTTP listener: {ex.Message}", "WS-SERVER", ex);
                throw;
            }

            _ = Task.Run(() => CleanupDeadClientsAsync(ct), ct);

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
                    _logger.Debug($"Error accepting request: {ex.Message}", "WS-SERVER");
                }
            }

            _httpListener.Stop();
        }

        private async Task HandleRequestAsync(HttpListenerContext context)
        {
            string path = context.Request.Url?.AbsolutePath ?? "/";
            string method = context.Request.HttpMethod;

            try
            {
                if (context.Request.IsWebSocketRequest && path == "/stream")
                {
                    await HandleWebSocketAsync(context);
                    return;
                }

                switch (path)
                {
                    case "/state":
                        if (method == "GET")
                            await HandleStateRequestAsync(context);
                        else
                            SendMethodNotAllowed(context);
                        break;

                    case "/healthz":
                        await SendTextResponseAsync(context, "ok");
                        break;

                    case "/broadcast":
                        if (method == "POST")
                            await HandleBroadcastRequestAsync(context);
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
                _logger.Debug($"Error handling request: {ex.Message}", "WS-SERVER");
                try { context.Response.StatusCode = 500; context.Response.Close(); } catch { }
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
                _logger.Debug($"WebSocket client connected", "WS-SERVER");

                lock (_clientsLock)
                {
                    _clients.Add(ws);
                }

                var buffer = new byte[_config.WebSocket.BufferSize];
                while (ws.State == WebSocketState.Open && !_ct.IsCancellationRequested)
                {
                    try
                    {
                        var result = await ws.ReceiveAsync(new ArraySegment<byte>(buffer), _ct);
                        if (result.MessageType == WebSocketMessageType.Close)
                            break;
                    }
                    catch
                    {
                        break;
                    }
                }

                if (ws.State == WebSocketState.Open || ws.State == WebSocketState.CloseReceived)
                {
                    try { await ws.CloseAsync(WebSocketCloseStatus.NormalClosure, "Closing", CancellationToken.None); } catch { }
                }
            }
            catch (Exception ex)
            {
                _logger.Debug($"WebSocket error: {ex.Message}", "WS-SERVER");
            }
            finally
            {
                if (wsContext != null)
                {
                    lock (_clientsLock) { _clients.Remove(wsContext.WebSocket); }
                    try { wsContext.WebSocket.Dispose(); } catch { }
                }
            }
        }

        private async Task HandleStateRequestAsync(HttpListenerContext context)
        {
            var state = _getStateSnapshot?.Invoke() ?? new StateSnapshot();
            string json = JsonSerializer.Serialize(state);
            await SendJsonResponseAsync(context, json);
        }

        private async Task HandleBroadcastRequestAsync(HttpListenerContext context)
        {
            using var reader = new StreamReader(context.Request.InputStream);
            string json = await reader.ReadToEndAsync();
            await BroadcastAsync(json);
            context.Response.StatusCode = 200;
            context.Response.Close();
        }

        private async Task SendInfoPageAsync(HttpListenerContext context)
        {
            string info = $"BCI Orchestrator WebSocket Server\nClients: {ConnectedClients}\nEndpoint: ws://127.0.0.1:{_config.WebSocket.Port}/stream";
            await SendTextResponseAsync(context, info);
        }

        private async Task SendJsonResponseAsync(HttpListenerContext context, string json)
        {
            byte[] body = Encoding.UTF8.GetBytes(json);
            context.Response.StatusCode = 200;
            context.Response.ContentType = "application/json";
            context.Response.ContentLength64 = body.Length;
            await context.Response.OutputStream.WriteAsync(body);
            context.Response.Close();
        }

        private async Task SendTextResponseAsync(HttpListenerContext context, string text)
        {
            byte[] body = Encoding.UTF8.GetBytes(text);
            context.Response.StatusCode = 200;
            context.Response.ContentType = "text/plain";
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

            foreach (var client in clientsCopy)
            {
                try
                {
                    await client.SendAsync(segment, WebSocketMessageType.Text, true, CancellationToken.None);
                    Interlocked.Increment(ref _messagesSent);
                }
                catch { }
            }
        }

        public async Task BroadcastAsync(BrainEvent brainEvent)
        {
            string json = JsonSerializer.Serialize(brainEvent);
            await BroadcastAsync(json);
        }

        private async Task CleanupDeadClientsAsync(CancellationToken ct)
        {
            while (!ct.IsCancellationRequested)
            {
                await Task.Delay(30000, ct);
                lock (_clientsLock)
                {
                    var dead = _clients.Where(c => c.State != WebSocketState.Open).ToList();
                    foreach (var d in dead)
                    {
                        _clients.Remove(d);
                        try { d.Dispose(); } catch { }
                    }
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
    // UDP RECEIVER (Integrated)
    //==========================================================================

    public class IntegratedUdpReceiver
    {
        private readonly Logger _logger;
        private readonly OrchestratorConfig _config;
        private UdpClient? _udpClient;
        private CancellationToken _ct;

        private string _activeAction = "neutral";
        private string _candidateAction = "neutral";
        private DateTime _candidateSince = DateTime.UtcNow;
        private double _lastConfidence = 0.0;
        private DateTime _activeSince = DateTime.UtcNow;
        private string _lastSource = "none";
        private DateTime _lastBroadcast = DateTime.MinValue;
        private string _lastBroadcastJson = "";

        private long _packetsReceived = 0;
        private long _stateChanges = 0;

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
            }
            catch (SocketException ex)
            {
                _logger.Error($"Failed to bind UDP port {port}: {ex.Message}", "UDP-RECV", ex);
                throw;
            }

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
                    _logger.Debug($"UDP error: {ex.Message}", "UDP-RECV");
                }
            }

            _udpClient.Close();
        }

        private async Task ProcessPacketAsync(byte[] buffer)
        {
            _packetsReceived++;

            string action;
            double confidence;

            if (TryParseOsc(buffer, out action, out confidence))
            {
                _lastSource = "osc";
            }
            else if (TryParseCsv(buffer, out action, out confidence))
            {
                _lastSource = "csv";
            }
            else
            {
                return;
            }

            _lastConfidence = confidence;
            await ApplyFiltersAndBroadcastAsync(action, confidence);
        }

        private bool TryParseOsc(byte[] buffer, out string action, out double confidence)
        {
            action = "";
            confidence = 0;

            try
            {
                if (buffer.Length < 8 || buffer[0] != '/') return false;

                int addressEnd = Array.IndexOf(buffer, (byte)0);
                if (addressEnd < 0) return false;

                string address = Encoding.ASCII.GetString(buffer, 0, addressEnd);
                string[] parts = address.Split('/');
                if (parts.Length >= 2)
                {
                    action = parts[^1].ToLowerInvariant();
                }

                int typeTagStart = ((addressEnd + 4) / 4) * 4;
                int dataStart = typeTagStart;
                while (dataStart < buffer.Length && buffer[dataStart] != 0) dataStart++;
                dataStart = ((dataStart + 4) / 4) * 4;

                if (dataStart + 4 <= buffer.Length)
                {
                    byte[] floatBytes = new byte[4];
                    Array.Copy(buffer, dataStart, floatBytes, 0, 4);
                    if (BitConverter.IsLittleEndian) Array.Reverse(floatBytes);
                    confidence = BitConverter.ToSingle(floatBytes, 0);
                }

                return !string.IsNullOrEmpty(action);
            }
            catch
            {
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
                    return false;

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

            string desired = _activeAction;
            if (confidence >= thresholds.OnThreshold && action != "neutral")
                desired = action;
            else if (confidence < thresholds.OffThreshold)
                desired = "neutral";

            if (desired != _candidateAction)
            {
                _candidateAction = desired;
                _candidateSince = DateTime.UtcNow;
            }
            else if ((DateTime.UtcNow - _candidateSince).TotalMilliseconds >= thresholds.DebounceMs)
            {
                if (_candidateAction != _activeAction)
                {
                    string prev = _activeAction;
                    _activeAction = _candidateAction;
                    _activeSince = DateTime.UtcNow;
                    _stateChanges++;
                    _logger.LogStateChange(prev, _activeAction, confidence, "debounced");
                }
            }

            int intervalMs = Math.Max(1, 1000 / thresholds.RateHz);
            if ((DateTime.UtcNow - _lastBroadcast).TotalMilliseconds < intervalMs)
                return;

            if (_activeAction != previousActive || (DateTime.UtcNow - _lastBroadcast).TotalSeconds > 1)
            {
                var brainEvent = new BrainEvent
                {
                    Ts = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                    Type = "mental_command",
                    Action = MapAction(_activeAction),
                    Confidence = confidence,
                    DurationMs = (int)(DateTime.UtcNow - _activeSince).TotalMilliseconds,
                    Source = _lastSource
                };

                string json = JsonSerializer.Serialize(brainEvent);
                if (json != _lastBroadcastJson)
                {
                    _lastBroadcastJson = json;
                    _lastBroadcast = DateTime.UtcNow;
                    if (OnBrainEvent != null)
                        await OnBrainEvent(brainEvent);
                }
            }
        }

        private string MapAction(string action)
        {
            return _config.ActionMap.TryGetValue(action, out string? mapped) ? mapped : action;
        }

        public void Stop()
        {
            _udpClient?.Close();
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
        private CancellationTokenSource _cts = new();
        private DateTime _startTime;
        private bool _isShuttingDown = false;

        public static async Task Main(string[] args)
        {
            // IMMEDIATE startup output
            Console.WriteLine();
            Console.WriteLine("╔═══════════════════════════════════════════════════════╗");
            Console.WriteLine("║     BCI GESTURE CONTROL ORCHESTRATOR v2.1             ║");
            Console.WriteLine("║     Starting up...                                    ║");
            Console.WriteLine("╚═══════════════════════════════════════════════════════╝");
            Console.WriteLine();
            Console.Out.Flush();

            Console.WriteLine($"[BOOT] Time: {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
            Console.WriteLine($"[BOOT] Args: {(args.Length > 0 ? string.Join(" ", args) : "(none)")}");
            Console.Out.Flush();

            var orchestrator = new BciOrchestrator();

            // Parse command line FIRST
            Console.WriteLine("[BOOT] Parsing command line arguments...");
            Console.Out.Flush();
            orchestrator.ParseArgs(args);

            Console.WriteLine($"[BOOT] Test Mode: {orchestrator._config.TestMode}");
            Console.WriteLine($"[BOOT] Log Level: {orchestrator._config.Logging.Level}");
            Console.Out.Flush();

            // Setup graceful shutdown
            Console.CancelKeyPress += (sender, e) =>
            {
                e.Cancel = true;
                Console.WriteLine("\n[SHUTDOWN] Received CTRL+C, shutting down...");
                orchestrator._isShuttingDown = true;
                orchestrator._cts.Cancel();
            };

            try
            {
                Console.WriteLine("[BOOT] Initializing...");
                Console.Out.Flush();

                if (!orchestrator.Initialize())
                {
                    Console.WriteLine("[BOOT] ✗ Initialization failed!");
                    return;
                }

                Console.WriteLine("[BOOT] ✓ Initialization complete");
                Console.Out.Flush();

                Console.WriteLine("[BOOT] Starting components...");
                Console.Out.Flush();

                if (!await orchestrator.StartAsync())
                {
                    Console.WriteLine("[BOOT] ✗ Failed to start components!");
                    orchestrator.Cleanup();
                    return;
                }

                Console.WriteLine("[BOOT] ✓ All components started");
                Console.Out.Flush();

                // Run main loop (or test mode)
                await orchestrator.RunAsync();
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[ERROR] Fatal: {ex.Message}");
                Console.WriteLine(ex.StackTrace);
            }
            finally
            {
                orchestrator.Cleanup();
                Console.WriteLine("[SHUTDOWN] Complete");
            }
        }

        private void ParseArgs(string[] args)
        {
            for (int i = 0; i < args.Length; i++)
            {
                string arg = args[i].ToLower();
                Console.WriteLine($"[BOOT]   Processing arg: {arg}");

                switch (arg)
                {
                    case "--help":
                    case "-h":
                        PrintHelp();
                        Environment.Exit(0);
                        break;

                    case "--test-mode":
                    case "-t":
                        _config.TestMode = true;
                        Console.WriteLine("[BOOT]   → Test mode ENABLED");
                        break;

                    case "--debug":
                    case "-d":
                        _config.Logging.Level = "DEBUG";
                        Console.WriteLine("[BOOT]   → Debug logging ENABLED");
                        break;

                    case "--allow-lan":
                        _config.WebSocket.AllowLAN = true;
                        _config.WebSocket.Host = "0.0.0.0";
                        break;

                    case "--port":
                    case "-p":
                        if (i + 1 < args.Length && int.TryParse(args[++i], out int port))
                            _config.WebSocket.Port = port;
                        break;

                    case "--udp-port":
                        if (i + 1 < args.Length && int.TryParse(args[++i], out int udpPort))
                            _config.UdpReceiver.Port = udpPort;
                        break;
                }
            }
        }

        private bool Initialize()
        {
            try
            {
                // Try to load config file
                if (File.Exists("./appsettings.json"))
                {
                    Console.WriteLine("[INIT] Loading appsettings.json...");
                    string json = File.ReadAllText("./appsettings.json");
                    var loaded = JsonSerializer.Deserialize<OrchestratorConfig>(json);
                    if (loaded != null)
                    {
                        // Preserve command-line overrides
                        bool testMode = _config.TestMode;
                        string logLevel = _config.Logging.Level;
                        _config = loaded;
                        _config.TestMode = testMode;
                        _config.Logging.Level = logLevel;
                    }
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[INIT] Warning: Config load failed: {ex.Message}");
            }

            // Create logger
            _logger = new Logger(_config.Logging);
            _startTime = DateTime.UtcNow;

            _logger.Info("═══════════════════════════════════════════════════════", "INIT");
            _logger.Info("  ORCHESTRATOR INITIALIZED", "INIT");
            _logger.Info("═══════════════════════════════════════════════════════", "INIT");
            _logger.Info($"  WebSocket Port: {_config.WebSocket.Port}", "INIT");
            _logger.Info($"  UDP Port: {_config.UdpReceiver.Port}", "INIT");
            _logger.Info($"  Test Mode: {_config.TestMode}", "INIT");
            _logger.Info($"  Log Level: {_config.Logging.Level}", "INIT");

            return true;
        }

        private async Task<bool> StartAsync()
        {
            // Start UDP receiver
            _udpReceiver = new IntegratedUdpReceiver(_logger, _config);
            _udpReceiver.OnBrainEvent += async (brainEvent) =>
            {
                if (_wsServer != null)
                    await _wsServer.BroadcastAsync(brainEvent);
            };

            _ = Task.Run(() => _udpReceiver.StartAsync(_cts.Token));
            await Task.Delay(300);
            _logger.LogStartup("UDP Receiver", true, $"Port {_config.UdpReceiver.Port}");

            // Start WebSocket server
            _wsServer = new IntegratedWebSocketServer(_logger, _config);
            _wsServer.SetStateProvider(GetStateSnapshot);

            _ = Task.Run(() => _wsServer.StartAsync(_cts.Token));
            await Task.Delay(300);
            _logger.LogStartup("WebSocket Server", true, $"Port {_config.WebSocket.Port}");

            _logger.Info("═══════════════════════════════════════════════════════", "STARTUP");
            _logger.Info("  ALL COMPONENTS STARTED", "STARTUP");
            _logger.Info("═══════════════════════════════════════════════════════", "STARTUP");

            return true;
        }

        private async Task RunAsync()
        {
            if (_config.TestMode)
            {
                // RUN TEST MODE IN FOREGROUND (this is the fix!)
                _logger.Info("═══════════════════════════════════════════════════════", "TEST");
                _logger.Info("  TEST MODE ACTIVE", "TEST");
                _logger.Info("  Type: action,confidence (e.g., push,0.85)", "TEST");
                _logger.Info("  Commands: sequence, status, help, quit", "TEST");
                _logger.Info("═══════════════════════════════════════════════════════", "TEST");

                await RunTestModeAsync();
            }
            else
            {
                _logger.Info("Running in normal mode. Press Ctrl+C to stop.", "MAIN");
                _logger.Info($"Connect Unity to: ws://127.0.0.1:{_config.WebSocket.Port}/stream", "MAIN");

                while (!_cts.Token.IsCancellationRequested)
                {
                    await Task.Delay(1000, _cts.Token);
                }
            }
        }

        private async Task RunTestModeAsync()
        {
            using var testUdp = new UdpClient();

            while (!_cts.Token.IsCancellationRequested)
            {
                Console.Write("\nTest> ");
                Console.Out.Flush();

                string? input = Console.ReadLine();

                if (string.IsNullOrWhiteSpace(input)) continue;

                input = input.Trim().ToLower();

                switch (input)
                {
                    case "quit":
                    case "exit":
                        _logger.Info("Exiting test mode...", "TEST");
                        _cts.Cancel();
                        return;

                    case "help":
                        PrintTestHelp();
                        continue;

                    case "status":
                        PrintStatus();
                        continue;

                    case "sequence":
                        await RunTestSequenceAsync(testUdp);
                        continue;
                }

                // Parse action,confidence
                var parts = input.Split(',');
                if (parts.Length != 2)
                {
                    Console.WriteLine("Invalid format. Use: action,confidence (e.g., push,0.85)");
                    continue;
                }

                if (!double.TryParse(parts[1].Trim(), out double confidence) || confidence < 0 || confidence > 1)
                {
                    Console.WriteLine("Confidence must be between 0.0 and 1.0");
                    continue;
                }

                await InjectCommandAsync(testUdp, parts[0].Trim(), confidence);
            }
        }

        private async Task InjectCommandAsync(UdpClient udp, string action, double confidence)
        {
            string payload = $"{action},{confidence:F2}";
            byte[] data = Encoding.UTF8.GetBytes(payload);

            try
            {
                await udp.SendAsync(data, data.Length, "127.0.0.1", _config.UdpReceiver.Port);
                Console.WriteLine($"✓ Sent: {payload}");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"✗ Error: {ex.Message}");
            }
        }

        private async Task RunTestSequenceAsync(UdpClient udp)
        {
            Console.WriteLine("\nRunning test sequence...");

            var sequence = new (string action, double conf, int delay)[]
            {
                ("neutral", 0.3, 500),
                ("push", 0.85, 1500),
                ("neutral", 0.3, 300),
                ("left", 0.75, 1500),
                ("neutral", 0.3, 300),
                ("right", 0.80, 1500),
                ("neutral", 0.3, 300),
                ("pull", 0.70, 1500),
                ("neutral", 0.3, 500)
            };

            foreach (var (action, conf, delay) in sequence)
            {
                await InjectCommandAsync(udp, action, conf);
                await Task.Delay(delay);
            }

            Console.WriteLine("Test sequence complete!");
        }

        private void PrintStatus()
        {
            Console.WriteLine();
            Console.WriteLine("╔═══════════════════════════════════════╗");
            Console.WriteLine("║           CURRENT STATUS              ║");
            Console.WriteLine("╠═══════════════════════════════════════╣");
            Console.WriteLine($"║  Active Action: {_udpReceiver?.ActiveAction ?? "neutral",-20} ║");
            Console.WriteLine($"║  Confidence:    {_udpReceiver?.LastConfidence ?? 0:F2,-20} ║");
            Console.WriteLine($"║  Source:        {_udpReceiver?.LastSource ?? "none",-20} ║");
            Console.WriteLine($"║  Packets:       {_udpReceiver?.PacketsReceived ?? 0,-20} ║");
            Console.WriteLine($"║  State Changes: {_udpReceiver?.StateChanges ?? 0,-20} ║");
            Console.WriteLine($"║  WS Clients:    {_wsServer?.ConnectedClients ?? 0,-20} ║");
            Console.WriteLine("╚═══════════════════════════════════════╝");
        }

        private void PrintTestHelp()
        {
            Console.WriteLine(@"
╔═══════════════════════════════════════════════════════════╗
║                    TEST COMMANDS                          ║
╠═══════════════════════════════════════════════════════════╣
║  push,0.85    - Send push command with 85% confidence     ║
║  pull,0.70    - Send pull command                         ║
║  left,0.75    - Send left command                         ║
║  right,0.80   - Send right command                        ║
║  lift,0.90    - Send lift command                         ║
║  neutral,0.30 - Return to neutral                         ║
║                                                           ║
║  sequence     - Run automated test sequence               ║
║  status       - Show current state                        ║
║  help         - Show this help                            ║
║  quit         - Exit test mode                            ║
╚═══════════════════════════════════════════════════════════╝
");
        }

        public StateSnapshot GetStateSnapshot()
        {
            return new StateSnapshot
            {
                Active = _udpReceiver?.ActiveAction ?? "neutral",
                Confidence = _udpReceiver?.LastConfidence ?? 0,
                DurationMs = _udpReceiver != null ? (int)(DateTime.UtcNow - _udpReceiver.ActiveSince).TotalMilliseconds : 0,
                Source = _udpReceiver?.LastSource ?? "none",
                Timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                UptimeMs = (long)(DateTime.UtcNow - _startTime).TotalMilliseconds,
                Stats = new OrchestratorStats
                {
                    TotalPackets = _udpReceiver?.PacketsReceived ?? 0,
                    TotalActions = _udpReceiver?.StateChanges ?? 0,
                    ConnectedClients = _wsServer?.ConnectedClients ?? 0
                }
            };
        }

        private void Cleanup()
        {
            _isShuttingDown = true;
            _cts.Cancel();
            _wsServer?.Stop();
            _udpReceiver?.Stop();
        }

        private void PrintHelp()
        {
            Console.WriteLine(@"
BCI Gesture Control Orchestrator v2.1

Usage: dotnet run -- [options]

Options:
  -h, --help        Show this help
  -t, --test-mode   Run in interactive test mode
  -d, --debug       Enable debug logging
  -p, --port PORT   WebSocket port (default: 8080)
  --udp-port PORT   UDP port (default: 7400)
  --allow-lan       Allow LAN connections

Examples:
  dotnet run -- --test-mode --debug
  dotnet run -- --port 9000
");
        }
    }
}