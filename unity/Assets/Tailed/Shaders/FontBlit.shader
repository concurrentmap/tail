// Copies dynamic-font glyph coverage (alpha) into the plate/sign glyph atlas.
Shader "Tailed/FontBlit"
{
    Properties { _MainTex ("Font", 2D) = "white" {} }
    SubShader
    {
        Pass
        {
            ZTest Always ZWrite Off Cull Off
            Blend One One
            HLSLPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
            TEXTURE2D(_MainTex); SAMPLER(sampler_MainTex);
            struct A { float4 positionOS : POSITION; float2 uv : TEXCOORD0; };
            struct V { float4 positionCS : SV_POSITION; float2 uv : TEXCOORD0; };
            V vert(A i) { V o; o.positionCS = TransformObjectToHClip(i.positionOS.xyz); o.uv = i.uv; return o; }
            half4 frag(V i) : SV_Target { float a = SAMPLE_TEXTURE2D(_MainTex, sampler_MainTex, i.uv).a; return half4(a, a, a, 1); }
            ENDHLSL
        }
    }
}
