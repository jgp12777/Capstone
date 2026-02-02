#!/usr/bin/env python3
"""
Simple HTTP server that acts as a wrapper between external input and Unity
Run this server, then send commands via HTTP POST to store them
Unity will poll GET requests to retrieve the latest command
"""

from http.server import HTTPServer, BaseHTTPRequestHandler
import json
from urllib.parse import parse_qs
import threading
import time


# Shared state - must be outside the class to persist between requests
class SharedState:
    def __init__(self):
        self.current_command = ""
        self.last_command_time = 0
        self.lock = threading.Lock()


# Global shared state
shared_state = SharedState()


class CommandServer(BaseHTTPRequestHandler):

    def do_GET(self):
        """Handle GET requests from Unity to retrieve commands"""
        if self.path == '/command':
            with shared_state.lock:
                command = shared_state.current_command
                # Clear the command after reading so Unity doesn't process it multiple times
                if command:
                    shared_state.current_command = ""

            self.send_response(200)
            self.send_header('Content-type', 'text/plain')
            self.send_header('Access-Control-Allow-Origin', '*')
            self.end_headers()
            self.wfile.write(command.encode())

            # Always log GET requests so we can see Unity polling
            if command:
                print(f"[GET] Unity polled - Returning command: '{command}' (cleared)")
            else:
                print(f"[GET] Unity polled - No command waiting (empty response)")

        else:
            self.send_response(404)
            self.end_headers()

    def do_POST(self):
        """Handle POST requests to receive new commands"""
        if self.path == '/command':
            content_length = int(self.headers['Content-Length'])
            post_data = self.rfile.read(content_length).decode()

            # Parse command from POST data
            try:
                # Try JSON format
                data = json.loads(post_data)
                command = data.get('command', '').upper()
            except json.JSONDecodeError:
                # Try form data or plain text
                if '=' in post_data:
                    parsed = parse_qs(post_data)
                    command = parsed.get('command', [''])[0].upper()
                else:
                    command = post_data.strip().upper()

            # Validate command
            if command in ['L', 'R', 'U', 'D']:
                with shared_state.lock:
                    shared_state.current_command = command
                    shared_state.last_command_time = time.time()

                self.send_response(200)
                self.send_header('Content-type', 'application/json')
                self.send_header('Access-Control-Allow-Origin', '*')
                self.end_headers()
                response = json.dumps({'status': 'success', 'command': command})
                self.wfile.write(response.encode())
                print(f"[POST] ✓ Command received and stored: '{command}'")
            else:
                self.send_response(400)
                self.send_header('Content-type', 'application/json')
                self.end_headers()
                response = json.dumps({'status': 'error', 'message': 'Invalid command. Use L, R, U, or D'})
                self.wfile.write(response.encode())
                print(f"[POST] ✗ Invalid command rejected: '{command}'")
        else:
            self.send_response(404)
            self.end_headers()

    def do_OPTIONS(self):
        """Handle CORS preflight requests"""
        self.send_response(200)
        self.send_header('Access-Control-Allow-Origin', '*')
        self.send_header('Access-Control-Allow-Methods', 'GET, POST, OPTIONS')
        self.send_header('Access-Control-Allow-Headers', 'Content-Type')
        self.end_headers()

    def log_message(self, format, *args):
        """Suppress default logging to keep output clean"""
        pass


def clear_old_commands():
    """Background thread to clear commands after 2 seconds"""
    while True:
        time.sleep(0.5)
        with shared_state.lock:
            if shared_state.current_command and (time.time() - shared_state.last_command_time > 2.0):
                old_command = shared_state.current_command
                shared_state.current_command = ""
                print(f"[AUTO-CLEAR] Cleared old command: '{old_command}' (2s timeout)")


def run_server(port=8080):
    server_address = ('', port)
    httpd = HTTPServer(server_address, CommandServer)

    # Start background thread to clear old commands
    clear_thread = threading.Thread(target=clear_old_commands, daemon=True)
    clear_thread.start()

    print("=" * 60)
    print(f"Command Server Running on Port {port}")
    print("=" * 60)
    print(f"POST commands to: http://localhost:{port}/command")
    print(f"Unity polls from:  http://localhost:{port}/command")
    print()
    print("Example usage:")
    print(f"  curl -X POST http://localhost:{port}/command -d 'command=R'")
    print(f"  python test_client_simple.py R")
    print()
    print("Waiting for commands...")
    print("=" * 60)

    httpd.serve_forever()


if __name__ == '__main__':
    run_server(8080)