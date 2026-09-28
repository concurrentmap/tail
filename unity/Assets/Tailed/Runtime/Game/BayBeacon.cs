using Tailed.Map;
using UnityEngine;

namespace Tailed.Game
{
    /// <summary>
    /// Local-only marker on a parking bay (the Mark's next stop, a burned Tail's body shop): a glowing
    /// ring and a soft light beam, pulsing. Never replicated, so nobody else can see it.
    /// </summary>
    public sealed class BayBeacon : MonoBehaviour
    {
        static BayBeacon _instance;
        Transform _ring, _beam;
        Material _mat;

        public static void Show(Vector3 position, Color colour)
        {
            if (_instance == null) _instance = Create();
            _instance.gameObject.SetActive(true);
            _instance.transform.position = position;
            _instance._colour = colour;
        }

        public static void Hide() { if (_instance != null) _instance.gameObject.SetActive(false); }

        Color _colour;

        static BayBeacon Create()
        {
            var go = new GameObject("BayBeacon");
            var b = go.AddComponent<BayBeacon>();
            b._mat = new Material(Shader.Find("Tailed/Additive"));
            var ring = new MeshBuilder();
            const int n = 40;
            for (int i = 0; i < n; i++)
            {
                float a0 = i * Mathf.PI * 2f / n, a1 = (i + 1) * Mathf.PI * 2f / n;
                Vector3 P(float a, float r) => new Vector3(Mathf.Cos(a) * r, 0.06f, Mathf.Sin(a) * r);
                ring.Quad(P(a0, 2.6f), P(a1, 2.6f), P(a1, 3.1f), P(a0, 3.1f), new Color(1, 1, 1, 1), Vector3.up);
            }
            b._ring = Child(go, "Ring", ring.Build("BeaconRing"), b._mat);
            var beam = new MeshBuilder();
            beam.SmoothCylinder(Vector3.zero, 1.2f, 0.3f, 14f, new Color(0.35f, 0.35f, 0.35f, 1f), 20, cap: false);
            b._beam = Child(go, "Beam", beam.Build("BeaconBeam"), b._mat);
            return b;
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

        void Update()
        {
            // HDR so it blooms and reads even on sunlit concrete.
            float pulse = 0.7f + 0.3f * Mathf.Sin(Time.time * 3f);
            _mat.color = _colour * (3.5f * pulse);
            _ring.localScale = Vector3.one * (1f + 0.05f * Mathf.Sin(Time.time * 3f));
        }
    }
}
