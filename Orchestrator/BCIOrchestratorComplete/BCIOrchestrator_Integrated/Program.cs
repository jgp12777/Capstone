/*******************************************************************************
 * BCI GESTURE CONTROL SYSTEM - INTEGRATED ORCHESTRATOR
 * Single-file executable combining:
 *   1. UDP Receiver (Emotiv BCI-OSC listener with confidence processing)
 *   2. WebSocket Server (for Unity/browser clients)
 *   3. Orchestration logic (coordination, health monitoring, logging)
 ******************************************************************************/

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
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using System.Drawing;

namespace BCIOrchestrator
{
    //==========================================================================
    // CONFIGURATION CLASSES
    //==========================================================================
    
    public class OrchestratorConfig
    {
        public WebSocketConfig WebSocket { get; set; } = new();
        public UdpReceiverConfig UdpReceiver { get; set; } = new();
        public EmotivOscConfig EmotivOSC { get; set; } = new();
        public Dictionary<string, string> ActionMap { get; set; } = new();
        public LoggingConfig Logging { get; set; } = new();
        public HealthConfig Health { get; set; } = new();
        public KeyboardConfig Keyboard { get; set; } = new();
    }

    public class WebSocketConfig
    {
        public int Port { get; set; } = 8080;
        public string Host { get; set; } = "127.0.0.1";
        public bool AllowLAN { get; set; } = false;
    }

    public class UdpReceiverConfig
    {
        public int Port { get; set; } = 7400;
        public ThresholdConfig Thresholds { get; set; } = new();
    }

    public class ThresholdConfig
    {
        public double OnThreshold { get; set; } = 0.6;
        public double OffThreshold { get; set; } = 0.5;
        public int DebounceMs { get; set; } = 150;
        public int RateHz { get; set; } = 15;
    }

    public class EmotivOscConfig
    {
        public bool Enabled { get; set; } = true;
        public int ExpectedPort { get; set; } = 7400;
        public int HealthCheckIntervalMs { get; set; } = 10000;
    }

    public class LoggingConfig
    {
        public string Level { get; set; } = "INFO";
        public string LogFile { get; set; } = "./logs/orchestrator.log";
        public int MaxLogSizeMB { get; set; } = 50;
    }

    public class HealthConfig
    {
        public int HeartbeatIntervalMs { get; set; } = 3000;
    }

    public class KeyboardConfig
    {
        public bool Enabled { get; set; } = false;
        public Dictionary<string, ushort> KeyMap { get; set; } = new()
        {
            ["push"] = 0x57,    // W
            ["pull"] = 0x53,    // S
            ["left"] = 0x41,    // A
            ["right"] = 0x44,   // D
            ["lift"] = 0x20,    // Space
            ["drop"] = 0x11     // Ctrl
        };
    }

    public class BrainEvent
    {
        public long Ts { get; set; }
        public string Type { get; set; } = "mental_command";
        public string Action { get; set; } = "neutral";
        public double Confidence { get; set; }
        public int DurationMs { get; set; }
        public Dictionary<string, string> Raw { get; set; } = new();
    }

    //==========================================================================
    // KEYBOARD INPUT SIMULATION
    //==========================================================================
    
    public static class KeyboardSimulator
    {
        [DllImport("user32.dll")]
        private static extern void keybd_event(byte bVk, byte bScan, uint dwFlags, UIntPtr dwExtraInfo);

        private const uint KEYEVENTF_KEYDOWN = 0x0000;
        private const uint KEYEVENTF_KEYUP = 0x0002;

        public static void PressKey(ushort keyCode)
        {
            keybd_event((byte)keyCode, 0, KEYEVENTF_KEYDOWN, UIntPtr.Zero);
        }

        public static void ReleaseKey(ushort keyCode)
        {
            keybd_event((byte)keyCode, 0, KEYEVENTF_KEYUP, UIntPtr.Zero);
        }
    }

    //==========================================================================
    // UDP RECEIVER (INTEGRATED)
    //==========================================================================
    
    public class IntegratedUdpReceiver
    {
        private readonly OrchestratorConfig _config;
        private readonly Action<string, double> _onActionDetected;
        private UdpClient? _udpClient;
        private bool _running;
        
        // State tracking for debouncing/filtering
        private string _active = "neutral";
        private string _candidate = "neutral";
        private DateTime _candidateSince = DateTime.UtcNow;
        private double _lastConfidence = 0.0;
        private DateTime _activeSince = DateTime.UtcNow;
        private DateTime _lastPrintAt = DateTime.MinValue;
        private string _lastPrintedLine = "";
        private int _intervalMs;

