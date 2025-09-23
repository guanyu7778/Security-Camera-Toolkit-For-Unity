// Copyright (c) https://github.com/Bian-Sh
// Licensed under the MIT License.
using System;
using System.IO;
using Newtonsoft.Json.Linq;                     // <— JSON 解析
using Unity.Profiling;
using UnityEngine;
using UnityEngine.Events;
using UnityEngine.Rendering;
using UnityEngine.UI;

namespace zFramework.Media
{
    /// <summary>
    /// 视频渲染器（整合 GPU 去畸变 + 全彩显示 + 回读给 AprilTag）
    /// - 从 I422 (YUV) 三平面创建 Texture2D
    /// - 调去畸变的 YUV 合成 shader 输出到全彩 RT（显示）
    /// - 按需 AsyncGPUReadback 回读 RGBA32 → 灰度 → 事件回调给 AprilTag
    /// - 不改变分辨率/FOV
    /// </summary>
    [RequireComponent(typeof(RawImage))]
    public class VideoRendererFotAprilTag : MonoBehaviour
    {
        #region Show In Inspector (原有)
#pragma warning disable CS0414
        [Header("渲染状态:"), SerializeField] private bool isRendering;
#pragma warning restore CS0414

        [Header("取流速率："), Range(10, 60), Tooltip(aboutframrate), SerializeField]
        private int framerate = 25;

        [Header("帧队列最大容量："), Range(2, 5), Tooltip(aboutQueueSize)]
        public int maxFrameQueueSize = 3;

        [Tooltip(aboutstatistics)]
        public bool enableStatistics = true;

        [SerializeField] string frameLoad, frameRender, frameDrop;

        [Space(8)]
        public VideoRendererEvent OnStatisticsReported = new VideoRendererEvent();
        public AprilTagColor32SpanProvider _provider; // 保留原 CPU 路径（可作为备选）
        #endregion

        #region New: GPU去畸变/显示/回读 选项
        [Header("Undistort & Composite (GPU)")]
        [Tooltip("去畸变+合成的材质（请指向你已修改的Shader材质）")]
        public Material undistortCompositeMaterial;

        [Tooltip("UV 平面尺寸 (像素)。4:2:0 → W/2,H/2；4:2:2 → W/2,H；等尺寸则填 W,H")]
        public Vector2Int UVSize = new Vector2Int(960, 540);

        [Tooltip("输出到全彩 RenderTexture（线性 ARGB32）。若为空会自动创建")]
        public RenderTexture OutputRT;

        [Tooltip("UI 预览（RawImage）。若为空默认用本组件的 RawImage")]
        public RawImage Preview;

        [Header("Calibration JSON (OpenCV风格)")]
        [Tooltip("StreamingAssets 相对路径或绝对路径")]
        public string CalibrationJsonPath = "calib.json";

        [Header("Shader Switches")]
        public bool FlipY = false;
        [Tooltip("false=Limited(16..235)，true=Full(0..255)")]
        public bool UseFullRange = false;
        [Tooltip("false=BT.601，true=BT.709")]
        public bool UseBt709 = false;

        [Header("AprilTag Readback")]
        public bool EnableReadback = true;
        [Range(1, 8)] public int ReadbackEveryNFrames = 1;
        public event Action<byte[], int, int> OnGrayFrameReady; // 回调给 AprilTag

        // Shader属性名（若你的shader属性名不同，可以改这里）
        readonly int ID_YTex = Shader.PropertyToID("_YTexture");
        readonly int ID_UTex = Shader.PropertyToID("_UTexture");
        readonly int ID_VTex = Shader.PropertyToID("_VTexture");
        readonly int ID_YSize = Shader.PropertyToID("_YSize");     // (w,h,1/w,1/h)
        readonly int ID_UVSize = Shader.PropertyToID("_UVSize");   // (w,h,1/w,1/h)
        readonly int ID_K = Shader.PropertyToID("_K");             // (fx,fy,cx,cy)
        readonly int ID_D1 = Shader.PropertyToID("_D1");           // (k1,k2,p1,p2)
        readonly int ID_K3 = Shader.PropertyToID("_K3");           // k3
        readonly int ID_FlipY = Shader.PropertyToID("_FlipY");
        readonly int ID_RangeMode = Shader.PropertyToID("_RangeMode"); // 0/1
        readonly int ID_ColorStd = Shader.PropertyToID("_ColorStd");   // 0/1

