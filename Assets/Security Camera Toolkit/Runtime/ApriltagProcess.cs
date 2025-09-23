using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using SecurityCameraToolkit.Runtime.Internal.AprilTag;
using UnityEngine;
using UnityEngine.UI;
using zFramework.Media;

public class ApriltagProcess : MonoBehaviour
{
    [Header("Input / Debug")]
    [SerializeField] private VideoRenderer videoRenderer;
    [SerializeField] private RawImage rawImage;
    [SerializeField] private bool debugVideo = false;


    [Header("Detection Params")]
    [Tooltip("摄像头垂直方向FOV（度）")]
    public float fov = 35f;

    [Tooltip("AprilTag 实际物理边长（米），用于将位姿解算到米制尺度")]
    public float tagSizeMeters = 0.20f;

    [Tooltip("AprilTag 实际物理边长（米），用于将位姿解算到米制尺度")]
    public float cubeSizeMeters = 0.20f;

    [Header("Cube Rendering")]
    [SerializeField] private Material cubeMat;
    [Tooltip("未看到多少帧后移除对应的 Cube")]
    public int staleFramesToRemove = 20;

    [Tooltip("是否开启位置/旋转平滑")]
    public bool smooth = true;

    [Tooltip("位置平滑时间常数（秒），越大越稳，越小越跟随")]
    public float positionSmoothTime = 0.05f;

    [Tooltip("旋转平滑插值因子（0-1），建议 0.2~0.4")]
    public float rotationLerp = 0.25f;
    [Tooltip("渲染摄像机（为空就用 Camera.main）")]
    public Camera targetCamera;
     [Tooltip("摄像机标定文件")]
    public string cameraCalibFile = "camera_calib.json";

    AprilTag.TagDetector _detector;

    // 每个 Tag 的可视化对象与其状态
    class TagCube
    {
        public GameObject go;
        public Transform t;
        public Vector3 vel;            // 给 SmoothDamp 用
        public int lastSeenFrame;
    }

    readonly Dictionary<int, TagCube> _cubes = new();
    Transform _container; // 作为所有 Cube 的父物体，便于管理

    void Start()
    {
        // 这里的分辨率/并行度请与你的视频源一致；你之前用的是 1920x1080, threads=2
        _detector = new AprilTag.TagDetector(1920, 1080, 2);

        _container = new GameObject("AprilTagCubes").transform;
        _container.SetParent(transform, false); // 放到当前物体（通常是摄像机）之下
        if (targetCamera == null)
            targetCamera = Camera.main;
        ApplyProjectionFromConfig(targetCamera, out var fovH, out var fovV, 0.01f, 100f, cameraCalibFile);
        this.fov = fovV;
    }

    void OnDestroy()
    {
        if (_detector != null)
        {
            _detector.Dispose();
            _detector = null;
        }

        // 清理临时生成的可视化对象
        foreach (var kv in _cubes)
        {
            if (kv.Value.go) Destroy(kv.Value.go);
        }
        _cubes.Clear();
    }

    void Update()
    {
        // 1) 取帧
        ReadOnlySpan<Color32> span = videoRenderer._provider.GetLatestColor32Span(out int w, out int h);
        if (span.IsEmpty)
        {
            // 没有帧就顺便做一下超时清理
            RemoveStaleCubes();
            return;
        }

        if (span.Length != w * h)
        {
            Debug.LogError($"Span length mismatch: {span.Length} != {w * h}");
            RemoveStaleCubes();
            return;
        }

        if (debugVideo && rawImage != null)
        {
            var tex = videoRenderer._provider.GetLatestTexture2D();
            if (tex != null) rawImage.texture = tex;
        }

        // 2) 检测帧（注意：第二个参数是垂直 FOV 的弧度，第三个是 tag 物理尺寸（米））
        _detector.ProcessImage(span, fov * Mathf.Deg2Rad, tagSizeMeters);

        // 3) 更新/创建 Cube
        foreach (var tag in _detector.DetectedTags)
        {
            // tag.ID 唯一标识；tag.Position / tag.Rotation 通常是“相机局部坐标系”下的位姿
            // 我们把 Cube 放到本脚本所在对象（建议是相机）之下，并设置 localPose
            if (!_cubes.TryGetValue(tag.ID, out var tc) || tc == null || tc.go == null)
            {
                tc = CreateCube(tag.ID);
                _cubes[tag.ID] = tc;
            }

            UpdateCubePose(tc, tag.Position, tag.Rotation);
            tc.lastSeenFrame = Time.frameCount;
        }

        // 4) 清理长时间未看到的 Tag
        RemoveStaleCubes();

        // 可选：输出调试信息
        // Debug.Log($"Detected {_detector.DetectedTags.Count()} tags");
    }

