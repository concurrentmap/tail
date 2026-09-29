using System;
using System.Collections.Generic;
using Tailed.Core.Identity;
using Tailed.Core.Roads;
using Tailed.Core.Util;

namespace Tailed.Core.Traffic
{
    public enum VehicleMode : byte { Lane, Connector, ParkIn, Parked, ParkOut, External }

    [Flags]
    public enum VehicleFlags : byte
    {
        None = 0, Brake = 1, IndicateLeft = 2, IndicateRight = 4, Hazard = 8, Horn = 16, Reverse = 32,
        /// <summary>Player cars only: windows down (voice carries further, GD §7).</summary>
        WindowsOpen = 64,
        /// <summary>The Mark, briefly, at a real stop: a gold marker over the car that anyone looking sees.</summary>
        Errand = 128,
    }

    public enum SignalState : byte { Green, Amber, Red }

    public sealed class SimVehicle
    {
        public int Id;
        public bool Alive;
        public VehicleIdentity Identity;
        public VehicleModel Model;
        public DriverProfile Profile;
        public VehicleMode Mode;
        public int Lane = -1, Connector = -1, Bay = -1;
        /// <summary>Front-bumper arc length along the current lane / connector / parking path.</summary>
        public float S, Speed, Accel;
        public float Length;
        /// <summary>Visual lateral offset during a lane change (m, positive = left). Decays to 0.</summary>
        public float Lateral;
        public VehicleFlags Flags;

        // Routing
        public readonly List<int> Route = new List<int>();
        public int RouteIndex;
        public int NextConnector = -1;
        public bool Committed;
        public int DestLane = -1, DestBay = -1;
        public float DestS;

        // Behaviour state
        public bool StoppedAtLine;
        public float StopTime, ParkTimer, StuckTimer, DisruptedTimer, HornTimer, LaneChangeCooldown, CommitTimer;
        /// <summary>Lane we intend to move into (signalling before the move), or -1.</summary>
        public int PendingLane = -1;
        public float SignalTimer;

        // Human imperfection (host only; the result reaches clients through the lateral offset and S).
        /// <summary>Drift within the lane (m, positive = left), wandering with distance travelled.</summary>
        public float Wander;
        public float Odometer, WanderPhase, WobblePhase;
        /// <summary>Stopped and waiting; moving off takes a moment once the way clears.</summary>
        public bool Waiting;
        public float ReactionTimer;
        /// <summary>Kerb stop we mean to pull in at on this lane (-1 none), and time left stopped there.</summary>
        public int KerbStop = -1;
        public float KerbTimer;
        /// <summary>Next junction: turn round instead of following the route (then re-plan).</summary>
        public bool WantUTurn;

        /// <summary>Total lateral offset from the lane centre as drawn (lane change + wander).</summary>
        public float VisualLateral => Lateral + Wander;

        /// <summary>World pose of the vehicle centre, refreshed every step.</summary>
        public Vec2 Position, Heading = new Vec2(1, 0);

        public bool IsExternal => Mode == VehicleMode.External;
    }

    /// <summary>
    /// Host-side traffic simulation (architecture §5). Deterministic for a given seed and inputs.
    /// NPCs follow lanes with IDM, obey junction control, change lanes for their route, park at
    /// POIs and start new trips. Player cars are "external" vehicles projected onto lanes each step.
    /// </summary>
    public sealed class TrafficSim
    {
        public const float LookAhead = 70f;
        const float TurnSpeedRight = 5.5f, TurnSpeedLeft = 7f, TurnSpeedUTurn = 3.2f, ParkSpeed = 5f;
        /// <summary>How far a stopped bus or van sits towards the kerb (m).</summary>
        public const float KerbShift = 0.7f;

        static float TurnSpeed(TurnType t) =>
            t == TurnType.Right ? TurnSpeedRight : t == TurnType.UTurn ? TurnSpeedUTurn : TurnSpeedLeft;

        public readonly LaneGraph Graph;
        public readonly RoadNetwork Net;
        public readonly LaneIndex Index;
        public readonly Router Router;
        public readonly IdentityService Identities;
        public readonly List<SimVehicle> Vehicles = new List<SimVehicle>();

        public float Time { get; private set; }
        public int TargetPopulation = 420;
        /// <summary>True if a point is visible to some player; nothing spawns or despawns there.</summary>
        public Func<Vec2, bool> IsObserved = _ => false;
        public event Action<SimVehicle> Spawned, Despawned;

        Rng _rng;
        readonly List<int>[] _laneOcc, _connOcc, _connCommitted;
        readonly float[] _signalOffset;
        readonly int[] _bayOwner;
        readonly Stack<int> _free = new Stack<int>();
        readonly float[] _laneWeights;
        float _directorTimer;
        int _npcCount, _busCount;
        /// <summary>Buses the director keeps on the road.</summary>
        public int BusTarget = 6;

        public TrafficSim(LaneGraph graph, ulong seed)
        {
            Graph = graph;
            Net = graph.Network;
            Index = new LaneIndex(graph);
            Router = new Router(graph);
            Identities = new IdentityService(seed);
            _rng = new Rng(seed * 2654435761UL + 1);
            _laneOcc = NewLists(graph.Lanes.Count);
            _connOcc = NewLists(graph.Connectors.Count);
            _connCommitted = NewLists(graph.Connectors.Count);
            _signalOffset = new float[Net.Nodes.Count];
            for (int i = 0; i < _signalOffset.Length; i++) _signalOffset[i] = _rng.Range(0f, 60f);
            _bayOwner = new int[graph.Bays.Count];
            for (int i = 0; i < _bayOwner.Length; i++) _bayOwner[i] = -1;
            _laneWeights = new float[graph.Lanes.Count];
            for (int i = 0; i < _laneWeights.Length; i++) _laneWeights[i] = graph.Lanes[i].Length;
        }

        static List<int>[] NewLists(int n)
        {
            var a = new List<int>[n];
            for (int i = 0; i < n; i++) a[i] = new List<int>();
            return a;
        }

        public int NpcCount => _npcCount;

        /// <summary>Clients keep a zero-population sim only for signal timing: follow the host clock.</summary>
        public void SyncTime(float hostTime) => Time = hostTime;

        // ---- public API ----------------------------------------------------------------

        /// <summary>Fill the town to the target population immediately (before anyone is watching).</summary>
        public void Populate()
        {
            for (int tries = 0; _npcCount < TargetPopulation && tries < TargetPopulation * 20; tries++) TrySpawn();
            RebuildOccupancy();
            UpdatePoses();
        }

        /// <summary>Register a physics-driven vehicle (a player). Its pose is pushed each frame.</summary>
        public SimVehicle AddExternal(VehicleIdentity identity)
        {
            var v = Allocate();
            v.Identity = identity;
            v.Model = VehicleCatalog.Models[identity.ModelId];
            v.Length = v.Model.Length;
            v.Mode = VehicleMode.External;
            Spawned?.Invoke(v);
            return v;
        }

