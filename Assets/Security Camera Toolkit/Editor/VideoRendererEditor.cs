// Copyright (c) https://github.com/Bian-Sh
// Licensed under the MIT License.
using UnityEngine;
using UnityEditor;
using zFramework.Media;

[CustomEditor(typeof(VideoRenderer)), CanEditMultipleObjects]
public class VideoRendererEditor : Editor
{
    // 原有属性
    SerializedProperty queuesize;
    SerializedProperty framerate;
    SerializedProperty isrendering;
    SerializedProperty width;
    SerializedProperty height;
    SerializedProperty frameload;
    SerializedProperty framerend;
    SerializedProperty framedrop;
    SerializedProperty event_s;

    // 新增 GPU 去畸变相关
    SerializedProperty undistortCompositeMaterial;
    SerializedProperty UVSize;
    SerializedProperty OutputRT;
    SerializedProperty Preview;
    SerializedProperty CalibrationJsonPath;
    SerializedProperty FlipY;
    SerializedProperty UseFullRange;
    SerializedProperty UseBt709;
    SerializedProperty EnableReadback;
    SerializedProperty ReadbackEveryNFrames;

    private void OnEnable()
    {
        width = serializedObject.FindProperty("lumaWidth");
        height = serializedObject.FindProperty("lumaHeight");
        queuesize = serializedObject.FindProperty("maxFrameQueueSize");
        frameload = serializedObject.FindProperty("frameLoad");
        framerend = serializedObject.FindProperty("frameRender");
        framedrop = serializedObject.FindProperty("frameDrop");
        event_s = serializedObject.FindProperty("OnStatisticsReported");
        framerate = serializedObject.FindProperty("framerate");
        isrendering = serializedObject.FindProperty("isRendering");

        // 新增字段
        undistortCompositeMaterial = serializedObject.FindProperty("undistortCompositeMaterial");
        UVSize = serializedObject.FindProperty("UVSize");
        OutputRT = serializedObject.FindProperty("OutputRT");
        Preview = serializedObject.FindProperty("Preview");
        CalibrationJsonPath = serializedObject.FindProperty("CalibrationJsonPath");
        FlipY = serializedObject.FindProperty("FlipY");
        UseFullRange = serializedObject.FindProperty("UseFullRange");
        UseBt709 = serializedObject.FindProperty("UseBt709");
        EnableReadback = serializedObject.FindProperty("EnableReadback");
        ReadbackEveryNFrames = serializedObject.FindProperty("ReadbackEveryNFrames");
    }

    public override void OnInspectorGUI()
    {
        serializedObject.UpdateIfRequiredOrScript();
        EditorGUI.BeginChangeCheck();

        // —— 渲染控制部分
        GUI.enabled = false;
        EditorGUILayout.PropertyField(isrendering);
        GUI.enabled = true;
        EditorGUILayout.PropertyField(framerate, new GUIContent("目标帧率"));
        GUI.enabled = !Application.isPlaying || !isrendering.boolValue;
        EditorGUILayout.PropertyField(queuesize, new GUIContent("帧队列容量"));
        GUI.enabled = true;

        EditorGUILayout.Space(8);

        // —— GPU 去畸变 + 合成设置
        EditorGUILayout.LabelField("Undistort & Composite (GPU)", EditorStyles.boldLabel);
        EditorGUILayout.PropertyField(undistortCompositeMaterial, new GUIContent("材质 (Shader)"));
        EditorGUILayout.PropertyField(UVSize, new GUIContent("UV 平面尺寸"));
        EditorGUILayout.PropertyField(OutputRT, new GUIContent("输出 RT"));
        EditorGUILayout.PropertyField(Preview, new GUIContent("预览 RawImage"));
        EditorGUILayout.PropertyField(CalibrationJsonPath, new GUIContent("标定 JSON 路径"));

        EditorGUILayout.PropertyField(FlipY, new GUIContent("翻转 Y 轴"));
        EditorGUILayout.PropertyField(UseFullRange, new GUIContent("使用 Full Range"));
        EditorGUILayout.PropertyField(UseBt709, new GUIContent("BT.709"));

        EditorGUILayout.Space(4);
        EditorGUILayout.PropertyField(EnableReadback, new GUIContent("启用 GPU 回读"));
        if (EnableReadback.boolValue)
        {
            EditorGUI.indentLevel++;
            EditorGUILayout.PropertyField(ReadbackEveryNFrames, new GUIContent("回读间隔 (帧)"));
            EditorGUI.indentLevel--;
        }

        EditorGUILayout.Space(8);

        // —— 视频信息
        DrawVideoFrameInfo();

        if (EditorGUI.EndChangeCheck())
        {
            serializedObject.ApplyModifiedProperties();
        }
    }

    private void DrawStatisticsInfo()
    {
        var itr = serializedObject.FindProperty("enableStatistics");
        EditorGUILayout.PropertyField(itr);
        if (itr.boolValue)
        {
            using (new EditorGUILayout.VerticalScope("box"))
            {
                GUI.enabled = false;
                frameload.stringValue = EditorGUILayout.TextField(new GUIContent("推流帧率：", aboutFrameload), frameload.stringValue);
                framerend.stringValue = EditorGUILayout.TextField(new GUIContent("取流帧率：", aboutFrameRend), framerend.stringValue);
                framedrop.stringValue = EditorGUILayout.TextField(new GUIContent("丢弃帧率：", aboutFrameDrop), framedrop.stringValue);
                GUI.enabled = true;
            }
            EditorGUILayout.Space(8);
            EditorGUILayout.PropertyField(event_s);
        }
    }

    private void DrawVideoFrameInfo()
    {
        width.isExpanded = EditorGUILayout.BeginFoldoutHeaderGroup(width.isExpanded, new GUIContent("视频信息", aboutfoldheader));
        if (width.isExpanded)
        {
            using (new EditorGUILayout.VerticalScope("box"))
            {
                GUI.enabled = false;
                width.intValue = EditorGUILayout.IntField("视频宽度:", width.intValue);
                height.intValue = EditorGUILayout.IntField("视频高度:", height.intValue);
                using (new EditorGUILayout.HorizontalScope())
                {
                    long rsl = width.intValue * height.intValue;
                    long size = rsl + rsl / 2;
                    EditorGUILayout.LongField("数据大小:", size);
                    EditorGUILayout.LabelField(" ≈ ", GUILayout.Width(20));
                    EditorGUILayout.TextField((size / 1024f / 1024f).ToString("F2"), GUILayout.Width(48));
                    EditorGUILayout.LabelField(" MB", GUILayout.Width(30));
                }
                GUI.enabled = true;
            }
            EditorGUILayout.Space(8);
            DrawStatisticsInfo();
        }
        EditorGUILayout.EndFoldoutHeaderGroup();
    }

    #region Tooltips
    const string aboutFrameload = "该数值表示在一秒内监控播放库 推送的数据量（单位：FPS）";
    const string aboutFrameRend = "该数值表示在一秒内绘制在 RawImage 上的帧数（单位：FPS）";
    const string aboutFrameDrop = "该数值表示在一秒内因为缓存队列满而丢弃的帧数（单位：FPS）";
    const string aboutfoldheader = "友情提示：展示里面的内容会导致 Game 窗口掉帧，请保持折叠";
    #endregion
}
