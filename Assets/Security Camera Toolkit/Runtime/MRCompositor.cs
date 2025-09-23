// MRCompositor.cs
// ���ļ��棺��ȡ�궨 �� ��ȷ��׶ �� �������Ⱦ��RT �� �����䡰Ť�䡱���ӵ���Ƶ
// ���� Newtonsoft.Json��Package: com.unity.nuget.newtonsoft-json��

using System.IO;
using UnityEngine;
using UnityEngine.UI;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using zFramework.Media;
using System.Collections;
using UnityEngine.UI;
public class MRCompositor : MonoBehaviour
{
    // 1. 默认开启获取视频
    // 2. 读取相机位姿
    // 3. 如果没有位姿文件，则提示新建位姿

    [SerializeField]
    ApriltagProcess apriltagProcess;
    [SerializeField]
    VideoRenderer videoRenderer;
    [SerializeField]
    Camera targetCamera;
    [SerializeField]
    Text info;

    CameraPoseData cameraPoseData;

    void Start()
    {
        StartCoroutine(Init());
        apriltagProcess.OnCameraPoseEstimated += (pose) =>
        {
            cameraPoseData = pose;
        };
    }

    IEnumerator Init()
    {
        yield return new WaitForEndOfFrame();
        //读取位姿文件
        if (!apriltagProcess.LoadCameraPose(out Vector3 pos, out Quaternion rot))
        {
            Debug.LogWarning("[MRCompositor] No camera pose file found. Creating a new one at camera_pose.json");
            info.text = "无标定";
            yield break;
        }
        targetCamera.transform.SetPositionAndRotation(pos, rot);
        cameraPoseData = new CameraPoseData
        {
            position = new float[] { pos.x, pos.y, pos.z },
            rotation = new float[] { rot.x, rot.y, rot.z, rot.w }
        };
        info.text = "标定完成";
        Debug.Log($"[MRCompositor] Camera pose loaded from file: pos={pos}, rot={rot.eulerAngles}");
    }

    public void Save()
    {
        if (cameraPoseData != null)
        {
            apriltagProcess.SaveCameraPose(new Vector3(cameraPoseData.position[0], cameraPoseData.position[1], cameraPoseData.position[2]),
                new Quaternion(cameraPoseData.rotation[0], cameraPoseData.rotation[1], cameraPoseData.rotation[2], cameraPoseData.rotation[3]));
            Debug.Log("[MRCompositor] Camera pose saved.");
            apriltagProcess.StopAprilTagDetection();
        }
        else
        {
            Debug.LogWarning("[MRCompositor] No camera pose data to save.");
        }
    }

    public void StartBD()
    { 
        apriltagProcess.StartAprilTagDetection();
    } 
}
