Shader "SecurityCameraToolkit/YuvUndistortComposite"
{
    Properties
    {
        [NoScaleOffset]_YTexture("Y Texture", 2D) = "black" {}
        [NoScaleOffset]_UTexture("U Texture", 2D) = "gray" {}
        [NoScaleOffset]_VTexture("V Texture", 2D) = "gray" {}
        [Toggle(_APPLY_UNDISTORT_ON)] _ApplyUndistort("Apply Undistort", Float) = 1
        _FlipX("Flip X", Float) = 0
        _FlipY("Flip Y", Float) = 1
    }
    SubShader
    {
        Tags { "RenderType" = "Opaque" }
        Cull Off ZWrite Off ZTest Always

        Pass
        {
            CGPROGRAM
            #pragma target 3.0
            #pragma vertex vert
            #pragma fragment frag
            #pragma multi_compile __ _APPLY_UNDISTORT_ON

            #include "UnityCG.cginc"

            sampler2D _YTexture;
            sampler2D _UTexture;
            sampler2D _VTexture;
            float4 _Intrinsics;      // fx, fy, cx, cy
            float4 _DistRadial;       // k1, k2, k3, 0
            float4 _DistTangential;   // p1, p2, 0, 0
            float4 _TexSize;          // width, height, 1/width, 1/height
            float  _FlipX;
            float  _FlipY;

            struct appdata
            {
                float4 vertex : POSITION;
                float2 uv     : TEXCOORD0;
            };

            struct v2f
            {
                float4 pos : SV_POSITION;
                float2 uv  : TEXCOORD0;
            };

            v2f vert(appdata v)
            {
                v2f o;
                o.pos = UnityObjectToClipPos(v.vertex);
                o.uv = v.uv;
    #if !UNITY_UV_STARTS_AT_TOP
                o.uv.y = 1 - o.uv.y;
    #endif
                return o;
            }

            inline float3 YuvToRgb(float y, float u, float v)
            {
                float3 rgb;
                float uShift = u - 0.5;
                float vShift = v - 0.5;
                rgb.r = y + 1.370705 * vShift;
                rgb.g = y - 0.337633 * uShift - 0.698001 * vShift;
                rgb.b = y + 1.732446 * uShift;
                return saturate(rgb);
            }

            inline float2 DistortForward(float2 xn, float4 kr, float4 pt)
            {
                float k1 = kr.x;
                float k2 = kr.y;
                float k3 = kr.z;
                float p1 = pt.x;
                float p2 = pt.y;

                float r2 = dot(xn, xn);
                float r4 = r2 * r2;
                float r6 = r4 * r2;
                float radial = 1.0 + k1 * r2 + k2 * r4 + k3 * r6;
                float x_t = 2.0 * p1 * xn.x * xn.y + p2 * (r2 + 2.0 * xn.x * xn.x);
                float y_t = p1 * (r2 + 2.0 * xn.y * xn.y) + 2.0 * p2 * xn.x * xn.y;
                return float2(xn.x * radial + x_t, xn.y * radial + y_t);
            }

            inline float2 UndistortUv(float2 uv)
            {
                float2 pixel = float2(uv.x * _TexSize.x, uv.y * _TexSize.y);
                float fx = _Intrinsics.x;
                float fy = _Intrinsics.y;
                float cx = _Intrinsics.z;
                float cy = _Intrinsics.w;

                float2 xn = float2((pixel.x - cx) / fx, (pixel.y - cy) / fy);
                float2 xd = DistortForward(xn, _DistRadial, _DistTangential);
                float2 samplePixel = float2(xd.x * fx + cx, xd.y * fy + cy);
                return float2(samplePixel.x * _TexSize.z, samplePixel.y * _TexSize.w);
            }

            fixed4 frag(v2f i) : SV_Target
            {
                float2 uv = i.uv;
                if (_FlipX > 0.5)
                {
                    uv.x = 1.0 - uv.x;
                }
                if (_FlipY > 0.5)
                {
                    uv.y = 1.0 - uv.y;
                }

                float2 sampleUV = uv;
            #ifdef _APPLY_UNDISTORT_ON
                sampleUV = UndistortUv(uv);
            #endif

                if (any(sampleUV < 0.0) || sampleUV.x > 1.0 || sampleUV.y > 1.0)
                {
                    return fixed4(0, 0, 0, 1);
                }

                float y = tex2D(_YTexture, sampleUV).a;
                float u = tex2D(_UTexture, sampleUV).a;
                float v = tex2D(_VTexture, sampleUV).a;
                float3 rgb = YuvToRgb(y, u, v);
                return fixed4(rgb, 1.0);
            }
            ENDCG
        }
    }
}
