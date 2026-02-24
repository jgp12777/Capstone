using UnityEngine;
using UnityEngine.Rendering.Universal;
using System.Collections;

/// <summary>
/// 2D Visual effects manager for lighting and polish
/// Manages 2D lights and post-processing
/// </summary>
public class VisualEffects2D : MonoBehaviour
{
    [Header("2D Lighting")]
    [SerializeField] private Light2D globalLight;
    [SerializeField] private Color ambientColor = new Color(0.2f, 0.3f, 0.5f);
    [SerializeField] private float globalLightIntensity = 0.5f;
    
    [Header("Dynamic Effects")]
    [SerializeField] private bool pulseLighting = true;
    [SerializeField] private float pulseSpeed = 1f;
    [SerializeField] private float pulseIntensity = 0.1f;
    
    private float baseLightIntensity;

    void Start()
    {
        SetupLighting();
        
        if (globalLight != null)
        {
            baseLightIntensity = globalLight.intensity;
        }
    }

    void Update()
    {
        if (pulseLighting && globalLight != null)
        {
            float pulse = Mathf.Sin(Time.time * pulseSpeed) * pulseIntensity;
            globalLight.intensity = baseLightIntensity + pulse;
        }
    }

    void SetupLighting()
    {
        // Create global light if it doesn't exist
        if (globalLight == null)
        {
            GameObject lightObj = new GameObject("Global Light 2D");
            globalLight = lightObj.AddComponent<Light2D>();
            globalLight.lightType = Light2D.LightType.Global;
        }
        
        globalLight.color = ambientColor;
        globalLight.intensity = globalLightIntensity;
        
        Debug.Log("<color=cyan>[2D VISUAL] Lighting setup complete</color>");
    }

    public void SetGlobalLightIntensity(float intensity)
    {
        if (globalLight != null)
        {
            globalLight.intensity = intensity;
            baseLightIntensity = intensity;
        }
    }

    public void SetAmbientColor(Color color)
    {
        if (globalLight != null)
        {
            globalLight.color = color;
        }
    }

    public void FlashLight(float duration, float intensity)
    {
        StartCoroutine(LightFlashCoroutine(duration, intensity));
    }

    IEnumerator LightFlashCoroutine(float duration, float maxIntensity)
    {
        if (globalLight == null) yield break;

        float startIntensity = globalLight.intensity;
        float elapsed = 0f;

        while (elapsed < duration)
        {
            elapsed += Time.deltaTime;
            float t = elapsed / duration;
            float intensity = Mathf.Lerp(startIntensity, maxIntensity, Mathf.Sin(t * Mathf.PI));
            globalLight.intensity = intensity;
            yield return null;
        }

        globalLight.intensity = startIntensity;
    }
}

/// <summary>
/// Orb visual effects for 2D - glowing sprite and particles
/// </summary>
public class OrbVisualEffects2D : MonoBehaviour
{
    [Header("2D Lighting")]
    [SerializeField] private Light2D orbLight;
    [SerializeField] private float lightIntensity = 1f;
    [SerializeField] private float lightRadius = 3f;
    [SerializeField] private Color lightColor = Color.cyan;
    
    [Header("Glow")]
    [SerializeField] private float glowPulseSpeed = 2f;
    [SerializeField] private float glowMinIntensity = 0.8f;
    [SerializeField] private float glowMaxIntensity = 1.2f;
    
    [Header("Trail")]
    [SerializeField] private TrailRenderer trailRenderer;
    [SerializeField] private Gradient trailGradient;
    [SerializeField] private float trailTime = 0.5f;
    
    [Header("Sprite Glow")]
    [SerializeField] private SpriteRenderer spriteRenderer;
    [SerializeField] private Material glowMaterial;

    private Material originalMaterial;

    void Start()
    {
        SetupLight();
        SetupTrail();
        SetupSprite();
    }

    void Update()
    {
        // Pulse light
        if (orbLight != null)
        {
            float pulse = Mathf.Sin(Time.time * glowPulseSpeed) * 0.5f + 0.5f;
            orbLight.intensity = Mathf.Lerp(glowMinIntensity, glowMaxIntensity, pulse);
        }
    }

