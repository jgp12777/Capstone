using UnityEngine;
using UnityEngine.UI;
using TMPro;
using System.Collections;

/// <summary>
/// 2D game manager that handles level progression, scoring, and game state
/// </summary>
public class MazeManager2D : MonoBehaviour
{
    public static MazeManager2D Instance { get; private set; }

    [Header("References")]
    [SerializeField] private OrbController2D orb;
    [SerializeField] private MazeGenerator2D mazeGenerator;
    [SerializeField] private Camera mainCamera;
    
    [Header("UI")]
    [SerializeField] private TextMeshProUGUI levelText;
    [SerializeField] private TextMeshProUGUI scoreText;
    [SerializeField] private TextMeshProUGUI timerText;
    [SerializeField] private TextMeshProUGUI collectiblesText;
    [SerializeField] private GameObject levelCompletePanel;
    [SerializeField] private TextMeshProUGUI levelCompleteText;
    [SerializeField] private GameObject gameOverPanel;
    
    [Header("Level Settings")]
    [SerializeField] private int currentLevel = 1;
    [SerializeField] private int maxLevels = 10;
    [SerializeField] private float levelTimeLimit = 60f;
    [SerializeField] private int scorePerCollectible = 100;
    [SerializeField] private int scorePerSecondRemaining = 10;
    
    [Header("Camera Settings")]
    [SerializeField] private float cameraSize = 8f;
    [SerializeField] private bool followOrb = true;
    [SerializeField] private float cameraFollowSpeed = 2f;
    
    private int totalScore = 0;
    private int collectiblesCollected = 0;
    private int totalCollectibles = 0;
    private float timeRemaining;
    private bool levelActive = false;
    private bool isPaused = false;

    void Awake()
    {
        if (Instance == null)
        {
            Instance = this;
        }
        else
        {
            Destroy(gameObject);
        }
    }

    void Start()
    {
        // Setup camera for 2D
        if (mainCamera != null)
        {
            mainCamera.orthographic = true;
            mainCamera.orthographicSize = cameraSize;
        }
        
        StartLevel();
    }

    void Update()
    {
        if (levelActive && !isPaused)
        {
            /*
            timeRemaining -= Time.deltaTime;
            if (timeRemaining <= 0)
            {
                timeRemaining = 0;
                OnTimerExpired();
            } */
            
            UpdateUI();
            
            // Camera follow for 2D
            if (followOrb && orb != null && mainCamera != null)
            {
                Vector3 targetPos = new Vector3(orb.transform.position.x, orb.transform.position.y, mainCamera.transform.position.z);
                mainCamera.transform.position = Vector3.Lerp(mainCamera.transform.position, targetPos, Time.deltaTime * cameraFollowSpeed);
            }
        }
    }

    public void StartLevel()
    {
        Debug.Log($"<color=yellow>[LEVEL] Starting Level {currentLevel}</color>");
        
        // Generate maze
        if (mazeGenerator != null)
        {
            mazeGenerator.GenerateMaze();
        }
        
        // Reset orb position
        if (orb != null)
        {
            orb.transform.position = mazeGenerator.GetStartPosition();
            orb.SetCanMove(true);
        }
        
        // Setup camera to see the whole maze
        if (mainCamera != null && mazeGenerator != null)
        {
            // Center camera on maze
            float mazeWidth = 10; // Get from generator if needed
            float mazeHeight = 10;
            Vector3 mazeCenter = new Vector3(mazeWidth * 0.5f, mazeHeight * 0.5f, -10);
            mainCamera.transform.position = mazeCenter;
            
            // Adjust camera size to fit maze
            mainCamera.orthographicSize = Mathf.Max(mazeWidth, mazeHeight) * 0.6f;
        }
        
        // Reset level stats
        collectiblesCollected = 0;
        totalCollectibles = GameObject.FindGameObjectsWithTag("Collectible").Length;
        timeRemaining = levelTimeLimit + (currentLevel * 10);
        levelActive = true;
        
        // Hide panels
        if (levelCompletePanel != null)
            levelCompletePanel.SetActive(false);
        if (gameOverPanel != null)
            gameOverPanel.SetActive(false);
        
        UpdateUI();
    }

