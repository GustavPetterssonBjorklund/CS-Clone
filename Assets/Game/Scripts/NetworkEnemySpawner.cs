using Unity.Netcode;
using UnityEngine;

[DisallowMultipleComponent]
public class NetworkEnemySpawner : MonoBehaviour
{
    [SerializeField] private GameObject enemyPrefab;
    [SerializeField, Min(1)] private int enemyCount = 5;
    [SerializeField, Min(1f)] private float spawnRadius = 18f;
    [SerializeField] private float spawnHeightOffset = 0.1f;
    [SerializeField] private bool spawnOnStart = true;
    [SerializeField] private bool verboseLogs = true;

    private bool spawned;
    private bool loggedWaitingState;

    private void OnEnable()
    {
        BindNetworkCallbacks();
    }

    private void OnDisable()
    {
        UnbindNetworkCallbacks();
    }

    private void Start()
    {
        BindNetworkCallbacks();
        TrySpawnEnemies();
    }

    private void Update()
    {
        if (spawned || !spawnOnStart) return;
        TrySpawnEnemies();
    }

    private void TrySpawnEnemies()
    {
        if (spawned || !spawnOnStart) return;
        if (enemyPrefab == null) return;

        NetworkManager networkManager = NetworkManager.Singleton;
        if (networkManager == null || !networkManager.IsListening || !networkManager.IsServer)
        {
            if (verboseLogs && !loggedWaitingState)
            {
                Debug.Log($"NetworkEnemySpawner: waiting. singleton={(networkManager != null)} listening={(networkManager != null && networkManager.IsListening)} server={(networkManager != null && networkManager.IsServer)} scene='{gameObject.scene.name}'.");
                loggedWaitingState = true;
            }
            return;
        }

        loggedWaitingState = false;
        spawned = true;
        if (verboseLogs)
        {
            Debug.Log($"NetworkEnemySpawner: spawning {enemyCount} enemies in scene '{gameObject.scene.name}'.");
        }

        for (int i = 0; i < enemyCount; i++)
        {
            float angle = (Mathf.PI * 2f * i) / enemyCount;
            Vector3 offset = new Vector3(Mathf.Cos(angle), 0f, Mathf.Sin(angle)) * spawnRadius;
            Vector3 spawnPosition = transform.position + offset;
            spawnPosition.y += spawnHeightOffset;

            GameObject enemyInstance = Instantiate(enemyPrefab, spawnPosition, Quaternion.LookRotation(-offset.normalized, Vector3.up));
            enemyInstance.name = $"Enemy_{i + 1:00}";

            NetworkObject networkObject = enemyInstance.GetComponent<NetworkObject>();
            if (networkObject == null)
            {
                Debug.LogWarning("NetworkEnemySpawner: Enemy prefab is missing a NetworkObject component.");
                Destroy(enemyInstance);
                continue;
            }

            networkObject.Spawn(true);
            if (verboseLogs)
            {
                Debug.Log($"NetworkEnemySpawner: spawned '{enemyInstance.name}' at {spawnPosition}.");
            }
        }
    }

    private void OnServerStarted()
    {
        TrySpawnEnemies();
    }

    private void OnSceneEvent(SceneEvent sceneEvent)
    {
        if (sceneEvent.SceneEventType != SceneEventType.LoadEventCompleted) return;
        TrySpawnEnemies();
    }

    private void BindNetworkCallbacks()
    {
        NetworkManager networkManager = NetworkManager.Singleton;
        if (networkManager == null) return;

        networkManager.OnServerStarted -= OnServerStarted;
        networkManager.OnServerStarted += OnServerStarted;

        if (networkManager.SceneManager != null)
        {
            networkManager.SceneManager.OnSceneEvent -= OnSceneEvent;
            networkManager.SceneManager.OnSceneEvent += OnSceneEvent;
        }
    }

    private void UnbindNetworkCallbacks()
    {
        NetworkManager networkManager = NetworkManager.Singleton;
        if (networkManager == null) return;

        networkManager.OnServerStarted -= OnServerStarted;

        if (networkManager.SceneManager != null)
        {
            networkManager.SceneManager.OnSceneEvent -= OnSceneEvent;
        }
    }
}
