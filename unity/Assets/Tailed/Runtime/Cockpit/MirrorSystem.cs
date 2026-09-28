using Tailed.Map;
using Tailed.Vehicles;
using MeshBuilder = Tailed.Map.MeshBuilder;
using System.Linq;
using UnityEngine;
using UnityEngine.Rendering.Universal;
using UnityEngine.UI;

namespace Tailed.Cockpit
{
    /// <summary>
    /// Optically simulated mirrors for the local car (GD §12, architecture §9.2). Each mirror renders
    /// from the driver's eye reflected across the mirror plane, with an off-axis frustum through the
    /// mirror's corners and the near plane on its surface — so what it shows depends on head position.
    /// The passenger mirror is convex (wider, smaller image).
    /// </summary>
    [DefaultExecutionOrder(100)]
    public sealed class MirrorSystem : MonoBehaviour
    {
        sealed class Mirror
        {
            public Transform Surface;
            public Camera Cam;
            public RenderTexture Rt;
            public Material Mat;
            public float Width, Height, Convex;
            public Glance Kind;
            public RectTransform Hud;
            public RawImage HudImage;
            public Vector2 HudPos, HudSize;
            public float ResizeCooldown;
            public float ScreenPx;
            /// <summary>Un-widened near-plane extents in camera space (l, r, b, t) and near distance.</summary>
            public Vector4 Extents;
            public float Near;
        }

        /// <summary>Mirror render texels per screen pixel covered (a little supersampling keeps glyph edges crisp).</summary>
        public const float Supersample = 1.25f;
        public const int MinTexels = 96, MaxTexels = 2048;

        PlayerCar _car;
        public static MirrorSystem Local { get; private set; }

        /// <summary>World position of the reflected eye for a mirror (its camera), for legibility reports.</summary>
        public Vector3 VirtualEye(Glance g) => (g == Glance.Left ? _left : g == Glance.Right ? _right : _rear).Cam.transform.position;
        /// <summary>
        /// Where a world point seen by a mirror appears on the mirror glass (world space), or null if the
        /// mirror doesn't show it. Accounts for convex widening.
        /// </summary>
        public Vector3? PointOnGlass(Glance g, Vector3 world, out Vector2 viewport)
        {
            var m = g == Glance.Left ? _left : g == Glance.Right ? _right : _rear;
            var vp = m.Cam.WorldToViewportPoint(world);
            viewport = vp;
            if (!m.Cam.enabled && m.ScreenPx <= 0f) { }
            if (vp.z <= m.Near || vp.x < 0f || vp.x > 1f || vp.y < 0f || vp.y > 1f) return null;
            var e = m.Extents;
            var local = new Vector3(Mathf.Lerp(e.x, e.y, vp.x), Mathf.Lerp(e.z, e.w, vp.y), m.Near);
            return m.Cam.transform.TransformPoint(local);
        }

        /// <summary>Chase view: the HUD strip for a mirror (for placing readouts next to it).</summary>
        public RectTransform HudOf(Glance g) => (g == Glance.Left ? _left : g == Glance.Right ? _right : _rear).HudImage?.rectTransform;

        public float ConvexOf(Glance g) => (g == Glance.Left ? _left : g == Glance.Right ? _right : _rear).Convex;
        public RenderTexture RtOf(Glance g) => (g == Glance.Left ? _left : g == Glance.Right ? _right : _rear).Rt;
        public float VerticalAngleOf(Glance g)
        {
            var m = g == Glance.Left ? _left : g == Glance.Right ? _right : _rear;
            float d = Vector3.Distance(m.Cam.transform.position, m.Surface.position);
            return 2f * Mathf.Atan(m.Height * 0.5f * m.Convex / d);
        }
        Mirror _rear, _left, _right;
        GameObject _hud;
        static readonly int MirrorVP = Shader.PropertyToID("_MirrorVP");
        public static int MirrorLayer = 10;

