// Ground shadows for the runtime-generated city (MapGen / MapView, v0.11.3a). Unlit, alpha-blended:
// vertex colour rgb = shadow tint, a = opacity. Fades out with the same distance haze as DayZero/Map
// (globals _DZFogColor/_DZFogParams). A stencil test keeps overlapping shadows from darkening twice.
// Lives in Resources so the build ships it (loaded with Resources.Load, no Always Included Shaders).
Shader "DayZero/MapShadow"
{
    Properties { }
    SubShader
    {
        Tags { "RenderType"="Transparent" "Queue"="Geometry+10" "IgnoreProjector"="True" }
        Pass
        {
            Cull Off
            ZWrite Off
            ZTest LEqual
            Offset -1, -1
            Blend SrcAlpha OneMinusSrcAlpha
            Stencil { Ref 1 Comp NotEqual Pass Replace }
            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #include "UnityCG.cginc"

            float4 _DZFogParams;

            struct appdata { float4 vertex : POSITION; float4 color : COLOR; };
            struct v2f { float4 pos : SV_POSITION; float4 color : COLOR; float3 wp : TEXCOORD0; };

            v2f vert (appdata v)
            {
                v2f o;
                o.pos = UnityObjectToClipPos(v.vertex);
                o.color = v.color;
                o.wp = mul(unity_ObjectToWorld, v.vertex).xyz;
                return o;
            }

            float4 frag (v2f i) : SV_Target
            {
                float d = distance(i.wp, _WorldSpaceCameraPos.xyz);
                float f = saturate((d - _DZFogParams.x) / max(0.001, _DZFogParams.y - _DZFogParams.x)) * _DZFogParams.z;
                return float4(i.color.rgb, i.color.a * (1.0 - f));
            }
            ENDCG
        }
    }
    Fallback Off
}
