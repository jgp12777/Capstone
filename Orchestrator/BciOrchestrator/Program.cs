/*******************************************************************************
 * BCI GESTURE CONTROL SYSTEM ORCHESTRATOR (C#/.NET 8)
 * Main executable that coordinates:
 *   1. Node.js WebSocket Server (for Unity/browser communication)
 *   2. C# UDP Receiver (confidence processor with debouncing/filtering)
 *   3. Emotiv BCI-OSC Stream (mental command source)
 *   4. Action-to-Client Bridge (WebSocket broadcasts via IPC)
 ******************************************************************************/

//==============================================================================
// USING STATEMENTS
//==============================================================================
using System;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Collections.Generic;
using System.Collections.Concurrent;
using System.Linq;

//==============================================================================
// CONFIGURATION CLASSES (Deserialize from appsettings.json)
//==============================================================================

public class OrchestratorConfig
{
    public WebSocketConfig WebSocket { get; set; } = new();
    public UdpReceiverConfig UdpReceiver { get; set; } = new();
    public EmotivOscConfig EmotivOSC { get; set; } = new();
    public Dictionary<string, string> ActionMap { get; set; } = new();
    public LoggingConfig Logging { get; set; } = new();
    public HealthConfig Health { get; set; } = new();
}

public class WebSocketConfig
{
    public int Port { get; set; } = 8080;
    public string Host { get; set; } = "127.0.0.1";
    public bool AllowLAN { get; set; } = false;
    public int ReconnectTimeout { get; set; } = 5000;
}

public class UdpReceiverConfig
{
    public string Executable { get; set; } = "./UdpReceiver.exe";
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
    public bool RotateDaily { get; set; } = true;
}

public class HealthConfig
{
    public int HeartbeatIntervalMs { get; set; } = 3000;
    public int MaxRestarts { get; set; } = 3;
    public int RestartCooldownMs { get; set; } = 30000;
}

//==============================================================================
// MESSAGE STRUCTURES (JSON schema for WebSocket clients)
//==============================================================================

public class BrainEvent
{
    public long Ts { get; set; }                    // Timestamp (epoch ms)
    public string Type { get; set; } = "mental_command";
    public string Action { get; set; } = "neutral";
    public double Confidence { get; set; }
    public int DurationMs { get; set; }
    public Dictionary<string, string> Raw { get; set; } = new();
}

//==============================================================================
// MAIN ORCHESTRATOR CLASS
//==============================================================================

public class BciOrchestrator
{
    // Configuration
    private OrchestratorConfig _config;

    // Child processes
    private Process? _wsServerProcess;
    private Process? _udpReceiverProcess;

    // Process management
    private readonly Dictionary<string, int> _restartCounts = new();
    private readonly Dictionary<string, DateTime> _lastRestartAttempt = new();

    // State tracking
    private string _lastActiveAction = "neutral";
    private DateTime? _lastEmotivPacketTime;
    private bool _isShuttingDown = false;

    // Metrics
    private long _totalActionsSent = 0;
    private long _totalPacketsReceived = 0;
    private double _avgConfidence = 0.0;
    private DateTime _startTime;

    // Threading
    private CancellationTokenSource _cts = new();

    // WebSocket client connections (if we host our own WS server)
    private readonly ConcurrentBag<WebSocket> _wsClients = new();

    // IPC: Named pipe or HTTP endpoint to communicate with Node.js server
    private HttpClient? _nodeServerClient;

    //==========================================================================
    // ENTRY POINT
    //==========================================================================

    public static async Task Main(string[] args)
    {
        Console.WriteLine("========================================");
        Console.WriteLine("BCI GESTURE CONTROL ORCHESTRATOR v1.0");
        Console.WriteLine("========================================");
        Console.WriteLine();

        var orchestrator = new BciOrchestrator();

        // Parse command line arguments
        orchestrator.ParseArgs(args);

        // Setup graceful shutdown
        Console.CancelKeyPress += (sender, e) =>
        {
            e.Cancel = true;
            orchestrator.Log("Received CTRL+C, initiating shutdown...");
            orchestrator._isShuttingDown = true;
            orchestrator._cts.Cancel();
        };

        // Initialize and run
        if (!await orchestrator.InitializeAsync())
        {
            orchestrator.LogError("Initialization failed. Exiting.");
            return;
        }

        if (!await orchestrator.StartAllProcessesAsync())
        {
            orchestrator.LogError("Failed to start required processes.");
            await orchestrator.CleanupAsync();
            return;
        }

        // Main monitoring loop
        await orchestrator.RunMonitoringLoopAsync();

        // Cleanup on exit
        await orchestrator.CleanupAsync();
        orchestrator.Log("Orchestrator shutdown complete.");
    }

    //==========================================================================
    // INITIALIZATION
    //==========================================================================

