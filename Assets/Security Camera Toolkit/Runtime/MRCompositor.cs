// MRCompositor.cs
// 单文件版：读取标定 → 精确视锥 → 虚拟层渲染到RT → 按畸变“扭弯”叠加到视频
// 依赖 Newtonsoft.Json（Package: com.unity.nuget.newtonsoft-json）

using System.IO;
using UnityEngine;
using UnityEngine.UI;
using Newtonsoft.Json;

[DefaultExecutionOrder(-100)]
public class MRCompositor : MonoBehaviour
{
    [Header("Calibration JSON in StreamingAssets")]
    [Tooltip("位于 Assets/StreamingAssets/ 下的文件名，比如 calibration.json")]
    public string calibrationFileName = "calibration.json";

    [Header("Cameras")]
    public Camera virtualCamera;    // 只渲染虚拟层 → RT（透明）
    public Camera backgroundCamera; // 你的背景视频相机（可不填）

    [Header("Composite Material (Distort)")]
    [Tooltip("使用 Shader: MR/DistortVirtualToLens（或 Hidden/DistortVirtualToLens）；为空会自动创建")]
    public Material distortMaterial;

    [Header("Output (UI)")]
    public RawImage outputRawImage;  // 全屏 RawImage 合成 
    public Canvas outputCanvas;      // 若为空将自动创建

    [Header("RenderTexture Size (optional)")]
    public Vector2Int virtualRTSize = new Vector2Int(0, 0); // 留空则用 JSON 的 image_size

    [Header("Projection")]
    [Tooltip("启用“精确视锥”：反算屏幕四边在无畸变空间的范围，像素级铺满且对齐")]
    public bool exactCover = true;
    [Range(16, 1024)]
    public int samplesPerEdge = 64; // 精确视锥边界采样点数
    public float nearClip = 0.01f;
    public float farClip = 100f;

    [Header("Virtual Camera")]
    public Color virtualClearColor = new Color(0, 0, 0, 0); // 透明背景

    // 内部资源
    private RenderTexture _virtualRT;
    private CalibData _calib;

    #region ==== Calib Types & Utils (内嵌) ====

    [System.Serializable]
    public class CalibData
    {
        [JsonProperty("image_size")] public int[] image_size;                // [width, height]
        [JsonProperty("camera_matrix")] public float[][] camera_matrix;           // 3x3
        [JsonProperty("distortion_coefficients")] public float[] distortion_coefficients;   // [k1,k2,p1,p2,k3]
        [JsonProperty("unity_projection_matrix")] public float[][] unity_projection_matrix; // 4x4（可能缺省）
    }

    static class CalibUtil
    {
        public static CalibData ParseJson(string jsonText)
        {
            var data = JsonConvert.DeserializeObject<CalibData>(jsonText);
            if (data == null) throw new System.Exception("[Calib] JSON parse failed (null).");
            return data;
        }

        public static Vector2Int GetImageSize(CalibData d)
        {
            if (d != null && d.image_size != null && d.image_size.Length >= 2)
                return new Vector2Int(d.image_size[0], d.image_size[1]);
            return new Vector2Int(Screen.width, Screen.height);
        }

        public static Vector4 GetIntrinsicsXYCXCY(CalibData d)
        {
            if (d == null || d.camera_matrix == null || d.camera_matrix.Length < 2 ||
                d.camera_matrix[0].Length < 3 || d.camera_matrix[1].Length < 3)
                throw new System.Exception("[Calib] camera_matrix missing or malformed (expect 3x3).");

            float fx = d.camera_matrix[0][0];
            float fy = d.camera_matrix[1][1];
            float cx = d.camera_matrix[0][2];
            float cy = d.camera_matrix[1][2];
            return new Vector4(fx, fy, cx, cy);
        }

        public static Vector4 GetRadialK1K2K3(CalibData d)
        {
            if (d == null || d.distortion_coefficients == null || d.distortion_coefficients.Length < 5)
                throw new System.Exception("[Calib] distortion_coefficients malformed (need [k1,k2,p1,p2,k3]).");
            return new Vector4(d.distortion_coefficients[0], d.distortion_coefficients[1], d.distortion_coefficients[4], 0f);
        }

        public static Vector4 GetTangentialP1P2(CalibData d)
        {
            if (d == null || d.distortion_coefficients == null || d.distortion_coefficients.Length < 4)
                throw new System.Exception("[Calib] distortion_coefficients missing p1/p2.");
            return new Vector4(d.distortion_coefficients[2], d.distortion_coefficients[3], 0f, 0f);
        }