        public void Init(PlayerCar car)
        {
            _car = car;
            if (car == PlayerCar.Local) Local = this;
            var s = car.Shape;
            // Mirrors are "adjusted" once for the neutral seating position, like a real driver would.
            var eye = s.Eye;
            // Aimed like a real driver sets it: rear window framed, road visible a few metres back.
            _rear = Make("RearView", s.RearViewMirror, eye, new Vector3(0f, 0.25f, -25f), 0.3f, 0.085f, 1f, 640, 180);
            _left = Make("LeftMirror", s.LeftMirror + new Vector3(-0.02f, 0f, -0.01f), eye, new Vector3(-3.2f, 0.9f, -30f), 0.21f, 0.14f, 1f, 320, 214);
            _right = Make("RightMirror", s.RightMirror + new Vector3(0.02f, 0f, -0.01f), eye, new Vector3(4.5f, 0.9f, -30f), 0.21f, 0.14f, 1.45f, 320, 214);
            _rear.Kind = Glance.Rear; _left.Kind = Glance.Left; _right.Kind = Glance.Right;
            BuildHud();
        }

        Mirror Make(string name, Vector3 localPos, Vector3 eye, Vector3 aimLocal, float w, float h, float convex, int rtW, int rtH)
        {
            var m = new Mirror { Width = w, Height = h, Convex = convex };
            // Aim: the surface normal bisects the directions to the eye and to the target behind.
            var toEye = (eye - localPos).normalized;
            var toTarget = (aimLocal - localPos).normalized;
            var normal = (toEye + toTarget).normalized;

            var go = GameObject.CreatePrimitive(PrimitiveType.Quad);
            DestroyImmediate(go.GetComponent<Collider>()); // now: parenting under the car rigidbody comes next
            go.name = name;
            go.layer = MirrorLayer;
            m.Surface = go.transform;
            m.Surface.SetParent(transform, false);
            m.Surface.localPosition = localPos;
            // Quad faces -z: point its visible side along the normal.
            m.Surface.localRotation = Quaternion.LookRotation(-normal, Vector3.up);
            m.Surface.localScale = new Vector3(w, h, 1f);

            // Housing: a dark shell just behind the glass, oriented with it.
            var housing = new GameObject(name + "Housing");
            housing.transform.SetParent(m.Surface, false);
            housing.transform.localPosition = new Vector3(0f, 0f, 0.035f);
            housing.transform.localScale = new Vector3(1.12f, 1.2f, 1f);
            housing.AddComponent<MeshFilter>().sharedMesh = HousingMesh;
            housing.AddComponent<MeshRenderer>().sharedMaterial = VehicleMaterials.Driver;

            m.Rt = NewRt(name, rtW, rtH);
            m.Mat = new Material(Shader.Find("Tailed/Mirror"));
            m.Mat.SetTexture("_MainTex", m.Rt);
            m.Mat.SetFloat("_Dirt", name == "RearView" ? 0.05f : 0.35f); // wing mirrors live outside
            go.GetComponent<MeshRenderer>().sharedMaterial = m.Mat;
            go.GetComponent<MeshRenderer>().shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;

            var camGo = new GameObject(name + "Cam");
            camGo.transform.SetParent(transform, false);
            m.Cam = camGo.AddComponent<Camera>();
            m.Cam.targetTexture = m.Rt;
            m.Cam.depth = -10;
            m.Cam.cullingMask = ~(1 << MirrorLayer);
            m.Cam.farClipPlane = 350f;
            var data = camGo.AddComponent<UniversalAdditionalCameraData>();
            data.renderShadows = false;
            data.renderPostProcessing = false;
            data.antialiasing = AntialiasingMode.None;
            data.requiresDepthTexture = false;
            data.requiresColorTexture = false;
            var optics = camGo.AddComponent<OpticsCamera>();
            optics.Magnification = 1f / convex;
            optics.IsMirror = true;
            return m;
        }

        static RenderTexture NewRt(string name, int w, int h) =>
            new RenderTexture(w, h, 24, RenderTextureFormat.DefaultHDR) { name = name + "RT", antiAliasing = 4, filterMode = FilterMode.Bilinear };

