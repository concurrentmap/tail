using System.Collections.Generic;
using Tailed.Map;
using UnityEngine;

namespace Tailed.Game
{
    /// <summary>
    /// The Mark's own stop markers in the world (local only, never replicated): a tall light column
    /// over every stop on the route, red while it's still to visit (the next one brighter and
    /// pulsing), green once visited. Wayfinding without turn-by-turn directions.
    /// </summary>
    public sealed class RouteBeacons : MonoBehaviour
    {
        static RouteBeacons _instance;
        static Mesh _beam;
        readonly List<(Transform t, Material m, int poi)> _beacons = new List<(Transform, Material, int)>();
        int _next;
        static readonly Color ToVisit = new Color(1f, 0.25f, 0.2f), Visited = new Color(0.3f, 1f, 0.4f);

        public static void Sync(IReadOnlyList<int> route, int next)
        {
            if (_instance == null) _instance = new GameObject("RouteBeacons").AddComponent<RouteBeacons>();
            _instance.gameObject.SetActive(true);
            _instance.Build(route);
            _instance._next = next;
        }

        public static void Hide() { if (_instance != null) _instance.gameObject.SetActive(false); }

        void Build(IReadOnlyList<int> route)
        {
            bool same = _beacons.Count == route.Count;
            for (int i = 0; same && i < route.Count; i++) same = _beacons[i].poi == route[i];
            if (same) return;
            foreach (var b in _beacons) Destroy(b.t.gameObject);
            _beacons.Clear();
            var town = TownBuilder.Instance;
            if (_beam == null)
            {
                var mb = new MeshBuilder();
                mb.SmoothCylinder(Vector3.zero, 1.6f, 0.7f, 90f, new Color(0.35f, 0.35f, 0.35f, 1f), 16, cap: false);
                _beam = mb.Build("RouteBeam");
            }
            foreach (int poi in route)
            {
                var c = town.Lanes.Sites[poi].Centre;
                var go = new GameObject("StopBeacon");
                go.transform.SetParent(transform, false);
                go.transform.position = new Vector3(c.X, 0f, c.Y);
                go.AddComponent<MeshFilter>().sharedMesh = _beam;
                var mr = go.AddComponent<MeshRenderer>();
                var mat = new Material(Shader.Find("Tailed/Additive"));
                mr.sharedMaterial = mat;
                mr.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
                _beacons.Add((go.transform, mat, poi));
            }
        }

        void Update()
        {
            float pulse = 0.65f + 0.35f * Mathf.Sin(Time.time * 3f);
            for (int i = 0; i < _beacons.Count; i++)
            {
                bool visited = i < _next, next = i == _next;
                var col = visited ? Visited * 0.9f : next ? ToVisit * (2.2f * pulse) : ToVisit * 0.8f;
                _beacons[i].m.color = col;
                _beacons[i].t.localScale = next ? new Vector3(1.3f, 1f, 1.3f) : Vector3.one;
            }
        }
    }
}
