Shader "MR/DistortVirtualToLens"
{
    Properties
    {
        // 用 MainTexture，使 RawImage 自动绑定
        [MainTexture] [NoScaleOffset] _MainTex("Virtual RT", 2D) = "black" {}
        [Toggle(_BYPASS_ON)] _Bypass("Debug: pass-through", Float) = 0
    }
        SubShader
        {
            Tags { "Queue" = "Overlay" "RenderType" = "Transparent" }
            Cull Off ZTest Always ZWrite Off
            Blend SrcAlpha OneMinusSrcAlpha

            Pass
            {
                CGPROGRAM
                #pragma vertex   vert
                #pragma fragment frag
                #pragma multi_compile __ _BYPASS_ON
                #include "UnityCG.cginc"

                sampler2D _MainTex;

                float4 _CamIntrinsics;     // (fx, fy, cx, cy)
                float4 _DistRadial;        // (k1, k2, k3, 0)
                float4 _DistTangential;    // (p1, p2, 0, 0)
                float4 _VirtualIntrinsics; // (fx, fy, cx, cy) after frustum cover
                float4 _TexSize;           // (w, h, 1/w, 1/h)

                struct appdata { float4 vertex:POSITION; float2 uv:TEXCOORD0; };
                struct v2f { float4 pos:SV_POSITION; float2 uv:TEXCOORD0; };

                v2f vert(appdata v) { v2f o; o.pos = UnityObjectToClipPos(v.vertex); o.uv = v.uv; return o; }

                inline float2 DistortForward(float2 x, float4 kr, float4 pt) {
                    float xn = x.x, yn = x.y; float k1 = kr.x,k2 = kr.y,k3 = kr.z; float p1 = pt.x,p2 = pt.y;
                    float r2 = xn * xn + yn * yn, r4 = r2 * r2, r6 = r4 * r2;
                    float radial = 1.0 + k1 * r2 + k2 * r4 + k3 * r6;
                    float x_t = 2.0 * p1 * xn * yn + p2 * (r2 + 2.0 * xn * xn);
                    float y_t = p1 * (r2 + 2.0 * yn * yn) + 2.0 * p2 * xn * yn;
                    return float2(xn * radial + x_t, yn * radial + y_t);
                }

                inline float2 InverseDistort(float2 xd, float4 kr, float4 pt) {
                    float2 x = xd;
                    [unroll(5)]
                    for (int i = 0; i < 5; i++) {
                        float2 f = DistortForward(x,kr,pt) - xd;
                        float eps = 1e-3;
                        float2 dx = float2(eps,0), dy = float2(0,eps);
                        float2 fx = DistortForward(x + dx,kr,pt) - DistortForward(x - dx,kr,pt);
                        float2 fy = DistortForward(x + dy,kr,pt) - DistortForward(x - dy,kr,pt);
                        float a = fx.x / (2 * eps), b = fy.x / (2 * eps), c = fx.y / (2 * eps), d = fy.y / (2 * eps);
                        float det = a * d - b * c + 1e-6;
                        float2 delta = float2((d * f.x - b * f.y) / det, (-c * f.x + a * f.y) / det);
                        x -= delta;
                    }
                    return x;
                }

                inline float2 DistortedScreenUV_to_VirtualRT_UV(float2 uv) {
                    float w = _TexSize.x, h = _TexSize.y;
                    float x_d = uv.x * w, y_d = uv.y * h;
                    float fx = _CamIntrinsics.x, fy = _CamIntrinsics.y;
                    float cx = _CamIntrinsics.z, cy = _CamIntrinsics.w;
                    float2 xd = float2((x_d - cx) / fx, (y_d - cy) / fy);
                    float2 xu = InverseDistort(xd,_DistRadial,_DistTangential);
                    float fxv = _VirtualIntrinsics.x, fyv = _VirtualIntrinsics.y;
                    float cxv = _VirtualIntrinsics.z, cyv = _VirtualIntrinsics.w;
                    float x_u = xu.x * fxv + cxv;
                    float y_u = xu.y * fyv + cyv;
                    return float2(x_u * _TexSize.z, y_u * _TexSize.w);
                }

                fixed4 frag(v2f i) :SV_Target
                {
                    #if _BYPASS_ON
                        return tex2D(_MainTex, i.uv); // 调试：直通看看纹理是否正确
                    #else
                        float2 uv_src = DistortedScreenUV_to_VirtualRT_UV(i.uv);
                        // 可先 clamp 调试，避免整屏透明
                        // uv_src = saturate(uv_src);
                        if (any(uv_src < 0) || uv_src.x > 1 || uv_src.y > 1) return 0;
                        return tex2D(_MainTex, uv_src);
                    #endif
                }
                ENDCG
            }
        }
}
