using Unity.Netcode;
using UnityEngine;

public class NetButtons : MonoBehaviour
{
    private void OnGUI()
    {
        NetworkManager networkManager = NetworkManager.Singleton;
        if (networkManager == null)
        {
            GUI.Label(new Rect(10, 10, 300, 40), "No NetworkManager in scene.");
            return;
        }

        const int w = 200, h = 40;
        int x = 10, y = 10;

        if (!networkManager.IsListening)
        {
            if (GUI.Button(new Rect(x, y, w, h), "Start Server")) networkManager.StartServer();
            y += h + 10;
            if (GUI.Button(new Rect(x, y, w, h), "Start Host")) networkManager.StartHost();
            y += h + 10;
            if (GUI.Button(new Rect(x, y, w, h), "Start Client")) networkManager.StartClient();
        }
        else
        {
            GUI.Label(new Rect(x, y, 400, h), $"Mode: " +
                (networkManager.IsServer ? (networkManager.IsClient ? "Host" : "Server") : "Client"));
        }
    }
}
