# BCI Gesture Control Orchestrator - Integrated Edition

A single-executable BCI (Brain-Computer Interface) orchestrator that combines UDP reception, WebSocket server, and keyboard simulation into one easy-to-use application.

## Features

✅ **All-in-One Executable** - No external dependencies needed  
✅ **UDP Receiver** - Listens for Emotiv BCI-OSC packets  
✅ **WebSocket Server** - Broadcasts brain events to Unity/browser clients  
✅ **Keyboard Simulation** - Optional virtual keyboard input (--keyboard flag)  
✅ **Advanced Filtering** - Debouncing, hysteresis, and rate limiting  
✅ **System Tray** - Runs in background with tray icon  
✅ **Health Monitoring** - Built-in metrics and logging  
✅ **Hot-Reload Config** - Edit appsettings.json without restart  

## Quick Start

### 1. Build the Application

**Windows:**
```batch
build.bat
```

**Manual Build:**
```bash
dotnet publish -c Release -r win-x64 --self-contained -p:PublishSingleFile=true -o ./Release
```

### 2. Configure Emotiv App

1. Open **Emotiv App**
2. Navigate to **Settings** → **OSC**
3. Enable **BCI-OSC** output
4. Set **Target IP**: `127.0.0.1`
5. Set **Target Port**: `7400`
6. Ensure **Mental Commands** are trained

### 3. Run the Orchestrator

**Basic Mode (no keyboard):**
```batch
cd Release
BCIOrchestrator.exe
```

**With Keyboard Simulation:**
```batch
BCIOrchestrator.exe --keyboard
```

**Allow LAN Connections:**
```batch
BCIOrchestrator.exe --allow-lan
```

**Debug Mode:**
```batch
BCIOrchestrator.exe --debug
```

### 4. Connect Your Client

**Unity Connection:**
```csharp
using UnityEngine;
using NativeWebSocket;

public class BCIClient : MonoBehaviour
{
    WebSocket websocket;

    async void Start()
    {
        websocket = new WebSocket("ws://127.0.0.1:8080/stream");
        
        websocket.OnMessage += (bytes) =>
        {
            var message = System.Text.Encoding.UTF8.GetString(bytes);
            var brainEvent = JsonUtility.FromJson<BrainEvent>(message);
            
            Debug.Log($"Action: {brainEvent.Action}, Confidence: {brainEvent.Confidence}");
            
            // Handle the action in your game
            HandleBrainAction(brainEvent.Action);
        };

        await websocket.Connect();
    }

    void Update()
    {
        #if !UNITY_WEBGL || UNITY_EDITOR
        websocket?.DispatchMessageQueue();
        #endif
    }

    async void OnDestroy()
    {
        await websocket?.Close();
    }
}

[System.Serializable]
public class BrainEvent
{
    public long Ts;
    public string Type;
    public string Action;
    public double Confidence;
    public int DurationMs;
}
```

**JavaScript/Browser Connection:**
```javascript
const ws = new WebSocket('ws://127.0.0.1:8080/stream');

ws.onmessage = (event) => {
    const brainEvent = JSON.parse(event.data);
    console.log('Brain Action:', brainEvent.action, 'Confidence:', brainEvent.confidence);
    
    // Handle the action
    handleBrainAction(brainEvent.action);
};

ws.onopen = () => console.log('Connected to BCI Orchestrator');
ws.onerror = (error) => console.error('WebSocket error:', error);
```

## Configuration

Edit `appsettings.json` to customize behavior:

```json
{
  "WebSocket": {
    "Port": 8080,
    "Host": "127.0.0.1"
  },
  "UdpReceiver": {
    "Port": 7400,
    "Thresholds": {
      "OnThreshold": 0.6,
      "OffThreshold": 0.5,
      "DebounceMs": 150,
      "RateHz": 15
    }
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
  "Keyboard": {
    "Enabled": false,
    "KeyMap": {
      "push": 87,    // W key
      "pull": 83,    // S key
      "left": 65,    // A key
      "right": 68,   // D key
      "lift": 32,    // Space
      "drop": 17     // Ctrl
    }
  }
}
```

### Configuration Options

| Setting | Description | Default |
|---------|-------------|---------|
| `WebSocket.Port` | WebSocket server port | 8080 |
| `UdpReceiver.Port` | UDP listening port | 7400 |
| `OnThreshold` | Confidence to activate action | 0.6 |
| `OffThreshold` | Confidence to deactivate action | 0.5 |
| `DebounceMs` | Stabilization time (ms) | 150 |
| `RateHz` | Max actions per second | 15 |
| `Keyboard.Enabled` | Enable keyboard simulation | false |

## How It Works

### Signal Flow

```
Emotiv BCI-OSC → UDP Receiver → Filtering → WebSocket Broadcast → Clients
                                     ↓
                              Keyboard Simulation (optional)
```

### Filtering Pipeline

1. **UDP Reception**: Receives OSC or CSV formatted brain commands
2. **Confidence Thresholding**: 
   - Action activates when confidence ≥ `OnThreshold`
   - Action deactivates when confidence < `OffThreshold`