        public IntegratedUdpReceiver(OrchestratorConfig config, Action<string, double> onActionDetected)
        {
            _config = config;
            _onActionDetected = onActionDetected;
            _intervalMs = Math.Max(1, (int)Math.Round(1000.0 / config.UdpReceiver.Thresholds.RateHz));
        }

        public async Task StartAsync(CancellationToken ct)
        {
            _running = true;
            _udpClient = new UdpClient(_config.UdpReceiver.Port);
            
            Logger.Log($"UDP Receiver started on port {_config.UdpReceiver.Port}");
            Logger.Log($"Waiting for Emotiv BCI-OSC packets...");

            try
            {
                while (_running && !ct.IsCancellationRequested)
                {
                    var result = await _udpClient.ReceiveAsync(ct);
                    ProcessUdpPacket(result.Buffer);
                }
            }
            catch (OperationCanceledException)
            {
                Logger.Log("UDP Receiver stopped");
            }
            catch (Exception ex)
            {
                Logger.LogError($"UDP Receiver error: {ex.Message}");
            }
            finally
            {
                _udpClient?.Close();
            }
        }

        private void ProcessUdpPacket(byte[] buffer)
        {
            string text = Encoding.UTF8.GetString(buffer).Trim();
            
            // Parse OSC or CSV format
            string action = "neutral";
            double confidence = 0.0;

            if (text.Contains("/MentalCommand"))
            {
                // OSC format: /MentalCommand 1 push 0.82
                var match = Regex.Match(text, @"/MentalCommand\s+\d+\s+(\w+)\s+([\d.]+)");
                if (match.Success)
                {
                    action = match.Groups[1].Value;
                    confidence = double.Parse(match.Groups[2].Value, CultureInfo.InvariantCulture);
                }
            }
            else if (text.Contains(","))
            {
                // CSV format: push,0.82
                var parts = text.Split(',');
                if (parts.Length >= 2)
                {
                    action = parts[0].Trim();
                    if (double.TryParse(parts[1].Trim(), NumberStyles.Any, CultureInfo.InvariantCulture, out double conf))
                    {
                        confidence = conf;
                    }
                }
            }

            // Apply filtering and debouncing
            ApplyFilter(action, confidence);
        }

        private void ApplyFilter(string newAction, double confidence)
        {
            _lastConfidence = confidence;
            var now = DateTime.UtcNow;

            // Determine if this is a valid "ON" action
            bool isOn = confidence >= _config.UdpReceiver.Thresholds.OnThreshold && newAction != "neutral";
            
            // If current active action is still above OFF threshold, keep it
            if (_active != "neutral" && confidence >= _config.UdpReceiver.Thresholds.OffThreshold)
            {
                // Still active, no change needed
                return;
            }

            // Check if we have a new candidate
            if (newAction != _candidate)
            {
                _candidate = newAction;
                _candidateSince = now;
                return;
            }

            // Check if candidate has been stable long enough (debounce)
            var candidateDuration = (now - _candidateSince).TotalMilliseconds;
            if (candidateDuration < _config.UdpReceiver.Thresholds.DebounceMs)
            {
                return; // Not stable enough yet
            }

            // Candidate is stable, check if it should become active
            if (isOn && _candidate != _active)
            {
                _active = _candidate;
                _activeSince = now;
                EmitAction(_active, confidence);
            }
            else if (!isOn && _active != "neutral")
            {
                _active = "neutral";
                _activeSince = now;
                EmitAction("neutral", 0.0);
            }
        }

        private void EmitAction(string action, double confidence)
        {
            var now = DateTime.UtcNow;
            
            // Rate limiting
            if ((now - _lastPrintAt).TotalMilliseconds < _intervalMs)
            {
                return;
            }

            _lastPrintAt = now;
            var line = $"active={action} (conf={confidence:0.00})";
            
            if (line != _lastPrintedLine)
            {
                _lastPrintedLine = line;
                Logger.Log($"[UDP] {line}");
                _onActionDetected(action, confidence);
            }
        }

        public void Stop()
        {
            _running = false;
            _udpClient?.Close();
        }
    }

    //==========================================================================
    // WEBSOCKET SERVER (INTEGRATED)
    //==========================================================================
    
    public class IntegratedWebSocketServer
    {
        private readonly OrchestratorConfig _config;
        private HttpListener? _httpListener;
        private readonly List<WebSocket> _clients = new();
        private readonly object _clientsLock = new();
        private bool _running;

