using System;
using System.Collections;
using System.Text;
using Unity.WebRTC;
using UnityEngine;
using UnityEngine.UI;

namespace SecurityCameraToolkit.Runtime.WebRTC
{
    [DisallowMultipleComponent]
    public class DualChannelWebRTCSender : MonoBehaviour
    {
        const string ExtractAlphaShaderName = "Hidden/ExtractAlpha";
        const string PoseDataChannelLabel = "pose";

        [Header("Source Capture")]
        [Tooltip("Camera that produces the MR composition. If assigned, a RenderTexture is auto-created with the correct format.")]
        [SerializeField] Camera sourceCamera;
        [Tooltip("Optional pre-created RenderTexture that already contains the MR composition with a valid alpha channel.")]
        [SerializeField] RenderTexture sourceTexture;
        [Tooltip("If true and no sourceTexture is assigned, a RenderTexture will be allocated using Stream Width/Height.")]
        [SerializeField] bool allocateSourceTextureIfMissing;
        [Min(64)] [SerializeField] int streamWidth = 1920;
        [Min(64)] [SerializeField] int streamHeight = 1080;

        [Header("Preview (Editor convenience)")]
        [SerializeField] RawImage compositePreview;
        [SerializeField] bool applyCompositeMaterial = true;
        [SerializeField] Material compositeMaterial;

        [Header("Signaling")]
        [SerializeField] LanWebSocketSignaler signaler;
        [SerializeField] bool autoConnectSignaler = true;
        [SerializeField] bool autoStartWhenConnected = false;
        [SerializeField] float signalerConnectTimeout = 5f;
        [SerializeField] string[] iceServerUrls = new[] { "stun:stun.l.google.com:19302" };

        [Header("Diagnostics")]
        [SerializeField] bool verboseLogging = true;

        [Header("Pose Synchronization")]
        [Tooltip("Camera whose transform is updated from remote pose messages. Defaults to sourceCamera if left empty.")]
        [SerializeField] Camera targetCamera;

        RenderTexture _colorRT;
        RenderTexture _alphaRT;
        RenderTexture _packedRT;
        bool _ownsColorRT;
        Material _extractAlphaMaterial;
        bool _missingExtractShaderLogged;
        bool _alphaBlitLogged;
        bool _previewLogged;
        bool _compositePackedWarningLogged;
        RTCDataChannel _poseChannel;
        bool _poseChannelOpenLogged;
        bool _poseChannelWarningLogged;
        bool _poseTargetMissingLogged;
        bool _poseMessageInvalidLogged;
        bool _poseAppliedLogged;
        bool _poseFovMissingLogged;
        bool _poseFovUnsupportedLogged;

        RTCPeerConnection _peer;
        VideoStreamTrack _videoTrack;
        MediaStream _videoStream;
        Coroutine _startRoutine;
        bool _isStreaming;
        bool _signalerHooked;

        RTCConfiguration _config;

        void Awake()
        {
            Unity.WebRTC.WebRTC.ConfigureNativeLogging(true, Unity.WebRTC.NativeLoggingSeverity.Info);
            _config = new RTCConfiguration
            {
                iceServers = BuildIceServers(iceServerUrls)
            };
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
            LogVerbose($"OnEnable: sourceTexture={(sourceTexture ? sourceTexture.width + "x" + sourceTexture.height : "null")}, autoAlloc={allocateSourceTextureIfMissing}");

            WebRTCUpdatePump.Instance.Retain();
            HookSignaler(true);
            EnsureRenderTargets();
            UpdateAlphaTextureIfNeeded();
            UpdatePackedTexture();
            UpdatePreview();

            if (autoConnectSignaler && signaler != null && !signaler.Connected)
            {
                LogVerbose("Auto-connecting signaler");
                signaler.Connect();
            }

            if (autoStartWhenConnected && signaler != null && signaler.Connected)
            {
                LogVerbose("Auto-starting streaming because signaler already connected");
                StartStreaming();
            }
        }