    TagCube CreateCube(int id)
    {
        var go = GameObject.CreatePrimitive(PrimitiveType.Cube);
        go.name = $"TagCube_{id}";
        go.transform.SetParent(_container, false);

        // 默认 Unity 立方体是 1x1x1 米；我们希望 Cube 尺寸与 Tag 尺寸一致（边长 = cubeSizeMeters）
        go.transform.localScale = Vector3.one * cubeSizeMeters;

        // 材质
        if (cubeMat != null)
        {
            var mr = go.GetComponent<MeshRenderer>();
            mr.sharedMaterial = cubeMat;
        }

        // 给它一个小的可视化标签（可选）
        var label = new GameObject("Label");
        label.transform.SetParent(go.transform, false);
        label.transform.localPosition = new Vector3(0, 0.6f * tagSizeMeters, 0);

        // 用 TextMesh 显示 ID（无需 Canvas）
        var tm = label.AddComponent<TextMesh>();
        tm.text = $"ID:{id}";
        tm.fontSize = 64;
        tm.characterSize = 0.01f;
        tm.anchor = TextAnchor.MiddleCenter;
        tm.alignment = TextAlignment.Center;

        return new TagCube { go = go, t = go.transform, vel = Vector3.zero, lastSeenFrame = Time.frameCount };
    }

    void UpdateCubePose(TagCube tc, Vector3 localPos, Quaternion localRot)
    {
        if (!smooth)
        {
            tc.t.localPosition = localPos;
            tc.t.localRotation = localRot;
            tc.t.transform.Translate(tc.t.forward * -1 * 0.5f * cubeSizeMeters, Space.World);
            return;
        }

        // 位置平滑（相机坐标系下）
        tc.t.localPosition = Vector3.SmoothDamp(
            tc.t.localPosition,
            localPos,
            ref tc.vel,
            Mathf.Max(0.0001f, positionSmoothTime),
            Mathf.Infinity,
            Time.deltaTime
        );

        // 旋转平滑
        tc.t.localRotation = Quaternion.Slerp(tc.t.localRotation, localRot, Mathf.Clamp01(rotationLerp));
    }

    void RemoveStaleCubes()
    {
        // 找出需要移除的
        var toRemove = new List<int>();
        var now = Time.frameCount;

        foreach (var kv in _cubes)
        {
            var tc = kv.Value;
            if (tc == null || tc.go == null)
            {
                toRemove.Add(kv.Key);
                continue;
            }

            if (now - tc.lastSeenFrame > staleFramesToRemove)
            {
                Destroy(tc.go);
                toRemove.Add(kv.Key);
            }
        }

        // 从字典移除
        foreach (var id in toRemove)
            _cubes.Remove(id);
    }

    /// <summary>
    /// 已知 tag 在相机坐标系下的位姿 (tagPosCam, tagRotCam),
    /// 反算得到相机在 tag 坐标系下的位姿 (camPosInTag, camRotInTag).
    /// 数学：X_tag = R_tc * X_cam + p_tc
    ///  =>  X_cam = R_ct * X_tag + p_ct, 其中 R_ct = R_tc^T, p_ct = -R_tc^T * p_tc
    /// 在 Unity 中：R_ct = Inverse(tagRotCam), p_ct = -(R_ct * tagPosCam)
    /// </summary>
    public static void GetCameraPoseInTagFrame(
        Vector3 tagPosCam, Quaternion tagRotCam,
        out Vector3 camPosInTag, out Quaternion camRotInTag)
    {
        camRotInTag = Quaternion.Inverse(tagRotCam);
        camPosInTag = -(camRotInTag * tagPosCam);
    }