        public IntegratedWebSocketServer(OrchestratorConfig config)
        {
            _config = config;
        }

        public async Task StartAsync(CancellationToken ct)
        {
            _running = true;
            _httpListener = new HttpListener();
            
            string prefix = $"http://{_config.WebSocket.Host}:{_config.WebSocket.Port}/";
            _httpListener.Prefixes.Add(prefix);
            
            try
            {
                _httpListener.Start();
                Logger.Log($"WebSocket Server started at {prefix}");
                Logger.Log($"Clients can connect to: ws://{_config.WebSocket.Host}:{_config.WebSocket.Port}/stream");

                while (_running && !ct.IsCancellationRequested)
                {
                    var context = await _httpListener.GetContextAsync();
                    _ = Task.Run(() => HandleRequestAsync(context, ct), ct);
                }
            }
            catch (Exception ex)
            {
                if (_running)
                {
                    Logger.LogError($"WebSocket Server error: {ex.Message}");
                }
            }
            finally
            {
                _httpListener?.Stop();
            }
        }

        private async Task HandleRequestAsync(HttpListenerContext context, CancellationToken ct)
        {
            try
            {
                if (context.Request.Url?.AbsolutePath == "/stream" && context.Request.IsWebSocketRequest)
                {
                    await HandleWebSocketAsync(context, ct);
                }
                else if (context.Request.Url?.AbsolutePath == "/health" || context.Request.Url?.AbsolutePath == "/healthz")
                {
                    await HandleHealthCheckAsync(context);
                }
                else if (context.Request.Url?.AbsolutePath == "/state")
                {
                    await HandleStateAsync(context);
                }
                else
                {
                    context.Response.StatusCode = 404;
                    context.Response.Close();
                }
            }
            catch (Exception ex)
            {
                Logger.LogError($"Request handler error: {ex.Message}");
                try
                {
                    context.Response.StatusCode = 500;
                    context.Response.Close();
                }
                catch { }
            }
        }

        private async Task HandleWebSocketAsync(HttpListenerContext context, CancellationToken ct)
        {
            HttpListenerWebSocketContext wsContext;
            
            try
            {
                wsContext = await context.AcceptWebSocketAsync(null);
                Logger.Log("WebSocket client connected");
            }
            catch (Exception ex)
            {
                Logger.LogError($"WebSocket handshake failed: {ex.Message}");
                context.Response.StatusCode = 500;
                context.Response.Close();
                return;
            }

            var ws = wsContext.WebSocket;
            
            lock (_clientsLock)
            {
                _clients.Add(ws);
            }

            try
            {
                var buffer = new byte[8192];

                while (ws.State == WebSocketState.Open && !ct.IsCancellationRequested)
                {
                    var result = await ws.ReceiveAsync(new ArraySegment<byte>(buffer), ct);

                    if (result.MessageType == WebSocketMessageType.Close)
                    {
                        await ws.CloseAsync(WebSocketCloseStatus.NormalClosure, "Closing", CancellationToken.None);
                        break;
                    }
                }
            }
            catch (Exception ex)
            {
                Logger.LogWarn($"WebSocket client error: {ex.Message}");
            }
            finally
            {
                lock (_clientsLock)
                {
                    _clients.Remove(ws);
                }
                ws.Dispose();
                Logger.Log("WebSocket client disconnected");
            }
        }

        private async Task HandleHealthCheckAsync(HttpListenerContext context)
        {
            var response = new { status = "ok", timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() };
            var json = JsonSerializer.Serialize(response);
            var buffer = Encoding.UTF8.GetBytes(json);

            context.Response.StatusCode = 200;
            context.Response.ContentType = "application/json";
            context.Response.ContentLength64 = buffer.Length;
            await context.Response.OutputStream.WriteAsync(buffer);
            context.Response.Close();
        }

        private async Task HandleStateAsync(HttpListenerContext context)
        {
            int clientCount;
            lock (_clientsLock)
            {
                clientCount = _clients.Count;
            }

            var response = new
            {
                clients = clientCount,
                uptime = (DateTime.UtcNow - Program.StartTime).TotalSeconds,
                timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()
            };

            var json = JsonSerializer.Serialize(response);
            var buffer = Encoding.UTF8.GetBytes(json);

            context.Response.StatusCode = 200;
            context.Response.ContentType = "application/json";
            context.Response.ContentLength64 = buffer.Length;
            await context.Response.OutputStream.WriteAsync(buffer);
            context.Response.Close();
        }

