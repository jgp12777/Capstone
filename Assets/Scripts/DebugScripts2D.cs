using UnityEngine;

/// <summary>
/// Development testing script for controlling the 2D orb with keyboard
/// Use this to test your game without the BCI hardware
/// </summary>
public class KeyboardController2DDebug : MonoBehaviour
{
    [Header("References")]
    [SerializeField] private OrbController2D orbController;
    
    [Header("Key Bindings")]
    [SerializeField] private KeyCode upKey = KeyCode.W;
    [SerializeField] private KeyCode downKey = KeyCode.S;
    [SerializeField] private KeyCode leftKey = KeyCode.A;
    [SerializeField] private KeyCode rightKey = KeyCode.D;
    [SerializeField] private KeyCode stopKey = KeyCode.Space;
    
    [Header("Alternative Arrow Keys")]
    [SerializeField] private bool useArrowKeys = true;
    
    [Header("Debug Options")]
    [SerializeField] private bool showDebugUI = true;
    [SerializeField] private bool logCommands = true;

    void Start()
    {
        if (orbController == null)
        {
            orbController = FindObjectOfType<OrbController2D>();
            if (orbController == null)
            {
                Debug.LogError("<color=red>[DEBUG] No OrbController2D found!</color>");
            }
        }
        
        Debug.Log("<color=yellow>[2D DEBUG] Keyboard controls enabled!</color>");
        Debug.Log($"<color=yellow>[CONTROLS] {upKey}=Up, {downKey}=Down, {leftKey}=Left, {rightKey}=Right</color>");
    }

    void Update()
    {
        if (orbController == null) return;

        // WASD or custom keys
        if (Input.GetKeyDown(upKey))
        {
            SendCommand("U");
        }
        else if (Input.GetKeyDown(downKey))
        {
            SendCommand("D");
        }
        else if (Input.GetKeyDown(leftKey))
        {
            SendCommand("L");
        }
        else if (Input.GetKeyDown(rightKey))
        {
            SendCommand("R");
        }
        else if (Input.GetKeyDown(stopKey))
        {
            SendCommand("STOP");
        }

        // Arrow keys
        if (useArrowKeys)
        {
            if (Input.GetKeyDown(KeyCode.UpArrow))
            {
                SendCommand("U");
            }
            else if (Input.GetKeyDown(KeyCode.DownArrow))
            {
                SendCommand("D");
            }
            else if (Input.GetKeyDown(KeyCode.LeftArrow))
            {
                SendCommand("L");
            }
            else if (Input.GetKeyDown(KeyCode.RightArrow))
            {
                SendCommand("R");
            }
        }

        // Test sequence
        if (Input.GetKeyDown(KeyCode.Alpha1))
        {
            Debug.Log("<color=cyan>[TEST] Running movement sequence</color>");
            SendCommand("R");
            Invoke(nameof(TestUp), 0.5f);
            Invoke(nameof(TestLeft), 1f);
            Invoke(nameof(TestDown), 1.5f);
        }

        // Restart
        if (Input.GetKeyDown(KeyCode.R) && Input.GetKey(KeyCode.LeftControl))
        {
            MazeManager2D.Instance?.RestartGame();
        }
    }

    void SendCommand(string command)
    {
        if (logCommands)
        {
            Debug.Log($"<color=cyan>[KEYBOARD] → {command}</color>");
        }
        orbController.ReceiveCommand(command);
    }

    void TestUp() { SendCommand("U"); }
    void TestLeft() { SendCommand("L"); }
    void TestDown() { SendCommand("D"); }

