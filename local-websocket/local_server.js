import { WebSocketServer, WebSocket } from "ws";
import http from "http";

// Finding 4: Distinguish simulator vs live mode at startup
const IS_SIMULATOR = process.env.MODE !== "live";
if (IS_SIMULATOR) {
  console.warn("WARNING: Running in SIMULATOR mode — not real EEG data");
}

// Finding 1: Shared secret for authenticating messages from the wrapper
const WRAPPER_TOKEN = process.env.WRAPPER_SECRET;

// Finding 3: Minimum confidence required to forward a command to Unity
// Matches BrainWrapper onThreshold (0.6) so commands are not double-filtered
const CONFIDENCE_THRESHOLD = process.env.CONFIDENCE_THRESHOLD
  ? parseFloat(process.env.CONFIDENCE_THRESHOLD)
  : 0.6;

// Finding 5: Maximum age (ms) of a message before it is considered stale
const MAX_MSG_AGE_MS = 500;

const PORT = parseInt(process.env.PORT) || 8080;
const HOST = "127.0.0.1";

// Current active state — kept in sync so /command and /state always reflect reality
let currentState = {
  action:     "neutral",
  confidence: 0,
  source:     IS_SIMULATOR ? "simulator" : "live",
  ts:         Date.now()
};

// Predefined pattern for repeatable simulator testing
const testPattern = [
  { action: "push",    confidence: 0.8  },
  { action: "left",    confidence: 0.9  },
  { action: "pull",    confidence: 0.7  },
  { action: "right",   confidence: 0.95 },
  { action: "neutral", confidence: 0.5  }
];

// Finding 2: Validate that a mental command message has the expected shape and values
function isValidCommand(msg) {
  const validCommands = ["push", "pull", "left", "right", "neutral"];
  return (
    typeof msg.action === "string" &&
    validCommands.includes(msg.action) &&
    typeof msg.confidence === "number" &&
    msg.confidence >= 0 &&
    msg.confidence <= 1
  );
}

// Map action name to single-letter direction for BoxController HTTP polling
function mapActionToCommand(action) {
  switch (action) {
    case "push":  return "U";
    case "pull":  return "D";
    case "left":  return "L";
    case "right": return "R";
    default:      return "";
  }
}

// ─── HTTP SERVER ─────────────────────────────────────────────────────────────
// Serves /command (BoxController), /state, /healthz on the same port as WS.

const httpServer = http.createServer((req, res) => {
  res.setHeader("Access-Control-Allow-Origin", "*");
  res.setHeader("Access-Control-Allow-Methods", "GET, OPTIONS");
  res.setHeader("Access-Control-Allow-Headers", "Content-Type");

  if (req.method === "OPTIONS") {
    res.writeHead(204);
    res.end();
    return;
  }

  const url = new URL(req.url, `http://${req.headers.host}`);
  const path = url.pathname;

  // BoxController polls this every 100 ms expecting a single letter: U/D/L/R or ""
  if (req.method === "GET" && path === "/command") {
    const cmd = mapActionToCommand(currentState.action);
    res.writeHead(200, { "Content-Type": "text/plain" });
    res.end(cmd);
    return;
  }

  // Full state snapshot — mirrors BrainWrapper.cs /state response format
  if (req.method === "GET" && path === "/state") {
    const json = JSON.stringify({
      ts:         currentState.ts,
      type:       "mental_command",
      action:     currentState.action,
      confidence: currentState.confidence,
      source:     currentState.source
    });
    res.writeHead(200, { "Content-Type": "application/json" });
    res.end(json);
    return;
  }

  // Health check
  if (req.method === "GET" && path === "/healthz") {
    res.writeHead(200, { "Content-Type": "application/json" });
    res.end(JSON.stringify({ status: "ok" }));
    return;
  }

  // Info page for any other path
  const info = `BCI Simulator\nEndpoints: ws /stream , GET /command , GET /state , GET /healthz\nMode: ${IS_SIMULATOR ? "simulator" : "live"}`;
  res.writeHead(200, { "Content-Type": "text/plain" });
  res.end(info);
});

// ─── WEBSOCKET SERVER ─────────────────────────────────────────────────────────
// Attached to httpServer so both share port 8080.
// Path is /stream to match BrainWrapper.cs — Unity uses the same URL regardless
// of which server is running.

const wss = new WebSocketServer({ server: httpServer });

