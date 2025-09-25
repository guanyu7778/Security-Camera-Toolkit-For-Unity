Shader "Hidden/ExtractAlpha"
{
    Properties {
        _MainTex ("Source", 2D) = "black" {}  // <---- 新增
    }
    SubShader
    {
        Tags { "RenderType"="Opaque" "Queue"="Overlay" }
        Pass
        {
            Cull Off ZWrite Off ZTest Always
            Blend Off

            HLSLPROGRAM
            #pragma vertex vert_img
            #pragma fragment frag
            #include "UnityCG.cginc"

            sampler2D _MainTex;

            fixed4 frag(v2f_img i) : SV_Target
            {
                fixed a = tex2D(_MainTex, i.uv).a;
                return fixed4(a, a, a, 1);
            }
            ENDHLSL
        }
    }
    Fallback Off
}
