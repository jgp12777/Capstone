// Code created from ChatGPT & Donny Wals
import { WebSocketServer } from "ws";

// Create server listening on port 8080
const wss = new WebSocketServer({port: 8080})

// Handle connections
wss.on('connection', function connection(wss) {
    wss.on('message', function message(data) {
        console.log('received %s', data);
        wss.close();
    });

    wss.send('connection received');

    var t = setInterval(function() {
        console.log("sending message");
        wss.send('sending message!');
    }, 1000);

    wss.on('close', function close() {
        console.log("received close");
        clearInterval(t);
      });
});