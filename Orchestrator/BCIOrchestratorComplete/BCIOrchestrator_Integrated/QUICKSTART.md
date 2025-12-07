# 🚀 QUICK START GUIDE

## For First-Time Users (5 Minutes Setup)

### Step 1: Build the Application

1. Double-click `build.bat`
2. Wait for compilation to complete
3. Find your executable in the `Release` folder

### Step 2: Configure Emotiv (One-Time Setup)

1. Open the **Emotiv App**
2. Go to **Settings** → **OSC**
3. Enable **BCI-OSC**
4. Set:
   - Target IP: `127.0.0.1`
   - Target Port: `7400`
5. Make sure mental commands are trained

### Step 3: Run the Orchestrator

**Option A: Use the launcher**
```
Double-click: run.bat
Choose option 1 or 2
```

**Option B: Direct execution**
```
cd Release
BCIOrchestrator.exe
```

**Option C: With keyboard simulation**
```
cd Release
BCIOrchestrator.exe --keyboard
```

### Step 4: Test the Connection

1. Open `test_client.html` in your web browser
2. It should auto-connect and show "CONNECTED"
3. Think a trained command (e.g., "push")
4. Watch the action appear in the test client!

---

## What Got Integrated?

✅ **3-in-1 Solution**: Previously you needed to run:
   - Node.js WebSocket server
   - C# UDP receiver
   - C# orchestrator

✅ **Now it's just ONE .exe file!**

---

## File Structure

```
BCIOrchestrator_Integrated/
├── BCIOrchestrator.csproj   ← Project file
├── Program.cs               ← Main application (all code!)
├── appsettings.json         ← Configuration
├── build.bat                ← Build script
├── run.bat                  ← Easy launcher
├── test_client.html         ← Browser test tool
├── README.md                ← Full documentation
├── TESTING.md               ← Testing guide
└── QUICKSTART.md            ← This file
```

After building:
```
Release/
└── BCIOrchestrator.exe      ← Single executable!
```

---

## Quick Reference Commands

### Build
```batch
build.bat
```

### Run Normal Mode
```batch
cd Release
BCIOrchestrator.exe
```

### Run with Keyboard
```batch
cd Release
BCIOrchestrator.exe --keyboard
```

### Run with Debug Logging
```batch
cd Release
BCIOrchestrator.exe --debug
```

### Allow LAN Connections
```batch
cd Release
BCIOrchestrator.exe --allow-lan
```

---

## Quick Test (No Emotiv Needed)

### PowerShell Test
```powershell
# Open PowerShell in the Release folder
$udp = New-Object System.Net.Sockets.UdpClient
$bytes = [Text.Encoding]::UTF8.GetBytes("push,0.85")
$udp.Send($bytes, $bytes.Length, "127.0.0.1", 7400)
$udp.Close()
```

You should see:
1. Console: `[UDP] active=push (conf=0.85)`
2. Test client: Action changes to "moveForward"

---

## Troubleshooting in 30 Seconds

### "Port already in use"
```batch
netstat -ano | findstr :8080
taskkill /PID [PID] /F
```

### "No packets received"
1. Check Emotiv App OSC is enabled
2. Verify target IP: 127.0.0.1
3. Verify target port: 7400
4. Check Windows Firewall

### "Can't connect from browser"
```batch
# Test if server is running
curl http://127.0.0.1:8080/healthz
```

### "Build failed"
1. Install .NET 8 SDK: https://dotnet.microsoft.com/download
2. Close Visual Studio if open
3. Try again

---

## Next Steps

1. ✅ **Got it working?** → Read README.md for advanced features
2. 🎮 **Building a game?** → See Unity example in README.md
3. 🐛 **Having issues?** → Check TESTING.md for detailed debugging
4. ⚙️ **Want to customize?** → Edit appsettings.json
5. ⌨️ **Need keyboard control?** → Use `--keyboard` flag

---

## What Changed from Previous Versions?

### Before (v1.0 - Multi-Process)
```
❌ Run Node.js server
❌ Run UDP receiver
❌ Run orchestrator
❌ Manage 3 separate processes
❌ Install Node.js dependencies
❌ Complex troubleshooting
```

### Now (v2.0 - Integrated)
```
✅ ONE .exe file
✅ Built-in WebSocket server
✅ Built-in UDP receiver  
✅ Built-in orchestration
✅ System tray support
✅ Simple troubleshooting
```

---

## Key Improvements

1. **Simplified Deployment**: One executable, no dependencies
2. **Better Integration**: Components communicate internally (no IPC overhead)
3. **System Tray**: Runs in background, right-click to control
4. **Improved Filtering**: Better debouncing and hysteresis
5. **Enhanced Logging**: Structured, rotatable logs
6. **Hot Configuration**: Edit settings without restart
7. **Keyboard Simulation**: Optional virtual keyboard input
8. **Health Monitoring**: Built-in metrics and diagnostics

---

## Performance Expectations

- **Latency**: 50-150ms (brain → action)
- **Throughput**: Up to 30 actions/second
- **Memory**: 50-100 MB
- **CPU**: <5% on modern hardware
- **Stability**: Hours of continuous operation

---

## Support & Help

1. **Documentation**: README.md (comprehensive)
2. **Testing**: TESTING.md (debugging guide)
3. **Quick Start**: This file
4. **Logs**: ./logs/orchestrator.log

---

**Made with ❤️ for ease of use**

Ready to control your game with your mind! 🧠🎮
