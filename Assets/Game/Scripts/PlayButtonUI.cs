using UnityEngine;
using UnityEngine.UIElements;
using Unity.Netcode;
using Unity.Netcode.Transports.UTP;
using System.Collections;
using UnityEngine.SceneManagement;

public class PlayButtonUI : MonoBehaviour
{
    private const string DefaultServerAddress = "pettersson.online";

    [SerializeField] private UIDocument uiDocument;
    [SerializeField] private bool overrideServerAddress;
    [SerializeField] private string serverAddress = DefaultServerAddress;
    [SerializeField] private ushort serverPort = 7777;
    [SerializeField, Min(1f)] private float connectTimeoutSeconds = 10f;
    [SerializeField] private bool verboseLogs = true;

    private Label statusLabel;
    private Button playButton;
    private Button cancelButton;
    private Button playNowButton;
    private NetworkManager nm;
    private bool callbacksBound;
    private Coroutine connectTimeoutRoutine;
    private bool localClientConnected;
    private bool matchSceneLoaded;
    private string originSceneName;

    private void Start()
    {
        if (Application.isBatchMode)
        {
            enabled = false;
            return;
        }

        if (uiDocument == null)
        {
            Debug.LogWarning("PlayButtonUI: UIDocument not assigned");
            return;
        }

        var root = uiDocument.rootVisualElement;
        statusLabel = root.Q<Label>("statusLabel");
        playButton = root.Q<Button>("playButton") ?? root.Q<Button>("Play");
        cancelButton = root.Q<Button>("cancelButton");
        playNowButton = root.Q<Button>("playNowButton");

        if (playButton == null)
        {
            Debug.LogWarning("PlayButtonUI: Missing play button in UXML (expected 'playButton' or 'Play').");
            return;
        }

        playButton.clicked += OnPlayClicked;
        if (cancelButton != null)
        {
            cancelButton.clicked += OnCancelClicked;
        }
        if (playNowButton != null)
        {
            playNowButton.clicked += OnPlayNowClicked;
            playNowButton.AddToClassList("hidden");
        }

        nm = NetworkManager.Singleton;
        BindCallbacksIfNeeded();
        SceneManager.sceneLoaded += OnSceneLoaded;
        LogFlow($"Start on scene '{SceneManager.GetActiveScene().name}'.");

        SetStateReady();
    }

    private void OnDestroy()
    {
        StopConnectTimeout();
        SceneManager.sceneLoaded -= OnSceneLoaded;
        if (playButton != null) playButton.clicked -= OnPlayClicked;
        if (cancelButton != null) cancelButton.clicked -= OnCancelClicked;
        if (playNowButton != null) playNowButton.clicked -= OnPlayNowClicked;

        if (callbacksBound && nm != null)
        {
            nm.OnClientConnectedCallback -= OnClientConnected;
            nm.OnClientDisconnectCallback -= OnClientDisconnected;
            if (nm.SceneManager != null)
            {
                nm.SceneManager.OnSceneEvent -= OnNetworkSceneEvent;
            }
        }
    }

    private void SetStateReady(string status = "Ready")
    {
        StopConnectTimeout();
        SetStatus(status);
        playButton.EnableInClassList("hidden", false);
        playButton.SetEnabled(true);
        if (cancelButton != null)
        {
            cancelButton.EnableInClassList("hidden", true);
            cancelButton.text = "Cancel";
        }
        if (playNowButton != null) playNowButton.EnableInClassList("hidden", true);
    }

    private void SetStateConnecting()
    {
        SetStatus("Connecting to server...");
        if (cancelButton == null)
        {
            playButton.SetEnabled(false);
        }
        else
        {
            playButton.EnableInClassList("hidden", true);
            cancelButton.EnableInClassList("hidden", false);
        }
        if (playNowButton != null) playNowButton.EnableInClassList("hidden", true);
    }

    private void SetStateConnected()
    {
        SetStatus("Connected. Joining match...");
        if (cancelButton == null)
        {
            playButton.SetEnabled(false);
        }
        else
        {
            playButton.EnableInClassList("hidden", true);
            cancelButton.EnableInClassList("hidden", false);
            cancelButton.text = "Leave";
        }
    }

    private void OnPlayClicked()
    {
        nm ??= NetworkManager.Singleton;
        BindCallbacksIfNeeded();
        localClientConnected = false;
        matchSceneLoaded = false;
        originSceneName = SceneManager.GetActiveScene().name;
        LogFlow($"Play clicked from '{originSceneName}'.");
        if (nm == null)
        {
            SetStatus("No NetworkManager in scene.");
            return;
        }

        if (nm.IsListening && nm.IsClient)
        {
            SetStatus("Already connected.");
            SetStateConnected();
            return;
        }

        if (nm.IsListening && nm.IsServer && !nm.IsClient)
        {
            SetStatus("This instance is server-only.");
            return;
        }

        ConfigureTransportForClient(nm);
        SetStateConnecting();
        LogFlow(
            $"Calling StartClient. IsListening={nm.IsListening} IsServer={nm.IsServer} IsClient={nm.IsClient} " +
            $"Address={GetConfiguredAddress()} Port={serverPort}");

        bool started = nm.StartClient();
        LogFlow($"StartClient returned {started}.");
        if (!started)
        {
            SetStateReady("Failed to start client.");
            return;
        }

        StopConnectTimeout();
        connectTimeoutRoutine = StartCoroutine(ConnectTimeoutWatchdog());
    }

