using System.Collections.Generic;
using System.Linq;
using Tailed.Map;
using Tailed.UI;
using Tailed.Vehicles;
using UnityEngine;
using UnityEngine.UI;

namespace Tailed.Cockpit
{
    /// <summary>
    /// Foveal plate readouts: when the driver looks straight at a plate (cockpit, near the centre of view)
    /// or glances at a mirror that shows one, a large copy of the plate pops up beside it — rendered at the
    /// legibility the driver's eye actually has (same function and mip curve as the plate shader). The
    /// readout removes the screen-resolution bottleneck, not the distance/angle/occlusion rules: far or
    /// blocked plates stay unreadable.
    /// </summary>
    [DefaultExecutionOrder(200)]
    public sealed class PlateReadout : MonoBehaviour
    {
        const int Slots = 3;
        const float DirectCone = 10f, MaxDistance = 40f;
        static readonly int Chars0 = Shader.PropertyToID("_Chars0"), Chars1 = Shader.PropertyToID("_Chars1");
        static readonly int LodId = Shader.PropertyToID("_Lod"), AlphaId = Shader.PropertyToID("_Alpha");

        sealed class Slot { public RectTransform Rect; public Material Mat; public float Alpha; public VehicleView Owner; public bool Front; }

        PlayerCar _car;
        Canvas _canvas;
        readonly Slot[] _slots = new Slot[Slots];
        readonly RaycastHit[] _hits = new RaycastHit[16];

        struct Candidate { public VehicleView View; public bool Front; public Vector2 Screen; public float Legibility, Priority; }

        public static PlateReadout Create(PlayerCar car)
        {
            var r = new GameObject("PlateReadouts").AddComponent<PlateReadout>();
            r._car = car;
            r._canvas = UiKit.Canvas("PlateReadoutCanvas", 20);
            r._canvas.transform.SetParent(r.transform, false);
            var shader = Shader.Find("Tailed/PlateUI");
            for (int i = 0; i < Slots; i++)
            {
                var rt = UiKit.Rect(r._canvas.transform, "Readout", Vector2.zero, Vector2.zero, new Vector2(0.5f, 0f), Vector2.zero, new Vector2(232, 60));
                var frame = rt.gameObject.AddComponent<Image>();
                frame.color = new Color(0f, 0f, 0f, 0.55f);
                frame.raycastTarget = false;
                var plate = UiKit.Stretch(rt, "Plate", 5);
                var img = plate.gameObject.AddComponent<RawImage>();
                img.raycastTarget = false;
                var mat = new Material(shader);
                mat.SetTexture("_GlyphAtlas", Glyphs.Atlas);
                img.material = mat;
                r._slots[i] = new Slot { Rect = rt, Mat = mat };
                rt.gameObject.SetActive(false);
            }
            return r;
        }

        void OnDestroy() { if (_canvas != null) Destroy(_canvas.gameObject); }

        void LateUpdate()
        {
            var dir = CameraDirector.Instance;
            var cam = Camera.main;
            var list = new List<Candidate>();
            if (_car != null && dir != null && cam != null && dir.Target == _car && Optics.PlateReadouts && dir.Mode != CameraMode.Free)
            {
                var mirrors = MirrorSystem.Local;
                if (dir.Glance != Glance.None && mirrors != null) CollectMirror(dir, mirrors, cam, list);
                else if (dir.Mode == CameraMode.Cockpit) CollectDirect(cam, list);
            }
            var chosen = list.OrderByDescending(c => c.Priority).Take(Slots).ToList();
            float scale = _canvas.GetComponent<RectTransform>().localScale.x;
            for (int i = 0; i < Slots; i++)
            {
                var s = _slots[i];
                bool on = i < chosen.Count;
                s.Alpha = Mathf.MoveTowards(s.Alpha, on ? 1f : 0f, Time.unscaledDeltaTime * 8f);
                s.Rect.gameObject.SetActive(s.Alpha > 0.01f);
                if (!on) continue;
                var c = chosen[i];
                var id = c.View.Identity;
                int G(int k) => k < id.Plate.Length ? Glyphs.Index(id.Plate[k]) : 37;
                s.Mat.SetVector(Chars0, new Vector4(G(0), G(1), G(2), G(3)));
                s.Mat.SetVector(Chars1, new Vector4(G(4), G(5), G(6), 37));
                s.Mat.SetFloat(LodId, Optics.LegibilityLod(c.Legibility));
                s.Mat.SetFloat(AlphaId, s.Alpha);
                s.Rect.GetComponent<Image>().color = new Color(0f, 0f, 0f, 0.55f * s.Alpha);
                // Above the plate, nudged apart if two readouts would overlap.
                var pos = c.Screen / scale + new Vector2(0f, 26f + i * 4f);
                for (int k = 0; k < i; k++)
                    if (Vector2.Distance(pos, _slots[k].Rect.anchoredPosition) < 64f) pos.y += 68f;
                s.Rect.anchoredPosition = pos;
            }
        }

