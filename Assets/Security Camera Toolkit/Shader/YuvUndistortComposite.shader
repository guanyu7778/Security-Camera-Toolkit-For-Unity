Shader "Custom/YUVUndistortComposite"
{
    Properties
    {
        // 三平面纹理：建议 R8（linear）
        _YTex ("Y Plane (R8)", 2D) = "black" {}
        _UTex ("U Plane (R8)", 2D) = "gray"  {}
        _VTex ("V Plane (R8)", 2D) = "gray"  {}

        // 尺寸：xy=(w,h), zw=(1/w,1/h)
        _YSize  ("Y Size (w,h,1/w,1/h)",  Vector) = (1920,1080,0.00052,0.00093)
        _UVSize ("UV Size (w,h,1/w,1/h)", Vector) = (960,540,0.00104,0.00185) // 4:2:0 默认

        // 去畸变参数（OpenCV 内外参）
        _K  ("fx, fy, cx, cy", Vector) = (1000,1000,960,540)
        _D1 ("k1,k2,p1,p2",   Vector) = (0,0,0,0)
        _K3 ("k3", Float) = 0

        // 控制项
        _FlipY     ("Flip Output Vertically", Float) = 0
        _RangeMode ("0=Limited(16..235) 1=Full(0..255)", Float) = 1
        _ColorStd  ("0=BT.601 1=BT.709", Float) = 0

        // 调试/兼容
        _SwapUV    ("Swap U and V", Float) = 0
        _DebugView ("Debug 0=RGB 1=Y 2=U 3=V", Float) = 0
        _BlackBorder ("Black border when OOB (0=Off 1=On)", Float) = 1
        _Undistort ("Enable Undistortion (0=Off 1=On)", Float) = 1
        _FovScale  ("FOV scale (<1 wider, >1 narrower)", Float) = 1
    }

    SubShader
    {
        Tags { "RenderType"="Opaque" "Queue"="Overlay" }
        ZTest Always ZWrite Off Cull Off Blend Off

        Pass
        {
            HLSLPROGRAM
            #pragma vertex   vert
            #pragma fragment frag
            #include "UnityCG.cginc"

            sampler2D _YTex, _UTex, _VTex;
            float4 _YSize;      // (w,h,1/w,1/h)
            float4 _UVSize;     // (w,h,1/w,1/h)

            float4 _K;          // (fx,fy,cx,cy)
            float4 _D1;         // (k1,k2,p1,p2)
            float  _K3;         // k3

            float  _FlipY;      // 0/1
            float  _RangeMode;  // 0=Limited,1=Full
            float  _ColorStd;   // 0=601,1=709
            float  _SwapUV;     // 0/1
            float  _DebugView;  // 0/1/2/3
            float _BlackBorder; // 0/1
            float _Undistort;
            float _FovScale;

            struct appdata { float4 vertex:POSITION; float2 uv:TEXCOORD0; };
            struct v2f { float4 pos:SV_POSITION; float2 uv:TEXCOORD0; };

            v2f vert(appdata v){ v2f o; o.pos = UnityObjectToClipPos(v.vertex); o.uv = v.uv; return o; }

            // —— 输出像素 uv → 源像素 uv（Brown-Conrady；不改分辨率/FOV）
            float2 UndistortUV(float2 uv, float2 texWH, float scale)
            {
                // 缩放后的内参（scale<1 → 更广角，更容易越界→出现黑边）
                float fx = _K.x * scale, fy = _K.y * scale, cx = _K.z, cy = _K.w;
                float k1 = _D1.x, k2 = _D1.y, p1 = _D1.z, p2 = _D1.w, k3 = _K3;

                float2 pix = uv * texWH;
                float x = (pix.x - cx) / fx;
                float y = (pix.y - cy) / fy;

                float r2 = x*x + y*y; float r4 = r2*r2; float r6 = r4*r2;
                float radial = 1.0 + k1*r2 + k2*r4 + k3*r6;

                float xt = 2.0*p1*x*y + p2*(r2 + 2.0*x*x);
                float yt = p1*(r2 + 2.0*y*y) + 2.0*p2*x*y;

                float xd = x * radial + xt;
                float yd = y * radial + yt;

                float2 srcPix = float2(fx*xd + cx, fy*yd + cy);
                return srcPix / texWH; // 归一化 uv
            }
            // —— YUV → RGB（支持 Full/Limited + 601/709）
            float3 YUV_to_RGB(float y, float u, float v)
            {
                if (_RangeMode < 0.5) {
                    // Limited：Y∈[16..235], U/V∈[16..240]
                    y = saturate((y * 255.0 - 16.0) / 219.0);
                    u = (u * 255.0 - 128.0) / 224.0;
                    v = (v * 255.0 - 128.0) / 224.0;
                } else {
                    // Full：0..255（已被纹理归一化到 0..1）
                    y = saturate(y);
                    u = (u - 0.5);
                    v = (v - 0.5);
                }

                if (_ColorStd < 0.5) {
                    // BT.601
                    return saturate(float3(
                        y + 1.402    * v,
                        y - 0.344136 * u - 0.714136 * v,
                        y + 1.772    * u
                    ));
                } else {
                    // BT.709
                    return saturate(float3(
                        y + 1.5748 * v,
                        y - 0.1873 * u - 0.4681 * v,
                        y + 1.8556 * u
                    ));
                }
            }

            fixed4 frag(v2f i) : SV_Target
            {
                float2 uv = i.uv;
                if (_FlipY > 0.5) uv.y = 1.0 - uv.y;

                // 1) 以 Y 尺寸计算“反畸变后的源 uv”
                float2 uv_src_Y = (_Undistort > 0.5) ? UndistortUV(uv, _YSize.xy, _FovScale) : uv;
                // U/V 使用同一归一化坐标
                float2 uv_src_UV = uv_src_Y;

                // 越界遮罩（也可以依赖纹理 Clamp）
                float inY  = step(0.0, uv_src_Y.x)  * step(uv_src_Y.x, 1.0)  * step(0.0, uv_src_Y.y)  * step(uv_src_Y.y, 1.0);
                float inUV = step(0.0, uv_src_UV.x) * step(uv_src_UV.x, 1.0) * step(0.0, uv_src_UV.y) * step(uv_src_UV.y, 1.0);

                
                // 一键黑边：开启时仅在范围内保留，越界=0；关闭时保持原逻辑（由纹理Clamp决定）
                float maskY  = (_BlackBorder > 0.5) ? inY  : 1.0;
                float maskUV = (_BlackBorder > 0.5) ? inUV : 1.0;

                // 3) 采样三平面（R8 → .r）
                float y = tex2D(_YTex, uv_src_Y ).r * maskY;
                float u = tex2D(_UTex, uv_src_UV).r * maskUV;
                float v = tex2D(_VTex, uv_src_UV).r * maskUV;

                // 可选：交换 U/V（有些源顺序相反）
                if (_SwapUV > 0.5) { float t = u; u = v; v = t; }

                // 调试视图
                if (_DebugView > 0.5 && _DebugView < 1.5) return float4(y, y, y, 1); // Y
                if (_DebugView > 1.5 && _DebugView < 2.5) return float4(u, u, u, 1); // U
                if (_DebugView > 2.5 && _DebugView < 3.5) return float4(v, v, v, 1); // V

                float3 rgb = YUV_to_RGB(y, u, v);
                return float4(rgb, 1);
            }
            ENDHLSL
        }
    }
}
