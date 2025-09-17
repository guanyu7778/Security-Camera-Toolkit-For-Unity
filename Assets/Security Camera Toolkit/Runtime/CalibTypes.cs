using UnityEngine;
using Newtonsoft.Json;

[System.Serializable]
public class CalibData
{
    // 与你的 JSON 字段名一一对应
    [JsonProperty("image_size")]
    public int[] image_size;                  // [width, height]

    [JsonProperty("camera_matrix")]
    public float[][] camera_matrix;           // 3x3

    [JsonProperty("distortion_coefficients")]
    public float[] distortion_coefficients;   // [k1,k2,p1,p2,k3]

    [JsonProperty("unity_projection_matrix")]
    public float[][] unity_projection_matrix; // 4x4（可能缺省）
}

public static class CalibUtil
{
    public static CalibData ParseJson(string jsonText)
    {
        // 直接反序列化成二维数组（Newtonsoft 支持）
        var data = JsonConvert.DeserializeObject<CalibData>(jsonText);
        if (data == null) throw new System.Exception("[CalibUtil] JSON parse failed (null).");
        return data;
    }

    public static Vector2Int GetImageSize(CalibData d)
    {
        if (d != null && d.image_size != null && d.image_size.Length >= 2)
            return new Vector2Int(d.image_size[0], d.image_size[1]);
        return new Vector2Int(Screen.width, Screen.height);
    }

    public static Vector4 GetIntrinsicsXYCXCY(CalibData d)
    {
        if (d == null || d.camera_matrix == null || d.camera_matrix.Length < 2 ||
            d.camera_matrix[0].Length < 3 || d.camera_matrix[1].Length < 3)
            throw new System.Exception("[CalibUtil] camera_matrix missing or malformed (expect 3x3).");

        float fx = d.camera_matrix[0][0];
        float fy = d.camera_matrix[1][1];
        float cx = d.camera_matrix[0][2];
        float cy = d.camera_matrix[1][2];
        return new Vector4(fx, fy, cx, cy);
    }

    public static Vector4 GetRadialK1K2K3(CalibData d)
    {
        if (d == null || d.distortion_coefficients == null || d.distortion_coefficients.Length < 5)
            throw new System.Exception("[CalibUtil] distortion_coefficients malformed (need [k1,k2,p1,p2,k3]).");
        return new Vector4(d.distortion_coefficients[0], d.distortion_coefficients[1], d.distortion_coefficients[4], 0f);
    }

    public static Vector4 GetTangentialP1P2(CalibData d)
    {
        if (d == null || d.distortion_coefficients == null || d.distortion_coefficients.Length < 4)
            throw new System.Exception("[CalibUtil] distortion_coefficients missing p1/p2.");
        return new Vector4(d.distortion_coefficients[2], d.distortion_coefficients[3], 0f, 0f);
    }

    /// 优先使用 JSON 给出的 4x4 投影；若没有，则由内参推导离轴投影
    public static bool TryGetProjectionMatrix(CalibData d, out Matrix4x4 proj, float near = 0.01f, float far = 100f)
    {
        // 1) JSON 的 4x4
        if (d != null && d.unity_projection_matrix != null && d.unity_projection_matrix.Length == 4 &&
            d.unity_projection_matrix[0].Length == 4 &&
            d.unity_projection_matrix[1].Length == 4 &&
            d.unity_projection_matrix[2].Length == 4 &&
            d.unity_projection_matrix[3].Length == 4)
        {
            var m = d.unity_projection_matrix;
            proj = new Matrix4x4();
            proj.m00 = m[0][0]; proj.m01 = m[0][1]; proj.m02 = m[0][2]; proj.m03 = m[0][3];
            proj.m10 = m[1][0]; proj.m11 = m[1][1]; proj.m12 = m[1][2]; proj.m13 = m[1][3];
            proj.m20 = m[2][0]; proj.m21 = m[2][1]; proj.m22 = m[2][2]; proj.m23 = m[2][3];
            proj.m30 = m[3][0]; proj.m31 = m[3][1]; proj.m32 = m[3][2]; proj.m33 = m[3][3];
            return true;
        }

        // 2) 回退：用 fx,fy,cx,cy + 图像尺寸 推导
        return TryBuildProjectionFromIntrinsics(d, near, far, out proj);
    }

    /// 由内参构造 Unity 的 off-center Frustum（像素坐标→相机归一化）
    public static bool TryBuildProjectionFromIntrinsics(CalibData d, float near, float far, out Matrix4x4 proj)
    {
        proj = Matrix4x4.identity;
        if (d == null || d.camera_matrix == null) return false;

        var wh = GetImageSize(d);
        float w = Mathf.Max(1, wh.x);
        float h = Mathf.Max(1, wh.y);

        var intr = GetIntrinsicsXYCXCY(d);
        float fx = intr.x, fy = intr.y, cx = intr.z, cy = intr.w;

        // OpenCV: x 向右、y 向下；构造离轴视锥
        float l = -near * (cx) / fx;
        float r = near * (w - cx) / fx;
        float t = near * (cy) / fy;
        float b = -near * (h - cy) / fy;

        proj = Matrix4x4.Frustum(l, r, b, t, near, far);
        return true;
    }
}
