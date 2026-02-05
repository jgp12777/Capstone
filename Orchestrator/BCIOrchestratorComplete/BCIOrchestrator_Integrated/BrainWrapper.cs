//==============================================================================
// BRAIN WRAPPER v2.0
// UDP → Filtered → WebSocket + Console + Keyboard
// Improved with extensive debugging and robust error handling
//==============================================================================

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;

//=============================================================================
// CONFIGURATION
//=============================================================================

var config = new WrapperConfig();

// Parse command line arguments
for (int i = 0; i < args.Length; i++)
{
    switch (args[i].ToLower())
    {
        case "--keys":
            config.KeyboardMode = true;
            break;
        case "--no-keys":
            config.KeyboardMode = false;
            break;
        case "--debug":
            config.LogLevel = "DEBUG";
            break;
        case "--port":
            if (i + 1 < args.Length && int.TryParse(args[++i], out int port))
                config.HttpPort = port;
            break;
        case "--udp-port":
            if (i + 1 < args.Length && int.TryParse(args[++i], out int udpPort))
                config.UdpPort = udpPort;
            break;
        case "--on-threshold":
            if (i + 1 < args.Length && double.TryParse(args[++i], out double onTh))
                config.OnThreshold = onTh;
            break;
        case "--off-threshold":
            if (i + 1 < args.Length && double.TryParse(args[++i], out double offTh))
                config.OffThreshold = offTh;
            break;
        case "--debounce":
            if (i + 1 < args.Length && int.TryParse(args[++i], out int debounce))
                config.DebounceMs = debounce;
            break;
        case "--rate":
            if (i + 1 < args.Length && int.TryParse(args[++i], out int rate))
                config.RateHz = rate;
            break;
        case "--help":
            PrintHelp();
            return;
    }
}

//=============================================================================
// LOGGER
//=============================================================================

var logger = new Logger(config.LogLevel);

//=============================================================================
// STATE VARIABLES
//=============================================================================

string activeAction = "neutral";
string candidateAction = "neutral";
DateTime candidateSince = DateTime.UtcNow;
double lastConfidence = 0.0;
DateTime activeSince = DateTime.UtcNow;
string lastSource = "none";
ushort currentKeyDown = 0;

// Rate limiting
int intervalMs = Math.Max(1, 1000 / config.RateHz);
DateTime lastBroadcast = DateTime.MinValue;
string lastBroadcastJson = "";

// WebSocket clients
var clients = new List<WebSocket>();
var clientsLock = new object();

// Metrics
long packetsReceived = 0;
long packetsProcessed = 0;
long stateChanges = 0;
DateTime startTime = DateTime.UtcNow;

// Cancellation
var cts = new CancellationTokenSource();

//=============================================================================
// STARTUP BANNER
//=============================================================================

logger.Info("STARTUP", "═══════════════════════════════════════════════════════");
logger.Info("STARTUP", "  BRAIN WRAPPER v2.0");
logger.Info("STARTUP", "  UDP → Filtered → WebSocket + Console");
logger.Info("STARTUP", "═══════════════════════════════════════════════════════");
logger.Info("STARTUP", $"UDP Input:        :{config.UdpPort}");
logger.Info("STARTUP", $"WebSocket:        ws://127.0.0.1:{config.HttpPort}/stream");
logger.Info("STARTUP", $"HTTP:             http://127.0.0.1:{config.HttpPort}/state");
logger.Info("STARTUP", $"On Threshold:     {config.OnThreshold}");
logger.Info("STARTUP", $"Off Threshold:    {config.OffThreshold}");
logger.Info("STARTUP", $"Debounce:         {config.DebounceMs}ms");
logger.Info("STARTUP", $"Rate Limit:       {config.RateHz}Hz");
logger.Info("STARTUP", $"Keyboard Mode:    {(config.KeyboardMode ? "ON (--keys)" : "OFF")}");
logger.Info("STARTUP", $"Log Level:        {config.LogLevel}");
logger.Info("STARTUP", "───────────────────────────────────────────────────────");

//=============================================================================
// GRACEFUL SHUTDOWN
//=============================================================================

Console.CancelKeyPress += (s, e) =>
{
    e.Cancel = true;
    logger.Info("SHUTDOWN", "Received CTRL+C, shutting down...");
    cts.Cancel();
};

//=============================================================================
// START HTTP/WEBSOCKET SERVER
//=============================================================================

var serverTask = Task.Run(async () => await StartHttpWebSocketServerAsync(cts.Token));

//=============================================================================
// UDP RECEIVER
//=============================================================================

