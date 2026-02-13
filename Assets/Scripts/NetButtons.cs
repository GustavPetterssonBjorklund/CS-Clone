using Unity.Netcode;
using UnityEngine;

public class NetButtons : MonoBehaviour
{
    void OnGUI()
    {
        const int w = 200, h = 40;
        int x = 10, y = 10;

        if (!NetworkManager.Singleton.IsListening)
        {
            if (GUI.Button(new Rect(x, y, w, h), "Start Server")) NetworkManager.Singleton.StartServer();
            y += h + 10;
            if (GUI.Button(new Rect(x, y, w, h), "Start Host")) NetworkManager.Singleton.StartHost();
            y += h + 10;
            if (GUI.Button(new Rect(x, y, w, h), "Start Client")) NetworkManager.Singleton.StartClient();
        }
        else
        {
            GUI.Label(new Rect(x, y, 400, h), $"Mode: " +
                (NetworkManager.Singleton.IsServer ? (NetworkManager.Singleton.IsClient ? "Host" : "Server") : "Client"));
        }
    }
}
