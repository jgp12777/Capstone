import { WebSocketServer } from "ws";

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

wss.on("connection", (ws) => {
  console.log("Client connected");

  // Send initial connection message
  const initMsg = { type: "status", message: "connection received", timestamp: Date.now() };
  ws.send(JSON.stringify(initMsg));
  console.log("Sent to client:", initMsg);

  let index = 0;

  // Send a command every second
  const interval = setInterval(() => {
    // Either pick from testPattern for predictable behavior
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

    ws.send(JSON.stringify(msg));
    console.log("Sent to client:", msg);

    index = (index + 1) % testPattern.length;
  }, 1000);

  // Handle messages from client
  ws.on("message", (data) => {
    console.log("Received from client:", data.toString());
  });

  ws.on("close", () => {
    console.log("Client disconnected");
    clearInterval(interval);
  });

  ws.on("error", (err) => {
    console.error("WebSocket error:", err);
    clearInterval(interval);
  });
});

console.log("BCI Simulator WebSocket server running on ws://localhost:8080");
