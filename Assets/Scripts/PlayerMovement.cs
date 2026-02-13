using UnityEngine;
using UnityEngine.InputSystem;
using Unity.Netcode;

[RequireComponent(typeof(CharacterController))]
public class PlayerMovement : MonoBehaviour
{
    public float speed = 6f;
    public float gravity = -9.81f;
    public float jumpHeight = 1.5f; // added jump height
    public float initialGroundSnapDuration = 5f;
    public float groundSnapProbeDistance = 300f;
    public float groundSnapOffset = 1.1f;
    public LayerMask groundMask = ~0;
    public bool logGroundSnap = true;

    private CharacterController controller;
    private Vector3 velocity;
    private Gun gun;
    private NetworkObject networkObject;
    private float spawnTime;
    private bool groundSnapComplete;
    private bool groundSnapTimedOutLogged;

    void Awake()
    {
        controller = GetComponent<CharacterController>();
        gun = GetComponentInChildren<Gun>(true);
        networkObject = GetComponent<NetworkObject>();
        spawnTime = Time.time;
    }

    public void OnAttack(InputValue value)
    {
        if (!CanProcessLocalInput()) return;
        if (value == null || !value.isPressed) return;
        FireCurrentGun();
    }

    public void OnAttack()
    {
        if (!CanProcessLocalInput()) return;
        FireCurrentGun();
    }

    private void FireCurrentGun()
    {
        if (gun == null)
            gun = GetComponentInChildren<Gun>(true);

        if (gun == null)
        {
            Debug.LogWarning("PlayerMovement: Attack received but no Gun component was found in children.");
            return;
        }

        gun.TriggerAttack();
    }

    void Update()
    {
        if (!CanProcessLocalInput()) return;

        TryGroundSnapDuringSpawnWindow();

        // New Input System: WASD/Arrows
        Vector2 input = Vector2.zero;
        if (Keyboard.current != null)
        {
            float x = 0f;
            float z = 0f;

            if (Keyboard.current.aKey.isPressed || Keyboard.current.leftArrowKey.isPressed) x -= 1f;
            if (Keyboard.current.dKey.isPressed || Keyboard.current.rightArrowKey.isPressed) x += 1f;
            if (Keyboard.current.wKey.isPressed || Keyboard.current.upArrowKey.isPressed) z += 1f;
            if (Keyboard.current.sKey.isPressed || Keyboard.current.downArrowKey.isPressed) z -= 1f;

            input = new Vector2(x, z);
            if (input.sqrMagnitude > 1f) input = input.normalized; // no faster diagonals

            // jump: spacebar
            if (controller.isGrounded && Keyboard.current.spaceKey.wasPressedThisFrame)
            {
                Debug.Log("PlayerMovement: Jump initiated");
                velocity.y = Mathf.Sqrt(jumpHeight * -2f * gravity);
            }
        }

        Vector3 move = transform.right * input.x + transform.forward * input.y;
        controller.Move(move * speed * Time.deltaTime);

        // gravity
        if (controller.isGrounded && velocity.y < 0f)
            velocity.y = -2f;

        velocity.y += gravity * Time.deltaTime;
        controller.Move(velocity * Time.deltaTime);
    }

    private bool CanProcessLocalInput()
    {
        if (networkObject == null) return true;
        if (NetworkManager.Singleton == null || !NetworkManager.Singleton.IsListening) return true;
        return networkObject.IsOwner;
    }

    private void TryGroundSnapDuringSpawnWindow()
    {
        if (groundSnapComplete) return;
        if (controller.isGrounded)
        {
            groundSnapComplete = true;
            return;
        }

        if (Time.time - spawnTime > initialGroundSnapDuration)
        {
            groundSnapComplete = true;
            if (logGroundSnap && !groundSnapTimedOutLogged)
            {
                Debug.LogWarning(
                    $"PlayerMovement: ground snap timed out for owner={networkObject != null && networkObject.IsOwner}. " +
                    $"position={transform.position}");
                groundSnapTimedOutLogged = true;
            }
            return;
        }

        float probeDistance = Mathf.Max(1f, groundSnapProbeDistance);
        Vector3 rayOrigin = transform.position + Vector3.up * (probeDistance * 0.5f);
        if (!TryFindGroundHit(rayOrigin, probeDistance, out RaycastHit hit))
        {
            return;
        }

        bool controllerWasEnabled = controller.enabled;
        if (controllerWasEnabled) controller.enabled = false;
        transform.position = hit.point + Vector3.up * Mathf.Max(0.05f, groundSnapOffset);
        if (controllerWasEnabled) controller.enabled = true;
        velocity.y = 0f;

        if (logGroundSnap)
        {
            Debug.Log(
                $"PlayerMovement: snapped to ground at {transform.position} from hit {hit.point}.");
        }
    }

    private bool TryFindGroundHit(Vector3 rayOrigin, float probeDistance, out RaycastHit groundHit)
    {
        RaycastHit[] hits = Physics.RaycastAll(
            rayOrigin,
            Vector3.down,
            probeDistance,
            groundMask,
            QueryTriggerInteraction.Ignore);

        if (hits == null || hits.Length == 0)
        {
            groundHit = default;
            return false;
        }

        System.Array.Sort(hits, (a, b) => a.distance.CompareTo(b.distance));
        for (int i = 0; i < hits.Length; i++)
        {
            RaycastHit hit = hits[i];
            if (IsSelfOrCharacterControllerHit(hit.collider)) continue;

            groundHit = hit;
            return true;
        }

        groundHit = default;
        return false;
    }

    private bool IsSelfOrCharacterControllerHit(Collider col)
    {
        if (col == null) return false;

        Transform hitTransform = col.transform;
        if (hitTransform == transform || hitTransform.IsChildOf(transform))
        {
            return true;
        }

        return col.GetComponent<CharacterController>() != null;
    }
}
