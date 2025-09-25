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

    public System.Action<CameraPoseData> OnCameraPoseEstimated;

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

    public CameraPoseData cameraPoseData;

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
        videoRenderer.OnFrameDataReady += OnFrameDataReady;
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

    void OnFrameDataReady(Color32[] rgba, int w, int h)
    {
        // 1) 取帧
        ReadOnlySpan<Color32> span = rgba.AsSpan();
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
            if (tag.ID == 0)
            {
                // 估计相机位姿
                GetCameraPoseInTagFrame(tag.Position, tag.Rotation, out var camPos, out var camRot);
                cameraPoseData = new CameraPoseData
                {
                    position = new float[] { camPos.x, camPos.y, camPos.z },
                    rotation = new float[] { camRot.x, camRot.y, camRot.z, camRot.w },
                    fov = this.fov
                };
                OnCameraPoseEstimated?.Invoke(cameraPoseData);
            }
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

    public void RemoveAllCubes()
    {
        foreach (var kv in _cubes)
        {
            var tc = kv.Value;
            if (tc != null && tc.go != null)
            {
                Destroy(tc.go);
            }
        }
        _cubes.Clear();
    }

    public void GetCameraPoseInTagFrame(
        Vector3 tagPosCam, Quaternion tagRotCam,
        out Vector3 camPosWorld, out Quaternion camRotWorld)
    {
        // 1. 相机在 Tag 坐标系下
        Quaternion camRotInTag = Quaternion.Inverse(tagRotCam);
        Vector3 camPosInTag = -(camRotInTag * tagPosCam);

        // 2. 定义 Tag 坐标系 → Unity 世界坐标系 的旋转
        // 假设 Tag 的 +Z = 世界 +Y,  Tag 的 +X = 世界 +Z,  Tag 的 +Y = 世界 +X
        Vector3 tagForward = -Vector3.up;       // Tag +Z -> 世界 Up
        Vector3 tagUp      = Vector3.forward;  // Tag +Y -> 世界 Forward (可按需要调整)
        Quaternion tagToWorld = Quaternion.LookRotation(tagForward, tagUp);

        // 3. 转换到世界系
        camRotWorld = tagToWorld * camRotInTag;
        camPosWorld = tagToWorld * camPosInTag;
    }

    public void SaveCameraPose(Vector3 pos, Quaternion rot, string fileName = "Configurations/camera_pose.json")
    {
        var data = new CameraPoseData
        {
            position = new float[] { pos.x, pos.y, pos.z },
            rotation = new float[] { rot.x, rot.y, rot.z, rot.w },
            fov = this.fov
        };

        // Newtonsoft.Json 支持格式化缩进
        string json = JsonConvert.SerializeObject(data, Formatting.Indented);

        string path = Path.Combine(Application.streamingAssetsPath, fileName);
        File.WriteAllText(path, json);

        Debug.Log($"[SaveCameraPose] Saved to {path}");
    }
    public bool LoadCameraPose(out Vector3 pos, out Quaternion rot, string fileName = "Configurations/camera_pose.json")
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
        cameraPoseData = data;
        Debug.Log($"[LoadCameraPose] Loaded from {path}");
        return true;
    }

    public bool ApplyProjectionFromConfig(
        Camera targetCam,
        out float hFovDeg,
        out float vFovDeg,
        float near = 0.01f,
        float far = 1000f,
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

            targetCam.fieldOfView = vFovDeg;

            Debug.Log($"[ApplyProjectionFromConfig] Done. hFOV={hFovDeg:F2}°, vFOV={vFovDeg:F2}°");
            return true;
        }
        catch (System.Exception ex)
        {
            Debug.LogError($"[ApplyProjectionFromConfig] Exception: {ex}");
            return false;
        }
    }

    public void StartAprilTagDetection()
    {
        if (videoRenderer != null)
        {
            videoRenderer.EnableReadback = true;
        }
    }

    public void StopAprilTagDetection()
    {
        if (videoRenderer != null)
        {
            videoRenderer.EnableReadback = false;
        }
        RemoveStaleCubes();
    }

    //通过tagid获取对应的cube，如果最后更新时间在阈值之内，就返回对应的camerapos
    public bool TryGetTagCubeCameraPose(int tagId, out Vector3 camPos, out Quaternion camRot)
    {
        camPos = Vector3.zero;
        camRot = Quaternion.identity;

        if (cameraPoseData == null)
        {
            return false;
        }

        if (_cubes.TryGetValue(tagId, out var tc))
        {
            if (Time.frameCount - tc.lastSeenFrame <= staleFramesToRemove)
            {
                camPos = new Vector3(cameraPoseData.position[0], cameraPoseData.position[1], cameraPoseData.position[2]);
                camRot = new Quaternion(cameraPoseData.rotation[0], cameraPoseData.rotation[1], cameraPoseData.rotation[2], cameraPoseData.rotation[3]);
                return true;
            }
        }
        return false;
    }
}

[Serializable]
public class CameraPoseData
{
    public float[] position;  // x,y,z
    public float[] rotation;  // x,y,z,w (四元数)
    public float fov;        // 垂直方向 FOV（度），可选
}
