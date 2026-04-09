using Unity.Netcode;
using UnityEngine;

[DisallowMultipleComponent]
[RequireComponent(typeof(NetworkObject))]
public class Target : NetworkBehaviour
{
    [Header("Health")]
    [SerializeField] private float maxHealth = 100f;
    [SerializeField] private bool destroyOnDeath = true;

    [Header("Presentation")]
    [SerializeField] private bool autoBuildPresentation = true;
    [SerializeField] private float colliderHeight = 1.8f;
    [SerializeField] private float colliderRadius = 0.35f;
    [SerializeField] private Color healthyColor = new(0.22f, 0.75f, 0.25f, 1f);
    [SerializeField] private Color criticalColor = new(0.87f, 0.16f, 0.16f, 1f);

    private readonly NetworkVariable<float> currentHealth = new(100f,
        NetworkVariableReadPermission.Everyone,
        NetworkVariableWritePermission.Server);
    private float standaloneHealth;

    private Transform healthBarPivot;
    private Transform healthBarFill;
    private Renderer healthBarFillRenderer;

    public float MaxHealth => maxHealth;
    public float CurrentHealth => IsSpawned ? currentHealth.Value : standaloneHealth;

    private void Awake()
    {
        standaloneHealth = Mathf.Max(1f, maxHealth);
        EnsureCollider();

        if (autoBuildPresentation)
        {
            EnsurePresentation();
        }

        RefreshHealthBar(standaloneHealth);
    }

    public override void OnNetworkSpawn()
    {
        base.OnNetworkSpawn();

        currentHealth.OnValueChanged += OnHealthChanged;

        if (IsServer)
        {
            currentHealth.Value = standaloneHealth;
        }

        RefreshHealthBar(currentHealth.Value);
    }

    public override void OnNetworkDespawn()
    {
        currentHealth.OnValueChanged -= OnHealthChanged;
        base.OnNetworkDespawn();
    }

    public void TakeDamage(float amount)
    {
        if (amount <= 0f) return;

        if (!IsSpawned)
        {
            ApplyStandaloneDamage(amount);
            return;
        }

        if (IsServer)
        {
            ApplyDamage(amount);
            return;
        }

        RequestDamageServerRpc(amount);
    }

    [Rpc(SendTo.Server)]
    private void RequestDamageServerRpc(float amount)
    {
        ApplyDamage(amount);
    }

    private void ApplyDamage(float amount)
    {
        if (!IsServer) return;
        if (currentHealth.Value <= 0f) return;

        float nextHealth = Mathf.Max(0f, currentHealth.Value - amount);
        currentHealth.Value = nextHealth;

        if (nextHealth <= 0f)
        {
            HandleDeath();
        }
    }

    private void ApplyStandaloneDamage(float amount)
    {
        float nextHealth = Mathf.Max(0f, standaloneHealth - amount);
        standaloneHealth = nextHealth;
        RefreshHealthBar(nextHealth);

        if (nextHealth <= 0f)
        {
            HandleDeath();
        }
    }

    private void HandleDeath()
    {
        if (!destroyOnDeath) return;

        NetworkObject networkObject = NetworkObject;
        if (networkObject != null && networkObject.IsSpawned)
        {
            networkObject.Despawn(true);
            return;
        }

        Destroy(gameObject);
    }

    private void OnHealthChanged(float previousValue, float nextValue)
    {
        RefreshHealthBar(nextValue);
    }

    private void RefreshHealthBar(float healthValue)
    {
        if (healthBarFill == null) return;

        float normalized = maxHealth <= 0.0001f ? 0f : Mathf.Clamp01(healthValue / maxHealth);
        Vector3 localScale = healthBarFill.localScale;
        localScale.x = normalized;
        healthBarFill.localScale = localScale;
        healthBarFill.localPosition = new Vector3((normalized - 1f) * 0.4f, 0f, -0.001f);

        if (healthBarFillRenderer != null)
        {
            Color tint = Color.Lerp(criticalColor, healthyColor, normalized);
            healthBarFillRenderer.material.color = tint;
        }
    }

    private void LateUpdate()
    {
        if (healthBarPivot == null) return;

        Camera activeCamera = Camera.main;
        if (activeCamera == null) return;

        Vector3 toCamera = healthBarPivot.position - activeCamera.transform.position;
        if (toCamera.sqrMagnitude <= 0.0001f) return;
        healthBarPivot.rotation = Quaternion.LookRotation(toCamera.normalized, Vector3.up);
    }

    private void EnsureCollider()
    {
        CapsuleCollider capsule = GetComponent<CapsuleCollider>();
        if (capsule == null)
        {
            capsule = gameObject.AddComponent<CapsuleCollider>();
        }

        capsule.height = colliderHeight;
        capsule.radius = colliderRadius;
        capsule.center = new Vector3(0f, colliderHeight * 0.5f, 0f);
    }

    private void EnsurePresentation()
    {
        if (Application.isBatchMode) return;

        Transform body = transform.Find("BodyVisual");
        if (body == null)
        {
            GameObject bodyObject = GameObject.CreatePrimitive(PrimitiveType.Capsule);
            bodyObject.name = "BodyVisual";
            bodyObject.transform.SetParent(transform, false);
            bodyObject.transform.localPosition = new Vector3(0f, colliderHeight * 0.5f, 0f);
            bodyObject.transform.localRotation = Quaternion.identity;
            bodyObject.transform.localScale = new Vector3(colliderRadius * 2.2f, colliderHeight * 0.5f, colliderRadius * 2.2f);

            Collider bodyCollider = bodyObject.GetComponent<Collider>();
            if (bodyCollider != null)
            {
                Destroy(bodyCollider);
            }

            Renderer bodyRenderer = bodyObject.GetComponent<Renderer>();
            if (bodyRenderer != null)
            {
                bodyRenderer.material.color = new Color(0.79f, 0.34f, 0.22f, 1f);
            }
        }

        if (healthBarPivot != null && healthBarFill != null) return;

        GameObject pivot = new("HealthBar");
        pivot.transform.SetParent(transform, false);
        pivot.transform.localPosition = new Vector3(0f, colliderHeight + 0.35f, 0f);
        healthBarPivot = pivot.transform;

        GameObject background = GameObject.CreatePrimitive(PrimitiveType.Cube);
        background.name = "Background";
        background.transform.SetParent(healthBarPivot, false);
        background.transform.localScale = new Vector3(0.82f, 0.1f, 0.04f);
        background.transform.localPosition = Vector3.zero;
        StripCollider(background);
        SetRendererColor(background, new Color(0.12f, 0.12f, 0.12f, 0.9f));

        GameObject fill = GameObject.CreatePrimitive(PrimitiveType.Cube);
        fill.name = "Fill";
        fill.transform.SetParent(healthBarPivot, false);
        fill.transform.localScale = new Vector3(0.8f, 0.06f, 0.02f);
        fill.transform.localPosition = Vector3.zero;
        StripCollider(fill);
        healthBarFill = fill.transform;
        healthBarFillRenderer = fill.GetComponent<Renderer>();
        RefreshHealthBar(CurrentHealth);
    }

    private static void StripCollider(GameObject gameObject)
    {
        Collider collider = gameObject.GetComponent<Collider>();
        if (collider != null)
        {
            Destroy(collider);
        }
    }

    private static void SetRendererColor(GameObject gameObject, Color color)
    {
        Renderer renderer = gameObject.GetComponent<Renderer>();
        if (renderer != null)
        {
            renderer.material.color = color;
        }
    }
}