        public void UpdateExternal(int id, Vec2 position, Vec2 heading, float speed, VehicleFlags flags)
        {
            var v = Vehicles[id];
            v.Position = position;
            v.Heading = heading.LengthSq > 1e-6f ? heading.Normalized : v.Heading;
            v.Speed = speed;
            v.Flags = flags;
        }

        public void Remove(int id)
        {
            var v = Vehicles[id];
            if (!v.Alive) return;
            if (v.Bay >= 0 && _bayOwner[v.Bay] == v.Id) _bayOwner[v.Bay] = -1;
            if (v.DestBay >= 0 && _bayOwner[v.DestBay] == v.Id) _bayOwner[v.DestBay] = -1;
            if (!v.IsExternal) { _npcCount--; Identities.Release(v.Identity); if (v.Model.Style == BodyStyle.Bus) _busCount--; }
            else ReleaseExternalBay(v.Id);
            v.Alive = false;
            _free.Push(id);
            Despawned?.Invoke(v);
        }

        /// <summary>A player hit this NPC: it stops, puts its hazards on and leans on the horn.</summary>
        public void Disrupt(int id, float seconds)
        {
            if (id < 0 || id >= Vehicles.Count) return; // not ours (e.g. a replica-only or dev vehicle)
            var v = Vehicles[id];
            if (v.Alive && !v.IsExternal) v.DisruptedTimer = Math.Max(v.DisruptedTimer, seconds);
        }

        /// <summary>Send an NPC to park at a POI (Traffic Director decoys, GD §9).</summary>
        public bool SendToPoi(int id, int poiId)
        {
            var v = Vehicles[id];
            if (!v.Alive || v.IsExternal || v.Mode != VehicleMode.Lane) return false;
            foreach (int b in Graph.Sites[poiId].Bays)
                if (_bayOwner[b] < 0)
                {
                    ReleaseDestination(v);
                    return PlanToBay(v, b);
                }
            return false;
        }

        public bool BayOccupied(int bay) => _bayOwner[bay] >= 0;

        public SignalState Signal(int nodeId, int edgeId)
        {
            var j = Graph.Junctions[nodeId];
            if (j.Control != JunctionControl.Signal) return SignalState.Green;
            float cycle = 0f;
            foreach (var p in j.Phases) cycle += p.Duration;
            float t = (Time + _signalOffset[nodeId]) % cycle;
            foreach (var p in j.Phases)
            {
                if (t < p.Duration)
                {
                    if (Array.IndexOf(p.GreenEdges, edgeId) < 0) return SignalState.Red;
                    return t < p.Green ? SignalState.Green : t < p.Green + p.Amber ? SignalState.Amber : SignalState.Red;
                }
                t -= p.Duration;
            }
            return SignalState.Red;
        }

        // ---- step ----------------------------------------------------------------------

        public void Step(float dt)
        {
            Time += dt;
            RebuildOccupancy();

            _directorTimer -= dt;
            if (_directorTimer <= 0f)
            {
                _directorTimer = 0.5f;
                for (int k = 0; k < 8 && _npcCount < TargetPopulation; k++) TrySpawn();
            }

            foreach (var v in Vehicles)
                if (v.Alive && !v.IsExternal) Think(v, dt);
            for (int i = 0; i < Vehicles.Count; i++)
            {
                var v = Vehicles[i];
                if (v.Alive && !v.IsExternal) Integrate(v, dt);
            }
            // Signals describe the state after this step's moves (a finished turn stops blinking now).
            foreach (var v in Vehicles)
                if (v.Alive && !v.IsExternal)
                {
                    v.Flags &= ~(VehicleFlags.IndicateLeft | VehicleFlags.IndicateRight);
                    ApplyIndicators(v);
                }
            UpdatePoses();
        }

        void RebuildOccupancy()
        {
            foreach (var l in _laneOcc) l.Clear();
            foreach (var l in _connOcc) l.Clear();
            foreach (var l in _connCommitted) l.Clear();
            foreach (var v in Vehicles)
            {
                if (!v.Alive) continue;
                switch (v.Mode)
                {
                    case VehicleMode.Lane:
                        _laneOcc[v.Lane].Add(v.Id);
                        if (v.Committed && v.NextConnector >= 0) _connCommitted[v.NextConnector].Add(v.Id);
                        break;
                    case VehicleMode.Connector:
                        _connOcc[v.Connector].Add(v.Id);
                        break;
                    case VehicleMode.External:
                        ProjectExternal(v);
                        break;
                }
            }
            Comparison<int> byS = (a, b) => Vehicles[a].S.CompareTo(Vehicles[b].S);
            foreach (var l in _laneOcc) if (l.Count > 1) l.Sort(byS);
            foreach (var l in _connOcc) if (l.Count > 1) l.Sort(byS);
        }

        /// <summary>Bay a player's car is parked in (vehicle id → bay), so NPCs don't drive into it.</summary>
        readonly Dictionary<int, int> _externalBay = new Dictionary<int, int>();

        void ProjectExternal(SimVehicle v)
        {
            v.Lane = v.Connector = -1;
            if (!Index.Project(v.Position, v.Heading, out var p)) { ClaimBayUnder(v); return; }
            ReleaseExternalBay(v.Id);
            v.S = p.S + v.Length * 0.5f; // front bumper
            if (p.OnConnector) { v.Connector = p.Id; _connOcc[p.Id].Add(v.Id); }
            else { v.Lane = p.Id; _laneOcc[p.Id].Add(v.Id); }
        }

        /// <summary>
        /// A player's car sitting in a parking bay takes it: the sim won't send NPCs there, and an NPC
        /// already heading for it picks somewhere else (ThinkLane).
        /// </summary>
        void ClaimBayUnder(SimVehicle v)
        {
            int found = -1;
            for (int b = 0; b < Graph.Bays.Count; b++)
            {
                var bay = Graph.Bays[b];
                if (Vec2.Distance(bay.Position - bay.Heading * 2.2f, v.Position) < 3.5f) { found = b; break; }
            }
            if (found < 0) { ReleaseExternalBay(v.Id); return; }
            if (_externalBay.TryGetValue(v.Id, out int had) && had != found) ReleaseExternalBay(v.Id);
            int owner = _bayOwner[found];
            // A parked NPC keeps its bay (the player squeezed in beside it); an NPC merely heading there loses it.
            if (owner >= 0 && owner != v.Id && Vehicles[owner].Alive && Vehicles[owner].Bay == found) return;
            _bayOwner[found] = v.Id;
            _externalBay[v.Id] = found;
        }

        void ReleaseExternalBay(int id)
        {
            if (!_externalBay.TryGetValue(id, out int b)) return;
            if (_bayOwner[b] == id) _bayOwner[b] = -1;
            _externalBay.Remove(id);
        }

        // ---- behaviour -----------------------------------------------------------------

