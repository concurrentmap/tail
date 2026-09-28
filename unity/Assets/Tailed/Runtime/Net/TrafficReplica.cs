using System.Collections.Generic;
using Tailed.Core.Identity;
using Tailed.Core.Net;
using Tailed.Core.Traffic;
using Tailed.Core.Util;
using Tailed.Map;
using Tailed.Traffic;
using Tailed.Vehicles;
using UnityEngine;

namespace Tailed.Net
{
    /// <summary>
    /// Client-side traffic: vehicles in this player's interest set, rendered ~120 ms behind the host
    /// by interpolating decoded snapshots. Poses come from lane-graph topology (seeded town), so the
    /// wire carries ~10 bytes per vehicle. Nothing here says which vehicles are players.
    /// </summary>
    public sealed class TrafficReplica : MonoBehaviour
    {
        public static TrafficReplica Instance { get; private set; }
        const float Delay = 0.12f;

        sealed class Entry
        {
            public VehicleIdentity Identity;
            public float Length;
            public VehicleView View;
            public readonly List<(float t, VehicleState s)> Buffer = new List<(float, VehicleState)>();
        }

        readonly Dictionary<int, Entry> _entries = new Dictionary<int, Entry>();
        readonly Stack<VehicleView> _pool = new Stack<VehicleView>();
        Transform _root;
        float _latest = -1f, _latestLocal;

        void Awake()
        {
            Instance = this;
            _root = new GameObject("ReplicaViews").transform;
        }

        public int Count => _entries.Count;

        public bool TryGetView(int id, out VehicleView view)
        {
            view = _entries.TryGetValue(id, out var e) ? e.View : null;
            return view != null;
        }

        public void Handle(Msg type, ByteReader r)
        {
            switch (type)
            {
                case Msg.TrafficSpawn:
                {
                    int n = r.U16();
                    for (int i = 0; i < n; i++)
                    {
                        int id = r.U16();
                        var identity = TrafficCodec.ReadIdentity(r);
                        if (!_entries.TryGetValue(id, out var e)) _entries[id] = e = new Entry();
                        e.Identity = identity;
                        e.Length = VehicleCatalog.Models[identity.ModelId].Length;
                        if (e.View == null) e.View = _pool.Count > 0 ? _pool.Pop() : VehicleView.Create(_root);
                        e.View.gameObject.layer = Layers.Vehicles;
                        e.View.Bind(id, identity);
                        e.Buffer.Clear();
                        e.View.gameObject.SetActive(false); // until the first snapshot places it
                    }
                    break;
                }
                case Msg.TrafficDespawn:
                {
                    int n = r.U16();
                    for (int i = 0; i < n; i++) Remove(r.U16());
                    break;
                }
                case Msg.TrafficSnapshot:
                {
                    float t = r.F32();
                    if (t > _latest) { _latest = t; _latestLocal = Time.time; }
                    int n = r.U16();
                    for (int i = 0; i < n; i++)
                    {
                        var s = TrafficCodec.Read(r);
                        if (!_entries.TryGetValue(s.Id, out var e)) continue;
                        if (e.Buffer.Count > 0 && e.Buffer[e.Buffer.Count - 1].t >= t) continue;
                        e.Buffer.Add((t, s));
                        if (e.Buffer.Count > 6) e.Buffer.RemoveAt(0);
                    }
                    break;
                }
            }
        }

        void Remove(int id)
        {
            if (!_entries.TryGetValue(id, out var e)) return;
            if (e.View != null) { e.View.Release(); _pool.Push(e.View); }
            _entries.Remove(id);
        }

        public void Clear()
        {
            foreach (var id in new List<int>(_entries.Keys)) Remove(id);
        }

        void OnDestroy()
        {
            Clear();
            if (_root != null) Destroy(_root.gameObject);
        }

        void LateUpdate()
        {
            var runner = TrafficRunner.Instance;
            if (runner == null || runner.Town == null || _latest < 0f) return;
            var g = runner.Town.Lanes;
            float renderTime = _latest + (Time.time - _latestLocal) - Delay;
            float dt = Time.deltaTime;
            foreach (var kv in _entries)
            {
                var e = kv.Value;
                if (e.Buffer.Count == 0 || e.View == null) continue;
                int i = e.Buffer.Count - 1;
                while (i > 0 && e.Buffer[i - 1].t > renderTime) i--;
                var b = e.Buffer[i];
                var a = i > 0 ? e.Buffer[i - 1] : b;
                float alpha = b.t > a.t ? Mathf.Clamp01((renderTime - a.t) / (b.t - a.t)) : 1f;
                if (renderTime > b.t) alpha = 1f;
                Pose(g, a.s, e.Length, out var pa, out var ha);
                Pose(g, b.s, e.Length, out var pb, out var hb);
                // Don't smear across teleports (respawns, chop shop swaps).
                if (Vec2.Distance(pa, pb) > 25f) { pa = pb; ha = hb; }
                var pos = Vec2.Lerp(pa, pb, alpha);
                var hd = Vec2.Lerp(ha, hb, alpha);
                if (hd.LengthSq < 1e-6f) hd = hb;
                if (!e.View.gameObject.activeSelf) e.View.gameObject.SetActive(true);
                float speed = Mathf.Lerp(a.s.Speed, b.s.Speed, alpha);
                e.View.SetPose(new Vector3(pos.X, 0f, pos.Y), new Vector3(hd.X, 0f, hd.Y), speed, b.s.Flags, dt, b.s.Mode != VehicleMode.External);
            }
        }

        static void Pose(Core.Roads.LaneGraph g, in VehicleState s, float length, out Vec2 pos, out Vec2 heading)
        {
            if (s.Mode == VehicleMode.External) { pos = s.Position; heading = s.Heading; return; }
            int lane = s.Mode == VehicleMode.Lane ? s.Ref : -1;
            int con = s.Mode == VehicleMode.Connector ? s.Ref : -1;
            int bay = s.Mode >= VehicleMode.ParkIn && s.Mode <= VehicleMode.ParkOut ? s.Ref : -1;
            VehiclePose.Compute(g, s.Mode, lane, con, bay, s.S, length, s.Lateral, out pos, out heading);
        }
    }
}
