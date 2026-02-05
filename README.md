# 🧠 BCI Brain Game Capstone Project

A comprehensive Brain-Computer Interface (BCI) system for translating Emotiv mental commands into game controls via WebSocket.

## 📁 Recommended GitHub Project Structure

```
BrainGame-Capstone/
│
├── 📄 README.md                     # This file - project overview
├── 📄 LICENSE                       # MIT License
├── 📄 .gitignore                    # Git ignore rules
├── 📄 CONTRIBUTING.md               # Contribution guidelines
├── 📄 CHANGELOG.md                  # Version history
│
├── 📁 docs/                         # Documentation
│   ├── 📄 ARCHITECTURE.md           # System architecture overview
│   ├── 📄 SETUP.md                  # Detailed setup instructions
│   ├── 📄 API.md                    # WebSocket/HTTP API documentation
│   ├── 📄 TROUBLESHOOTING.md        # Common issues and solutions
│   └── 📁 diagrams/                 # Architecture diagrams
│       ├── 📄 system-flow.png
│       └── 📄 data-flow.mermaid
│
├── 📁 src/                          # Source code (main applications)
│   │
│   ├── 📁 Orchestrator/             # Central orchestration service
│   │   ├── 📁 BciOrchestrator/      # C# orchestrator project
│   │   │   ├── 📄 Program.cs        # Main orchestrator code
│   │   │   ├── 📄 BciOrchestrator.csproj
│   │   │   ├── 📄 appsettings.json  # Configuration
│   │   │   └── 📄 appsettings.Development.json
│   │   │
│   │   └── 📁 UdpReceiver/          # Standalone UDP receiver (optional)
│   │       ├── 📄 Program.cs
│   │       └── 📄 UdpReceiver.csproj
│   │
│   ├── 📁 Wrapper/                  # Standalone wrapper application
│   │   ├── 📄 BrainWrapper.cs       # Main wrapper code
│   │   └── 📄 BrainWrapper.csproj
│   │
│   ├── 📁 WebSocketServer/          # Node.js WebSocket server
│   │   ├── 📄 local_server.js       # Main server code
│   │   ├── 📄 package.json
│   │   └── 📄 package-lock.json
│   │
│   └── 📁 UnityClient/              # Unity game integration
│       ├── 📁 Assets/
│       │   ├── 📁 Scripts/
│       │   │   ├── 📄 BciWebSocketClient.cs
│       │   │   ├── 📄 BciEventHandler.cs
│       │   │   └── 📄 EmotivUnityItf.cs
│       │   └── 📁 Prefabs/
│       └── 📄 README.md
│
├── 📁 tests/                        # Test suites
│   ├── 📁 unit/                     # Unit tests
│   ├── 📁 integration/              # Integration tests
│   └── 📁 manual/                   # Manual test scripts
│       └── 📄 test_commands.txt     # Test command sequences
│
├── 📁 scripts/                      # Utility scripts
│   ├── 📄 build.ps1                 # Windows build script
│   ├── 📄 build.sh                  # Linux/Mac build script
│   ├── 📄 deploy.ps1                # Deployment script
│   └── 📄 test_sender.py            # UDP test sender
│
├── 📁 config/                       # Configuration templates
│   ├── 📄 appsettings.template.json
│   ├── 📄 emotiv-osc-config.json
│   └── 📄 production.json
│
└── 📁 releases/                     # Pre-built releases (git-ignored)
    └── 📄 .gitkeep
```

## 🏗️ System Architecture

```
┌─────────────────────────────────────────────────────────────────────────┐
│                          EMOTIV HEADSET                                  │
│                    (Mental Command Training)                             │
└───────────────────────────────┬─────────────────────────────────────────┘
                                │
                                │ OSC/UDP (Port 7400)
                                ▼
┌─────────────────────────────────────────────────────────────────────────┐
│                      BCI ORCHESTRATOR                                    │
│  ┌─────────────────┐  ┌─────────────────┐  ┌─────────────────────────┐ │
│  │  UDP Receiver   │→ │  Signal Filter  │→ │  WebSocket Broadcaster  │ │
│  │  - OSC Parser   │  │  - Hysteresis   │  │  - State Management     │ │
│  │  - CSV Parser   │  │  - Debounce     │  │  - Client Tracking      │ │
│  └─────────────────┘  │  - Rate Limit   │  │  - Health Monitoring    │ │
│                       └─────────────────┘  └─────────────────────────┘ │
└───────────────────────────────┬─────────────────────────────────────────┘
                                │
                                │ WebSocket (Port 8080)
                                ▼
┌─────────────────────────────────────────────────────────────────────────┐
│                         GAME CLIENTS                                     │
│  ┌──────────────────────┐  ┌──────────────────────┐                     │
│  │    Unity Game        │  │    Web Browser       │                     │
│  │  - Character Control │  │  - Debug Dashboard   │                     │
│  │  - Visual Feedback   │  │  - State Monitor     │                     │
│  └──────────────────────┘  └──────────────────────┘                     │
└─────────────────────────────────────────────────────────────────────────┘
```

## 📦 Components

### 1. **BCI Orchestrator** (C# / .NET 8)
The central service that manages all BCI signal processing and distribution.

**Features:**
- Integrated UDP receiver with OSC and CSV parsing
- Advanced signal filtering (hysteresis, debounce, rate limiting)
- Built-in WebSocket server (no Node.js dependency)
- Extensive logging and debugging support
- Health monitoring and auto-restart
- Test mode for development without Emotiv headset
- Configurable via JSON and command-line arguments

