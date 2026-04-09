using UnityEngine;
using UnityEngine.InputSystem;
using Unity.Netcode;
using UnityEngine.SceneManagement;

[RequireComponent(typeof(CharacterController))]
public class PlayerMovement : NetworkBehaviour
{
    public float walkSpeed = 3.5f;
    public float runSpeed = 6f;
    public float gravity = -9.81f;
    public float jumpHeight = 1.5f; // added jump height
    public float initialGroundSnapDuration = 0.1f;
    public float groundSnapProbeDistance = 300f;
    public float groundSnapOffset = 1.1f;
    public LayerMask groundMask = ~0;
    public bool logGroundSnap = true;
    public bool forceAlwaysAnimate = true;

    private CharacterController controller;
    private Animator characterAnimator;
    private PlayerInput playerInput;
    private Vector3 velocity;
    private Gun legacyGun;
    private WeaponManager weaponManager;
    private float displayedMoveSpeed;
    private float displayedMotionSpeed;
    private float spawnTime;
    private bool groundSnapComplete;
    private bool groundSnapTimedOutLogged;
    private bool missingAnimatorControllerWarningLogged;
    private const float MinGroundNormalY = 0.25f;
    private const float RemoteLerpSpeed = 18f;
    private readonly NetworkVariable<Vector3> syncedPosition = new(
        default,
        NetworkVariableReadPermission.Everyone,
        NetworkVariableWritePermission.Owner);
    private readonly NetworkVariable<Quaternion> syncedRotation = new(
        Quaternion.identity,
        NetworkVariableReadPermission.Everyone,
        NetworkVariableWritePermission.Owner);
    private readonly NetworkVariable<float> syncedMoveSpeed = new(
        0f,
        NetworkVariableReadPermission.Everyone,
        NetworkVariableWritePermission.Owner);
    private readonly NetworkVariable<bool> syncedGrounded = new(
        true,
        NetworkVariableReadPermission.Everyone,
        NetworkVariableWritePermission.Owner);

    void Awake()
    {
        controller = GetComponent<CharacterController>();
        characterAnimator = FindPlayableAnimator();
        playerInput = GetComponent<PlayerInput>();
        weaponManager = GetComponent<WeaponManager>();
        legacyGun = GetComponentInChildren<Gun>(true);
        spawnTime = Time.time;
        if (characterAnimator != null)
        {
            characterAnimator.applyRootMotion = false;
            if (forceAlwaysAnimate)
            {
                characterAnimator.cullingMode = AnimatorCullingMode.AlwaysAnimate;
            }
        }
    }

    private void OnEnable()
    {
        SceneManager.sceneLoaded += OnSceneLoaded;
    }

    private void OnDisable()
    {
        SceneManager.sceneLoaded -= OnSceneLoaded;
    }

    public override void OnNetworkSpawn()
    {
        ResetGroundSnapWindow();

        if (IsOwner)
        {
            syncedPosition.Value = transform.position;
            syncedRotation.Value = transform.rotation;
            syncedMoveSpeed.Value = 0f;
            syncedGrounded.Value = controller != null && controller.isGrounded;
        }
    }

    private void OnSceneLoaded(Scene scene, LoadSceneMode mode)
    {
        if (!CanProcessLocalInput()) return;

        ResetGroundSnapWindow();
        TryRepositionToSceneSpawn(scene);
    }

    public void OnAttack(InputValue value)
    {
        if (!CanProcessLocalInput()) return;
        if (value == null || !value.isPressed) return;
        FireCurrentWeapon();
    }

    public void OnAttack()
    {
        if (!CanProcessLocalInput()) return;
        FireCurrentWeapon();
    }

    public void OnNext(InputValue value)
    {
        if (!CanProcessLocalInput()) return;
        if (value == null || !value.isPressed) return;
        SwitchToNextWeapon();
    }

    public void OnNext()
    {
        if (!CanProcessLocalInput()) return;
        SwitchToNextWeapon();
    }

    public void OnPrevious(InputValue value)
    {
        if (!CanProcessLocalInput()) return;
        if (value == null || !value.isPressed) return;
        SwitchToPreviousWeapon();
    }

    public void OnPrevious()
    {
        if (!CanProcessLocalInput()) return;
        SwitchToPreviousWeapon();
    }

    private void FireCurrentWeapon()
    {
        if (weaponManager != null)
        {
            weaponManager.FireCurrent();
            return;
        }

        if (legacyGun == null)
            legacyGun = GetComponentInChildren<Gun>(true);

        if (legacyGun == null)
        {
            Debug.LogWarning("PlayerMovement: Attack received but no Gun component was found in children.");
            return;
        }

        legacyGun.TriggerAttack();
    }

