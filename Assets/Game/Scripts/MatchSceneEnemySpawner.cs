using Unity.Netcode;
using UnityEngine;

[RequireComponent(typeof(NetworkManager))]
[DisallowMultipleComponent]
public class MatchSceneEnemySpawner : MonoBehaviour
{
    [SerializeField] private string targetSceneName = "NatureWorld";
    [SerializeField] private GameObject enemyPrefab;
    [SerializeField, Min(1)] private int enemyCount = 5;
    [SerializeField] private Vector3 spawnCenter = new(-13.8f, 0.15f, 27.5f);
    [SerializeField, Min(1f)] private float spawnRadius = 12f;
    [SerializeField] private float spawnHeightOffset = 0.15f;
    [SerializeField] private bool verboseLogs = true;

    private NetworkManager networkManager;
    private MatchEventHud matchEventHud;
    private bool spawnedForCurrentMatch;
    private bool loggedWaitingState;
    private bool loggedNonTargetScene;

    private void Awake()
    {
        networkManager = GetComponent<NetworkManager>();
        matchEventHud = GetComponent<MatchEventHud>();
    }

    private void OnEnable()
    {
        networkManager ??= GetComponent<NetworkManager>();
        if (networkManager == null) return;

        networkManager.OnServerStarted += OnServerStarted;
        if (networkManager.SceneManager != null)
        {
            networkManager.SceneManager.OnSceneEvent += OnSceneEvent;
        }
    }

    private void Update()
    {
        if (networkManager == null)
        {
            networkManager = GetComponent<NetworkManager>();
        }

        if (networkManager == null)
        {
            return;
        }

        if (!networkManager.IsServer)
        {
            if (verboseLogs && !loggedWaitingState)
            {
                Debug.Log($"MatchSceneEnemySpawner: waiting for server state. listening={networkManager.IsListening} server={networkManager.IsServer} scene='{UnityEngine.SceneManagement.SceneManager.GetActiveScene().name}'.");
                loggedWaitingState = true;
            }
            return;
        }

        loggedWaitingState = false;
        TrySpawnForActiveScene();
    }

    private void OnDisable()
    {
        if (networkManager == null) return;

        networkManager.OnServerStarted -= OnServerStarted;
        if (networkManager.SceneManager != null)
        {
            networkManager.SceneManager.OnSceneEvent -= OnSceneEvent;
        }
    }

    private void OnServerStarted()
    {
        spawnedForCurrentMatch = false;
        TrySpawnForActiveScene();
    }

    private void OnSceneEvent(SceneEvent sceneEvent)
    {
        if (!networkManager.IsServer) return;

        if (sceneEvent.SceneEventType == SceneEventType.LoadEventCompleted)
        {
            spawnedForCurrentMatch = false;
            TrySpawnForActiveScene(sceneEvent.SceneName);
        }
    }

    private void TrySpawnForActiveScene(string sceneNameOverride = null)
    {
        if (spawnedForCurrentMatch) return;
        if (enemyPrefab == null)
        {
            if (verboseLogs)
            {
                Debug.LogWarning("MatchSceneEnemySpawner: enemyPrefab is null.");
            }
            return;
        }

        if (networkManager == null || !networkManager.IsServer) return;

        string activeSceneName = string.IsNullOrWhiteSpace(sceneNameOverride)
            ? UnityEngine.SceneManagement.SceneManager.GetActiveScene().name
            : sceneNameOverride;

        if (!string.Equals(activeSceneName, targetSceneName, System.StringComparison.Ordinal))
        {
            if (verboseLogs && !loggedNonTargetScene)
            {
                Debug.Log($"MatchSceneEnemySpawner: skipping scene '{activeSceneName}', target is '{targetSceneName}'.");
            }
            loggedNonTargetScene = true;
            return;
        }

        loggedNonTargetScene = false;
        spawnedForCurrentMatch = true;

        if (verboseLogs)
        {
            Debug.Log($"MatchSceneEnemySpawner: spawning {enemyCount} enemies for scene '{activeSceneName}'.");
        }
        matchEventHud?.BroadcastFromServer($"Server: spawning {enemyCount} enemies in {activeSceneName}.");

        for (int i = 0; i < enemyCount; i++)
        {
            float angle = (Mathf.PI * 2f * i) / enemyCount;
            Vector3 offset = new Vector3(Mathf.Cos(angle), 0f, Mathf.Sin(angle)) * spawnRadius;
            Vector3 spawnPosition = spawnCenter + offset + (Vector3.up * spawnHeightOffset);
            Quaternion spawnRotation = offset.sqrMagnitude > 0.001f
                ? Quaternion.LookRotation(-offset.normalized, Vector3.up)
                : Quaternion.identity;

            GameObject enemyInstance = Instantiate(enemyPrefab, spawnPosition, spawnRotation);
            enemyInstance.name = $"Enemy_{i + 1:00}";

            NetworkObject networkObject = enemyInstance.GetComponent<NetworkObject>();
            if (networkObject == null)
            {
                Debug.LogWarning("MatchSceneEnemySpawner: enemy prefab missing NetworkObject.");
                matchEventHud?.BroadcastFromServer("Server: enemy prefab missing NetworkObject.");
                Destroy(enemyInstance);
                continue;
            }

            networkObject.Spawn(true);

            if (verboseLogs)
            {
                Debug.Log($"MatchSceneEnemySpawner: spawned '{enemyInstance.name}' at {spawnPosition}.");
            }
        }

        matchEventHud?.BroadcastFromServer($"Server: finished enemy spawn for {activeSceneName}.");
    }
}