        void OnDisable()
        {
            LogVerbose("OnDisable");
            StopStreamingInternal(sendBye: false);
            HookSignaler(false);
            WebRTCUpdatePump.Instance.Release();
        }

        void OnDestroy()
        {
            LogVerbose("OnDestroy: releasing render targets");
            ReleaseRenderTargets();
            DisposeExtractMaterial();
        }

        void LateUpdate()
        {
            if (_colorRT == null)
                return;

            if (_alphaRT == null || _alphaRT.width != _colorRT.width || _alphaRT.height != _colorRT.height)
            {
                LogVerbose("LateUpdate detected alpha RT mismatch. Recreating.");
                EnsureAlphaTexture();
            }

            UpdateAlphaTextureIfNeeded();
            UpdatePackedTexture();
            UpdatePreview();
        }

        public void SetSourceTexture(RenderTexture texture)
        {
            sourceTexture = texture;
            LogVerbose($"SetSourceTexture: {(texture ? texture.width + "x" + texture.height : "null")}");
            EnsureRenderTargets();
            UpdateAlphaTextureIfNeeded();
            UpdatePackedTexture();
            UpdatePreview();
        }

        void HookSignaler(bool on)
        {
            if (signaler == null)
                return;

            if (on && !_signalerHooked)
            {
                signaler.OnJson += OnSignalerMessage;
                signaler.OnConnected += HandleSignalerConnected;
                signaler.OnDisconnected += HandleSignalerDisconnected;
                _signalerHooked = true;
                LogVerbose("Subscribed to signaler events");
            }
            else if (!on && _signalerHooked)
            {
                signaler.OnJson -= OnSignalerMessage;
                signaler.OnConnected -= HandleSignalerConnected;
                signaler.OnDisconnected -= HandleSignalerDisconnected;
                _signalerHooked = false;
                LogVerbose("Unsubscribed from signaler events");
            }
        }
        void HandleSignalerConnected()
        {
            LogVerbose("Signaler connected event");
            if (autoStartWhenConnected && !_isStreaming)
            {
                StartStreaming();
            }
        }

        void HandleSignalerDisconnected()
        {
            LogVerbose("Signaler disconnected event");
            if (_isStreaming)
            {
                StopStreamingInternal(sendBye: false);
            }
        }

        void EnsureRenderTargets()
        {
            if (sourceCamera != null)
            {
                EnsureCameraColorTarget();
            }
            else if (sourceTexture != null)
            {
                if (_ownsColorRT && _colorRT != null)
                {
                    _colorRT.Release();
                    Destroy(_colorRT);
                    _colorRT = null;
                    _ownsColorRT = false;
                }
                _colorRT = sourceTexture;
                LogVerbose($"Using provided source texture {_colorRT.width}x{_colorRT.height}");
            }
            else if (_colorRT == null)
            {
                if (!allocateSourceTextureIfMissing)
                {
                    Debug.LogWarning("[DualChannelWebRTCSender] Source texture not assigned.", this);
                    return;
                }

                _colorRT = CreateColorRenderTexture(streamWidth, streamHeight);
                _ownsColorRT = true;
                LogVerbose($"Allocated fallback source texture {_colorRT.width}x{_colorRT.height} ({_colorRT.format})");
            }

            if (_colorRT == null)
                return;

            if (!_colorRT.IsCreated())
            {
                _colorRT.Create();
                LogVerbose("Ensured color RT is created");
            }

            EnsureAlphaTexture();
            EnsurePackedTexture();
            EnsureExtractMaterial();
        }

