using System;
using System.Collections;
using Unity.WebRTC;
using UnityEngine;
using UnityEngine.UI;

namespace SecurityCameraToolkit.Runtime.WebRTC
{
    [DisallowMultipleComponent]
    public class DualChannelWebRTCSender : MonoBehaviour
    {
        const string ExtractAlphaShaderName = "Hidden/ExtractAlpha";

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

        RenderTexture _colorRT;
        RenderTexture _alphaRT;
        bool _ownsColorRT;
        Material _extractAlphaMaterial;
        bool _missingExtractShaderLogged;
        bool _alphaBlitLogged;
        bool _previewLogged;

        RTCPeerConnection _peer;
        VideoStreamTrack _colorTrack;
        VideoStreamTrack _alphaTrack;
        MediaStream _colorStream;
        MediaStream _alphaStream;
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
            UpdatePreview();
        }

        public void SetSourceTexture(RenderTexture texture)
        {
            sourceTexture = texture;
            LogVerbose($"SetSourceTexture: {(texture ? texture.width + "x" + texture.height : "null")}");
            EnsureRenderTargets();
            UpdateAlphaTextureIfNeeded();
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

            _alphaRT = new RenderTexture(_colorRT.width, _colorRT.height, 0, RenderTextureFormat.ARGB32)
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

            Graphics.Blit(_colorRT, _alphaRT, _extractAlphaMaterial);
            if (!_alphaBlitLogged)
            {
                LogVerbose("Alpha RT updated via ExtractAlpha shader");
                _alphaBlitLogged = true;
            }
        }

        void UpdatePreview()
        {
            if (compositePreview == null)
                return;

            if (compositeMaterial != null && applyCompositeMaterial)
            {
                compositeMaterial.SetTexture("_ColorTex", _colorRT);
                compositeMaterial.SetTexture("_AlphaTex", _alphaRT);
                compositePreview.material = compositeMaterial;
                compositePreview.texture = _colorRT;
            }
            else
            {
                compositePreview.material = null;
                compositePreview.texture = _colorRT;
            }

            if (!_previewLogged && _colorRT != null)
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

            if (_colorRT == null || _alphaRT == null)
            {
                Debug.LogError("[DualChannelWebRTCSender] Missing render textures. Cannot start streaming.", this);
                return;
            }

            if (signaler == null)
            {
                Debug.LogError("[DualChannelWebRTCSender] Signaler is not assigned.", this);
                return;
            }

            LogVerbose($"Starting streaming with color {_colorRT.width}x{_colorRT.height} and alpha {_alphaRT.width}x{_alphaRT.height}");
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

            if (_colorRT == null || _alphaRT == null)
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

            _colorTrack = new VideoStreamTrack(_colorRT, CopyTextureHelper.VerticalFlipCopy);
            _colorStream = new MediaStream();
            _colorStream.AddTrack(_colorTrack);
            var colorSender = _peer.AddTrack(_colorTrack, _colorStream);
            if (colorSender != null)
            {
                colorSender.SyncApplicationFramerate = true;
            }
            LogVerbose($"Added color track {_colorRT.width}x{_colorRT.height}");

            _alphaTrack = new VideoStreamTrack(_alphaRT, CopyTextureHelper.VerticalFlipCopy);
            _alphaStream = new MediaStream();
            _alphaStream.AddTrack(_alphaTrack);
            var alphaSender = _peer.AddTrack(_alphaTrack, _alphaStream);
            if (alphaSender != null)
            {
                alphaSender.SyncApplicationFramerate = true;
            }
            LogVerbose($"Added alpha track {_alphaRT.width}x{_alphaRT.height}");
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
            _alphaStream?.Dispose();
            _alphaStream = null;
            _colorStream?.Dispose();
            _colorStream = null;

            _alphaTrack?.Dispose();
            _alphaTrack = null;
            _colorTrack?.Dispose();
            _colorTrack = null;

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


