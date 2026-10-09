// Low-poly water (URP): gently rolling faceted waves, coloured by depth from
// the mesh's vertex colours (alpha = opacity), with a sun glint and a sky
// reflection at grazing angles.
Shader "LastCast/LowPolyWater"
{
    Properties
    {
        _SkyColor ("Sky reflection", Color) = (0.62, 0.8, 0.92, 1)
        _WaveHeight ("Wave height", Float) = 0.06
        _WaveScale ("Wave scale", Float) = 0.35
    }
    SubShader
    {
        Tags { "RenderType" = "Transparent" "Queue" = "Transparent" "RenderPipeline" = "UniversalPipeline" }

        Pass
        {
            Name "ForwardLit"
            Tags { "LightMode" = "UniversalForward" }
            Blend SrcAlpha OneMinusSrcAlpha
            ZWrite Off
            Cull Off

            HLSLPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #pragma multi_compile_fog

            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Lighting.hlsl"

            CBUFFER_START(UnityPerMaterial)
                half4 _SkyColor;
                float _WaveHeight;
                float _WaveScale;
            CBUFFER_END

            // uv.x: how close to the shore (1 at the waterline), uv.y: a phase so the surf isn't in step.
            struct Attributes { float4 positionOS : POSITION; half4 color : COLOR; float2 uv : TEXCOORD0; };

            struct Varyings
            {
                float4 positionCS : SV_POSITION;
                float3 positionWS : TEXCOORD0;
                half4 color : COLOR;
                half fog : TEXCOORD1;
                float2 shore : TEXCOORD2;
            };

            Varyings vert(Attributes v)
            {
                Varyings o;
                float3 ws = TransformObjectToWorld(v.positionOS.xyz);
                float t = _Time.y;
                float k = _WaveScale;
                ws.y += _WaveHeight * (sin(ws.x * k + t * 1.1) + sin(ws.z * k * 1.3 - t * 0.9) + 0.5 * sin((ws.x + ws.z) * k * 2.1 + t * 1.7));
                o.positionWS = ws;
                o.positionCS = TransformWorldToHClip(ws);
                o.color = v.color;
                o.fog = ComputeFogFactor(o.positionCS.z);
                o.shore = v.uv;
                return o;
            }

            half4 frag(Varyings i, bool front : SV_IsFrontFace) : SV_Target
            {
                // Faceted look: the normal of each little triangle.
                float3 n = normalize(cross(ddy(i.positionWS), ddx(i.positionWS)));
                if (n.y < 0) n = -n;
                float3 viewDir = normalize(GetWorldSpaceViewDir(i.positionWS));
                Light light = GetMainLight();
                half ndl = saturate(dot(n, light.direction)) * 0.6 + 0.4;
                half3 col = i.color.rgb * light.color * ndl + i.color.rgb * SampleSH(n) * 0.4;
                half fresnel = pow(1 - saturate(dot(n, viewDir)), 4);
                col = lerp(col, _SkyColor.rgb, fresnel * 0.6);
                float3 h = normalize(light.direction + viewDir);
                col += light.color * pow(saturate(dot(n, h)), 120) * 1.2;
                // Surf washing in and out along the shore, as in the 2D game.
                half wave = 0.5 + 0.5 * sin(_Time.y * 1.6 + i.shore.y);
                half foam = smoothstep(0.55 + 0.35 * wave, 1.0, i.shore.x);
                col = lerp(col, half3(1, 1, 1), foam * 0.75);
                half alpha = saturate(i.color.a + fresnel * 0.3 + foam * 0.5);
                col = MixFog(col, i.fog);
                return half4(col, alpha);
            }
            ENDHLSL
        }
    }
}