        void Think(SimVehicle v, float dt)
        {
            // Signals are recomputed from intent every tick (never sticky).
            v.Flags &= ~(VehicleFlags.Brake | VehicleFlags.Horn | VehicleFlags.Hazard | VehicleFlags.IndicateLeft | VehicleFlags.IndicateRight);
            v.LaneChangeCooldown -= dt;
            v.SignalTimer -= dt;

            if (v.DisruptedTimer > 0f)
            {
                v.DisruptedTimer -= dt;
                v.Accel = v.Speed > 0f ? -6f : 0f;
                v.Flags |= VehicleFlags.Hazard;
                if ((int)(v.DisruptedTimer * 2f) % 3 == 0) v.Flags |= VehicleFlags.Horn;
                return;
            }

            float prevAccel = v.Accel;
            switch (v.Mode)
            {
                case VehicleMode.Lane: ThinkLane(v, dt); break;
                case VehicleMode.Connector: ThinkConnector(v); break;
                case VehicleMode.ParkIn: ThinkParkIn(v); break;
                case VehicleMode.Parked: ThinkParked(v, dt); break;
                case VehicleMode.ParkOut: ThinkParkOut(v); break;
            }
            Humanise(v, prevAccel, dt);
            if (v.Accel < -0.5f || (v.Speed < 0.1f && v.Mode != VehicleMode.Parked)) v.Flags |= VehicleFlags.Brake;
            if (v.HornTimer > 0f) { v.HornTimer -= dt; v.Flags |= VehicleFlags.Horn; }
        }

        /// <summary>
        /// People aren't controllers: they pause before moving off when the way clears, and they
        /// feed the throttle in rather than stamping on it. Only ever lowers acceleration, so it
        /// can't make anyone less safe than plain IDM.
        /// </summary>
        void Humanise(SimVehicle v, float prevAccel, float dt)
        {
            if (v.Mode == VehicleMode.Parked) { v.Waiting = false; v.ReactionTimer = 0f; return; }
            if (v.Speed < 0.2f)
            {
                if (v.Accel <= 0.2f) { v.Waiting = true; v.ReactionTimer = 0f; }
                else if (v.Waiting)
                {
                    v.Waiting = false;
                    v.ReactionTimer = _rng.Range(v.Profile.ReactionMin, v.Profile.ReactionMax);
                }
            }
            else v.Waiting = false;
            if (v.ReactionTimer > 0f) { v.ReactionTimer -= dt; v.Accel = Math.Min(v.Accel, 0f); return; }
            // Throttle builds at a human rate (brakes still bite immediately).
            if (v.Accel > 0f) v.Accel = Math.Min(v.Accel, Math.Max(prevAccel, 0f) + v.Profile.Jerk * dt);
        }

        IdmParams LaneIdm(SimVehicle v, float limit)
        {
            var p = v.Profile.Idm(limit, v.Model.AccelFactor, v.Model.TopSpeedFactor);
            // Nobody holds an exact speed: the target drifts slowly up and down.
            float w = v.Profile.SpeedWobble, t = Time * (2f * MathF.PI / v.Profile.WobblePeriod) + v.WobblePhase;
            p.DesiredSpeed *= 1f + w * (0.7f * MathF.Sin(t) + 0.3f * MathF.Sin(t * 2.7f + 1.3f));
            return p;
        }

        IdmParams ParkIdm(SimVehicle v)
        {
            var p = v.Profile.Idm(ParkSpeed, 1f, 1f);
            p.DesiredSpeed = ParkSpeed;
            return p;
        }

        void ThinkLane(SimVehicle v, float dt)
        {
            // Our bay got taken (a player parked in it): go somewhere else.
            if (v.DestBay >= 0 && _bayOwner[v.DestBay] != v.Id) { v.DestBay = -1; NewTrip(v); }
            var lane = Graph.Lanes[v.Lane];
            var p = LaneIdm(v, lane.SpeedLimit);
            float remaining = lane.Length - v.S;

            // Overshot our bay's entry (wrong lane, or blocked): go round the block.
            if (v.DestBay >= 0)
            {
                var bay = Graph.Bays[v.DestBay];
                var access = Graph.Lanes[bay.AccessLane];
                if (access.EdgeId == lane.EdgeId && access.ToNode == lane.ToNode && v.S > bay.EntryS + 6f && v.NextConnector < 0)
                    Replan(v);
            }

            // Destinations on this lane.
            if (v.DestBay >= 0 && Graph.Bays[v.DestBay].AccessLane == v.Lane && v.S <= Graph.Bays[v.DestBay].EntryS)
            {
                float toEntry = Graph.Bays[v.DestBay].EntryS - v.S;
                v.Accel = Math.Min(Leader(v, p, out _), SlowTo(v, ParkSpeed, toEntry));
                return;
            }

            // Pulled over at a kerb stop (bus stop, double-parked van): sit there, then pull out.
            if (v.KerbTimer > 0f)
            {
                v.KerbTimer -= dt;
                v.Accel = v.Speed > 0f ? -3f : 0f;
                if (IsDoubleParker(v)) v.Flags |= VehicleFlags.Hazard;
                if (v.KerbTimer <= 0f) v.KerbStop = -1;
                return;
            }

            if (v.LaneChangeCooldown <= 0f && MathF.Abs(v.Lateral) < 0.3f) TryLaneChange(v, lane, remaining);
            lane = Graph.Lanes[v.Lane];

            // Missed the lane for our turn: take whatever this lane allows and re-route after.
            if (v.NextConnector < 0 && remaining < 10f && lane.Outgoing.Count > 0 && !OnFinalEdge(v))
            {
                v.NextConnector = Graph.RandomOutgoing(v.Lane, ref _rng);
                v.Route.Clear();
            }

            float accel = Leader(v, p, out var leaderId);

            if (v.KerbStop >= 0)
            {
                var k = Graph.KerbStops[v.KerbStop];
                if (k.Lane != v.Lane) v.KerbStop = -1;
                else
                {
                    // Virtual stationary obstacle so IDM's jam gap ends at the stop.
                    float toStop = KerbStopFront(v, k) - v.S;
                    accel = Math.Min(accel, Idm.Accel(p, v.Speed, toStop + p.MinGap, 0f));
                    if (toStop < 0.8f && v.Speed < 0.4f)
                    {
                        v.KerbTimer = v.Model.Style == BodyStyle.Bus ? _rng.Range(7f, 14f) : _rng.Range(20f, 45f);
                        if (IsDoubleParker(v)) v.Flags |= VehicleFlags.Hazard;
                    }
                }
            }

            // Junction approach.
            if (v.NextConnector >= 0 && remaining < LookAhead)
            {
                var con = Graph.Connectors[v.NextConnector];
                if (lane.Control == ApproachControl.Stop && remaining < v.Profile.StopShort + p.MinGap + 1.5f && v.Speed < 0.5f && !v.StoppedAtLine)
                {
                    v.StoppedAtLine = true;
                    v.StopTime = Time;
                }
                if (!v.Committed)
                {
                    bool permitted = CanEnter(v, con, remaining);
                    float stopDist = v.Speed * v.Speed / (2f * v.Profile.ComfortDecel);
                    if (permitted && remaining < Math.Max(stopDist + 4f, 8f)) Commit(v);
                    else if (!permitted)
                        accel = Math.Min(accel, Idm.Accel(p, v.Speed, remaining - v.Profile.StopShort, 0f));
                }
                else if ((v.CommitTimer += dt) > 12f && v.Speed < 0.1f)
                {
                    v.Committed = false; // waited too long to enter; let others through
                    v.CommitTimer = 0f;
                }
                if (con.Turn != TurnType.Straight)
                {
                    // Each driver has their own turn speed and brakes for it early or late.
                    float lead = Math.Min(v.Profile.TurnBrakeLead, remaining * 0.5f);
                    accel = Math.Min(accel, SlowTo(v, TurnSpeed(con.Turn) * v.Profile.TurnSpeedFactor, remaining - lead));
                }
            }
            else if (v.NextConnector < 0)
            {
                accel = Math.Min(accel, Idm.Accel(p, v.Speed, remaining - 0.5f, 0f));
            }
            v.Accel = accel;
            Honk(v, leaderId, dt);
        }

