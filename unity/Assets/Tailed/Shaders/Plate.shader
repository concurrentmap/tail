// Licence plates. Legibility is computed from the *driver's eye* (or the optical path through a
// mirror), never from screen resolution: effective distance, viewing angle, visibility and (third
// person) whether the plate is in the driver's view set a minimum blur. Glyphs are a signed distance
// field thresholded with a one-pixel edge, so ink stays dark and plate stays light at any size
// (a mip-blurred bitmap averages them to grey); strokes thicken slightly as glyphs shrink.
// Globals come from Tailed.Cockpit.Optics per camera.
Shader "Tailed/Plate"
{
    Properties
    {
        _GlyphAtlas ("Glyph atlas", 2D) = "white" {}
        _PlateColor ("Plate", Color) = (0.97, 0.94, 0.82, 1)
        _InkColor ("Ink", Color) = (0.08, 0.14, 0.35, 1)
        // 9 m / 34 m for a real-size plate, × Glyphs.PlateScale (1.7) — keep in step with Optics.
        // Real-size plate: 9 m / 34 m, × Glyphs.PlateScale (1.7). Crisp until the knee (70% of the
        // cut-off), then falls off fast, like eyesight. Keep in step with Optics.
        _FullAt ("Crisp within (m)", Float) = 40.5
        _GoneAt ("Illegible beyond (m)", Float) = 57.8
    }
    SubShader
    {
        Tags { "RenderType" = "Opaque" "RenderPipeline" = "UniversalPipeline" "Queue" = "Geometry" }
        Pass
        {
            Name "ForwardLit"
            Tags { "LightMode" = "UniversalForward" }
            HLSLPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #pragma multi_compile_fog
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Lighting.hlsl"

            TEXTURE2D(_GlyphAtlas); SAMPLER(sampler_GlyphAtlas);
            CBUFFER_START(UnityPerMaterial)
                float4 _PlateColor, _InkColor;
                float _FullAt, _GoneAt;
            CBUFFER_END
            float4 _TailedEyePos;       // xyz = eye (reflected eye for mirror cameras)
            float4 _TailedEyeForward;   // xyz = gaze (zero vector = no cone), w = cos(half cone; may be negative)
            float _TailedMagnification; // < 1 for convex mirrors
            float _TailedVisibility;    // 1 = clear day; lower at night / in rain
            float _TailedMirrored;      // 1 when rendering for a mirror with readable (pre-reversed) plates

            struct Attributes
            {
                float4 positionOS : POSITION; float3 normalOS : NORMAL;
                float2 uv : TEXCOORD0; float4 chars0 : TEXCOORD1; float4 chars1 : TEXCOORD2;
            };
            struct Varyings
            {
                float4 positionCS : SV_POSITION; float3 positionWS : TEXCOORD0; float3 normalWS : TEXCOORD1;
                // Glyph indices must not be interpolated: 16.0 can arrive as 15.9999 and select the wrong atlas row.
                float2 uv : TEXCOORD2; nointerpolation float4 chars0 : TEXCOORD3; nointerpolation float4 chars1 : TEXCOORD4; float fog : TEXCOORD5;
            };

            Varyings vert(Attributes i)
            {
                Varyings o;
                VertexPositionInputs p = GetVertexPositionInputs(i.positionOS.xyz);
                o.positionCS = p.positionCS; o.positionWS = p.positionWS;
                o.normalWS = TransformObjectToWorldNormal(i.normalOS);
                o.uv = i.uv; o.chars0 = i.chars0; o.chars1 = i.chars1;
                o.fog = ComputeFogFactor(p.positionCS.z);
                return o;
            }

            float CharAt(float slot, float4 c0, float4 c1)
            {
                // Slots 0-2 letters, 3 = gap, 4-7 digits.
                if (slot < 0.5) return c0.x; if (slot < 1.5) return c0.y; if (slot < 2.5) return c0.z;
                if (slot < 3.5) return 37; // blank
                if (slot < 4.5) return c0.w; if (slot < 5.5) return c1.x; if (slot < 6.5) return c1.y;
                return c1.z;
            }

            half4 frag(Varyings i) : SV_Target
            {
                float3 n = normalize(i.normalWS);
                float3 eye = _TailedEyePos.xyz;
                float3 toEye = eye - i.positionWS;
                float dist = length(toEye);
                toEye /= max(dist, 1e-4);
                float facing = saturate(dot(n, toEye));
                float eff = dist / max(_TailedMagnification, 0.05) / max(facing, 0.2) / max(_TailedVisibility, 0.05);
                // Gaze cone (third person): plates outside the driver's field of view are unreadable.
                // Enabled by a non-zero gaze vector; the cone is wider than 180°, so its cosine is negative.
                if (dot(_TailedEyeForward.xyz, _TailedEyeForward.xyz) > 0.5 && dot(_TailedEyeForward.xyz, -toEye) < _TailedEyeForward.w) eff = 1e4;
                float legibility = 1 - saturate((eff - _FullAt) / (_GoneAt - _FullAt));

                // Glyph cell lookup.
                // Mirrors reverse text; unless 'hardcore mirrors' is on, draw plates pre-reversed so they read normally.
                float u = _TailedMirrored > 0.5 ? 1 - i.uv.x : i.uv.x;
                float2 inner = float2((u - 0.06) / 0.88, (i.uv.y - 0.14) / 0.72);
                float ux = inner.x * 8;
                float slot = floor(ux);
                float2 local = float2(frac(ux), inner.y);
                float g = floor(CharAt(slot, i.chars0, i.chars1) + 0.5);
                float2 cell = float2(fmod(g, 8), floor(g / 8));
                float2 atlasUV = (cell + local) / 8;

                // Screen-space gradients of atlasUV, from the continuous coordinates (frac() would
                // spike at slot boundaries). Anisotropic sampling filters each axis only as much as
                // it needs: the condensed glyphs are squeezed ~1.7:1, so one isotropic mip over-blurs.
                float2 gx = float2(ddx(inner.x), ddx(inner.y) / 8);
                float2 gy = float2(ddy(inner.x), ddy(inner.y) / 8);
                const float atlasPx = 1024;
                // Legibility floor: blur at least 2^lod texels along both axes (eye limit, not screen).
                float minTexels = exp2(pow(1 - legibility, 0.7) * 7.2);
                gx *= max(1, minTexels / max(length(gx) * atlasPx, 1e-4));
                gy *= max(1, minTexels / max(length(gy) * atlasPx, 1e-4));
                // Glyph height on screen (px) → stroke thickening for very small text (optical sizing).
                float texPerPx = max(length(gy), length(gx)) * atlasPx;
                float glyphPx = 100 / max(texPerPx, 1e-3);
                // Thicken strokes by up to ~0.12 screen px per side (more closes the counters of 6/8/9) as glyphs drop below ~10 px, so
                // sub-pixel strokes still cover whole pixels (field units: 1/(2·spread) per texel).
                float bold = min(saturate((10 - glyphPx) / 5) * 0.12 * texPerPx / (2 * 16), 0.15);
                // 4 rotated-grid taps across the pixel footprint, each thresholded with a quarter-pixel
                // edge, then averaged: exact-looking coverage of the true glyph shape (clean shapes at
                // 5 px) while each tap stays ink-or-plate (contrast doesn't wash out to grey).
                float2 hx = gx * 0.5, hy = gy * 0.5;
                float2 o0 = hx * 0.125 + hy * 0.375, o1 = hx * 0.375 - hy * 0.125;
                float f0 = SAMPLE_TEXTURE2D_GRAD(_GlyphAtlas, sampler_GlyphAtlas, atlasUV + o0, hx, hy).r;
                float f1 = SAMPLE_TEXTURE2D_GRAD(_GlyphAtlas, sampler_GlyphAtlas, atlasUV - o0, hx, hy).r;
                float f2 = SAMPLE_TEXTURE2D_GRAD(_GlyphAtlas, sampler_GlyphAtlas, atlasUV + o1, hx, hy).r;
                float f3 = SAMPLE_TEXTURE2D_GRAD(_GlyphAtlas, sampler_GlyphAtlas, atlasUV - o1, hx, hy).r;
                // Field change per half pixel: SDF units are 1/(2·spread) per mip-0 texel.
                float aa = max(0.5 * texPerPx / (2 * 16), 1e-3);
                float edge = 0.5 - bold;
                float crisp = 0.25 * (saturate((f0 - edge) / aa + 0.5) + saturate((f1 - edge) / aa + 0.5) +
                                      saturate((f2 - edge) / aa + 0.5) + saturate((f3 - edge) / aa + 0.5));
                // Past the eye limit, contrast goes rather than the shapes turning to hard blobs.
                float field = 0.25 * (f0 + f1 + f2 + f3);
                float soft = saturate((field - 0.3) * 2.5) * 0.6;
                float ink = lerp(soft, crisp, smoothstep(0.0, 0.45, legibility));
                ink *= (inner.x >= 0 && inner.x <= 1 && inner.y >= 0 && inner.y <= 1) ? 1 : 0;

                float border = step(min(min(i.uv.x, 1 - i.uv.x) * 3.86, min(i.uv.y, 1 - i.uv.y)), 0.05);
                float3 albedo = lerp(_PlateColor.rgb, _InkColor.rgb, saturate(ink + border));
                Light light = GetMainLight();
                float3 lighting = light.color * saturate(dot(n, light.direction) * 0.5 + 0.5) * 0.8 + SampleSH(n) * 0.6;
                // Retroreflective sheeting: plates hold their brightness in shade (less so at night).
                lighting = max(lighting, lerp(0.35, 0.95, saturate(_TailedVisibility)));
                float3 c = albedo * lighting;
                // Mostly exempt from distance fog: weather already lowers legibility via _TailedVisibility.
                return half4(lerp(MixFog(c, i.fog), c, 0.75), 1);
            }
            ENDHLSL
        }
    }
}
