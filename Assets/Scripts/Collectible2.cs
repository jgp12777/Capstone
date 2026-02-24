using UnityEngine;
using System.Collections;

/// <summary>
/// 2D Collectible item that the orb can pick up
/// </summary>
public class Collectible2 : MonoBehaviour
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

        Debug.Log($"<color=green>[COLLECTED] {gameObject.name}</color>");

        // Play effect
        if (collectEffect != null)
        {
            ParticleSystem effect = Instantiate(collectEffect, transform.position, Quaternion.identity);
            Destroy(effect.gameObject, 2f);
        }

        // Notify manager
        MazeManager2D.Instance?.OnCollectibleCollected();

        // Destroy with animation
        StartCoroutine(CollectAnim());
    }

    IEnumerator CollectAnim()
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