    void UpdateUI()
    {
        if (levelText != null)
            levelText.text = $"Level: {currentLevel}";
        
        if (scoreText != null)
            scoreText.text = $"Score: {totalScore}";
        
        if (timerText != null)
        {
            int minutes = Mathf.FloorToInt(timeRemaining / 60);
            int seconds = Mathf.FloorToInt(timeRemaining % 60);
            timerText.text = $"Time: {minutes:00}:{seconds:00}";
            
            // Change color when low on time
            if (timeRemaining < 10f)
                timerText.color = Color.red;
            else
                timerText.color = Color.white;
        }
        
        if (collectiblesText != null)
            collectiblesText.text = $"Collectibles: {collectiblesCollected}/{totalCollectibles}";
    }

    public void OnCollectibleCollected()
    {
        collectiblesCollected++;
        totalScore += scorePerCollectible;
        
        Debug.Log($"<color=cyan>[COLLECT] {collectiblesCollected}/{totalCollectibles}</color>");
        
        // Bonus for collecting all
        if (collectiblesCollected >= totalCollectibles)
        {
            totalScore += 500;
            Debug.Log("<color=green>[BONUS] All collectibles! +500</color>");
        }
    }

    public void OnGoalReached()
    {
        if (!levelActive) return;
        
        levelActive = false;
        
        Debug.Log("<color=green>[LEVEL] Goal reached!</color>");
        
        // Calculate bonus score
        int timeBonus = Mathf.FloorToInt(timeRemaining) * scorePerSecondRemaining;
        totalScore += timeBonus;
        
        // Show level complete
        if (levelCompletePanel != null)
        {
            levelCompletePanel.SetActive(true);
            if (levelCompleteText != null)
            {
                levelCompleteText.text = $"Level {currentLevel} Complete!\n\nTime Bonus: +{timeBonus}\nTotal Score: {totalScore}";
            }
        }
        
        // Progress to next level
        StartCoroutine(NextLevelDelay());
    }

    IEnumerator NextLevelDelay()
    {
        yield return new WaitForSeconds(3f);
        
        currentLevel++;
        
        if (currentLevel > maxLevels)
        {
            ShowGameComplete();
        }
        else
        {
            StartLevel();
        }
    }

    void OnTimerExpired()
    {
        Debug.Log("<color=red>[GAME] Time's up!</color>");
        levelActive = false;
        
        if (orb != null)
        {
            orb.SetCanMove(false);
        }
        
        ShowGameOver();
    }

    public void OnHazardHit()
    {
        Debug.Log("<color=red>[HAZARD] Player hit hazard!</color>");
        
        // Reset to start position
        if (orb != null)
        {
            orb.ResetToStart();
        }
        
        // Penalty
        totalScore = Mathf.Max(0, totalScore - 50);
        timeRemaining = Mathf.Max(0, timeRemaining - 5f);
    }

    void ShowGameOver()
    {
        if (gameOverPanel != null)
        {
            gameOverPanel.SetActive(true);
        }
    }

    void ShowGameComplete()
    {
        Debug.Log($"<color=green>[GAME] Complete! Final Score: {totalScore}</color>");
        
        if (levelCompletePanel != null)
        {
            levelCompletePanel.SetActive(true);
            if (levelCompleteText != null)
            {
                levelCompleteText.text = $"All Levels Complete!\n\nFinal Score: {totalScore}\n\nCongratulations!";
            }
        }
    }

    public void RestartGame()
    {
        currentLevel = 1;
        totalScore = 0;
        StartLevel();
    }

    public void PauseGame()
    {
        isPaused = true;
        Time.timeScale = 0;
    }

    public void ResumeGame()
    {
        isPaused = false;
        Time.timeScale = 1;
    }

    public void QuitGame()
    {
        Application.Quit();
    }
}