        void ThinkConnector(SimVehicle v)
        {
            var con = Graph.Connectors[v.NextConnectorOrCurrent()];
            float limit = con.Turn == TurnType.Straight ? Graph.Lanes[con.ToLane].SpeedLimit : TurnSpeed(con.Turn) * v.Profile.TurnSpeedFactor;
            v.Accel = Leader(v, LaneIdm(v, limit), out _);
        }

        void ThinkParkIn(SimVehicle v)
        {
            var bay = Graph.Bays[v.Bay];
            // Virtual stationary leader placed so IDM's jam gap ends exactly at the bay.
            var p = ParkIdm(v);
            v.Accel = Idm.Accel(p, v.Speed, bay.EntryLength - v.S + p.MinGap, 0f);
        }

        void ThinkParked(SimVehicle v, float dt)
        {
            v.Accel = 0f;
            v.Speed = 0f;
            v.ParkTimer -= dt;
            if (v.ParkTimer > 0f) return;
            if (!IsObserved(v.Position) && _rng.NextDouble() < 0.5) { Remove(v.Id); return; }
            var bay = Graph.Bays[v.Bay];
            if (!LaneClear(bay.AccessLane, bay.ExitS - 60f, bay.ExitS + 8f)) return;
            v.Mode = VehicleMode.ParkOut;
            v.S = 0f;
        }

        /// <summary>Leaving a bay: drive the exit path, but yield at the merge point until the lane is clear.</summary>
        void ThinkParkOut(SimVehicle v)
        {
            var bay = Graph.Bays[v.Bay];
            var p = ParkIdm(v);
            float remaining = bay.ExitLength - v.S;
            v.Accel = Idm.FreeRoadAccel(p, v.Speed);
            if (remaining < 10f && !MergeClear(v, bay))
                v.Accel = Math.Min(v.Accel, Idm.Accel(p, v.Speed, remaining + p.MinGap - 0.5f, 0f));
        }

        /// <summary>
        /// Turn signals from what the driver is actually doing, recomputed every tick:
        /// pulling out (left), pulling in (right), a lane change about to happen or in progress,
        /// or a turn at a real junction from 50 m out until the turn is complete. Road bends
        /// get no signal. Some personalities occasionally forget, per manoeuvre.
        /// </summary>
        void ApplyIndicators(SimVehicle v)
        {
            if (v.DisruptedTimer > 0f) return; // hazards instead
            int side = 0, manoeuvre = 0;
            switch (v.Mode)
            {
                case VehicleMode.ParkOut: side = -1; manoeuvre = 100000 + v.Bay; break;
                case VehicleMode.ParkIn: side = 1; manoeuvre = 200000 + v.Bay; break;
                case VehicleMode.Connector:
                {
                    var con = Graph.Connectors[v.Connector];
                    if (con.Turn != TurnType.Straight && Net.Nodes[con.NodeId].Degree >= 3)
                    { side = con.Turn == TurnType.Right ? 1 : -1; manoeuvre = con.NodeId; }
                    break;
                }
                case VehicleMode.Lane:
                {
                    var lane = Graph.Lanes[v.Lane];
                    if (v.KerbTimer > 0f)
                    {
                        // Hazards while double-parked; a bus signals out as it's about to leave.
                        if (!IsDoubleParker(v) && v.KerbTimer < 2.5f) { side = -1; manoeuvre = 400000 + v.KerbStop; }
                    }
                    else if (v.KerbStop >= 0 && KerbStopFront(v, Graph.KerbStops[v.KerbStop]) - v.S < 35f)
                    { side = 1; manoeuvre = 400000 + v.KerbStop; }
                    else if (MathF.Abs(v.Lateral) > 0.3f) { side = v.Lateral > 0f ? 1 : -1; manoeuvre = 300000 + lane.EdgeId; }
                    else if (v.PendingLane >= 0) { side = Graph.Lanes[v.PendingLane].Index > lane.Index ? -1 : 1; manoeuvre = 300000 + lane.EdgeId; }
                    else if (v.DestBay >= 0 && Graph.Bays[v.DestBay].AccessLane == v.Lane && Graph.Bays[v.DestBay].EntryS - v.S < 30f)
                    { side = 1; manoeuvre = 200000 + v.DestBay; }
                    else if (v.NextConnector >= 0 && lane.Length - v.S < 50f)
                    {
                        var con = Graph.Connectors[v.NextConnector];
                        if (con.Turn != TurnType.Straight && Net.Nodes[con.NodeId].Degree >= 3)
                        { side = con.Turn == TurnType.Right ? 1 : -1; manoeuvre = con.NodeId; }
                    }
                    break;
                }
            }
            if (side == 0 || !Signals(v, manoeuvre)) return;
            v.Flags |= side < 0 ? VehicleFlags.IndicateLeft : VehicleFlags.IndicateRight;
        }

        /// <summary>Does this driver signal this particular manoeuvre? Stable for the whole manoeuvre.</summary>
        public static bool Signals(SimVehicle v, int manoeuvre)
        {
            if (v.Profile == null || v.Profile.IndicatorChance >= 1f) return true;
            uint h = (uint)(v.Id * 73856093) ^ (uint)(manoeuvre * 19349663);
            h ^= h >> 13; h *= 0x5bd1e995; h ^= h >> 15;
            return (h % 1000u) < v.Profile.IndicatorChance * 1000f;
        }

        /// <summary>Front-bumper S where a vehicle pulls up at a kerb stop (bus front just past the shelter).</summary>
        static float KerbStopFront(SimVehicle v, KerbStop k) => k.S + (k.BusStop ? 6f : v.Length * 0.5f);

        /// <summary>Delivery vans and lorries stop with hazards on; buses at their stop don't.</summary>
        static bool IsDoubleParker(SimVehicle v) => v.Model.Style != BodyStyle.Bus;

        bool MergeClear(SimVehicle v, ParkingBay bay)
        {
            float at = bay.ExitS;
            foreach (int id in _laneOcc[bay.AccessLane])
            {
                var o = Vehicles[id];
                if (o.Id == v.Id) continue;
                if (o.S >= at) { if (o.S - o.Length - at < 3f) return false; }
                else if (at - v.Length - o.S < o.Speed * 1.6f + 4f) return false;
            }
            foreach (int c in Graph.Lanes[bay.AccessLane].Incoming)
                if (_connOcc[c].Count > 0 && at < 25f) return false;
            return true;
        }

