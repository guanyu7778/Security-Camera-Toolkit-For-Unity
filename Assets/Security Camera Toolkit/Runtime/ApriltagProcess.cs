using System;
using System.Collections.Generic;
using System.Linq;
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

        // 默认 Unity 立方体是 1x1x1 米；我们希望 Cube 尺寸与 Tag 尺寸一致（边长 = tagSizeMeters）
        go.transform.localScale = Vector3.one * tagSizeMeters;

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
}
