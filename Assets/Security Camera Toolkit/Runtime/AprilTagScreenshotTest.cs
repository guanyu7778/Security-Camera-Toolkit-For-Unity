using System;
using System.Linq;
using UnityEngine;
using AprilTag;   // 来自 jp.keijiro.apriltag
using SecurityCameraToolkit.Runtime.Internal.AprilTag;
using PlasticGui;

public class AprilTagScreenshotTest : MonoBehaviour
{
    [Header("Input")]
    public Texture2D screenshot;           // 要检测的截图（Import 设置需 Read/Write Enabled）
    [Tooltip("Tag 实际黑框外边长（米）")]
    public float tagSizeMeters = 0.2f;
    [Tooltip("如果留空，将用当前相机FOV估算fx,fy,cx,cy")]
    public Vector4 customIntrinsics = Vector4.zero; // (fx, fy, cx, cy). 0 表示用相机估算

    [Header("Visual")]
    public Camera targetCamera;            // 用来叠加Cube的相机（为空就用 Camera.main）
    public Material cubeMat;               // 可选，给Cube的材质

    TagDetector _detector;
    TagDrawer tagDrawer;

    void Start()
    {
        if (screenshot == null)
        {
            Debug.LogError("[AprilTagScreenshotTest] 请赋值 screenshot（Texture2D，可读）");
            enabled = false;
            return;
        }

        if (!screenshot.isReadable)
        {
            Debug.LogError("[AprilTagScreenshotTest] screenshot 需要 Read/Write Enabled 才能被 CPU 读取。");
            enabled = false;
            return;
        }

        if (targetCamera == null)
            targetCamera = Camera.main;

        if (targetCamera == null)
        {
            Debug.LogError("[AprilTagScreenshotTest] 找不到 Camera。");
            enabled = false;
            return;
        }
        tagDrawer = new TagDrawer(cubeMat);
        _detector = new TagDetector(screenshot.width, screenshot.height, 1);

        // 静态图只跑一次
        RunOnceAndVisualize();
    }

    void OnDestroy()
    {
        _detector?.Dispose();
        _detector = null;
    }

    void RunOnceAndVisualize()
    {
        // 取像素（RGBA32）
        var pixels = screenshot.GetPixels32();
        ReadOnlySpan<Color32> pixelSpan = pixels;

        float horizontalFovDegrees = ComputeHorizontalFovDegrees();
        _detector.ProcessImage(pixelSpan, 60 * Mathf.Deg2Rad, Mathf.Max(0.001f, tagSizeMeters));
        Debug.Log($"[AprilTagScreenshotTest] 检测到 {_detector.DetectedTags.Count()} 个 AprilTag，FOV {Camera.main.fieldOfView * Mathf.Deg2Rad}°");
        foreach (var tag in _detector.DetectedTags)
        {
            tagDrawer.Draw(tag.ID, tag.Position, tag.Rotation, tagSizeMeters);
            var cube = GameObject.CreatePrimitive(PrimitiveType.Cube);
            cube.transform.position = tag.Position;
            cube.transform.rotation = tag.Rotation;
            cube.transform.localScale = Vector3.one * tagSizeMeters;
            if (cubeMat != null)
                cube.GetComponent<Renderer>().material = cubeMat;
            Debug.Log($"[AprilTagScreenshotTest] Tag ID {tag.ID} 位于 {tag.Position}, 旋转 {tag.Rotation.eulerAngles}");
        }
    }

    float ComputeHorizontalFovDegrees()
    {
        if (customIntrinsics.x > 0f)
        {
            float fx = customIntrinsics.x;
            float width = Mathf.Max(1, screenshot.width);
            float fovRad = 2f * Mathf.Atan(width / (2f * fx));
            return fovRad * Mathf.Rad2Deg;
        }

        float aspect = targetCamera.aspect > 0f
            ? targetCamera.aspect
            : (float)screenshot.width / Mathf.Max(1, screenshot.height);

        float verticalRad = targetCamera.fieldOfView * Mathf.Deg2Rad;
        float horizontalRad = 2f * Mathf.Atan(Mathf.Tan(verticalRad * 0.5f) * aspect);
        return horizontalRad * Mathf.Rad2Deg;
    }
}
