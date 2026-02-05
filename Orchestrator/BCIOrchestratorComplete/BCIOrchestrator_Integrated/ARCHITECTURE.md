# 🏗️ System Architecture

## Overview

The BCI Brain Game system consists of several interconnected components that process brain signals from an Emotiv headset and translate them into game actions.

## System Flow Diagram

```
┌─────────────────────────────────────────────────────────────────────────────┐
│                              HARDWARE LAYER                                  │
├─────────────────────────────────────────────────────────────────────────────┤
│                                                                              │
│   ┌──────────────────────────────────────────────────────────────────────┐  │
│   │                      EMOTIV HEADSET                                   │  │
│   │  • EEG Signal Acquisition                                             │  │
│   │  • Mental Command Detection (push, pull, left, right, lift, drop)    │  │
│   │  • Confidence Scoring (0.0 - 1.0)                                     │  │
│   └──────────────────────────────────────────────────────────────────────┘  │
│                                    │                                         │
└────────────────────────────────────┼─────────────────────────────────────────┘
                                     │
                                     │ OSC over UDP
                                     │ Port 7400
                                     ▼
┌─────────────────────────────────────────────────────────────────────────────┐
│                           PROCESSING LAYER                                   │
├─────────────────────────────────────────────────────────────────────────────┤
│                                                                              │
│   ┌──────────────────────────────────────────────────────────────────────┐  │
│   │                    BCI ORCHESTRATOR                                   │  │
│   │                                                                       │  │
│   │  ┌─────────────────────────────────────────────────────────────────┐ │  │
│   │  │                    UDP RECEIVER                                  │ │  │
│   │  │  • OSC Message Parsing (Emotiv format)                          │ │  │
│   │  │  • CSV Message Parsing (fallback/test)                          │ │  │
│   │  │  • Raw Signal Logging                                           │ │  │
│   │  └──────────────────────────┬──────────────────────────────────────┘ │  │
│   │                             │                                        │  │
│   │                             ▼                                        │  │
│   │  ┌─────────────────────────────────────────────────────────────────┐ │  │
│   │  │                   SIGNAL FILTER                                  │ │  │
│   │  │  • Hysteresis (OnThreshold/OffThreshold)                        │ │  │
│   │  │  • Debouncing (prevent rapid state changes)                     │ │  │
│   │  │  • Rate Limiting (cap broadcast frequency)                      │ │  │
│   │  │  • Action Mapping (raw → game actions)                          │ │  │
│   │  └──────────────────────────┬──────────────────────────────────────┘ │  │
│   │                             │                                        │  │
│   │                             ▼                                        │  │
│   │  ┌─────────────────────────────────────────────────────────────────┐ │  │
│   │  │                 WEBSOCKET SERVER                                 │ │  │
│   │  │  • Client Connection Management                                  │ │  │
│   │  │  • JSON Message Broadcasting                                     │ │  │
│   │  │  • HTTP API (state, health, metrics)                            │ │  │
│   │  │  • Connection Health Monitoring                                  │ │  │
│   │  └─────────────────────────────────────────────────────────────────┘ │  │
│   └──────────────────────────────────────────────────────────────────────┘  │
│                                    │                                         │
└────────────────────────────────────┼─────────────────────────────────────────┘
                                     │
                                     │ WebSocket JSON
                                     │ Port 8080
                                     ▼
┌─────────────────────────────────────────────────────────────────────────────┐
│                           APPLICATION LAYER                                  │
├─────────────────────────────────────────────────────────────────────────────┤
│                                                                              │
│   ┌────────────────────────┐    ┌────────────────────────┐                  │
│   │      UNITY GAME        │    │    DEBUG DASHBOARD     │                  │
│   │                        │    │                        │                  │
│   │  • Character Control   │    │  • State Visualization │                  │
│   │  • Visual Feedback     │    │  • Signal Monitoring   │                  │
│   │  • Game Logic          │    │  • Performance Metrics │                  │
│   └────────────────────────┘    └────────────────────────┘                  │
│                                                                              │
└─────────────────────────────────────────────────────────────────────────────┘
```

## Component Details

### 1. UDP Receiver

**Purpose:** Receive and parse brain signals from Emotiv headset.

**Input Formats:**

1. **OSC (Open Sound Control)**
   ```
   Address: /com/action (e.g., /com/push, /com/left)
   Arguments: float confidence (0.0 - 1.0)
   ```

2. **CSV (Test/Fallback)**
   ```
   action,confidence
   push,0.85
   ```

**Parsing Logic:**
```csharp
// OSC: Extract action from address, confidence from float argument
// CSV: Split by comma, parse action and confidence
```

### 2. Signal Filter

**Purpose:** Clean and stabilize the noisy brain signals.

**Filtering Stages:**

```
Raw Signal → Hysteresis → Debounce → Rate Limit → Clean Output
```

#### Hysteresis
Prevents oscillation between states using two thresholds:
- **OnThreshold (0.6):** Signal must exceed this to turn ON
- **OffThreshold (0.5):** Signal must fall below this to turn OFF

