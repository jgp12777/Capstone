#!/usr/bin/env python3
"""
BCI Test Sender - UDP Test Utility

Sends test UDP packets to the BCI Orchestrator for testing without an Emotiv headset.

Usage:
    python test_sender.py                    # Interactive mode
    python test_sender.py --sequence         # Run test sequence
    python test_sender.py push 0.85          # Single command
"""

import socket
import time
import argparse
import sys
from datetime import datetime

# Configuration
DEFAULT_HOST = "127.0.0.1"
DEFAULT_PORT = 7400

# Available actions
ACTIONS = ["push", "pull", "left", "right", "lift", "drop", "neutral"]

def create_socket():
    """Create UDP socket"""
    return socket.socket(socket.AF_INET, socket.SOCK_DGRAM)

def send_command(sock, host, port, action, confidence):
    """Send a single command"""
    message = f"{action},{confidence:.2f}"
    sock.sendto(message.encode('utf-8'), (host, port))
    timestamp = datetime.now().strftime("%H:%M:%S.%f")[:-3]
    print(f"[{timestamp}] Sent: {message}")

def run_sequence(sock, host, port):
    """Run predefined test sequence"""
    print("\n🧪 Running test sequence...")
    print("-" * 40)
    
    sequence = [
        ("neutral", 0.3, 1.0),
        ("push", 0.85, 2.0),
        ("neutral", 0.4, 0.5),
        ("left", 0.75, 2.0),
        ("neutral", 0.3, 0.5),
        ("right", 0.80, 2.0),
        ("neutral", 0.4, 0.5),
        ("pull", 0.70, 2.0),
        ("neutral", 0.3, 1.0),
        ("lift", 0.90, 1.0),
        ("neutral", 0.3, 1.0),
        ("drop", 0.88, 1.0),
        ("neutral", 0.3, 2.0),
    ]
    
    for action, confidence, delay in sequence:
        send_command(sock, host, port, action, confidence)
        time.sleep(delay)
    
    print("-" * 40)
    print("✓ Test sequence complete\n")

def run_continuous(sock, host, port, action, confidence, frequency):
    """Run continuous broadcast"""
    print(f"\n📡 Broadcasting '{action}' at {frequency}Hz...")
    print("Press Ctrl+C to stop\n")
    
    interval = 1.0 / frequency
    
    try:
        while True:
            send_command(sock, host, port, action, confidence)
            time.sleep(interval)
    except KeyboardInterrupt:
        print("\n✓ Stopped")

def run_ramp(sock, host, port, action):
    """Ramp confidence up and down"""
    print(f"\n📈 Ramping '{action}' confidence...")
    
    # Ramp up
    for i in range(0, 101, 5):
        conf = i / 100.0
        send_command(sock, host, port, action, conf)
        time.sleep(0.1)
    
    # Hold
    time.sleep(1.0)
    
    # Ramp down
    for i in range(100, -1, -5):
        conf = i / 100.0
        send_command(sock, host, port, action, conf)
        time.sleep(0.1)
    
    send_command(sock, host, port, "neutral", 0.3)
    print("✓ Ramp complete")

def interactive_mode(sock, host, port):
    """Run interactive command mode"""
    print("\n🎮 Interactive Mode")
    print("=" * 50)
    print("Commands:")
    print("  action,confidence  - Send command (e.g., push,0.85)")
    print("  sequence          - Run test sequence")
    print("  ramp ACTION       - Ramp confidence for action")
    print("  continuous ACTION CONF HZ - Continuous broadcast")
    print("  help              - Show this help")
    print("  quit              - Exit")
    print("=" * 50)
    print(f"Sending to {host}:{port}")
    print()
    
    while True:
        try:
            user_input = input(">>> ").strip().lower()
            
            if not user_input:
                continue
            
            if user_input in ["quit", "exit", "q"]:
                print("Goodbye!")
                break
            
            if user_input == "help":
                print(f"Actions: {', '.join(ACTIONS)}")
                print("Confidence: 0.0 to 1.0")
                continue
            
            if user_input == "sequence":
                run_sequence(sock, host, port)
                continue
            
            if user_input.startswith("ramp "):
                action = user_input.split()[1]
                if action in ACTIONS:
                    run_ramp(sock, host, port, action)
                else:
                    print(f"Unknown action. Available: {', '.join(ACTIONS)}")
                continue
            
            if user_input.startswith("continuous "):
                parts = user_input.split()
                if len(parts) == 4:
                    action = parts[1]
                    conf = float(parts[2])
                    freq = float(parts[3])
                    run_continuous(sock, host, port, action, conf, freq)
                else:
                    print("Usage: continuous ACTION CONFIDENCE FREQUENCY")
                continue
            
            # Parse action,confidence
            if "," in user_input:
                parts = user_input.split(",")
                if len(parts) == 2:
                    action = parts[0].strip()
                    try:
                        confidence = float(parts[1].strip())
                        if 0 <= confidence <= 1:
                            send_command(sock, host, port, action, confidence)
                        else:
                            print("Confidence must be between 0.0 and 1.0")
                    except ValueError:
                        print("Invalid confidence value")
                else:
                    print("Format: action,confidence (e.g., push,0.85)")
            else:
                print("Unknown command. Type 'help' for options.")
                
        except KeyboardInterrupt:
            print("\nGoodbye!")
            break
        except Exception as e:
            print(f"Error: {e}")

def main():
    parser = argparse.ArgumentParser(
        description="BCI Test Sender - Send test UDP packets",
        formatter_class=argparse.RawDescriptionHelpFormatter,
        epilog="""
Examples:
  python test_sender.py                     # Interactive mode
  python test_sender.py --sequence          # Run test sequence
  python test_sender.py push 0.85           # Single command
  python test_sender.py --host 192.168.1.10 # Different host
        """
    )
    
    parser.add_argument("action", nargs="?", help="Action to send")
    parser.add_argument("confidence", nargs="?", type=float, help="Confidence (0.0-1.0)")
    parser.add_argument("--host", default=DEFAULT_HOST, help=f"Target host (default: {DEFAULT_HOST})")
    parser.add_argument("--port", type=int, default=DEFAULT_PORT, help=f"Target port (default: {DEFAULT_PORT})")
    parser.add_argument("--sequence", action="store_true", help="Run test sequence")
    parser.add_argument("--continuous", action="store_true", help="Continuous mode")
    parser.add_argument("--frequency", type=float, default=10, help="Frequency for continuous mode (Hz)")
    
    args = parser.parse_args()
    
    sock = create_socket()
    
    print(f"\n🧠 BCI Test Sender v2.0")
    print(f"Target: {args.host}:{args.port}")
    
    try:
        if args.sequence:
            run_sequence(sock, args.host, args.port)
        elif args.action and args.confidence is not None:
            if args.continuous:
                run_continuous(sock, args.host, args.port, args.action, args.confidence, args.frequency)
            else:
                send_command(sock, args.host, args.port, args.action, args.confidence)
        else:
            interactive_mode(sock, args.host, args.port)
    finally:
        sock.close()

if __name__ == "__main__":
    main()
