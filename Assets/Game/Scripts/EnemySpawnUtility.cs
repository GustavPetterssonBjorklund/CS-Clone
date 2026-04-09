using Unity.Netcode;
using UnityEngine;

public static class EnemySpawnUtility
{
    public readonly struct SpawnSummary
    {
        public SpawnSummary(int requestedCount, int spawnedCount, bool missingNetworkObject)
        {
            RequestedCount = requestedCount;
            SpawnedCount = spawnedCount;
            MissingNetworkObject = missingNetworkObject;
        }

        public int RequestedCount { get; }
        public int SpawnedCount { get; }
        public bool MissingNetworkObject { get; }
    }

    public static SpawnSummary SpawnEnemyRing(
        GameObject enemyPrefab,
        int enemyCount,
        Vector3 spawnCenter,
        float spawnRadius,
        float spawnHeightOffset,
        string logPrefix,
        bool verboseLogs)
    {
        if (enemyPrefab == null)
        {
            if (verboseLogs)
            {
                Debug.LogWarning($"{logPrefix}: enemyPrefab is null.");
            }

            return new SpawnSummary(enemyCount, 0, false);
        }

        if (enemyPrefab.GetComponent<NetworkObject>() == null)
        {
            Debug.LogWarning($"{logPrefix}: enemy prefab missing NetworkObject.");
            return new SpawnSummary(enemyCount, 0, true);
        }

        int spawnedCount = 0;
        for (int i = 0; i < enemyCount; i++)
        {
            Vector3 offset = GetSpawnOffset(i, enemyCount, spawnRadius);
            Vector3 spawnPosition = spawnCenter + offset + (Vector3.up * spawnHeightOffset);
            Quaternion spawnRotation = GetSpawnRotation(offset);

            GameObject enemyInstance = Object.Instantiate(enemyPrefab, spawnPosition, spawnRotation);
            enemyInstance.name = $"Enemy_{i + 1:00}";

            NetworkObject networkObject = enemyInstance.GetComponent<NetworkObject>();
            networkObject.Spawn(true);
            spawnedCount++;

            if (verboseLogs)
            {
                Debug.Log($"{logPrefix}: spawned '{enemyInstance.name}' at {spawnPosition}.");
            }
        }

        return new SpawnSummary(enemyCount, spawnedCount, false);
    }

    private static Vector3 GetSpawnOffset(int index, int enemyCount, float spawnRadius)
    {
        float angle = (Mathf.PI * 2f * index) / Mathf.Max(1, enemyCount);
        return new Vector3(Mathf.Cos(angle), 0f, Mathf.Sin(angle)) * spawnRadius;
    }

    private static Quaternion GetSpawnRotation(Vector3 offset)
    {
        return offset.sqrMagnitude > 0.001f
            ? Quaternion.LookRotation(-offset.normalized, Vector3.up)
            : Quaternion.identity;
    }
}
