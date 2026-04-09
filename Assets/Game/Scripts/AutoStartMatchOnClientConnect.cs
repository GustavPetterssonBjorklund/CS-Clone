using Unity.Netcode;
using UnityEngine;
using UnityEngine.SceneManagement;

[RequireComponent(typeof(NetworkManager))]
public class AutoStartMatchOnClientConnect : MonoBehaviour
{
    [SerializeField] private string gameSceneName = "NatureWorld";
    [SerializeField] private bool requireRemoteClient = true;
    [SerializeField, Min(0f)] private float loadDelaySeconds = 0.2f;

    private NetworkManager nm;
    private bool loadQueued;

    private void Awake()
    {
        nm = GetComponent<NetworkManager>();
    }

    private void OnEnable()
    {
        nm ??= GetComponent<NetworkManager>();
        if (nm != null)
        {
            nm.OnClientConnectedCallback += OnClientConnected;
        }
    }

    private void OnDisable()
    {
        CancelInvoke(nameof(LoadMatchScene));
        loadQueued = false;

        if (nm != null)
        {
            nm.OnClientConnectedCallback -= OnClientConnected;
        }
    }

    private void OnClientConnected(ulong clientId)
    {
        if (!Application.isBatchMode) return;
        if (nm == null || !nm.IsServer || loadQueued) return;
        if (string.IsNullOrWhiteSpace(gameSceneName)) return;
        if (requireRemoteClient && clientId == nm.LocalClientId) return;
        if (SceneManager.GetActiveScene().name == gameSceneName) return;

        loadQueued = true;
        Debug.Log($"[PlayFlow] AutoStartMatchOnClientConnect scheduling '{gameSceneName}' due to client {clientId}.");
        Invoke(nameof(LoadMatchScene), loadDelaySeconds);
    }

    private void LoadMatchScene()
    {
        if (nm == null || !nm.IsServer) return;
        if (SceneManager.GetActiveScene().name == gameSceneName) return;
        if (nm.SceneManager == null)
        {
            loadQueued = false;
            Debug.LogWarning("AutoStartMatchOnClientConnect: NetworkSceneManager is unavailable.");
            return;
        }

        var status = nm.SceneManager.LoadScene(gameSceneName, LoadSceneMode.Single);
        if (status != SceneEventProgressStatus.Started)
        {
            loadQueued = false;
            Debug.LogWarning($"AutoStartMatchOnClientConnect: failed to load '{gameSceneName}' ({status}).");
        }
    }
}