        // 若你的材质变量名不是 _YTexture/_UTexture/_VTexture，可在这里改
        [Header("Shader Property Names (可按需改)")]
        [SerializeField] private string attr_y = "_YTexture";
        [SerializeField] private string attr_u = "_UTexture";
        [SerializeField] private string attr_v = "_VTexture";
        #endregion

        #region Mono
        private void Awake() => monitor = GetComponent<RawImage>();
        protected void OnDisable() => CreateEmptyVideoTextures();

        void Start()
        {
            _provider = new AprilTagColor32SpanProvider();

            // UI 材质克隆（保留你原有逻辑）
            monitor.material = new Material(monitor.material);
            videoMaterial = monitor.materialForRendering;

            // 优先使用传入的材质，否则尝试从 RawImage 的材质上找
            if (!undistortCompositeMaterial)
                undistortCompositeMaterial = videoMaterial;

            CreateEmptyVideoTextures();

            // 如果没指定 Preview，就用自己
            if (!Preview) Preview = monitor;

            // 输出 RT 初始化在第一次拿到帧尺寸时完成（因为需要知道 Width/Height）
        }

        void Update() => TryProcessI422VideoFrame();
        void OnDestroy()
        {
            StopRendering();
            if (OutputRT) OutputRT.Release();
            if (_tmpCpuTex) Destroy(_tmpCpuTex);
        }
        #endregion

        #region Renderer Behaviours (原有)
        public void StopRendering()
        {
            if (null != source)
            {
                source.OnVideoFrameReady -= I422AVideoFrameReady;
                source.OnInterruptedSignal -= OnInterruptedSignal;
                source = null;
            }
            videoFrameQueue?.Clear();
            videoFrameQueue = null;
            isRendering = false;
            CreateEmptyVideoTextures();
        }

        public void PauseRendering() => videoFrameQueue?.Clear();
        internal void ResumeRendering() => videoFrameQueue?.RestartTick();

        public void StartRendering(IVideoSource source)
        {
            this.source = source;
            videoFrameQueue = new VideoFrameQueue<I422VideoFrameStorage>(maxFrameQueueSize);
            source.OnVideoFrameReady += I422AVideoFrameReady;
            source.OnInterruptedSignal += OnInterruptedSignal;
            isRendering = true;
        }
        #endregion

        #region Frame Flow（融合 GPU 去畸变 + 回读）
        public float RenderFPS { set => framerate = Mathf.RoundToInt(value); }
        private bool OnInterruptedSignal() => videoFrameQueue.IsQueueBlocked;

        private void CreateEmptyVideoTextures()
        {
            _textureY = _textureU = _textureV = null;
            if (videoMaterial)
            {
                videoMaterial.SetTexture(attr_y, _textureY);
                videoMaterial.SetTexture(attr_u, _textureU);
                videoMaterial.SetTexture(attr_v, _textureV);
            }
            if (Preview) Preview.texture = null;
        }

        protected void I422AVideoFrameReady(I422VideoFrame frame)
        {
            // 采集线程 → 入队，主线程出队渲 UI
            videoFrameQueue.Enqueue(frame);
        }

