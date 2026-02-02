#!/usr/bin/env python3
"""
Simple test client to send commands to the Unity command server
Usage: python test_client.py [L|R|U|D]
"""

import requests
import sys
import time

def send_command(command, server_url="http://localhost:8080/command"):
    """Send a command to the server"""
    try:
        response = requests.post(server_url, json={"command": command})
        if response.status_code == 200:
            print(f"✓ Command '{command}' sent successfully")
            print(f"  Response: {response.json()}")
            return True
        else:
            print(f"✗ Error sending command '{command}'")
            print(f"  Status: {response.status_code}")
            print(f"  Response: {response.text}")
            return False
    except requests.exceptions.ConnectionError:
        print("✗ Could not connect to server. Is it running?")
        return False
    except Exception as e:
        print(f"✗ Error: {e}")
        return False

def interactive_mode():
    """Run in interactive mode"""
    print("=== Unity Command Test Client ===")
    print("Commands: L (left), R (right), U (up), D (down), Q (quit)")
    print("Server: http://localhost:8080/command")
    print()
    
    while True:
        try:
            command = input("Enter command: ").strip().upper()
            
            if command == 'Q':
                print("Goodbye!")
                break
            elif command in ['L', 'R', 'U', 'D']:
                send_command(command)
            elif command == '':
                continue
            else:
                print(f"Invalid command: {command}")
                print("Use L, R, U, D, or Q to quit")
        except KeyboardInterrupt:
            print("\nGoodbye!")
            break

def sequence_mode(commands):
    """Send a sequence of commands"""
    print(f"=== Sending command sequence: {commands} ===")
    for command in commands:
        command = command.upper()
        if command in ['L', 'R', 'U', 'D']:
            send_command(command)
            time.sleep(0.5)  # Brief delay between commands
        else:
            print(f"Skipping invalid command: {command}")

if __name__ == '__main__':
    if len(sys.argv) > 1:
        # Command line mode
        if len(sys.argv) == 2 and len(sys.argv[1]) > 1:
            # Sequence mode: python test_client.py RRRUUUDDD
            sequence_mode(sys.argv[1])
        else:
            # Single command mode: python test_client.py R
            for command in sys.argv[1:]:
                send_command(command.upper())
                if len(sys.argv) > 2:
                    time.sleep(0.5)
    else:
        # Interactive mode
        interactive_mode()