    private void SwitchToNextWeapon()
    {
        if (weaponManager == null)
        {
            Debug.LogWarning("PlayerMovement: No WeaponManager found for Next weapon input.");
            return;
        }

        weaponManager.NextWeapon();
    }

    private void SwitchToPreviousWeapon()
    {
        if (weaponManager == null)
        {
            Debug.LogWarning("PlayerMovement: No WeaponManager found for Previous weapon input.");
            return;
        }

        weaponManager.PreviousWeapon();
    }

    void Update()
    {
        if (IsSpawned && !IsOwner)
        {
            ApplyRemoteState();
            return;
        }

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

            // Fallback input while PlayerInput actions are not configured on the prefab.
            if (ShouldUseDirectInputFallback())
            {
                if (Mouse.current != null && Mouse.current.leftButton.wasPressedThisFrame)
                    FireCurrentWeapon();
                if (Keyboard.current.digit1Key.wasPressedThisFrame) SwitchToPreviousWeapon();
                if (Keyboard.current.digit2Key.wasPressedThisFrame) SwitchToNextWeapon();
            }
        }

        bool sprintHeld = Keyboard.current != null &&
            (Keyboard.current.leftShiftKey.isPressed || Keyboard.current.rightShiftKey.isPressed);
        float motionBlend = Mathf.Clamp01(input.magnitude);
        float currentMoveSpeed = sprintHeld ? runSpeed : walkSpeed;
        Vector3 move = transform.right * input.x + transform.forward * input.y;
        controller.Move(move * currentMoveSpeed * Time.deltaTime);

        // gravity
        if (controller.isGrounded && velocity.y < 0f)
            velocity.y = -2f;

        velocity.y += gravity * Time.deltaTime;
        controller.Move(velocity * Time.deltaTime);

        float locomotionSpeed = currentMoveSpeed * motionBlend;

        if (IsSpawned && IsOwner)
        {
            syncedPosition.Value = transform.position;
            syncedRotation.Value = transform.rotation;
            syncedMoveSpeed.Value = locomotionSpeed;
            syncedGrounded.Value = controller.isGrounded;
        }

