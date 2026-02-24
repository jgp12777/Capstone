using UnityEngine;
using UnityEngine.Networking;
using System.Collections;
using System.Collections.Generic;

/// <summary>
/// 2D orb controller for brain-computer interface with L, R, U, D commands
/// Auto-continues in straight lines until reaching an intersection
/// </summary>
[RequireComponent(typeof(Rigidbody2D))]
[RequireComponent(typeof(CircleCollider2D))]
public class OrbController2D : MonoBehaviour
{
    [Header("Movement Settings")]
    [SerializeField] private float moveDistance = 1f;
    [SerializeField] private float moveSpeed = 5f;

    [Header("Server Settings")]
    [SerializeField] private string serverUrl = "http://localhost:8080/command";
    [SerializeField] private float pollInterval = 0.1f;

    [Header("Visual Effects")]
    [SerializeField] private ParticleSystem moveParticles;
    [SerializeField] private TrailRenderer trailRenderer;
    [SerializeField] private SpriteRenderer orbSprite;
    [SerializeField] private AnimationCurve moveCurve = AnimationCurve.EaseInOut(0, 0, 1, 1);

    [Header("Audio")]
    [SerializeField] private AudioSource audioSource;
    [SerializeField] private AudioClip moveSound;
    [SerializeField] private AudioClip collectSound;
    [SerializeField] private AudioClip errorSound;

    [Header("Visual Settings")]
    [SerializeField] private Color normalColor = Color.cyan;
    [SerializeField] private Color movingColor = Color.white;
    [SerializeField] private float rotationSpeed = 360f;
    [SerializeField] private float pulseSpeed = 2f;
    [SerializeField] private float pulseAmount = 0.1f;

    [Header("Collectible Detection")]
    [SerializeField] private float collectRadius = 0.4f;

    // Private variables
    private Vector2 targetPosition;
    private Vector2 startPosition;
    private bool isMoving = false;
    private float moveProgress = 0f;
    private Queue<string> commandQueue = new Queue<string>();
    private Rigidbody2D rb;
    private Vector2 moveDirection;
    private Vector2 lastMoveDirection = Vector2.zero;
    private Vector3 baseScale;
    private LayerMask wallLayer;
    private bool canMove = true;

    void Start()
    {
        // Setup Rigidbody2D
        rb = GetComponent<Rigidbody2D>();
        rb.bodyType = RigidbodyType2D.Dynamic;
        rb.simulated = true;
        rb.gravityScale = 0;
        rb.constraints = RigidbodyConstraints2D.FreezeRotation;

        // Get sprite renderer
        if (orbSprite == null)
        {
            orbSprite = GetComponent<SpriteRenderer>();
        }

        // Store base scale for pulse animation
        baseScale = transform.localScale;

        // Setup wall layer for raycasts
        wallLayer = LayerMask.GetMask("Wall");

        // Initialize positions
        Vector2 currentPos = transform.position;
        targetPosition = currentPos;
        startPosition = currentPos;

        Debug.Log("==============================================");
        Debug.Log("<color=cyan>[2D ORB] Initialized!</color>");
        Debug.Log($"<color=cyan>[CONFIG] Server URL: {serverUrl}</color>");
        Debug.Log($"<color=cyan>[CONFIG] Starting Position: {currentPos}</color>");
        Debug.Log($"<color=cyan>[CONFIG] Wall LayerMask: {wallLayer.value}</color>");
        Debug.Log("==============================================");

        StartCoroutine(PollForCommands());
    }

    void Update()
    {
        // Check for collectibles at current position
        CheckForCollectibles();

        if (isMoving)
        {
            // Smooth movement with animation curve
            moveProgress += Time.deltaTime * moveSpeed;
            float curvedProgress = moveCurve.Evaluate(Mathf.Clamp01(moveProgress));

            Vector2 newPos = Vector2.Lerp(startPosition, targetPosition, curvedProgress);
            rb.MovePosition(newPos);

            // Rotate orb during movement
            transform.Rotate(0, 0, rotationSpeed * Time.deltaTime);

            // Check if movement complete
            if (moveProgress >= 1f)
            {
                OnMovementComplete();
            }
        }
        else
        {
            // Idle animation - gentle pulse
            float pulse = 1f + Mathf.Sin(Time.time * pulseSpeed) * pulseAmount;
            transform.localScale = baseScale * pulse;
        }
    }

