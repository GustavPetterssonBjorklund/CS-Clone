using Unity.Netcode;
using UnityEngine;
using System.Linq;

public class AutoStartServer : MonoBehaviour
{
    void Start()
    {
        var args = System.Environment.GetCommandLineArgs();
        bool forceServer = args.Contains("-server");

        if (Application.isBatchMode || forceServer)
        {
            Debug.Log("Starting dedicated server...");
            NetworkManager.Singleton.StartServer();
        }
    }
}
