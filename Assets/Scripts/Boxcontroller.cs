using UnityEngine;
using UnityEngine.Networking;
using System.Collections;
using System.Collections.Generic;

/// <summary>
/// Controls a box that responds to commands from an external HTTP wrapper
/// Commands: L (left), R (right), U (up), D (down)
/// </summary>
public class BoxController : MonoBehaviour
{
    [Header("Movement Settings")]
    [SerializeField] private float moveDistance = 1f;
    [SerializeField] private float moveSpeed = 5f;

    [Header("Server Settings")]
    [SerializeField] private string serverUrl = "http://localhost:8080/command";
    [SerializeField] private float pollInterval = 0.1f;

    private Vector3 targetPosition;
    private bool isMoving = false;
    private Queue<string> commandQueue = new Queue<string>();

    void Start()
    {
        targetPosition = transform.position;
        Debug.Log("==============================================");
        Debug.Log("<color=white>[BOXCONTROLLER] Started successfully!</color>");
        Debug.Log($"<color=white>[CONFIG] Server URL: {serverUrl}</color>");
        Debug.Log($"<color=white>[CONFIG] Poll Interval: {pollInterval}s</color>");
        Debug.Log($"<color=white>[CONFIG] Move Distance: {moveDistance}</color>");
        Debug.Log($"<color=white>[CONFIG] Move Speed: {moveSpeed}</color>");
        Debug.Log($"<color=white>[CONFIG] Starting Position: {targetPosition}</color>");
        Debug.Log("==============================================");
        StartCoroutine(PollForCommands());
    }

    void Update()
    {
        // Smooth movement to target position
        if (Vector3.Distance(transform.position, targetPosition) > 0.01f)
        {
            transform.position = Vector3.Lerp(transform.position, targetPosition, Time.deltaTime * moveSpeed);
            isMoving = true;
        }
        else
        {
            transform.position = targetPosition;
            isMoving = false;

            // Process next command in queue if not moving
            if (commandQueue.Count > 0)
            {
                ProcessCommand(commandQueue.Dequeue());
            }
        }
    }

    /// <summary>
    /// Polls the HTTP server for new commands
    /// </summary>
    IEnumerator PollForCommands()
    {
        Debug.Log("[POLLING] Starting to poll server...");
        Debug.Log($"[POLLING] Target URL: {serverUrl}");

        int pollCount = 0;
        bool hasShownConnectionError = false;

        while (true)
        {
            yield return new WaitForSeconds(pollInterval);

            pollCount++;

            UnityWebRequest request = UnityWebRequest.Get(serverUrl);
            yield return request.SendWebRequest();

            if (request.result == UnityWebRequest.Result.Success)
            {
                string command = request.downloadHandler.text.Trim().ToUpper();
                if (!string.IsNullOrEmpty(command))
                {
                    Debug.Log($"<color=green>[POLL SUCCESS] Received command from server: '{command}'</color>");
                    ReceiveCommand(command);
                }
                // Don't log empty responses - too much spam

                // Reset connection error flag if we successfully connected
                hasShownConnectionError = false;
            }
            else if (request.result == UnityWebRequest.Result.ConnectionError)
            {
                // Only show connection error once, not repeatedly
                if (!hasShownConnectionError)
                {
                    Debug.LogWarning($"<color=yellow>[CONNECTION ERROR] Cannot connect to server at {serverUrl}. Is the Python server running?</color>");
                    hasShownConnectionError = true;
                }
            }
            else
            {
                Debug.LogError($"<color=red>[POLL ERROR] Failed to poll server: {request.error}</color>");
            }
        }
    }

    /// <summary>
    /// Receives a command from the external wrapper
    /// </summary>
    public void ReceiveCommand(string command)
    {
        command = command.Trim().ToUpper();

        if (IsValidCommand(command))
        {
            commandQueue.Enqueue(command);
            Debug.Log($"<color=cyan>[COMMAND QUEUED] Command '{command}' added to queue. Queue size: {commandQueue.Count}</color>");
        }
        else
        {
            Debug.LogWarning($"<color=orange>[INVALID COMMAND] Received invalid command: '{command}'. Valid commands are L, R, U, D</color>");
        }
    }

    /// <summary>
    /// Validates if the command is one of L, R, U, D
    /// </summary>
    bool IsValidCommand(string command)
    {
        return command == "L" || command == "R" || command == "U" || command == "D";
    }

    /// <summary>
    /// Processes a command and updates the target position
    /// </summary>
    void ProcessCommand(string command)
    {
        Vector3 oldPosition = targetPosition;

        switch (command)
        {
            case "L":
                targetPosition += Vector3.left * moveDistance;
                break;
            case "R":
                targetPosition += Vector3.right * moveDistance;
                break;
            case "U":
                targetPosition += Vector3.up * moveDistance;
                break;
            case "D":
                targetPosition += Vector3.down * moveDistance;
                break;
        }

        Debug.Log($"<color=lime>[EXECUTING] Moving '{command}' from {oldPosition} to {targetPosition}</color>");
    }

    /// <summary>
    /// Alternative: Use this for WebSocket connection instead of polling
    /// </summary>
    public void ReceiveCommandDirect(string command)
    {
        ReceiveCommand(command);
    }
}