        /// <summary>Match the mirror's render resolution to the screen area it covers.</summary>
        void UpdateResolution(Mirror m, float screenPx, float dt)
        {
            m.ScreenPx = screenPx;
            m.Cam.enabled = screenPx > 3f;
            m.ResizeCooldown -= dt;
            if (!m.Cam.enabled || m.ResizeCooldown > 0f) return;
            int want = Mathf.Clamp(Mathf.CeilToInt(screenPx * Supersample / 32f) * 32, MinTexels, MaxTexels);
            if (want <= m.Rt.width * 1.15f && want >= m.Rt.width * 0.6f) return;
            m.ResizeCooldown = 0.25f;
            int h = Mathf.Max(32, Mathf.RoundToInt(want * m.Height / m.Width));
            var old = m.Rt;
            m.Rt = NewRt(m.Surface.name, want, h);
            m.Cam.targetTexture = m.Rt;
            m.Mat.SetTexture("_MainTex", m.Rt);
            if (m.HudImage != null) m.HudImage.texture = m.Rt;
            old.Release();
            Destroy(old);
        }

        /// <summary>Horizontal screen pixels covered by the mirror surface as seen by <paramref name="cam"/> (0 if not visible).</summary>
        static float ScreenWidth(Mirror m, Camera cam)
        {
            float minX = float.MaxValue, maxX = float.MinValue, minY = float.MaxValue, maxY = float.MinValue;
            int inFront = 0;
            foreach (var c in Corners)
            {
                var sp = cam.WorldToScreenPoint(m.Surface.TransformPoint(c));
                if (sp.z <= 0f) continue;
                inFront++;
                minX = Mathf.Min(minX, sp.x); maxX = Mathf.Max(maxX, sp.x);
                minY = Mathf.Min(minY, sp.y); maxY = Mathf.Max(maxY, sp.y);
            }
            if (inFront == 0 || maxX < 0f || minX > cam.pixelWidth || maxY < 0f || minY > cam.pixelHeight) return 0f;
            return Mathf.Min(maxX - minX, cam.pixelWidth * 2f);
        }

        static readonly Vector3[] Corners = { new Vector3(-0.5f, -0.5f, 0), new Vector3(0.5f, -0.5f, 0), new Vector3(0.5f, 0.5f, 0), new Vector3(-0.5f, 0.5f, 0) };

        /// <summary>Readout for tests: each mirror's covered screen width and render resolution.</summary>
        public string Describe() =>
            string.Join(" ", new[] { _left, _rear, _right }.Select(m => $"{m.Kind}:{m.ScreenPx:0}px→{m.Rt.width}x{m.Rt.height}{(m.Cam.enabled ? "" : "(off)")}"));

        static Mesh _housing;
        static Mesh HousingMesh
        {
            get
            {
                if (_housing != null) return _housing;
                var mb = new MeshBuilder();
                mb.LocalBox(new Vector3(-0.5f, -0.5f, -0.03f), new Vector3(0.5f, 0.5f, 0.03f), new Color(0.16f, 0.16f, 0.18f));
                return _housing = mb.Build("MirrorHousing");
            }
        }

        void LateUpdate()
        {
            // Must run after CameraDirector moved the eye (script execution order: late).
            var eye = _car.Eye.position;
            Place(_rear, eye);
            Place(_left, eye);
            Place(_right, eye);
            var dir = CameraDirector.Instance;
            bool chase = dir != null && dir.Mode == CameraMode.Chase && dir.Target == _car;
            bool cockpit = dir != null && dir.Mode == CameraMode.Cockpit && dir.Target == _car;
            if (_hud != null) _hud.SetActive(chase);
            var cam = Camera.main;
            float dt = Time.unscaledDeltaTime;
            foreach (var m in new[] { _left, _rear, _right })
            {
                float px = 0f;
                if (cockpit && cam != null) px = ScreenWidth(m, cam);
                else if (chase && m.Hud != null)
                {
                    // Chase view: a glance enlarges that mirror's strip to centre stage.
                    bool big = dir.Glance == m.Kind;
                    var size = big ? m.HudSize * (m.Kind == Glance.Rear ? 2.6f : 3.2f) : m.HudSize;
                    var pos = big ? new Vector2(0, -40) : m.HudPos;
                    float k = 1f - Mathf.Exp(-dt * 16f);
                    m.Hud.sizeDelta = Vector2.Lerp(m.Hud.sizeDelta, size + new Vector2(12, 12), k);
                    m.Hud.anchoredPosition = Vector2.Lerp(m.Hud.anchoredPosition, pos, k);
                    if (big) m.Hud.SetAsLastSibling();
                    px = m.HudImage.rectTransform.rect.width * m.Hud.lossyScale.x;
                }
                UpdateResolution(m, px, dt);
            }
        }

