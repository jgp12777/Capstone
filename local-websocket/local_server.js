import { WebSocketServer } from "ws";

// Finding 4: Distinguish simulator vs live mode at startup
const IS_SIMULATOR = process.env.MODE !== "live";
if (IS_SIMULATOR) {
  console.warn("WARNING: Running in SIMULATOR mode — not real EEG data");
}

// Finding 1: Shared secret for authenticating messages from the Python wrapper
const WRAPPER_TOKEN = process.env.WRAPPER_SECRET;

// Finding 3: Minimum confidence required to forward a command to Unity
// Matches BrainWrapper onThreshold (0.6) so commands are not double-filtered
const CONFIDENCE_THRESHOLD = process.env.CONFIDENCE_THRESHOLD
  ? parseFloat(process.env.CONFIDENCE_THRESHOLD)
  : 0.6;

// Finding 5: Maximum age (ms) of a message before it is considered stale
const MAX_MSG_AGE_MS = 500;

// Create WebSocket server on port 8080
const wss = new WebSocketServer({ port: 8080 });

// Predefined mental commands for testing (can be changed based on requirements for project)
const commands = ["push", "pull", "left", "right", "neutral"];

// Optional: predefined pattern for repeatable testing (this can help with testing mental commands)
const testPattern = [
  { command: "push", confidence: 0.8 },
  { command: "left", confidence: 0.9 },
  { command: "pull", confidence: 0.7 },
  { command: "right", confidence: 0.95 },
  { command: "neutral", confidence: 0.5 }
];

// Finding 2: Validate that a mental command message has the expected shape and values
function isValidCommand(msg) {
  const validCommands = ["push", "pull", "left", "right", "neutral"];
  return (
    typeof msg.command === "string" &&
    validCommands.includes(msg.command) &&
    typeof msg.confidence === "number" &&
    msg.confidence >= 0 &&
    msg.confidence <= 1
  );
}

wss.on("connection", (ws) => {
  console.log("Client connected");

  // Finding 4: Include mode field so Unity can display a warning when running in simulator
  const initMsg = {
    type: "status",
    message: "connection received",
    mode: IS_SIMULATOR ? "simulator" : "live",
    timestamp: Date.now()
  };
  ws.send(JSON.stringify(initMsg));
  console.log("Sent to client:", initMsg);

  let index = 0;

  // Send a command every second (simulator only — live mode relies on Python wrapper messages)
  const interval = IS_SIMULATOR
    ? setInterval(() => {
        const msg = {
          type: "mental_command",
          ...testPattern[index],
          timestamp: Date.now()
        };

        // Or use random command / confidence for more variation:
        // const msg = {
        //   type: "mental_command",
        //   command: commands[Math.floor(Math.random() * commands.length)],
        //   confidence: Math.random(),
        //   timestamp: Date.now()
        // };

        index = (index + 1) % testPattern.length;

        // Finding 2: Drop malformed messages before they reach Unity
        if (!isValidCommand(msg)) {
          console.warn("Dropped malformed mental command:", msg);
          return;
        }

        // Finding 3: Suppress low-confidence commands (neutral always passes — it's a reset signal)
        if (msg.command !== "neutral" && msg.confidence < CONFIDENCE_THRESHOLD) {
          console.log(`Suppressed low-confidence command: ${msg.command} (${msg.confidence})`);
          return;
        }

        ws.send(JSON.stringify(msg));
        console.log("Sent to client:", msg);
      }, 1000)
    : null;

  // Handle messages from client (e.g. Python wrapper pushing live EEG commands)
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
      // Strip the token before forwarding so Unity never sees it
      delete parsed.token;
    }

    // Finding 5: Drop messages that are too old to act on safely
    if (parsed.timestamp && Date.now() - parsed.timestamp > MAX_MSG_AGE_MS) {
      console.warn("Dropped stale command:", parsed);
      return;
    }

    // Finding 2 + 3: Validate and threshold-gate mental commands
    if (parsed.type === "mental_command") {
      if (!isValidCommand(parsed)) {
        console.warn("Dropped malformed mental command:", parsed);
        return;
      }
      if (parsed.command !== "neutral" && parsed.confidence < CONFIDENCE_THRESHOLD) {
        console.log(`Suppressed low-confidence command: ${parsed.command} (${parsed.confidence})`);
        return;
      }
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

console.log(
  `BCI Simulator WebSocket server running on ws://localhost:8080 [mode: ${IS_SIMULATOR ? "simulator" : "live"}]`
);
