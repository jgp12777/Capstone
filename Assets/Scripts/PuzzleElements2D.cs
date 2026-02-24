using UnityEngine;
using System.Collections;

/// <summary>
/// 2D Collectible item that the orb can pick up
/// </summary>
public class Collectible2D : MonoBehaviour
{
    [Header("Visual Settings")]
    [SerializeField] private float rotationSpeed = 90f;
    [SerializeField] private float pulseSpeed = 2f;
    [SerializeField] private float pulseAmount = 0.2f;
    [SerializeField] private ParticleSystem collectEffect;
    
    [Header("Audio")]
    [SerializeField] private AudioClip collectSound;
    
    private Vector3 baseScale;
    private bool isCollected = false;
    private SpriteRenderer spriteRenderer;

    void Start()
    {
        baseScale = transform.localScale;
        spriteRenderer = GetComponent<SpriteRenderer>();
    }

    void Update()
    {
        if (!isCollected)
        {
            // Rotate
            transform.Rotate(0, 0, rotationSpeed * Time.deltaTime);
            
            // Pulse scale
            float pulse = 1f + Mathf.Sin(Time.time * pulseSpeed) * pulseAmount;
            transform.localScale = baseScale * pulse;
        }
    }

    public void Collect()
    {
        if (isCollected) return;
        
        isCollected = true;
        
        // Play effect
        if (collectEffect != null)
        {
            ParticleSystem effect = Instantiate(collectEffect, transform.position, Quaternion.identity);
            Destroy(effect.gameObject, 2f);
        }
        
        // Notify manager
        MazeManager2D.Instance?.OnCollectibleCollected();
        
        // Destroy with animation
        StartCoroutine(CollectAnimation());
    }

    IEnumerator CollectAnimation()
    {
        float duration = 0.3f;
        float elapsed = 0f;
        Vector3 startScale = transform.localScale;
        Vector3 startPos = transform.position;
        
        while (elapsed < duration)
        {
            elapsed += Time.deltaTime;
            float progress = elapsed / duration;
            
            // Scale up then fade
            transform.localScale = startScale * (1f + progress * 0.5f);
            
            // Move up
            transform.position = startPos + Vector3.up * progress * 0.5f;
            
            // Fade out sprite
            if (spriteRenderer != null)
            {
                Color color = spriteRenderer.color;
                color.a = 1f - progress;
                spriteRenderer.color = color;
            }
            
            yield return null;
        }
        
        Destroy(gameObject);
    }
}

/// <summary>
/// 2D Pressure switch that can be activated by the orb
/// </summary>
public class PressureSwitch2D : MonoBehaviour
{
    [Header("Settings")]
    [SerializeField] private bool isToggle = false;
    [SerializeField] private Color activeColor = Color.green;
    [SerializeField] private Color inactiveColor = Color.red;
    [SerializeField] private GameObject[] connectedDoors;
    
    [Header("Visual")]
    [SerializeField] private SpriteRenderer switchSprite;
    [SerializeField] private ParticleSystem activationEffect;
    
    private bool isActive = false;

    void Start()
    {
        if (switchSprite == null)
        {
            switchSprite = GetComponent<SpriteRenderer>();
        }
        UpdateVisuals();
    }

    void OnTriggerEnter2D(Collider2D other)
    {
        if (other.CompareTag("Player"))
        {
            if (isToggle)
            {
                isActive = !isActive;
            }
            else
            {
                isActive = true;
            }
            
            Activate();
        }
    }

    void OnTriggerExit2D(Collider2D other)
    {
        if (other.CompareTag("Player") && !isToggle)
        {
            isActive = false;
            Deactivate();
        }
    }

    void Activate()
    {
        UpdateVisuals();
        
        if (activationEffect != null)
        {
            activationEffect.Play();
        }
        
        foreach (GameObject door in connectedDoors)
        {
            if (door != null)
            {
                Door2D doorScript = door.GetComponent<Door2D>();
                if (doorScript != null)
                {
                    doorScript.Open();
                }
            }
        }
    }

    void Deactivate()
    {
        UpdateVisuals();
        
        foreach (GameObject door in connectedDoors)
        {
            if (door != null)
            {
                Door2D doorScript = door.GetComponent<Door2D>();
                if (doorScript != null)
                {
                    doorScript.Close();
                }
            }
        }
    }

    void UpdateVisuals()
    {
        Color targetColor = isActive ? activeColor : inactiveColor;
        
        if (switchSprite != null)
        {
            switchSprite.color = targetColor;
        }
    }
}

/// <summary>
/// 2D Door that can be opened/closed by switches
/// </summary>
public class Door2D : MonoBehaviour
{
    [Header("Settings")]
    [SerializeField] private float openDistance = 2f;
    [SerializeField] private Vector2 openDirection = Vector2.up;
    [SerializeField] private float moveSpeed = 2f;
    [SerializeField] private bool startOpen = false;
    
    [Header("Audio")]
    [SerializeField] private AudioSource audioSource;
    [SerializeField] private AudioClip openSound;
    [SerializeField] private AudioClip closeSound;
    
    private Vector2 closedPosition;
    private Vector2 openPosition;
    private bool isOpen;
    private Coroutine moveCoroutine;
    private SpriteRenderer spriteRenderer;
    private BoxCollider2D boxCollider;

    void Start()
    {
        closedPosition = transform.position;
        openPosition = closedPosition + openDirection * openDistance;
        
        spriteRenderer = GetComponent<SpriteRenderer>();
        boxCollider = GetComponent<BoxCollider2D>();
        
        isOpen = startOpen;
        transform.position = isOpen ? openPosition : closedPosition;
        
        if (boxCollider != null)
        {
            boxCollider.enabled = !isOpen;
        }
    }

    public void Open()
    {
        if (!isOpen)
        {
            isOpen = true;
            PlaySound(openSound);
            
            if (moveCoroutine != null)
                StopCoroutine(moveCoroutine);
            
            moveCoroutine = StartCoroutine(MoveDoor(openPosition));
            
            if (boxCollider != null)
            {
                boxCollider.enabled = false;
            }
        }
    }

    public void Close()
    {
        if (isOpen)
        {
            isOpen = false;
            PlaySound(closeSound);
            
            if (moveCoroutine != null)
                StopCoroutine(moveCoroutine);
            
            moveCoroutine = StartCoroutine(MoveDoor(closedPosition));
            
            if (boxCollider != null)
            {
                boxCollider.enabled = true;
            }
        }
    }

    IEnumerator MoveDoor(Vector2 targetPosition)
    {
        while (Vector2.Distance(transform.position, targetPosition) > 0.01f)
        {
            transform.position = Vector2.Lerp(transform.position, targetPosition, Time.deltaTime * moveSpeed);
            yield return null;
        }
        
        transform.position = targetPosition;
    }

    void PlaySound(AudioClip clip)
    {
        if (audioSource != null && clip != null)
        {
            audioSource.PlayOneShot(clip);
        }
    }
}