    void SetupLight()
    {
        if (orbLight == null)
        {
            GameObject lightObj = new GameObject("Orb Light");
            lightObj.transform.SetParent(transform);
            lightObj.transform.localPosition = Vector3.zero;
            orbLight = lightObj.AddComponent<Light2D>();
        }

        orbLight.lightType = Light2D.LightType.Point;
        orbLight.color = lightColor;
        orbLight.intensity = lightIntensity;
        orbLight.pointLightOuterRadius = lightRadius;
        orbLight.pointLightInnerRadius = lightRadius * 0.5f;
        orbLight.falloffIntensity = 0.5f;
    }

    void SetupTrail()
    {
        if (trailRenderer == null)
        {
            GameObject trailObj = new GameObject("Trail");
            trailObj.transform.SetParent(transform);
            trailObj.transform.localPosition = Vector3.zero;
            trailRenderer = trailObj.AddComponent<TrailRenderer>();
        }

        trailRenderer.time = trailTime;
        trailRenderer.startWidth = 0.3f;
        trailRenderer.endWidth = 0f;
        trailRenderer.numCornerVertices = 5;
        trailRenderer.numCapVertices = 5;
        
        if (trailGradient != null && trailGradient.colorKeys.Length > 0)
        {
            trailRenderer.colorGradient = trailGradient;
        }
        else
        {
            // Default gradient
            Gradient gradient = new Gradient();
            gradient.SetKeys(
                new GradientColorKey[] {
                    new GradientColorKey(lightColor, 0f),
                    new GradientColorKey(lightColor, 1f)
                },
                new GradientAlphaKey[] {
                    new GradientAlphaKey(1f, 0f),
                    new GradientAlphaKey(0f, 1f)
                }
            );
            trailRenderer.colorGradient = gradient;
        }

        // Use sprite default material for 2D
        if (trailRenderer.material == null)
        {
            trailRenderer.material = new Material(Shader.Find("Sprites/Default"));
        }
        
        trailRenderer.sortingOrder = 0;
    }

    void SetupSprite()
    {
        if (spriteRenderer == null)
        {
            spriteRenderer = GetComponent<SpriteRenderer>();
        }

        if (spriteRenderer != null)
        {
            originalMaterial = spriteRenderer.material;
            
            // If you have a glow material, apply it
            if (glowMaterial != null)
            {
                spriteRenderer.material = glowMaterial;
            }
            
            spriteRenderer.sortingOrder = 2; // Above floor and walls
        }
    }

    public void SetTrailEmitting(bool emitting)
    {
        if (trailRenderer != null)
        {
            trailRenderer.emitting = emitting;
        }
    }

    public void SetLightColor(Color color)
    {
        lightColor = color;
        if (orbLight != null)
        {
            orbLight.color = color;
        }
    }

    public void FlashLight(float duration)
    {
        StartCoroutine(LightFlashCoroutine(duration));
    }

    IEnumerator LightFlashCoroutine(float duration)
    {
        if (orbLight == null) yield break;

        float startIntensity = orbLight.intensity;
        float maxIntensity = startIntensity * 2f;
        float elapsed = 0f;

        while (elapsed < duration)
        {
            elapsed += Time.deltaTime;
            float t = elapsed / duration;
            orbLight.intensity = Mathf.Lerp(maxIntensity, startIntensity, t);
            yield return null;
        }

        orbLight.intensity = startIntensity;
    }
}

/// <summary>
/// Add this to collectibles for glowing effect
/// </summary>
public class Collectible2DGlow : MonoBehaviour
{
    [Header("2D Lighting")]
    [SerializeField] private Light2D itemLight;
    [SerializeField] private Color glowColor = Color.yellow;
    [SerializeField] private float lightRadius = 2f;
    [SerializeField] private float pulseSpeed = 2f;
    
    private float baseLightIntensity = 0.8f;

    void Start()
    {
        SetupLight();
    }

    void Update()
    {
        if (itemLight != null)
        {
            float pulse = Mathf.Sin(Time.time * pulseSpeed) * 0.5f + 0.5f;
            itemLight.intensity = baseLightIntensity * (0.5f + pulse * 0.5f);
        }
    }

    void SetupLight()
    {
        if (itemLight == null)
        {
            GameObject lightObj = new GameObject("Item Light");
            lightObj.transform.SetParent(transform);
            lightObj.transform.localPosition = Vector3.zero;
            itemLight = lightObj.AddComponent<Light2D>();
        }

        itemLight.lightType = Light2D.LightType.Point;
        itemLight.color = glowColor;
        itemLight.intensity = baseLightIntensity;
        itemLight.pointLightOuterRadius = lightRadius;
        itemLight.pointLightInnerRadius = lightRadius * 0.5f;
    }
}