        // 尝试直接用 JSON 的 4x4 投影（若存在）
        public static bool TryGetJsonProjection(CalibData d, out Matrix4x4 proj)
        {
            proj = Matrix4x4.identity;
            if (d == null || d.unity_projection_matrix == null || d.unity_projection_matrix.Length != 4)
                return false;
            for (int r = 0; r < 4; r++)
                if (d.unity_projection_matrix[r] == null || d.unity_projection_matrix[r].Length != 4)
                    return false;

            var m = d.unity_projection_matrix;
            proj = new Matrix4x4();
            proj.m00 = m[0][0]; proj.m01 = m[0][1]; proj.m02 = m[0][2]; proj.m03 = m[0][3];
            proj.m10 = m[1][0]; proj.m11 = m[1][1]; proj.m12 = m[1][2]; proj.m13 = m[1][3];
            proj.m20 = m[2][0]; proj.m21 = m[2][1]; proj.m22 = m[2][2]; proj.m23 = m[2][3];
            proj.m30 = m[3][0]; proj.m31 = m[3][1]; proj.m32 = m[3][2]; proj.m33 = m[3][3];
            return true;
        }

        // 由内参直接构造（没有“精确覆盖”）
        public static Matrix4x4 BuildFrustumFromIntrinsics(Vector4 intr, Vector2Int wh, float near, float far)
        {
            float fx = intr.x, fy = intr.y, cx = intr.z, cy = intr.w;
            float w = wh.x, h = wh.y;

            float l = -near * (cx) / fx;
            float r = near * (w - cx) / fx;
            float t = near * (cy) / fy;
            float b = -near * (h - cy) / fy;

            return Matrix4x4.Frustum(l, r, b, t, near, far);
        }
    }

    #endregion

    private void Awake()
    {
        if (virtualCamera == null)
        {
            Debug.LogError("[MRCompositor] virtualCamera is null."); enabled = false; return;
        }

        // 自动创建材质（Shader 名优先 MR/DistortVirtualToLens，找不到再用 Hidden/DistortVirtualToLens）
        if (distortMaterial == null)
        {
            Shader sh = Shader.Find("MR/DistortVirtualToLens");
            if (sh == null) sh = Shader.Find("Hidden/DistortVirtualToLens");
            if (sh == null)
            {
                Debug.LogError("[MRCompositor] Shader 'MR/DistortVirtualToLens' not found. 请确认 shader 在 Assets/ 且命名匹配。");
                enabled = false; return;
            }
            distortMaterial = new Material(sh) { name = "DistortVirtualToLens (runtime)" };
        }

        // 读取 JSON（Windows：文件 IO）
        string path = Path.Combine(Application.streamingAssetsPath, calibrationFileName);
        if (!File.Exists(path))
        {
            Debug.LogError($"[MRCompositor] Calibration file not found: {path}");
            enabled = false; return;
        }

        string jsonText = File.ReadAllText(path);
        try
        {
            _calib = CalibUtil.ParseJson(jsonText);
        }
        catch (System.Exception ex)
        {
            Debug.LogError($"[MRCompositor] Parse JSON failed: {ex.Message}");
            enabled = false; return;
        }

        InitializeWithCalib(_calib);
    }