using var udp = new UdpClient(config.UdpPort);
logger.Info("UDP", $"Listening on port {config.UdpPort}");

try
{
    while (!cts.Token.IsCancellationRequested)
    {
        try
        {
            if (udp.Available > 0)
            {
                var result = await udp.ReceiveAsync(cts.Token);
                await ProcessPacketAsync(result.Buffer);
            }
            else
            {
                await Task.Delay(1, cts.Token);
            }
        }
        catch (OperationCanceledException)
        {
            break;
        }
        catch (Exception ex)
        {
            logger.Error("UDP", $"Receive error: {ex.Message}", ex);
        }
    }
}
finally
{
    udp.Close();
    logger.Info("UDP", "Receiver stopped");
}

// Wait for server to stop
await serverTask;
logger.Info("SHUTDOWN", "Wrapper stopped");

//=============================================================================
// PACKET PROCESSING
//=============================================================================

async Task ProcessPacketAsync(byte[] buffer)
{
    packetsReceived++;
    
    string action;
    double confidence;

    // Try OSC parsing first
    if (TryParseOsc(buffer, out action, out confidence))
    {
        lastSource = "osc";
        logger.Debug("UDP", $"Parsed OSC: action={action}, conf={confidence:F2}");
    }
    // Fall back to CSV
    else if (TryParseCsv(buffer, out action, out confidence))
    {
        lastSource = "csv";
        logger.Debug("UDP", $"Parsed CSV: action={action}, conf={confidence:F2}");
    }
    else
    {
        logger.Debug("UDP", $"Failed to parse: {Encoding.UTF8.GetString(buffer)}");
        return;
    }

    lastConfidence = confidence;
    packetsProcessed++;

    await ApplyFiltersAndBroadcastAsync(action, confidence);
}

bool TryParseOsc(byte[] buffer, out string action, out double confidence)
{
    action = "";
    confidence = 0;

    try
    {
        if (buffer.Length < 8 || buffer[0] != '/') return false;

        int addressEnd = Array.IndexOf(buffer, (byte)0);
        if (addressEnd < 0) return false;

        string address = Encoding.ASCII.GetString(buffer, 0, addressEnd);
        logger.Debug("OSC", $"Address: {address}");

        string[] parts = address.Split('/');
        if (parts.Length >= 2)
        {
            action = parts[^1].ToLowerInvariant();
        }

        int typeTagStart = ((addressEnd + 4) / 4) * 4;
        int dataStart = typeTagStart;
        while (dataStart < buffer.Length && buffer[dataStart] != 0)
            dataStart++;
        dataStart = ((dataStart + 4) / 4) * 4;

        if (dataStart + 4 <= buffer.Length)
        {
            byte[] floatBytes = new byte[4];
            Array.Copy(buffer, dataStart, floatBytes, 0, 4);
            if (BitConverter.IsLittleEndian)
                Array.Reverse(floatBytes);
            confidence = BitConverter.ToSingle(floatBytes, 0);
        }

        return !string.IsNullOrEmpty(action);
    }
    catch (Exception ex)
    {
        logger.Debug("OSC", $"Parse error: {ex.Message}");
        return false;
    }
}

bool TryParseCsv(byte[] buffer, out string action, out double confidence)
{
    action = "";
    confidence = 0;

    try
    {
        string text = Encoding.UTF8.GetString(buffer).Trim();
        var parts = text.Split(',');
        if (parts.Length != 2) return false;

        action = parts[0].Trim().ToLowerInvariant();
        return double.TryParse(parts[1].Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out confidence);
    }
    catch
    {
        return false;
    }
}

//=============================================================================
// FILTER & BROADCAST
//=============================================================================

async Task ApplyFiltersAndBroadcastAsync(string action, double confidence)
{
    string previousActive = activeAction;

    // Determine desired state (hysteresis)
    string desired = activeAction;
    if (confidence >= config.OnThreshold && action != "neutral")
    {
        desired = action;
    }
    else if (confidence < config.OffThreshold)
    {
        desired = "neutral";
    }
    else if (activeAction != "neutral" && action == activeAction)
    {
        desired = activeAction;
    }

    // Debounce logic
    if (desired != candidateAction)
    {
        candidateAction = desired;
        candidateSince = DateTime.UtcNow;
        logger.Debug("FILTER", $"New candidate: {candidateAction} (conf={confidence:F2})");
    }
    else if ((DateTime.UtcNow - candidateSince).TotalMilliseconds >= config.DebounceMs)
    {
        if (candidateAction != activeAction)
        {
            string prev = activeAction;
            activeAction = candidateAction;
            activeSince = DateTime.UtcNow;
            stateChanges++;

            logger.Info("STATE", $"CHANGE: {prev} → {activeAction} (conf={confidence:F2})");

            // Update keyboard
            UpdateKeyboard(activeAction);
        }
    }

    // Rate limiting
    if ((DateTime.UtcNow - lastBroadcast).TotalMilliseconds < intervalMs)
        return;

    // Build and broadcast
    if (activeAction != previousActive || (DateTime.UtcNow - lastBroadcast).TotalSeconds > 1)
    {
        var brainEvent = new BrainEvent
        {
            Ts = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            Type = "mental_command",
            Action = activeAction,
            Confidence = confidence,
            DurationMs = (int)(DateTime.UtcNow - activeSince).TotalMilliseconds,
            Source = lastSource
        };

        string json = JsonSerializer.Serialize(brainEvent);

        if (json != lastBroadcastJson)
        {
            lastBroadcastJson = json;
            lastBroadcast = DateTime.UtcNow;
            await BroadcastAsync(json);
        }
    }
}

