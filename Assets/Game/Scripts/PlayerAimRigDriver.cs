using System;
using System.Collections.Generic;
using Unity.Netcode;
using UnityEngine;
using UnityEngine.Animations.Rigging;

[DisallowMultipleComponent]
public class PlayerAimRigDriver : NetworkBehaviour
{
    private enum WeaponIKTargetSource
    {
        Auto,
        WorldModelOnly,
        ViewModelOnly
    }

    [Header("Aim Target")]
    [SerializeField] private Transform aimTarget;
    [SerializeField] private Transform aimSource;
    [SerializeField] private float aimDistance = 25f;
    [SerializeField] private bool driveOnlyForOwner = true;
    [SerializeField] private bool createAimTargetIfMissing = true;
    [SerializeField] private string aimTargetName = "AimTarget";
    [SerializeField] private bool driveAimTargetFromCamera = true;
    [SerializeField] private bool clampRigAimPitch = true;
    [SerializeField] private float minRigAimPitch = -40f;
    [SerializeField] private float maxRigAimPitch = 30f;

    [Header("Body Yaw")]
    [SerializeField] private bool rotateBodyYawToAim;
    [SerializeField] private float yawRotationSpeed = 14f;

    [Header("Constraint Auto-Wire")]
    [SerializeField] private bool patchRigAtRuntime;
    [SerializeField] private bool autoWireMultiAimConstraints = true;
    [SerializeField] private bool wireAllMultiAimConstraints;
    [SerializeField] private string[] preferredConstraintNames = { "SpineAim", "UpperChestAim" };
    [SerializeField] private bool keepRigsUnderAnimatedHierarchy = true;
    [SerializeField] private bool autoCreateAimRigIfMissing = true;
    [Header("Weapon Hand IK")]
    [SerializeField] private bool enableWeaponHandIK = true;
    [SerializeField] private bool useRightHandIK;
    [SerializeField] private bool useLeftHandIK = true;
    [SerializeField, Range(0f, 1f)] private float rightHandIKHintWeight = 0f;
    [SerializeField, Range(0f, 1f)] private float leftHandIKHintWeight = 0f;
    [SerializeField] private bool driveWeaponHandIKForOwnerOnly;
    [SerializeField] private bool applyWeaponHandIKForOwnerViewModel = true;
    [SerializeField] private WeaponIKTargetSource weaponIKTargetSource = WeaponIKTargetSource.WorldModelOnly;
    [SerializeField] private bool preventCyclicIKTargets = true;
    [SerializeField] private bool createFallbackWeaponGripsIfMissing;
    [SerializeField] private string rightHandGripName = "RightHandGrip";
    [SerializeField] private string leftHandGripName = "LeftHandGrip";
    [SerializeField] private bool keepHandTargetsOutsideCharacterHitbox = true;
    [SerializeField, Min(0f)] private float handTargetHitboxPadding = 0.12f;
    [SerializeField] private bool keepLeftElbowOutsideCharacterHitbox = true;
    [SerializeField, Min(0f)] private float leftElbowHitboxPadding = 0.08f;
    [Header("IK Console Debug")]
    [SerializeField] private bool debugConsoleIK;
    [SerializeField] private float debugConsoleIKInterval = 0.4f;
    [SerializeField] private bool debugShowHandTargets;
    [SerializeField] private float debugTargetRadius = 0.04f;
    [SerializeField] private Color debugRightHandColor = Color.cyan;
    [SerializeField] private Color debugLeftHandColor = Color.magenta;
    [SerializeField] private Vector3 rightHandGripFallbackLocalPosition = new(0.11f, -0.06f, 0.2f);
    [SerializeField] private Vector3 leftHandGripFallbackLocalPosition = new(-0.08f, -0.02f, 0.08f);
    [SerializeField] private Vector3 rightElbowHintLocalOffset = new(0.2f, -0.05f, -0.2f);
    [SerializeField] private Vector3 leftElbowHintLocalOffset = new(-0.2f, -0.05f, -0.2f);

    private RigBuilder activeRigBuilder;
    private Animator animatedHierarchyAnimator;
    private WeaponManager weaponManager;
    private CharacterController characterController;
    private bool initialized;
    private bool ownsAimTarget;
    private TwoBoneIKConstraint rightHandIKConstraint;
    private TwoBoneIKConstraint leftHandIKConstraint;
    private Transform rightHandIKTarget;
    private Transform leftHandIKTarget;
    private Transform rightHandIKHint;
    private Transform leftHandIKHint;
    private Transform rightUpperArm;
    private Transform leftUpperArm;
    private bool loggedWeaponIKTarget;
    private GameObject debugRightHandle;
    private GameObject debugLeftHandle;
    private float nextIKDebugLogTime;
    private string lastIKDebugMessage;

    private void Awake()
    {
        weaponManager = GetComponent<WeaponManager>();
        characterController = GetComponent<CharacterController>();
        InitializeIfNeeded();
    }

    public override void OnNetworkSpawn()
    {
        InitializeIfNeeded();
    }

    private void OnEnable()
    {
        initialized = false;
    }

