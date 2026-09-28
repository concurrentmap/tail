using Tailed.Map;
using UnityEngine;

namespace Tailed.Vehicles
{
    /// <summary>
    /// The errand marker (GD §4): while the Mark is briefly at a real stop, a spinning gold gem floats
    /// over the car and a pulse ring spreads from it. It rides on the car's replicated flags like the
    /// brake lights — anyone who happens to be looking at the car sees it; nothing is computed about
    /// who can see what. Fake stops never show it. Tails remember it for the marking at the end.
    /// </summary>
    public sealed class ErrandMarker : MonoBehaviour
    {
        static Mesh _gemMesh, _ringMesh;
        static readonly Color Gold = new Color(1f, 0.78f, 0.2f);

        Transform _gem, _ring;
        Material _gemMat, _ringMat;
        float _height, _since;

        public static ErrandMarker Attach(Transform car, float carHeight)
        {
            var go = new GameObject("ErrandMarker");
            go.transform.SetParent(car, false);
            var m = go.AddComponent<ErrandMarker>();
            m._height = carHeight;
            var shader = Shader.Find("Tailed/Additive");
            m._gemMat = new Material(shader);
            m._ringMat = new Material(shader);
            m._gem = Child(go, "Gem", GemMesh(), m._gemMat);
            m._ring = Child(go, "Ring", RingMesh(), m._ringMat);
            go.SetActive(false);
            return m;
        }

        public void Show(bool on)
        {
            if (on == gameObject.activeSelf) return;
            if (on) _since = Time.time;
            gameObject.SetActive(on);
        }

        static Mesh GemMesh()
        {
            if (_gemMesh != null) return _gemMesh;
            var gem = new MeshBuilder();
            Vector3 top = new Vector3(0, 0.55f, 0), bottom = new Vector3(0, -0.55f, 0);
            var mid = new[] { new Vector3(0.4f, 0, 0), new Vector3(0, 0, 0.4f), new Vector3(-0.4f, 0, 0), new Vector3(0, 0, -0.4f) };
            var white = Color.white;
            for (int i = 0; i < 4; i++)
            {
                var a = mid[i]; var b = mid[(i + 1) % 4];
                gem.Polygon(new[] { top, b, a }, white, (top + a + b).normalized);       // faces point outward
                gem.Polygon(new[] { bottom, a, b }, white, (bottom + a + b).normalized);
            }
            return _gemMesh = gem.Build("ErrandGem");
        }

        static Mesh RingMesh()
        {
            if (_ringMesh != null) return _ringMesh;
            var ring = new MeshBuilder();
            const int n = 40;
            for (int i = 0; i < n; i++)
            {
                float a0 = i * Mathf.PI * 2f / n, a1 = (i + 1) * Mathf.PI * 2f / n;
                Vector3 P(float a, float r) => new Vector3(Mathf.Cos(a) * r, 0.07f, Mathf.Sin(a) * r);
                ring.Quad(P(a0, 1f), P(a1, 1f), P(a1, 1.18f), P(a0, 1.18f), Color.white, Vector3.up);
            }
            return _ringMesh = ring.Build("ErrandRing");
        }

        static Transform Child(GameObject parent, string name, Mesh mesh, Material mat)
        {
            var go = new GameObject(name);
            go.transform.SetParent(parent.transform, false);
            go.AddComponent<MeshFilter>().sharedMesh = mesh;
            var mr = go.AddComponent<MeshRenderer>();
            mr.sharedMaterial = mat;
            mr.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            return go.transform;
        }

        void LateUpdate()
        {
            float appear = Mathf.Clamp01((Time.time - _since) / 0.3f);
            _gem.localPosition = Vector3.up * (_height + 1.3f + 0.15f * Mathf.Sin(Time.time * 2.5f));
            _gem.rotation = Quaternion.Euler(0f, Time.time * 90f, 0f);
            _gem.localScale = Vector3.one * appear;
            _gemMat.color = Gold * 3f;
            // Ground pulse spreading from the car once a second (world-flat, whatever the body does).
            float t = Mathf.Repeat(Time.time, 1f);
            _ring.rotation = Quaternion.identity;
            _ring.localScale = Vector3.one * Mathf.Lerp(2f, 5.5f, t);
            _ringMat.color = Gold * (2.5f * (1f - t));
        }
    }
}