        void EnsureCameraColorTarget()
        {
            int width = Mathf.Max(64, streamWidth);
            int height = Mathf.Max(64, streamHeight);
            var desiredFormat = GetColorRenderTextureFormat();

            bool needsNew = !_ownsColorRT
                             || _colorRT == null
                             || _colorRT.width != width
                             || _colorRT.height != height
                             || _colorRT.format != desiredFormat;

            if (needsNew)
            {
                if (_ownsColorRT && _colorRT != null)
                {
                    if (sourceCamera != null && sourceCamera.targetTexture == _colorRT)
                    {
                        sourceCamera.targetTexture = null;
                    }
                    _colorRT.Release();
                    Destroy(_colorRT);
                }

                _colorRT = CreateColorRenderTexture(width, height);
                _ownsColorRT = true;
                LogVerbose($"Allocated camera source texture {_colorRT.width}x{_colorRT.height} ({_colorRT.format})");
            }

            if (sourceCamera != null && sourceCamera.targetTexture != _colorRT)
            {
                sourceCamera.targetTexture = _colorRT;
                LogVerbose("Assigned camera target texture");
            }
        }

        RenderTexture CreateColorRenderTexture(int width, int height)
        {
            var format = GetColorRenderTextureFormat();
            var rt = new RenderTexture(width, height, 0, format)
            {
                name = "__WebRTC_Color__",
                useMipMap = false,
                autoGenerateMips = false,
                filterMode = FilterMode.Bilinear,
                wrapMode = TextureWrapMode.Clamp
            };
            rt.Create();
            return rt;
        }

        RenderTextureFormat GetColorRenderTextureFormat()
        {
            switch (Application.platform)
            {
                case RuntimePlatform.WindowsPlayer:
                case RuntimePlatform.WindowsEditor:
                    return RenderTextureFormat.BGRA32;
                case RuntimePlatform.IPhonePlayer:
                    return RenderTextureFormat.BGRA32;
                case RuntimePlatform.Android:
                    return RenderTextureFormat.ARGB32;
                default:
                    return RenderTextureFormat.ARGB32;
            }
        }

        void EnsureAlphaTexture()
        {
            if (_colorRT == null)
                return;

            bool needsRecreate = _alphaRT == null
                                  || _alphaRT.width != _colorRT.width
                                  || _alphaRT.height != _colorRT.height
                                  || !_alphaRT.IsCreated();

            if (!needsRecreate)
                return;

            if (_alphaRT != null)
            {
                _alphaRT.Release();
                Destroy(_alphaRT);
                _alphaRT = null;
            }

            if (_packedRT != null)
            {
                _packedRT.Release();
                Destroy(_packedRT);
                _packedRT = null;
            }

            var alphaFormat = GetColorRenderTextureFormat(); // Mirror color format so WebRTC accepts the stream.
            _alphaRT = new RenderTexture(_colorRT.width, _colorRT.height, 0, alphaFormat)
            {
                name = "__WebRTC_Alpha__",
                useMipMap = false,
                autoGenerateMips = false,
                filterMode = FilterMode.Bilinear,
                wrapMode = TextureWrapMode.Clamp
            };
            _alphaRT.Create();
            _alphaBlitLogged = false;
            LogVerbose($"Created alpha RT {_alphaRT.width}x{_alphaRT.height}");
        }

        void EnsurePackedTexture()
        {
            if (_colorRT == null)
                return;

            int expectedWidth = _colorRT.width;
            int expectedHeight = _colorRT.height * 2;

            bool needsRecreate = _packedRT == null
                                  || _packedRT.width != expectedWidth
                                  || _packedRT.height != expectedHeight
                                  || !_packedRT.IsCreated();

            if (!needsRecreate)
                return;

            if (_packedRT != null)
            {
                _packedRT.Release();
                Destroy(_packedRT);
                _packedRT = null;
            }

            var format = GetColorRenderTextureFormat();
            _packedRT = new RenderTexture(expectedWidth, expectedHeight, 0, format)
            {
                name = "__WebRTC_Packed__",
                useMipMap = false,
                autoGenerateMips = false,
                filterMode = FilterMode.Bilinear,
                wrapMode = TextureWrapMode.Clamp
            };
            _packedRT.Create();
            LogVerbose($"Created packed RT {_packedRT.width}x{_packedRT.height}");
        }

