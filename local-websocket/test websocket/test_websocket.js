const WebSocket = require('ws');
const readline = require('readline');

const wss = new WebSocket.Server({ port: 8080 });

console.log("WebSocket server running on ws://localhost:8080");
console.log("Type: right, left, up, down, neutral");
console.log("Press Ctrl+C to stop.\n");

let connectedClient = null;

wss.on('connection', function connection(ws) {
  console.log("Client connected!");
  connectedClient = ws;

  ws.on('close', () => {
    console.log("Client disconnected");
    connectedClient = null;
  });
});

// Setup terminal input
const rl = readline.createInterface({
  input: process.stdin,
  output: process.stdout
});

// Listen for user input
rl.on('line', (input) => {
  const command = input.trim().toLowerCase();

  const validCommands = ["right", "left", "up", "down", "neutral"];

  if (!validCommands.includes(command)) {
    console.log("Invalid command. Use: right, left, up, down, neutral");
    return;
  }

  if (connectedClient && connectedClient.readyState === WebSocket.OPEN) {
    const message = JSON.stringify({
      command: command,
      confidence: 0.85
    });

    connectedClient.send(message);
    console.log("Sent:", message);
  } else {
    console.log("No client connected.");
  }
});