using System.Collections.Generic;
using Tailed.Core.Traffic;
using Tailed.Core.Util;
using Tailed.Map;
using Tailed.Vehicles;
using UnityEngine;

namespace Tailed.Traffic
{
    /// <summary>
    /// Runs the authoritative traffic simulation (host / single player) at a fixed 20 Hz and renders
    /// it with pooled <see cref="VehicleView"/>s, interpolating between steps.
    /// </summary>
    public sealed class TrafficRunner : MonoBehaviour
    {
        public const float StepDt = 0.05f;
        public int Population = 420;
        /// <summary>False on clients: they render replicated snapshots instead (Net.TrafficReplicator).</summary>
        public bool Authoritative = true;
        /// <summary>Client mode: host clock used to keep signal phases in sync.</summary>
        public System.Func<float> ExternalClock;

        public static TrafficRunner Instance { get; private set; }
        public TrafficSim Sim { get; private set; }
        public TownBuilder Town { get; private set; }
        public float Alpha { get; private set; }

        /// <summary>Positions that count as "watched" (every player's car), besides the local camera.</summary>
        public readonly List<Transform> Observers = new List<Transform>();
        /// <summary>Vehicle ids that have a local physics car and so get no view.</summary>
        public readonly HashSet<int> LocallyDriven = new HashSet<int>();

        readonly Dictionary<int, VehicleView> _views = new Dictionary<int, VehicleView>();
        readonly Stack<VehicleView> _pool = new Stack<VehicleView>();
        Vector2[] _prevPos = new Vector2[0], _prevDir = new Vector2[0];
        float _acc, _lampTimer;
        Transform _viewRoot;

        void Awake() => Instance = this;

        void Start()
        {
            _viewRoot = new GameObject("TrafficViews").transform;
            TownBuilder.Built += Attach;
            if (TownBuilder.Instance != null && TownBuilder.Instance.Lanes != null) Attach(TownBuilder.Instance);
        }

        void OnDestroy() => TownBuilder.Built -= Attach;

        void Attach(TownBuilder town)
        {
            foreach (var v in _views.Values) { v.Release(); _pool.Push(v); }
            _views.Clear();
            Town = town;
            if (!Authoritative)
            {
                // Clients: no NPCs (they arrive as replicas); the sim only provides signal timing and routing.
                Sim = new TrafficSim(town.Lanes, (ulong)town.Seed) { TargetPopulation = 0 };
                return;
            }
            Sim = new TrafficSim(town.Lanes, (ulong)town.Seed) { TargetPopulation = Population, IsObserved = Observed };
            Sim.Spawned += OnSpawned;
            Sim.Despawned += OnDespawned;
            Sim.Populate();
            SnapshotPrev();
        }

        /// <summary>Fresh traffic in the given mode (after leaving a session: no leftover player cars).</summary>
        public void Restart(bool authoritative)
        {
            Authoritative = authoritative;
            if (Town != null) Attach(Town);
        }

        /// <summary>Switch between host/offline (simulate) and client (replicate) modes.</summary>
        public void SetAuthoritative(bool authoritative)
        {
            if (Authoritative == authoritative && Sim != null) return;
            Authoritative = authoritative;
            if (Town != null) Attach(Town);
        }

        void OnSpawned(SimVehicle v)
        {
            if (LocallyDriven.Contains(v.Id)) return;
            var view = _pool.Count > 0 ? _pool.Pop() : VehicleView.Create(_viewRoot);
            view.gameObject.layer = Layers.Vehicles;
            view.Bind(v.Id, v.Identity);
            _views[v.Id] = view;
            EnsureCapacity(v.Id);
            _prevPos[v.Id] = new Vector2(v.Position.X, v.Position.Y);
            _prevDir[v.Id] = new Vector2(v.Heading.X, v.Heading.Y);
        }

        void OnDespawned(SimVehicle v)
        {
            if (!_views.TryGetValue(v.Id, out var view)) return;
            view.Release();
            _pool.Push(view);
            _views.Remove(v.Id);
        }

        /// <summary>Hide/show the view for a vehicle that is driven by local physics.</summary>
        public void SetLocallyDriven(int id, bool local)
        {
            if (local) { LocallyDriven.Add(id); OnDespawned(Sim.Vehicles[id]); }
            else LocallyDriven.Remove(id);
        }

