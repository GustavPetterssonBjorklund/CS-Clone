using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.Rendering;

public class Gun : MonoBehaviour
{
    [Header("Definition")]
    public WeaponDefinition definition;
    public bool applyDefinitionOnStart = true;

    [Header("Gun Settings")]
    public float damage = 25f;
    public float range = 100f;
    public float fireRate = 10f;

    [Header("References")]
    public Camera fpsCam;
    public ParticleSystem muzzleFlash;
    public AudioSource gunSound;

    // Optional LineRenderer reference (can assign in inspector)
    public LineRenderer lineRenderer;
    public Material lineMaterial;
    public float lineWidth = 0.02f;
    public float lineDuration = 1f; // seconds the line stays visible

    float nextTimeToFire;

    // Internal line state
    Vector3 _lineStart;
    Vector3 _lineEnd;
    float _lineEndTime;

    public void TriggerAttack()
    {
        if (fireRate > 0f)
        {
            if (Time.time < nextTimeToFire) return;
            nextTimeToFire = Time.time + (1f / fireRate);
        }

        Shoot();
    }

    // Called by Input System event
    public void OnAttack(InputAction.CallbackContext ctx)
    {
        if (!ctx.performed) return;
        TriggerAttack();
    }


    void Start()
    {
        if (IsHeadlessRuntime())
        {
            // Dedicated server has no rendering/shaders.
            enabled = false;
            return;
        }

        if (fpsCam == null)
            fpsCam = GetComponentInParent<Camera>();
        if (fpsCam == null)
            fpsCam = Camera.main;

        if (applyDefinitionOnStart)
        {
            ApplyDefinition(definition);
        }

        // Ensure there's a LineRenderer to use. If one isn't assigned, create a child object.
        if (lineRenderer == null)
        {
            var go = new GameObject("Gun_LineRenderer");
            go.transform.SetParent(transform, false);
            lineRenderer = go.AddComponent<LineRenderer>();

            // Configure a simple material if none supplied
            if (lineMaterial != null)
            {
                lineRenderer.material = lineMaterial;
            }
            else
            {
                var shader = Shader.Find("Sprites/Default");
                if (shader == null)
                {
                    Debug.LogWarning("Gun: Shader 'Sprites/Default' not found. Disabling tracer visuals.");
                    Destroy(go);
                    lineRenderer = null;
                    return;
                }

                lineRenderer.material = new Material(shader);
                lineRenderer.material.color = Color.red;
            }

            lineRenderer.startWidth = lineWidth;
            lineRenderer.endWidth = lineWidth;
            lineRenderer.positionCount = 2;
            lineRenderer.useWorldSpace = true;
            lineRenderer.numCapVertices = 2;
            lineRenderer.enabled = false;
        }
    }

    void Update()
    {
        // Toggle visibility based on timer
        if (lineRenderer != null)
        {
            if (Time.time < _lineEndTime)
            {
                if (!lineRenderer.enabled) lineRenderer.enabled = true;
                lineRenderer.SetPosition(0, _lineStart);
                lineRenderer.SetPosition(1, _lineEnd);
            }
            else
            {
                if (lineRenderer.enabled) lineRenderer.enabled = false;
            }
        }
    }

    void Shoot()
    {
        if (muzzleFlash) muzzleFlash.Play();
        if (gunSound) gunSound.Play();

        Debug.Log("Gun: Shoot called");

        if (fpsCam == null)
        {
            Debug.LogWarning("Gun: fpsCam is null - using gun transform for debug raycast");
        }

        // Debug raycast for better visibility in logs
        Vector3 rayOrigin = fpsCam != null ? fpsCam.transform.position : transform.position;
        Vector3 rayDirection = fpsCam != null ? fpsCam.transform.forward : transform.forward;
        Debug.DrawRay(rayOrigin, rayDirection * range, Color.red, 1f);

        // Set up the LineRenderer positions and timer so the line is visible in Game view
        _lineStart = rayOrigin;
        _lineEnd = rayOrigin + rayDirection * range;
        _lineEndTime = Time.time + lineDuration;

        if (lineRenderer != null)
        {
            lineRenderer.SetPosition(0, _lineStart);
            lineRenderer.SetPosition(1, _lineEnd);
            lineRenderer.enabled = true;
        }

        if (Physics.Raycast(rayOrigin, rayDirection, out RaycastHit hit, range))
        {
            Debug.Log($"Gun: Hit '{hit.transform.name}' at {hit.point} (distance {hit.distance:F2})");

            var target = hit.transform.GetComponent<Target>();
            if (target != null)
            {
                target.TakeDamage(damage);
                Debug.Log($"Gun: Applied {damage} damage to '{hit.transform.name}'");
            }
            else
            {
                Debug.Log("Gun: Hit object has no Target component");
            }

            // shorten the visible line to the hit point
            _lineEnd = hit.point;
            if (lineRenderer != null) lineRenderer.SetPosition(1, _lineEnd);
        }
        else
        {
            Debug.Log("Gun: Shot missed");
        }
        
    }

    public void ApplyDefinition(WeaponDefinition weaponDefinition)
    {
        definition = weaponDefinition;
        if (definition == null) return;

        damage = definition.damage;
        range = definition.range;
        fireRate = definition.fireRate;
    }

    private static bool IsHeadlessRuntime()
    {
#if UNITY_SERVER
        return true;
#else
        return Application.isBatchMode || SystemInfo.graphicsDeviceType == GraphicsDeviceType.Null;
#endif
    }
}