    void OnMovementComplete()
    {
        Vector2 currentPos = transform.position;
        isMoving = false;
        moveProgress = 0f;

        OnMoveComplete();

        // Check if we should auto-continue or stop
        bool atIntersection = IsAtIntersection(currentPos);
        bool canContinue = CanMoveInDirection(currentPos, moveDirection);

        if (!atIntersection && canContinue && commandQueue.Count == 0)
        {
            // Keep moving in same direction (straightaway)
            Debug.Log($"<color=yellow>[AUTO-CONTINUE] Moving {moveDirection}</color>");
            startPosition = currentPos;
            targetPosition = currentPos + moveDirection * moveDistance;
            isMoving = true;
            moveProgress = 0f;
            OnMoveStart();
        }
        else if (atIntersection)
        {
            // Stop at intersection, wait for input
            Debug.Log($"<color=cyan>[INTERSECTION] Waiting for input</color>");
            lastMoveDirection = Vector2.zero;

            if (commandQueue.Count > 0 && canMove)
            {
                ProcessCommand(commandQueue.Dequeue());
            }
        }
        else if (!canContinue)
        {
            // Hit a wall, stop
            Debug.Log($"<color=red>[WALL] Stopped by obstacle</color>");
            lastMoveDirection = Vector2.zero;

            if (commandQueue.Count > 0 && canMove)
            {
                ProcessCommand(commandQueue.Dequeue());
            }
        }
        else
        {
            // Has queued commands, process them
            if (commandQueue.Count > 0 && canMove)
            {
                ProcessCommand(commandQueue.Dequeue());
            }
        }
    }

    void CheckForCollectibles()
    {
        Collider2D[] hits = Physics2D.OverlapCircleAll(transform.position, collectRadius);
        foreach (Collider2D hit in hits)
        {
            if (hit.gameObject == gameObject) continue; // Skip self

            if (hit.CompareTag("Collectible"))
            {
                Collectible2 collectible = hit.GetComponent<Collectible2>();
                if (collectible != null)
                {
                    collectible.Collect();
                    PlaySound(collectSound);
                }
            }
            else if (hit.CompareTag("Goal"))
            {
                MazeManager2D.Instance?.OnGoalReached();
            }
        }
    }

    bool IsAtIntersection(Vector2 position)
    {
        // Count how many directions are open
        int openDirections = 0;
        Vector2[] directions = { Vector2.up, Vector2.down, Vector2.left, Vector2.right };

        foreach (Vector2 dir in directions)
        {
            if (CanMoveInDirection(position, dir))
            {
                openDirections++;
            }
        }

        // More than 2 open directions = intersection (T or + junction)
        if (openDirections > 2)
        {
            return true;
        }

        // Check if we can turn perpendicular to current direction
        if (lastMoveDirection != Vector2.zero)
        {
            Vector2 perpendicular1 = new Vector2(-lastMoveDirection.y, lastMoveDirection.x);
            Vector2 perpendicular2 = new Vector2(lastMoveDirection.y, -lastMoveDirection.x);

            bool canTurnLeft = CanMoveInDirection(position, perpendicular1);
            bool canTurnRight = CanMoveInDirection(position, perpendicular2);

            // If we can turn, it's an intersection
            if (canTurnLeft || canTurnRight)
            {
                return true;
            }
        }

        return false;
    }

    bool CanMoveInDirection(Vector2 position, Vector2 direction)
    {
        // Raycast to check for walls
        RaycastHit2D hit = Physics2D.Raycast(position, direction, moveDistance + 0.1f, wallLayer);
        return (hit.collider == null);
    }

