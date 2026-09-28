// HUD plate readout: the same plate layout and glyph atlas as Tailed/Plate, blurred to an explicit
// legibility mip (what the driver's eye resolves) instead of by on-screen size.
Shader "Tailed/PlateUI"
{
    Properties
    {
        _MainTex ("Unused (uGUI)", 2D) = "white" {}
        _GlyphAtlas ("Glyph atlas", 2D) = "white" {}
        _PlateColor ("Plate", Color) = (0.97, 0.94, 0.82, 1)
        _InkColor ("Ink", Color) = (0.08, 0.14, 0.35, 1)
        _Chars0 ("Chars 0-3", Vector) = (37, 37, 37, 37)
        _Chars1 ("Chars 4-6", Vector) = (37, 37, 37, 37)
        _Lod ("Legibility mip", Float) = 0
        _Alpha ("Alpha", Float) = 1
    }
    SubShader
    {
        Tags { "Queue" = "Transparent" "RenderType" = "Transparent" "IgnoreProjector" = "True" }
        Cull Off ZWrite Off ZTest Always
        Blend SrcAlpha OneMinusSrcAlpha
        Pass
        {
            HLSLPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
            TEXTURE2D(_GlyphAtlas); SAMPLER(sampler_GlyphAtlas);
            float4 _PlateColor, _InkColor, _Chars0, _Chars1;
            float _Lod, _Alpha;
            struct A { float4 positionOS : POSITION; float2 uv : TEXCOORD0; float4 color : COLOR; };
            struct V { float4 positionCS : SV_POSITION; float2 uv : TEXCOORD0; float4 color : COLOR; };
            V vert(A i) { V o; o.positionCS = TransformObjectToHClip(i.positionOS.xyz); o.uv = i.uv; o.color = i.color; return o; }
            float CharAt(float slot)
            {
                if (slot < 0.5) return _Chars0.x; if (slot < 1.5) return _Chars0.y; if (slot < 2.5) return _Chars0.z;
                if (slot < 3.5) return 37;
                if (slot < 4.5) return _Chars0.w; if (slot < 5.5) return _Chars1.x; if (slot < 6.5) return _Chars1.y;
                return _Chars1.z;
            }
            half4 frag(V i) : SV_Target
            {
                float2 inner = float2((i.uv.x - 0.06) / 0.88, (i.uv.y - 0.14) / 0.72);
                float ux = inner.x * 8;
                float slot = floor(ux);
                float g = floor(CharAt(slot) + 0.5);
                float2 cell = float2(fmod(g, 8), floor(g / 8));
                float2 atlasUV = (cell + float2(frac(ux), inner.y)) / 8;
                float ink = (inner.x >= 0 && inner.x <= 1 && inner.y >= 0 && inner.y <= 1)
                    ? SAMPLE_TEXTURE2D_LOD(_GlyphAtlas, sampler_GlyphAtlas, atlasUV, _Lod).r : 0;
                float border = step(min(min(i.uv.x, 1 - i.uv.x) * 3.86, min(i.uv.y, 1 - i.uv.y)), 0.05);
                float3 c = lerp(_PlateColor.rgb, _InkColor.rgb, saturate(ink + border));
                return half4(c, _Alpha * i.color.a);
            }
            ENDHLSL
        }
    }
}
