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
    

    //通过TryGetTagCubeCameraPose获取位姿
    public void Save()  
    {
        if (apriltagProcess.TryGetTagCubeCameraPose(0, out Vector3 pos, out Quaternion rot))
        {
            cameraPoseData = new CameraPoseData
            {
                position = new float[] { pos.x, pos.y, pos.z },
                rotation = new float[] { rot.x, rot.y, rot.z, rot.w }
            };
            apriltagProcess.SaveCameraPose(pos, rot);
            info.text = "标定OK";
            apriltagProcess.StopAprilTagDetection();
            //把camera设置为正确位姿
            targetCamera.transform.SetPositionAndRotation(pos, rot);
            apriltagProcess.RemoveAllCubes();
            Debug.Log($"[MRCompositor] Camera pose saved to file: pos={pos}, rot={rot.eulerAngles}");
        }
        else
        {
            info.text = "无标定";
            Debug.LogWarning("[MRCompositor] No valid tag cube detected. Cannot save camera pose.");
        }
    }

    public void StartBD()
    {
        apriltagProcess.StartAprilTagDetection();
        //临时把camera设置为000
        targetCamera.transform.SetPositionAndRotation(Vector3.zero, Quaternion.identity);
    } 
}