    private void ConfigureTransportForClient(NetworkManager manager)
    {
        if (!overrideServerAddress) return;

        if (manager.NetworkConfig.NetworkTransport is UnityTransport utp)
        {
            string address = GetConfiguredAddress();
            utp.SetConnectionData(address, serverPort);
            LogFlow($"Configured UnityTransport to {address}:{serverPort} (override={overrideServerAddress}).");
        }
        else
        {
            Debug.LogWarning("PlayButtonUI: Transport is not UnityTransport, cannot override address/port.");
        }
    }

    private void OnCancelClicked()
    {
        nm ??= NetworkManager.Singleton;
        if (nm != null && nm.IsListening)
        {
            nm.Shutdown();
        }

        SetStateReady();
    }

    private void OnPlayNowClicked()
    {
        nm ??= NetworkManager.Singleton;
        if (nm == null)
        {
            SetStatus("No NetworkManager in scene.");
            return;
        }

        if (!nm.IsListening)
        {
            nm.StartHost();
            SetStatus("Host started.");
        }
    }

    private void OnClientConnected(ulong clientId)
    {
        LogFlow($"OnClientConnected clientId={clientId} localClientId={(nm != null ? nm.LocalClientId : 0)}.");
        if (nm == null || clientId != nm.LocalClientId) return;
        localClientConnected = true;
        SetStatus("Connected. Waiting for match start...");
    }

    private void OnClientDisconnected(ulong clientId)
    {
        LogFlow(
            $"OnClientDisconnected clientId={clientId} localClientId={(nm != null ? nm.LocalClientId : 0)} " +
            $"reason='{(nm != null ? nm.DisconnectReason : "<nm-null>")}'.");
        if (nm == null || clientId != nm.LocalClientId) return;
        localClientConnected = false;
        StopConnectTimeout();

        string reason = string.IsNullOrWhiteSpace(nm.DisconnectReason)
            ? "Disconnected."
            : $"Disconnected: {nm.DisconnectReason}";
        SetStateReady(reason);
    }

    private void BindCallbacksIfNeeded()
    {
        if (callbacksBound || nm == null) return;

        nm.OnClientConnectedCallback += OnClientConnected;
        nm.OnClientDisconnectCallback += OnClientDisconnected;
        if (nm.SceneManager != null)
        {
            nm.SceneManager.OnSceneEvent += OnNetworkSceneEvent;
        }
        LogFlow("Bound NGO callbacks.");
        callbacksBound = true;
    }

    private void SetStatus(string text)
    {
        if (statusLabel != null)
        {
            statusLabel.text = text;
        }
        else
        {
            Debug.Log($"PlayButtonUI: {text}");
        }
    }

    private IEnumerator ConnectTimeoutWatchdog()
    {
        float elapsed = 0f;
        while (elapsed < connectTimeoutSeconds)
        {
            if (matchSceneLoaded) yield break;
            elapsed += Time.unscaledDeltaTime;
            yield return null;
        }

        if (nm != null && nm.IsListening && nm.IsClient)
        {
            LogFlow("Connect timeout reached. Shutting down client.");
            nm.Shutdown();
        }
        SetStateReady("Failed to join a running match.");
    }

    private void StopConnectTimeout()
    {
        if (connectTimeoutRoutine == null) return;
        StopCoroutine(connectTimeoutRoutine);
        connectTimeoutRoutine = null;
    }

    private void OnSceneLoaded(Scene scene, LoadSceneMode mode)
    {
        LogFlow(
            $"Unity sceneLoaded -> '{scene.name}' mode={mode} " +
            $"localClientConnected={localClientConnected} origin='{originSceneName}'.");
        if (!localClientConnected) return;
        if (string.Equals(scene.name, originSceneName, System.StringComparison.Ordinal)) return;

        matchSceneLoaded = true;
        StopConnectTimeout();
        SetStateConnected();
    }

    private void OnNetworkSceneEvent(SceneEvent sceneEvent)
    {
        LogFlow(
            $"NGO SceneEvent type={sceneEvent.SceneEventType} scene='{sceneEvent.SceneName}' " +
            $"clientId={sceneEvent.ClientId}");
    }

    private string GetConfiguredAddress()
    {
        if (overrideServerAddress)
        {
            if (!string.Equals(serverAddress, DefaultServerAddress, System.StringComparison.Ordinal))
            {
                LogFlow($"Overriding serialized server address '{serverAddress}' with '{DefaultServerAddress}'.");
                serverAddress = DefaultServerAddress;
            }

            return DefaultServerAddress;
        }

        return string.IsNullOrWhiteSpace(serverAddress) ? "127.0.0.1" : serverAddress.Trim();
    }

    private void LogFlow(string message)
    {
        if (!verboseLogs) return;
        Debug.Log($"[PlayFlow] {message}");
    }
}
