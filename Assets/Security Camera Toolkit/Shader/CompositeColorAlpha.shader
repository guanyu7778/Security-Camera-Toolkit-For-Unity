Shader "WebRTC/CompositeColorAlpha"
{
    Properties {
        _ColorTex ("Color", 2D) = "black" {}
        _AlphaTex ("Alpha", 2D) = "black" {}
        _AlphaThreshold ("Alpha Threshold", Range(0, 0.5)) = 0.05
    }
    SubShader
    {
        Tags { "Queue"="Transparent" "RenderType"="Transparent" }
        Pass
        {
            ZWrite Off
            Blend SrcAlpha OneMinusSrcAlpha
            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #include "UnityCG.cginc"
            sampler2D _ColorTex;
            sampler2D _AlphaTex;
            float _AlphaThreshold;
            struct app { float4 vertex:POSITION; float2 uv:TEXCOORD0; };
            struct v2f { float4 pos:SV_POSITION; float2 uv:TEXCOORD0; };
            v2f vert(app v){ v2f o; o.pos=UnityObjectToClipPos(v.vertex); o.uv=v.uv; return o; }
            fixed4 frag(v2f i):SV_Target
            {
                fixed3 c = tex2D(_ColorTex, i.uv).rgb;
                fixed  a = tex2D(_AlphaTex, i.uv).r;
                if (a <= _AlphaThreshold)
                {
                    a = 0;
                }
                else
                {
                    a = saturate((a - _AlphaThreshold) / max(1e-4, 1.0 - _AlphaThreshold));
                }
                return fixed4(c, a);
            }
            ENDCG
        }
    }
}