```
                OnThreshold (0.6)
    ──────────────────────────────── ON Zone
                     │
         Hysteresis  │ No change zone
            Zone     │
    ──────────────────────────────── 
                OffThreshold (0.5)
    ──────────────────────────────── OFF Zone
```

#### Debounce
Requires action to remain stable for a minimum time:
```
candidateAction = newAction
if (stableFor >= DebounceMs):
    activeAction = candidateAction
```

#### Rate Limiting
Caps broadcast frequency to prevent overwhelming clients:
```
if (timeSinceLastBroadcast < 1000/RateHz):
    skip broadcast
```

### 3. WebSocket Server

**Purpose:** Distribute processed signals to game clients.

**Endpoints:**

| Endpoint | Protocol | Purpose |
|----------|----------|---------|
| `/stream` | WebSocket | Real-time brain events |
| `/state` | HTTP GET | Current state snapshot |
| `/healthz` | HTTP GET | Server health check |
| `/metrics` | HTTP GET | Performance metrics |
| `/broadcast` | HTTP POST | Manual message injection |

**Message Format (Brain Event):**
```json
{
  "ts": 1704067200000,      // Unix timestamp (ms)
  "type": "mental_command",  // Event type
  "action": "moveForward",   // Mapped action
  "confidence": 0.85,        // Confidence score
  "durationMs": 1500,        // Time in current state
  "source": "emotiv-osc",    // Signal source
  "raw": {                   // Debug info
    "rawAction": "push",
    "rawConfidence": 0.85,
    "filtered": true
  }
}
```

### 4. Action Mapping

**Purpose:** Translate raw Emotiv actions to game-specific actions.

**Default Mapping:**
```json
{
  "push": "moveForward",
  "pull": "moveBackward",
  "left": "turnLeft",
  "right": "turnRight",
  "lift": "jump",
  "drop": "crouch",
  "neutral": "idle"
}
```

## Data Flow Timing

```
┌─────────────────────────────────────────────────────────────────────────┐
│                        TIMING DIAGRAM                                    │
├─────────────────────────────────────────────────────────────────────────┤
│                                                                          │
│  Emotiv   │░░░░░░░░░░░░░░░░░░░░│                                        │
│  (10 Hz)  └───┬───┬───┬───┬───┘                                        │
│               │   │   │   │                                             │
│  UDP Recv │   ●   ●   ●   ●   │ (immediate)                            │
│               │   │   │   │                                             │
│  Filter   │   ╔═══╗   │   │   │ (debounce: 150ms)                      │
│               ║   ║   │   │                                             │
│  Broadcast│   └───●───┘   │   │ (rate limit: 15 Hz = 66ms)             │
│                   │       │                                             │
│  Unity    │       ●───────●   │ (frame-rate dependent)                 │
│                                                                          │
│  Timeline:  0   100  200  300  400ms                                    │
└─────────────────────────────────────────────────────────────────────────┘
```

## Logging Architecture

### Log Levels

| Level | Purpose | Examples |
|-------|---------|----------|
| DEBUG | Detailed debugging | Packet contents, state transitions |
| INFO | Normal operation | Startup, connections, state changes |
| WARN | Potential issues | Reconnections, timeouts |
| ERROR | Failures | Crashes, parse errors |

### Log Format

```
[TIMESTAMP] [LEVEL] [COMPONENT] [CONTEXT] Message
[2024-01-01 12:00:00.123] [INFO ] [ORCHESTRATOR] [STARTUP] Server started on port 8080
```

### Log Rotation

- **Daily rotation:** New file each day
- **Size limit:** 50MB per file
- **Naming:** `orchestrator_YYYY-MM-DD.log`

## Error Handling

### Recovery Strategies

| Component | Error | Strategy |
|-----------|-------|----------|
| UDP Receiver | Port busy | Retry with backoff |
| WebSocket Server | Client disconnect | Remove from list, continue |
| Signal Filter | Invalid data | Log and skip packet |
| Process Manager | Child crash | Auto-restart (max 3 times) |

### Health Monitoring

```
┌─────────────────────────────────────────────┐
│            HEALTH CHECK FLOW                │
├─────────────────────────────────────────────┤
│                                             │
│  Every 3 seconds:                           │
│    ├─ Check UDP packet received recently?   │
│    ├─ Check WebSocket clients connected?    │
│    └─ Check child processes running?        │
│                                             │
│  If unhealthy:                              │
│    ├─ Log warning                           │
│    └─ Attempt restart if applicable         │
│                                             │
└─────────────────────────────────────────────┘
```

## Performance Characteristics

| Metric | Target | Actual |
|--------|--------|--------|
| UDP → WebSocket latency | < 50ms | ~20ms |
| State change response | < 200ms | ~170ms (with debounce) |
| Max clients | 100+ | Tested to 50 |
| Memory usage | < 100MB | ~30MB |
| CPU usage | < 5% | ~2% |

## Security Considerations

1. **Localhost by default:** No LAN exposure unless explicitly enabled
2. **No authentication:** Intended for single-user local deployment
3. **Input validation:** All UDP messages validated before processing
4. **No sensitive data:** Brain signals contain no identifying information