        void EnsureExtractMaterial()
        {
            if (_extractAlphaMaterial != null)
                return;

            var shader = Shader.Find(ExtractAlphaShaderName);
            if (shader == null)
            {
                if (!_missingExtractShaderLogged)
                {
                    Debug.LogError($"[DualChannelWebRTCSender] Shader '{ExtractAlphaShaderName}' not found. Cannot extract alpha channel.", this);
                    _missingExtractShaderLogged = true;
                }
                return;
            }

            _extractAlphaMaterial = new Material(shader);
            _missingExtractShaderLogged = false;
            LogVerbose("Created extract-alpha material");
        }

        void UpdateAlphaTextureIfNeeded()
        {
            if (_colorRT == null || _alphaRT == null)
                return;

            EnsureExtractMaterial();
            if (_extractAlphaMaterial == null)
                return;
            _extractAlphaMaterial.SetTexture("_MainTex", _colorRT);
            Graphics.Blit(_colorRT, _alphaRT, _extractAlphaMaterial);
            if (!_alphaBlitLogged)
            {
                LogVerbose("Alpha RT updated via ExtractAlpha shader");
                _alphaBlitLogged = true;
            }
        }

        void UpdatePackedTexture()
        {
            if (_packedRT == null || _colorRT == null)
                return;

            int width = _colorRT.width;
            int height = _colorRT.height;

            var previous = RenderTexture.active;
            RenderTexture.active = _packedRT;
            GL.PushMatrix();
            try
            {
                GL.LoadPixelMatrix(0f, _packedRT.width, 0f, _packedRT.height);
                GL.Clear(false, true, Color.black);

                var topRect = new Rect(0f, height, width, height);
                Graphics.DrawTexture(topRect, _colorRT);

                var bottomSource = (Texture)(_alphaRT != null ? _alphaRT : Texture2D.blackTexture);
                var bottomRect = new Rect(0f, 0f, width, height);
                Graphics.DrawTexture(bottomRect, bottomSource);
            }
            finally
            {
                GL.PopMatrix();
                RenderTexture.active = previous;
            }
        }

        void UpdatePreview()
        {
            if (compositePreview == null)
                return;

            Texture displayTex = _packedRT ?? (Texture)_colorRT;

            if (compositeMaterial != null && applyCompositeMaterial)
            {
                bool applied = false;
                if (_packedRT != null && compositeMaterial.HasProperty("_PackedTex"))
                {
                    compositeMaterial.SetTexture("_PackedTex", _packedRT);
                    applied = true;
                }
                else if (_colorRT != null && _alphaRT != null && compositeMaterial.HasProperty("_ColorTex") && compositeMaterial.HasProperty("_AlphaTex"))
                {
                    compositeMaterial.SetTexture("_ColorTex", _colorRT);
                    compositeMaterial.SetTexture("_AlphaTex", _alphaRT);
                    applied = true;
                }

                if (!applied)
                {
                    if (!_compositePackedWarningLogged)
                    {
                        Debug.LogWarning("[DualChannelWebRTCSender] Composite material is missing expected texture properties (_PackedTex or _ColorTex/_AlphaTex).", this);
                        _compositePackedWarningLogged = true;
                    }
                }
                else
                {
                    _compositePackedWarningLogged = false;
                }

                compositePreview.material = applied ? compositeMaterial : null;
                compositePreview.texture = displayTex;
            }
            else
            {
                compositePreview.material = null;
                compositePreview.texture = displayTex;
            }

            if (!_previewLogged && displayTex != null)
            {
                LogVerbose("Preview updated with current textures");
                _previewLogged = true;
            }
        }

        void ReleaseRenderTargets()
        {
            if (sourceCamera != null && sourceCamera.targetTexture == _colorRT)
            {
                sourceCamera.targetTexture = null;
            }

            if (_ownsColorRT && _colorRT != null)
            {
                _colorRT.Release();
                Destroy(_colorRT);
            }
            _colorRT = null;
            _ownsColorRT = false;

            if (_alphaRT != null)
            {
                _alphaRT.Release();
                Destroy(_alphaRT);
                _alphaRT = null;
            }

            if (_packedRT != null)
            {
                _packedRT.Release();
                Destroy(_packedRT);
                _packedRT = null;
            }
        }