        public async Task BroadcastAsync(string json)
        {
            var buffer = Encoding.UTF8.GetBytes(json);
            List<WebSocket> clientsCopy;

            lock (_clientsLock)
            {
                clientsCopy = new List<WebSocket>(_clients);
            }

            var tasks = clientsCopy
                .Where(c => c.State == WebSocketState.Open)
                .Select(async c =>
                {
                    try
                    {
                        await c.SendAsync(
                            new ArraySegment<byte>(buffer),
                            WebSocketMessageType.Text,
                            true,
                            CancellationToken.None
                        );
                    }
                    catch (Exception ex)
                    {
                        Logger.LogWarn($"Failed to send to client: {ex.Message}");
                    }
                });

            await Task.WhenAll(tasks);
        }

        public void Stop()
        {
            _running = false;
            _httpListener?.Stop();
            
            lock (_clientsLock)
            {
                foreach (var client in _clients.Where(c => c.State == WebSocketState.Open))
                {
                    try
                    {
                        client.CloseAsync(WebSocketCloseStatus.NormalClosure, "Server shutting down", CancellationToken.None).Wait(TimeSpan.FromSeconds(2));
                    }
                    catch { }
                }
                _clients.Clear();
            }
        }
    }

    //==========================================================================
    // LOGGER
    //==========================================================================
    
    public static class Logger
    {
        private static string? _logFilePath;
        private static readonly object _logLock = new();

        public static void Initialize(string logPath)
        {
            _logFilePath = logPath;
            Directory.CreateDirectory(Path.GetDirectoryName(logPath) ?? "./logs");
        }

        public static void Log(string message)
        {
            string timestamp = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff");
            string fullMessage = $"[{timestamp}] [INFO] {message}";
            Console.WriteLine(fullMessage);
            AppendToLogFile(fullMessage);
        }

        public static void LogDebug(string message)
        {
            string timestamp = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff");
            string fullMessage = $"[{timestamp}] [DEBUG] {message}";
            Console.ForegroundColor = ConsoleColor.Gray;
            Console.WriteLine(fullMessage);
            Console.ResetColor();
            AppendToLogFile(fullMessage);
        }

        public static void LogWarn(string message)
        {
            string timestamp = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff");
            string fullMessage = $"[{timestamp}] [WARN] {message}";
            Console.ForegroundColor = ConsoleColor.Yellow;
            Console.WriteLine(fullMessage);
            Console.ResetColor();
            AppendToLogFile(fullMessage);
        }

        public static void LogError(string message)
        {
            string timestamp = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff");
            string fullMessage = $"[{timestamp}] [ERROR] {message}";
            Console.ForegroundColor = ConsoleColor.Red;
            Console.WriteLine(fullMessage);
            Console.ResetColor();
            AppendToLogFile(fullMessage);
        }

        private static void AppendToLogFile(string message)
        {
            if (string.IsNullOrEmpty(_logFilePath)) return;

            try
            {
                lock (_logLock)
                {
                    File.AppendAllText(_logFilePath, message + Environment.NewLine);
                }
            }
            catch { }
        }
    }

    //==========================================================================
    // SYSTEM TRAY MANAGER
    //==========================================================================
    
    public class SystemTrayManager : IDisposable
    {
        private NotifyIcon? _trayIcon;
        private readonly Action _onExit;

        public SystemTrayManager(Action onExit)
        {
            _onExit = onExit;
            InitializeTrayIcon();
        }

        private void InitializeTrayIcon()
        {
            _trayIcon = new NotifyIcon
            {
                Icon = SystemIcons.Application,
                Visible = true,
                Text = "BCI Orchestrator"
            };

            var contextMenu = new ContextMenuStrip();
            contextMenu.Items.Add("Show Console", null, (s, e) => ShowConsole());
            contextMenu.Items.Add("Hide Console", null, (s, e) => HideConsole());
            contextMenu.Items.Add("-");
            contextMenu.Items.Add("Exit", null, (s, e) => _onExit());

            _trayIcon.ContextMenuStrip = contextMenu;
            _trayIcon.DoubleClick += (s, e) => ShowConsole();
        }

        [DllImport("kernel32.dll")]
        private static extern IntPtr GetConsoleWindow();

        [DllImport("user32.dll")]
        private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

        private const int SW_HIDE = 0;
        private const int SW_SHOW = 5;

        private void ShowConsole()
        {
            var handle = GetConsoleWindow();
            ShowWindow(handle, SW_SHOW);
        }

