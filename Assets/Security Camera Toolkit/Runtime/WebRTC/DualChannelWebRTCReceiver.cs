using System;
using System.Collections.Generic;
using System.Collections;
using System.Net;
using Unity.WebRTC;
using UnityEngine;
using UnityEngine.UI;
using WebSocketSharp;
using WebSocketSharp.Server;

namespace SecurityCameraToolkit.Runtime.WebRTC
{
    [DisallowMultipleComponent]
    public class DualChannelWebRTCReceiver : MonoBehaviour
    {
        [Header("Signaling")]
        [SerializeField] int signalingPort = 7001;
        [SerializeField] string signalingPath = "/ws";
        [SerializeField] bool autoStartServer = true;

        [Header("Preview Outputs")]
        [SerializeField] RawImage colorPreview;
        [SerializeField] RawImage alphaPreview;
        [SerializeField] RawImage compositePreview;
        [SerializeField] Material compositeMaterial;
        [SerializeField] bool autoApplyCompositeMaterial = true;

        [Header("Connection")]
        [SerializeField] bool verboseLogging = true;
        [SerializeField] string[] iceServerUrls = new[] { "stun:stun.l.google.com:19302" };

        WebSocketServer _wsServer;
        readonly Dictionary<string, PeerContext> _peers = new();
        PeerContext _activePeer;
        RTCConfiguration _config;

        void Awake()
        {
            _config = new RTCConfiguration
            {
                iceServers = new[] { new RTCIceServer { urls = iceServerUrls } }
            };
        }

        void OnEnable()
        {
            WebRTCUpdatePump.Instance.Retain();
            if (autoStartServer)
            {
                StartServer();
            }
        }

        void OnDisable()
        {
            StopServer();
            WebRTCUpdatePump.Instance.Release();
        }

        public void StartServer()
        {
            if (_wsServer != null)
            {
                if (verboseLogging)
                    Debug.Log("[DualChannelWebRTCReceiver] Signaling server already running.", this);
                return;
            }

            try
            {
                _wsServer = new WebSocketServer(IPAddress.Any, signalingPort);
                _wsServer.AddWebSocketService(signalingPath, () => new ReceiverBehavior(this));
                _wsServer.Start();
                if (verboseLogging)
                {
                    Debug.Log($"[DualChannelWebRTCReceiver] Signaling server listening on ws://0.0.0.0:{signalingPort}{signalingPath}", this);
                }
            }
            catch (Exception ex)
            {
                Debug.LogError($"[DualChannelWebRTCReceiver] Failed to start signaling server: {ex.Message}", this);
                StopServer();
            }
        }

        public void StopServer()
        {
            foreach (var kv in _peers)
            {
                CleanupPeer(kv.Value);
            }
            _peers.Clear();
            _activePeer = null;

            if (_wsServer != null)
            {
                try { _wsServer.Stop(); } catch { }
                _wsServer = null;
            }
        }

        internal void HandleClientMessage(string clientId, ReceiverBehavior behavior, string json)
        {
            if (string.IsNullOrEmpty(json))
                return;

            SignalingMessage message;
            try
            {
                message = SignalingMessage.FromJson(json);
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[DualChannelWebRTCReceiver] Failed to parse signaling JSON: {ex.Message}\n{json}", this);
                return;
            }

            if (message == null)
                return;

            if (verboseLogging)
            {
                Debug.Log($"[DualChannelWebRTCReceiver] <- {message.type} (client {clientId})", this);
            }

            switch (message.type)
            {
                case "offer":
                    if (!string.IsNullOrEmpty(message.sdp))
                        StartCoroutine(HandleOffer(clientId, behavior, message.sdp));
                    break;
                case "ice":
                    if (message.candidate != null && _peers.TryGetValue(clientId, out var ctxIce))
                        ApplyRemoteIce(ctxIce, message.candidate);
                    break;
                case "bye":
                    RemovePeer(clientId);
                    break;
            }
        }

        internal void HandleClientClosed(string clientId)
        {
            if (verboseLogging)
            {
                Debug.Log($"[DualChannelWebRTCReceiver] Client disconnected: {clientId}", this);
            }
            RemovePeer(clientId);
        }

        internal void HandleClientError(string clientId, string message)
        {
            Debug.LogWarning($"[DualChannelWebRTCReceiver] Client error ({clientId}): {message}", this);
        }

