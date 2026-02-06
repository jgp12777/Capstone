/**
 * BCI Orchestrator - Local WebSocket Server v2.0
 * 
 * A robust WebSocket server for broadcasting BCI events to connected clients.
 * Features:
 * - WebSocket broadcast to all connected clients
 * - HTTP endpoints for state, health, and broadcast
 * - Configurable via environment variables
 * - Extensive logging and debugging
 * - Graceful shutdown handling
 */

import { WebSocketServer, WebSocket } from 'ws';
import http from 'http';

//=============================================================================
// CONFIGURATION
//=============================================================================

const config = {
    port: parseInt(process.env.WS_PORT) || 8080,
    host: process.env.WS_HOST || '127.0.0.1',
    pingInterval: parseInt(process.env.PING_INTERVAL) || 30000,
    logLevel: process.env.LOG_LEVEL || 'INFO', // DEBUG, INFO, WARN, ERROR
    maxPayloadSize: parseInt(process.env.MAX_PAYLOAD) || 1024 * 1024, // 1MB
};

//=============================================================================
// LOGGING
//=============================================================================

const LogLevel = {
    DEBUG: 0,
    INFO: 1,
    WARN: 2,
    ERROR: 3
};

const currentLogLevel = LogLevel[config.logLevel] ?? LogLevel.INFO;

function log(level, component, message, data = null) {
    if (LogLevel[level] < currentLogLevel) return;
    
    const timestamp = new Date().toISOString();
    const dataStr = data ? ` ${JSON.stringify(data)}` : '';
    const colors = {
        DEBUG: '\x1b[90m',
        INFO: '\x1b[37m',
        WARN: '\x1b[33m',
        ERROR: '\x1b[31m'
    };
    const reset = '\x1b[0m';
    
    console.log(`${colors[level]}[${timestamp}] [${level.padEnd(5)}] [${component}] ${message}${dataStr}${reset}`);
}

const logger = {
    debug: (component, message, data) => log('DEBUG', component, message, data),
    info: (component, message, data) => log('INFO', component, message, data),
    warn: (component, message, data) => log('WARN', component, message, data),
    error: (component, message, data) => log('ERROR', component, message, data),
};

//=============================================================================
// STATE
//=============================================================================

let currentState = {
    active: 'neutral',
    confidence: 0,
    durationMs: 0,
    source: 'none',
    timestamp: Date.now(),
};

const stats = {
    totalConnections: 0,
    messagesSent: 0,
    messagesReceived: 0,
    startTime: Date.now(),
};

//=============================================================================
// HTTP SERVER
//=============================================================================

const httpServer = http.createServer((req, res) => {
    const url = new URL(req.url, `http://${req.headers.host}`);
    const path = url.pathname;
    const method = req.method;
    
    logger.debug('HTTP', `${method} ${path}`, { remoteAddress: req.socket.remoteAddress });
    
    // CORS headers
    res.setHeader('Access-Control-Allow-Origin', '*');
    res.setHeader('Access-Control-Allow-Methods', 'GET, POST, OPTIONS');
    res.setHeader('Access-Control-Allow-Headers', 'Content-Type');
    
    if (method === 'OPTIONS') {
        res.writeHead(204);
        res.end();
        return;
    }
    
    switch (path) {
        case '/state':
            handleStateRequest(req, res);
            break;
            
        case '/healthz':
            handleHealthRequest(req, res);
            break;
            
        case '/broadcast':
            if (method === 'POST') {
                handleBroadcastRequest(req, res);
            } else {
                sendMethodNotAllowed(res);
            }
            break;
            
        case '/metrics':
            handleMetricsRequest(req, res);
            break;
            
        default:
            handleInfoRequest(req, res);
            break;
    }
});

function handleStateRequest(req, res) {
    const state = {
        ...currentState,
        timestamp: Date.now(),
        durationMs: currentState.active !== 'neutral' 
            ? Date.now() - currentState.timestamp 
            : 0,
    };
    sendJson(res, state);
}

function handleHealthRequest(req, res) {
    sendJson(res, {
        status: 'ok',
        timestamp: Date.now(),
        connectedClients: wss.clients.size,
        uptime: Date.now() - stats.startTime,
    });
}