        bool LaneClear(int laneId, float from, float to)
        {
            foreach (int id in _laneOcc[laneId])
            {
                var o = Vehicles[id];
                if (o.S > from && o.S - o.Length < to) return false;
            }
            return true;
        }

        void Honk(SimVehicle v, int leaderId, float dt)
        {
            if (leaderId < 0) { v.StuckTimer = v.Speed < 0.3f ? v.StuckTimer : 0f; return; }
            var leader = Vehicles[leaderId];
            // Blocked by a stationary player car that isn't waiting at a junction: honk.
            if (leader.IsExternal && leader.Speed < 0.3f && v.Speed < 0.3f && v.StuckTimer > 4f && v.HornTimer <= 0f &&
                (int)(v.StuckTimer * 10f) % 40 == 0)
                v.HornTimer = v.Profile.Type == DriverType.Aggressive ? 1.2f : 0.5f;
        }

        static float SlowTo(SimVehicle v, float target, float distance)
        {
            if (v.Speed <= target) return float.MaxValue;
            return (target * target - v.Speed * v.Speed) / (2f * Math.Max(distance, 1f));
        }

        bool Chance(float p, int salt) => ((uint)(salt * 2654435761u) % 1000u) < p * 1000f;

        bool OnFinalEdge(SimVehicle v) =>
            v.DestLane >= 0 && Graph.Lanes[v.DestLane].EdgeId == Graph.Lanes[v.Lane].EdgeId &&
            Graph.Lanes[v.DestLane].ToNode == Graph.Lanes[v.Lane].ToNode;

        void Commit(SimVehicle v)
        {
            v.Committed = true;
            v.CommitTimer = 0f;
            _connCommitted[v.NextConnector].Add(v.Id);
        }

        /// <summary>IDM acceleration against the nearest thing ahead (vehicle, stop line, or path end).</summary>
        float Leader(SimVehicle v, in IdmParams p, out int leaderId)
        {
            leaderId = -1;
            float gap = float.MaxValue, leaderSpeed = 0f;
            float remaining;
            int nextConnector;
            if (v.Mode == VehicleMode.Lane)
            {
                if (AheadIn(_laneOcc[v.Lane], v, out leaderId, out gap, out leaderSpeed)) return Idm.Accel(p, v.Speed, gap, leaderSpeed);
                remaining = Graph.Lanes[v.Lane].Length - v.S;
                nextConnector = v.NextConnector;
                if (nextConnector < 0) return Idm.FreeRoadAccel(p, v.Speed);
                if (remaining > LookAhead) return Idm.FreeRoadAccel(p, v.Speed);
                if (TailOf(_connOcc[nextConnector], out var t)) { leaderId = t; return Gap(p, v, remaining + Vehicles[t].S - Vehicles[t].Length, Vehicles[t].Speed); }
                var con = Graph.Connectors[nextConnector];
                if (TailOf(_laneOcc[con.ToLane], out t)) { leaderId = t; return Gap(p, v, remaining + con.Length + Vehicles[t].S - Vehicles[t].Length, Vehicles[t].Speed); }
                return Idm.FreeRoadAccel(p, v.Speed);
            }
            else // Connector
            {
                if (AheadIn(_connOcc[v.Connector], v, out leaderId, out gap, out leaderSpeed)) return Idm.Accel(p, v.Speed, gap, leaderSpeed);
                var con = Graph.Connectors[v.Connector];
                remaining = con.Length - v.S;
                if (TailOf(_laneOcc[con.ToLane], out var t)) { leaderId = t; return Gap(p, v, remaining + Vehicles[t].S - Vehicles[t].Length, Vehicles[t].Speed); }
                return Idm.FreeRoadAccel(p, v.Speed);
            }
        }

        static float Gap(in IdmParams p, SimVehicle v, float gap, float speed) => Idm.Accel(p, v.Speed, gap, speed);

        bool AheadIn(List<int> occ, SimVehicle v, out int leader, out float gap, out float speed)
        {
            leader = -1; gap = 0f; speed = 0f;
            float best = float.MaxValue;
            foreach (int id in occ)
            {
                if (id == v.Id) continue;
                var o = Vehicles[id];
                if (o.S <= v.S && !(o.S == v.S && id > v.Id)) continue;
                float g = o.S - o.Length - v.S;
                if (g < best) { best = g; leader = id; speed = o.Speed; }
            }
            gap = best;
            return leader >= 0;
        }

        bool TailOf(List<int> occ, out int id)
        {
            id = occ.Count > 0 ? occ[0] : -1;
            return id >= 0;
        }

        /// <summary>Junction entry rules: control, conflicts, yielding, and exit space.</summary>
        bool CanEnter(SimVehicle v, Connector con, float remaining)
        {
            var lane = Graph.Lanes[con.FromLane];
            var j = Graph.Junctions[con.NodeId];
            bool yieldToApproaching = false;

            switch (lane.Control)
            {
                case ApproachControl.Signal:
                    var st = Signal(con.NodeId, lane.EdgeId);
                    if (st == SignalState.Red) return false;
                    if (st == SignalState.Amber)
                    {
                        float stopDist = v.Speed * v.Speed / (2f * v.Profile.ComfortDecel);
                        bool cantStop = remaining < stopDist;
                        if (!cantStop && !(Chance(v.Profile.AmberRisk, v.Id + (int)Time) && remaining < stopDist * 2f)) return false;
                    }
                    yieldToApproaching = con.Turn == TurnType.Left || con.Turn == TurnType.UTurn;
                    break;
                case ApproachControl.Stop:
                    if (!v.StoppedAtLine || Time - v.StopTime < 0.8f) return false;
                    if (j.Control == JunctionControl.AllWayStop && !FirstToStop(v, j)) return false;
                    yieldToApproaching = j.Control == JunctionControl.Priority;
                    break;
                default:
                    yieldToApproaching = (con.Turn == TurnType.Left || con.Turn == TurnType.UTurn) && j.Control == JunctionControl.Priority;
                    break;
            }

            foreach (int k in con.Conflicts)
            {
                if (_connOcc[k].Count > 0) return false;
                foreach (int id in _connCommitted[k]) if (id != v.Id) return false;
            }

            if (yieldToApproaching)
                foreach (int k in con.Conflicts)
                {
                    var other = Graph.Connectors[k];
                    var otherLane = Graph.Lanes[other.FromLane];
                    if (!HasPriorityOver(otherLane, lane, j)) continue;
                    foreach (int id in _laneOcc[otherLane.Id])
                    {
                        var o = Vehicles[id];
                        if (!o.IsExternal && o.NextConnector != k) continue;
                        float dist = otherLane.Length - o.S;
                        if (dist > LookAhead) continue;
                        if (dist / Math.Max(o.Speed, 0.5f) < v.Profile.CriticalGap) return false;
                    }
                }

            // Don't block the box: there must be room on the far side.
            if (TailOf(_laneOcc[con.ToLane], out int tail))
            {
                var t = Vehicles[tail];
                if (t.S - t.Length < v.Length + 1.5f && t.Speed < 2f) return false;
            }
            return true;
        }

