using System;
using UnityEngine;
using WebSocketSharp;
using SecurityCameraToolkit.Runtime.WebRTC;

[DisallowMultipleComponent]
public class LanWebSocketSignaler : MonoBehaviour
{
    [Header("WebSocket Signaling (Sender side)")]
    [Tooltip("ws://<ReceiverIP>:7001/ws")]
    public string wsUrl = "ws://192.168.1.10:7001/ws";

    WebSocket _ws;

    public event Action<string> OnJson;  // 收到对端 JSON
    public event Action OnConnected;
    public event Action OnDisconnected;

    public bool Connected => _ws != null && _ws.IsAlive;

    public bool SetWebSocketUrl(string newUrl)
    {
        if (string.IsNullOrWhiteSpace(newUrl))
        {
            Debug.LogWarning("[Signaler] Ignoring empty wsUrl update", this);
            return Connected;
        }

        newUrl = newUrl.Trim();
        bool wasConnected = Connected;
        if (!string.Equals(wsUrl, newUrl, StringComparison.Ordinal))
        {
            Debug.Log($"[Signaler] Updating wsUrl to {newUrl}", this);
        }
        else if (wasConnected)
        {
            Debug.Log("[Signaler] wsUrl unchanged, forcing reconnection", this);
        }

        wsUrl = newUrl;
        CloseWebSocket();
        return wasConnected;
    }

    public void Disconnect()
    {
        Debug.Log("[Signaler] Disconnect requested", this);
        CloseWebSocket();
    }

    void CloseWebSocket()
    {
        if (_ws == null)
            return;

        try
        {
            _ws.Close();
        }
        catch (Exception ex)
        {
            Debug.LogWarning($"[Signaler] Exception while closing WS: {ex.Message}", this);
        }
        finally
        {
            _ws = null;
        }
    }


    public void Connect()
    {
        if (_ws != null && _ws.IsAlive)
        {
            Debug.Log("[Signaler] WS already connected");
            return;
        }

        Debug.Log($"[Signaler] Connecting to {wsUrl}");
        _ws = new WebSocket(wsUrl);
        if (_ws.IsSecure)
            _ws.SslConfiguration.EnabledSslProtocols = System.Security.Authentication.SslProtocols.None;

        _ws.OnOpen += (_, __) =>
        {
            Debug.Log("[Signaler] WS opened");
            UnityMainThreadDispatcher.Enqueue(() =>
            {
                try { OnConnected?.Invoke(); }
                catch (Exception ex) { Debug.LogException(ex, this); }
            });
        };
        _ws.OnClose += (_, e) =>
        {
            Debug.LogWarning($"[Signaler] WS closed: {e.Reason}");
            UnityMainThreadDispatcher.Enqueue(() =>
            {
                try { OnDisconnected?.Invoke(); }
                catch (Exception ex) { Debug.LogException(ex, this); }
            });
        };
        _ws.OnError += (_, e) => Debug.LogWarning($"[Signaler] WS error: {e.Message}");
        _ws.OnMessage += (_, m) =>
        {
            if (!m.IsText)
            {
                Debug.LogWarning("[Signaler] Ignored non-text WS frame");
                return;
            }

            var summary = DescribePayload(m.Data);
            Debug.Log($"[Signaler] <- {summary}");
            UnityMainThreadDispatcher.Enqueue(() => OnJson?.Invoke(m.Data));
        };
        _ws.ConnectAsync();
    }

    public void SendJson(string json)
    {
        if (_ws != null && _ws.IsAlive)
        {
            var summary = DescribePayload(json);
            Debug.Log($"[Signaler] -> {summary}");
            _ws.Send(json);
        }
        else
        {
            Debug.LogWarning("[Signaler] WS not connected");
        }
    }

    static string DescribePayload(string json)
    {
        if (string.IsNullOrEmpty(json))
            return "(empty payload)";

        try
        {
            var msg = SignalingMessage.FromJson(json);
            if (msg == null)
                return $"unknown message: {Truncate(json)}";

            return msg.type switch
            {
                "ice" => $"ice candidate (len={msg.candidate?.candidate?.Length ?? 0})",
                "offer" => $"offer (sdp {DescribeSdp(msg.sdp)})",
                "answer" => $"answer (sdp {DescribeSdp(msg.sdp)})",
                "bye" => "bye",
                _ => $"{msg.type ?? "unknown"}: {Truncate(json)}"
            };
        }
        catch (Exception ex)
        {
            return $"parse error {ex.GetType().Name}: {Truncate(json)}";
        }
    }

    static string DescribeSdp(string sdp)
    {
        if (string.IsNullOrEmpty(sdp))
            return "empty";
        var lines = sdp.Split('\n');
        foreach (var line in lines)
        {
            var trimmed = line.Trim();
            if (trimmed.StartsWith("m=", StringComparison.Ordinal))
                return trimmed;
        }
        return $"len={sdp.Length}";
    }

    static string Truncate(string value, int max = 60)
    {
        if (string.IsNullOrEmpty(value))
            return string.Empty;
        value = value.Replace('\n', ' ').Replace('\r', ' ');
        return value.Length <= max ? value : value.Substring(0, max) + "...";
    }

    void OnDestroy()
    {
        CloseWebSocket();
    }
}
