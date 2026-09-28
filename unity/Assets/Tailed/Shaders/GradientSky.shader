// Clean stylised sky: three-colour vertical gradient plus a soft sun glow. No clouds (yet).
Shader "Tailed/GradientSky"
{
    Properties
    {
        _TopColor ("Top", Color) = (0.36, 0.58, 0.9, 1)
        _HorizonColor ("Horizon", Color) = (0.78, 0.87, 0.96, 1)
        _BottomColor ("Below horizon", Color) = (0.7, 0.78, 0.84, 1)
        _Exponent ("Gradient exponent", Range(0.1, 4)) = 0.6
        _SunGlow ("Sun glow", Range(0, 1)) = 0.35
    }
    SubShader
    {
        Tags { "Queue" = "Background" "RenderType" = "Background" "PreviewType" = "Skybox" "RenderPipeline" = "UniversalPipeline" }
        Cull Off ZWrite Off
        Pass
        {
            HLSLPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Lighting.hlsl"

            CBUFFER_START(UnityPerMaterial)
                float4 _TopColor, _HorizonColor, _BottomColor;
                float _Exponent, _SunGlow;
            CBUFFER_END

            struct Attributes { float4 positionOS : POSITION; };
            struct Varyings { float4 positionCS : SV_POSITION; float3 dir : TEXCOORD0; };

            Varyings vert(Attributes i)
            {
                Varyings o;
                o.positionCS = TransformObjectToHClip(i.positionOS.xyz);
                o.dir = i.positionOS.xyz;
                return o;
            }

            half4 frag(Varyings i) : SV_Target
            {
                float3 d = normalize(i.dir);
                float up = saturate(d.y);
                float3 sky = lerp(_HorizonColor.rgb, _TopColor.rgb, pow(up, _Exponent));
                sky = d.y < 0 ? lerp(_HorizonColor.rgb, _BottomColor.rgb, saturate(-d.y * 4)) : sky;
                float sun = saturate(dot(d, GetMainLight().direction));
                sky += GetMainLight().color * (pow(sun, 64) * _SunGlow + pow(sun, 800) * 2.0);
                return half4(sky, 1);
            }
            ENDHLSL
        }
    }
}