    public static void SaveCameraPose(Vector3 pos, Quaternion rot, string fileName = "camera_pose.json")
    {
        var data = new CameraPoseData
        {
            position = new float[] { pos.x, pos.y, pos.z },
            rotation = new float[] { rot.x, rot.y, rot.z, rot.w }
        };

        // Newtonsoft.Json 支持格式化缩进
        string json = JsonConvert.SerializeObject(data, Formatting.Indented);

        string path = Path.Combine(Application.streamingAssetsPath, fileName);
        File.WriteAllText(path, json);

        Debug.Log($"[SaveCameraPose] Saved to {path}");
    }
    public bool LoadCameraPose(out Vector3 pos, out Quaternion rot, string fileName = "camera_pose.json")
    {
        string path = Path.Combine(Application.streamingAssetsPath, fileName);

        if (!File.Exists(path))
        {
            Debug.LogWarning($"[LoadCameraPose] File not found: {path}");
            pos = Vector3.zero;
            rot = Quaternion.identity;
            return false;
        }

        string json = File.ReadAllText(path);
        var data = JsonConvert.DeserializeObject<CameraPoseData>(json);

        pos = new Vector3(data.position[0], data.position[1], data.position[2]);
        rot = new Quaternion(data.rotation[0], data.rotation[1], data.rotation[2], data.rotation[3]);

        Debug.Log($"[LoadCameraPose] Loaded from {path}");
        return true;
    }
    
    /// <summary>
    /// 从 StreamingAssets/配置文件 读取投影参数并应用到目标摄像机。
    /// 优先：unity_projection_matrix；否则用 camera_matrix + image_size 构建近似投影。
    /// 同时计算（或读取）横/竖 FOV（度）。
    /// </summary>
    /// <param name="targetCam">要设置的目标摄像机</param>
    /// <param name="fileName">配置文件名（位于 StreamingAssets）</param>
    /// <param name="near">投影近裁剪面</param>
    /// <param name="far">投影远裁剪面</param>
    /// <param name="hFovDeg">输出：横向 FOV（度）</param>
    /// <param name="vFovDeg">输出：纵向 FOV（度）</param>
    /// <returns>成功/失败</returns>
    public bool ApplyProjectionFromConfig(
        Camera targetCam,
        out float hFovDeg,
        out float vFovDeg,
        float near = 0.01f,
        float far  = 1000f,
        string fileName = "camera_calib.json")
    {
        hFovDeg = vFovDeg = 0f;

        try
        {
            string path = Path.Combine(Application.streamingAssetsPath, fileName);
            if (!File.Exists(path))
            {
                Debug.LogError($"[ApplyProjectionFromConfig] File not found: {path}");
                return false;
            }

            string json = File.ReadAllText(path);
            var root = JObject.Parse(json);

            // 1) 先尝试读取 summary 里的 FOV（若存在）
            var summary = root["summary"] as JObject;
            if (summary != null)
            {
                hFovDeg = (float?)summary["horizontal_fov_deg"] ?? 0f;
                vFovDeg = (float?)summary["vertical_fov_deg"] ?? 0f;
            }

            // 2) 读取 image_size（用于 FOV 计算与投影构建）
            int imgW = 0, imgH = 0;
            var imageSize = root["image_size"] as JArray;
            if (imageSize != null && imageSize.Count >= 2)
            {
                imgW = (int)imageSize[0];
                imgH = (int)imageSize[1];
            }

            // 3) 若存在 unity_projection_matrix，优先直接应用
            var upm = root["unity_projection_matrix"] as JArray;
            if (upm != null && upm.Count == 4 && upm[0] is JArray && upm[1] is JArray && upm[2] is JArray && upm[3] is JArray)
            {
                Matrix4x4 P = ReadMatrix4x4(upm);
                targetCam.projectionMatrix = P;
            }
            else
            {
                // 4) 否则用 camera_matrix + image_size 构建投影
                //    OpenCV 相机内参：fx, fy, cx, cy
                var K = root["camera_matrix"] as JArray;
                if (K == null || K.Count != 3 || !(K[0] is JArray) || !(K[1] is JArray))
                {
                    Debug.LogError("[ApplyProjectionFromConfig] camera_matrix missing or invalid.");
                    return false;
                }

                double fx = (double)K[0][0];
                double fy = (double)K[1][1];
                double cx = (double)K[0][2];
                double cy = (double)K[1][2];

                if (imgW <= 0 || imgH <= 0)
                {
                    Debug.LogWarning("[ApplyProjectionFromConfig] image_size missing; fallback to targetCam pixelRect.");
                    imgW = (int)targetCam.pixelWidth;
                    imgH = (int)targetCam.pixelHeight;
                }

                // 构建 Unity 的投影矩阵（右手摄像机 → 左手裁剪空间）
                // 参考：把 OpenCV pinhole 内参转成 NDC（0..1）再映射到 Unity 的裁剪空间。
                targetCam.projectionMatrix = BuildUnityProjectionFromIntrinsics((float)fx, (float)fy, (float)cx, (float)cy, imgW, imgH, near, far);
            }

            // 5) 若 FOV 没读到，则根据内参计算
            if (hFovDeg <= 0f || vFovDeg <= 0f)
            {
                // 需要 fx, fy；尽量从 camera_matrix 取
                var K2 = root["camera_matrix"] as JArray;
                if (K2 != null && K2.Count == 3 && K2[0] is JArray && K2[1] is JArray && imgW > 0 && imgH > 0)
                {
                    double fx = (double)K2[0][0];
                    double fy = (double)K2[1][1];

                    // hfov = 2 * atan( w / (2*fx) ), vfov = 2 * atan( h / (2*fy) )
                    hFovDeg = Mathf.Rad2Deg * (2f * Mathf.Atan((float)imgW / (2f * (float)fx)));
                    vFovDeg = Mathf.Rad2Deg * (2f * Mathf.Atan((float)imgH / (2f * (float)fy)));
                }
            }

            // （可选）把 Unity 的 vertical FOV 同步写回，方便你的其他逻辑使用
            // 注意：当你手动设置 projectionMatrix 时，Camera.fieldOfView 的值不会自动更新，也不会真正被用到。
            // 这里只是为了“读取/展示”方便。
            if (vFovDeg > 0f) targetCam.fieldOfView = vFovDeg;

            Debug.Log($"[ApplyProjectionFromConfig] Done. hFOV={hFovDeg:F2}°, vFOV={vFovDeg:F2}°");
            return true;
        }
        catch (System.Exception ex)
        {
            Debug.LogError($"[ApplyProjectionFromConfig] Exception: {ex}");
            return false;
        }
    }