        bool HasPriorityOver(Lane other, Lane mine, Junction j)
        {
            if (other.Control == ApproachControl.Signal) return Signal(j.NodeId, other.EdgeId) != SignalState.Red;
            if (other.Control == ApproachControl.Free) return true;
            return false;
        }

        bool FirstToStop(SimVehicle v, Junction j)
        {
            foreach (int laneId in j.IncomingLanes)
            {
                var occ = _laneOcc[laneId];
                if (occ.Count == 0) continue;
                var front = Vehicles[occ[occ.Count - 1]];
                if (front.Id != v.Id && front.StoppedAtLine && !front.IsExternal && front.StopTime < v.StopTime) return false;
            }
            return true;
        }

        void TryLaneChange(SimVehicle v, Lane lane, float remaining)
        {
            if (lane.LanesInDirection < 2 || v.S < 12f || remaining < 6f) return;
            int target = -1;
            if (v.NextConnector < 0 && !OnFinalEdge(v) && v.RouteIndex < v.Route.Count)
            {
                // Mandatory: move towards a lane that has our turn.
                int want = RequiredLane(lane, v.Route[v.RouteIndex]);
                if (want >= 0) target = Graph.Lanes[want].Index > lane.Index ? lane.CentreNeighbor : lane.KerbNeighbor;
            }
            else if (v.DestBay >= 0 && Graph.Bays[v.DestBay].AccessLane != v.Lane &&
                     Graph.Lanes[Graph.Bays[v.DestBay].AccessLane].EdgeId == lane.EdgeId &&
                     Graph.Lanes[Graph.Bays[v.DestBay].AccessLane].ToNode == lane.ToNode)
            {
                target = lane.KerbNeighbor; // heading for a kerbside bay
            }
            else if (v.KerbStop < 0 && remaining > 30f && AheadIn(_laneOcc[lane.Id], v, out int parker, out float pgap, out _) &&
                     Vehicles[parker].KerbTimer > 0f && pgap < 25f)
            {
                // Stuck behind a bus or a double-parked van: go round it.
                int cand = lane.CentreNeighbor >= 0 ? lane.CentreNeighbor : lane.KerbNeighbor;
                if (cand >= 0 && RouteAllows(v, cand) && GapAhead(cand, v) > pgap + 6f) target = cand;
            }
            else if (remaining > 80f && v.Speed > 3f && v.KerbStop < 0)
            {
                // Discretionary (MOBIL-lite): overtake a slower leader if the other lane is better.
                AheadIn(_laneOcc[lane.Id], v, out int leader, out float gap, out float lspeed);
                if (leader >= 0 && gap < 40f && lspeed < v.Speed - 1.5f)
                {
                    int cand = lane.CentreNeighbor >= 0 ? lane.CentreNeighbor : lane.KerbNeighbor;
                    if (cand >= 0 && RouteAllows(v, cand) && GapAhead(cand, v) > gap + 15f * (1f + v.Profile.Politeness)) target = cand;
                }
            }
            if (target < 0) { v.PendingLane = -1; return; }
            // Signal first, move after: ~1.3 s of indicator before the change (less for the impatient).
            if (v.PendingLane != target)
            {
                v.PendingLane = target;
                v.SignalTimer = v.Profile.Type == DriverType.Aggressive ? 0.5f : v.Profile.Type == DriverType.Distracted ? 0.4f : 1.3f;
                return;
            }
            if (v.SignalTimer > 0f || !SafeToMerge(v, target)) return;
            v.PendingLane = -1;

            float side = Graph.Lanes[target].Index > lane.Index ? -1f : 1f; // centre-ward = left
            _laneOcc[v.Lane].Remove(v.Id);
            v.Lane = target;
            _laneOcc[target].Add(v.Id);
            _laneOcc[target].Sort((a, b) => Vehicles[a].S.CompareTo(Vehicles[b].S));
            v.Lateral = side * Graph.Lanes[target].Width; // start from the old lane visually
            v.LaneChangeCooldown = 4f;
            v.Committed = false;
            ChooseNextConnector(v);
        }

        int RequiredLane(Lane lane, int nextEdge)
        {
            int id = lane.Id;
            while (Graph.Lanes[id].KerbNeighbor >= 0) id = Graph.Lanes[id].KerbNeighbor;
            for (; id >= 0; id = Graph.Lanes[id].CentreNeighbor)
                if (Graph.ConnectorTo(id, nextEdge) >= 0) return id;
            return -1;
        }

        bool RouteAllows(SimVehicle v, int laneId) =>
            v.RouteIndex >= v.Route.Count || Graph.ConnectorTo(laneId, v.Route[v.RouteIndex]) >= 0 ||
            Graph.Lanes[laneId].Length - v.S > 150f;

        float GapAhead(int laneId, SimVehicle v)
        {
            float best = Graph.Lanes[laneId].Length - v.S + 30f;
            foreach (int id in _laneOcc[laneId])
            {
                var o = Vehicles[id];
                if (o.S > v.S) best = Math.Min(best, o.S - o.Length - v.S);
            }
            return best;
        }

        bool SafeToMerge(SimVehicle v, int laneId)
        {
            foreach (int id in _laneOcc[laneId])
                if (!ClearOf(v, Vehicles[id], Vehicles[id].S)) return false;
            // Vehicles still in the junction, about to arrive on the target lane.
            foreach (int c in Graph.Lanes[laneId].Incoming)
                foreach (int id in _connOcc[c])
                    if (!ClearOf(v, Vehicles[id], Vehicles[id].S - Graph.Connectors[c].Length)) return false;
            return true;
        }

        const float SafeDecel = 4f;

        /// <summary>
        /// MOBIL safety criterion against vehicle <paramref name="o"/> (front at <paramref name="oS"/> in
        /// the target lane's coordinates): neither we (if it's ahead) nor it (if it's behind) may need to
        /// brake harder than <see cref="SafeDecel"/>.
        /// </summary>
        bool ClearOf(SimVehicle v, SimVehicle o, float oS)
        {
            if (o.Id == v.Id) return true;
            float limit = Graph.Lanes[v.Lane].SpeedLimit;
            if (oS >= v.S)
            {
                float gap = oS - o.Length - v.S;
                return gap > 1f && Idm.Accel(LaneIdm(v, limit), v.Speed, gap, o.Speed) > -SafeDecel;
            }
            float back = v.S - v.Length - oS;
            if (back < 1f) return false;
            if (o.IsExternal) return back > o.Speed * 1.5f + 3f;
            return Idm.Accel(LaneIdm(o, limit), o.Speed, back, v.Speed) > -SafeDecel;
        }

        // ---- integration & transitions -------------------------------------------------

