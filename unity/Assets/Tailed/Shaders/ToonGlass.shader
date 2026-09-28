// Car glass: tinted, fresnel-brightened, see-through so the bean drivers are visible.
Shader "Tailed/ToonGlass"
{
    Properties
    {
        _Tint ("Tint", Color) = (1, 1, 1, 1)
        _Fresnel ("Fresnel", Range(0, 1)) = 0.5
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
                float4 _Tint;
                float _Fresnel;
            CBUFFER_END
            struct Attributes { float4 positionOS : POSITION; float3 normalOS : NORMAL; float4 color : COLOR; };
            struct Varyings { float4 positionCS : SV_POSITION; float3 positionWS : TEXCOORD0; float3 normalWS : TEXCOORD1; float4 color : COLOR; float fog : TEXCOORD2; };
            Varyings vert(Attributes i)
            {
                Varyings o;
                VertexPositionInputs p = GetVertexPositionInputs(i.positionOS.xyz);
                o.positionCS = p.positionCS; o.positionWS = p.positionWS;
                o.normalWS = TransformObjectToWorldNormal(i.normalOS);
                o.color = i.color * _Tint;
                o.fog = ComputeFogFactor(p.positionCS.z);
                return o;
            }
            half4 frag(Varyings i, bool front : SV_IsFrontFace) : SV_Target
            {
                float3 n = normalize(i.normalWS) * (front ? 1 : -1);
                float3 v = normalize(GetWorldSpaceViewDir(i.positionWS));
                float f = pow(1 - saturate(dot(n, v)), 3) * _Fresnel;
                float3 sky = SampleSH(reflect(-v, n));
                float3 c = lerp(i.color.rgb * (GetMainLight().color * 0.5 + SampleSH(n)), sky, f);
                // From inside the car (back faces) glass is nearly clear: it's what you drive through.
                float alpha = front ? saturate(i.color.a + f * 0.5) : i.color.a * 0.25;
                return half4(MixFog(c, i.fog), alpha);
            }
            ENDHLSL
        }
    }
}