        private void HideConsole()
        {
            var handle = GetConsoleWindow();
            ShowWindow(handle, SW_HIDE);
        }

        public void Dispose()
        {
            _trayIcon?.Dispose();
        }
    }

    //==========================================================================
    // MAIN ORCHESTRATOR
    //==========================================================================
    
    public class Program
    {
        public static DateTime StartTime { get; private set; }
        
        private static OrchestratorConfig _config = new();
        private static IntegratedUdpReceiver? _udpReceiver;
        private static IntegratedWebSocketServer? _wsServer;
        private static SystemTrayManager? _trayManager;
        private static CancellationTokenSource _cts = new();
        
        // Metrics
        private static long _totalActionsSent = 0;
        private static long _totalPacketsReceived = 0;
        private static double _avgConfidence = 0.0;
        private static string _lastActiveAction = "neutral";
        private static DateTime _lastActionTime = DateTime.UtcNow;
        private static ushort _currentKeyDown = 0;

        [STAThread]
        public static async Task Main(string[] args)
        {
            Console.WriteLine("========================================");
            Console.WriteLine("BCI GESTURE CONTROL ORCHESTRATOR v2.0");
            Console.WriteLine("========================================");
            Console.WriteLine();

            StartTime = DateTime.UtcNow;

            // Parse arguments
            ParseArgs(args);

            // Load configuration
            LoadConfiguration();

            // Initialize logger
            Logger.Initialize(_config.Logging.LogFile);
            Logger.Log("Orchestrator starting...");

            // Setup graceful shutdown
            Console.CancelKeyPress += (sender, e) =>
            {
                e.Cancel = true;
                Logger.Log("Received CTRL+C, initiating shutdown...");
                _cts.Cancel();
            };

            // Initialize system tray
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            _trayManager = new SystemTrayManager(() =>
            {
                Logger.Log("Exit requested from system tray");
                _cts.Cancel();
            });

            // Start components
            await StartComponentsAsync();

            // Wait for shutdown signal
            try
            {
                await Task.Delay(Timeout.Infinite, _cts.Token);
            }
            catch (OperationCanceledException)
            {
                Logger.Log("Shutdown initiated");
            }

            // Cleanup
            await CleanupAsync();
            Logger.Log("Orchestrator shutdown complete");
        }

        private static void ParseArgs(string[] args)
        {
            foreach (var arg in args)
            {
                switch (arg.ToLower())
                {
                    case "--help":
                    case "-h":
                        PrintHelp();
                        Environment.Exit(0);
                        break;

                    case "--keyboard":
                    case "--keys":
                        _config.Keyboard.Enabled = true;
                        Logger.Log("Keyboard simulation enabled");
                        break;

                    case "--no-keyboard":
                    case "--no-keys":
                        _config.Keyboard.Enabled = false;
                        break;

                    case "--allow-lan":
                        _config.WebSocket.AllowLAN = true;
                        _config.WebSocket.Host = "0.0.0.0";
                        Logger.Log("LAN connections allowed");
                        break;

                    case "--debug":
                        _config.Logging.Level = "DEBUG";
                        Logger.Log("Debug logging enabled");
                        break;
                }
            }
        }

        private static void LoadConfiguration()
        {
            string configPath = "./appsettings.json";

            if (File.Exists(configPath))
            {
                try
                {
                    string json = File.ReadAllText(configPath);
                    _config = JsonSerializer.Deserialize<OrchestratorConfig>(json) ?? new OrchestratorConfig();
                    Logger.Log("Configuration loaded from appsettings.json");
                }
                catch (Exception ex)
                {
                    Logger.LogWarn($"Failed to load config, using defaults: {ex.Message}");
                }
            }
            else
            {
                Logger.Log("Using default configuration");
            }
        }

        private static async Task StartComponentsAsync()
        {
            Logger.Log("Starting components...");

            // Start UDP Receiver
            _udpReceiver = new IntegratedUdpReceiver(_config, OnActionDetected);
            _ = Task.Run(() => _udpReceiver.StartAsync(_cts.Token));
            await Task.Delay(500);

            // Start WebSocket Server
            _wsServer = new IntegratedWebSocketServer(_config);
            _ = Task.Run(() => _wsServer.StartAsync(_cts.Token));
            await Task.Delay(500);

            // Start health monitor
            _ = Task.Run(() => HealthMonitorLoopAsync(_cts.Token));

            Logger.Log("All components started successfully");
            Logger.Log($"WebSocket: ws://{_config.WebSocket.Host}:{_config.WebSocket.Port}/stream");
            Logger.Log($"UDP Listening: 0.0.0.0:{_config.UdpReceiver.Port}");
            Logger.Log($"Keyboard Mode: {(_config.Keyboard.Enabled ? "ENABLED" : "DISABLED")}");
            Logger.Log("System tray icon active (right-click for options)");
        }