    private void ParseArgs(string[] args)
    {
        for (int i = 0; i < args.Length; i++)
        {
            switch (args[i].ToLower())
            {
                case "--help":
                    PrintHelp();
                    Environment.Exit(0);
                    break;

                case "--config":
                    if (i + 1 < args.Length)
                    {
                        string configPath = args[++i];
                        LoadConfigFromFile(configPath);
                    }
                    break;

                case "--test-mode":
                    Log("Running in TEST MODE (no Emotiv required)");
                    _config.EmotivOSC.Enabled = false;
                    // Could start interactive test loop
                    break;

                case "--debug":
                    _config.Logging.Level = "DEBUG";
                    Log("Debug logging enabled");
                    break;

                case "--allow-lan":
                    _config.WebSocket.AllowLAN = true;
                    _config.WebSocket.Host = "0.0.0.0";
                    Log("LAN connections allowed");
                    break;
            }
        }
    }

    private async Task<bool> InitializeAsync()
    {
        Log("Initializing orchestrator...");

        // Load configuration (default or from file)
        try
        {
            _config = LoadConfiguration();
            Log("Configuration loaded successfully");
        }
        catch (Exception ex)
        {
            LogWarn($"Config load failed, using defaults: {ex.Message}");
            _config = new OrchestratorConfig();
        }

        // Validate executables exist
        if (!File.Exists(_config.UdpReceiver.Executable))
        {
            LogError($"UDP Receiver not found: {_config.UdpReceiver.Executable}");
            return false;
        }

        if (!File.Exists("./local_server.js"))
        {
            LogError("WebSocket server script not found: ./local_server.js");
            return false;
        }

        // Create log directory
        Directory.CreateDirectory("./logs/");

        // Initialize state
        _startTime = DateTime.UtcNow;
        _restartCounts["wsServer"] = 0;
        _restartCounts["udpReceiver"] = 0;

        // Setup HTTP client for Node.js IPC
        _nodeServerClient = new HttpClient
        {
            BaseAddress = new Uri($"http://{_config.WebSocket.Host}:{_config.WebSocket.Port}")
        };

        Log("Initialization complete");
        return true;
    }

    private OrchestratorConfig LoadConfiguration()
    {
        // Try to load from appsettings.json
        string configPath = "./appsettings.json";

        if (File.Exists(configPath))
        {
            string json = File.ReadAllText(configPath);
            return JsonSerializer.Deserialize<OrchestratorConfig>(json)
                   ?? new OrchestratorConfig();
        }

        return new OrchestratorConfig();
    }

    private void LoadConfigFromFile(string path)
    {
        string json = File.ReadAllText(path);
        _config = JsonSerializer.Deserialize<OrchestratorConfig>(json)
                  ?? new OrchestratorConfig();
    }

    //==========================================================================
    // PROCESS MANAGEMENT
    //==========================================================================

    private async Task<bool> StartAllProcessesAsync()
    {
        Log("Starting child processes...");

        bool success = true;

        // Start C# UDP Receiver FIRST (must be listening before data arrives)
        if (!await StartUdpReceiverAsync())
        {
            LogError("Failed to start UDP Receiver");
            success = false;
        }
        else
        {
            Log($"✓ UDP Receiver started on port {_config.UdpReceiver.Port}");
            await Task.Delay(1000); // Give time to bind
        }

        // Start Node.js WebSocket Server
        if (!await StartWebSocketServerAsync())
        {
            LogError("Failed to start WebSocket Server");
            success = false;
        }
        else
        {
            Log($"✓ WebSocket Server started on port {_config.WebSocket.Port}");
            await Task.Delay(500);
        }

        // Start Emotiv health monitor
        _ = Task.Run(() => MonitorEmotivHealthAsync(_cts.Token));

        return success;
    }

    //--------------------------------------------------------------------------
    // Start C# UDP Receiver Process
    //--------------------------------------------------------------------------
    private async Task<bool> StartUdpReceiverAsync()
    {
        Log("Launching C# UDP Receiver...");

        try
        {
            var startInfo = new ProcessStartInfo
            {
                FileName = _config.UdpReceiver.Executable,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
                WorkingDirectory = Directory.GetCurrentDirectory()
            };

            // Add command-line arguments for configuration
            // (Assumes UDP Receiver accepts these args)
            startInfo.ArgumentList.Add("--port");
            startInfo.ArgumentList.Add(_config.UdpReceiver.Port.ToString());
            startInfo.ArgumentList.Add("--on-threshold");
            startInfo.ArgumentList.Add(_config.UdpReceiver.Thresholds.OnThreshold.ToString());
            startInfo.ArgumentList.Add("--off-threshold");
            startInfo.ArgumentList.Add(_config.UdpReceiver.Thresholds.OffThreshold.ToString());
            startInfo.ArgumentList.Add("--debounce");
            startInfo.ArgumentList.Add(_config.UdpReceiver.Thresholds.DebounceMs.ToString());
            startInfo.ArgumentList.Add("--rate");
            startInfo.ArgumentList.Add(_config.UdpReceiver.Thresholds.RateHz.ToString());

            _udpReceiverProcess = Process.Start(startInfo);

            if (_udpReceiverProcess == null)
            {
                LogError("Failed to start UDP Receiver process");
                return false;
            }

            // Attach output handlers
            _udpReceiverProcess.OutputDataReceived += OnUdpReceiverOutput;
            _udpReceiverProcess.ErrorDataReceived += OnUdpReceiverError;
            _udpReceiverProcess.EnableRaisingEvents = true;
            _udpReceiverProcess.Exited += OnUdpReceiverExited;

            _udpReceiverProcess.BeginOutputReadLine();
            _udpReceiverProcess.BeginErrorReadLine();

            return true;
        }
        catch (Exception ex)
        {
            LogError($"Failed to start UDP Receiver: {ex.Message}");
            return false;
        }
    }