    IEnumerator PollForCommands()
    {
        bool hasShownConnectionError = false;

        while (true)
        {
            yield return new WaitForSeconds(pollInterval);

            UnityWebRequest request = UnityWebRequest.Get(serverUrl);
            yield return request.SendWebRequest();

            if (request.result == UnityWebRequest.Result.Success)
            {
                string command = request.downloadHandler.text.Trim().ToUpper();
                if (!string.IsNullOrEmpty(command))
                {
                    Debug.Log($"<color=green>[COMMAND] Received: '{command}'</color>");
                    ReceiveCommand(command);
                }
                hasShownConnectionError = false;
            }
            else if (request.result == UnityWebRequest.Result.ConnectionError)
            {
                if (!hasShownConnectionError)
                {
                    Debug.LogWarning($"<color=yellow>[CONNECTION] Waiting for server at {serverUrl}</color>");
                    hasShownConnectionError = true;
                }
            }
            else
            {
                Debug.LogError($"<color=red>[ERROR] {request.error}</color>");
            }
        }
    }

    public void ReceiveCommand(string command)
    {
        command = command.Trim().ToUpper();

        if (IsValidCommand(command))
        {
            if (!canMove)
            {
                Debug.Log($"<color=orange>[BLOCKED] Cannot move right now</color>");
                PlaySound(errorSound);
                return;
            }

            commandQueue.Enqueue(command);
            Debug.Log($"<color=cyan>[QUEUED] '{command}' | Queue: {commandQueue.Count}</color>");

            // Start processing if not already moving
            if (!isMoving && commandQueue.Count == 1)
            {
                ProcessCommand(commandQueue.Dequeue());
            }
        }
        else
        {
            Debug.LogWarning($"<color=orange>[INVALID] '{command}' - Use L/R/U/D</color>");
            PlaySound(errorSound);
        }
    }

    bool IsValidCommand(string command)
    {
        return command == "L" || command == "R" || command == "U" || command == "D" || command == "STOP";
    }

    void ProcessCommand(string command)
    {
        if (command == "STOP")
        {
            commandQueue.Clear();
            lastMoveDirection = Vector2.zero;
            Debug.Log("<color=yellow>[STOP] Command queue cleared</color>");
            return;
        }

        Vector2 currentPos = transform.position;
        Vector2 intendedPosition;
        Vector2 direction = Vector2.zero;

        switch (command)
        {
            case "L":
                direction = Vector2.left;
                break;
            case "R":
                direction = Vector2.right;
                break;
            case "U":
                direction = Vector2.up;
                break;
            case "D":
                direction = Vector2.down;
                break;
        }

        intendedPosition = currentPos + direction * moveDistance;

        // Set up movement
        startPosition = currentPos;
        targetPosition = intendedPosition;
        moveDirection = direction;
        lastMoveDirection = direction;
        isMoving = true;
        moveProgress = 0f;

        OnMoveStart();

        Debug.Log($"<color=lime>[MOVING] {command}: {startPosition} → {targetPosition}</color>");
        PlaySound(moveSound);
    }

    void OnMoveStart()
    {
        if (moveParticles != null)
        {
            moveParticles.Play();
        }

        if (orbSprite != null)
        {
            orbSprite.color = movingColor;
        }
    }

    void OnMoveComplete()
    {
        if (moveParticles != null)
        {
            moveParticles.Stop();
        }

        if (orbSprite != null)
        {
            orbSprite.color = normalColor;
        }
    }

    void PlaySound(AudioClip clip)
    {
        if (audioSource != null && clip != null)
        {
            audioSource.PlayOneShot(clip);
        }
    }

    public void SetCanMove(bool canMove)
    {
        this.canMove = canMove;
    }

    public void ResetToStart()
    {
        StopAllCoroutines();
        commandQueue.Clear();
        transform.position = startPosition;
        targetPosition = startPosition;
        isMoving = false;
        moveProgress = 0f;
        lastMoveDirection = Vector2.zero;
        StartCoroutine(PollForCommands());
    }
}