        IEnumerator HandleOffer(string clientId, ReceiverBehavior behavior, string sdp)
        {
            var ctx = GetOrCreateContext(clientId, behavior);
            if (ctx.peer != null)
            {
                CleanupPeer(ctx);
            }

            ctx.peer = new RTCPeerConnection(ref _config);
            ctx.peer.OnIceCandidate = cand => SendLocalIce(ctx, cand);
            ctx.peer.OnTrack = e => HandleTrack(ctx, e);
            ctx.peer.OnConnectionStateChange = state =>
            {
                if (verboseLogging)
                {
                    Debug.Log($"[DualChannelWebRTCReceiver] Peer {clientId} state: {state}", this);
                }
                if (state == RTCPeerConnectionState.Failed || state == RTCPeerConnectionState.Disconnected)
                {
                    RemovePeer(clientId);
                }
            };

            var offerDesc = new RTCSessionDescription { type = RTCSdpType.Offer, sdp = sdp };
            var remoteOp = ctx.peer.SetRemoteDescription(ref offerDesc);
            yield return remoteOp;
            if (remoteOp.IsError)
            {
                Debug.LogError($"[DualChannelWebRTCReceiver] SetRemoteDescription failed: {remoteOp.Error.message}", this);
                RemovePeer(clientId);
                yield break;
            }

            var answerOp = ctx.peer.CreateAnswer();
            yield return answerOp;
            if (answerOp.IsError)
            {
                Debug.LogError($"[DualChannelWebRTCReceiver] CreateAnswer failed: {answerOp.Error.message}", this);
                RemovePeer(clientId);
                yield break;
            }

            var answerDesc = answerOp.Desc;
            var localOp = ctx.peer.SetLocalDescription(ref answerDesc);
            yield return localOp;
            if (localOp.IsError)
            {
                Debug.LogError($"[DualChannelWebRTCReceiver] SetLocalDescription failed: {localOp.Error.message}", this);
                RemovePeer(clientId);
                yield break;
            }

            var message = SignalingMessage.CreateAnswer(answerDesc.sdp);
            behavior.SendJson(message.ToJson());
            if (verboseLogging)
            {
                Debug.Log($"[DualChannelWebRTCReceiver] -> answer (client {clientId})", this);
            }
        }

        void HandleTrack(PeerContext ctx, RTCTrackEvent e)
        {
            if (e.Track is not VideoStreamTrack videoTrack)
                return;

            if (ctx.colorTrack == null)
            {
                ctx.colorTrack = videoTrack;
                ctx.colorHandler = tex => OnColorFrame(ctx, tex);
                videoTrack.OnVideoReceived += ctx.colorHandler;
                ctx.colorTexture = videoTrack.Texture;
                PromoteActivePeer(ctx);
                ApplyTexturesToUI(ctx);
            }
            else if (ctx.alphaTrack == null)
            {
                ctx.alphaTrack = videoTrack;
                ctx.alphaHandler = tex => OnAlphaFrame(ctx, tex);
                videoTrack.OnVideoReceived += ctx.alphaHandler;
                ctx.alphaTexture = videoTrack.Texture;
                ApplyTexturesToUI(ctx);
            }
        }

        void PromoteActivePeer(PeerContext ctx)
        {
            _activePeer = ctx;
            if (verboseLogging)
            {
                Debug.Log($"[DualChannelWebRTCReceiver] Active peer set to {ctx.id}", this);
            }
        }

        void OnColorFrame(PeerContext ctx, Texture tex)
        {
            ctx.colorTexture = tex;
            ApplyTexturesToUI(ctx);
        }

        void OnAlphaFrame(PeerContext ctx, Texture tex)
        {
            ctx.alphaTexture = tex;
            ApplyTexturesToUI(ctx);
        }

        void ApplyTexturesToUI(PeerContext ctx)
        {
            if (ctx != _activePeer)
                return;

            if (colorPreview != null && ctx.colorTexture != null)
            {
                colorPreview.texture = ctx.colorTexture;
            }

            if (alphaPreview != null && ctx.alphaTexture != null)
            {
                alphaPreview.texture = ctx.alphaTexture;
            }

            if (compositePreview != null && ctx.colorTexture != null && compositeMaterial != null)
            {
                compositeMaterial.SetTexture("_ColorTex", ctx.colorTexture);
                compositeMaterial.SetTexture("_AlphaTex", ctx.alphaTexture ?? Texture2D.blackTexture);
                compositePreview.texture = ctx.colorTexture;
                compositePreview.material = autoApplyCompositeMaterial ? compositeMaterial : null;
            }
        }