    void OnGUI()
    {
        if (!showDebugUI) return;

        GUIStyle style = new GUIStyle(GUI.skin.box);
        style.fontSize = 14;
        style.alignment = TextAnchor.UpperLeft;
        style.normal.textColor = Color.white;
        style.padding = new RectOffset(10, 10, 10, 10);

        GUILayout.BeginArea(new Rect(10, 10, 300, 180));
        GUILayout.Box("🎮 2D KEYBOARD CONTROLS\n\n" +
                     $"Movement:\n" +
                     $"  {upKey} / ↑ = Up\n" +
                     $"  {downKey} / ↓ = Down\n" +
                     $"  {leftKey} / ← = Left\n" +
                     $"  {rightKey} / → = Right\n" +
                     $"  {stopKey} = Stop\n\n" +
                     $"Shortcuts:\n" +
                     $"  1 = Test Sequence\n" +
                     $"  Ctrl+R = Restart", style);
        GUILayout.EndArea();
    }
}

/// <summary>
/// Simple 2D camera controller for testing
/// </summary>
public class Camera2DDebugController : MonoBehaviour
{
    [Header("Settings")]
    [SerializeField] private float zoomSpeed = 2f;
    [SerializeField] private float panSpeed = 5f;
    [SerializeField] private float minZoom = 3f;
    [SerializeField] private float maxZoom = 15f;

    private Camera cam;
    private Vector3 dragOrigin;

    void Start()
    {
        cam = GetComponent<Camera>();
        if (cam == null)
        {
            cam = Camera.main;
        }
    }

    void Update()
    {
        // Zoom with mouse wheel
        float scroll = Input.GetAxis("Mouse ScrollWheel");
        if (scroll != 0 && cam != null)
        {
            cam.orthographicSize -= scroll * zoomSpeed;
            cam.orthographicSize = Mathf.Clamp(cam.orthographicSize, minZoom, maxZoom);
        }

        // Pan with middle mouse button
        if (Input.GetMouseButtonDown(2))
        {
            dragOrigin = cam.ScreenToWorldPoint(Input.mousePosition);
        }

        if (Input.GetMouseButton(2))
        {
            Vector3 difference = dragOrigin - cam.ScreenToWorldPoint(Input.mousePosition);
            cam.transform.position += difference;
        }

        // Reset with Home
        if (Input.GetKeyDown(KeyCode.Home))
        {
            ResetCamera();
        }
    }

    void ResetCamera()
    {
        if (cam != null)
        {
            cam.orthographicSize = 8f;
            transform.position = new Vector3(5, 5, -10);
        }
        Debug.Log("<color=cyan>[CAMERA] Reset to default</color>");
    }

    void OnGUI()
    {
        GUIStyle style = new GUIStyle(GUI.skin.box);
        style.fontSize = 12;
        style.alignment = TextAnchor.UpperLeft;
        style.normal.textColor = Color.white;
        style.padding = new RectOffset(10, 10, 10, 10);

        GUILayout.BeginArea(new Rect(Screen.width - 310, 10, 300, 120));
        GUILayout.Box("📷 2D CAMERA\n\n" +
                     "  Mouse Wheel = Zoom\n" +
                     "  Middle Click = Pan\n" +
                     "  Home = Reset", style);
        GUILayout.EndArea();
    }
}

/// <summary>
/// Performance monitor for 2D
/// </summary>
public class PerformanceMonitor2D : MonoBehaviour
{
    [SerializeField] private bool showFPS = true;
    [SerializeField] private float updateInterval = 0.5f;

    private float fps;
    private float frameCount;
    private float timeElapsed;

    void Update()
    {
        frameCount++;
        timeElapsed += Time.deltaTime;

        if (timeElapsed >= updateInterval)
        {
            fps = frameCount / timeElapsed;
            frameCount = 0;
            timeElapsed = 0;
        }
    }

    void OnGUI()
    {
        if (!showFPS) return;

        GUIStyle style = new GUIStyle(GUI.skin.box);
        style.fontSize = 12;
        style.normal.textColor = fps >= 60 ? Color.green : fps >= 30 ? Color.yellow : Color.red;
        style.padding = new RectOffset(10, 10, 5, 5);

        GUILayout.BeginArea(new Rect(Screen.width - 310, Screen.height - 50, 300, 40));
        GUILayout.Box($"📊 FPS: {fps:F0}", style);
        GUILayout.EndArea();
    }
}
