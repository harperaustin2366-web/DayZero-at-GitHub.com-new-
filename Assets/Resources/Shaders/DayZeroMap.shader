// Map shader for the runtime-generated city (MapView.cs). Unlit: lighting is baked into vertex
// colours when the meshes are built. Texture * vertex colour, plus a distance haze driven by
// globals MapView sets (_DZFogColor, _DZFogParams = start, end, strength) so it does not depend
// on Unity's fog variants being kept in the build. Lives in Resources so the build always ships it.
Shader "DayZero/Map"
{
    Properties
    {
        _MainTex ("Texture", 2D) = "white" {}
    }
    SubShader
    {
        Tags { "RenderType"="Opaque" "Queue"="Geometry" }
        Pass
        {
            Cull Off
            ZWrite On
            ZTest LEqual
            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #include "UnityCG.cginc"

            sampler2D _MainTex;
            float4 _MainTex_ST;
            float4 _DZFogColor;
            float4 _DZFogParams;

            struct appdata { float4 vertex : POSITION; float4 color : COLOR; float2 uv : TEXCOORD0; };
            struct v2f { float4 pos : SV_POSITION; float4 color : COLOR; float2 uv : TEXCOORD0; float fog : TEXCOORD1; };

            v2f vert (appdata v)
            {
                v2f o;
                o.pos = UnityObjectToClipPos(v.vertex);
                o.color = v.color;
                o.uv = TRANSFORM_TEX(v.uv, _MainTex);
                float3 wp = mul(unity_ObjectToWorld, v.vertex).xyz;
                float d = distance(wp, _WorldSpaceCameraPos.xyz);
                o.fog = saturate((d - _DZFogParams.x) / max(0.001, _DZFogParams.y - _DZFogParams.x)) * _DZFogParams.z;
                return o;
            }

            fixed4 frag (v2f i) : SV_Target
            {
                float3 c = tex2D(_MainTex, i.uv).rgb * i.color.rgb;
                c = lerp(c, _DZFogColor.rgb, i.fog);
                return fixed4(c, 1.0);
            }
            ENDCG
        }
    }
    Fallback Off
}