    private void LateUpdate()
    {
        if (!InitializeIfNeeded()) return;

        if (enableWeaponHandIK)
        {
            UpdateWeaponHandIKTargets();
        }

        if (!ShouldDriveAim()) return;
        if (aimSource == null || aimTarget == null) return;

        Vector3 targetPosition = aimTarget.position;
        if (driveAimTargetFromCamera && ownsAimTarget)
        {
            float distance = Mathf.Max(1f, aimDistance);
            Vector3 aimDirection = ResolveRigAimDirection();
            targetPosition = aimSource.position + (aimDirection * distance);
            aimTarget.position = targetPosition;
        }

        if (!rotateBodyYawToAim) return;

        Vector3 flatDirection = targetPosition - transform.position;
        flatDirection.y = 0f;
        if (flatDirection.sqrMagnitude < 0.0001f) return;

        Quaternion desiredYaw = Quaternion.LookRotation(flatDirection.normalized, Vector3.up);
        float t = Mathf.Clamp01(yawRotationSpeed * Time.deltaTime);
        transform.rotation = Quaternion.Slerp(transform.rotation, desiredYaw, t);
    }

    private Vector3 ResolveRigAimDirection()
    {
        if (aimSource == null) return transform.forward;

        Vector3 direction = aimSource.forward;
        if (!clampRigAimPitch) return direction.normalized;

        Vector3 localDirection = transform.InverseTransformDirection(direction.normalized);
        float yaw = Mathf.Atan2(localDirection.x, localDirection.z) * Mathf.Rad2Deg;
        float horizontalMagnitude = new Vector2(localDirection.x, localDirection.z).magnitude;
        float pitch = Mathf.Atan2(localDirection.y, Mathf.Max(0.0001f, horizontalMagnitude)) * Mathf.Rad2Deg;
        float clampedPitch = Mathf.Clamp(pitch, minRigAimPitch, maxRigAimPitch);

        Quaternion clampedLocalRotation = Quaternion.Euler(clampedPitch, yaw, 0f);
        return transform.TransformDirection(clampedLocalRotation * Vector3.forward).normalized;
    }

    private bool InitializeIfNeeded()
    {
        if (initialized) return true;

        if (patchRigAtRuntime || autoCreateAimRigIfMissing)
        {
            animatedHierarchyAnimator = FindPlayableAnimator();
            TryMoveRigBuilderToAnimatedHierarchy();
        }
        EnsureAimSource();
        EnsureAimTarget();

        bool rigChanged = false;
        if (autoCreateAimRigIfMissing)
        {
            rigChanged |= EnsureAimRigExists();
        }

        if (enableWeaponHandIK)
        {
            rigChanged |= EnsureWeaponHandIKRigExists();
        }

        if ((patchRigAtRuntime || autoCreateAimRigIfMissing) && autoWireMultiAimConstraints)
        {
            AutoWireMultiAimConstraints();
            rigChanged = true;
        }

        if (patchRigAtRuntime || rigChanged)
        {
            RebuildRig();
        }

        LogIKDebug(
            $"Initialized. owner={IsOwner} spawned={IsSpawned} " +
            $"enableWeaponHandIK={enableWeaponHandIK} " +
            $"rightConstraint={(rightHandIKConstraint != null)} leftConstraint={(leftHandIKConstraint != null)} " +
            $"rightTarget={(rightHandIKTarget != null)} leftTarget={(leftHandIKTarget != null)}",
            true);
        initialized = true;
        return true;
    }

    private bool ShouldDriveAim()
    {
        if (!driveOnlyForOwner) return true;
        if (!IsSpawned) return true;
        return IsOwner;
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

            if (candidate.avatar != null && candidate.runtimeAnimatorController != null)
            {
                return candidate;
            }
        }

