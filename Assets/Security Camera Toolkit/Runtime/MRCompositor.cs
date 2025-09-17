using System.IO;
using UnityEngine;
using UnityEngine.UI;

[DefaultExecutionOrder(-100)]
public class MRCompositor : MonoBehaviour
{
    [Header("Calibration JSON in StreamingAssets")]
    [Tooltip("文件名（位于 Assets/StreamingAssets/ 下）")]
    public string calibrationFileName = "calibration.json";

    [Header("Cameras")]
    public Camera virtualCamera;    // 只渲染虚拟层 → RT（透明背景）
    public Camera backgroundCamera; // 你的背景视频相机（可留空）

    [Header("Composite Material (Distort)")]
    [Tooltip("使用 Shader: Hidden/DistortVirtualToLens 的材质；若留空，会自动创建")]
    public Material distortMaterial;

    [Header("Output (UI)")]
    public RawImage outputRawImage;  // 全屏 RawImage 合成
    public Canvas outputCanvas;      // 若为空将自动创建

    [Header("RenderTexture Size (optional)")]
    public Vector2Int virtualRTSize = new Vector2Int(0, 0); // 留空则用 JSON 的 image_size

    [Header("Options")]
    public bool matchJsonProjection = true; // 优先使用 JSON 的 4x4 投影；否则回退内参推导
    public Color virtualClearColor = new Color(0, 0, 0, 0);

    private RenderTexture _virtualRT;
    private CalibData _calib;

    private void Awake()
    {
        if (virtualCamera == null)
        {
            Debug.LogError("[MRCompositor] virtualCamera is null.");
            enabled = false; return;
        }

        // 若未手动指定材质，则运行时自动创建
        if (distortMaterial == null)
        {
            var sh = Shader.Find("Hidden/DistortVirtualToLens");
            if (sh == null)
            {
                Debug.LogError("Shader 'Hidden/DistortVirtualToLens' not found. " +
                               "确认 DistortVirtualToLens.shader 在 Assets/ 且 shader 名称匹配；或改名去掉 Hidden/。");
                enabled = false; return;
            }
            distortMaterial = new Material(sh) { name = "DistortVirtualToLens (runtime)" };
        }

        // 从 StreamingAssets 读取 JSON（Windows：直接文件 IO）
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
        // 1) RT 分辨率
        Vector2Int whFromJson = CalibUtil.GetImageSize(calib);
        int w = virtualRTSize.x > 0 ? virtualRTSize.x : whFromJson.x;
        int h = virtualRTSize.y > 0 ? virtualRTSize.y : whFromJson.y;

        // 2) 创建虚拟相机 RT
        _virtualRT = new RenderTexture(w, h, 0, RenderTextureFormat.ARGB32)
        {
            name = "VirtualRT",
            useMipMap = false,
            autoGenerateMips = false
        };
        _virtualRT.Create();

        // 3) 虚拟相机配置（透明）
        virtualCamera.targetTexture = _virtualRT;
        virtualCamera.clearFlags = CameraClearFlags.SolidColor;
        virtualCamera.backgroundColor = virtualClearColor;
        virtualCamera.allowHDR = false;
        virtualCamera.allowMSAA = false;

        if (matchJsonProjection)
        {
            if (CalibUtil.TryGetProjectionMatrix(calib, out Matrix4x4 proj, near: 0.01f, far: 100f))
            {
                virtualCamera.usePhysicalProperties = false;
                virtualCamera.projectionMatrix = proj;
            }
            else
            {
                Debug.LogWarning("[MRCompositor] No valid unity_projection_matrix and cannot build from intrinsics. Using default camera projection.");
            }
        }

        // 4) 给畸变材质喂参数（像素单位）
        Vector4 intr = CalibUtil.GetIntrinsicsXYCXCY(calib);   // (fx, fy, cx, cy)
        Vector4 k123 = CalibUtil.GetRadialK1K2K3(calib);       // (k1, k2, k3, 0)
        Vector4 p12 = CalibUtil.GetTangentialP1P2(calib);     // (p1, p2, 0, 0)
        Vector4 texSize = new Vector4(w, h, 1f / w, 1f / h);   // 与虚拟RT/视频一致

        distortMaterial.SetVector("_CamIntrinsics", intr);
        distortMaterial.SetVector("_DistRadial", k123);
        distortMaterial.SetVector("_DistTangential", p12);
        distortMaterial.SetVector("_TexSize", texSize);

        // 5) 输出叠加 UI
        EnsureOutputUI();
        outputRawImage.texture = _virtualRT;
        outputRawImage.material = distortMaterial;
        outputRawImage.color = Color.white;
        outputRawImage.raycastTarget = false;

        Debug.Log($"[MRCompositor] Ready. RT={w}x{h}, fx={intr.x:F3}, fy={intr.y:F3}, cx={intr.z:F3}, cy={intr.w:F3}");
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

        // 全屏
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
}