        displayedMoveSpeed = Mathf.Lerp(displayedMoveSpeed, locomotionSpeed, Time.deltaTime * RemoteLerpSpeed);
        displayedMotionSpeed = Mathf.Lerp(displayedMotionSpeed, motionBlend, Time.deltaTime * RemoteLerpSpeed);
        UpdateAnimator(displayedMoveSpeed, displayedMotionSpeed, controller.isGrounded);
    }

    private bool CanProcessLocalInput()
    {
        if (!IsSpawned) return true;
        return IsOwner;
    }

    private bool ShouldUseDirectInputFallback()
    {
        return playerInput == null || playerInput.actions == null;
    }

    private void ResetGroundSnapWindow()
    {
        spawnTime = Time.time;
        groundSnapComplete = false;
        groundSnapTimedOutLogged = false;
    }

    private void TryRepositionToSceneSpawn(Scene scene)
    {
        if (!scene.IsValid() || !scene.isLoaded) return;

        Transform spawnPoint = FindSceneSpawnPoint(scene);
        if (spawnPoint == null) return;

        float probeDistance = Mathf.Max(1f, groundSnapProbeDistance);
        Vector3 rayOrigin = spawnPoint.position + Vector3.up * (probeDistance * 0.5f);
        Vector3 targetPosition = TryFindGroundHit(rayOrigin, probeDistance, out RaycastHit hit)
            ? hit.point + Vector3.up * Mathf.Max(0.05f, groundSnapOffset)
            : spawnPoint.position + Vector3.up * Mathf.Max(0.05f, groundSnapOffset);

        bool controllerWasEnabled = controller != null && controller.enabled;
        if (controllerWasEnabled) controller.enabled = false;
        transform.SetPositionAndRotation(targetPosition, spawnPoint.rotation);
        if (controllerWasEnabled) controller.enabled = true;

        velocity = Vector3.zero;

        if (IsSpawned && IsOwner)
        {
            syncedPosition.Value = transform.position;
            syncedRotation.Value = transform.rotation;
            syncedMoveSpeed.Value = 0f;
            syncedGrounded.Value = controller != null && controller.isGrounded;
        }

        if (logGroundSnap)
        {
            Debug.Log(
                $"PlayerMovement: repositioned to spawn '{spawnPoint.name}' in scene '{scene.name}' at {transform.position}.");
        }
    }

    private Transform FindSceneSpawnPoint(Scene scene)
    {
        GameObject[] roots = scene.GetRootGameObjects();
        Transform firstSpawnPoint = null;

        for (int i = 0; i < roots.Length; i++)
        {
            Transform[] transforms = roots[i].GetComponentsInChildren<Transform>(true);
            for (int j = 0; j < transforms.Length; j++)
            {
                Transform candidate = transforms[j];
                if (candidate == null) continue;
                if (!candidate.name.StartsWith("SpawnPoint", System.StringComparison.OrdinalIgnoreCase)) continue;

                if (firstSpawnPoint == null)
                {
                    firstSpawnPoint = candidate;
                }

                if (candidate.gameObject.activeInHierarchy)
                {
                    return candidate;
                }
            }
        }

        return firstSpawnPoint;
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
                    $"PlayerMovement: ground snap timed out for owner={IsOwner}. " +
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
        groundSnapComplete = true;

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
            if (hit.normal.y < MinGroundNormalY) continue;

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

    private void ApplyRemoteState()
    {
        if (!controller.enabled)
        {
            transform.position = Vector3.Lerp(transform.position, syncedPosition.Value, Time.deltaTime * RemoteLerpSpeed);
            transform.rotation = Quaternion.Slerp(transform.rotation, syncedRotation.Value, Time.deltaTime * RemoteLerpSpeed);
            return;
        }

        controller.enabled = false;
        transform.position = Vector3.Lerp(transform.position, syncedPosition.Value, Time.deltaTime * RemoteLerpSpeed);
        transform.rotation = Quaternion.Slerp(transform.rotation, syncedRotation.Value, Time.deltaTime * RemoteLerpSpeed);
        controller.enabled = true;

        float smoothedSpeed = Mathf.Lerp(displayedMoveSpeed, syncedMoveSpeed.Value, Time.deltaTime * RemoteLerpSpeed);
        displayedMoveSpeed = smoothedSpeed;
        float targetMotionSpeed = syncedMoveSpeed.Value > 0.01f ? 1f : 0f;
        float smoothedMotionSpeed = Mathf.Lerp(displayedMotionSpeed, targetMotionSpeed, Time.deltaTime * RemoteLerpSpeed);
        displayedMotionSpeed = smoothedMotionSpeed;
        UpdateAnimator(smoothedSpeed, smoothedMotionSpeed, syncedGrounded.Value);
    }

    private void UpdateAnimator(float moveSpeed, float motionSpeed, bool grounded)
    {
        if (characterAnimator == null) return;
        if (characterAnimator.runtimeAnimatorController == null)
        {
            if (!missingAnimatorControllerWarningLogged)
            {
                Debug.LogWarning(
                    "PlayerMovement: Animator exists but has no RuntimeAnimatorController; skipping animator parameter updates.");
                missingAnimatorControllerWarningLogged = true;
            }
            return;
        }

        // Supports both the custom lightweight controller and the imported Starter Assets controller.
        SetAnimatorFloatIfExists("speed", motionSpeed);
        SetAnimatorFloatIfExists("Speed", moveSpeed);
        SetAnimatorFloatIfExists("MoveSpeed", moveSpeed);
        SetAnimatorFloatIfExists("Velocity", moveSpeed);
        SetAnimatorFloatIfExists("MotionSpeed", motionSpeed);

        SetAnimatorBoolIfExists("Grounded", grounded);
        SetAnimatorBoolIfExists("IsGrounded", grounded);
    }

    private void SetAnimatorFloatIfExists(string name, float value)
    {
        int hash = Animator.StringToHash(name);
        if (!HasAnimatorParameter(hash, AnimatorControllerParameterType.Float)) return;
        characterAnimator.SetFloat(hash, value);
    }

    private void SetAnimatorBoolIfExists(string name, bool value)
    {
        int hash = Animator.StringToHash(name);
        if (!HasAnimatorParameter(hash, AnimatorControllerParameterType.Bool)) return;
        characterAnimator.SetBool(hash, value);
    }

    private bool HasAnimatorParameter(int hash, AnimatorControllerParameterType type)
    {
        if (characterAnimator == null) return false;
        if (characterAnimator.runtimeAnimatorController == null) return false;
        var parameters = characterAnimator.parameters;
        for (int i = 0; i < parameters.Length; i++)
        {
            var p = parameters[i];
            if (p.nameHash == hash && p.type == type) return true;
        }

        return false;
    }

    private Animator FindPlayableAnimator()
    {
        Animator[] animators = GetComponentsInChildren<Animator>(true);
        Animator fallback = null;

        for (int i = 0; i < animators.Length; i++)
        {
            Animator candidate = animators[i];
            if (candidate == null) continue;
            if (fallback == null) fallback = candidate;

            if (candidate.runtimeAnimatorController != null)
            {
                return candidate;
            }
        }

        return fallback;
    }
}