        void CollectDirect(Camera cam, List<Candidate> list)
        {
            var eye = _car.Eye.position;
            foreach (var v in VehicleView.Active)
            {
                if (v == null || !v.gameObject.activeInHierarchy || (v.transform.position - eye).sqrMagnitude > MaxDistance * MaxDistance) continue;
                foreach (bool front in new[] { true, false })
                {
                    var (pos, normal) = Plate(v, front);
                    var toPlate = pos - eye;
                    float angle = Vector3.Angle(cam.transform.forward, toPlate);
                    if (angle > DirectCone) continue;
                    float facing = Vector3.Dot(normal, -toPlate.normalized);
                    if (facing < 0.15f) continue;
                    var sp = cam.WorldToScreenPoint(pos);
                    if (sp.z <= 0f || !Visible(eye, pos, normal, v)) continue;
                    float leg = Optics.Legibility(toPlate.magnitude, 1f, facing);
                    if (leg < 0.12f) continue;
                    list.Add(new Candidate { View = v, Front = front, Screen = sp, Legibility = leg, Priority = 100f - angle * 5f + leg * 10f });
                }
            }
        }

        void CollectMirror(CameraDirector dir, MirrorSystem mirrors, Camera cam, List<Candidate> list)
        {
            var g = dir.Glance;
            var virtualEye = mirrors.VirtualEye(g);
            float mag = 1f / mirrors.ConvexOf(g);
            RectTransform hud = dir.Mode == CameraMode.Chase ? mirrors.HudOf(g) : null;
            foreach (var v in VehicleView.Active)
            {
                if (v == null || !v.gameObject.activeInHierarchy || (v.transform.position - virtualEye).sqrMagnitude > MaxDistance * MaxDistance) continue;
                foreach (bool front in new[] { true, false })
                {
                    var (pos, normal) = Plate(v, front);
                    var glass = mirrors.PointOnGlass(g, pos, out var vp);
                    if (glass == null) continue;
                    var toPlate = pos - virtualEye;
                    float facing = Vector3.Dot(normal, -toPlate.normalized);
                    if (facing < 0.15f || !Visible(virtualEye, pos, normal, v)) continue;
                    float leg = Optics.Legibility(toPlate.magnitude, mag, facing);
                    if (leg < 0.12f) continue;
                    Vector2 screen;
                    if (hud != null)
                    {
                        // HUD strip shows the mirror image flipped horizontally (like the glass does).
                        var r = hud.rect;
                        var local = new Vector2(Mathf.Lerp(r.xMax, r.xMin, vp.x), Mathf.Lerp(r.yMin, r.yMax, vp.y));
                        screen = RectTransformUtility.WorldToScreenPoint(null, hud.TransformPoint(local));
                    }
                    else
                    {
                        var sp = cam.WorldToScreenPoint(glass.Value);
                        if (sp.z <= 0f) continue;
                        screen = sp;
                    }
                    list.Add(new Candidate { View = v, Front = front, Screen = screen, Legibility = leg, Priority = 200f - toPlate.magnitude + leg * 10f });
                }
            }
        }

        static (Vector3 pos, Vector3 normal) Plate(VehicleView v, bool front)
        {
            var shape = VehicleMeshFactory.Shape(v.Identity.ModelId);
            var t = v.transform;
            return front ? (t.TransformPoint(shape.FrontPlate), t.forward) : (t.TransformPoint(shape.RearPlate), -t.forward);
        }

        /// <summary>Clear line from the (virtual) eye to the plate: buildings and other vehicles block it.</summary>
        bool Visible(Vector3 from, Vector3 plate, Vector3 normal, VehicleView target)
        {
            var to = plate + normal * 0.06f;
            var d = to - from;
            int mask = Layers.BuildingsMask | (1 << Layers.Vehicles);
            int n = Physics.RaycastNonAlloc(from, d.normalized, _hits, d.magnitude, mask, QueryTriggerInteraction.Ignore);
            for (int i = 0; i < n; i++)
            {
                var hitView = _hits[i].collider.GetComponentInParent<VehicleView>();
                if (hitView == target) continue;
                return false;
            }
            return true;
        }
    }
}
