using System;
using System.Collections.Generic;
using Unity.Netcode;
using UnityEngine;
using UnityEngine.Animations.Rigging;

[DisallowMultipleComponent]
public class PlayerAimRigDriver : NetworkBehaviour
{
    [Header("Aim Target")]
    [SerializeField] private Transform aimTarget;
    [SerializeField] private Transform aimSource;
    [SerializeField] private float aimDistance = 25f;
    [SerializeField] private bool driveOnlyForOwner = true;
    [SerializeField] private bool createAimTargetIfMissing = true;
    [SerializeField] private string aimTargetName = "AimTarget";
    [SerializeField] private bool driveAimTargetFromCamera = true;

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
    [SerializeField] private bool driveWeaponHandIKForOwnerOnly;
    [SerializeField] private bool applyWeaponHandIKForOwnerViewModel = true;
    [SerializeField] private string rightHandGripName = "RightHandGrip";
    [SerializeField] private string leftHandGripName = "LeftHandGrip";
    [SerializeField] private Vector3 rightHandGripFallbackLocalPosition = new(0.11f, -0.06f, 0.2f);
    [SerializeField] private Vector3 leftHandGripFallbackLocalPosition = new(-0.08f, -0.02f, 0.08f);
    [SerializeField] private Vector3 rightElbowHintLocalOffset = new(0.2f, -0.05f, -0.2f);
    [SerializeField] private Vector3 leftElbowHintLocalOffset = new(-0.2f, -0.05f, -0.2f);

    private RigBuilder activeRigBuilder;
    private Animator animatedHierarchyAnimator;
    private WeaponManager weaponManager;
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

    private void Awake()
    {
        weaponManager = GetComponent<WeaponManager>();
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
            targetPosition = aimSource.position + (aimSource.forward * distance);
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
            rightHandIKHint);

        changed |= EnsureTwoBoneIKConstraint(
            ref leftHandIKConstraint,
            leftConstraintTransform,
            leftUpperArm,
            leftLowerArm,
            leftHand,
            leftHandIKTarget,
            leftHandIKHint);

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
        Transform hintTransform)
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

        if (hintTransform != null && data.hint != hintTransform)
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

        if (Mathf.Abs(data.hintWeight - 1f) > 0.001f)
        {
            data.hintWeight = 1f;
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
            SetHandIKWeights(0f);
            return;
        }

        if (rightHandIKConstraint == null || leftHandIKConstraint == null || rightHandIKTarget == null || leftHandIKTarget == null)
        {
            return;
        }

        Transform weaponTransform = ResolveActiveWeaponTransformForIK();
        if (weaponTransform == null)
        {
            SetHandIKWeights(0f);
            return;
        }

        Transform rightGrip = FindOrCreateGrip(weaponTransform, rightHandGripName, rightHandGripFallbackLocalPosition);
        Transform leftGrip = FindOrCreateGrip(weaponTransform, leftHandGripName, leftHandGripFallbackLocalPosition);

        bool hasRightGrip = rightGrip != null;
        bool hasLeftGrip = leftGrip != null;

        if (hasRightGrip)
        {
            rightHandIKTarget.SetPositionAndRotation(rightGrip.position, rightGrip.rotation);
        }

        if (hasLeftGrip)
        {
            leftHandIKTarget.SetPositionAndRotation(leftGrip.position, leftGrip.rotation);
        }

        rightHandIKConstraint.weight = useRightHandIK && hasRightGrip ? 1f : 0f;
        leftHandIKConstraint.weight = useLeftHandIK && hasLeftGrip ? 1f : 0f;
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

        if (weaponManager.EquippedWorldModel != null)
        {
            return weaponManager.EquippedWorldModel.transform;
        }

        bool localAuthority = !IsSpawned || IsOwner;
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

    private void RebuildRig()
    {
        if (activeRigBuilder == null) return;

        activeRigBuilder.Clear();
        activeRigBuilder.Build();
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
