using UnityEngine;
using Unity.Netcode;

[DisallowMultipleComponent]
public class WeaponManager : NetworkBehaviour
{
    [Header("Loadout")]
    [SerializeField] private WeaponDefinition[] loadout;
    [SerializeField] private int startingWeaponIndex;

    [Header("Sockets")]
    [SerializeField] private Transform viewModelSocket;
    [SerializeField] private Transform worldModelSocket;

    [Header("References")]
    [SerializeField] private Camera ownerCamera;
    [Header("FPS Viewmodel")]
    [SerializeField] private bool lockViewModelToCamera = true;
    [SerializeField] private Vector3 viewModelPositionOffset = new(0f, -0.3f, 0.6f);
    [SerializeField] private Vector3 viewModelEulerOffset = Vector3.zero;
    [SerializeField] private bool spawnWorldModelForOwner = true;
    [SerializeField] private bool enforceViewModelLockInLateUpdate = true;
    [SerializeField] private bool disableViewModelPhysics = true;

    private int currentWeaponIndex = -1;
    private GameObject equippedViewModel;
    private GameObject equippedWorldModel;
    private Gun equippedGun;
    private bool missingLoadoutWarningLogged;
    private bool missingWeaponManagerConfigLogged;

    public int CurrentWeaponIndex => currentWeaponIndex;
    public GameObject EquippedViewModel => equippedViewModel;
    public GameObject EquippedWorldModel => equippedWorldModel;

    private void Awake()
    {
        AutoAssignReferences();
    }

    private void Start()
    {
        // Allows local/offline testing before Netcode spawn.
        if (!IsSpawned && HasLoadout())
        {
            EquipIndex(startingWeaponIndex);
        }
    }

    private void LateUpdate()
    {
        if (!enforceViewModelLockInLateUpdate) return;
        if (!IsLocalAuthority()) return;
        if (!lockViewModelToCamera || ownerCamera == null) return;
        if (equippedViewModel == null) return;

        if (equippedViewModel.transform.parent != ownerCamera.transform)
        {
            equippedViewModel.transform.SetParent(ownerCamera.transform, false);
        }

        equippedViewModel.transform.localPosition = viewModelPositionOffset;
        equippedViewModel.transform.localRotation = Quaternion.Euler(viewModelEulerOffset);
    }

    public override void OnNetworkSpawn()
    {
        base.OnNetworkSpawn();

        if (HasLoadout())
        {
            EquipIndex(startingWeaponIndex);
        }
    }

    public void FireCurrent()
    {
        if (!CanProcessLocalInput()) return;

        if (equippedGun == null)
        {
            equippedGun = GetComponentInChildren<Gun>(true);
            if (equippedGun != null && ownerCamera != null && equippedGun.fpsCam == null)
            {
                equippedGun.fpsCam = ownerCamera;
            }
        }

        if (equippedGun == null)
        {
            if (!missingWeaponManagerConfigLogged)
            {
                Debug.LogWarning("WeaponManager: No equipped Gun was found.");
                missingWeaponManagerConfigLogged = true;
            }
            return;
        }

        equippedGun.TriggerAttack();
    }

    public void NextWeapon()
    {
        if (!CanProcessLocalInput()) return;
        if (!HasLoadout())
        {
            WarnMissingLoadoutOnce();
            return;
        }

        int nextIndex = currentWeaponIndex < 0
            ? 0
            : (currentWeaponIndex + 1) % loadout.Length;
        EquipIndex(nextIndex);
    }

    public void PreviousWeapon()
    {
        if (!CanProcessLocalInput()) return;
        if (!HasLoadout())
        {
            WarnMissingLoadoutOnce();
            return;
        }

        int previousIndex = currentWeaponIndex < 0
            ? 0
            : (currentWeaponIndex - 1 + loadout.Length) % loadout.Length;
        EquipIndex(previousIndex);
    }