        void DisposeExtractMaterial()
        {
            if (_extractAlphaMaterial != null)
            {
                Destroy(_extractAlphaMaterial);
                _extractAlphaMaterial = null;
            }
        }

        public void StartStreaming()
        {
            if (!isActiveAndEnabled)
            {
                Debug.LogWarning("[DualChannelWebRTCSender] Component disabled, aborting start.", this);
                return;
            }

            if (_isStreaming || _startRoutine != null)
                return;

            EnsureRenderTargets();
            UpdateAlphaTextureIfNeeded();
            EnsurePackedTexture();
            UpdatePackedTexture();

            if (_colorRT == null || _alphaRT == null || _packedRT == null)
            {
                Debug.LogError("[DualChannelWebRTCSender] Missing render textures. Cannot start streaming.", this);
                return;
            }

            if (signaler == null)
            {
                Debug.LogError("[DualChannelWebRTCSender] Signaler is not assigned.", this);
                return;
            }

            LogVerbose($"Starting streaming with packed {_packedRT.width}x{_packedRT.height} (source {_colorRT.width}x{_colorRT.height})");
            _startRoutine = StartCoroutine(BeginStreamingRoutine());
        }

        public void StopStreaming()
        {
            StopStreamingInternal(sendBye: true);
        }

        IEnumerator BeginStreamingRoutine()
        {
            if (autoConnectSignaler && !signaler.Connected)
            {
                LogVerbose("Waiting for signaler connection");
                signaler.Connect();
            }

            float timeout = Mathf.Max(0.5f, signalerConnectTimeout);
            while (!signaler.Connected && timeout > 0f)
            {
                timeout -= Time.deltaTime;
                yield return null;
            }

            if (!signaler.Connected)
            {
                Debug.LogError("[DualChannelWebRTCSender] Signaler connection timed out.", this);
                _startRoutine = null;
                yield break;
            }

            SetupPeerConnection();
            if (_peer == null)
            {
                _startRoutine = null;
                yield break;
            }

            LogVerbose("Creating SDP offer");
            var offerOp = _peer.CreateOffer();
            yield return offerOp;
            if (offerOp.IsError)
            {
                Debug.LogError($"[DualChannelWebRTCSender] CreateOffer failed: {offerOp.Error.message}", this);
                CleanupPeer();
                _startRoutine = null;
                yield break;
            }

            var desc = offerOp.Desc;
            LogVerbose("Setting local description");
            var localOp = _peer.SetLocalDescription(ref desc);
            yield return localOp;
            if (localOp.IsError)
            {
                Debug.LogError($"[DualChannelWebRTCSender] SetLocalDescription failed: {localOp.Error.message}", this);
                CleanupPeer();
                _startRoutine = null;
                yield break;
            }

            LogVerbose("Sending SDP offer");
            var message = SignalingMessage.CreateOffer(desc.sdp);
            signaler.SendJson(message.ToJson());
            _isStreaming = true;
            _startRoutine = null;
        }

        void SetupPeerConnection()
        {
            CleanupPeer();

            if (_packedRT == null)
            {
                Debug.LogError("[DualChannelWebRTCSender] Render textures not ready.", this);
                return;
            }

            _peer = new RTCPeerConnection(ref _config);
            _peer.OnIceCandidate = HandleLocalIceCandidate;
            _peer.OnConnectionStateChange = state =>
            {
                LogVerbose($"Peer connection state: {state}");
            };

            _peer.OnDataChannel = HandleRemoteDataChannel;

            SetupPoseDataChannel();

            _videoTrack = new VideoStreamTrack(_packedRT, CopyTextureHelper.VerticalFlipCopy);
            _videoStream = new MediaStream();
            _videoStream.AddTrack(_videoTrack);
            var videoSender = _peer.AddTrack(_videoTrack, _videoStream);
            if (videoSender != null)
            {
                ApplyEncodingLimits(videoSender, maxFps: 30, maxKbps: 3500);
                //videoSender.SyncApplicationFramerate = true;
            }
            LogVerbose($"Added packed track {_packedRT.width}x{_packedRT.height}");
        }

