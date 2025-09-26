using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
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
        const string PoseDataChannelLabel = "pose";

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

        [Header("Pose Synchronization")]
        [SerializeField] bool enablePoseBroadcast = true;
        [Tooltip("Path to the JSON pose configuration file relative to StreamingAssets.")]
        [SerializeField] string poseConfigPath = "Configurations/camera_pose.json";
        [Tooltip("Seconds between pose updates sent over the data channel.")]
        [SerializeField] float poseSendIntervalSeconds = 2f;

        WebSocketServer _wsServer;
        readonly Dictionary<string, PeerContext> _peers = new Dictionary<string, PeerContext>();
        PeerContext _activePeer;
        RTCConfiguration _config;
        bool _poseConfigMissingLogged;
        bool _poseConfigParseLogged;
        bool _poseConfigInvalidLogged;

        Rect _colorPreviewDefaultUV = new Rect(0f, 0f, 1f, 1f);
        Rect _alphaPreviewDefaultUV = new Rect(0f, 0f, 1f, 1f);
        bool _compositePackedWarningLogged;

        void Awake()
        {
            _config = new RTCConfiguration
            {
                iceServers = BuildIceServers(iceServerUrls)
            };

            if (colorPreview != null)
                _colorPreviewDefaultUV = colorPreview.uvRect;
            if (alphaPreview != null)
                _alphaPreviewDefaultUV = alphaPreview.uvRect;
        }

        static RTCIceServer[] BuildIceServers(string[] urls)
        {
            if (urls == null || urls.Length == 0)
                return Array.Empty<RTCIceServer>();

            var valid = new System.Collections.Generic.List<string>();
            foreach (var url in urls)
            {
                if (string.IsNullOrWhiteSpace(url))
                    continue;
                valid.Add(url.Trim());
            }

            if (valid.Count == 0)
                return Array.Empty<RTCIceServer>();

            return new[] { new RTCIceServer { urls = valid.ToArray() } };
        }

        void OnEnable()
        {
            LogVerbose("Receiver enabled");
            WebRTCUpdatePump.Instance.Retain();
            if (autoStartServer)
            {
                StartServer();
            }
        }

        void OnDisable()
        {
            LogVerbose("Receiver disabled");
            StopServer();
            WebRTCUpdatePump.Instance.Release();
        }

        public void StartServer()
        {
            if (_wsServer != null)
            {
                LogVerbose("Signaling server already running");
                return;
            }

            try
            {
                _wsServer = new WebSocketServer(IPAddress.Any, signalingPort);
                _wsServer.AddWebSocketService(signalingPath, () => new ReceiverBehavior(this));
                _wsServer.Start();
                LogVerbose($"Signaling server listening on ws://0.0.0.0:{signalingPort}{signalingPath}");
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
                try { _wsServer.Stop(); }
                catch { }
                _wsServer = null;
                LogVerbose("Signaling server stopped");
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

            LogVerbose($"<- {clientId}: {message.type}");

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
            LogVerbose($"Client disconnected: {clientId}");
            RemovePeer(clientId);
        }

        internal void HandleClientError(string clientId, string message)
        {
            Debug.LogWarning($"[DualChannelWebRTCReceiver] Client error ({clientId}): {message}", this);
        }

        IEnumerator HandleOffer(string clientId, ReceiverBehavior behavior, string sdp)
        {
            LogVerbose($"Processing offer from {clientId}");
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
                LogVerbose($"Peer {clientId} connection state: {state}");
                if (state == RTCPeerConnectionState.Failed || state == RTCPeerConnectionState.Disconnected)
                {
                    RemovePeer(clientId);
                }
            };

            ctx.peer.OnDataChannel = channel =>
            {
                var captured = channel;
                UnityMainThreadDispatcher.Enqueue(() => HandleDataChannel(ctx, captured));
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

            LogVerbose($"Creating answer for {clientId}");
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

            LogVerbose($"-> {clientId}: answer");
            var message = SignalingMessage.CreateAnswer(answerDesc.sdp);
            behavior.SendJson(message.ToJson());
        }

        private void HandleTrack(PeerContext ctx, RTCTrackEvent e)
        {
            if (!(e.Track is VideoStreamTrack videoTrack))
                return;

            var trackTexture = videoTrack.Texture;
            string texInfo = trackTexture != null ? $"{trackTexture.width}x{trackTexture.height}" : "null";
            LogVerbose($"Peer {ctx.id} received track kind={videoTrack.Kind} texture={texInfo}");

            if (ctx.packedTrack != null)
            {
                LogVerbose($"Peer {ctx.id} received extra track (ignored)");
                return;
            }

            ctx.packedTrack = videoTrack;
            ctx.packedHandler = tex => OnPackedFrame(ctx, tex);
            videoTrack.OnVideoReceived += ctx.packedHandler;
            ctx.packedTexture = videoTrack.Texture;
            PromoteActivePeer(ctx);
            ApplyTexturesToUI(ctx);
        }

        private void PromoteActivePeer(PeerContext ctx)
        {
            _activePeer = ctx;
            LogVerbose($"Active peer set to {ctx.id}");
        }

        private void OnPackedFrame(PeerContext ctx, Texture tex)
        {
            ctx.packedTexture = tex;
            if (!ctx.loggedFirstFrame)
            {
                LogVerbose($"First packed frame from {ctx.id}");
                ctx.loggedFirstFrame = true;
            }
            ApplyTexturesToUI(ctx);
        }

        private void ApplyTexturesToUI(PeerContext ctx)
        {
            if (ctx != _activePeer)
                return;

            if (ctx.packedTexture == null)
                return;

            if (colorPreview != null)
            {
                colorPreview.texture = ctx.packedTexture;
                colorPreview.uvRect = new Rect(0f, 0.5f, 1f, 0.5f);
            }

            if (alphaPreview != null)
            {
                alphaPreview.texture = ctx.packedTexture;
                alphaPreview.uvRect = new Rect(0f, 0f, 1f, 0.5f);
            }

            if (compositePreview != null)
            {
                bool applied = false;
                if (compositeMaterial != null)
                {
                    if (compositeMaterial.HasProperty("_PackedTex"))
                    {
                        compositeMaterial.SetTexture("_PackedTex", ctx.packedTexture);
                        applied = true;
                    }
                    else if (!_compositePackedWarningLogged)
                    {
                        Debug.LogWarning("[DualChannelWebRTCReceiver] Composite material is missing the _PackedTex property. Assign the new 'CompositePackedColorAlpha' shader or disable auto-apply.", this);
                        _compositePackedWarningLogged = true;
                    }
                }

                if (applied)
                {
                    _compositePackedWarningLogged = false;
                }

                if (autoApplyCompositeMaterial && compositeMaterial != null && applied)
                {
                    compositePreview.material = compositeMaterial;
                }
                else if (!autoApplyCompositeMaterial || compositeMaterial == null || !applied)
                {
                    compositePreview.material = null;
                }

                compositePreview.texture = ctx.packedTexture;
            }
        }

        private void HandleDataChannel(PeerContext ctx, RTCDataChannel channel)
        {
            if (ctx == null || channel == null)
                return;

            if (channel.Label != PoseDataChannelLabel)
            {
                LogVerbose($"Peer {ctx.id} offered unsupported data channel '{channel.Label}' (ignored)");
                return;
            }

            if (!enablePoseBroadcast)
            {
                LogVerbose("Pose broadcast disabled; rejecting data channel");
                try { channel.Close(); }
                catch (Exception) { }
                channel.Dispose();
                return;
            }

            AttachPoseChannel(ctx, channel);
        }

        void AttachPoseChannel(PeerContext ctx, RTCDataChannel channel)
        {
            if (ctx == null || channel == null)
                return;

            if (ctx.poseChannel == channel)
                return;

            if (ctx.poseChannel != null)
            {
                StopPoseRoutine(ctx);
                DisposePoseChannel(ctx, true);
            }

            ctx.poseChannel = channel;
            var captured = channel;
            ctx.poseChannel.OnOpen = () => UnityMainThreadDispatcher.Enqueue(() => OnPoseChannelOpen(ctx, captured));
            ctx.poseChannel.OnClose = () => UnityMainThreadDispatcher.Enqueue(() => OnPoseChannelClosed(ctx, captured));
            ctx.poseChannel.OnMessage = _ => { };

            if (ctx.poseChannel.ReadyState == RTCDataChannelState.Open)
            {
                OnPoseChannelOpen(ctx, captured);
            }
        }

        void OnPoseChannelOpen(PeerContext ctx, RTCDataChannel channel)
        {
            if (ctx.poseChannel != channel)
                return;

            StopPoseRoutine(ctx);
            ctx.poseRoutine = StartCoroutine(PoseSendLoop(ctx));
            LogVerbose($"Pose channel open for peer {ctx.id}");
        }

        void OnPoseChannelClosed(PeerContext ctx, RTCDataChannel channel)
        {
            if (ctx.poseChannel != channel)
                return;

            StopPoseRoutine(ctx);
            DisposePoseChannel(ctx);
            LogVerbose($"Pose channel closed for peer {ctx.id}");
        }

        void StopPoseRoutine(PeerContext ctx)
        {
            if (ctx.poseRoutine != null)
            {
                StopCoroutine(ctx.poseRoutine);
                ctx.poseRoutine = null;
            }
        }

        void DisposePoseChannel(PeerContext ctx, bool requestClose = false)
        {
            var channel = ctx.poseChannel;
            if (channel == null)
                return;

            ctx.poseChannel = null;
            channel.OnOpen = null;
            channel.OnClose = null;
            channel.OnMessage = null;

            if (requestClose)
            {
                try { channel.Close(); }
                catch (Exception) { }
            }

            channel.Dispose();
        }

        IEnumerator PoseSendLoop(PeerContext ctx)
        {
            var interval = Mathf.Max(0.1f, poseSendIntervalSeconds);
            var wait = new WaitForSeconds(interval);

            while (enablePoseBroadcast && ctx.poseChannel != null && ctx.poseChannel.ReadyState == RTCDataChannelState.Open)
            {
                SendPoseOnce(ctx);
                yield return wait;
            }

            ctx.poseRoutine = null;
        }

        void SendPoseOnce(PeerContext ctx)
        {
            if (ctx.poseChannel == null || ctx.poseChannel.ReadyState != RTCDataChannelState.Open)
                return;

            if (!TryLoadPose(out _, out var json))
                return;

            try
            {
                ctx.poseChannel.Send(json);
                if (verboseLogging)
                {
                    LogVerbose($"Sent pose update to peer {ctx.id}");
                }
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[DualChannelWebRTCReceiver] Failed to send pose update: {ex.Message}", this);
            }
        }

        string ResolvePoseConfigPath()
        {
            if (string.IsNullOrWhiteSpace(poseConfigPath))
                return null;

            var trimmed = poseConfigPath.Trim();
            if (Path.IsPathRooted(trimmed))
                return trimmed;

            var basePath = !string.IsNullOrEmpty(Application.streamingAssetsPath) ? Application.streamingAssetsPath : Application.dataPath;
            return Path.Combine(basePath, trimmed);
        }

        bool TryLoadPose(out CameraPoseMessage payload, out string json)
        {
            payload = null;
            json = null;

            if (!enablePoseBroadcast)
                return false;

            var resolvedPath = ResolvePoseConfigPath();

            if (string.IsNullOrEmpty(resolvedPath))
            {
                if (!_poseConfigMissingLogged)
                {
                    Debug.LogWarning("[DualChannelWebRTCReceiver] Pose config path could not be resolved.", this);
                    _poseConfigMissingLogged = true;
                }
                return false;
            }

            if (!File.Exists(resolvedPath))
            {
                if (!_poseConfigMissingLogged)
                {
                    Debug.LogWarning($"[DualChannelWebRTCReceiver] Pose config file not found at '{resolvedPath}'.", this);
                    _poseConfigMissingLogged = true;
                }
                return false;
            }

            _poseConfigMissingLogged = false;

            try
            {
                json = File.ReadAllText(resolvedPath, Encoding.UTF8);
            }
            catch (Exception ex)
            {
                if (!_poseConfigParseLogged)
                {
                    Debug.LogWarning($"[DualChannelWebRTCReceiver] Failed to read pose config: {ex.Message}", this);
                    _poseConfigParseLogged = true;
                }
                return false;
            }

            _poseConfigParseLogged = false;

            if (string.IsNullOrWhiteSpace(json))
            {
                if (!_poseConfigInvalidLogged)
                {
                    Debug.LogWarning("[DualChannelWebRTCReceiver] Pose config file is empty.", this);
                    _poseConfigInvalidLogged = true;
                }
                return false;
            }

            try
            {
                payload = JsonUtility.FromJson<CameraPoseMessage>(json);
            }
            catch (Exception ex)
            {
                if (!_poseConfigInvalidLogged)
                {
                    Debug.LogWarning($"[DualChannelWebRTCReceiver] Pose config JSON invalid: {ex.Message}", this);
                    _poseConfigInvalidLogged = true;
                }
                return false;
            }

            if (payload == null)
            {
                if (!_poseConfigInvalidLogged)
                {
                    Debug.LogWarning("[DualChannelWebRTCReceiver] Pose config JSON produced null payload.", this);
                    _poseConfigInvalidLogged = true;
                }
                return false;
            }

            payload.EnsureConsistency();

            if (!payload.HasFieldOfView)
            {
                if (!_poseConfigInvalidLogged)
                {
                    Debug.LogWarning("[DualChannelWebRTCReceiver] Pose config missing field-of-view.", this);
                    _poseConfigInvalidLogged = true;
                }
                return false;
            }

            if (!payload.HasPosition || !payload.HasRotation)
            {
                if (!_poseConfigInvalidLogged)
                {
                    Debug.LogWarning("[DualChannelWebRTCReceiver] Pose config missing position or rotation data.", this);
                    _poseConfigInvalidLogged = true;
                }
                return false;
            }

            _poseConfigInvalidLogged = false;
            json = payload.ToJson(false);
            return true;
        }

        private void SendLocalIce(PeerContext ctx, RTCIceCandidate candidate)
        {
            var message = SignalingMessage.CreateIce(candidate);
            candidate?.Dispose();
            if (message == null)
                return;

            ctx.behavior?.SendJson(message.ToJson());
            LogVerbose($"-> {ctx.id}: ice candidate (len={message.candidate.candidate?.Length ?? 0})");
        }

        private void ApplyRemoteIce(PeerContext ctx, SignalingMessage.IceCandidatePayload payload)
        {
            if (ctx.peer == null || payload == null)
                return;

            try
            {
                using var candidate = payload.ToCandidate();
                bool added = ctx.peer.AddIceCandidate(candidate);
                LogVerbose(added
                    ? $"Applied ICE candidate from {ctx.id}"
                    : $"Failed to apply ICE candidate from {ctx.id}");
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[DualChannelWebRTCReceiver] Exception while adding ICE candidate: {ex.Message}", this);
            }
        }

        private PeerContext GetOrCreateContext(string clientId, ReceiverBehavior behavior)
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
            LogVerbose($"Created context for peer {clientId}");
            return ctx;
        }

        private void RemovePeer(string clientId)
        {
            if (_peers.TryGetValue(clientId, out var ctx))
            {
                LogVerbose($"Removing peer {clientId}");
                CleanupPeer(ctx);
                _peers.Remove(clientId);
            }
        }

        private void CleanupPeer(PeerContext ctx)
        {
            if (ctx == null)
                return;

            StopPoseRoutine(ctx);
            DisposePoseChannel(ctx, true);

            if (ctx.packedTrack != null && ctx.packedHandler != null)
            {
                ctx.packedTrack.OnVideoReceived -= ctx.packedHandler;
                ctx.packedTrack.Dispose();
            }

            ctx.peer?.Close();
            ctx.peer?.Dispose();
            ctx.peer = null;
            ctx.packedTrack = null;
            ctx.packedTexture = null;
            ctx.packedHandler = null;
            ctx.loggedFirstFrame = false;

            if (_activePeer == ctx)
            {
                _activePeer = null;
                if (colorPreview != null)
                {
                    colorPreview.texture = null;
                    colorPreview.uvRect = _colorPreviewDefaultUV;
                }
                if (alphaPreview != null)
                {
                    alphaPreview.texture = null;
                    alphaPreview.uvRect = _alphaPreviewDefaultUV;
                }
                if (compositePreview != null)
                {
                    compositePreview.texture = null;
                    if (autoApplyCompositeMaterial)
                        compositePreview.material = null;
                }
                _compositePackedWarningLogged = false;
            }
        }

        class PeerContext
        {
            public string id;
            public ReceiverBehavior behavior;
            public RTCPeerConnection peer;
            public VideoStreamTrack packedTrack;
            public Texture packedTexture;
            public Unity.WebRTC.OnVideoReceived packedHandler;
            public RTCDataChannel poseChannel;
            public Coroutine poseRoutine;
            public bool loggedFirstFrame;
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
                {
                    _owner.LogVerbose("Ignored non-text WS frame from client");
                    return;
                }
                UnityMainThreadDispatcher.Enqueue(() => _owner.HandleClientMessage(ID, this, e.Data));
            }

            protected override void OnClose(CloseEventArgs e)
            {
                UnityMainThreadDispatcher.Enqueue(() => _owner.HandleClientClosed(ID));
            }

            protected override void OnError(WebSocketSharp.ErrorEventArgs e)
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

        void LogVerbose(string message)
        {
            if (verboseLogging)
            {
                Debug.Log($"[DualChannelWebRTCReceiver] {message}", this);
            }
        }
    }
}