    private void OnUdpReceiverOutput(object sender, DataReceivedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(e.Data)) return;

        string line = e.Data.Trim();
        Log($"[UDP-RECV] {line}");

        // Parse action from C# output
        // Expected format: "active=push (candidate=push conf=0.82)"
        if (line.Contains("active="))
        {
            ParseAndBroadcastAction(line);
        }
    }

    private void OnUdpReceiverError(object sender, DataReceivedEventArgs e)
    {
        if (!string.IsNullOrWhiteSpace(e.Data))
        {
            LogError($"[UDP-RECV-ERR] {e.Data}");
        }
    }

    private void OnUdpReceiverExited(object? sender, EventArgs e)
    {
        int exitCode = _udpReceiverProcess?.ExitCode ?? -1;
        LogWarn($"UDP Receiver exited with code {exitCode}");

        if (!_isShuttingDown)
        {
            _ = Task.Run(() => AttemptProcessRestartAsync("udpReceiver"));
        }
    }

    //--------------------------------------------------------------------------
    // Start Node.js WebSocket Server Process
    //--------------------------------------------------------------------------
    private async Task<bool> StartWebSocketServerAsync()
    {
        Log("Launching WebSocket Server (Node.js)...");

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

            // Pass configuration via environment variables
            startInfo.Environment["WS_PORT"] = _config.WebSocket.Port.ToString();
            startInfo.Environment["WS_HOST"] = _config.WebSocket.Host;

            _wsServerProcess = Process.Start(startInfo);

            if (_wsServerProcess == null)
            {
                LogError("Failed to start WebSocket Server process");
                return false;
            }

            // Attach output handlers
            _wsServerProcess.OutputDataReceived += OnWsServerOutput;
            _wsServerProcess.ErrorDataReceived += OnWsServerError;
            _wsServerProcess.EnableRaisingEvents = true;
            _wsServerProcess.Exited += OnWsServerExited;

            _wsServerProcess.BeginOutputReadLine();
            _wsServerProcess.BeginErrorReadLine();

            return true;
        }
        catch (Exception ex)
        {
            LogError($"Failed to start WebSocket Server: {ex.Message}");
            return false;
        }
    }

    private void OnWsServerOutput(object sender, DataReceivedEventArgs e)
    {
        if (!string.IsNullOrWhiteSpace(e.Data))
        {
            Log($"[WS-SERVER] {e.Data}");
        }
    }

    private void OnWsServerError(object sender, DataReceivedEventArgs e)
    {
        if (!string.IsNullOrWhiteSpace(e.Data))
        {
            LogError($"[WS-SERVER-ERR] {e.Data}");
        }
    }

    private void OnWsServerExited(object? sender, EventArgs e)
    {
        int exitCode = _wsServerProcess?.ExitCode ?? -1;
        LogWarn($"WebSocket Server exited with code {exitCode}");

        if (!_isShuttingDown)
        {
            _ = Task.Run(() => AttemptProcessRestartAsync("wsServer"));
        }
    }

    //--------------------------------------------------------------------------
    // Process Restart Logic
    //--------------------------------------------------------------------------
    private async Task<bool> AttemptProcessRestartAsync(string processName)
    {
        int restartCount = _restartCounts[processName];

        if (restartCount >= _config.Health.MaxRestarts)
        {
            LogError($"Max restarts exceeded for {processName}. Manual intervention required.");
            return false;
        }

        // Check cooldown
        if (_lastRestartAttempt.TryGetValue(processName, out var lastAttempt))
        {
            var timeSinceLastRestart = DateTime.UtcNow - lastAttempt;
            if (timeSinceLastRestart.TotalMilliseconds < _config.Health.RestartCooldownMs)
            {
                LogWarn($"Restart cooldown active for {processName}, skipping...");
                return false;
            }
        }

        Log($"Attempting to restart {processName} (attempt {restartCount + 1}/{_config.Health.MaxRestarts})");

        _restartCounts[processName]++;
        _lastRestartAttempt[processName] = DateTime.UtcNow;

        await Task.Delay(_config.Health.RestartCooldownMs);

        bool success = processName switch
        {
            "udpReceiver" => await StartUdpReceiverAsync(),
            "wsServer" => await StartWebSocketServerAsync(),
            _ => false
        };

        if (success)
        {
            Log($"Successfully restarted {processName}");
            _restartCounts[processName] = 0; // Reset counter on success
        }
        else
        {
            LogError($"Failed to restart {processName}");
        }

        return success;
    }

    //==========================================================================
    // ACTION PARSING & BROADCASTING
    //==========================================================================

    private void ParseAndBroadcastAction(string line)
    {
        try
        {
            // Parse: "active=push (candidate=push conf=0.82)"
            var activeMatch = Regex.Match(line, @"active=(\w+)");
            if (!activeMatch.Success) return;

            string action = activeMatch.Groups[1].Value;

            // Extract confidence
            var confMatch = Regex.Match(line, @"conf=([\d.]+)");
            double confidence = confMatch.Success
                ? double.Parse(confMatch.Groups[1].Value)
                : 0.0;

            // Deduplicate - only broadcast if action changed
            if (_lastActiveAction == action) return;

            _lastActiveAction = action;
            _lastEmotivPacketTime = DateTime.UtcNow;
            _totalPacketsReceived++;

            // Map to game action
            string mappedAction = _config.ActionMap.TryGetValue(action, out var mapped)
                ? mapped
                : action;

            // Build JSON message
            var brainEvent = new BrainEvent
            {
                Ts = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                Type = "mental_command",
                Action = mappedAction,
                Confidence = confidence,
                DurationMs = 0, // Could track duration separately
                Raw = new Dictionary<string, string>
                {
                    ["source"] = "emotiv-osc",
                    ["filtered"] = "true"
                }
            };

            // Broadcast to WebSocket clients
            _ = Task.Run(() => BroadcastToClientsAsync(brainEvent));

            // Update metrics
            _totalActionsSent++;
            _avgConfidence = (_avgConfidence * 0.9) + (confidence * 0.1);

            LogDebug($"Action broadcasted: {mappedAction} (conf={confidence:0.00})");
        }
        catch (Exception ex)
        {
            LogError($"Failed to parse action: {ex.Message}");
        }
    }

    //--------------------------------------------------------------------------
    // WebSocket Broadcasting (via Node.js IPC or direct)
    //--------------------------------------------------------------------------
    private async Task BroadcastToClientsAsync(BrainEvent brainEvent)
    {
        string json = JsonSerializer.Serialize(brainEvent);

        // OPTION 1: Direct WebSocket connections (if we host our own WS server)
        // foreach (var client in _wsClients.Where(c => c.State == WebSocketState.Open))
        // {
        //     try
        //     {
        //         var buffer = Encoding.UTF8.GetBytes(json);
        //         await client.SendAsync(
        //             new ArraySegment<byte>(buffer),
        //             WebSocketMessageType.Text,
        //             true,
        //             CancellationToken.None
        //         );
        //     }
        //     catch (Exception ex)
        //     {
        //         LogError($"Failed to send to client: {ex.Message}");
        //     }
        // }

        // OPTION 2: IPC to Node.js server via HTTP POST
        try
        {
            var content = new StringContent(json, Encoding.UTF8, "application/json");
            var response = await _nodeServerClient.PostAsync("/broadcast", content);

            if (!response.IsSuccessStatusCode)
            {
                LogWarn($"Node.js broadcast failed: {response.StatusCode}");
            }
        }
        catch (Exception ex)
        {
            LogWarn($"IPC to Node.js failed: {ex.Message}");
        }

        // OPTION 3: Named Pipe IPC (more efficient)
        // using var pipe = new NamedPipeClientStream(".", "BciPipe", PipeDirection.Out);
        // await pipe.ConnectAsync(1000);
        // var buffer = Encoding.UTF8.GetBytes(json);
        // await pipe.WriteAsync(buffer, 0, buffer.Length);
    }

    //==========================================================================
    // HEALTH MONITORING
    //==========================================================================

    private async Task MonitorEmotivHealthAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            await Task.Delay(_config.EmotivOSC.HealthCheckIntervalMs, ct);

            if (!_config.EmotivOSC.Enabled) continue;

            if (_lastEmotivPacketTime == null)
            {
                // No packets yet, could still be starting up
                continue;
            }

            var timeSinceLastPacket = DateTime.UtcNow - _lastEmotivPacketTime.Value;

            if (timeSinceLastPacket.TotalMilliseconds > _config.EmotivOSC.HealthCheckIntervalMs)
            {
                LogWarn($"⚠ No Emotiv data for {timeSinceLastPacket.TotalSeconds:0}s");
                LogWarn($"  Check Emotiv App → BCI-OSC settings:");
                LogWarn($"    - Target IP: 127.0.0.1");
                LogWarn($"    - Target Port: {_config.UdpReceiver.Port}");
                LogWarn($"    - OSC Output: Enabled");
            }
        }
    }

    //--------------------------------------------------------------------------
    // Main Monitoring Loop
    //--------------------------------------------------------------------------
    private async Task RunMonitoringLoopAsync()
    {
        Log("Entering monitoring loop...");

        int iteration = 0;

        while (!_isShuttingDown)
        {
            try
            {
                await Task.Delay(_config.Health.HeartbeatIntervalMs, _cts.Token);

                // Check process health
                CheckProcessHealth("udpReceiver", _udpReceiverProcess);
                CheckProcessHealth("wsServer", _wsServerProcess);

                // Periodic status log (every ~20 iterations = 1 minute)
                iteration++;
                if (iteration % 20 == 0)
                {
                    LogStatusSummary();
                }
            }
            catch (OperationCanceledException)
            {
                // Expected on shutdown
                break;
            }
            catch (Exception ex)
            {
                LogError($"Monitoring loop error: {ex.Message}");
            }
        }
    }

    private void CheckProcessHealth(string name, Process? process)
    {
        if (process == null || process.HasExited)
        {
            LogError($"{name} is not running!");

            if (!_isShuttingDown)
            {
                _ = Task.Run(() => AttemptProcessRestartAsync(name));
            }
        }
    }

    private void LogStatusSummary()
    {
        var uptime = DateTime.UtcNow - _startTime;

        Log("--- STATUS SUMMARY ---");
        Log($"Uptime: {uptime.TotalMinutes:0.0} min");
        Log($"Packets received: {_totalPacketsReceived}");
        Log($"Actions sent: {_totalActionsSent}");
        Log($"Avg confidence: {_avgConfidence:0.00}");
        Log($"Last action: {_lastActiveAction}");
        Log("---------------------");
    }

    //==========================================================================
    // CLEANUP
    //==========================================================================

    private async Task CleanupAsync()
    {
        Log("Cleaning up resources...");

        _isShuttingDown = true;
        _cts.Cancel();

        // Terminate child processes gracefully
        await TerminateProcessAsync("UDP Receiver", _udpReceiverProcess);
        await TerminateProcessAsync("WebSocket Server", _wsServerProcess);

        // Cleanup HTTP client
        _nodeServerClient?.Dispose();

        Log("Cleanup complete");
    }

    private async Task TerminateProcessAsync(string name, Process? process)
    {
        if (process == null || process.HasExited) return;

        Log($"Stopping {name}...");

        try
        {
            // Try graceful shutdown first
            process.CloseMainWindow();

            bool exited = process.WaitForExit(5000); // 5 second timeout

            if (!exited)
            {
                Log($"Force killing {name}");
                process.Kill(entireProcessTree: true);
                await Task.Delay(500);
            }

            process.Dispose();
        }
        catch (Exception ex)
        {
            LogError($"Error stopping {name}: {ex.Message}");
        }
    }

    //==========================================================================
    // LOGGING UTILITIES
    //==========================================================================

    private void Log(string message)
    {
        string timestamp = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff");
        string fullMessage = $"[{timestamp}] [INFO] {message}";

        Console.WriteLine(fullMessage);
        AppendToLogFile(fullMessage);
    }

    private void LogDebug(string message)
    {
        if (_config.Logging.Level != "DEBUG") return;

        string timestamp = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff");
        string fullMessage = $"[{timestamp}] [DEBUG] {message}";

        Console.WriteLine(fullMessage);
        AppendToLogFile(fullMessage);
    }

    private void LogWarn(string message)
    {
        string timestamp = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff");
        string fullMessage = $"[{timestamp}] [WARN] {message}";

        Console.ForegroundColor = ConsoleColor.Yellow;
        Console.WriteLine(fullMessage);
        Console.ResetColor();

        AppendToLogFile(fullMessage);
    }

    private void LogError(string message)
    {
        string timestamp = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff");
        string fullMessage = $"[{timestamp}] [ERROR] {message}";

        Console.ForegroundColor = ConsoleColor.Red;
        Console.WriteLine(fullMessage);
        Console.ResetColor();

        AppendToLogFile(fullMessage);
    }

    private void AppendToLogFile(string message)
    {
        try
        {
            string logPath = _config?.Logging.LogFile ?? "./logs/orchestrator.log";
            File.AppendAllText(logPath, message + Environment.NewLine);

            // TODO: Implement log rotation if file exceeds MaxLogSizeMB
        }
        catch
        {
            // Silently fail to avoid infinite error loop
        }
    }

    //==========================================================================
    // CLI UTILITIES
    //==========================================================================

    private void PrintHelp()
    {
        Console.WriteLine("BCI Gesture Control Orchestrator");
        Console.WriteLine("Usage: BciOrchestrator.exe [options]");
        Console.WriteLine();
        Console.WriteLine("Options:");
        Console.WriteLine("  --help              Show this help message");
        Console.WriteLine("  --config PATH       Load configuration from PATH");
        Console.WriteLine("  --test-mode         Run without Emotiv (manual testing)");
        Console.WriteLine("  --debug             Enable debug logging");
        Console.WriteLine("  --allow-lan         Allow LAN connections (default: localhost only)");
        Console.WriteLine();
        Console.WriteLine("Configuration:");
        Console.WriteLine("  Edit appsettings.json to adjust ports, thresholds, and action mappings");
        Console.WriteLine();
        Console.WriteLine("Example appsettings.json:");
        Console.WriteLine(@"  {
    ""WebSocket"": { ""Port"": 8080, ""Host"": ""127.0.0.1"" },
    ""UdpReceiver"": {
      ""Port"": 7400,
      ""Thresholds"": {
        ""OnThreshold"": 0.6,
        ""OffThreshold"": 0.5,
        ""DebounceMs"": 150,
        ""RateHz"": 15
      }
    },
    ""ActionMap"": {
      ""push"": ""moveForward"",
      ""pull"": ""moveBackward"",
      ""left"": ""turnLeft"",
      ""right"": ""turnRight"",
      ""lift"": ""jump"",
      ""neutral"": ""idle""
    }
  }");
    }
}

