```bash
using UnityEngine;
using System;
using System.Net.WebSockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Collections.Concurrent;

public class PCWebSocketClient2D : MonoBehaviour
{
    [Header("WebSocket Settings")]
    public string serverUrl = "ws://127.0.0.1:8080";

    [Header("Movement Settings")]
    public float moveSpeed = 10f;

    private ClientWebSocket ws;
    private CancellationTokenSource cancellation;
    private ConcurrentQueue<string> messageQueue = new ConcurrentQueue<string>();
    private Rigidbody2D rb;

    // Store the current movement direction
    private Vector2 moveDirection = Vector2.zero;

    void Start()
    {
        rb = GetComponent<Rigidbody2D>();
        if (rb == null)
        {
            Debug.LogError("Rigidbody2D not found! Please attach one to this GameObject.");
            return;
        }

        ws = new ClientWebSocket();
        cancellation = new CancellationTokenSource();

        // Connect to Node WebSocket server
        ConnectWebSocket();
    }

    async void ConnectWebSocket()
    {
        try
        {
            await ws.ConnectAsync(new Uri(serverUrl), cancellation.Token);
            Debug.Log("Connected to WebSocket server");
            _ = ReceiveLoop();
        }
        catch (Exception e)
        {
            Debug.LogError("WebSocket connection failed: " + e.Message);
        }
    }

    async Task ReceiveLoop()
    {
        var buffer = new byte[1024];

        while (ws.State == WebSocketState.Open)
        {
            try
            {
                var result = await ws.ReceiveAsync(new ArraySegment<byte>(buffer), cancellation.Token);

                if (result.MessageType == WebSocketMessageType.Text)
                {
                    string message = Encoding.UTF8.GetString(buffer, 0, result.Count);
                    messageQueue.Enqueue(message);
                }
            }
            catch (Exception e)
            {
                Debug.LogError("WebSocket receive error: " + e.Message);
                break;
            }
        }
    }

    void Update()
    {
        // Process all messages received this frame
        while (messageQueue.TryDequeue(out string message))
        {
            HandleMessage(message);
        }
    }

    void FixedUpdate()
    {
        // Move the cube continuously in the current direction
        if (moveDirection != Vector2.zero)
        {
            rb.MovePosition(rb.position + moveDirection * moveSpeed * Time.fixedDeltaTime);
        }
    }

    void HandleMessage(string message)
    {
        Debug.Log("Received message: " + message);

        try
        {
            BCICommand cmd = JsonUtility.FromJson<BCICommand>(message);
            Debug.Log($"Parsed command: {cmd.command}, confidence: {cmd.confidence}");

            if (cmd.confidence < 0.6f)
            {
                Debug.Log("Low confidence, ignoring command");
                return;
            }

            // Update moveDirection based on the command
            switch (cmd.command.ToLower())
            {
                case "right":
                    moveDirection = Vector2.right;
                    break;
                case "left":
                    moveDirection = Vector2.left;
                    break;
                case "push":
                    moveDirection = Vector2.up;
                    break;
                case "pull":
                    moveDirection = Vector2.down;
                    break;
                case "neutral":
                    moveDirection = Vector2.zero; // stop moving
                    break;
                default:
                    moveDirection = Vector2.zero;
                    Debug.Log("Unrecognized command: " + cmd.command);
                    break;
            }
        }
        catch (Exception e)
        {
            Debug.LogError("Failed to parse JSON: " + e.Message);
        }
    }

    async void OnApplicationQuit()
    {
        cancellation.Cancel();

        if (ws != null && ws.State == WebSocketState.Open)
        {
            await ws.CloseAsync(WebSocketCloseStatus.NormalClosure, "Closing", CancellationToken.None);
        }
    }
}

[Serializable]
public class BCICommand
{
    public string command;
    public float confidence;
}

```