wss.on("connection", (ws, req) => {
  // Reject connections not targeting /stream
  if (req.url !== "/stream") {
    ws.close(1008, "Use ws://127.0.0.1:8080/stream");
    return;
  }

  console.log("Client connected");

  // Finding 4: Include mode so Unity can warn when running against the simulator
  const initMsg = {
    type:    "status",
    message: "connection received",
    mode:    IS_SIMULATOR ? "simulator" : "live",
    ts:      Date.now()
  };
  ws.send(JSON.stringify(initMsg));
  console.log("Sent to client:", initMsg);

  let index = 0;

  // Simulator: push test patterns to Unity every second.
  // Live: Unity receives forwarded messages from the wrapper (see ws.on("message")).
  const interval = IS_SIMULATOR
    ? setInterval(() => {
        const msg = {
          type:       "mental_command",
          action:     testPattern[index].action,
          confidence: testPattern[index].confidence,
          ts:         Date.now()
        };
        index = (index + 1) % testPattern.length;

        // Finding 2: Drop malformed messages before they reach Unity
        if (!isValidCommand(msg)) {
          console.warn("Dropped malformed mental command:", msg);
          return;
        }

        // Finding 3: Suppress low-confidence commands (neutral always passes)
        if (msg.action !== "neutral" && msg.confidence < CONFIDENCE_THRESHOLD) {
          console.log(`Suppressed low-confidence command: ${msg.action} (${msg.confidence})`);
          return;
        }

        // Keep state in sync so /command HTTP endpoint stays accurate
        currentState = { action: msg.action, confidence: msg.confidence, source: "simulator", ts: msg.ts };

        ws.send(JSON.stringify(msg));
        console.log("Sent to client:", msg);
      }, 1000)
    : null;

  // Handle messages from clients.
  // In live mode the wrapper connects here and pushes real EEG commands.
  // Validated messages are forwarded to all other connected clients (Unity).
  ws.on("message", (data) => {
    let parsed;
    try {
      parsed = JSON.parse(data.toString());
    } catch {
      console.warn("Invalid message format — ignored");
      return;
    }

    // Finding 1: Reject mental_command messages that lack a valid wrapper token
    if (parsed.type === "mental_command") {
      if (WRAPPER_TOKEN && parsed.token !== WRAPPER_TOKEN) {
        console.warn("Rejected unauthenticated command injection attempt");
        ws.close(1008, "Unauthorized");
        return;
      }
      // Strip token before forwarding so Unity never sees it
      delete parsed.token;
    }

    // Finding 5: Drop messages that are too old to act on safely
    if (parsed.ts && Date.now() - parsed.ts > MAX_MSG_AGE_MS) {
      console.warn("Dropped stale command:", parsed);
      return;
    }

    // Finding 2 + 3: Validate and threshold-gate mental commands
    if (parsed.type === "mental_command") {
      if (!isValidCommand(parsed)) {
        console.warn("Dropped malformed mental command:", parsed);
        return;
      }
      if (parsed.action !== "neutral" && parsed.confidence < CONFIDENCE_THRESHOLD) {
        console.log(`Suppressed low-confidence command: ${parsed.action} (${parsed.confidence})`);
        return;
      }

      // Keep state in sync so /command HTTP endpoint stays accurate
      currentState = {
        action:     parsed.action,
        confidence: parsed.confidence,
        source:     "live",
        ts:         parsed.ts || Date.now()
      };

      // Forward validated command to all other connected clients (Unity)
      wss.clients.forEach((client) => {
        if (client !== ws && client.readyState === WebSocket.OPEN) {
          client.send(JSON.stringify(parsed));
        }
      });
    }

    console.log("Received from client:", parsed);
  });

  ws.on("close", () => {
    console.log("Client disconnected");
    if (interval) clearInterval(interval);
  });

  ws.on("error", (err) => {
    console.error("WebSocket error:", err);
    if (interval) clearInterval(interval);
  });
});

// ─── START ────────────────────────────────────────────────────────────────────

httpServer.listen(PORT, HOST, () => {
  console.log(`BCI WebSocket server running [mode: ${IS_SIMULATOR ? "simulator" : "live"}]`);
  console.log(`  WebSocket : ws://${HOST}:${PORT}/stream`);
  console.log(`  HTTP GET  : http://${HOST}:${PORT}/command  (BoxController)`);
  console.log(`  HTTP GET  : http://${HOST}:${PORT}/state`);
  console.log(`  HTTP GET  : http://${HOST}:${PORT}/healthz`);
  if (!IS_SIMULATOR && !WRAPPER_TOKEN) {
    console.warn("WARNING: WRAPPER_SECRET not set — any client can push commands in live mode");
  }
});
