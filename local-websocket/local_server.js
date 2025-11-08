// Base code from ChatGPT & Donny Wals
import { WebSocketServer } from "ws";

// Create server listening on port 8080 (CHANGE PORT TO SOMETHING NO ONE WILL BE USING)
const wss = new WebSocketServer({port: 8080})

// Handle connections
wss.on("connection", (socket) => {
    // send and receive messages
});