        void SendLocalIce(PeerContext ctx, RTCIceCandidate candidate)
        {
            var message = SignalingMessage.CreateIce(candidate);
            candidate?.Dispose();
            if (message == null)
                return;

            ctx.behavior?.SendJson(message.ToJson());
            if (verboseLogging)
            {
                Debug.Log($"[DualChannelWebRTCReceiver] -> ice (client {ctx.id})", this);
            }
        }

        void ApplyRemoteIce(PeerContext ctx, SignalingMessage.IceCandidatePayload payload)
        {
            if (ctx.peer == null || payload == null)
                return;

            try
            {
                using var candidate = payload.ToCandidate();
                bool added = ctx.peer.AddIceCandidate(candidate);
                if (!added && verboseLogging)
                    Debug.LogWarning($"[DualChannelWebRTCReceiver] Failed to add ICE candidate for {ctx.id}", this);
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[DualChannelWebRTCReceiver] Exception while adding ICE candidate: {ex.Message}", this);
            }
        }

        PeerContext GetOrCreateContext(string clientId, ReceiverBehavior behavior)
        {
            if (_peers.TryGetValue(clientId, out var ctx))
            {
                ctx.behavior = behavior;
                return ctx;
            }

            ctx = new PeerContext
            {
                id = clientId,
                behavior = behavior
            };
            _peers[clientId] = ctx;
            return ctx;
        }

        void RemovePeer(string clientId)
        {
            if (_peers.TryGetValue(clientId, out var ctx))
            {
                CleanupPeer(ctx);
                _peers.Remove(clientId);
            }
        }

        void CleanupPeer(PeerContext ctx)
        {
            if (ctx == null)
                return;

            if (ctx.colorTrack != null && ctx.colorHandler != null)
            {
                ctx.colorTrack.OnVideoReceived -= ctx.colorHandler;
                ctx.colorTrack.Dispose();
            }
            if (ctx.alphaTrack != null && ctx.alphaHandler != null)
            {
                ctx.alphaTrack.OnVideoReceived -= ctx.alphaHandler;
                ctx.alphaTrack.Dispose();
            }

            ctx.peer?.Close();
            ctx.peer?.Dispose();
            ctx.peer = null;
            ctx.colorTrack = null;
            ctx.alphaTrack = null;
            ctx.colorTexture = null;
            ctx.alphaTexture = null;

            if (_activePeer == ctx)
            {
                _activePeer = null;
                if (colorPreview != null) colorPreview.texture = null;
                if (alphaPreview != null) alphaPreview.texture = null;
                if (compositePreview != null)
                {
                    compositePreview.texture = null;
                    if (autoApplyCompositeMaterial)
                        compositePreview.material = null;
                }
            }
        }

        class PeerContext
        {
            public string id;
            public ReceiverBehavior behavior;
            public RTCPeerConnection peer;
            public VideoStreamTrack colorTrack;
            public VideoStreamTrack alphaTrack;
            public Texture colorTexture;
            public Texture alphaTexture;
            public OnVideoReceived  colorHandler;
            public OnVideoReceived  alphaHandler;
        }

        public class ReceiverBehavior : WebSocketBehavior
        {
            readonly DualChannelWebRTCReceiver _owner;

            public ReceiverBehavior(DualChannelWebRTCReceiver owner)
            {
                _owner = owner;
            }

            protected override void OnMessage(MessageEventArgs e)
            {
                if (!e.IsText)
                    return;
                UnityMainThreadDispatcher.Enqueue(() => _owner.HandleClientMessage(ID, this, e.Data));
            }

            protected override void OnClose(CloseEventArgs e)
            {
                UnityMainThreadDispatcher.Enqueue(() => _owner.HandleClientClosed(ID));
            }

            protected override void OnError(ErrorEventArgs e)
            {
                UnityMainThreadDispatcher.Enqueue(() => _owner.HandleClientError(ID, e.Message));
            }

            public void SendJson(string json)
            {
                if (string.IsNullOrEmpty(json))
                    return;
                Send(json);
            }
        }
    }
}