//==============================================================================
// ALTERNATIVE: INTEGRATED WEBSOCKET SERVER (Option instead of Node.js)
//==============================================================================
// If you want to host WebSocket server directly in C# instead of using Node.js

public class IntegratedWebSocketServer
{
    private HttpListener? _httpListener;
    private readonly List<WebSocket> _clients = new();
    private readonly BciOrchestrator _orchestrator;
    private CancellationToken _ct;

    public IntegratedWebSocketServer(BciOrchestrator orchestrator)
    {
        _orchestrator = orchestrator;
    }

    public async Task StartAsync(string host, int port, CancellationToken ct)
    {
        _ct = ct;

        _httpListener = new HttpListener();
        _httpListener.Prefixes.Add($"http://{host}:{port}/");
        _httpListener.Start();

        _orchestrator.Log($"Integrated WebSocket Server listening on {host}:{port}");

        while (!ct.IsCancellationRequested)
        {
            try
            {
                var context = await _httpListener.GetContextAsync();

                if (context.Request.IsWebSocketRequest)
                {
                    _ = Task.Run(() => HandleWebSocketAsync(context));
                }
                else if (context.Request.Url?.AbsolutePath == "/broadcast")
                {
                    // IPC endpoint for broadcasting messages
                    await HandleBroadcastAsync(context);
                }
                else if (context.Request.Url?.AbsolutePath == "/state")
                {
                    // HTTP endpoint for current state
                    await HandleStateRequestAsync(context);
                }
                else
                {
                    context.Response.StatusCode = 404;
                    context.Response.Close();
                }
            }
            catch (Exception ex)
            {
                _orchestrator.LogError($"WebSocket server error: {ex.Message}");
            }
        }

        _httpListener.Stop();
    }