    private void InitializeWithCalib(CalibData calib)
    {
        // ---- 1) 分辨率 ----
        Vector2Int whFromJson = CalibUtil.GetImageSize(calib);
        int w = virtualRTSize.x > 0 ? virtualRTSize.x : whFromJson.x;
        int h = virtualRTSize.y > 0 ? virtualRTSize.y : whFromJson.y;

        // ---- 2) 创建虚拟相机 RT ----
        _virtualRT = new RenderTexture(w, h, 0, RenderTextureFormat.ARGB32)
        {
            name = "VirtualRT",
            useMipMap = false,
            autoGenerateMips = false
        };
        _virtualRT.Create();

        // ---- 3) 虚拟相机配置（透明） ----
        virtualCamera.targetTexture = _virtualRT;
        virtualCamera.clearFlags = CameraClearFlags.SolidColor;
        virtualCamera.backgroundColor = virtualClearColor;
        virtualCamera.allowHDR = false;
        virtualCamera.allowMSAA = false;

        // ---- 4) 设置虚拟相机投影 ----
        var intr = CalibUtil.GetIntrinsicsXYCXCY(calib);
        var k123 = CalibUtil.GetRadialK1K2K3(calib);
        var p12 = CalibUtil.GetTangentialP1P2(calib);
        var wh = new Vector2Int(w, h);

        Matrix4x4 proj = Matrix4x4.identity;
        bool projOK = false;
        bool hasCoverBounds = false;
        Vector2 undistMin = Vector2.zero;
        Vector2 undistMax = Vector2.zero;

        if (exactCover)
        {
            proj = BuildFrustumCoveringDistortedImage(intr, k123, p12, wh, nearClip, farClip, Mathf.Max(16, samplesPerEdge), out undistMin, out undistMax);
            projOK = true;
            hasCoverBounds = true;
        }
        else if (CalibUtil.TryGetJsonProjection(calib, out proj))
        {
            projOK = true;
        }
        else
        {
            proj = CalibUtil.BuildFrustumFromIntrinsics(intr, wh, nearClip, farClip);
            projOK = true;
        }

        if (projOK)
        {
            virtualCamera.usePhysicalProperties = false;
            virtualCamera.projectionMatrix = proj;
        }

        Vector4 samplingIntr = intr;
        if (hasCoverBounds)
        {
            float spanX = undistMax.x - undistMin.x;
            float spanY = undistMax.y - undistMin.y;
            if (spanX > 1e-6f && spanY > 1e-6f)
            {
                float fxVirtual = w / spanX;
                float fyVirtual = h / spanY;
                float cxVirtual = -undistMin.x * fxVirtual;
                float cyVirtual = -undistMin.y * fyVirtual;
                samplingIntr = new Vector4(fxVirtual, fyVirtual, cxVirtual, cyVirtual);
            }
        }


        // ---- 5) 给畸变材质喂参数（像素单位）----
        Vector4 texSize = new Vector4(w, h, 1f / w, 1f / h);
        distortMaterial.SetVector("_CamIntrinsics", intr);   // (fx, fy, cx, cy)
        distortMaterial.SetVector("_DistRadial", k123);   // (k1, k2, k3, 0)
        distortMaterial.SetVector("_DistTangential", p12);    // (p1, p2, 0, 0)
        distortMaterial.SetVector("_VirtualIntrinsics", samplingIntr);
        distortMaterial.SetVector("_TexSize", texSize);

        // **若你的 shader 属性名不是 _MainTex（比如 _SrcTex），可以加下面一行保证两边都绑定**
        distortMaterial.SetTexture("_MainTex", _virtualRT);
        distortMaterial.SetTexture("_SrcTex", _virtualRT);

        // ---- 6) 输出叠加 UI ----
        EnsureOutputUI();
        outputRawImage.texture = _virtualRT;      // RawImage 会把它绑定到 _MainTex
        outputRawImage.material = distortMaterial;
        outputRawImage.color = Color.white;
        outputRawImage.raycastTarget = false;

        Debug.Log($"[MRCompositor] Ready. RT={w}x{h} | fx={intr.x:F3} fy={intr.y:F3} cx={intr.z:F3} cy={intr.w:F3} | exactCover={exactCover} samples={samplesPerEdge}");

        Debug.Log($"[Diag] RT={w}x{h}, TexSize={w}x{h}, Intr={intr}, exactCover={exactCover}");
    }

    private void EnsureOutputUI()
    {
        if (outputRawImage != null) return;

        if (outputCanvas == null)
        {
            var goCanvas = new GameObject("MRCompositeCanvas", typeof(Canvas), typeof(CanvasScaler), typeof(GraphicRaycaster));
            outputCanvas = goCanvas.GetComponent<Canvas>();
            outputCanvas.renderMode = RenderMode.ScreenSpaceOverlay;

            var scaler = goCanvas.GetComponent<CanvasScaler>();
            scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
            scaler.referenceResolution = new Vector2(1920, 1080);
        }

        var goImg = new GameObject("MRCompositeImage", typeof(RawImage));
        goImg.transform.SetParent(outputCanvas.transform, false);
        outputRawImage = goImg.GetComponent<RawImage>();

        // 全屏铺满
        var rt = outputRawImage.rectTransform;
        rt.anchorMin = Vector2.zero;
        rt.anchorMax = Vector2.one;
        rt.offsetMin = Vector2.zero;
        rt.offsetMax = Vector2.zero;
    }

    private void OnDestroy()
    {
        if (_virtualRT != null)
        {
            if (_virtualRT.IsCreated()) _virtualRT.Release();
            Destroy(_virtualRT);
        }
    }

    #region ==== 精确视锥：反算边界并构造 Frustum ====