        private static void OnActionDetected(string action, double confidence)
        {
            _totalPacketsReceived++;
            
            // Update metrics
            _avgConfidence = (_avgConfidence * (_totalPacketsReceived - 1) + confidence) / _totalPacketsReceived;

            // Map action
            string mappedAction = _config.ActionMap.TryGetValue(action, out var mapped) ? mapped : action;

            // Create brain event
            var brainEvent = new BrainEvent
            {
                Ts = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                Type = "mental_command",
                Action = mappedAction,
                Confidence = confidence,
                DurationMs = (int)(DateTime.UtcNow - _lastActionTime).TotalMilliseconds,
                Raw = new Dictionary<string, string>
                {
                    ["original_action"] = action
                }
            };

            _lastActiveAction = action;
            _lastActionTime = DateTime.UtcNow;
            _totalActionsSent++;

            // Broadcast to WebSocket clients
            var json = JsonSerializer.Serialize(brainEvent);
            _ = Task.Run(() => _wsServer?.BroadcastAsync(json));

            // Simulate keyboard if enabled
            if (_config.Keyboard.Enabled)
            {
                SimulateKeyboard(action);
            }

            Logger.LogDebug($"Action: {action} -> {mappedAction} ({confidence:0.00})");
        }

        private static void SimulateKeyboard(string action)
        {
            // Release previous key
            if (_currentKeyDown != 0)
            {
                KeyboardSimulator.ReleaseKey(_currentKeyDown);
                _currentKeyDown = 0;
            }

            // Press new key
            if (_config.Keyboard.KeyMap.TryGetValue(action, out var keyCode) && action != "neutral")
            {
                KeyboardSimulator.PressKey(keyCode);
                _currentKeyDown = keyCode;
            }
        }

        private static async Task HealthMonitorLoopAsync(CancellationToken ct)
        {
            var lastReport = DateTime.UtcNow;

            while (!ct.IsCancellationRequested)
            {
                await Task.Delay(_config.Health.HeartbeatIntervalMs, ct);

                var now = DateTime.UtcNow;
                if ((now - lastReport).TotalSeconds >= 30)
                {
                    Logger.Log("==========================");
                    Logger.Log($"Uptime: {(now - StartTime).TotalMinutes:0.0} min");
                    Logger.Log($"Packets received: {_totalPacketsReceived}");
                    Logger.Log($"Actions sent: {_totalActionsSent}");
                    Logger.Log($"Avg confidence: {_avgConfidence:0.00}");
                    Logger.Log($"Last action: {_lastActiveAction}");
                    Logger.Log($"Memory: {GC.GetTotalMemory(false) / 1024 / 1024} MB");
                    Logger.Log("==========================");

                    lastReport = now;
                }
            }
        }

        private static async Task CleanupAsync()
        {
            Logger.Log("Cleaning up...");

            // Release any held keys
            if (_currentKeyDown != 0)
            {
                KeyboardSimulator.ReleaseKey(_currentKeyDown);
            }

            // Stop components
            _udpReceiver?.Stop();
            _wsServer?.Stop();
            _trayManager?.Dispose();

            await Task.Delay(500);
        }

        private static void PrintHelp()
        {
            Console.WriteLine(@"
BCI GESTURE CONTROL ORCHESTRATOR

USAGE:
    BCIOrchestrator.exe [OPTIONS]

OPTIONS:
    --help, -h          Show this help message
    --keyboard          Enable keyboard simulation
    --no-keyboard       Disable keyboard simulation (default)
    --allow-lan         Allow LAN connections (bind to 0.0.0.0)
    --debug             Enable debug logging

CONFIGURATION:
    Edit appsettings.json to customize:
    - UDP port (default: 7400)
    - WebSocket port (default: 8080)
    - Thresholds and debounce settings
    - Action mappings
    - Keyboard key mappings

EMOTIV SETUP:
    1. Open Emotiv App
    2. Enable BCI-OSC output
    3. Set target IP: 127.0.0.1
    4. Set target port: 7400

CLIENT CONNECTION:
    Unity/Browser: ws://127.0.0.1:8080/stream
    Health check: http://127.0.0.1:8080/healthz
");
        }
    }
}