//=============================================================================
// HTTP/WEBSOCKET SERVER
//=============================================================================

async Task StartHttpWebSocketServerAsync(CancellationToken ct)
{
    var listener = new HttpListener();
    
    try
    {
        listener.Prefixes.Add($"http://127.0.0.1:{config.HttpPort}/");
        listener.Start();
        logger.Info("HTTP", $"Server listening on http://127.0.0.1:{config.HttpPort}/");
    }
    catch (HttpListenerException ex)
    {
        logger.Error("HTTP", $"Failed to start: {ex.Message}", ex);
        logger.Warn("HTTP", "Try running as Administrator or reserving the URL");
        return;
    }

    while (!ct.IsCancellationRequested)
    {
        try
        {
            var context = await listener.GetContextAsync();
            _ = Task.Run(() => HandleRequestAsync(context, ct), ct);
        }
        catch (HttpListenerException) when (ct.IsCancellationRequested)
        {
            break;
        }
        catch (Exception ex)
        {
            logger.Error("HTTP", $"Accept error: {ex.Message}", ex);
        }
    }

    listener.Stop();
    logger.Info("HTTP", "Server stopped");
}

async Task HandleRequestAsync(HttpListenerContext ctx, CancellationToken ct)
{
    string path = ctx.Request.RawUrl ?? "/";
    string method = ctx.Request.HttpMethod;
    
    logger.Debug("HTTP", $"{method} {path}");

    try
    {
        // WebSocket: /stream
        if (ctx.Request.IsWebSocketRequest && path == "/stream")
        {
            await HandleWebSocketAsync(ctx, ct);
            return;
        }

        // HTTP: /state
        if (method == "GET" && path == "/state")
        {
            var state = new StateSnapshot
            {
                Active = activeAction,
                Confidence = lastConfidence,
                DurationMs = (int)(DateTime.UtcNow - activeSince).TotalMilliseconds,
                Source = lastSource,
                Timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()
            };
            await SendJsonAsync(ctx, state);
            return;
        }

        // HTTP: /healthz
        if (method == "GET" && path == "/healthz")
        {
            await SendJsonAsync(ctx, new { status = "ok", timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() });
            return;
        }

        // HTTP: /metrics
        if (method == "GET" && path == "/metrics")
        {
            var metrics = new
            {
                packetsReceived,
                packetsProcessed,
                stateChanges,
                connectedClients = clients.Count(c => c.State == WebSocketState.Open),
                uptimeMs = (long)(DateTime.UtcNow - startTime).TotalMilliseconds
            };
            await SendJsonAsync(ctx, metrics);
            return;
        }

        // Default info page
        string info = $@"Brain Wrapper v2.0

Endpoints:
  ws://127.0.0.1:{config.HttpPort}/stream  - WebSocket stream
  GET /state   - Current state
  GET /healthz - Health check
  GET /metrics - Metrics

State: {activeAction} (conf={lastConfidence:F2})
Clients: {clients.Count(c => c.State == WebSocketState.Open)}
Packets: {packetsReceived}";

        byte[] body = Encoding.UTF8.GetBytes(info);
        ctx.Response.StatusCode = 200;
        ctx.Response.ContentType = "text/plain";
        await ctx.Response.OutputStream.WriteAsync(body);
        ctx.Response.Close();
    }
    catch (Exception ex)
    {
        logger.Error("HTTP", $"Request error: {ex.Message}", ex);
        try { ctx.Response.StatusCode = 500; ctx.Response.Close(); } catch { }
    }
}

