// Friendslop base look: flat vertex colours, soft two-tone light, cool shadow tint, faint rim.
// Everything static in the world (roads, buildings, props) uses this with vertex colours
// baked by the mesh builders, so the whole town is a handful of draw calls.
Shader "Tailed/Toon"
{
    Properties
    {
        _BaseColor ("Tint", Color) = (1, 1, 1, 1)
        _ShadeSoftness ("Shade softness", Range(0.01, 1)) = 0.25
        _ShadowTint ("Shadow tint", Color) = (0.55, 0.6, 0.85, 1)
        _AmbientStrength ("Ambient strength", Range(0, 2)) = 0.6
        _RimStrength ("Rim strength", Range(0, 1)) = 0.12
        _Wind ("Wind sway (foliage)", Range(0, 3)) = 0
    }

    SubShader
    {
        Tags { "RenderType" = "Opaque" "RenderPipeline" = "UniversalPipeline" "Queue" = "Geometry" }

        HLSLINCLUDE
        #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
        CBUFFER_START(UnityPerMaterial)
            float4 _BaseColor;
            float4 _ShadowTint;
            float _ShadeSoftness;
            float _AmbientStrength;
            float _RimStrength;
            float _Wind;
        CBUFFER_END
        float _TailedNight;
        float _TailedGust; // global wind strength multiplier (weather)

        // Foliage sway: displacement grows with height above the ground, phase varies across town
        // so trees don't move in lockstep. Applied identically in every pass (shadows match).
        float3 Sway(float3 ws)
        {
            if (_Wind <= 0) return ws;
            float h = max(0, ws.y - 1.2);
            float phase = dot(ws.xz, float2(0.13, 0.17));
            float t = _Time.y;
            float gust = 0.7 + 0.3 * sin(t * 0.23 + phase * 0.2);
            float2 d = float2(sin(t * 1.3 + phase), sin(t * 0.9 + phase * 1.7) * 0.6);
            ws.xz += d * h * h * 0.0035 * _Wind * gust * (1 + _TailedGust);
            return ws;
        }
        ENDHLSL

        Pass
        {
            Name "ForwardLit"
            Tags { "LightMode" = "UniversalForward" }

            HLSLPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #pragma multi_compile _ _MAIN_LIGHT_SHADOWS _MAIN_LIGHT_SHADOWS_CASCADE _MAIN_LIGHT_SHADOWS_SCREEN
            #pragma multi_compile_fragment _ _SHADOWS_SOFT _SHADOWS_SOFT_LOW _SHADOWS_SOFT_MEDIUM _SHADOWS_SOFT_HIGH
            #pragma multi_compile_fragment _ _SCREEN_SPACE_OCCLUSION
            #pragma multi_compile_fog
            #pragma multi_compile_instancing
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Lighting.hlsl"

            struct Attributes
            {
                float4 positionOS : POSITION;
                float3 normalOS : NORMAL;
                float4 color : COLOR;
                UNITY_VERTEX_INPUT_INSTANCE_ID
            };

            struct Varyings
            {
                float4 positionCS : SV_POSITION;
                float3 positionWS : TEXCOORD0;
                float3 normalWS : TEXCOORD1;
                float4 color : COLOR;
                float fogFactor : TEXCOORD2;
                UNITY_VERTEX_INPUT_INSTANCE_ID
            };

            Varyings vert(Attributes i)
            {
                Varyings o;
                UNITY_SETUP_INSTANCE_ID(i);
                UNITY_TRANSFER_INSTANCE_ID(i, o);
                float3 ws = Sway(TransformObjectToWorld(i.positionOS.xyz));
                o.positionCS = TransformWorldToHClip(ws);
                o.positionWS = ws;
                o.normalWS = TransformObjectToWorldNormal(i.normalOS);
                o.color = i.color * _BaseColor;
                o.fogFactor = ComputeFogFactor(o.positionCS.z);
                return o;
            }

            half4 frag(Varyings i) : SV_Target
            {
                UNITY_SETUP_INSTANCE_ID(i);
                float3 n = normalize(i.normalWS);
                Light light = GetMainLight(TransformWorldToShadowCoord(i.positionWS));
                float ndl = dot(n, light.direction);
                float lit = smoothstep(0.0, _ShadeSoftness, ndl) * light.shadowAttenuation;

                #if defined(_SCREEN_SPACE_OCCLUSION)
                    AmbientOcclusionFactor ao = GetScreenSpaceAmbientOcclusion(GetNormalizedScreenSpaceUV(i.positionCS));
                    float occlusion = ao.indirectAmbientOcclusion;
                #else
                    float occlusion = 1.0;
                #endif

                float3 albedo = i.color.rgb;
                float3 direct = lerp(_ShadowTint.rgb * 0.45, light.color, lit);
                float3 ambient = SampleSH(n) * _AmbientStrength * occlusion;
                float3 viewDir = normalize(GetWorldSpaceViewDir(i.positionWS));
                float rim = pow(1.0 - saturate(dot(n, viewDir)), 4.0) * _RimStrength;

                float3 color = albedo * (direct + ambient) + rim * light.color;
                // Windows (vertex alpha 0.95) light up at night — about 60% of them, varying per window.
                if (abs(i.color.a - 0.9) < 0.02) color = lerp(color, float3(1.0, 0.85, 0.6) * 3.0, _TailedNight);
                if (abs(i.color.a - 0.95) < 0.02 && _TailedNight > 0.01)
                {
                    float3 cell = floor(i.positionWS * float3(0.45, 0.31, 0.45));
                    float h = frac(sin(dot(cell, float3(12.9898, 78.233, 37.719))) * 43758.5453);
                    if (h > 0.4) color = lerp(color, float3(1.0, 0.82, 0.5) * (1.2 + h), _TailedNight * 0.9);
                }
                color = MixFog(color, i.fogFactor);
                return half4(color, 1);
            }
            ENDHLSL
        }

        Pass
        {
            Name "ShadowCaster"
            Tags { "LightMode" = "ShadowCaster" }
            ZWrite On ZTest LEqual ColorMask 0 Cull Back

            HLSLPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #pragma multi_compile_instancing
            #pragma multi_compile_vertex _ _CASTING_PUNCTUAL_LIGHT_SHADOW
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Shadows.hlsl"

            float3 _LightDirection;
            float3 _LightPosition;

            struct Attributes { float4 positionOS : POSITION; float3 normalOS : NORMAL; UNITY_VERTEX_INPUT_INSTANCE_ID };
            struct Varyings { float4 positionCS : SV_POSITION; };

            Varyings vert(Attributes i)
            {
                Varyings o;
                UNITY_SETUP_INSTANCE_ID(i);
                float3 positionWS = Sway(TransformObjectToWorld(i.positionOS.xyz));
                float3 normalWS = TransformObjectToWorldNormal(i.normalOS);
                #if _CASTING_PUNCTUAL_LIGHT_SHADOW
                    float3 lightDir = normalize(_LightPosition - positionWS);
                #else
                    float3 lightDir = _LightDirection;
                #endif
                float4 positionCS = TransformWorldToHClip(ApplyShadowBias(positionWS, normalWS, lightDir));
                #if UNITY_REVERSED_Z
                    positionCS.z = min(positionCS.z, UNITY_NEAR_CLIP_VALUE);
                #else
                    positionCS.z = max(positionCS.z, UNITY_NEAR_CLIP_VALUE);
                #endif
                o.positionCS = positionCS;
                return o;
            }

            half4 frag(Varyings i) : SV_Target { return 0; }
            ENDHLSL
        }

        Pass
        {
            Name "DepthOnly"
            Tags { "LightMode" = "DepthOnly" }
            ZWrite On ColorMask R

            HLSLPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #pragma multi_compile_instancing

            struct Attributes { float4 positionOS : POSITION; UNITY_VERTEX_INPUT_INSTANCE_ID };
            struct Varyings { float4 positionCS : SV_POSITION; };

            Varyings vert(Attributes i)
            {
                Varyings o;
                UNITY_SETUP_INSTANCE_ID(i);
                o.positionCS = TransformWorldToHClip(Sway(TransformObjectToWorld(i.positionOS.xyz)));
                return o;
            }

            half frag(Varyings i) : SV_Target { return i.positionCS.z; }
            ENDHLSL
        }

        Pass
        {
            Name "DepthNormals"
            Tags { "LightMode" = "DepthNormals" }
            ZWrite On

            HLSLPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #pragma multi_compile_instancing

            struct Attributes { float4 positionOS : POSITION; float3 normalOS : NORMAL; UNITY_VERTEX_INPUT_INSTANCE_ID };
            struct Varyings { float4 positionCS : SV_POSITION; float3 normalWS : TEXCOORD0; };

            Varyings vert(Attributes i)
            {
                Varyings o;
                UNITY_SETUP_INSTANCE_ID(i);
                o.positionCS = TransformWorldToHClip(Sway(TransformObjectToWorld(i.positionOS.xyz)));
                o.normalWS = TransformObjectToWorldNormal(i.normalOS);
                return o;
            }

            half4 frag(Varyings i) : SV_Target { return half4(NormalizeNormalPerPixel(i.normalWS), 0); }
            ENDHLSL
        }
    }
}