        static void Place(Mirror m, Vector3 eye)
        {
            var t = m.Surface;
            var n = -t.forward; // points towards the driver
            var centre = t.position;
            float dist = Vector3.Dot(eye - centre, n);
            if (dist < 0.01f) return; // eye behind the mirror: nothing sensible to show
            var reflected = eye - 2f * dist * n;
            var up = Vector3.ProjectOnPlane(t.up, n).normalized;
            m.Cam.transform.SetPositionAndRotation(reflected, Quaternion.LookRotation(n, up));

            // Corners in camera space; the mirror plane is perpendicular to the view axis at distance `dist`.
            var inv = m.Cam.transform.worldToLocalMatrix;
            float l = float.MaxValue, r = float.MinValue, b = float.MaxValue, top = float.MinValue;
            foreach (var c in new[] { new Vector3(-0.5f, -0.5f, 0), new Vector3(0.5f, -0.5f, 0), new Vector3(0.5f, 0.5f, 0), new Vector3(-0.5f, 0.5f, 0) })
            {
                var p = inv.MultiplyPoint3x4(t.TransformPoint(c));
                l = Mathf.Min(l, p.x); r = Mathf.Max(r, p.x); b = Mathf.Min(b, p.y); top = Mathf.Max(top, p.y);
            }
            float near = dist;
            m.Extents = new Vector4(l, r, b, top);
            m.Near = near;
            var narrow = Matrix4x4.Frustum(l, r, b, top, near, m.Cam.farClipPlane);
            float k = m.Convex;
            m.Cam.projectionMatrix = Matrix4x4.Frustum(l * k, r * k, b * k, top * k, near, m.Cam.farClipPlane);
            m.Mat.SetMatrix(MirrorVP, narrow * m.Cam.worldToCameraMatrix);
        }

        void BuildHud()
        {
            // Third person: a mirror strip at the top of the screen showing the same render textures.
            _hud = new GameObject("MirrorHud");
            var canvas = _hud.AddComponent<Canvas>();
            canvas.renderMode = RenderMode.ScreenSpaceOverlay;
            canvas.sortingOrder = 5;
            var scaler = _hud.AddComponent<CanvasScaler>();
            scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
            scaler.referenceResolution = new Vector2(1920, 1080);
            Strip(_left, new Vector2(-600, -20), new Vector2(270, 180));
            Strip(_rear, new Vector2(0, -20), new Vector2(560, 158));
            Strip(_right, new Vector2(600, -20), new Vector2(270, 180));
            _hud.SetActive(false);
        }

        void Strip(Mirror m, Vector2 pos, Vector2 size)
        {
            var go = new GameObject(m.Surface.name + "Hud");
            go.transform.SetParent(_hud.transform, false);
            var frame = go.AddComponent<Image>();
            frame.color = new Color(0.1f, 0.1f, 0.12f, 1f);
            var rt = go.GetComponent<RectTransform>();
            rt.anchorMin = rt.anchorMax = new Vector2(0.5f, 1f);
            rt.pivot = new Vector2(0.5f, 1f);
            rt.anchoredPosition = pos;
            rt.sizeDelta = size + new Vector2(12, 12);
            var img = new GameObject("View").AddComponent<RawImage>();
            img.transform.SetParent(go.transform, false);
            img.texture = m.Rt;
            // The mirror camera looks back at the driver, so its image is left-right swapped vs. the mirror.
            img.uvRect = new Rect(1, 0, -1, 1);
            var irt = img.rectTransform;
            irt.anchorMin = Vector2.zero;
            irt.anchorMax = Vector2.one;
            irt.offsetMin = new Vector2(6, 6);
            irt.offsetMax = new Vector2(-6, -6);
            m.Hud = rt;
            m.HudImage = img;
            m.HudPos = pos;
            m.HudSize = size;
        }

        void OnDestroy()
        {
            foreach (var m in new[] { _rear, _left, _right })
                if (m != null && m.Rt != null) m.Rt.Release();
            if (_hud != null) Destroy(_hud);
        }
    }
}
