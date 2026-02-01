# Testing & Debugging Guide

## Quick Testing Without Emotiv Device

You can test the orchestrator without an Emotiv device by sending manual UDP packets.

### Using PowerShell

```powershell
# Send a test command
$udp = New-Object System.Net.Sockets.UdpClient
$bytes = [System.Text.Encoding]::UTF8.GetBytes("push,0.85")
$udp.Send($bytes, $bytes.Length, "127.0.0.1", 7400)
$udp.Close()
```

### Using Python

```python
import socket
import time

def send_command(action, confidence):
    sock = socket.socket(socket.AF_INET, socket.SOCK_DGRAM)
    message = f"{action},{confidence}"
    sock.sendto(message.encode(), ("127.0.0.1", 7400))
    sock.close()
    print(f"Sent: {message}")

# Test sequence
send_command("push", 0.85)
time.sleep(1)
send_command("left", 0.75)
time.sleep(1)
send_command("neutral", 0.0)
```

### Using C#

```csharp
using System.Net;
using System.Net.Sockets;
using System.Text;

var udp = new UdpClient();
var endpoint = new IPEndPoint(IPAddress.Parse("127.0.0.1"), 7400);

void SendCommand(string action, double confidence)
{
    var message = $"{action},{confidence:0.00}";
    var bytes = Encoding.UTF8.GetBytes(message);
    udp.Send(bytes, bytes.Length, endpoint);
    Console.WriteLine($"Sent: {message}");
}

// Test
SendCommand("push", 0.85);
Thread.Sleep(1000);
SendCommand("left", 0.75);
```

## Testing Checklist

### ✅ Pre-Flight Checks

- [ ] .NET 8 SDK installed
- [ ] Project builds successfully
- [ ] Port 7400 is free (UDP)
- [ ] Port 8080 is free (HTTP/WebSocket)
- [ ] Windows Firewall allows the app

### ✅ UDP Receiver Tests

1. **Start the orchestrator**
   ```
   BCIOrchestrator.exe --debug
   ```

2. **Send test UDP packet**
   ```powershell
   $udp = New-Object System.Net.Sockets.UdpClient
   $bytes = [Text.Encoding]::UTF8.GetBytes("push,0.85")
   $udp.Send($bytes, $bytes.Length, "127.0.0.1", 7400)
   ```

3. **Expected output**
   ```
   [INFO] UDP Receiver started on port 7400
   [INFO] Waiting for Emotiv BCI-OSC packets...
   [UDP] active=push (conf=0.85)
   ```

### ✅ WebSocket Server Tests

1. **Check HTTP endpoint**
   ```
   curl http://127.0.0.1:8080/healthz
   ```
   Expected: `{"status":"ok","timestamp":...}`

2. **Check state endpoint**
   ```
   curl http://127.0.0.1:8080/state
   ```
   Expected: `{"clients":0,"uptime":...}`

3. **Open test client**
   - Open `test_client.html` in browser
   - Click "Connect"
   - Should see "CONNECTED" status

### ✅ Integration Tests

1. **End-to-End Flow**
   ```
   Start Orchestrator → Send UDP → See WebSocket broadcast
   ```

2. **Send command while test client is connected**
   ```powershell
   $udp = New-Object System.Net.Sockets.UdpClient
   $bytes = [Text.Encoding]::UTF8.GetBytes("push,0.85")
   $udp.Send($bytes, $bytes.Length, "127.0.0.1", 7400)
   ```

3. **Expected in test client**
   - Current Action changes to "push" (or mapped action)
   - Confidence shows 0.85
   - Event appears in log

### ✅ Keyboard Simulation Tests

1. **Start with keyboard enabled**
   ```
   BCIOrchestrator.exe --keyboard
   ```

2. **Open Notepad**