    private async Task HandleWebSocketAsync(HttpListenerContext context)
    {
        WebSocketContext wsContext;

        try
        {
            wsContext = await context.AcceptWebSocketAsync(null);
            _orchestrator.Log("WebSocket client connected");
        }
        catch (Exception ex)
        {
            _orchestrator.LogError($"WebSocket handshake failed: {ex.Message}");
            context.Response.StatusCode = 500;
            context.Response.Close();
            return;
        }

        var ws = wsContext.WebSocket;
        _clients.Add(ws);

        try
        {
            var buffer = new byte[8192];

            while (ws.State == WebSocketState.Open && !_ct.IsCancellationRequested)
            {
                var result = await ws.ReceiveAsync(
                    new ArraySegment<byte>(buffer),
                    _ct
                );

                if (result.MessageType == WebSocketMessageType.Close)
                {
                    await ws.CloseAsync(
                        WebSocketCloseStatus.NormalClosure,
                        "Closing",
                        CancellationToken.None
                    );
                    break;
                }

                // Echo or handle incoming messages if needed
                // string message = Encoding.UTF8.GetString(buffer, 0, result.Count);
            }
        }
        catch (Exception ex)
        {
            _orchestrator.LogWarn($"WebSocket client error: {ex.Message}");
        }
        finally
        {
            _clients.Remove(ws);
            ws.Dispose();
            _orchestrator.Log("WebSocket client disconnected");
        }
    }

