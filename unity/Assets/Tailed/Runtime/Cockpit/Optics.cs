using UnityEngine;
using UnityEngine.Rendering;

namespace Tailed.Cockpit
{
    /// <summary>Pushes optics globals before each camera renders. One instance lives in the scene.</summary>
    public static class Optics
    {
        static readonly int EyePos = Shader.PropertyToID("_TailedEyePos");
        static readonly int EyeForward = Shader.PropertyToID("_TailedEyeForward");
        static readonly int Magnification = Shader.PropertyToID("_TailedMagnification");
        static readonly int Visibility = Shader.PropertyToID("_TailedVisibility");
        static readonly int Mirrored = Shader.PropertyToID("_TailedMirrored");

        /// <summary>Realistic mirror-reversed plates (off by default: reading mirror-writing under pressure isn't fun).</summary>
        public static bool HardcoreMirrors;
        /// <summary>Pop-up plate readouts at eye-legibility (accessibility option, off by default now plates are big). Menu toggle.</summary>
        public static bool PlateReadouts = false;

        /// <summary>
        /// Legibility band (m, effective distance) for the 1.7× plates. Eyesight isn't a linear fade:
        /// text is crisp until near the limit, then goes quickly — so crisp to 70% of the cut-off.
        /// Matches Tailed/Plate's _FullAt/_GoneAt.
        /// </summary>
        public const float GoneAt = 34f * Tailed.Vehicles.Glyphs.PlateScale, FullAt = GoneAt * 0.7f;

        /// <summary>The plate shader's legibility, on the CPU: 1 = crisp, 0 = unreadable.</summary>
        public static float Legibility(float distance, float magnification, float facing)
        {
            float eff = distance / Mathf.Max(magnification, 0.05f) / Mathf.Max(facing, 0.2f) / Mathf.Max(WorldVisibility, 0.05f);
            return 1f - Mathf.Clamp01((eff - FullAt) / (GoneAt - FullAt));
        }

        /// <summary>Glyph mip level for a legibility (matches Tailed/Plate).</summary>
        public static float LegibilityLod(float legibility) => Mathf.Pow(1f - legibility, 0.7f) * 7.2f;
        static bool _installed;

        /// <summary>1 = clear day. Night and rain lower it (GD §10).</summary>
        public static float WorldVisibility = 1f;

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        public static void Install()
        {
            if (_installed) return;
            _installed = true;
            RenderPipelineManager.beginCameraRendering += OnBeginCamera;
        }

        static void OnBeginCamera(ScriptableRenderContext ctx, Camera cam)
        {
            var o = cam.GetComponent<OpticsCamera>();
            Vector3 eye = o != null && o.Eye != null ? o.Eye.position : cam.transform.position;
            Vector4 fwd = Vector4.zero;
            if (o != null && o.UseGazeCone && o.Eye != null)
            {
                var f = o.Eye.forward;
                fwd = new Vector4(f.x, f.y, f.z, o.GazeCone);
            }
            var cmd = CommandBufferPool.Get("Tailed Optics");
            cmd.SetGlobalVector(EyePos, eye);
            cmd.SetGlobalVector(EyeForward, fwd);
            cmd.SetGlobalFloat(Magnification, o != null ? o.Magnification : 1f);
            cmd.SetGlobalFloat(Visibility, WorldVisibility);
            cmd.SetGlobalFloat(Mirrored, o != null && o.IsMirror && !HardcoreMirrors ? 1f : 0f);
            ctx.ExecuteCommandBuffer(cmd);
            CommandBufferPool.Release(cmd);
        }
    }
}