    // 计算：要覆盖整个“畸变后”的屏幕矩形 [0..w]x[0..h]，
    // 在“无畸变归一化”平面上需要的最小/最大范围，然后构建离轴视锥。
    static Matrix4x4 BuildFrustumCoveringDistortedImage(
        Vector4 intr, Vector4 radial, Vector4 tangential, Vector2Int wh,
        float near, float far, int samplesPerEdge,
        out Vector2 undistMin, out Vector2 undistMax)
    {
        float fx = intr.x, fy = intr.y, cx = intr.z, cy = intr.w;
        int w = Mathf.Max(1, wh.x), h = Mathf.Max(1, wh.y);
        samplesPerEdge = Mathf.Max(16, samplesPerEdge);

        float minXn = float.PositiveInfinity, maxXn = float.NegativeInfinity;
        float minYn = float.PositiveInfinity, maxYn = float.NegativeInfinity;

        System.Action<System.Func<int, Vector2>> sampleEdge = edgePointFunc =>
        {
            for (int i = 0; i < samplesPerEdge; i++)
            {
                float t = (samplesPerEdge == 1) ? 0.0f : (i / (samplesPerEdge - 1f));
                Vector2 p = edgePointFunc(i); // 像素坐标（畸变后）
                float xd = (p.x - cx) / fx;
                float yd = (p.y - cy) / fy;
                Vector2 xu = InverseDistortNorm(new Vector2(xd, yd), radial, tangential); // 无畸变归一化
                minXn = Mathf.Min(minXn, xu.x);
                maxXn = Mathf.Max(maxXn, xu.x);
                minYn = Mathf.Min(minYn, xu.y);
                maxYn = Mathf.Max(maxYn, xu.y);
            }
        };

        // 四条边：上、下、左、右（像素坐标原点左上，y向下）
        sampleEdge(i => new Vector2(Mathf.Lerp(0, w, i / (samplesPerEdge - 1f)), 0)); // top
        sampleEdge(i => new Vector2(Mathf.Lerp(0, w, i / (samplesPerEdge - 1f)), h)); // bottom
        sampleEdge(i => new Vector2(0, Mathf.Lerp(0, h, i / (samplesPerEdge - 1f)))); // left
        sampleEdge(i => new Vector2(w, Mathf.Lerp(0, h, i / (samplesPerEdge - 1f)))); // right

        // 注意：像素坐标 y 向下；Unity 投影 y 向上
        float l = near * minXn;
        float r = near * maxXn;
        float t = -near * minYn;   // top = -near * (最小 yn)
        float b = -near * maxYn;   // bottom = -near * (最大 yn)

        undistMin = new Vector2(minXn, minYn);
        undistMax = new Vector2(maxXn, maxYn);
        return Matrix4x4.Frustum(l, r, b, t, near, far);
    }

    // 与 shader 同步的“畸变逆”迭代（输入/输出均为归一化坐标）
    static Vector2 InverseDistortNorm(Vector2 xd, Vector4 radial, Vector4 tangential)
    {
        float k1 = radial.x, k2 = radial.y, k3 = radial.z;
        float p1 = tangential.x, p2 = tangential.y;

        Vector2 x = xd; // 初值
        for (int i = 0; i < 5; i++)
        {
            float r2 = x.x * x.x + x.y * x.y;
            float r4 = r2 * r2;
            float r6 = r4 * r2;
            float radialF = 1f + k1 * r2 + k2 * r4 + k3 * r6;
            float xt = 2f * p1 * x.x * x.y + p2 * (r2 + 2f * x.x * x.x);
            float yt = p1 * (r2 + 2f * x.y * x.y) + 2f * p2 * x.x * x.y;
            Vector2 f = new Vector2(x.x * radialF + xt, x.y * radialF + yt) - xd;

            // 数值雅可比
            float eps = 1e-3f;
            Vector2 dx = new Vector2(eps, 0), dy = new Vector2(0, eps);
            Vector2 fx = DistortForwardNorm(x + dx, radial, tangential) - DistortForwardNorm(x - dx, radial, tangential);
            Vector2 fy = DistortForwardNorm(x + dy, radial, tangential) - DistortForwardNorm(x - dy, radial, tangential);
            float a = fx.x / (2 * eps), b = fy.x / (2 * eps);
            float c = fx.y / (2 * eps), d = fy.y / (2 * eps);
            float det = a * d - b * c + 1e-9f;
            Vector2 delta = new Vector2((d * f.x - b * f.y) / det,
                                        (-c * f.x + a * f.y) / det);
            x -= delta;
            if (delta.sqrMagnitude < 1e-14f) break;
        }
        return x;
    }

    static Vector2 DistortForwardNorm(Vector2 x, Vector4 radial, Vector4 tangential)
    {
        float k1 = radial.x, k2 = radial.y, k3 = radial.z;
        float p1 = tangential.x, p2 = tangential.y;
        float r2 = x.x * x.x + x.y * x.y;
        float r4 = r2 * r2;
        float r6 = r4 * r2;
        float radialF = 1f + k1 * r2 + k2 * r4 + k3 * r6;
        float xt = 2f * p1 * x.x * x.y + p2 * (r2 + 2f * x.x * x.x);
        float yt = p1 * (r2 + 2f * x.y * x.y) + 2f * p2 * x.x * x.y;
        return new Vector2(x.x * radialF + xt, x.y * radialF + yt);
    }

    #endregion
}
