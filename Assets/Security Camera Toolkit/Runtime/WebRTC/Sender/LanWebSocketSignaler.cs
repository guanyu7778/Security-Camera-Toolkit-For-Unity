using System;
using UnityEngine;
using WebSocketSharp;

[DisallowMultipleComponent]
public class LanWebSocketSignaler : MonoBehaviour
{
    [Header("WebSocket Signaling (Sender side)")]
    [Tooltip("ws://<ReceiverIP>:7001/ws")]
    public string wsUrl = "ws://192.168.1.10:7001/ws";

    WebSocket _ws;

    public event Action<string> OnJson;  // 收到对端 JSON

    public bool Connected => _ws != null && _ws.IsAlive;

    public void Connect()
    {
        if (_ws != null && _ws.IsAlive) return;

        _ws = new WebSocket(wsUrl);
        if(_ws.IsSecure)
            _ws.SslConfiguration.EnabledSslProtocols = System.Security.Authentication.SslProtocols.None;
        _ws.OnOpen += (_, __) => Debug.Log("[Signaler] WS opened");
        _ws.OnClose += (_, e) => Debug.LogWarning($"[Signaler] WS closed: {e.Reason}");
        _ws.OnError += (_, e) => Debug.LogWarning($"[Signaler] WS error: {e.Message}");
        _ws.OnMessage += (_, m) =>
        {
            if (m.IsText)
                UnityMainThreadDispatcher.Enqueue(() => OnJson?.Invoke(m.Data));
        };
        _ws.ConnectAsync();
    }

    public void SendJson(string json)
    {
        if (_ws != null && _ws.IsAlive) _ws.Send(json);
        else Debug.LogWarning("[Signaler] WS not connected");
    }

    void OnDestroy() { try { _ws?.Close(); } catch {} _ws = null; }
}