        public bool TryGetView(int id, out VehicleView view) => _views.TryGetValue(id, out view);

        void EnsureCapacity(int id)
        {
            if (id < _prevPos.Length) return;
            int n = Mathf.Max(id + 1, _prevPos.Length * 2, 64);
            System.Array.Resize(ref _prevPos, n);
            System.Array.Resize(ref _prevDir, n);
        }

        void SnapshotPrev()
        {
            EnsureCapacity(Sim.Vehicles.Count);
            foreach (var v in Sim.Vehicles)
            {
                _prevPos[v.Id] = new Vector2(v.Position.X, v.Position.Y);
                _prevDir[v.Id] = new Vector2(v.Heading.X, v.Heading.Y);
            }
        }

        bool Observed(Vec2 p)
        {
            var wp = new Vector3(p.X, 0f, p.Y);
            foreach (var o in Observers)
                if (o != null && (o.position - wp).sqrMagnitude < 230f * 230f) return true;
            var cam = Camera.main;
            if (cam == null) return false;
            var d = wp - cam.transform.position;
            if (d.sqrMagnitude < 60f * 60f) return true;
            if (d.sqrMagnitude > 500f * 500f) return false;
            var vp = cam.WorldToViewportPoint(wp);
            return vp.z > 0f && vp.x > -0.15f && vp.x < 1.15f && vp.y > -0.3f && vp.y < 1.3f;
        }

        void Update()
        {
            if (Sim == null) return;
            if (!Authoritative)
            {
                if (ExternalClock != null) Sim.SyncTime(ExternalClock());
                RefreshLamps(Time.deltaTime);
                return;
            }
            _acc += Time.deltaTime;
            int steps = 0;
            while (_acc >= StepDt && steps < 4)
            {
                SnapshotPrev();
                Sim.Step(StepDt);
                _acc -= StepDt;
                steps++;
            }
            if (steps == 4) _acc = 0f; // don't spiral after a hitch
            Alpha = _acc / StepDt;

            float dt = Time.deltaTime;
            foreach (var kv in _views)
            {
                var v = Sim.Vehicles[kv.Key];
                if (!v.Alive) continue;
                var pos = Vector2.Lerp(_prevPos[v.Id], new Vector2(v.Position.X, v.Position.Y), Alpha);
                var dir = Vector2.Lerp(_prevDir[v.Id], new Vector2(v.Heading.X, v.Heading.Y), Alpha);
                if (dir.sqrMagnitude < 1e-6f) dir = new Vector2(v.Heading.X, v.Heading.Y);
                if (v.Mode == VehicleMode.External)
                {
                    // Remote players: updates arrive asynchronously; ease towards the latest pose.
                    var view = kv.Value.transform;
                    var target = new Vector3(v.Position.X, 0f, v.Position.Y);
                    float k = 1f - Mathf.Exp(-dt * 14f);
                    var p = Vector3.Distance(view.position, target) > 20f ? target : Vector3.Lerp(view.position, target, k);
                    var f = Vector3.Slerp(view.forward, new Vector3(v.Heading.X, 0f, v.Heading.Y), k);
                    kv.Value.SetPose(p, f, v.Speed, v.Flags, dt, steerFromMotion: false);
                    continue;
                }
                kv.Value.SetPose(new Vector3(pos.x, 0f, pos.y), new Vector3(dir.x, 0f, dir.y), v.Speed, v.Flags, dt);
            }
            RefreshLamps(dt);
        }

        void RefreshLamps(float dt)
        {
            _lampTimer -= dt;
            if (_lampTimer <= 0f && Town != null && Town.Lamps != null)
            {
                _lampTimer = 0.1f;
                Town.Lamps.Refresh(Sim.Signal);
            }
        }

        /// <summary>A local physics car hit this simulated vehicle.</summary>
        public void ReportImpact(int vehicleId, float impulse)
        {
            if (Sim != null && impulse > 1500f) Sim.Disrupt(vehicleId, Mathf.Clamp(impulse / 1500f, 3f, 12f));
        }
    }
}