    private async Task HandleBroadcastAsync(HttpListenerContext context)
    {
        // Read JSON payload from POST body
        using var reader = new StreamReader(context.Request.InputStream);
        string json = await reader.ReadToEndAsync();

        // Broadcast to all connected WebSocket clients
        var buffer = Encoding.UTF8.GetBytes(json);
        var tasks = _clients
            .Where(c => c.State == WebSocketState.Open)
            .Select(c => c.SendAsync(
                new ArraySegment<byte>(buffer),
                WebSocketMessageType.Text,
                true,
                CancellationToken.None
            ));

        await Task.WhenAll(tasks);

        // Send response
        context.Response.StatusCode = 200;
        context.Response.Close();
    }

    private async Task HandleStateRequestAsync(HttpListenerContext context)
    {
        // Return current state as JSON
        var state = new
        {
            timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            lastAction = _orchestrator._lastActiveAction,
            connectedClients = _clients.Count,
            uptime = (DateTime.UtcNow - _orchestrator._startTime).TotalSeconds
        };

        string json = JsonSerializer.Serialize(state);
        byte[] buffer = Encoding.UTF8.GetBytes(json);

        context.Response.ContentType = "application/json";
        context.Response.ContentLength64 = buffer.Length;
        await context.Response.OutputStream.WriteAsync(buffer, 0, buffer.Length);
        context.Response.Close();
    }

    public async Task BroadcastAsync(BrainEvent brainEvent)
    {
        string json = JsonSerializer.Serialize(brainEvent);
        var buffer = Encoding.UTF8.GetBytes(json);

        var tasks = _clients
            .Where(c => c.State == WebSocketState.Open)
            .Select(c => c.SendAsync(
                new ArraySegment<byte>(buffer),
                WebSocketMessageType.Text,
                true,
                CancellationToken.None
            ));

        try
        {
            await Task.WhenAll(tasks);
        }
        catch (Exception ex)
        {
            _orchestrator.LogError($"Broadcast error: {ex.Message}");
        }
    }
}

//==============================================================================
// TEST MODE: INTERACTIVE COMMAND INJECTION
//==============================================================================

public class TestCommandInterface
{
    private readonly BciOrchestrator _orchestrator;
    private UdpClient? _testUdp;

    public TestCommandInterface(BciOrchestrator orchestrator)
    {
        _orchestrator = orchestrator;
    }