    private static Matrix4x4 ReadMatrix4x4(JArray arr4x4)
    {
        // arr4x4: 4 行，每行 4 列
        Matrix4x4 m = new Matrix4x4();
        for (int r = 0; r < 4; r++)
        {
            var row = (JArray)arr4x4[r];
            for (int c = 0; c < 4; c++)
                m[r, c] = (float)row[c];
        }
        return m;
    }

    /// <summary>
    /// 由内参（fx, fy, cx, cy）与图像尺寸构建 Unity 投影矩阵。
    /// 适配 Unity 左手裁剪空间（z: -near..-far），与 Camera.projectionMatrix 兼容。
    /// </summary>
    private static Matrix4x4 BuildUnityProjectionFromIntrinsics(
        float fx, float fy, float cx, float cy,
        int width, int height,
        float near, float far)
    {
        // 归一化视口边界（NDC 左右上下），来源：把像素坐标映射到相机归一化平面再到裁剪空间
        float x0 = -cx / fx;
        float x1 = (width - cx) / fx;
        float y0 = -(height - cy) / fy; // 注意 Unity 屏幕 y 方向与相机坐标的对应
        float y1 = cy / fy;

        float left   = near * x0;
        float right  = near * x1;
        float bottom = near * y0;
        float top    = near * y1;

        Matrix4x4 m = new Matrix4x4();
        m[0,0] = 2f * near / (right - left);
        m[0,1] = 0f;
        m[0,2] = (right + left) / (right - left);
        m[0,3] = 0f;

        m[1,0] = 0f;
        m[1,1] = 2f * near / (top - bottom);
        m[1,2] = (top + bottom) / (top - bottom);
        m[1,3] = 0f;

        m[2,0] = 0f;
        m[2,1] = 0f;
        m[2,2] = -(far + near) / (far - near);
        m[2,3] = -(2f * far * near) / (far - near);

        m[3,0] = 0f;
        m[3,1] = 0f;
        m[3,2] = -1f;
        m[3,3] = 0f;

        return m;
    }
}

[Serializable]
public class CameraPoseData
{
    public float[] position;  // x,y,z
    public float[] rotation;  // x,y,z,w (四元数)
}