async Task HandleWebSocketAsync(HttpListenerContext ctx, CancellationToken ct)
{
    WebSocketContext? wsContext = null;
    
    try
    {
        wsContext = await ctx.AcceptWebSocketAsync(subProtocol: null);
        var ws = wsContext.WebSocket;
        
        logger.Info("WS", "Client connected");
        
        lock (clientsLock)
        {
            clients.Add(ws);
        }

        // Send initial state
        var initialState = new StateSnapshot
        {
            Active = activeAction,
            Confidence = lastConfidence,
            DurationMs = (int)(DateTime.UtcNow - activeSince).TotalMilliseconds,
            Source = lastSource,
            Timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()
        };
        await SendToClientAsync(ws, JsonSerializer.Serialize(initialState));

        // Receive loop
        var buffer = new byte[1024];
        while (ws.State == WebSocketState.Open && !ct.IsCancellationRequested)
        {
            try
            {
                var result = await ws.ReceiveAsync(new ArraySegment<byte>(buffer), ct);
                if (result.MessageType == WebSocketMessageType.Close)
                    break;
                
                if (result.MessageType == WebSocketMessageType.Text)
                {
                    string msg = Encoding.UTF8.GetString(buffer, 0, result.Count);
                    logger.Debug("WS", $"Received: {msg}");
                    
                    if (msg == "ping")
                        await SendToClientAsync(ws, "pong");
                }
            }
            catch (WebSocketException)
            {
                break;
            }
        }

        if (ws.State == WebSocketState.Open)
        {
            await ws.CloseAsync(WebSocketCloseStatus.NormalClosure, "Closing", CancellationToken.None);
        }
    }
    catch (Exception ex)
    {
        logger.Warn("WS", $"Client error: {ex.Message}", ex);
    }
    finally
    {
        if (wsContext != null)
        {
            lock (clientsLock)
            {
                clients.Remove(wsContext.WebSocket);
            }
            try { wsContext.WebSocket.Dispose(); } catch { }
        }
        logger.Info("WS", $"Client disconnected (remaining: {clients.Count(c => c.State == WebSocketState.Open)})");
    }
}

async Task BroadcastAsync(string message)
{
    var buffer = Encoding.UTF8.GetBytes(message);
    var segment = new ArraySegment<byte>(buffer);
    
    List<WebSocket> toSend;
    lock (clientsLock)
    {
        toSend = clients.Where(c => c.State == WebSocketState.Open).ToList();
    }

    var tasks = toSend.Select(async client =>
    {
        try
        {
            await client.SendAsync(segment, WebSocketMessageType.Text, true, CancellationToken.None);
        }
        catch (Exception ex)
        {
            logger.Debug("WS", $"Send failed: {ex.Message}");
        }
    });

    await Task.WhenAll(tasks);
    logger.Debug("WS", $"Broadcasted to {toSend.Count} clients");
}

async Task SendToClientAsync(WebSocket ws, string message)
{
    if (ws.State != WebSocketState.Open) return;
    var buffer = Encoding.UTF8.GetBytes(message);
    await ws.SendAsync(new ArraySegment<byte>(buffer), WebSocketMessageType.Text, true, CancellationToken.None);
}

async Task SendJsonAsync(HttpListenerContext ctx, object data)
{
    string json = JsonSerializer.Serialize(data);
    byte[] body = Encoding.UTF8.GetBytes(json);
    ctx.Response.StatusCode = 200;
    ctx.Response.ContentType = "application/json";
    ctx.Response.ContentEncoding = Encoding.UTF8;
    await ctx.Response.OutputStream.WriteAsync(body);
    ctx.Response.Close();
}

//=============================================================================
// KEYBOARD EMULATION (Windows only)
//=============================================================================

void UpdateKeyboard(string newAction)
{
    if (!config.KeyboardMode) return;
    if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows)) return;

    ushort? newKey = newAction.ToLowerInvariant() switch
    {
        "push" => 0x57,  // W
        "pull" => 0x53,  // S
        "left" => 0x41,  // A
        "right" => 0x44, // D
        "lift" => 0x20,  // Space
        _ => null
    };

    // Release old key
    if (currentKeyDown != 0 && (!newKey.HasValue || newKey.Value != currentKeyDown))
    {
        SendKey(currentKeyDown, false);
        logger.Debug("KB", $"Released key 0x{currentKeyDown:X2}");
        currentKeyDown = 0;
    }

    // Press new key
    if (newKey.HasValue && newKey.Value != 0 && newKey.Value != currentKeyDown)
    {
        SendKey(newKey.Value, true);
        logger.Debug("KB", $"Pressed key 0x{newKey.Value:X2}");
        currentKeyDown = newKey.Value;
    }
}