    public async Task StartInteractiveLoopAsync(int targetPort)
    {
        _testUdp = new UdpClient();

        _orchestrator.Log("========================================");
        _orchestrator.Log("TEST MODE - Interactive Command Injection");
        _orchestrator.Log("========================================");
        _orchestrator.Log("Format: action,confidence");
        _orchestrator.Log("Examples:");
        _orchestrator.Log("  push,0.85");
        _orchestrator.Log("  left,0.72");
        _orchestrator.Log("  neutral,0.50");
        _orchestrator.Log("Commands: 'quit' or 'exit' to stop");
        _orchestrator.Log("========================================");
        _orchestrator.Log("");

        while (true)
        {
            Console.Write("Test> ");
            string? input = Console.ReadLine();

            if (string.IsNullOrWhiteSpace(input)) continue;

            input = input.Trim().ToLower();

            if (input == "quit" || input == "exit")
            {
                _orchestrator.Log("Exiting test mode...");
                break;
            }

            // Parse command
            var parts = input.Split(',');

            if (parts.Length != 2)
            {
                _orchestrator.LogWarn("Invalid format. Use: action,confidence");
                continue;
            }

            string action = parts[0].Trim();

            if (!double.TryParse(parts[1].Trim(), out double confidence))
            {
                _orchestrator.LogWarn("Invalid confidence value. Must be 0.0-1.0");
                continue;
            }

            if (confidence < 0 || confidence > 1)
            {
                _orchestrator.LogWarn("Confidence must be between 0.0 and 1.0");
                continue;
            }

            // Inject command
            await InjectCommandAsync(action, confidence, targetPort);
        }

        _testUdp.Close();
    }

    private async Task InjectCommandAsync(string action, double confidence, int port)
    {
        string payload = $"{action},{confidence:0.00}";
        byte[] data = Encoding.UTF8.GetBytes(payload);

        try
        {
            await _testUdp!.SendAsync(data, data.Length, "127.0.0.1", port);
            _orchestrator.Log($"✓ Injected: {payload}");
        }
        catch (Exception ex)
        {
            _orchestrator.LogError($"Injection failed: {ex.Message}");
        }
    }

    // Automated test sequences
    public async Task RunAutomatedTestSequenceAsync(int targetPort)
    {
        _testUdp = new UdpClient();

        _orchestrator.Log("Running automated test sequence...");

        var testSequence = new[]
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

        foreach (var (action, confidence, durationMs) in testSequence)
        {
            await InjectCommandAsync(action, confidence, targetPort);
            await Task.Delay(durationMs);
        }

        _orchestrator.Log("Automated test sequence complete");
        _testUdp.Close();
    }
}

//==============================================================================
// SYSTEM TRAY INTEGRATION (Optional Windows GUI)
//==============================================================================

#if WINDOWS
using System.Windows.Forms;

public class SystemTrayManager : IDisposable
{
    private NotifyIcon? _trayIcon;
    private readonly BciOrchestrator _orchestrator;
    
    public SystemTrayManager(BciOrchestrator orchestrator)
    {
        _orchestrator = orchestrator;
    }
    
    public void Initialize()
    {
        _trayIcon = new NotifyIcon
        {
            Icon = SystemIcons.Application,
            Visible = true,
            Text = "BCI Orchestrator"
        };
        
        // Create context menu
        var contextMenu = new ContextMenuStrip();
        
        contextMenu.Items.Add("Status", null, OnShowStatus);
        contextMenu.Items.Add("Test Command", null, OnTestCommand);
        contextMenu.Items.Add("-"); // Separator
        contextMenu.Items.Add("Exit", null, OnExit);
        
        _trayIcon.ContextMenuStrip = contextMenu;
        
        _orchestrator.Log("System tray icon initialized");
    }
    
    private void OnShowStatus(object? sender, EventArgs e)
    {
        var uptime = DateTime.UtcNow - _orchestrator._startTime;
        
        MessageBox.Show(
            $"BCI Orchestrator Status\n\n" +
            $"Uptime: {uptime.Hours}h {uptime.Minutes}m\n" +
            $"Last Action: {_orchestrator._lastActiveAction}\n" +
            $"Actions Sent: {_orchestrator._totalActionsSent}\n" +
            $"Avg Confidence: {_orchestrator._avgConfidence:0.00}",
            "Status",
            MessageBoxButtons.OK,
            MessageBoxIcon.Information
        );
    }
    
    private void OnTestCommand(object? sender, EventArgs e)
    {
        // Open simple dialog for manual command injection
        string? input = Prompt.ShowDialog("Enter test command (action,confidence):", "Test Command");
        
        if (string.IsNullOrWhiteSpace(input)) return;
        
        var parts = input.Split(',');
        if (parts.Length == 2 && double.TryParse(parts[1], out double conf))
        {
            // Inject via test interface
            _orchestrator.Log($"Manual test: {parts[0]},{conf}");
        }
    }
    
    private void OnExit(object? sender, EventArgs e)
    {
        _orchestrator._isShuttingDown = true;
        _orchestrator._cts.Cancel();
        Application.Exit();
    }
    
    public void Dispose()
    {
        _trayIcon?.Dispose();
    }
}

