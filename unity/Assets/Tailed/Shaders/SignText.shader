// Pixel-font sign lettering from the shared glyph atlas (POI names). Unlit, alpha-tested.
Shader "Tailed/SignText"
{
    Properties
    {
        _GlyphAtlas ("Glyph atlas", 2D) = "white" {}
        _Color ("Ink", Color) = (1, 1, 1, 1)
    }
    SubShader
    {
        Tags { "RenderType" = "TransparentCutout" "Queue" = "AlphaTest" "RenderPipeline" = "UniversalPipeline" }
        Pass
        {
            Name "Unlit"
            Tags { "LightMode" = "UniversalForward" }
            Cull Off
            HLSLPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #pragma multi_compile_fog
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
            TEXTURE2D(_GlyphAtlas); SAMPLER(sampler_GlyphAtlas);
            CBUFFER_START(UnityPerMaterial)
                float4 _Color;
            CBUFFER_END
            struct Attributes { float4 positionOS : POSITION; float2 uv : TEXCOORD0; float4 color : COLOR; };
            struct Varyings { float4 positionCS : SV_POSITION; float2 uv : TEXCOORD0; float4 color : COLOR; float fog : TEXCOORD1; };
            Varyings vert(Attributes i)
            {
                Varyings o;
                o.positionCS = TransformObjectToHClip(i.positionOS.xyz);
                o.uv = i.uv; o.color = i.color * _Color;
                o.fog = ComputeFogFactor(o.positionCS.z);
                return o;
            }
            half4 frag(Varyings i) : SV_Target
            {
                float a = SAMPLE_TEXTURE2D(_GlyphAtlas, sampler_GlyphAtlas, i.uv).r;
                clip(a - 0.5);
                return half4(MixFog(i.color.rgb, i.fog), 1);
            }
            ENDHLSL
        }
    }
}
