// Map shader for the runtime-generated city (MapView.cs). Unlit, vertex colour only (no texture):
// lighting is baked into vertex colours when the meshes are built. Distance haze is driven by
// globals MapView sets (_DZFogColor, _DZFogParams = start, end, strength) so it does not depend
// on Unity's fog variants being kept in the build. The haze is computed PER PIXEL from the
// interpolated world position: v0.11.1a computed it per vertex, and on the old 4-vertex ground
// quad (corners hundreds of tiles away) that fogged the whole ground into the clear colour.
// Lives in Resources (loaded with Resources.Load) so the build ships it.
// Water: MapGen writes vertex alpha 255 on every map vertex except water surface quads, which carry
// 0..200 (shore..deep). Those pixels get animated ripple bands, a sky/haze sheen at grazing angles,
// sparse glints and pulsing shore foam, all from _Time (no textures). Other pixels are unchanged.
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

            float3 water (float3 c, float depth, float3 wp)
            {
                float t = _Time.y;
                float2 p = wp.xz;
                // slow warp so the bands never look like straight stripes
                float n = sin(p.x * 0.37 + t * 0.55) * sin(p.y * 0.29 - t * 0.41);
                float b1 = sin(dot(p, float2(0.80, 0.60)) * 2.6 + t * 1.30 + n * 2.2);
                float b2 = sin(dot(p, float2(-0.45, 0.89)) * 3.7 - t * 0.95 + n * 1.6);
                float rip = (b1 * 0.5 + 0.5) * (b2 * 0.5 + 0.5);               // 0..1
                float crest = pow(rip, 5.0);
                float3 v = normalize(_WorldSpaceCameraPos.xyz - wp);
                float fres = pow(1.0 - saturate(v.y), 2.0);                       // grazing views mirror the sky
                float3 sky = _DZFogColor.rgb;
                float3 o = c * (0.90 + 0.18 * rip);
                o = lerp(o, sky, saturate(0.10 + 0.30 * fres) * (0.6 + 0.4 * rip));
                o += crest * 0.07 * (sky + 0.25);
                // sparse glints: a random tile-ish cell flickers on briefly
                float2 cell = floor(p * 3.0);
                float h = frac(sin(dot(cell, float2(12.9898, 78.233))) * 43758.5453);
                float tw = pow(saturate(sin(t * (1.7 + h * 2.3) + h * 40.0)), 24.0);
                o += step(0.93, h) * tw * crest * 0.6;
                // shore foam: pulsing pale strip where the water meets the bank
                float shore = 1.0 - smoothstep(0.0, 0.22, depth);
                float pulse = 0.55 + 0.45 * sin(t * 1.6 + p.x * 0.9 + p.y * 0.7 + n * 3.0);
                o = lerp(o, float3(0.62, 0.66, 0.60) * (0.7 + 0.3 * sky * 2.0), saturate(shore * pulse * 0.45));
                return o;
            }

            float4 frag (v2f i) : SV_Target
            {
                float d = distance(i.wp, _WorldSpaceCameraPos.xyz);
                float f = saturate((d - _DZFogParams.x) / max(0.001, _DZFogParams.y - _DZFogParams.x)) * _DZFogParams.z;
                float3 base = i.color.rgb;
                if (i.color.a < 0.9) base = water(base, saturate(i.color.a * (255.0 / 200.0)), i.wp);
                float3 c = lerp(base, _DZFogColor.rgb, f);
                return float4(c, 1.0);
            }
            ENDCG
        }
    }
    Fallback Off
}
