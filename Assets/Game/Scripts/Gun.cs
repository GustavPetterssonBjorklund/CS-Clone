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
    [SerializeField] private LayerMask hitMask = ~0;
    [SerializeField, Range(0f, 5f)] private float spreadAngle = 0.15f;
    [SerializeField] private bool ignoreTriggerColliders = true;
    [SerializeField] private bool debugShots;

    [Header("References")]
    public Camera fpsCam;
    public ParticleSystem muzzleFlash;
    public AudioSource gunSound;

    // Optional LineRenderer reference (can assign in inspector)
    public LineRenderer lineRenderer;
    public Material lineMaterial;
    public float lineWidth = 0.02f;
    public float lineDuration = 1f; // seconds the line stays visible

    private float nextTimeToFire;
    private GameObject runtimeLineRendererObject;
    private Material runtimeLineMaterial;

    // Internal line state
    private Vector3 lineStart;
    private Vector3 lineEnd;
    private float lineEndTime;

    public bool TriggerAttack()
    {
        if (fireRate > 0f)
        {
            if (Time.time < nextTimeToFire) return false;
            nextTimeToFire = Time.time + (1f / fireRate);
        }

        Shoot();
        return true;
    }

    // Called by Input System event
    public void OnAttack(InputAction.CallbackContext ctx)
    {
        if (!ctx.performed) return;
        TriggerAttack();
    }


    private void Start()
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
            GameObject go = new("Gun_LineRenderer");
            go.transform.SetParent(transform, false);
            lineRenderer = go.AddComponent<LineRenderer>();
            runtimeLineRendererObject = go;

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
                    runtimeLineRendererObject = null;
                    return;
                }

                runtimeLineMaterial = new Material(shader);
                runtimeLineMaterial.color = Color.red;
                lineRenderer.material = runtimeLineMaterial;
            }

            lineRenderer.startWidth = lineWidth;
            lineRenderer.endWidth = lineWidth;
            lineRenderer.positionCount = 2;
            lineRenderer.useWorldSpace = true;
            lineRenderer.numCapVertices = 2;
            lineRenderer.enabled = false;
        }
    }

    private void OnDisable()
    {
        if (lineRenderer != null)
        {
            lineRenderer.enabled = false;
        }
    }

    private void OnDestroy()
    {
        if (runtimeLineMaterial != null)
        {
            Destroy(runtimeLineMaterial);
        }

        if (runtimeLineRendererObject != null)
        {
            Destroy(runtimeLineRendererObject);
        }
    }

    private void Update()
    {
        // Toggle visibility based on timer
        if (lineRenderer != null)
        {
            if (Time.time < lineEndTime)
            {
                if (!lineRenderer.enabled) lineRenderer.enabled = true;
                lineRenderer.SetPosition(0, lineStart);
                lineRenderer.SetPosition(1, lineEnd);
            }
            else
            {
                if (lineRenderer.enabled) lineRenderer.enabled = false;
            }
        }
    }

    private void Shoot()
    {
        if (muzzleFlash) muzzleFlash.Play();
        if (gunSound) gunSound.Play();

        if (fpsCam == null)
        {
            Debug.LogWarning("Gun: fpsCam is null - using gun transform for debug raycast");
        }

        Vector3 rayOrigin = fpsCam != null ? fpsCam.transform.position : transform.position;
        Vector3 rayDirection = fpsCam != null ? fpsCam.transform.forward : transform.forward;
        rayDirection = ApplySpread(rayDirection);
        QueryTriggerInteraction triggerInteraction = ignoreTriggerColliders
            ? QueryTriggerInteraction.Ignore
            : QueryTriggerInteraction.Collide;

        if (debugShots)
        {
            Debug.DrawRay(rayOrigin, rayDirection * range, Color.red, 1f);
        }

        // Set up the LineRenderer positions and timer so the line is visible in Game view
        lineStart = rayOrigin;
        lineEnd = rayOrigin + rayDirection * range;
        lineEndTime = Time.time + lineDuration;

        if (lineRenderer != null)
        {
            lineRenderer.SetPosition(0, lineStart);
            lineRenderer.SetPosition(1, lineEnd);
            lineRenderer.enabled = true;
        }

        if (Physics.Raycast(rayOrigin, rayDirection, out RaycastHit hit, range, hitMask, triggerInteraction))
        {
            Target target = hit.transform.GetComponentInParent<Target>();
            if (target != null)
            {
                target.TakeDamage(damage);
                if (debugShots)
                {
                    Debug.Log($"Gun: Applied {damage} damage to '{hit.transform.name}'");
                }
            }

            lineEnd = hit.point;
            if (lineRenderer != null) lineRenderer.SetPosition(1, lineEnd);
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

    private Vector3 ApplySpread(Vector3 direction)
    {
        if (spreadAngle <= 0.001f) return direction.normalized;

        Quaternion spread = Quaternion.Euler(
            Random.Range(-spreadAngle, spreadAngle),
            Random.Range(-spreadAngle, spreadAngle),
            0f);
        return spread * direction.normalized;
    }
}