        void ApplyEncodingLimits(Unity.WebRTC.RTCRtpSender sender, int maxFps, int maxKbps)
        {
            if (sender == null) return;
            var p = sender.GetParameters();
            if (p.encodings == null || p.encodings.Length == 0)
                p.encodings = new[] { new Unity.WebRTC.RTCRtpEncodingParameters() };

            // 单路编码（不分层）。注意：单位 bps
            p.encodings[0].maxFramerate = 30; // maxFps;
            p.encodings[0].maxBitrate   = (ulong)maxKbps * 1000;

            // 可选：如果 API 暴露�?scaleResolutionDownBy（某些版本有�?
            // p.encodings[0].scaleResolutionDownBy = 1.0; // 彩色不降分辨�?
            var err = sender.SetParameters(p);
            // 生产里可检�?err 是否�?RTCError.None
        }

        void SetupPoseDataChannel()
        {
            if (_peer == null)
                return;

            var init = new RTCDataChannelInit
            {
                ordered = true
            };

            try
            {
                var channel = _peer.CreateDataChannel(PoseDataChannelLabel, init);
                AttachPoseChannel(channel, remoteInitiated: false);
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[DualChannelWebRTCSender] Failed to create pose data channel: {ex.Message}", this);
            }
        }

        void HandleRemoteDataChannel(RTCDataChannel channel)
        {
            if (channel == null)
                return;

            if (channel.Label == PoseDataChannelLabel)
            {
                AttachPoseChannel(channel, remoteInitiated: true);
            }
            else
            {
                LogVerbose($"Received unsupported data channel '{channel.Label}' (ignored)");
            }
        }

        void AttachPoseChannel(RTCDataChannel channel, bool remoteInitiated)
        {
            if (channel == null)
                return;

            if (_poseChannel == channel)
                return;

            if (_poseChannel != null)
            {
                DisposePoseChannel(true);
            }

            _poseChannel = channel;
            ResetPoseDiagnostics();

            var initiated = remoteInitiated;
            _poseChannel.OnOpen = () => UnityMainThreadDispatcher.Enqueue(() => OnPoseChannelOpen(initiated));
            _poseChannel.OnClose = () => UnityMainThreadDispatcher.Enqueue(OnPoseChannelClosed);
            _poseChannel.OnMessage = HandlePoseDataMessage;

            if (_poseChannel.ReadyState == RTCDataChannelState.Open)
            {
                OnPoseChannelOpen(initiated);
            }
        }

        void OnPoseChannelOpen(bool remoteInitiated)
        {
            if (_poseChannelOpenLogged)
                return;

            LogVerbose(remoteInitiated
                ? "Pose data channel established (remote)"
                : "Pose data channel established");
            _poseChannelOpenLogged = true;
        }

        void OnPoseChannelClosed()
        {
            DisposePoseChannel();
        }

        void DisposePoseChannel(bool requestClose = false)
        {
            if (_poseChannel == null)
                return;

            var channel = _poseChannel;
            _poseChannel = null;

            channel.OnOpen = null;
            channel.OnClose = null;
            channel.OnMessage = null;

            if (requestClose)
            {
                try { channel.Close(); }
                catch (Exception) { }
            }

            channel.Dispose();
            ResetPoseDiagnostics();
        }

        void ResetPoseDiagnostics()
        {
            _poseChannelOpenLogged = false;
            _poseChannelWarningLogged = false;
            _poseTargetMissingLogged = false;
            _poseMessageInvalidLogged = false;
            _poseAppliedLogged = false;
            _poseFovMissingLogged = false;
            _poseFovUnsupportedLogged = false;
        }

