using System.IO;
using UnityEngine;
using UnityEngine.UI;

[DefaultExecutionOrder(-100)]
public class MRCompositor : MonoBehaviour
{
    [Header("Calibration JSON in StreamingAssets")]
    [Tooltip("�ļ�����λ�� Assets/StreamingAssets/ �£�")]
    public string calibrationFileName = "calibration.json";

    [Header("Cameras")]
    public Camera virtualCamera;    // ֻ��Ⱦ����� �� RT��͸��������
    public Camera backgroundCamera; // ��ı�����Ƶ����������գ�

    [Header("Composite Material (Distort)")]
    [Tooltip("ʹ�� Shader: Hidden/DistortVirtualToLens �Ĳ��ʣ������գ����Զ�����")]
    public Material distortMaterial;

    [Header("Output (UI)")]
    public RawImage outputRawImage;  // ȫ�� RawImage �ϳ�
    public Canvas outputCanvas;      // ��Ϊ�ս��Զ�����

    [Header("RenderTexture Size (optional)")]
    public Vector2Int virtualRTSize = new Vector2Int(0, 0); // �������� JSON �� image_size

    [Header("Options")]
    public bool matchJsonProjection = true; // ����ʹ�� JSON �� 4x4 ͶӰ����������ڲ��Ƶ�
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

        // ��δ�ֶ�ָ�����ʣ�������ʱ�Զ�����
        if (distortMaterial == null)
        {
            var sh = Shader.Find("Hidden/DistortVirtualToLens");
            if (sh == null)
            {
                Debug.LogError("Shader 'Hidden/DistortVirtualToLens' not found. " +
                               "ȷ�� DistortVirtualToLens.shader �� Assets/ �� shader ����ƥ�䣻�����ȥ�� Hidden/��");
                enabled = false; return;
            }
            distortMaterial = new Material(sh) { name = "DistortVirtualToLens (runtime)" };
        }

        // �� StreamingAssets ��ȡ JSON��Windows��ֱ���ļ� IO��
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
        // 1) RT �ֱ���
        Vector2Int whFromJson = CalibUtil.GetImageSize(calib);
        int w = virtualRTSize.x > 0 ? virtualRTSize.x : whFromJson.x;
        int h = virtualRTSize.y > 0 ? virtualRTSize.y : whFromJson.y;

        // 2) ����������� RT
        _virtualRT = new RenderTexture(w, h, 0, RenderTextureFormat.ARGB32)
        {
            name = "VirtualRT",
            useMipMap = false,
            autoGenerateMips = false
        };
        _virtualRT.Create();

        // 3) ����������ã�͸����
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

        // 4) ���������ι���������ص�λ��
        Vector4 intr = CalibUtil.GetIntrinsicsXYCXCY(calib);   // (fx, fy, cx, cy)
        Vector4 k123 = CalibUtil.GetRadialK1K2K3(calib);       // (k1, k2, k3, 0)
        Vector4 p12 = CalibUtil.GetTangentialP1P2(calib);     // (p1, p2, 0, 0)
        Vector4 texSize = new Vector4(w, h, 1f / w, 1f / h);   // ������RT/��Ƶһ��

        distortMaterial.SetVector("_CamIntrinsics", intr);
        distortMaterial.SetVector("_DistRadial", k123);
        distortMaterial.SetVector("_DistTangential", p12);
        distortMaterial.SetVector("_TexSize", texSize);

        // 5) ������� UI
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

        // ȫ��
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
