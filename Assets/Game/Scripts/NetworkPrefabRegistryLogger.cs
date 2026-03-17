using System.Collections.Generic;
using System.Text;
using Unity.Netcode;
using UnityEngine;

[RequireComponent(typeof(NetworkManager))]
[DisallowMultipleComponent]
public class NetworkPrefabRegistryLogger : MonoBehaviour
{
    [SerializeField] private bool logOnStart = true;
    [SerializeField] private bool verboseLogs = true;

    private NetworkManager networkManager;
    private bool logged;

    private void Awake()
    {
        networkManager = GetComponent<NetworkManager>();
    }

    private void Start()
    {
        if (!logOnStart) return;
        TryLogRegistry("Start");
    }

    private void OnEnable()
    {
        networkManager ??= GetComponent<NetworkManager>();
        if (networkManager == null) return;
        networkManager.OnServerStarted += OnServerStarted;
        networkManager.OnClientConnectedCallback += OnClientConnected;
    }

    private void OnDisable()
    {
        if (networkManager == null) return;
        networkManager.OnServerStarted -= OnServerStarted;
        networkManager.OnClientConnectedCallback -= OnClientConnected;
    }

    private void OnServerStarted()
    {
        TryLogRegistry("OnServerStarted");
    }

    private void OnClientConnected(ulong clientId)
    {
        if (networkManager == null) return;
        if (clientId != networkManager.LocalClientId) return;
        TryLogRegistry("OnClientConnected");
    }

    private void TryLogRegistry(string source)
    {
        if (!verboseLogs || logged || networkManager == null || networkManager.NetworkConfig == null) return;

        var prefabLists = networkManager.NetworkConfig.Prefabs?.NetworkPrefabsLists;
        if (prefabLists == null || prefabLists.Count == 0)
        {
            Debug.Log($"[PrefabRegistry] {source} no NetworkPrefabsLists configured.");
            logged = true;
            return;
        }

        var seen = new HashSet<uint>();
        var builder = new StringBuilder();
        builder.Append($"[PrefabRegistry] {source} role=");
        builder.Append(networkManager.IsServer ? (networkManager.IsClient ? "Host" : "Server") : (networkManager.IsClient ? "Client" : "Offline"));
        builder.Append(" scene='");
        builder.Append(UnityEngine.SceneManagement.SceneManager.GetActiveScene().name);
        builder.Append("' entries:");

        foreach (NetworkPrefabsList prefabList in prefabLists)
        {
            if (prefabList == null || prefabList.PrefabList == null) continue;

            foreach (NetworkPrefab prefabEntry in prefabList.PrefabList)
            {
                GameObject prefab = prefabEntry?.Prefab;
                if (prefab == null) continue;

                NetworkObject networkObject = prefab.GetComponent<NetworkObject>();
                if (networkObject == null) continue;

                uint hash = prefabEntry.SourcePrefabGlobalObjectIdHash;
                if (hash == 0)
                {
                    hash = networkObject.PrefabIdHash;
                }
                if (!seen.Add(hash)) continue;

                builder.Append(' ');
                builder.Append(prefab.name);
                builder.Append('=');
                builder.Append(hash);
            }
        }

        Debug.Log(builder.ToString());
        logged = true;
    }
}