// Simple prompt dialog helper
public static class Prompt
{
    public static string? ShowDialog(string text, string caption)
    {
        Form prompt = new Form
        {
            Width = 400,
            Height = 150,
            FormBorderStyle = FormBorderStyle.FixedDialog,
            Text = caption,
            StartPosition = FormStartPosition.CenterScreen,
            MaximizeBox = false,
            MinimizeBox = false
        };
        
        Label textLabel = new Label { Left = 20, Top = 20, Text = text, Width = 340 };
        TextBox textBox = new TextBox { Left = 20, Top = 50, Width = 340 };
        Button confirmation = new Button { Text = "OK", Left = 240, Width = 100, Top = 80, DialogResult = DialogResult.OK };
        
        confirmation.Click += (sender, e) => { prompt.Close(); };
        
        prompt.Controls.Add(textLabel);
        prompt.Controls.Add(textBox);
        prompt.Controls.Add(confirmation);
        prompt.AcceptButton = confirmation;
        
        return prompt.ShowDialog() == DialogResult.OK ? textBox.Text : null;
    }
}
#endif

//==============================================================================
// PERFORMANCE METRICS & DIAGNOSTICS
//==============================================================================

public class PerformanceMonitor
{
    private readonly BciOrchestrator _orchestrator;
    private DateTime _lastReportTime;
    private long _lastPacketCount;
    private long _lastActionCount;

    public PerformanceMonitor(BciOrchestrator orchestrator)
    {
        _orchestrator = orchestrator;
        _lastReportTime = DateTime.UtcNow;
    }

    public void GenerateReport()
    {
        var now = DateTime.UtcNow;
        var elapsed = (now - _lastReportTime).TotalSeconds;

        if (elapsed < 60) return; // Report every minute

        long currentPackets = _orchestrator._totalPacketsReceived;
        long currentActions = _orchestrator._totalActionsSent;

        double packetsPerSecond = (currentPackets - _lastPacketCount) / elapsed;
        double actionsPerSecond = (currentActions - _lastActionCount) / elapsed;

        _orchestrator.Log("=== PERFORMANCE METRICS ===");
        _orchestrator.Log($"Packets/sec: {packetsPerSecond:0.00}");
        _orchestrator.Log($"Actions/sec: {actionsPerSecond:0.00}");
        _orchestrator.Log($"Avg Confidence: {_orchestrator._avgConfidence:0.00}");
        _orchestrator.Log($"Memory: {GC.GetTotalMemory(false) / 1024 / 1024} MB");
        _orchestrator.Log("==========================");

        _lastPacketCount = currentPackets;
        _lastActionCount = currentActions;
        _lastReportTime = now;
    }
}

//==============================================================================
// CONFIGURATION FILE EXAMPLE (appsettings.json)
//==============================================================================

/*
{
  "WebSocket": {
    "Port": 8080,
    "Host": "127.0.0.1",
    "AllowLAN": false,
    "ReconnectTimeout": 5000
  },
  "UdpReceiver": {
    "Executable": "./UdpReceiver.exe",
    "Port": 7400,
    "Thresholds": {
      "OnThreshold": 0.6,
      "OffThreshold": 0.5,
      "DebounceMs": 150,
      "RateHz": 15
    }
  },
  "EmotivOSC": {
    "Enabled": true,
    "ExpectedPort": 7400,
    "HealthCheckIntervalMs": 10000
  },
  "ActionMap": {
    "push": "moveForward",
    "pull": "moveBackward",
    "left": "turnLeft",
    "right": "turnRight",
    "lift": "jump",
    "drop": "crouch",
    "neutral": "idle"
  },
  "Logging": {
    "Level": "INFO",
    "LogFile": "./logs/orchestrator.log",
    "MaxLogSizeMB": 50,
    "RotateDaily": true
  },
  "Health": {
    "HeartbeatIntervalMs": 3000,
    "MaxRestarts": 3,
    "RestartCooldownMs": 30000
  }
}
*/

//==============================================================================
// DEPLOYMENT NOTES
//==============================================================================

/*
COMPILATION:
  dotnet publish -c Release -r win-x64 --self-contained -p:PublishSingleFile=true

DEPENDENCIES:
  - .NET 8 Runtime (or self-contained)
  - Node.js (for local_server.js)
  - UdpReceiver.exe (compiled from your C# UDP code)
  - Emotiv App with BCI-OSC enabled

DIRECTORY STRUCTURE:
  BciOrchestrator/
    ├── BciOrchestrator.exe        (this orchestrator)
    ├── UdpReceiver.exe             (your UDP confidence processor)
    ├── local_server.js             (Node.js WebSocket server)
    ├── appsettings.json            (configuration)
    ├── package.json                (Node.js dependencies)
    └── logs/                       (auto-created)

RUNNING:
  1. Configure Emotiv App BCI-OSC:
     - Target IP: 127.0.0.1
     - Target Port: 7400
     - Enable OSC Output
  
  2. Start orchestrator:
     > BciOrchestrator.exe
     
  3. Optional flags:
     > BciOrchestrator.exe --debug
     > BciOrchestrator.exe --test-mode
     > BciOrchestrator.exe --config custom.json

TESTING WITHOUT EMOTIV:
  > BciOrchestrator.exe --test-mode
  Test> push,0.85
  Test> left,0.72
  Test> quit

UNITY INTEGRATION:
  Unity connects to: ws://127.0.0.1:8080/stream
  Receives JSON: {"ts":..., "type":"mental_command", "action":"moveForward", ...}
*/