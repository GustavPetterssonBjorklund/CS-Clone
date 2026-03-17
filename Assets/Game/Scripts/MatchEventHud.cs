using System.Collections.Generic;
using Unity.Collections;
using Unity.Netcode;
using UnityEngine;

[RequireComponent(typeof(NetworkManager))]
[DisallowMultipleComponent]
public class MatchEventHud : MonoBehaviour
{
    private const string MessageChannel = "match-event-hud";
    private const float MessageDuration = 6f;

    [SerializeField] private bool showHud = true;
    [SerializeField] private bool verboseLogs = true;

    private readonly Queue<UiMessage> messages = new();
    private NetworkManager networkManager;

    private struct UiMessage
    {
        public string Text;
        public float ExpireAt;
    }

    private void Awake()
    {
        networkManager = GetComponent<NetworkManager>();
    }

    private void OnEnable()
    {
        networkManager ??= GetComponent<NetworkManager>();
        TryRegisterHandler();
    }

    private void OnDisable()
    {
        if (networkManager?.CustomMessagingManager != null)
        {
            networkManager.CustomMessagingManager.UnregisterNamedMessageHandler(MessageChannel);
        }
    }

    private void Update()
    {
        TryRegisterHandler();

        while (messages.Count > 0 && messages.Peek().ExpireAt <= Time.unscaledTime)
        {
            messages.Dequeue();
        }
    }

    public void BroadcastFromServer(string message)
    {
        if (string.IsNullOrWhiteSpace(message)) return;

        ShowLocal(message);

        if (networkManager == null || !networkManager.IsServer || networkManager.CustomMessagingManager == null)
        {
            return;
        }

        FixedString512Bytes payload = message;
        using FastBufferWriter writer = new(1024, Allocator.Temp);
        writer.WriteValueSafe(payload);
        networkManager.CustomMessagingManager.SendNamedMessageToAll(MessageChannel, writer, NetworkDelivery.Reliable);
    }

    public void ShowLocal(string message)
    {
        if (string.IsNullOrWhiteSpace(message)) return;

        messages.Enqueue(new UiMessage
        {
            Text = message,
            ExpireAt = Time.unscaledTime + MessageDuration
        });

        if (verboseLogs)
        {
            Debug.Log($"MatchEventHud: {message}");
        }
    }

    private void OnNamedMessage(ulong senderClientId, FastBufferReader reader)
    {
        reader.ReadValueSafe(out FixedString512Bytes payload);
        ShowLocal(payload.ToString());
    }

    private void TryRegisterHandler()
    {
        if (networkManager == null || networkManager.CustomMessagingManager == null) return;

        networkManager.CustomMessagingManager.UnregisterNamedMessageHandler(MessageChannel);
        networkManager.CustomMessagingManager.RegisterNamedMessageHandler(MessageChannel, OnNamedMessage);
    }

    private void OnGUI()
    {
        if (!showHud || Application.isBatchMode || messages.Count == 0) return;

        GUI.color = new Color(0f, 0f, 0f, 0.72f);
        GUI.Box(new Rect(16f, 16f, 460f, 28f + (messages.Count * 26f)), GUIContent.none);

        GUI.color = Color.white;
        int index = 0;
        foreach (UiMessage message in messages)
        {
            GUI.Label(new Rect(28f, 26f + (index * 24f), 430f, 22f), message.Text);
            index++;
        }
    }
}
