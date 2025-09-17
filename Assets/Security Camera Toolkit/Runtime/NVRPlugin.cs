using System.Collections;
using System.Collections.Generic;
using System.Threading.Tasks;
using UnityEngine;
using zFramework.Media;

public class NVRPlugin : MonoBehaviour
{
    [SerializeField]
    SecurityCamera sc;

    private void Start()
    {
        Init();
    }

    private void Init()
    {
        //初始化摄像头，并自动播放   
        _ = InitNVR();
    }

    async Task InitNVR()
    {
        await NVRManager.LoginAllAsync();
        if (NVRConfiguration.Instance.nvrs.Count <= 0)
        {
            Debug.LogWarning("NVRConfiguration.Instance.nvrs count = 0");
            return;
        }
        var data = NVRConfiguration.Instance.nvrs[0];
        sc.host = data.host;
        sc.sdk = data.type;
        sc.PlayReal(); 
    }

    private void Update()
    {
        if(Input.GetKeyDown(KeyCode.D))
        {
            string filename = $"Screenshot_{System.DateTime.Now:yyyyMMdd_HHmmss}.png";
            ScreenCapture.CaptureScreenshot(filename);
            Debug.Log("保存截图到: " + filename);
        }
    }
}