function handleBroadcastRequest(req, res) {
    let body = '';
    
    req.on('data', chunk => {
        body += chunk.toString();
        
        // Prevent too large payloads
        if (body.length > config.maxPayloadSize) {
            res.writeHead(413);
            res.end('Payload too large');
            req.destroy();
        }
    });
    
    req.on('end', () => {
        try {
            // Validate JSON
            const data = JSON.parse(body);
            
            // Update internal state
            if (data.action) {
                currentState = {
                    active: data.action,
                    confidence: data.confidence || 0,
                    durationMs: data.durationMs || 0,
                    source: data.source || 'broadcast',
                    timestamp: Date.now(),
                };
            }
            
            // Broadcast to all clients
            broadcast(body);
            
            res.writeHead(200);
            res.end('OK');
            
            logger.debug('HTTP', 'Broadcast received and forwarded', { 
                clients: wss.clients.size,
                action: data.action 
            });
        } catch (err) {
            logger.error('HTTP', 'Invalid broadcast payload', { error: err.message });
            res.writeHead(400);
            res.end('Invalid JSON');
        }
    });
}

function handleMetricsRequest(req, res) {
    sendJson(res, {
        totalConnections: stats.totalConnections,
        activeConnections: wss.clients.size,
        messagesSent: stats.messagesSent,
        messagesReceived: stats.messagesReceived,
        uptimeMs: Date.now() - stats.startTime,
    });
}

function handleInfoRequest(req, res) {
    const info = `
BCI Orchestrator WebSocket Server v2.0

Endpoints:
  WebSocket: ws://${config.host}:${config.port}/
  GET  /state   - Current state JSON
  GET  /healthz - Health check
  GET  /metrics - Server metrics
  POST /broadcast - Broadcast message to all clients

Connected Clients: ${wss.clients.size}
Total Connections: ${stats.totalConnections}
Messages Sent: ${stats.messagesSent}
Uptime: ${Math.floor((Date.now() - stats.startTime) / 1000)}s
`.trim();
    
    res.writeHead(200, { 'Content-Type': 'text/plain' });
    res.end(info);
}

function sendJson(res, data) {
    const json = JSON.stringify(data);
    res.writeHead(200, { 
        'Content-Type': 'application/json',
        'Content-Length': Buffer.byteLength(json)
    });
    res.end(json);
}

function sendMethodNotAllowed(res) {
    res.writeHead(405);
    res.end('Method Not Allowed');
}

//=============================================================================
// WEBSOCKET SERVER
//=============================================================================

const wss = new WebSocketServer({ 
    server: httpServer,
    maxPayload: config.maxPayloadSize,
});

wss.on('connection', (ws, req) => {
    const clientIP = req.socket.remoteAddress;
    stats.totalConnections++;
    
    logger.info('WS', `Client connected from ${clientIP}`, { 
        totalClients: wss.clients.size,
        connectionId: stats.totalConnections 
    });
    
    // Send current state on connect
    try {
        ws.send(JSON.stringify({
            type: 'state',
            ...currentState,
            timestamp: Date.now(),
        }));
    } catch (err) {
        logger.error('WS', 'Failed to send initial state', { error: err.message });
    }
    
    // Handle incoming messages
    ws.on('message', (data) => {
        stats.messagesReceived++;
        const message = data.toString();
        
        logger.debug('WS', 'Received message', { message: message.substring(0, 100) });
        
        // Handle ping
        if (message === 'ping') {
            try {
                ws.send('pong');
            } catch (err) {
                logger.error('WS', 'Failed to send pong', { error: err.message });
            }
            return;
        }
        
        // Handle state request
        if (message === 'state') {
            try {
                ws.send(JSON.stringify(currentState));
            } catch (err) {
                logger.error('WS', 'Failed to send state', { error: err.message });
            }
            return;
        }
        
        // Try to parse as JSON and broadcast
        try {
            const parsed = JSON.parse(message);
            
            // Update state if it's a brain event
            if (parsed.action) {
                currentState = {
                    active: parsed.action,
                    confidence: parsed.confidence || 0,
                    durationMs: parsed.durationMs || 0,
                    source: parsed.source || 'client',
                    timestamp: Date.now(),
                };
            }
            
            // Broadcast to other clients
            broadcastExcept(ws, message);
        } catch {
            logger.debug('WS', 'Non-JSON message received', { message });
        }
    });
    
    ws.on('close', (code, reason) => {
        logger.info('WS', `Client disconnected`, { 
            code, 
            reason: reason.toString(),
            remainingClients: wss.clients.size - 1 
        });
    });
    
    ws.on('error', (err) => {
        logger.error('WS', 'Client error', { error: err.message });
    });
});