        private void TryProcessI422VideoFrame()
        {
            // 控帧率（保留你的逻辑）
            if (preFrameRate != framerate)
            {
                preFrameRate = framerate;
                frameDuration = Mathf.Max(0f, 1f / Mathf.Max(10, framerate) - 0.003f);
            }

            if (videoFrameQueue != null)
            {
                var curTime = Time.time;
                if (curTime - lastUpdateTime >= frameDuration)
                {
                    DoProcess();
                    lastUpdateTime = curTime;
                }
                ReportProfilerStatistics();
            }

            void DoProcess()
            {
                if (videoFrameQueue.TryDequeue(out I422VideoFrameStorage frame))
                {
                    lumaWidth = frame.Width;
                    lumaHeight = frame.Height;

                    // ——— 1) 创建/更新三路 R8 纹理
                    if (_textureY == null || _textureY.width != lumaWidth || _textureY.height != lumaHeight)
                    {
                        _textureY = new Texture2D(lumaWidth, lumaHeight, TextureFormat.Alpha8, mipChain: false, linear: true);
                        undistortCompositeMaterial.SetTexture(ID_YTex, _textureY);
                    }
                    int chromaWidth = lumaWidth / 2;
                    int chromaHeight = lumaHeight / 2;
                    if (_textureU == null || _textureU.width != chromaWidth || _textureU.height != chromaHeight)
                    {
                        _textureU = new Texture2D(chromaWidth, chromaHeight, TextureFormat.Alpha8, mipChain: false, linear: true);
                        undistortCompositeMaterial.SetTexture(ID_UTex, _textureU);
                    }
                    if (_textureV == null || _textureV.width != chromaWidth || _textureV.height != chromaHeight)
                    {
                        _textureV = new Texture2D(chromaWidth, chromaHeight, TextureFormat.Alpha8, mipChain: false, linear: true);
                        undistortCompositeMaterial.SetTexture(ID_VTex, _textureV);
                    }

                    // ——— 2) 更新像素数据 & 上传到 GPU
                    using (var _ = loadTextureDataMarker.Auto())
                    {
                        _textureY.LoadRawTextureData(frame.Buffer_Y);
                        _textureU.LoadRawTextureData(frame.Buffer_U);
                        _textureV.LoadRawTextureData(frame.Buffer_V);
                    }
                    using (var _ = uploadTextureToGpuMarker.Auto())
                    {
                        _textureY.Apply(false, false);
                        _textureU.Apply(false, false);
                        _textureV.Apply(false, false);
                    }

                    // ——— 3) 输出 RT 初始化（与 Y 一致尺寸；线性 ARGB32）
                    if (!OutputRT || OutputRT.width != lumaWidth || OutputRT.height != lumaHeight)
                    {
                        if (OutputRT) OutputRT.Release();
                        OutputRT = new RenderTexture(lumaWidth, lumaHeight, 0, RenderTextureFormat.ARGB32, RenderTextureReadWrite.Linear);
                        OutputRT.filterMode = FilterMode.Bilinear;
                        OutputRT.wrapMode = TextureWrapMode.Clamp;
                        OutputRT.Create();
                        if (Preview) Preview.texture = OutputRT;

                        // 标定 JSON 只需在拿到尺寸后加载一次（或重新加载）
                        TryLoadCalibration(CalibrationJsonPath, lumaWidth, lumaHeight);

                        // 同步 Y/UV 尺寸到材质（不改变分辨率/FOV）
                        undistortCompositeMaterial.SetVector(ID_YSize, new Vector4(lumaWidth, lumaHeight, 1f / lumaWidth, 1f / lumaHeight));
                        var uvW = Mathf.Max(1, UVSize.x);
                        var uvH = Mathf.Max(1, UVSize.y);
                        undistortCompositeMaterial.SetVector(ID_UVSize, new Vector4(uvW, uvH, 1f / uvW, 1f / uvH));
                    }

                    // ——— 4) 动态开关（翻转/色彩/范围）
                    undistortCompositeMaterial.SetFloat(ID_FlipY,   FlipY ? 1f : 0f);
                    undistortCompositeMaterial.SetFloat(ID_RangeMode, UseFullRange ? 1f : 0f);
                    undistortCompositeMaterial.SetFloat(ID_ColorStd,  UseBt709 ? 1f : 0f);

                    // ——— 5) 合成 + 去畸变 → 全彩 RT（不改变分辨率/FOV）
                    Graphics.Blit(null, OutputRT, undistortCompositeMaterial);

                    // ——— 6) 按需回读（降频）
                    if (EnableReadback && (++_readbackFrame % Mathf.Max(1, ReadbackEveryNFrames) == 0))
                    {
                        AsyncGPUReadback.Request(OutputRT, 0, TextureFormat.RGBA32, OnReadback);
                    }

                    // ——— 7) 仍保留 CPU 灰度路径（可关）
                    if (_provider == null) _provider = new AprilTagColor32SpanProvider();
                    _provider.UpdateFromI422_Y(frame.Buffer_Y, lumaWidth, lumaHeight, lumaWidth);

                    // 回收帧
                    videoFrameQueue.RecycleStorage(frame);
                }
            }
        }
        #endregion