        return fallback;
    }

    private void TryMoveRigBuilderToAnimatedHierarchy()
    {
        RigBuilder rootRigBuilder = GetComponent<RigBuilder>();
        Animator rootAnimator = GetComponent<Animator>();

        if (animatedHierarchyAnimator == null)
        {
            activeRigBuilder = rootRigBuilder;
            EnsureRigLayerReference(activeRigBuilder);
            return;
        }

        RigBuilder targetRigBuilder = animatedHierarchyAnimator.GetComponent<RigBuilder>();
        if (targetRigBuilder == null)
        {
            targetRigBuilder = animatedHierarchyAnimator.gameObject.AddComponent<RigBuilder>();
        }

        // Clear both possible graphs before we mutate layers/rig ownership.
        if (rootRigBuilder != null) rootRigBuilder.Clear();
        if (targetRigBuilder != null && targetRigBuilder != rootRigBuilder) targetRigBuilder.Clear();

        EnsureRigLayerReference(rootRigBuilder);
        EnsureRigLayerReference(targetRigBuilder);

        if (rootRigBuilder != null && rootRigBuilder != targetRigBuilder)
        {
            if (rootRigBuilder.layers != null && rootRigBuilder.layers.Count > 0)
            {
                var copiedLayers = new List<RigLayer>(rootRigBuilder.layers.Count);
                for (int i = 0; i < rootRigBuilder.layers.Count; i++)
                {
                    RigLayer sourceLayer = rootRigBuilder.layers[i];
                    if (sourceLayer == null || sourceLayer.rig == null) continue;
                    copiedLayers.Add(new RigLayer(sourceLayer.rig, sourceLayer.active));
                }

                if (copiedLayers.Count > 0)
                {
                    targetRigBuilder.layers = copiedLayers;
                }
            }

            rootRigBuilder.enabled = false;
        }

        if (rootAnimator != null && rootAnimator != animatedHierarchyAnimator)
        {
            bool rootAnimatorIsUnconfigured = rootAnimator.avatar == null && rootAnimator.runtimeAnimatorController == null;
            if (rootAnimatorIsUnconfigured)
            {
                rootAnimator.enabled = false;
            }
        }

        EnsureRigLayerReference(targetRigBuilder);

        if (keepRigsUnderAnimatedHierarchy && targetRigBuilder != null && animatedHierarchyAnimator != null)
        {
            ReparentRigsUnderAnimator(targetRigBuilder, animatedHierarchyAnimator.transform);
        }

        activeRigBuilder = targetRigBuilder != null ? targetRigBuilder : rootRigBuilder;
    }

    private void EnsureRigLayerReference(RigBuilder rigBuilder)
    {
        if (rigBuilder == null) return;

        List<RigLayer> layers = rigBuilder.layers;
        if (layers == null) return;

        layers.RemoveAll(layer => layer == null || layer.rig == null);
        if (layers.Count > 0) return;

        Rig fallbackRig = FindDescendantByName(transform, "Rig 1")?.GetComponent<Rig>();
        if (fallbackRig != null)
        {
            layers.Add(new RigLayer(fallbackRig, true));
        }
    }

    private void ReparentRigsUnderAnimator(RigBuilder rigBuilder, Transform animatorRoot)
    {
        if (rigBuilder == null || animatorRoot == null) return;
        List<RigLayer> layers = rigBuilder.layers;
        if (layers == null || layers.Count == 0) return;

        for (int i = 0; i < layers.Count; i++)
        {
            RigLayer layer = layers[i];
            if (layer == null || layer.rig == null) continue;

            Transform rigTransform = layer.rig.transform;
            if (rigTransform == null) continue;
            if (rigTransform.IsChildOf(animatorRoot)) continue;

            rigTransform.SetParent(animatorRoot, true);
        }
    }

    private void EnsureAimSource()
    {
        if (aimSource != null) return;

        aimSource = FindDescendantByName(transform, "PlayerCamera");
        if (aimSource == null)
        {
            aimSource = FindDescendantByName(transform, "CameraPivot");
        }
    }

    private void EnsureAimTarget()
    {
        if (aimTarget == null && !string.IsNullOrWhiteSpace(aimTargetName))
        {
            aimTarget = FindDescendantByName(transform, aimTargetName);
            if (aimTarget == null)
            {
                GameObject globalTarget = GameObject.Find(aimTargetName);
                if (globalTarget != null)
                {
                    aimTarget = globalTarget.transform;
                }
            }
        }

        if (aimTarget != null)
        {
            ownsAimTarget = aimTarget.IsChildOf(transform);
            return;
        }

        if (!createAimTargetIfMissing) return;

        var targetObject = new GameObject(string.IsNullOrWhiteSpace(aimTargetName) ? "AimTarget" : aimTargetName);
        targetObject.transform.SetParent(transform, false);

        float distance = Mathf.Max(1f, aimDistance);
        if (aimSource != null)
        {
            targetObject.transform.position = aimSource.position + (aimSource.forward * distance);
        }
        else
        {
            targetObject.transform.position = transform.position + (transform.forward * distance);
        }

        aimTarget = targetObject.transform;
        ownsAimTarget = true;
    }

    private void AutoWireMultiAimConstraints()
    {
        if (aimTarget == null) return;

        MultiAimConstraint[] constraints = GetComponentsInChildren<MultiAimConstraint>(true);
        for (int i = 0; i < constraints.Length; i++)
        {
            MultiAimConstraint constraint = constraints[i];
            if (constraint == null || !ShouldConfigureConstraint(constraint)) continue;

            MultiAimConstraintData data = constraint.data;

            if (data.constrainedObject == null)
            {
                Transform inferredBone = InferConstrainedBone(constraint.name);
                if (inferredBone != null)
                {
                    data.constrainedObject = inferredBone;
                }
            }

            WeightedTransformArray sources = data.sourceObjects;
            bool hasAimTarget = false;

            for (int s = 0; s < sources.Count; s++)
            {
                if (sources[s].transform != aimTarget) continue;

                hasAimTarget = true;
                if (sources[s].weight <= 0f)
                {
                    var weightedTransform = sources[s];
                    weightedTransform.weight = 1f;
                    sources[s] = weightedTransform;
                }
            }

            if (!hasAimTarget)
            {
                if (sources.Count < WeightedTransformArray.k_MaxLength)
                {
                    sources.Add(new WeightedTransform(aimTarget, 1f));
                }
                else if (sources.Count > 0)
                {
                    sources.SetTransform(0, aimTarget);
                    sources.SetWeight(0, 1f);
                }
            }

            data.sourceObjects = sources;
            constraint.data = data;
            if (constraint.weight <= 0f)
            {
                constraint.weight = 1f;
            }
        }
    }

    private bool ShouldConfigureConstraint(MultiAimConstraint constraint)
    {
        if (wireAllMultiAimConstraints) return true;
        if (preferredConstraintNames == null || preferredConstraintNames.Length == 0) return false;

        for (int i = 0; i < preferredConstraintNames.Length; i++)
        {
            string expectedName = preferredConstraintNames[i];
            if (string.IsNullOrWhiteSpace(expectedName)) continue;
            if (string.Equals(constraint.name, expectedName, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    private Transform InferConstrainedBone(string constraintObjectName)
    {
        if (string.IsNullOrWhiteSpace(constraintObjectName)) return null;

        if (constraintObjectName.IndexOf("UpperChest", StringComparison.OrdinalIgnoreCase) >= 0)
        {
            Transform upperChest = FindDescendantByName(transform, "UpperChest");
            if (upperChest != null) return upperChest;
        }

        if (constraintObjectName.IndexOf("Spine", StringComparison.OrdinalIgnoreCase) >= 0)
        {
            Transform spine = FindDescendantByName(transform, "Spine");
            if (spine != null) return spine;
        }

        return null;
    }

    private bool EnsureAimRigExists()
    {
        if (aimTarget == null || animatedHierarchyAnimator == null) return false;

        if (activeRigBuilder == null)
        {
            activeRigBuilder = animatedHierarchyAnimator.GetComponent<RigBuilder>();
            if (activeRigBuilder == null)
            {
                activeRigBuilder = animatedHierarchyAnimator.gameObject.AddComponent<RigBuilder>();
            }
        }

        MultiAimConstraint[] existingConstraints = animatedHierarchyAnimator.GetComponentsInChildren<MultiAimConstraint>(true);
        if (existingConstraints.Length > 0)
        {
            EnsureRigLayerReference(activeRigBuilder);
            return false;
        }

        Rig rig = FindOrCreateRigRoot();
        if (rig == null) return false;

        bool createdAnyConstraint = false;
        createdAnyConstraint |= EnsureConstraintUnderRig(rig.transform, "SpineAim", "Spine");
        createdAnyConstraint |= EnsureConstraintUnderRig(rig.transform, "UpperChestAim", "UpperChest", "Chest");

        EnsureRigLayerContainsRig(activeRigBuilder, rig);
        return createdAnyConstraint;
    }

    private Rig FindOrCreateRigRoot()
    {
        Transform animatorRoot = animatedHierarchyAnimator != null ? animatedHierarchyAnimator.transform : transform;
        Transform existingRigTransform = FindDescendantByName(animatorRoot, "Rig 1");
        Rig existingRig = existingRigTransform != null ? existingRigTransform.GetComponent<Rig>() : null;
        if (existingRig != null) return existingRig;

        GameObject rigObject = new("Rig 1");
        rigObject.transform.SetParent(animatorRoot, false);
        return rigObject.AddComponent<Rig>();
    }

    private bool EnsureConstraintUnderRig(Transform rigRoot, string constraintName, params string[] boneCandidates)
    {
        if (rigRoot == null || string.IsNullOrWhiteSpace(constraintName) || boneCandidates == null || boneCandidates.Length == 0)
        {
            return false;
        }

        Transform constrainedBone = FindFirstDescendantByName(animatedHierarchyAnimator.transform, boneCandidates);
        if (constrainedBone == null) return false;

        Transform constraintTransform = FindDescendantByName(rigRoot, constraintName);
        bool wasCreated = false;
        if (constraintTransform == null)
        {
            GameObject constraintObject = new(constraintName);
            constraintObject.transform.SetParent(rigRoot, false);
            constraintTransform = constraintObject.transform;
            wasCreated = true;
        }

        MultiAimConstraint constraint = constraintTransform.GetComponent<MultiAimConstraint>();
        if (constraint == null)
        {
            constraint = constraintTransform.gameObject.AddComponent<MultiAimConstraint>();
            wasCreated = true;
        }

        MultiAimConstraintData data = constraint.data;
        data.constrainedObject = constrainedBone;

        WeightedTransformArray sources = data.sourceObjects;
        bool hasAimTarget = false;
        for (int i = 0; i < sources.Count; i++)
        {
            if (sources[i].transform != aimTarget) continue;
            hasAimTarget = true;
            if (sources[i].weight <= 0f)
            {
                var weightedTransform = sources[i];
                weightedTransform.weight = 1f;
                sources[i] = weightedTransform;
            }
        }

        if (!hasAimTarget)
        {
            if (sources.Count < WeightedTransformArray.k_MaxLength)
            {
                sources.Add(new WeightedTransform(aimTarget, 1f));
            }
            else if (sources.Count > 0)
            {
                sources.SetTransform(0, aimTarget);
                sources.SetWeight(0, 1f);
            }
        }

        data.sourceObjects = sources;
        constraint.data = data;
        if (constraint.weight <= 0f)
        {
            constraint.weight = 1f;
        }

        return wasCreated;
    }

    private static Transform FindFirstDescendantByName(Transform root, params string[] names)
    {
        if (root == null || names == null || names.Length == 0) return null;

        for (int i = 0; i < names.Length; i++)
        {
            string candidate = names[i];
            if (string.IsNullOrWhiteSpace(candidate)) continue;

            Transform found = FindDescendantByName(root, candidate);
            if (found != null) return found;
        }

        return null;
    }

    private static void EnsureRigLayerContainsRig(RigBuilder rigBuilder, Rig rig)
    {
        if (rigBuilder == null || rig == null) return;

        List<RigLayer> layers = rigBuilder.layers;
        if (layers == null) return;

        for (int i = 0; i < layers.Count; i++)
        {
            RigLayer layer = layers[i];
            if (layer != null && layer.rig == rig) return;
        }

        layers.Add(new RigLayer(rig, true));
    }

    private bool EnsureWeaponHandIKRigExists()
    {
        if (animatedHierarchyAnimator == null) return false;

        if (activeRigBuilder == null)
        {
            activeRigBuilder = animatedHierarchyAnimator.GetComponent<RigBuilder>();
            if (activeRigBuilder == null)
            {
                activeRigBuilder = animatedHierarchyAnimator.gameObject.AddComponent<RigBuilder>();
            }
        }

        Rig rig = FindOrCreateRigRoot();
        if (rig == null) return false;

        rightUpperArm = FindFirstDescendantByName(animatedHierarchyAnimator.transform, "Right_UpperArm", "RightUpperArm");
        Transform rightLowerArm = FindFirstDescendantByName(animatedHierarchyAnimator.transform, "Right_LowerArm", "RightLowerArm");
        Transform rightHand = FindFirstDescendantByName(animatedHierarchyAnimator.transform, "Right_Hand", "RightHand");

        leftUpperArm = FindFirstDescendantByName(animatedHierarchyAnimator.transform, "Left_UpperArm", "LeftUpperArm");
        Transform leftLowerArm = FindFirstDescendantByName(animatedHierarchyAnimator.transform, "Left_LowerArm", "LeftLowerArm");
        Transform leftHand = FindFirstDescendantByName(animatedHierarchyAnimator.transform, "Left_Hand", "LeftHand");

        bool changed = false;

        Transform rightConstraintTransform = FindOrCreateChild(rig.transform, "RightHandIK");
        Transform leftConstraintTransform = FindOrCreateChild(rig.transform, "LeftHandIK");
        rightHandIKTarget = FindOrCreateChild(rig.transform, "RightHandIK_Target");
        leftHandIKTarget = FindOrCreateChild(rig.transform, "LeftHandIK_Target");

        if (rightUpperArm != null)
        {
            rightHandIKHint = FindOrCreateChild(rightUpperArm, "RightHandIK_Hint");
            if (rightHandIKHint.localPosition != rightElbowHintLocalOffset)
            {
                rightHandIKHint.localPosition = rightElbowHintLocalOffset;
                changed = true;
            }
        }

        if (leftUpperArm != null)
        {
            leftHandIKHint = FindOrCreateChild(leftUpperArm, "LeftHandIK_Hint");
            if (leftHandIKHint.localPosition != leftElbowHintLocalOffset)
            {
                leftHandIKHint.localPosition = leftElbowHintLocalOffset;
                changed = true;
            }
        }

        changed |= EnsureTwoBoneIKConstraint(
            ref rightHandIKConstraint,
            rightConstraintTransform,
            rightUpperArm,
            rightLowerArm,
            rightHand,
            rightHandIKTarget,
            rightHandIKHint,
            rightHandIKHintWeight);

        changed |= EnsureTwoBoneIKConstraint(
            ref leftHandIKConstraint,
            leftConstraintTransform,
            leftUpperArm,
            leftLowerArm,
            leftHand,
            leftHandIKTarget,
            leftHandIKHint,
            leftHandIKHintWeight);

        EnsureRigLayerContainsRig(activeRigBuilder, rig);
        return changed;
    }

    private static bool EnsureTwoBoneIKConstraint(
        ref TwoBoneIKConstraint constraint,
        Transform constraintTransform,
        Transform rootBone,
        Transform midBone,
        Transform tipBone,
        Transform targetTransform,
        Transform hintTransform,
        float hintWeight)
    {
        if (constraintTransform == null || rootBone == null || midBone == null || tipBone == null || targetTransform == null)
        {
            return false;
        }

        bool changed = false;
        if (constraint == null)
        {
            constraint = constraintTransform.GetComponent<TwoBoneIKConstraint>();
        }

        if (constraint == null)
        {
            constraint = constraintTransform.gameObject.AddComponent<TwoBoneIKConstraint>();
            changed = true;
        }

        TwoBoneIKConstraintData data = constraint.data;
        if (data.root != rootBone)
        {
            data.root = rootBone;
            changed = true;
        }

        if (data.mid != midBone)
        {
            data.mid = midBone;
            changed = true;
        }

        if (data.tip != tipBone)
        {
            data.tip = tipBone;
            changed = true;
        }

        if (data.target != targetTransform)
        {
            data.target = targetTransform;
            changed = true;
        }

        float clampedHintWeight = Mathf.Clamp01(hintWeight);
        if (clampedHintWeight <= 0.001f)
        {
            if (data.hint != null)
            {
                data.hint = null;
                changed = true;
            }
        }
        else if (hintTransform != null && data.hint != hintTransform)
        {
            data.hint = hintTransform;
            changed = true;
        }

        if (Mathf.Abs(data.targetPositionWeight - 1f) > 0.001f)
        {
            data.targetPositionWeight = 1f;
            changed = true;
        }

        if (Mathf.Abs(data.targetRotationWeight - 1f) > 0.001f)
        {
            data.targetRotationWeight = 1f;
            changed = true;
        }

        if (Mathf.Abs(data.hintWeight - clampedHintWeight) > 0.001f)
        {
            data.hintWeight = clampedHintWeight;
            changed = true;
        }

        constraint.data = data;
        if (constraint.weight <= 0f)
        {
            constraint.weight = 1f;
            changed = true;
        }

        return changed;
    }

    private void UpdateWeaponHandIKTargets()
    {
        if (!ShouldDriveWeaponHandIK())
        {
            LogIKDebug("Skipping weapon hand IK: ShouldDriveWeaponHandIK() returned false.");
            SetHandIKWeights(0f);
            return;
        }

        if (rightHandIKConstraint == null || leftHandIKConstraint == null || rightHandIKTarget == null || leftHandIKTarget == null)
        {
            LogIKDebug(
                $"Skipping weapon hand IK: missing references " +
                $"rightConstraint={(rightHandIKConstraint != null)} leftConstraint={(leftHandIKConstraint != null)} " +
                $"rightTarget={(rightHandIKTarget != null)} leftTarget={(leftHandIKTarget != null)}");
            return;
        }

        Transform weaponTransform = ResolveActiveWeaponTransformForIK();
        if (weaponTransform == null)
        {
            LogIKDebug("No active weapon transform resolved for IK. Setting hand weights to 0.");
            SetHandIKWeights(0f);
            return;
        }

        if (!loggedWeaponIKTarget)
        {
            loggedWeaponIKTarget = true;
            Debug.Log($"PlayerAimRigDriver: IK weapon target='{weaponTransform.name}'.");
        }

        Transform rightGrip = FindGrip(weaponTransform, rightHandGripName);
        Transform leftGrip = FindGrip(weaponTransform, leftHandGripName);

        if (createFallbackWeaponGripsIfMissing)
        {
            if (rightGrip == null)
            {
                rightGrip = FindOrCreateGrip(weaponTransform, rightHandGripName, rightHandGripFallbackLocalPosition);
            }

            if (leftGrip == null)
            {
                leftGrip = FindOrCreateGrip(weaponTransform, leftHandGripName, leftHandGripFallbackLocalPosition);
            }
        }

        bool hasRightGrip = rightGrip != null;
        bool hasLeftGrip = leftGrip != null;

        LogIKDebug(
            $"Weapon='{weaponTransform.name}' rightGrip={(hasRightGrip ? rightGrip.name : "missing")} " +
            $"leftGrip={(hasLeftGrip ? leftGrip.name : "missing")} " +
            $"fallbackGripCreation={createFallbackWeaponGripsIfMissing}");

        if (hasRightGrip)
        {
            rightHandIKTarget.SetPositionAndRotation(
                ResolveHandTargetPosition(rightGrip.position, true),
                GetGripRotation(rightGrip, weaponTransform));
        }

        if (hasLeftGrip)
        {
            leftHandIKTarget.SetPositionAndRotation(
                ResolveHandTargetPosition(leftGrip.position, false),
                GetGripRotation(leftGrip, weaponTransform));
        }

        UpdateElbowHintTargets();

        UpdateDebugHandle(ref debugRightHandle, hasRightGrip, rightGrip != null ? rightGrip.position : Vector3.zero, debugRightHandColor);
        UpdateDebugHandle(ref debugLeftHandle, hasLeftGrip, leftGrip != null ? leftGrip.position : Vector3.zero, debugLeftHandColor);

        rightHandIKConstraint.weight = useRightHandIK && hasRightGrip ? 1f : 0f;
        leftHandIKConstraint.weight = useLeftHandIK && hasLeftGrip ? 1f : 0f;

        LogIKDebug(
            $"Applied IK weights: right={rightHandIKConstraint.weight:0.00} left={leftHandIKConstraint.weight:0.00} " +
            $"useRight={useRightHandIK} useLeft={useLeftHandIK}");
    }

    private bool ShouldDriveWeaponHandIK()
    {
        if (!enableWeaponHandIK) return false;
        if (!driveWeaponHandIKForOwnerOnly) return true;
        if (!IsSpawned) return true;
        return IsOwner;
    }

    private Transform ResolveActiveWeaponTransformForIK()
    {
        if (weaponManager == null)
        {
            weaponManager = GetComponent<WeaponManager>();
        }

        if (weaponManager == null) return null;

        bool localAuthority = !IsSpawned || IsOwner;
        switch (weaponIKTargetSource)
        {
            case WeaponIKTargetSource.WorldModelOnly:
                if (weaponManager.EquippedWorldModel != null)
                {
                    Transform worldOnly = weaponManager.EquippedWorldModel.transform;
                    if (IsSafeWeaponIKTarget(worldOnly)) return worldOnly;
                }
                return null;
            case WeaponIKTargetSource.ViewModelOnly:
                if (localAuthority && weaponManager.EquippedViewModel != null) return weaponManager.EquippedViewModel.transform;
                return null;
        }

        if (weaponManager.EquippedWorldModel != null)
        {
            Transform worldModel = weaponManager.EquippedWorldModel.transform;
            if (IsSafeWeaponIKTarget(worldModel))
            {
                return worldModel;
            }

            LogIKDebug(
                $"Rejected world-model IK target '{worldModel.name}' because it is under animated hierarchy. " +
                "Falling back to viewmodel/none.");
        }

        if (localAuthority && applyWeaponHandIKForOwnerViewModel && weaponManager.EquippedViewModel != null)
        {
            return weaponManager.EquippedViewModel.transform;
        }

        if (localAuthority && weaponManager.EquippedViewModel != null)
        {
            return weaponManager.EquippedViewModel.transform;
        }

        return null;
    }

    private bool IsSafeWeaponIKTarget(Transform candidate)
    {
        if (candidate == null) return false;
        if (!preventCyclicIKTargets) return true;
        if (animatedHierarchyAnimator == null) return true;
        return !candidate.IsChildOf(animatedHierarchyAnimator.transform);
    }

    private static Transform FindGrip(Transform weaponRoot, string gripName)
    {
        if (weaponRoot == null || string.IsNullOrWhiteSpace(gripName)) return null;
        return FindDescendantByName(weaponRoot, gripName);
    }

    private static Quaternion GetGripRotation(Transform grip, Transform weaponTransform)
    {
        if (grip != null)
        {
            return grip.rotation;
        }

        if (weaponTransform != null)
        {
            return Quaternion.LookRotation(weaponTransform.forward, weaponTransform.up);
        }

        return Quaternion.identity;
    }

    private Vector3 ResolveHandTargetPosition(Vector3 desiredPosition, bool isRightHand)
    {
        if (!keepHandTargetsOutsideCharacterHitbox) return desiredPosition;
        return ResolvePositionOutsideCharacterHitbox(desiredPosition, handTargetHitboxPadding, isRightHand ? transform.right : -transform.right);
    }

    private void UpdateElbowHintTargets()
    {
        if (!keepLeftElbowOutsideCharacterHitbox) return;
        if (leftHandIKHint == null || leftUpperArm == null) return;

        Vector3 desiredPosition = leftUpperArm.TransformPoint(leftElbowHintLocalOffset);
        Vector3 fallbackDirection = (-transform.right + (-transform.forward * 0.35f)).normalized;
        leftHandIKHint.position = ResolvePositionOutsideCharacterHitbox(desiredPosition, leftElbowHitboxPadding, fallbackDirection);
    }

    private Vector3 ResolvePositionOutsideCharacterHitbox(Vector3 desiredPosition, float padding, Vector3 fallbackDirection)
    {
        if (characterController == null)
        {
            characterController = GetComponent<CharacterController>();
        }

        if (characterController == null) return desiredPosition;

        GetCharacterControllerCapsule(characterController, out Vector3 capsuleBottom, out Vector3 capsuleTop, out float capsuleRadius);

        float clearance = capsuleRadius + Mathf.Max(0f, padding);
        Vector3 clampedPosition = desiredPosition;
        Vector3 closestPointOnAxis = ClosestPointOnSegment(capsuleBottom, capsuleTop, clampedPosition);
        Vector3 fromCapsule = clampedPosition - closestPointOnAxis;
        float sqrDistance = fromCapsule.sqrMagnitude;

        Vector3 planarFallbackDirection = fallbackDirection;
        planarFallbackDirection.y = 0f;

        if (planarFallbackDirection.sqrMagnitude <= 0.0001f)
        {
            planarFallbackDirection = desiredPosition - transform.position;
            planarFallbackDirection.y = 0f;
        }

        Vector3 pushDirection = sqrDistance > 0.000001f
            ? fromCapsule.normalized
            : planarFallbackDirection.normalized;

        if (sqrDistance < clearance * clearance)
        {
            clampedPosition = closestPointOnAxis + (pushDirection * clearance);
        }

        return clampedPosition;
    }

    private static void GetCharacterControllerCapsule(
        CharacterController controller,
        out Vector3 capsuleBottom,
        out Vector3 capsuleTop,
        out float capsuleRadius)
    {
        Transform controllerTransform = controller.transform;
        Vector3 lossyScale = controllerTransform.lossyScale;
        float radiusScale = Mathf.Max(Mathf.Abs(lossyScale.x), Mathf.Abs(lossyScale.z));
        float heightScale = Mathf.Abs(lossyScale.y);

        capsuleRadius = controller.radius * radiusScale;
        float scaledHeight = controller.height * heightScale;
        float cylinderHalfHeight = Mathf.Max(0f, (scaledHeight * 0.5f) - capsuleRadius);

        Vector3 worldCenter = controllerTransform.TransformPoint(controller.center);
        Vector3 up = controllerTransform.up;

        capsuleBottom = worldCenter - (up * cylinderHalfHeight);
        capsuleTop = worldCenter + (up * cylinderHalfHeight);
    }

    private static Vector3 ClosestPointOnSegment(Vector3 start, Vector3 end, Vector3 point)
    {
        Vector3 segment = end - start;
        float sqrLength = segment.sqrMagnitude;
        if (sqrLength <= 0.000001f) return start;

        float t = Mathf.Clamp01(Vector3.Dot(point - start, segment) / sqrLength);
        return start + (segment * t);
    }

    private static Transform FindOrCreateGrip(Transform weaponRoot, string gripName, Vector3 localFallbackPosition)
    {
        if (weaponRoot == null || string.IsNullOrWhiteSpace(gripName)) return null;

        Transform grip = FindDescendantByName(weaponRoot, gripName);
        if (grip != null) return grip;

        var gripObject = new GameObject(gripName);
        gripObject.transform.SetParent(weaponRoot, false);
        gripObject.transform.localPosition = localFallbackPosition;
        gripObject.transform.localRotation = Quaternion.identity;
        gripObject.transform.localScale = Vector3.one;
        return gripObject.transform;
    }

    private static Transform FindOrCreateChild(Transform parent, string childName)
    {
        if (parent == null || string.IsNullOrWhiteSpace(childName)) return null;

        Transform existing = FindDescendantByName(parent, childName);
        if (existing != null) return existing;

        var child = new GameObject(childName);
        child.transform.SetParent(parent, false);
        return child.transform;
    }

    private void SetHandIKWeights(float weight)
    {
        if (rightHandIKConstraint != null)
        {
            rightHandIKConstraint.weight = weight;
        }

        if (leftHandIKConstraint != null)
        {
            leftHandIKConstraint.weight = weight;
        }
    }

    private void UpdateDebugHandle(ref GameObject handle, bool active, Vector3 position, Color color)
    {
        if (!debugShowHandTargets)
        {
            if (handle != null && handle.activeSelf)
            {
                handle.SetActive(false);
            }
            return;
        }

        if (handle == null)
        {
            handle = CreateDebugHandle(color);
            if (handle == null) return;
        }

        handle.SetActive(active);
        if (active)
        {
            handle.transform.position = position;
        }
    }

    private GameObject CreateDebugHandle(Color color)
    {
        GameObject debugHandle = GameObject.CreatePrimitive(PrimitiveType.Sphere);
        Collider collider = debugHandle.GetComponent<Collider>();
        if (collider != null)
        {
            Destroy(collider);
        }

        debugHandle.name = "HandIKTarget";
        debugHandle.transform.SetParent(transform, false);
        debugHandle.transform.localScale = Vector3.one * debugTargetRadius;

        Renderer renderer = debugHandle.GetComponent<Renderer>();
        if (renderer != null)
        {
            Shader shader = Shader.Find("Unlit/Color");
            Material mat = shader != null ? new Material(shader) : new Material(Shader.Find("Standard"));
            mat.color = color;
            renderer.material = mat;
        }

        return debugHandle;
    }

    private void RebuildRig()
    {
        if (activeRigBuilder == null) return;

        activeRigBuilder.Clear();
        activeRigBuilder.Build();
        LogIKDebug($"Rig rebuilt on '{activeRigBuilder.gameObject.name}'.", true);
    }

    private void LogIKDebug(string message, bool force = false)
    {
        if (!debugConsoleIK) return;
        if (string.IsNullOrWhiteSpace(message)) return;

        float now = Time.unscaledTime;
        float interval = Mathf.Max(0.05f, debugConsoleIKInterval);
        bool canLogNow = force || now >= nextIKDebugLogTime;
        bool changedMessage = !string.Equals(lastIKDebugMessage, message, StringComparison.Ordinal);
        if (!canLogNow && !changedMessage) return;

        Debug.Log($"PlayerAimRigDriver[{name}]: {message}");
        lastIKDebugMessage = message;
        nextIKDebugLogTime = now + interval;
    }

    private static Transform FindDescendantByName(Transform root, string targetName)
    {
        if (root == null || string.IsNullOrWhiteSpace(targetName)) return null;
        if (string.Equals(root.name, targetName, StringComparison.OrdinalIgnoreCase)) return root;

        for (int i = 0; i < root.childCount; i++)
        {
            Transform found = FindDescendantByName(root.GetChild(i), targetName);
            if (found != null) return found;
        }

        return null;
    }
}