wss.on('error', (err) => {
    logger.error('WS-SERVER', 'Server error', { error: err.message });
});

//=============================================================================
// BROADCAST FUNCTIONS
//=============================================================================

function broadcast(message) {
    let sent = 0;
    
    wss.clients.forEach((client) => {
        if (client.readyState === WebSocket.OPEN) {
            try {
                client.send(message);
                sent++;
                stats.messagesSent++;
            } catch (err) {
                logger.error('WS', 'Failed to send to client', { error: err.message });
            }
        }
    });
    
    logger.debug('WS', `Broadcast complete`, { sent, total: wss.clients.size });
}

function broadcastExcept(excludeClient, message) {
    let sent = 0;
    
    wss.clients.forEach((client) => {
        if (client !== excludeClient && client.readyState === WebSocket.OPEN) {
            try {
                client.send(message);
                sent++;
                stats.messagesSent++;
            } catch (err) {
                logger.error('WS', 'Failed to send to client', { error: err.message });
            }
        }
    });
    
    logger.debug('WS', `Broadcast (excluding sender) complete`, { sent });
}

//=============================================================================
// PING/PONG KEEPALIVE
//=============================================================================

const pingInterval = setInterval(() => {
    let alive = 0;
    let dead = 0;
    
    wss.clients.forEach((ws) => {
        if (ws.isAlive === false) {
            dead++;
            return ws.terminate();
        }
        
        ws.isAlive = false;
        ws.ping();
        alive++;
    });
    
    if (dead > 0) {
        logger.info('WS', `Ping sweep: terminated ${dead} dead connections`, { alive, dead });
    }
}, config.pingInterval);

wss.on('connection', (ws) => {
    ws.isAlive = true;
    ws.on('pong', () => {
        ws.isAlive = true;
    });
});

//=============================================================================
// STARTUP
//=============================================================================

httpServer.listen(config.port, config.host, () => {
    logger.info('SERVER', '═══════════════════════════════════════════════════════');
    logger.info('SERVER', '  BCI WEBSOCKET SERVER v2.0 STARTED');
    logger.info('SERVER', '═══════════════════════════════════════════════════════');
    logger.info('SERVER', `HTTP/WebSocket listening on ${config.host}:${config.port}`);
    logger.info('SERVER', `  WebSocket: ws://${config.host}:${config.port}/`);
    logger.info('SERVER', `  HTTP GET:  http://${config.host}:${config.port}/state`);
    logger.info('SERVER', `  HTTP GET:  http://${config.host}:${config.port}/healthz`);
    logger.info('SERVER', `  HTTP POST: http://${config.host}:${config.port}/broadcast`);
    logger.info('SERVER', `Log Level: ${config.logLevel}`);
});

//=============================================================================
// GRACEFUL SHUTDOWN
//=============================================================================

function shutdown(signal) {
    logger.info('SERVER', `Received ${signal}, shutting down gracefully...`);
    
    clearInterval(pingInterval);
    
    // Close all WebSocket connections
    wss.clients.forEach((ws) => {
        try {
            ws.close(1001, 'Server shutting down');
        } catch (err) {
            logger.error('SERVER', 'Error closing client', { error: err.message });
        }
    });
    
    // Close HTTP server
    httpServer.close(() => {
        logger.info('SERVER', 'Server closed');
        process.exit(0);
    });
    
    // Force exit after 5 seconds
    setTimeout(() => {
        logger.warn('SERVER', 'Forcing exit after timeout');
        process.exit(1);
    }, 5000);
}

process.on('SIGTERM', () => shutdown('SIGTERM'));
process.on('SIGINT', () => shutdown('SIGINT'));
process.on('uncaughtException', (err) => {
    logger.error('SERVER', 'Uncaught exception', { error: err.message, stack: err.stack });
    shutdown('uncaughtException');
});