        #region Calibration & Readback
        /// <summary>读取 JSON 标定并设置到材质（OpenCV 风格）。不改变分辨率/FOV。</summary>
        void TryLoadCalibration(string path, int frameW, int frameH)
        {
            try
            {
                string full = path;
                if (!Path.IsPathRooted(full))
                    full = Path.Combine(Application.streamingAssetsPath, path);

                var jo = JObject.Parse(File.ReadAllText(full));

                var K = jo["camera_matrix"] as JArray;
                var D = jo["distortion_coefficients"] as JArray;

                float fx = (float)K[0][0];
                float fy = (float)K[1][1];
                float cx = (float)K[0][2];
                float cy = (float)K[1][2];

                float k1 = (float)D[0];
                float k2 = (float)D[1];
                float p1 = (float)D[2];
                float p2 = (float)D[3];
                float k3 = D.Count > 4 ? (float)D[4] : 0f;

                undistortCompositeMaterial.SetVector(ID_K,  new Vector4(fx, fy, cx, cy));
                undistortCompositeMaterial.SetVector(ID_D1, new Vector4(k1, k2, p1, p2));
                undistortCompositeMaterial.SetFloat (ID_K3, k3);

                var sz = jo["image_size"] as JArray;
                if (sz != null)
                {
                    int jw = (int)sz[0], jh = (int)sz[1];
                    if (jw != frameW || jh != frameH)
                        Debug.LogWarning($"[VideoRenderer] JSON image_size={jw}x{jh} 与当前帧 {frameW}x{frameH} 不一致；已按当前帧渲染（分辨率/FOV 不改变）。");
                }
            }
            catch (Exception ex)
            {
                Debug.LogError($"[VideoRenderer] LoadCalibration failed: {ex.Message}");
                // 失败：零畸变
                undistortCompositeMaterial.SetVector(ID_D1, Vector4.zero);
                undistortCompositeMaterial.SetFloat (ID_K3, 0f);
            }
        }

        // GPU 回读完成：从 RGBA32 生成灰度（BT.601 加权）并回调
        void OnReadback(AsyncGPUReadbackRequest req)
        {
            if (req.hasError) { Debug.LogWarning("GPU readback error"); return; }

            var data = req.GetData<Color32>();
            int n = data.Length;
            int w = OutputRT ? OutputRT.width : 0;
            int h = OutputRT ? OutputRT.height : 0;
            if (n != w * h || w == 0) return;

            // 转灰度（0.299R + 0.587G + 0.114B）
            byte[] gray = new byte[n];
            for (int i = 0; i < n; i++)
            {
                var c = data[i];
                int g = (int)(0.299f * c.r + 0.587f * c.g + 0.114f * c.b + 0.5f);
                if (g < 0) g = 0; else if (g > 255) g = 255;
                gray[i] = (byte)g;
            }

            OnGrayFrameReady?.Invoke(gray, w, h);
        }

        // 仅极端回退用，不建议：会 stall
        Texture2D _tmpCpuTex;
        #endregion

        #region Profiling & Fields (原有)
        private void ReportProfilerStatistics()
        {
            if (enableStatistics)
            {
                using (var _ = displayStatsMarker.Auto())
                {
                    IVideoFrameQueue stats = (IVideoFrameQueue)videoFrameQueue;
                    frameLoad = stats.QueuedFramesPerSecond.ToString("F2");
                    frameRender = stats.DequeuedFramesPerSecond.ToString("F2");
                    frameDrop = stats.DroppedFramesPerSecond.ToString("F2");
                    OnStatisticsReported.Invoke(frameLoad, frameRender, frameDrop);
                }
            }
        }

        IVideoSource source;
        RawImage monitor;
        Material videoMaterial;
        private VideoFrameQueue<I422VideoFrameStorage> videoFrameQueue = null;
        private float frameDuration;
        private float lastUpdateTime;

        private ProfilerMarker displayStatsMarker = new ProfilerMarker("DisplayStats");
        private ProfilerMarker loadTextureDataMarker = new ProfilerMarker("LoadTextureData");
        private ProfilerMarker uploadTextureToGpuMarker = new ProfilerMarker("UploadTextureToGPU");

        private Texture2D _textureY, _textureU, _textureV; // R8
        private int preFrameRate = 0;
        [SerializeField] private int lumaWidth;
        [SerializeField] private int lumaHeight;

        // 读回降频
        int _readbackFrame = 0;

        [Serializable] public class VideoRendererEvent : UnityEvent<string, string, string> { }
        #endregion

        #region tooltips
        const string aboutframrate = "为快速交换数据稍微设置大一些，推荐值大于 SDK 推流帧率即可";
        const string aboutstatistics = "开启后 OnStatisticsReported 事件才会进行分发，反之不会, 影响性能，建议关闭";
        const string aboutQueueSize = "一帧视频数据可观，减少队列容量，避免内存高涨，仅当停止播放时可调节";
        #endregion
    }
}