### 2. **Brain Wrapper** (C# / .NET 8)
A lightweight standalone wrapper for simple deployments.

**Features:**
- Same filtering capabilities as Orchestrator
- Optional keyboard emulation (WASD)
- Smaller footprint
- Single-purpose design

### 3. **WebSocket Server** (Node.js)
Alternative WebSocket server implementation.

**Features:**
- Pure JavaScript implementation
- Easy to modify and extend
- Cross-platform compatibility
- Useful for web-first deployments

### 4. **Unity Client**
Unity integration for game development.

**Features:**
- WebSocket client component
- Event-driven architecture
- Easy integration with existing games

## 🚀 Quick Start

### Prerequisites
- .NET 8 SDK
- Node.js 16+ (optional, for Node.js server)
- Emotiv App with BCI-OSC enabled
- Unity 2021.3+ (for game client)

### Installation

```bash
# Clone the repository
git clone https://github.com/yourusername/BrainGame-Capstone.git
cd BrainGame-Capstone

# Build the Orchestrator
cd src/Orchestrator/BciOrchestrator
dotnet build

# Run the Orchestrator
dotnet run

# Or build for release
dotnet publish -c Release -r win-x64 --self-contained
```

### Running the System

1. **Configure Emotiv App:**
   - Open Emotiv App
   - Go to Settings → BCI-OSC
   - Set Target IP: `127.0.0.1`
   - Set Target Port: `7400`
   - Enable OSC Output

2. **Start the Orchestrator:**
   ```bash
   # Normal mode
   ./BciOrchestrator

   # Debug mode
   ./BciOrchestrator --debug

   # Test mode (no Emotiv required)
   ./BciOrchestrator --test-mode --debug
   ```

3. **Connect Unity Client:**
   - Add `BciWebSocketClient` to your scene
   - Set WebSocket URL: `ws://127.0.0.1:8080/stream`
   - Start the game

## 🔧 Configuration

### appsettings.json

```json
{
  "WebSocket": {
    "Port": 8080,
    "Host": "127.0.0.1",
    "AllowLAN": false
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
    "neutral": "idle"
  },
  "Logging": {
    "Level": "INFO"
  }
}
```

### Command Line Options

```
--help, -h          Show help
--config, -c PATH   Load configuration from PATH
--test-mode, -t     Run in test mode
--debug, -d         Enable debug logging
--port, -p PORT     WebSocket port (default: 8080)
--udp-port PORT     UDP port (default: 7400)
--allow-lan         Allow LAN connections
--use-node          Use Node.js WebSocket server
```

## 📡 API Reference

### WebSocket Messages

**Connect:** `ws://127.0.0.1:8080/stream`

**Brain Event (Server → Client):**
```json
{
  "ts": 1704067200000,
  "type": "mental_command",
  "action": "moveForward",
  "confidence": 0.85,
  "durationMs": 1500,
  "source": "emotiv-osc"
}
```

**Commands (Client → Server):**
- `ping` → Server responds with `pong`
- `state` → Server responds with current state

### HTTP Endpoints

| Endpoint | Method | Description |
|----------|--------|-------------|
| `/state` | GET | Current state JSON |
| `/healthz` | GET | Health check |
| `/metrics` | GET | Server metrics |
| `/broadcast` | POST | Broadcast message to clients |

## 🧪 Testing

### Test Mode
```bash
# Start in test mode
./BciOrchestrator --test-mode --debug

# Available commands:
# push,0.85    - Simulate push action
# left,0.72    - Simulate left action
# sequence     - Run automated test sequence
# status       - Show current state
# help         - Show help
# quit         - Exit
```

### UDP Test Sender (Python)
```python
import socket
import time

sock = socket.socket(socket.AF_INET, socket.SOCK_DGRAM)

commands = [
    ("push", 0.85),
    ("left", 0.72),
    ("neutral", 0.3),
]

for action, conf in commands:
    message = f"{action},{conf}"
    sock.sendto(message.encode(), ("127.0.0.1", 7400))
    time.sleep(1)
```

## 📋 Development Guidelines

### Code Style
- Use meaningful variable names
- Add XML documentation comments
- Follow C# naming conventions
- Include error handling for all I/O operations

### Git Workflow
1. Create feature branch from `develop`
2. Make changes with descriptive commits
3. Run tests before pushing
4. Create PR to `develop`
5. Merge to `main` for releases

### Commit Message Format
```
type(scope): description

[optional body]

[optional footer]
```

Types: `feat`, `fix`, `docs`, `style`, `refactor`, `test`, `chore`

## 🐛 Debugging

### Enable Debug Logging
```bash
./BciOrchestrator --debug
```

### Log Levels
- `DEBUG` - All messages including packet data
- `INFO` - Normal operation messages
- `WARN` - Warning conditions
- `ERROR` - Error conditions

### Log File Location
```
./logs/orchestrator_YYYY-MM-DD.log
```

### Common Issues

| Issue | Solution |
|-------|----------|
| Port already in use | Check for other instances, change port |
| No UDP data received | Verify Emotiv OSC settings |
| WebSocket won't connect | Check firewall, verify URL |
| High latency | Reduce debounce time, increase rate |

## 📄 License

MIT License - see [LICENSE](LICENSE) file.

## 🤝 Contributing

See [CONTRIBUTING.md](CONTRIBUTING.md) for guidelines.

## 📞 Support

- Create an issue for bugs
- Use discussions for questions
- Check [TROUBLESHOOTING.md](docs/TROUBLESHOOTING.md)