3. **Debouncing**: Action must be stable for `DebounceMs` milliseconds
4. **Rate Limiting**: Maximum `RateHz` actions per second
5. **Hysteresis**: Prevents rapid on/off flickering

### Message Format

Brain events are broadcast as JSON:

```json
{
  "Ts": 1699564823456,
  "Type": "mental_command",
  "Action": "moveForward",
  "Confidence": 0.82,
  "DurationMs": 250,
  "Raw": {
    "original_action": "push"
  }
}
```

## API Endpoints

| Endpoint | Method | Description |
|----------|--------|-------------|
| `ws://localhost:8080/stream` | WebSocket | Brain event stream |
| `http://localhost:8080/healthz` | GET | Health check |
| `http://localhost:8080/state` | GET | Server state/metrics |

### Health Check Response

```json
{
  "status": "ok",
  "timestamp": 1699564823456
}
```

### State Response

```json
{
  "clients": 2,
  "uptime": 123.45,
  "timestamp": 1699564823456
}
```

## Troubleshooting

### No UDP Packets Received

1. Check Emotiv App is running and OSC is enabled
2. Verify target IP is `127.0.0.1` and port is `7400`
3. Check Windows Firewall isn't blocking UDP port 7400
4. Test with: `netstat -an | findstr 7400`

### WebSocket Connection Failed

1. Check port 8080 isn't in use: `netstat -an | findstr 8080`
2. Verify Windows Firewall allows the application
3. Try running as Administrator

### Keyboard Not Working

1. Ensure you started with `--keyboard` flag
2. Check the target application has focus
3. Verify key mappings in `appsettings.json`
4. Try running as Administrator

### Actions Are Jittery

Adjust thresholds in `appsettings.json`:
- Increase `DebounceMs` (try 200-300ms)
- Widen threshold gap (e.g., OnThreshold=0.65, OffThreshold=0.45)
- Reduce `RateHz` (try 10-12 Hz)

### High Latency

- Decrease `DebounceMs` (minimum 50ms recommended)
- Increase `RateHz` (maximum 30 Hz recommended)
- Check network connection to Emotiv device

## System Requirements

- **OS**: Windows 10/11 (64-bit)
- **.NET**: .NET 8 Runtime (included in self-contained build)
- **Emotiv Device**: EPOC X, Insight, or compatible BCI headset
- **RAM**: 100 MB
- **Disk**: 50 MB

## Development

### Project Structure

```
BCIOrchestrator/
├── Program.cs              # Main application code
├── BCIOrchestrator.csproj  # Project file
├── appsettings.json        # Configuration
├── build.bat               # Build script
└── README.md               # This file
```

### Key Components

- **IntegratedUdpReceiver**: UDP packet reception and parsing
- **IntegratedWebSocketServer**: WebSocket server with HTTP endpoints
- **KeyboardSimulator**: Virtual keyboard input via Win32 API
- **SystemTrayManager**: Background system tray management
- **Logger**: Thread-safe logging system

### Building from Source

```bash
# Restore dependencies
dotnet restore

# Build debug version
dotnet build

# Run debug version
dotnet run

# Publish release
dotnet publish -c Release -r win-x64 --self-contained -p:PublishSingleFile=true
```

## Command-Line Options

```
BCIOrchestrator.exe [OPTIONS]

OPTIONS:
  --help, -h          Show help message
  --keyboard          Enable keyboard simulation
  --no-keyboard       Disable keyboard simulation (default)
  --allow-lan         Allow LAN connections (bind to 0.0.0.0)
  --debug             Enable debug logging
```

## Logging

Logs are written to `./logs/orchestrator.log`

**Log Levels:**
- `INFO`: Normal operation
- `WARN`: Warnings (e.g., client disconnects)
- `ERROR`: Errors (e.g., bind failures)
- `DEBUG`: Verbose details (--debug flag)

## Performance

**Typical Metrics:**
- Latency: 50-150ms (UDP → WebSocket)
- Throughput: Up to 30 actions/second
- Memory: ~50-100 MB
- CPU: <5% on modern hardware

## Security Notes

⚠️ **Default Configuration**: Binds to `127.0.0.1` (localhost only)

To allow remote connections:
```batch
BCIOrchestrator.exe --allow-lan
```

⚠️ **Warning**: This exposes the WebSocket server to your LAN. Only use on trusted networks.

## License

MIT License - See LICENSE file for details

## Support

For issues, questions, or feature requests:
1. Check the Troubleshooting section
2. Review logs in `./logs/orchestrator.log`
3. Open an issue on GitHub

## Changelog

### v2.0 (Integrated Edition)
- Combined all components into single executable
- Added system tray functionality
- Improved filtering pipeline
- Enhanced logging and metrics
- Hot-reload configuration support

### v1.0 (Multi-Process Edition)
- Initial release with separate processes
- Node.js WebSocket server
- Standalone UDP receiver

---

**Made with ❤️ for the BCI community**