void SendKey(ushort vk, bool down)
{
    if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows)) return;

    var input = new INPUT
    {
        type = 1, // KEYBOARD
        u = new InputUnion
        {
            ki = new KEYBDINPUT
            {
                wVk = vk,
                dwFlags = down ? 0u : 2u // KEYUP = 2
            }
        }
    };

    SendInput(1, new[] { input }, Marshal.SizeOf<INPUT>());
}

[DllImport("user32.dll")]
static extern uint SendInput(uint nInputs, INPUT[] pInputs, int cbSize);

[StructLayout(LayoutKind.Sequential)]
struct INPUT
{
    public uint type;
    public InputUnion u;
}

[StructLayout(LayoutKind.Explicit)]
struct InputUnion
{
    [FieldOffset(0)] public KEYBDINPUT ki;
}

[StructLayout(LayoutKind.Sequential)]
struct KEYBDINPUT
{
    public ushort wVk;
    public ushort wScan;
    public uint dwFlags;
    public uint time;
    public IntPtr dwExtraInfo;
}

//=============================================================================
// HELP
//=============================================================================

void PrintHelp()
{
    Console.WriteLine(@"
Brain Wrapper v2.0 - UDP → Filtered → WebSocket + Console

Usage: BrainWrapper [options]

Options:
  --keys              Enable keyboard emulation (WASD)
  --no-keys           Disable keyboard emulation (default)
  --debug             Enable debug logging
  --port PORT         HTTP/WebSocket port (default: 8080)
  --udp-port PORT     UDP listener port (default: 7400)
  --on-threshold N    On threshold (default: 0.6)
  --off-threshold N   Off threshold (default: 0.5)
  --debounce MS       Debounce time in ms (default: 150)
  --rate HZ           Rate limit in Hz (default: 15)
  --help              Show this help

Input Format:
  CSV: action,confidence (e.g., push,0.82)
  OSC: /path/action with float argument

Actions: push, pull, left, right, lift, drop, neutral
");
}

//=============================================================================
// TYPES
//=============================================================================

class WrapperConfig
{
    public double OnThreshold { get; set; } = 0.6;
    public double OffThreshold { get; set; } = 0.5;
    public int DebounceMs { get; set; } = 150;
    public int RateHz { get; set; } = 15;
    public int UdpPort { get; set; } = 7400;
    public int HttpPort { get; set; } = 8080;
    public bool KeyboardMode { get; set; } = false;
    public string LogLevel { get; set; } = "INFO";
}

class BrainEvent
{
    [JsonPropertyName("ts")] public long Ts { get; set; }
    [JsonPropertyName("type")] public string Type { get; set; } = "";
    [JsonPropertyName("action")] public string Action { get; set; } = "";
    [JsonPropertyName("confidence")] public double Confidence { get; set; }
    [JsonPropertyName("durationMs")] public int DurationMs { get; set; }
    [JsonPropertyName("source")] public string Source { get; set; } = "";
}

class StateSnapshot
{
    [JsonPropertyName("active")] public string Active { get; set; } = "";
    [JsonPropertyName("confidence")] public double Confidence { get; set; }
    [JsonPropertyName("durationMs")] public int DurationMs { get; set; }
    [JsonPropertyName("source")] public string Source { get; set; } = "";
    [JsonPropertyName("timestamp")] public long Timestamp { get; set; }
}

class Logger
{
    private readonly int _level;
    
    public Logger(string level)
    {
        _level = level.ToUpper() switch
        {
            "DEBUG" => 0,
            "INFO" => 1,
            "WARN" => 2,
            "ERROR" => 3,
            _ => 1
        };
    }

    private void Log(int level, string levelStr, string component, string message, ConsoleColor color)
    {
        if (level < _level) return;
        
        string timestamp = DateTime.Now.ToString("HH:mm:ss.fff");
        Console.ForegroundColor = color;
        Console.WriteLine($"[{timestamp}] [{levelStr,-5}] [{component}] {message}");
        Console.ResetColor();
    }

    public void Debug(string component, string message) => Log(0, "DEBUG", component, message, ConsoleColor.Gray);
    public void Info(string component, string message) => Log(1, "INFO", component, message, ConsoleColor.White);
    public void Warn(string component, string message, Exception? ex = null)
    {
        Log(2, "WARN", component, message, ConsoleColor.Yellow);
        if (ex != null && _level == 0) Console.WriteLine($"  {ex}");
    }
    public void Error(string component, string message, Exception? ex = null)
    {
        Log(3, "ERROR", component, message, ConsoleColor.Red);
        if (ex != null) Console.WriteLine($"  {ex}");
    }
}
