// Pools of street-lamp light on the ground: additive, scaled by the global night factor (0 by day).
Shader "Tailed/NightGlow"
{
    SubShader
    {
        Tags { "RenderType" = "Transparent" "Queue" = "Transparent-10" "RenderPipeline" = "UniversalPipeline" }
        Pass
        {
            Tags { "LightMode" = "UniversalForward" }
            Blend One One
            ZWrite Off
            Offset -1, -1
            HLSLPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
            float _TailedNight;
            struct A { float4 positionOS : POSITION; float4 color : COLOR; };
            struct V { float4 positionCS : SV_POSITION; float4 color : COLOR; };
            V vert(A i) { V o; o.positionCS = TransformObjectToHClip(i.positionOS.xyz); o.color = i.color; return o; }
            half4 frag(V i) : SV_Target { return half4(i.color.rgb * _TailedNight, 1); }
            ENDHLSL
        }
    }
}