        void Integrate(SimVehicle v, float dt)
        {
            v.Speed = Math.Max(0f, v.Speed + v.Accel * dt);
            v.S += v.Speed * dt;
            // Lane changes move sideways with forward motion (no crabbing at a standstill).
            float latRate = dt * (0.05f + 1.35f * Math.Min(1f, v.Speed / 5f));
            if (MathF.Abs(v.Lateral) > 0f)
                v.Lateral = MathF.Abs(v.Lateral) <= latRate ? 0f : v.Lateral - MathF.Sign(v.Lateral) * latRate;
            UpdateWander(v, dt);
            v.StuckTimer = v.Speed < 0.3f && v.Mode != VehicleMode.Parked ? v.StuckTimer + dt : 0f;

            switch (v.Mode)
            {
                case VehicleMode.Lane:
                {
                    var lane = Graph.Lanes[v.Lane];
                    if (v.DestLane >= 0 && OnFinalEdge(v) && v.S >= v.DestS) { TripDone(v); return; }
                    if (v.DestBay >= 0 && Graph.Bays[v.DestBay].AccessLane == v.Lane)
                    {
                        var bay = Graph.Bays[v.DestBay];
                        if (v.S >= bay.EntryS && v.S < bay.EntryS + 6f)
                        {
                            v.Mode = VehicleMode.ParkIn;
                            v.Bay = v.DestBay;
                            v.S -= bay.EntryS;
                            return;
                        }
                    }
                    if (v.S >= lane.Length)
                    {
                        if (v.NextConnector >= 0 && v.Committed)
                        {
                            v.S -= lane.Length;
                            v.Mode = VehicleMode.Connector;
                            v.Connector = v.NextConnector;
                            v.Committed = false;
                            v.StoppedAtLine = false;
                        }
                        else { v.S = lane.Length; v.Speed = 0f; }
                    }
                    break;
                }
                case VehicleMode.Connector:
                {
                    var con = Graph.Connectors[v.Connector];
                    if (v.S >= con.Length)
                    {
                        v.S -= con.Length;
                        v.Mode = VehicleMode.Lane;
                        v.Lane = con.ToLane;
                        v.Connector = -1;
                        EnterLane(v);
                    }
                    break;
                }
                case VehicleMode.ParkIn:
                {
                    var bay = Graph.Bays[v.Bay];
                    if (v.S >= bay.EntryLength - 0.6f && v.Speed < 0.3f)
                    {
                        v.S = bay.EntryLength;
                        v.Speed = 0f;
                        v.Mode = VehicleMode.Parked;
                        v.DestBay = -1;
                        v.ParkTimer = _rng.Range(15f, 70f);
                    }
                    else if (v.S > bay.EntryLength) v.S = bay.EntryLength;
                    break;
                }
                case VehicleMode.ParkOut:
                {
                    var bay = Graph.Bays[v.Bay];
                    if (v.S >= bay.ExitLength)
                    {
                        _bayOwner[v.Bay] = -1;
                        v.Mode = VehicleMode.Lane;
                        v.Lane = bay.AccessLane;
                        v.S = bay.ExitS + (v.S - bay.ExitLength);
                        v.Bay = -1;
                        NewTrip(v);
                    }
                    break;
                }
            }

            if (v.StuckTimer > 45f && !IsObserved(v.Position)) Remove(v.Id);
        }

        /// <summary>
        /// Lane wander: a slow, smooth drift as a function of distance travelled (so it freezes when
        /// stopped), eased to zero near lane ends so junction paths join up, and nudged to the kerb
        /// for kerb stops. Sideways speed is tied to forward speed, like steering.
        /// </summary>
        void UpdateWander(SimVehicle v, float dt)
        {
            v.Odometer += v.Speed * dt;
            float target = 0f;
            if (v.Mode == VehicleMode.Lane)
            {
                var lane = Graph.Lanes[v.Lane];
                float fade = Math.Clamp(Math.Min(v.S - v.Length, lane.Length - v.S) / 12f, 0f, 1f);
                float o = v.Odometer;
                target = v.Profile.WanderAmp * fade *
                         (0.65f * MathF.Sin(o / 19f + v.WanderPhase) + 0.35f * MathF.Sin(o / 7.3f + v.WanderPhase * 2.1f));
                if (v.KerbTimer > 0f || (v.KerbStop >= 0 && KerbStopFront(v, Graph.KerbStops[v.KerbStop]) - v.S < 25f))
                    target = -KerbShift;
            }
            else if (v.Mode == VehicleMode.Connector)
            {
                // Everyone's own line through a turn: some cut the corner, some swing wide. Zero at both
                // ends so it joins the lanes either side.
                var con = Graph.Connectors[v.Connector];
                float side = con.Turn == TurnType.Left ? 1f : con.Turn == TurnType.Right ? -1f : 0f;
                float f = Math.Clamp((v.S - v.Length * 0.5f) / Math.Max(con.Length, 1f), 0f, 1f);
                target = side * v.Profile.TurnLine * MathF.Sin(MathF.PI * f);
            }
            float maxStep = dt * (0.03f + 0.25f * Math.Min(v.Speed, 6f));
            v.Wander += Math.Clamp(target - v.Wander, -maxStep, maxStep);
        }

        void EnterLane(SimVehicle v)
        {
            v.PendingLane = -1;
            var lane = Graph.Lanes[v.Lane];
            ChooseKerbStop(v, lane);
            if (v.RouteIndex < v.Route.Count && v.Route[v.RouteIndex] == lane.EdgeId) v.RouteIndex++;
            else if (!OnFinalEdge(v) && !(v.DestBay >= 0 && Graph.Lanes[Graph.Bays[v.DestBay].AccessLane].EdgeId == lane.EdgeId))
                Replan(v);

            // Lost / distracted drivers occasionally take a wrong turn at the next junction —
            // or realise they're heading the wrong way and turn round.
            if (_rng.NextDouble() < v.Profile.WrongTurnChance && lane.Outgoing.Count > 1 && !OnFinalEdge(v))
            {
                int u = Graph.UTurnFrom(v.Lane);
                v.NextConnector = u >= 0 && _rng.NextDouble() < v.Profile.UTurnChance * 0.5 ? u : Graph.RandomOutgoing(v.Lane, ref _rng);
                v.Route.Clear();
                v.RouteIndex = 0;
                return;
            }
            ChooseNextConnector(v);
        }

        /// <summary>Buses stop at every bus stop; vans and lorries sometimes double-park at kerb stops on wide roads.</summary>
        void ChooseKerbStop(SimVehicle v, Lane lane)
        {
            v.KerbStop = -1;
            if (!Graph.LaneStops.TryGetValue(lane.Id, out var stops)) return;
            var k = Graph.KerbStops[stops[0]];
            if (KerbStopFront(v, k) - v.S < 25f) return;
            bool bus = v.Model.Style == BodyStyle.Bus;
            bool delivery = v.Model.Style == BodyStyle.Van || v.Model.Style == BodyStyle.BoxTruck;
            if (bus ? k.BusStop : delivery && !k.BusStop && lane.LanesInDirection >= 2 && _rng.NextDouble() < 0.45)
                v.KerbStop = k.Id;
        }