        void HandlePoseDataMessage(byte[] bytes)
        {
            if (bytes == null || bytes.Length == 0)
                return;

            string json;
            try
            {
                json = Encoding.UTF8.GetString(bytes);
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[DualChannelWebRTCSender] Failed to decode pose message: {ex.Message}", this);
                return;
            }

            if (string.IsNullOrWhiteSpace(json))
                return;

            CameraPoseMessage payload;
            try
            {
                payload = JsonUtility.FromJson<CameraPoseMessage>(json);
            }
            catch (Exception ex)
            {
                if (!_poseMessageInvalidLogged)
                {
                    Debug.LogWarning($"[DualChannelWebRTCSender] Invalid pose message JSON: {ex.Message}", this);
                    _poseMessageInvalidLogged = true;
                }
                return;
            }

            if (payload == null)
            {
                if (!_poseMessageInvalidLogged)
                {
                    Debug.LogWarning("[DualChannelWebRTCSender] Received empty pose payload.", this);
                    _poseMessageInvalidLogged = true;
                }
                return;
            }

            payload.EnsureConsistency();

            var hasPosition = payload.TryGetPosition(out var position);
            var hasRotation = payload.TryGetRotation(out var rotation);
            var hasFieldOfView = payload.TryGetFieldOfView(out var fieldOfView);

            if (!hasFieldOfView)
            {
                if (!_poseFovMissingLogged)
                {
                    LogVerbose("Pose message missing field-of-view. Using existing camera value.");
                    _poseFovMissingLogged = true;
                }
            }
            else
            {
                _poseFovMissingLogged = false;
            }

            if (!hasPosition && !hasRotation)
            {
                if (!_poseChannelWarningLogged)
                {
                    Debug.LogWarning("[DualChannelWebRTCSender] Pose message missing position and rotation. Ignoring.", this);
                    _poseChannelWarningLogged = true;
                }
                return;
            }

            _poseChannelWarningLogged = false;
            _poseMessageInvalidLogged = false;

            UnityMainThreadDispatcher.Enqueue(() => ApplyPoseFromMessage(position, hasPosition, rotation, hasRotation, fieldOfView, hasFieldOfView, payload.calibration, payload.timestamp));
        }

        void ApplyPoseFromMessage(Vector3 position, bool hasPosition, Quaternion rotation, bool hasRotation, float fieldOfView, bool hasFieldOfView, string calibrationId, string timestamp)
        {
            var targetTransform = ResolvePoseTarget();
            var poseCamera = targetCamera != null ? targetCamera : sourceCamera;
            if (targetTransform == null)
            {
                if (!_poseTargetMissingLogged)
                {
                    Debug.LogWarning("[DualChannelWebRTCSender] Pose target camera not assigned.", this);
                    _poseTargetMissingLogged = true;
                }
                return;
            }

            _poseTargetMissingLogged = false;

            if (hasPosition)
            {
                targetTransform.position = position;
            }

            if (hasRotation)
            {
                targetTransform.rotation = rotation;
            }

            string fovLabel = hasFieldOfView ? "skipped" : "unchanged";
            if (hasFieldOfView)
            {
                if (poseCamera != null && !poseCamera.orthographic)
                {
                    var clampedFov = Mathf.Clamp(fieldOfView, 1f, 179f);
                    poseCamera.fieldOfView = clampedFov;
                    fovLabel = clampedFov.ToString("F1");
                    _poseFovUnsupportedLogged = false;
                }
                else if (!_poseFovUnsupportedLogged)
                {
                    Debug.LogWarning("[DualChannelWebRTCSender] Pose message provided field-of-view but target camera cannot accept it.", this);
                    _poseFovUnsupportedLogged = true;
                }
            }
            else
            {
                _poseFovUnsupportedLogged = false;
            }

            if (verboseLogging && !_poseAppliedLogged)
            {
                var timestampLabel = string.IsNullOrEmpty(timestamp) ? "n/a" : timestamp;
                var calibrationLabel = string.IsNullOrEmpty(calibrationId) ? "n/a" : calibrationId;
                LogVerbose($"Applied remote pose (timestamp={timestampLabel}, calibration={calibrationLabel}, pos={(hasPosition ? position.ToString("F3") : "unchanged")}, rot={(hasRotation ? rotation.eulerAngles.ToString("F1") : "unchanged")}, fov={fovLabel})");
                _poseAppliedLogged = true;
            }
        }

