Shader "WebRTC/CompositeColorAlpha"
{
    Properties {
        _ColorTex ("Color", 2D) = "black" {}
        _AlphaTex ("Alpha", 2D) = "black" {}
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
            struct app { float4 vertex:POSITION; float2 uv:TEXCOORD0; };
            struct v2f { float4 pos:SV_POSITION; float2 uv:TEXCOORD0; };
            v2f vert(app v){ v2f o; o.pos=UnityObjectToClipPos(v.vertex); o.uv=v.uv; return o; }
            fixed4 frag(v2f i):SV_Target
            {
                fixed3 c = tex2D(_ColorTex, i.uv).rgb;
                fixed  a = tex2D(_AlphaTex, i.uv).r; // 灰度→Alpha
                return fixed4(c, a);
            }
            ENDCG
        }
    }
}