        void ChooseNextConnector(SimVehicle v)
        {
            v.NextConnector = -1;
            v.Committed = false;
            if (v.WantUTurn)
            {
                v.WantUTurn = false;
                int u = Graph.UTurnFrom(v.Lane);
                if (u >= 0) { v.NextConnector = u; return; }
            }
            if (v.RouteIndex < v.Route.Count) v.NextConnector = Graph.ConnectorTo(v.Lane, v.Route[v.RouteIndex]);
            else if (!OnFinalEdge(v) && v.DestBay < 0 && Graph.Lanes[v.Lane].Outgoing.Count > 0)
                v.NextConnector = Graph.RandomOutgoing(v.Lane, ref _rng);
        }

        void TripDone(SimVehicle v)
        {
            if (!IsObserved(v.Position) && _rng.NextDouble() < 0.6) { Remove(v.Id); return; }
            NewTrip(v);
        }

        void ReleaseDestination(SimVehicle v)
        {
            if (v.DestBay >= 0 && _bayOwner[v.DestBay] == v.Id) _bayOwner[v.DestBay] = -1;
            v.DestBay = -1;
            v.DestLane = -1;
        }

        void NewTrip(SimVehicle v)
        {
            ReleaseDestination(v);
            if (!v.Model.NpcOnly && _rng.NextDouble() < v.Profile.ParkChance && Graph.Bays.Count > 0)
            {
                for (int tries = 0; tries < 6; tries++)
                {
                    int b = _rng.NextInt(Graph.Bays.Count);
                    if (_bayOwner[b] >= 0) continue;
                    if (Vec2.Distance(Graph.Bays[b].Position, v.Position) < 200f) continue;
                    if (PlanToBay(v, b)) return;
                }
            }
            for (int tries = 0; tries < 10; tries++)
            {
                int l = _rng.PickWeighted(_laneWeights);
                var lane = Graph.Lanes[l];
                if (lane.Length < 40f || Vec2.Distance(lane.Start, v.Position) < 300f) continue;
                v.DestLane = l;
                v.DestS = _rng.Range(10f, lane.Length - 10f);
                if (Plan(v, lane)) return;
            }
            v.DestLane = -1;
            v.Route.Clear();
            v.RouteIndex = 0;
            ChooseNextConnector(v);
        }

        bool PlanToBay(SimVehicle v, int bayId)
        {
            var bay = Graph.Bays[bayId];
            _bayOwner[bayId] = v.Id;
            v.DestBay = bayId;
            if (Plan(v, Graph.Lanes[bay.AccessLane], bay.EntryS)) return true;
            ReleaseDestination(v);
            return false;
        }

        bool Plan(SimVehicle v, Lane target, float targetS = -1f)
        {
            var cur = Graph.Lanes[v.Lane];
            int start = Router.Directed(cur.EdgeId, cur.ToNode);
            int goal = Router.Directed(target.EdgeId, target.ToNode);
            if (targetS < 0f) targetS = v.DestS;
            bool mustLeave = start == goal && v.S > targetS - 5f;
            var route = Router.Route(start, goal, mustLeave, 0.35f, ref _rng);
            if (route == null) return false;
            v.Route.Clear();
            v.Route.AddRange(route);
            v.RouteIndex = 0;
            // Destination back the way we came: some drivers just turn round at the next junction.
            var goalPoint = targetS >= 0f ? target.PointAt(Math.Min(targetS, target.Length)) : target.Start;
            if (!v.Model.NpcOnly && Vec2.Dot(goalPoint - cur.PointAt(v.S), cur.Direction) < -60f &&
                Graph.UTurnFrom(cur.Id) >= 0 && _rng.NextDouble() < v.Profile.UTurnChance)
                v.WantUTurn = true;
            ChooseNextConnector(v);
            return true;
        }

        void Replan(SimVehicle v)
        {
            if (v.DestBay >= 0) { if (!PlanToBay(v, v.DestBay)) NewTrip(v); return; }
            if (v.DestLane >= 0 && Plan(v, Graph.Lanes[v.DestLane])) return;
            NewTrip(v);
        }

        // ---- spawning ------------------------------------------------------------------

        SimVehicle Allocate()
        {
            SimVehicle v;
            if (_free.Count > 0) v = Vehicles[_free.Pop()];
            else { v = new SimVehicle { Id = Vehicles.Count }; Vehicles.Add(v); }
            int id = v.Id;
            v = new SimVehicle { Id = id, Alive = true, Mode = VehicleMode.Lane };
            Vehicles[id] = v;
            return v;
        }

        void TrySpawn()
        {
            int l = _rng.PickWeighted(_laneWeights);
            var lane = Graph.Lanes[l];
            if (lane.Length < 30f) return;
            float s = _rng.Range(12f, lane.Length - 15f);
            var pos = lane.PointAt(s);
            if (IsObserved(pos)) return;
            foreach (int id in _laneOcc[l])
                if (MathF.Abs(Vehicles[id].S - s) < 14f) return;

            bool bus = _busCount < BusTarget && lane.Index == 0;
            var v = Allocate();
            v.Identity = bus ? Identities.Create(BusModel, VehicleCatalog.Models[BusModel].Livery) : Identities.Random();
            v.Model = VehicleCatalog.Models[v.Identity.ModelId];
            v.Length = v.Model.Length;
            v.Profile = DriverProfile.Sample(ref _rng);
            if (v.Model.NpcOnly)
            {
                // Professional drivers: steady, patient, always signal.
                v.Profile.Type = DriverType.Normal; v.Profile.IndicatorChance = 1f; v.Profile.WrongTurnChance = 0f;
                v.Profile.AmberRisk = 0f; v.Profile.UTurnChance = 0f; v.Profile.WanderAmp *= 0.6f;
            }
            if (bus) _busCount++;
            v.WanderPhase = _rng.Range(0f, 6.283f);
            v.WobblePhase = _rng.Range(0f, 6.283f);
            v.Odometer = _rng.Range(0f, 500f);
            v.Lane = l;
            v.S = s;
            v.Speed = lane.SpeedLimit * 0.7f;
            v.Position = pos;
            v.Heading = lane.Direction;
            _npcCount++;
            _laneOcc[l].Add(v.Id);
            _laneOcc[l].Sort((a, b) => Vehicles[a].S.CompareTo(Vehicles[b].S));
            NewTrip(v);
            ChooseKerbStop(v, lane);
            Spawned?.Invoke(v);
        }

        static readonly int BusModel = Array.FindIndex(VehicleCatalog.Models, m => m.Style == BodyStyle.Bus);

        // ---- poses ---------------------------------------------------------------------

        void UpdatePoses()
        {
            foreach (var v in Vehicles)
            {
                if (!v.Alive || v.IsExternal) continue;
                VehiclePose.Compute(Graph, v.Mode, v.Lane, v.Connector, v.Bay, v.S, v.Length, v.VisualLateral, out v.Position, out v.Heading);
            }
        }
    }

    static class SimVehicleExtensions
    {
        public static int NextConnectorOrCurrent(this SimVehicle v) => v.Mode == VehicleMode.Connector ? v.Connector : v.NextConnector;
    }
}