    public bool EquipIndex(int index)
    {
        if (!HasLoadout())
        {
            WarnMissingLoadoutOnce();
            return false;
        }

        int clampedIndex = Mathf.Clamp(index, 0, loadout.Length - 1);
        WeaponDefinition weapon = loadout[clampedIndex];
        if (weapon == null)
        {
            Debug.LogWarning($"WeaponManager: Loadout slot {clampedIndex} has no WeaponDefinition.");
            return false;
        }

        DestroyEquippedInstances();
        currentWeaponIndex = clampedIndex;

        bool isLocalAuthority = IsLocalAuthority();
        bool shouldSpawnViewModel = isLocalAuthority;
        bool shouldSpawnWorldModel = !isLocalAuthority || spawnWorldModelForOwner;
        bool spawnedAny = false;

        if (shouldSpawnViewModel)
        {
            Transform viewSocket = viewModelSocket;
            if (lockViewModelToCamera && ownerCamera != null)
            {
                viewSocket = ownerCamera.transform;
            }

            if (viewSocket == null)
            {
                Debug.LogWarning("WeaponManager: viewModelSocket is not assigned.");
            }
            else if (weapon.viewModelPrefab == null)
            {
                Debug.LogWarning($"WeaponManager: '{weapon.weaponID}' has no viewModelPrefab.");
            }
            else
            {
                equippedViewModel = SpawnWeaponInstance(
                    weapon.viewModelPrefab,
                    viewSocket,
                    isViewModel: true,
                    localPosition: viewModelPositionOffset,
                    localRotation: Quaternion.Euler(viewModelEulerOffset));
                spawnedAny |= equippedViewModel != null;
            }
        }

        if (shouldSpawnWorldModel)
        {
            if (worldModelSocket == null)
            {
                Debug.LogWarning("WeaponManager: worldModelSocket is not assigned.");
            }
            else if (weapon.worldModelPrefab == null)
            {
                Debug.LogWarning($"WeaponManager: '{weapon.weaponID}' has no worldModelPrefab.");
            }
            else
            {
                equippedWorldModel = SpawnWeaponInstance(
                    weapon.worldModelPrefab,
                    worldModelSocket,
                    isViewModel: false,
                    localPosition: Vector3.zero,
                    localRotation: Quaternion.identity);
                spawnedAny |= equippedWorldModel != null;
            }
        }

        if (!spawnedAny)
        {
            return false;
        }

        equippedGun = shouldSpawnViewModel && equippedViewModel != null
            ? equippedViewModel.GetComponentInChildren<Gun>(true)
            : (equippedWorldModel != null ? equippedWorldModel.GetComponentInChildren<Gun>(true) : null);

        if (equippedGun == null && shouldSpawnViewModel && equippedViewModel != null)
        {
            equippedGun = equippedViewModel.AddComponent<Gun>();
        }

        if (equippedGun != null)
        {
            equippedGun.ApplyDefinition(weapon);
            if (shouldSpawnViewModel && ownerCamera != null)
            {
                equippedGun.fpsCam = ownerCamera;
            }
        }

        missingLoadoutWarningLogged = false;
        missingWeaponManagerConfigLogged = false;
        return true;
    }

    private GameObject SpawnWeaponInstance(
        GameObject prefab,
        Transform socket,
        bool isViewModel,
        Vector3 localPosition,
        Quaternion localRotation)
    {
        if (prefab == null || socket == null) return null;

        GameObject instance = Instantiate(prefab, socket);
        instance.transform.localPosition = localPosition;
        instance.transform.localRotation = localRotation;
        // Keep authored prefab scale (e.g. mirrored axes) instead of forcing 1,1,1.

        if (isViewModel && disableViewModelPhysics)
        {
            DisablePhysics(instance);
        }

        return instance;
    }

    private void DestroyEquippedInstances()
    {
        if (equippedViewModel != null)
        {
            Destroy(equippedViewModel);
            equippedViewModel = null;
        }

        if (equippedWorldModel != null)
        {
            Destroy(equippedWorldModel);
            equippedWorldModel = null;
        }

        equippedGun = null;
    }

    private bool CanProcessLocalInput()
    {
        if (!IsSpawned) return true;
        return IsOwner;
    }

    private bool IsLocalAuthority()
    {
        if (!IsSpawned) return true;
        return IsOwner;
    }

    private bool HasLoadout()
    {
        return loadout != null && loadout.Length > 0;
    }

    private void WarnMissingLoadoutOnce()
    {
        if (missingLoadoutWarningLogged) return;
        missingLoadoutWarningLogged = true;
        Debug.LogWarning("WeaponManager: Loadout is empty. Add WeaponDefinition assets in the inspector.");
    }

    private void AutoAssignReferences()
    {
        if (viewModelSocket == null)
        {
            viewModelSocket = FindDescendantByName(transform, "WeaponVM_Socket");
        }

        if (worldModelSocket == null)
        {
            worldModelSocket = FindDescendantByName(transform, "GunSocket");
        }

        if (worldModelSocket == null)
        {
            worldModelSocket = FindDescendantByName(transform, "WorldModelRoot");
        }

        if (ownerCamera == null)
        {
            Transform playerCameraTransform = FindDescendantByName(transform, "PlayerCamera");
            if (playerCameraTransform != null)
            {
                ownerCamera = playerCameraTransform.GetComponent<Camera>();
            }
        }

        if (ownerCamera == null)
        {
            ownerCamera = GetComponentInChildren<Camera>(true);
        }
    }

    private static Transform FindDescendantByName(Transform root, string targetName)
    {
        if (root == null || string.IsNullOrWhiteSpace(targetName)) return null;
        if (root.name == targetName) return root;

        for (int i = 0; i < root.childCount; i++)
        {
            Transform found = FindDescendantByName(root.GetChild(i), targetName);
            if (found != null) return found;
        }

        return null;
    }

    private static void DisablePhysics(GameObject root)
    {
        if (root == null) return;

        Rigidbody[] rigidbodies = root.GetComponentsInChildren<Rigidbody>(true);
        for (int i = 0; i < rigidbodies.Length; i++)
        {
            Rigidbody body = rigidbodies[i];
            if (body == null) continue;
            body.isKinematic = true;
            body.detectCollisions = false;
        }

        Collider[] colliders = root.GetComponentsInChildren<Collider>(true);
        for (int i = 0; i < colliders.Length; i++)
        {
            Collider collider = colliders[i];
            if (collider == null) continue;
            collider.enabled = false;
        }
    }
}
