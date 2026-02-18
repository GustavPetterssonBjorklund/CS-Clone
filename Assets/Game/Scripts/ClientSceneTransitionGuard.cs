using Unity.Netcode;
using UnityEngine;
using UnityEngine.SceneManagement;

[RequireComponent(typeof(NetworkManager))]
public class ClientSceneTransitionGuard : MonoBehaviour
{
    [SerializeField] private bool enforceGuard = false;
    [SerializeField] private string lobbySceneName = "Lobby";
    [SerializeField] private bool verboseLogs = true;

    private NetworkManager nm;

    private void Awake()
    {
        nm = GetComponent<NetworkManager>();
        SceneManager.sceneLoaded += OnSceneLoaded;
    }

    private void OnDestroy()
    {
        SceneManager.sceneLoaded -= OnSceneLoaded;
    }

    private void OnSceneLoaded(Scene scene, LoadSceneMode mode)
    {
        if (!enforceGuard) return;
        if (nm == null) return;
        if (!nm.IsClient || !nm.IsListening) return;

        bool connectedClient = nm.IsConnectedClient;
        if (connectedClient) return;

        if (string.Equals(scene.name, lobbySceneName, System.StringComparison.Ordinal)) return;

        Log($"Blocked unexpected client scene '{scene.name}' while not connected. Returning to '{lobbySceneName}'.");
        nm.Shutdown();
        SceneManager.LoadScene(lobbySceneName, LoadSceneMode.Single);
    }

    private void Log(string msg)
    {
        if (!verboseLogs) return;
        Debug.Log($"[PlayFlow] {msg}");
    }
}