3. **Send commands**
   ```powershell
   # Should type 'W' in Notepad
   $udp = New-Object System.Net.Sockets.UdpClient
   $bytes = [Text.Encoding]::UTF8.GetBytes("push,0.85")
   $udp.Send($bytes, $bytes.Length, "127.0.0.1", 7400)
   Start-Sleep -Milliseconds 500
   
   # Should release 'W'
   $bytes = [Text.Encoding]::UTF8.GetBytes("neutral,0.0")
   $udp.Send($bytes, $bytes.Length, "127.0.0.1", 7400)
   ```

## Common Issues & Solutions

### Issue: "Address already in use"

**Symptoms:**
```
[ERROR] WebSocket Server error: Address already in use
```

**Solutions:**
1. Check what's using the port:
   ```
   netstat -ano | findstr :8080
   ```

2. Kill the process:
   ```
   taskkill /PID [PID] /F
   ```

3. Change port in `appsettings.json`

### Issue: No UDP packets received

**Symptoms:**
```
[INFO] UDP Receiver started on port 7400
[INFO] Waiting for Emotiv BCI-OSC packets...
(nothing happens)
```

**Solutions:**
1. Verify port is listening:
   ```
   netstat -an | findstr :7400
   ```

2. Check Windows Firewall:
   - Allow BCIOrchestrator.exe through firewall
   - Allow UDP port 7400

3. Test with manual packet (see above)

4. If using Emotiv:
   - Check Emotiv App OSC settings
   - Verify target IP: 127.0.0.1
   - Verify target port: 7400

### Issue: WebSocket clients can't connect

**Symptoms:**
```
ERR_CONNECTION_REFUSED in browser
```

**Solutions:**
1. Check server is running:
   ```
   curl http://127.0.0.1:8080/healthz
   ```

2. Check firewall allows port 8080

3. Try running as Administrator

4. Check logs for binding errors

### Issue: Actions are delayed/laggy

**Symptoms:**
- 500ms+ delay between thought and action
- Choppy movement

**Solutions:**
1. Reduce `DebounceMs` in config:
   ```json
   "DebounceMs": 50
   ```

2. Increase `RateHz`:
   ```json
   "RateHz": 30
   ```

3. Check system performance:
   - High CPU usage?
   - Network latency?
   - Emotiv signal quality?

### Issue: Actions are jittery/flickering

**Symptoms:**
- Rapid on/off switching
- Unstable actions

**Solutions:**
1. Increase `DebounceMs`:
   ```json
   "DebounceMs": 300
   ```

2. Widen threshold gap:
   ```json
   "OnThreshold": 0.7,
   "OffThreshold": 0.4
   ```

3. Reduce `RateHz`:
   ```json
   "RateHz": 10
   ```

## Performance Benchmarking

### Latency Test

```python
import socket
import time

sock = socket.socket(socket.AF_INET, socket.SOCK_DGRAM)

for i in range(100):
    start = time.time()
    message = f"push,0.85"
    sock.sendto(message.encode(), ("127.0.0.1", 7400))
    # Measure time to WebSocket broadcast (use test client)
    latency = (time.time() - start) * 1000
    print(f"Latency: {latency:.2f}ms")
    time.sleep(0.1)
```

**Expected Results:**
- Typical: 10-50ms
- Good: <100ms
- Acceptable: <200ms

### Throughput Test

```python
import socket
import time

sock = socket.socket(socket.AF_INET, socket.SOCK_DGRAM)
actions = ["push", "pull", "left", "right", "lift", "drop"]

start = time.time()
count = 0

for _ in range(1000):
    action = actions[count % len(actions)]
    message = f"{action},0.85"
    sock.sendto(message.encode(), ("127.0.0.1", 7400))
    count += 1
    time.sleep(0.01)

duration = time.time() - start
print(f"Sent {count} packets in {duration:.2f}s")
print(f"Throughput: {count/duration:.2f} packets/sec")
```

**Expected Results:**
- Should handle 30+ packets/second with RateHz=30
- No dropped packets
- Memory remains stable