        Transform ResolvePoseTarget()
        {
            if (targetCamera != null)
                return targetCamera.transform;
            if (sourceCamera != null)
                return sourceCamera.transform;
            return null;
        }

        void HandleLocalIceCandidate(RTCIceCandidate candidate)
        {
            var message = SignalingMessage.CreateIce(candidate);
            if (message != null)
            {
                LogVerbose($"Sending ICE candidate (len={message.candidate.candidate?.Length ?? 0})");
                signaler?.SendJson(message.ToJson());
            }
            candidate?.Dispose();
        }

        void OnSignalerMessage(string json)
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
                Debug.LogWarning($"[DualChannelWebRTCSender] Failed to parse signaling message: {ex.Message}\n{json}", this);
                return;
            }

            if (message == null)
                return;

            LogVerbose($"Received signaling message '{message.type}'");

            switch (message.type)
            {
                case "answer":
                    if (!string.IsNullOrEmpty(message.sdp))
                        StartCoroutine(ApplyRemoteAnswer(message.sdp));
                    break;
                case "ice":
                    if (message.candidate != null)
                        ApplyRemoteCandidate(message.candidate);
                    break;
                case "bye":
                    StopStreamingInternal(sendBye: false);
                    break;
            }
        }

        IEnumerator ApplyRemoteAnswer(string sdp)
        {
            if (_peer == null)
                yield break;

            LogVerbose("Applying remote answer");
            var desc = new RTCSessionDescription { type = RTCSdpType.Answer, sdp = sdp };
            var remoteOp = _peer.SetRemoteDescription(ref desc);
            yield return remoteOp;
            if (remoteOp.IsError)
            {
                Debug.LogError($"[DualChannelWebRTCSender] SetRemoteDescription failed: {remoteOp.Error.message}", this);
            }
        }

        void ApplyRemoteCandidate(SignalingMessage.IceCandidatePayload payload)
        {
            if (_peer == null || payload == null)
                return;

            try
            {
                using var candidate = payload.ToCandidate();
                bool added = _peer.AddIceCandidate(candidate);
                LogVerbose(added
                    ? "Applied remote ICE candidate"
                    : "Failed to apply remote ICE candidate");
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[DualChannelWebRTCSender] Exception adding remote ICE: {ex.Message}", this);
            }
        }

        void StopStreamingInternal(bool sendBye)
        {
            if (_startRoutine != null)
            {
                StopCoroutine(_startRoutine);
                _startRoutine = null;
            }

            if (!_isStreaming)
            {
                CleanupPeer();
                return;
            }

            if (sendBye && signaler != null)
            {
                LogVerbose("Sending bye message");
                var bye = new SignalingMessage { type = "bye" };
                signaler.SendJson(bye.ToJson());
            }

            LogVerbose("Stopping streaming and cleaning up peer");
            CleanupPeer();
            _isStreaming = false;
        }

        void CleanupPeer()
        {
            DisposePoseChannel(true);
            _videoStream?.Dispose();
            _videoStream = null;

            _videoTrack?.Dispose();
            _videoTrack = null;

            if (_peer != null)
            {
                try { _peer.Close(); }
                catch { }
                _peer.Dispose();
                _peer = null;
                LogVerbose("Peer connection disposed");
            }
        }
        void LogVerbose(string message)
        {
            if (verboseLogging)
            {
                Debug.Log($"[DualChannelWebRTCSender] {message}", this);
            }
        }
    }
}
