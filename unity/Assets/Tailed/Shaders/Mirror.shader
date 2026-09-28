// Mirror surface: samples its mirror camera's render texture by projecting the surface point with
// that camera's (un-widened) view-projection, so the image is exact for the current eye position.
Shader "Tailed/Mirror"
{
    Properties
    {
        _MainTex ("Mirror view", 2D) = "black" {}
        _Tint ("Tint", Color) = (0.86, 0.88, 0.9, 1)
        _Dirt ("Grime", Range(0, 1)) = 0
    }
    SubShader
    {
        Tags { "RenderType" = "Opaque" "RenderPipeline" = "UniversalPipeline" "Queue" = "Geometry" }
        Pass
        {
            Name "Mirror"
            Tags { "LightMode" = "UniversalForward" }
            HLSLPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
            TEXTURE2D(_MainTex); SAMPLER(sampler_MainTex);
            CBUFFER_START(UnityPerMaterial)
                float4 _Tint;
                float4x4 _MirrorVP;
                float _Dirt;
            CBUFFER_END
            float _TailedRain;

            float Hash(float2 p) { return frac(sin(dot(p, float2(127.1, 311.7))) * 43758.5453); }
            struct Attributes { float4 positionOS : POSITION; };
            struct Varyings { float4 positionCS : SV_POSITION; float4 mirrorClip : TEXCOORD0; };
            Varyings vert(Attributes i)
            {
                Varyings o;
                float3 ws = TransformObjectToWorld(i.positionOS.xyz);
                o.positionCS = TransformWorldToHClip(ws);
                o.mirrorClip = mul(_MirrorVP, float4(ws, 1));
                return o;
            }
            half4 frag(Varyings i) : SV_Target
            {
                float2 uv = i.mirrorClip.xy / i.mirrorClip.w * 0.5 + 0.5;
                float shade = 1;
                if (_TailedRain > 0.5)
                {
                    // Raindrops on the glass: each grid cell may hold a drop that refracts and darkens.
                    float2 grid = uv * float2(26, 14);
                    float2 cell = floor(grid), f = frac(grid) - 0.5;
                    float h = Hash(cell);
                    float2 centre = float2(Hash(cell + 3.1), Hash(cell + 7.7)) - 0.5;
                    float d = length((f - centre * 0.6) * float2(1, 1.3));
                    float radius = 0.12 + h * 0.18;
                    if (h > 0.45 && d < radius) { uv -= (f - centre * 0.6) * 0.035; shade = 0.8 + 0.2 * d / radius; }
                }
                float3 c = SAMPLE_TEXTURE2D(_MainTex, sampler_MainTex, uv).rgb * _Tint.rgb * shade;
                float edge = smoothstep(0.25, 0.75, length(uv - 0.5));
                c *= 1 - _Dirt * (0.25 * edge + 0.1 * Hash(floor(uv * 90)));
                return half4(c, 1);
            }
            ENDHLSL
        }
    }
}