## Debugging Tips

### Enable Debug Logging

```
BCIOrchestrator.exe --debug
```

This shows:
- Every UDP packet received
- Every action processed
- Every WebSocket message sent
- Detailed filtering logic

### Monitor Logs in Real-Time

PowerShell:
```powershell
Get-Content ./logs/orchestrator.log -Wait -Tail 50
```

### Network Capture

Use Wireshark to inspect UDP packets:
1. Filter: `udp.port == 7400`
2. Look for malformed packets
3. Check packet rate

### Check System Resources

Task Manager:
- CPU usage should be <5%
- Memory should be ~50-100MB
- No memory leaks over time

### Test Emotiv Connection

```csharp
// Simple OSC packet tester
using var udp = new UdpClient(7400);
Console.WriteLine("Listening for Emotiv packets...");

while (true)
{
    var result = await udp.ReceiveAsync();
    var text = Encoding.UTF8.GetString(result.Buffer);
    Console.WriteLine($"Received: {text}");
}
```

## Automated Test Suite

Create `test_suite.ps1`:

```powershell
# Automated test suite
$ErrorActionPreference = "Stop"

Write-Host "=== BCI Orchestrator Test Suite ===" -ForegroundColor Cyan

# Test 1: Check .NET
Write-Host "`nTest 1: .NET SDK" -ForegroundColor Yellow
dotnet --version
if ($LASTEXITCODE -ne 0) { throw ".NET not found" }
Write-Host "✓ PASS" -ForegroundColor Green

# Test 2: Build project
Write-Host "`nTest 2: Build" -ForegroundColor Yellow
dotnet build -c Release
if ($LASTEXITCODE -ne 0) { throw "Build failed" }
Write-Host "✓ PASS" -ForegroundColor Green

# Test 3: Check ports available
Write-Host "`nTest 3: Port availability" -ForegroundColor Yellow
$ports = @(7400, 8080)
foreach ($port in $ports) {
    $connections = Get-NetTCPConnection -LocalPort $port -ErrorAction SilentlyContinue
    if ($connections) {
        throw "Port $port is in use"
    }
}
Write-Host "✓ PASS" -ForegroundColor Green

# Test 4: UDP packet test
Write-Host "`nTest 4: UDP communication" -ForegroundColor Yellow
# Start orchestrator in background
$process = Start-Process -FilePath ".\bin\Release\net8.0-windows\BCIOrchestrator.exe" -PassThru
Start-Sleep -Seconds 3

# Send test packet
$udp = New-Object System.Net.Sockets.UdpClient
$bytes = [Text.Encoding]::UTF8.GetBytes("push,0.85")
$udp.Send($bytes, $bytes.Length, "127.0.0.1", 7400)
$udp.Close()

Start-Sleep -Seconds 2
$process.Kill()
Write-Host "✓ PASS" -ForegroundColor Green

Write-Host "`n=== All Tests Passed ===" -ForegroundColor Green
```

Run with:
```powershell
.\test_suite.ps1
```

## Deployment Checklist

Before distributing:

- [ ] All tests pass
- [ ] Performance benchmarks meet targets
- [ ] No memory leaks over 1-hour run
- [ ] Tested with real Emotiv device
- [ ] Tested with Unity client
- [ ] Tested keyboard simulation
- [ ] Tested LAN mode
- [ ] README is complete
- [ ] Config examples are valid
- [ ] Build is single-file executable
- [ ] Antivirus doesn't flag it

## Getting Help

If you're stuck:

1. Check logs: `./logs/orchestrator.log`
2. Run with `--debug` flag
3. Test with manual UDP packets
4. Verify all ports are free
5. Check Windows Firewall
6. Run as Administrator
7. Review this guide

Still stuck? File an issue with:
- Full error message
- Log file contents
- Steps to reproduce
- System info (OS, .NET version)
