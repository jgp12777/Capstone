const WebSocket = require('ws');

const wss = new WebSocket.Server({ port: 8080 });

console.log("WebSocket server running on ws://localhost:8080");

wss.on('connection', function connection(ws) {
  console.log("Client connected!");

  setInterval(() => {
    const message = JSON.stringify({
      command: "right",
      confidence: 0.9
    });

    ws.send(message);
    console.log("Sent:", message);
  }, 1000);
});