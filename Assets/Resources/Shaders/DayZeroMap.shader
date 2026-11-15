// Map shader for the runtime-generated city (MapView.cs). Unlit, vertex colour only (no texture):
// lighting is baked into vertex colours when the meshes are built. Distance haze is driven by
// globals MapView sets (_DZFogColor, _DZFogParams = start, end, strength) so it does not depend
// on Unity's fog variants being kept in the build. The haze is computed PER PIXEL from the
// interpolated world position: v0.11.1a computed it per vertex, and on the old 4-vertex ground
// quad (corners hundreds of tiles away) that fogged the whole ground into the clear colour.
// Lives in Resources (loaded with Resources.Load) so the build ships it.
Shader "DayZero/Map"
{
    Properties { }
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

            float4 _DZFogColor;
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
                float3 c = lerp(i.color.rgb, _DZFogColor.rgb, f);
                return float4(c, 1.0);
            }
            ENDCG
        }
    }
    Fallback Off
}
