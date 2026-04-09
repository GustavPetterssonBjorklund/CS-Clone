using Unity.Netcode;
using Unity.Netcode.Transports.UTP;
using UnityEngine;
using System.Linq;
using UnityEngine.Rendering;
using UnityEngine.SceneManagement;

public class AutoStartServer : MonoBehaviour
{
    private const string BootstrapSceneName = "Lobby";
    private const string DedicatedServerListenAddress = "0.0.0.0";

    void Start()
    {
        var args = System.Environment.GetCommandLineArgs();
        bool forceServer = args.Contains("-server");
        bool headlessRuntime = IsHeadlessRuntime();

        Debug.Log(
            $"AutoStartServer: startup check " +
            $"scene='{SceneManager.GetActiveScene().name}' " +
            $"isEditor={Application.isEditor} " +
            $"batchMode={Application.isBatchMode} " +
            $"headless={headlessRuntime} " +
            $"graphicsDeviceType={SystemInfo.graphicsDeviceType} " +
            $"forceServerArg={forceServer} " +
            $"unityServerBuild={IsUnityServerBuild()} " +
            $"args=[{string.Join(", ", args)}]");

        if (!(headlessRuntime || forceServer))
        {
            return;
        }

        if (NetworkManager.Singleton == null)
        {
            string activeSceneName = SceneManager.GetActiveScene().name;
            if (!string.IsNullOrWhiteSpace(BootstrapSceneName) && activeSceneName != BootstrapSceneName)
            {
                Debug.LogWarning(
                    $"AutoStartServer: no NetworkManager in scene '{activeSceneName}'. " +
                    $"Loading bootstrap scene '{BootstrapSceneName}'.");
                SceneManager.LoadScene(BootstrapSceneName, LoadSceneMode.Single);
                return;
            }

            Debug.LogWarning(
                $"AutoStartServer: no NetworkManager.Singleton found in active scene '{activeSceneName}', " +
                "skipping server startup.");
            return;
        }

        if (NetworkManager.Singleton.IsListening)
        {
            return;
        }

        ConfigureServerTransport(NetworkManager.Singleton);

        Debug.Log(
            $"Starting dedicated server... " +
            $"(batchMode={Application.isBatchMode}, headless={headlessRuntime}, forced={forceServer})");
        bool started = NetworkManager.Singleton.StartServer();
        Debug.Log($"AutoStartServer: StartServer returned {started}.");
    }

    private static void ConfigureServerTransport(NetworkManager networkManager)
    {
        if (networkManager == null) return;

        if (networkManager.NetworkConfig?.NetworkTransport is not UnityTransport transport)
        {
            Debug.LogWarning("AutoStartServer: active transport is not UnityTransport; skipping listen address override.");
            return;
        }

        string address = transport.ConnectionData.Address;
        ushort port = transport.ConnectionData.Port;
        transport.SetConnectionData(address, port, DedicatedServerListenAddress);

        Debug.Log(
            $"AutoStartServer: configured UnityTransport address='{address}' port={port} " +
            $"listen='{DedicatedServerListenAddress}'.");
    }

    private static bool IsHeadlessRuntime()
    {
#if UNITY_SERVER
        // An active server build profile can define UNITY_SERVER in the editor.
        // Treat editor play mode as a client run unless it was explicitly launched for server use.
        return !Application.isEditor || Application.isBatchMode;
#else
        // Restrict implicit server startup to actual batch-mode launches in non-server builds.
        // Null graphics can occur outside dedicated-server intent and was causing client builds
        // to enter a server-only NetworkManager state before the Lobby Play flow ran.
        return Application.isBatchMode;
#endif
    }

    private static bool IsUnityServerBuild()
    {
#if UNITY_SERVER
        return true;
#else
        return false;
#endif
    }
}
