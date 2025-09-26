Shader "WebRTC/CompositePackedColorAlpha"
{
    Properties
    {
        _PackedTex ("Packed", 2D) = "black" {}
        _AlphaThreshold ("Alpha Threshold", Range(0, 0.5)) = 0.05
    }
    SubShader
    {
        Tags { "Queue" = "Transparent" "RenderType" = "Transparent" }
        Pass
        {
            ZWrite Off
            Blend SrcAlpha OneMinusSrcAlpha

            HLSLPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #include "UnityCG.cginc"

            sampler2D _PackedTex;
            float _AlphaThreshold;

            struct app
            {
                float4 vertex : POSITION;
                float2 uv : TEXCOORD0;
            };

            struct v2f
            {
                float4 pos : SV_POSITION;
                float2 uv : TEXCOORD0;
            };

            v2f vert(app v)
            {
                v2f o;
                o.pos = UnityObjectToClipPos(v.vertex);
                o.uv = v.uv;
                return o;
            }

            fixed4 frag(v2f i) : SV_Target
            {
                float2 uvColor = float2(i.uv.x, i.uv.y * 0.5f + 0.5f);
                float2 uvAlpha = float2(i.uv.x, i.uv.y * 0.5f);

                fixed3 rgb = tex2D(_PackedTex, saturate(uvColor)).rgb;
                fixed a = tex2D(_PackedTex, saturate(uvAlpha)).r;

                if (a <= _AlphaThreshold)
                {
                    a = 0;
                }
                else
                {
                    a = saturate((a - _AlphaThreshold) / max(1e-4, 1.0 - _AlphaThreshold));
                }

                return fixed4(rgb, a);
            }
            ENDHLSL
        }
    }
}