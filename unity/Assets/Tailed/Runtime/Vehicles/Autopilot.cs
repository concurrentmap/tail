using System.Collections.Generic;
using Tailed.Core.Roads;
using Tailed.Core.Traffic;
using Tailed.Core.Util;
using Tailed.Traffic;
using UnityEngine;

namespace Tailed.Vehicles
{
    /// <summary>
    /// Drives a <see cref="PlayerCar"/> along the lane graph like a (slightly clumsy) NPC: pure pursuit
    /// steering, speed from curvature, stops for reds and obstacles. Used for bots, soak tests and
    /// multi-instance smoke tests (-tailed-autodrive). Optionally follows a target car (bot Tails).
    /// </summary>
    public sealed class Autopilot : MonoBehaviour
    {
        public Transform FollowTarget;
        /// <summary>Drive to this parking bay and stop in it (-1 = roam).</summary>
        public int TargetBay = -1;
        public bool Arrived { get; private set; }

        public void ParkAtBay(int bay)
        {
            TargetBay = bay;
            Arrived = false;
            _path.Clear();
            _bestGoalDist = float.MaxValue;
            _noProgress = 0f;
        }

        /// <summary>
        /// Drive to a business and park in any free bay there (the rules accept any of its bays).
        /// The choice is re-checked on the approach in case another car takes it first.
        /// </summary>
        public void ParkAtSite(int poi)
        {
            TargetSite = poi;
            ParkAtBay(FreeBay(poi));
        }
        public int TargetSite = -1;
        float _bayRecheck;

        int FreeBay(int poi)
        {
            var g = TrafficRunner.Instance.Town.Lanes;
            var site = g.Sites[poi];
            int best = site.Bays[0]; float bestD = float.MaxValue;
            foreach (int b in site.Bays)
            {
                if (BayTaken(g, b)) continue;
                var c = Core.Rules.Match.BayCentre(g.Bays[b]);
                float d = (new Vector2(c.X, c.Y) - new Vector2(transform.position.x, transform.position.z)).sqrMagnitude;
                if (d < bestD) { bestD = d; best = b; }
            }
            return best;
        }

        bool BayTaken(LaneGraph g, int b)
        {
            var c = Core.Rules.Match.BayCentre(g.Bays[b]);
            foreach (var col in Physics.OverlapSphere(new Vector3(c.X, 0.9f, c.Y), 1.8f, 1 << Map.Layers.Vehicles | 1 << Map.Layers.PlayerCar, QueryTriggerInteraction.Ignore))
                if (col.attachedRigidbody == null || col.attachedRigidbody.gameObject != gameObject) return true;
            return false;
        }

        /// <summary>Dev: planner state for the trip harness.</summary>
        public string DebugState => $"path={_path.Count} cursor={_cursor} replans={_replans} watchdog={_watchdogs} noProgress={_noProgress:0}";
        int _replans, _watchdogs;
        float _bestGoalDist = float.MaxValue, _noProgress;
        public float FollowDistance = 45f;

        PlayerCar _car;
        readonly List<Vector3> _path = new List<Vector3>();
        readonly List<(int lane, int pathIndex)> _laneEnds = new List<(int, int)>();
        int _cursor;
        Rng _rng;
        float _replanTimer;
        static int _instances;

        void Awake()
        {
            _car = GetComponent<PlayerCar>();
            _rng = new Rng((ulong)(++_instances) * 7919UL + (ulong)System.Environment.TickCount);
        }

        void OnEnable() => _path.Clear();

        void OnDisable() { if (_car != null) _car.ReverseOnBrake = true; }

        /// <summary>Draw the planned path (dev).</summary>
        public static bool DebugDraw;
        public float LastAimAngle;
        LineRenderer _line;

        void Update()
        {
            if (!DebugDraw) { if (_line) _line.enabled = false; return; }
            if (_line == null)
            {
                _line = new GameObject("AutopilotPath").AddComponent<LineRenderer>();
                _line.material = new Material(Shader.Find("Tailed/UnlitVertexColor"));
                _line.widthMultiplier = 0.35f;
                _line.startColor = Color.magenta; _line.endColor = Color.yellow;
            }
            _line.enabled = true;
            _line.positionCount = _path.Count;
            for (int i = 0; i < _path.Count; i++) _line.SetPosition(i, _path[i] + Vector3.up * 0.4f);
        }

        void FixedUpdate()
        {
            var runner = TrafficRunner.Instance;
            if (runner == null || runner.Town == null) return;
            var g = runner.Town.Lanes;
            var pos = transform.position;
            _replanTimer -= Time.fixedDeltaTime;
            // Brake means brake (a parked or waiting bot must never creep backwards), except while backing out.
            _car.ReverseOnBrake = _reverseTimer > 0f;
            float goalDist = float.MaxValue;
            if (TargetBay >= 0)
            {
                // Parked: close to the bay centre (where the rules look for us) and nearly stopped,
                // or at the end of the bay's entry path.
                var bc = Core.Rules.Match.BayCentre(g.Bays[TargetBay]);
                goalDist = Flat(new Vector3(bc.X, 0, bc.Y) - pos).magnitude;
                float v = Mathf.Abs(_car.ForwardSpeed);
                bool atEnd = _path.Count > 0 && _cursor >= _path.Count - 2;
                if (!Arrived && ((goalDist < 3f && v < 1.5f) || (atEnd && goalDist < 5f && v < 0.5f))) Arrived = true;
                // Someone parked in our bay while we were on the way: take another at the same place.
                if (!Arrived && TargetSite >= 0 && goalDist < 45f && goalDist > 4f && (_bayRecheck -= Time.fixedDeltaTime) <= 0f)
                {
                    _bayRecheck = 1f;
                    if (BayTaken(g, TargetBay))
                    {
                        int other = FreeBay(TargetSite);
                        if (other != TargetBay) { TargetBay = other; _path.Clear(); }
                    }
                }

                // Watchdog: no progress ALONG the planned path for a long while (wedged, or off it and
                // circling) → re-plan. Straight-line distance is useless here: detours and red lights
                // legitimately keep it flat.
                if (!Arrived && _path.Count > 1)
                {
                    float left = 0f;
                    for (int k = _cursor; k < _path.Count - 1; k++) left += Vector3.Distance(_path[k], _path[k + 1]);
                    if (left < _bestGoalDist - 5f) { _bestGoalDist = left; _noProgress = 0f; }
                    else _noProgress += Time.fixedDeltaTime;
                    if (_noProgress > 45f) { _noProgress = 0f; _watchdogs++; _path.Clear(); }
                }
            }
            bool pathSpent = _path.Count > 0 && _cursor >= _path.Count - 3;
            if (!Arrived && (_path.Count == 0 || (pathSpent && (TargetBay < 0 || goalDist > 12f)) || (_replanTimer <= 0f && FollowTarget != null))) Plan(g);
            if (Arrived) { _car.Brake = 1f; _car.Throttle = 0f; _car.Steer = 0f; return; }
            // Last few metres: a parking manoeuvre, not path following (see FinalApproach).
            if (TargetBay >= 0 && goalDist < 9f && _reverseTimer <= 0f) { FinalApproach(g, goalDist); return; }
            _finalTime = 0f;
            if (_path.Count == 0) { _car.Brake = 1f; _car.Throttle = 0f; _car.Steer = 0f; return; }

            // Project onto the nearest path segment in a short forward window (never backwards).
            float bestD = float.MaxValue;
            int bestSeg = _cursor;
            for (int i = _cursor; i < Mathf.Min(_path.Count - 1, _cursor + 25); i++)
            {
                var a = Flat(_path[i]); var b = Flat(_path[i + 1]);
                var ab = b - a;
                float t = ab.sqrMagnitude > 1e-6f ? Mathf.Clamp01(Vector3.Dot(Flat(pos) - a, ab) / ab.sqrMagnitude) : 0f;
                float d = (a + ab * t - Flat(pos)).sqrMagnitude;
                if (d < bestD) { bestD = d; bestSeg = i; _segT = t; }
            }
            _cursor = bestSeg;
            // Too far off the path (knocked off course): replan from here.
            if (bestD > 12f * 12f) { _path.Clear(); return; }

            float speed = Mathf.Max(0f, _car.ForwardSpeed);

            // Stuck (wedged on a wall or kerb): back out with opposite lock, then replan.
            _car.ReverseOnBrake = _reverseTimer > 0f; // brake means brake, except when deliberately backing out
            if (_reverseTimer > 0f)
            {
                _reverseTimer -= Time.fixedDeltaTime;
                _car.Throttle = 0f; _car.Brake = 1f; _car.Steer = -_stuckSteer;
                if (_reverseTimer <= 0f) _path.Clear();
                return;
            }
            _stuckTimer = (_car.Throttle > 0.5f && speed < 0.5f) ? _stuckTimer + Time.fixedDeltaTime : 0f;
            if (_stuckTimer > 1.5f) { _stuckTimer = 0f; _reverseTimer = 1.8f; _stuckSteer = _car.Steer; return; }

            float look = Mathf.Clamp(speed * 0.6f, 4f, 12f);
            var target = PointAhead(look, out _);
            var local = transform.InverseTransformPoint(target);
            float angle = Mathf.Atan2(local.x, local.z) * Mathf.Rad2Deg;
            LastAimAngle = angle;
            _car.Steer = Mathf.Clamp(angle / 28f, -1f, 1f);

            // Brake for the sharpest bend within the next ~35 m, not just the aim point.
            float curvature = UpcomingTurn(35f, out float turnDist);
            float turnSpeed = Mathf.Lerp(12.5f, 4.5f, Mathf.Clamp01(curvature / 70f));
            float desired = Mathf.Min(12.5f, Mathf.Sqrt(turnSpeed * turnSpeed + 2f * 2.5f * Mathf.Max(0f, turnDist - 4f)));
            float stopIn = StopDistance(g, runner);
            if (FollowTarget != null)
            {
                // Hang back: ease off approaching the follow distance, stop well short of the target.
                float d = Vector3.Distance(FollowTarget.position, pos);
                desired *= Mathf.Clamp((d - 25f) / FollowDistance, 0f, 1.25f);
            }
            if (stopIn < float.MaxValue) desired = Mathf.Min(desired, Mathf.Sqrt(Mathf.Max(0f, 2f * 3f * (stopIn - 3f))));
            if (TargetBay >= 0)
            {
                // Ease into the bay: parking speed for the last stretch (the S-curve in is tight).
                float left = 0f;
                for (int k = _cursor; k < _path.Count - 1; k++) left += Vector3.Distance(_path[k], _path[k + 1]);
                desired = Mathf.Min(desired, Mathf.Sqrt(Mathf.Max(0f, 2f * 1.8f * (left - 0.5f))));
                if (left < 30f) desired = Mathf.Min(desired, 3.5f + left * 0.1f);
            }
            // Obstacles along where we're actually steering (not straight ahead: pulling out of a bay,
            // straight ahead is the car in the next bay).
            var aimDir = Flat(target - pos).normalized;
            if (aimDir.sqrMagnitude < 0.5f) aimDir = transform.forward;
            if (Physics.Raycast(pos + Vector3.up * 0.8f + aimDir * (_car.Shape.Length * 0.5f), aimDir, out var hit, 8f + speed * 1.2f, 1 << Map.Layers.Vehicles | 1 << Map.Layers.PlayerCar))
            {
                desired = Mathf.Min(desired, Mathf.Max(0f, (hit.distance - 4f) * 0.6f));
                // Blocked by something that isn't moving (a parked car across our way): back off, re-plan.
                var body = hit.collider.attachedRigidbody;
                bool still = body == null || body.isKinematic || body.linearVelocity.magnitude < 0.3f;
                _blocked = speed < 0.3f && still ? _blocked + Time.fixedDeltaTime : 0f;
                if (_blocked > 8f) { _blocked = 0f; _reverseTimer = 1.8f; _stuckSteer = -_car.Steer; }
            }
            else _blocked = 0f;

            float err = desired - speed;
            _car.Throttle = Mathf.Clamp01(err * 0.5f);
            _car.Brake = Mathf.Clamp01(-err * 0.35f);
            _car.Handbrake = false;
        }

        static Vector3 Flat(Vector3 v) => new Vector3(v.x, 0f, v.z);

        float _finalTime, _blocked;

        /// <summary>
        /// Parking, the way a driver does it at walking pace: creep forward to the bay if it's ahead,
        /// reverse into it if it's behind, back out and try again if it's alongside. Counts as parked
        /// once inside any bay of the business (what the rules check), nearly stopped.
        /// </summary>
        void FinalApproach(LaneGraph g, float goalDist)
        {
            _finalTime += Time.fixedDeltaTime;
            var pos = transform.position;
            float v = _car.ForwardSpeed;
            // Inside any bay of the target site (or ours) and slow: done.
            float nearestSiteBay = goalDist;
            if (TargetSite >= 0)
                foreach (int b in g.Sites[TargetSite].Bays)
                {
                    var c = Core.Rules.Match.BayCentre(g.Bays[b]);
                    nearestSiteBay = Mathf.Min(nearestSiteBay, (new Vector2(c.X, c.Y) - new Vector2(pos.x, pos.z)).magnitude);
                }
            if (nearestSiteBay < 2.8f && Mathf.Abs(v) < 0.6f) { Arrived = true; _car.Brake = 1f; _car.Throttle = 0f; return; }
            if (nearestSiteBay < 2.8f) { _car.Throttle = 0f; _car.Brake = 1f; _car.ReverseOnBrake = false; return; } // in: stop

            var bc = Core.Rules.Match.BayCentre(g.Bays[TargetBay]);
            var local = transform.InverseTransformPoint(new Vector3(bc.X, pos.y, bc.Y));
            _car.Handbrake = false;
            if (_finalTime > 25f) { _finalTime = 0f; _path.Clear(); ParkAtBay(TargetBay); return; } // give up this attempt: re-plan
            if (local.z > 1.2f && Mathf.Abs(local.x) < local.z * 1.6f + 1f)
            {
                // Ahead: creep towards it.
                float ang = Mathf.Atan2(local.x, local.z) * Mathf.Rad2Deg;
                _car.Steer = Mathf.Clamp(ang / 25f, -1f, 1f);
                _car.ReverseOnBrake = false;
                float want = Mathf.Clamp(goalDist * 0.5f, 1f, 2.5f);
                _car.Throttle = v < want ? 0.35f : 0f;
                _car.Brake = v > want + 0.5f ? 0.4f : 0f;
            }
            else
            {
                // Behind or alongside: reverse, steering the tail towards it (backing away from a bay
                // that's beside us gives the room to come at it again).
                float ang = Mathf.Atan2(local.x, -local.z) * Mathf.Rad2Deg;
                _car.Steer = local.z < -1.2f ? Mathf.Clamp(ang / 25f, -1f, 1f) : -Mathf.Sign(local.x) * 0.8f;
                _car.Throttle = 0f;
                _car.ReverseOnBrake = true;
                _car.Brake = v > 0.3f ? 0.6f : (v > -1.8f ? 0.45f : 0f); // stop first, then back up slowly
            }
        }

        /// <summary>
        /// Close to the target bay (e.g. after a nudge or a slightly wide entry): finish the entry path
        /// from where we are, rather than leaving and going round the block again.
        /// </summary>
        bool FinishParking(LaneGraph g, Vector3 p)
        {
            var bay = g.Bays[TargetBay];
            var bc = Core.Rules.Match.BayCentre(bay);
            if ((new Vector2(bc.X, bc.Y) - new Vector2(p.x, p.z)).sqrMagnitude > 10f * 10f) return false; // only right at the bay
            var pts = bay.EntryPath;
            int nearest = -1; float best = 12f * 12f;
            for (int i = 0; i < pts.Length; i++)
            {
                float d = (new Vector2(pts[i].X, pts[i].Y) - new Vector2(p.x, p.z)).sqrMagnitude;
                if (d < best) { best = d; nearest = i; }
            }
            if (nearest < 0) return false;
            _path.Add(p);
            for (int i = Mathf.Min(nearest + 1, pts.Length - 1); i < pts.Length; i++) _path.Add(new Vector3(pts[i].X, 0f, pts[i].Y));
            if (_path.Count < 2) _path.Add(new Vector3(pts[pts.Length - 1].X, 0f, pts[pts.Length - 1].Y));
            return true;
        }

        /// <summary>The bay we're sitting in (car centre near its centre), or -1.</summary>
        static int ParkedIn(LaneGraph g, Vector3 p)
        {
            for (int b = 0; b < g.Bays.Count; b++)
            {
                var c = Core.Rules.Match.BayCentre(g.Bays[b]);
                if ((new Vector2(c.X, c.Y) - new Vector2(p.x, p.z)).sqrMagnitude < 5f * 5f) return b;
            }
            return -1;
        }

        /// <summary>Closest lane to a point, either direction (for targets parked off the road).</summary>
        static int NearestLane(LaneGraph g, Vector3 p)
        {
            float best = float.MaxValue; int id = 0;
            var q = new Vec2(p.x, p.z);
            foreach (var l in g.Lanes)
            {
                float s = Mathf.Clamp(Vec2.Dot(q - l.Start, l.Direction), 0f, l.Length);
                float d = Vec2.Distance(l.PointAt(s), q);
                if (d < best) { best = d; id = l.Id; }
            }
            return id;
        }
        float _stuckTimer, _reverseTimer, _stuckSteer;

        /// <summary>Largest heading change (deg) over any 12 m window ahead, and the distance to it.</summary>
        float UpcomingTurn(float horizon, out float distance)
        {
            float best = 0f, acc = 0f;
            distance = horizon;
            for (int i = _cursor; i < _path.Count - 1 && acc < horizon; i++)
            {
                float seg = Vector3.Distance(_path[i], _path[i + 1]);
                float window = 0f;
                int j = i + 1;
                while (j < _path.Count - 1 && window < 12f) { window += Vector3.Distance(_path[j], _path[j + 1]); j++; }
                var a = Flat(_path[i + 1] - _path[i]);
                var b = Flat(_path[j] - _path[j - 1]);
                if (a.sqrMagnitude > 1e-4f && b.sqrMagnitude > 1e-4f)
                {
                    float ang = Vector3.Angle(a, b);
                    if (ang > best) { best = ang; distance = acc; }
                }
                acc += seg;
            }
            return best;
        }

        float _segT;
        int _stoppedAt = -1;
        float _stopWait;

        Vector3 PointAhead(float distance, out float curvatureDeg)
        {
            // Walk `distance` metres along the path from the car's projection onto it.
            int i = _cursor;
            var p = Vector3.Lerp(_path[i], _path[Mathf.Min(i + 1, _path.Count - 1)], _segT);
            float left = distance;
            while (i < _path.Count - 1)
            {
                float seg = Vector3.Distance(p, _path[i + 1]);
                if (seg >= left) { p = Vector3.MoveTowards(p, _path[i + 1], left); break; }
                left -= seg;
                p = _path[i + 1];
                i++;
            }
            // Curvature proxy: heading change over the next ~25 m.
            int j = Mathf.Min(_path.Count - 1, i + 6);
            var d1 = Flat(_path[Mathf.Min(i, _path.Count - 1)] - _path[_cursor]);
            var d2 = Flat(_path[j] - _path[Mathf.Min(i, _path.Count - 1)]);
            curvatureDeg = d1.sqrMagnitude > 0.01f && d2.sqrMagnitude > 0.01f ? Vector3.Angle(d1, d2) : 0f;
            return p;
        }

        /// <summary>Distance to a red/amber stop line or stop sign ahead on the path, if any.</summary>
        float StopDistance(LaneGraph g, TrafficRunner runner)
        {
            if (runner.Sim == null) return float.MaxValue;
            foreach (var (lane, idx) in _laneEnds)
            {
                if (idx < _cursor) continue;
                float d = 0f;
                for (int k = _cursor; k < idx && k < _path.Count - 1; k++) d += Vector3.Distance(_path[k], _path[k + 1]);
                if (d > 60f) break;
                var l = g.Lanes[lane];
                if (l.Control == ApproachControl.Signal && runner.Sim.Signal(l.ToNode, l.EdgeId) != SignalState.Green && d > 4f) return d;
                if (l.Control == ApproachControl.Stop && _stoppedAt != lane)
                {
                    // Stop sign: come to a full stop at the line, pause, then go (like the NPCs).
                    if (d < 6f && Mathf.Abs(_car.ForwardSpeed) < 0.3f)
                    {
                        if ((_stopWait += Time.fixedDeltaTime) > 1f) { _stoppedAt = lane; _stopWait = 0f; return float.MaxValue; }
                    }
                    return d + 2.5f; // stop line, not the junction centre
                }
                return float.MaxValue;
            }
            return float.MaxValue;
        }

        void Plan(LaneGraph g)
        {
            _replanTimer = 3f;
            _replans++;
            _bestGoalDist = float.MaxValue;
            _noProgress = 0f;
            _path.Clear();
            _laneEnds.Clear();
            _cursor = 0;
            var runner = TrafficRunner.Instance;
            var sim = runner.Sim;
            var p = transform.position;
            var f = transform.forward;
            var idx = sim != null ? sim.Index : new LaneIndex(g);
            if (TargetBay >= 0 && FinishParking(g, p)) return;
            bool onRoad = idx.Project(new Vec2(p.x, p.z), new Vec2(f.x, f.z), out var proj);
            if (onRoad && proj.OnConnector)
            {
                // Mid-junction: finish the turn we're in, then carry on from its exit lane.
                var con = g.Connectors[proj.Id];
                float acc = 0f;
                for (int i = 0; i + 1 < con.Points.Length; i++)
                {
                    acc += Vec2.Distance(con.Points[i], con.Points[i + 1]);
                    if (acc > proj.S) _path.Add(new Vector3(con.Points[i + 1].X, 0f, con.Points[i + 1].Y));
                }
                proj = new LaneIndex.Projection { Id = con.ToLane, S = 0f };
            }
            else if (!onRoad && ParkedIn(g, p) is int parked && parked >= 0)
            {
                // Leaving a parking bay: take its exit path back onto the access lane, like the NPCs.
                var bay = g.Bays[parked];
                foreach (var bp in bay.ExitPath) _path.Add(new Vector3(bp.X, 0f, bp.Y));
                proj = new LaneIndex.Projection { Id = bay.AccessLane, S = Mathf.Min(bay.ExitS + 2f, g.Lanes[bay.AccessLane].Length) };
            }
            else if (!onRoad)
            {
                // Off-road (forecourt, kerb): rejoin at the nearest point of a lane heading our way.
                float best = float.MaxValue; int bestLane = -1; float bestS = 0f;
                foreach (var l in g.Lanes)
                {
                    if (Vector3.Dot(new Vector3(l.Direction.X, 0, l.Direction.Y), f) < 0.2f) continue;
                    float s0 = Mathf.Clamp(Vec2.Dot(new Vec2(p.x, p.z) - l.Start, l.Direction) + 6f, 0f, l.Length);
                    var q = l.PointAt(s0);
                    float d = (new Vector3(q.X, 0, q.Y) - p).sqrMagnitude;
                    if (d < best) { best = d; bestLane = l.Id; bestS = s0; }
                }
                if (bestLane < 0) return;
                proj = new LaneIndex.Projection { Id = bestLane, S = bestS };
                _path.Add(p);
            }
            var lane = g.Lanes[proj.Id];

            // Destination: a bay's access lane, the follow target, else random.
            int goalLane;
            if (TargetBay >= 0) goalLane = g.Bays[TargetBay].AccessLane;
            else if (FollowTarget != null && idx.Project(new Vec2(FollowTarget.position.x, FollowTarget.position.z),
                    new Vec2(FollowTarget.forward.x, FollowTarget.forward.z), out var tp) && !tp.OnConnector)
                goalLane = tp.Id;
            else if (FollowTarget != null)
                goalLane = NearestLane(g, FollowTarget.position); // target parked off the road: go to the street it's on
            else goalLane = _rng.NextInt(g.Lanes.Count);

            var router = sim != null ? sim.Router : new Router(g);
            var goal = g.Lanes[goalLane];
            var route = router.Route(router.Directed(lane.EdgeId, lane.ToNode), router.Directed(goal.EdgeId, goal.ToNode), false, 0.2f, ref _rng) ?? new List<int>();

            float s = proj.S;
            if (TargetBay >= 0 && route.Count == 0 && lane.EdgeId == goal.EdgeId && lane.ToNode == goal.ToNode && s > g.Bays[TargetBay].EntryS - 3f)
                route = router.Route(router.Directed(lane.EdgeId, lane.ToNode), router.Directed(goal.EdgeId, goal.ToNode), true, 0.2f, ref _rng) ?? route;
            for (int hop = 0; hop <= route.Count && hop < 80; hop++)
            {
                bool final = TargetBay >= 0 && hop == route.Count && (lane.Id == goal.Id || (lane.EdgeId == goal.EdgeId && lane.ToNode == goal.ToNode));
                if (final)
                {
                    // Run along the kerb lane to the bay entry, then follow the bay's S-curve in.
                    var bay = g.Bays[TargetBay];
                    var access = g.Lanes[bay.AccessLane];
                    for (float t = s; t < bay.EntryS; t += 3f) _path.Add(new Vector3(access.PointAt(t).X, 0f, access.PointAt(t).Y));
                    // The entry path ends where the front bumper stops; we steer by the car's centre.
                    foreach (var bp in bay.EntryPath) _path.Add(new Vector3(bp.X, 0f, bp.Y));
                    break;
                }
                for (float t = s; t < lane.Length; t += 3f) _path.Add(new Vector3(lane.PointAt(t).X, 0f, lane.PointAt(t).Y));
                _path.Add(new Vector3(lane.End.X, 0f, lane.End.Y));
                _laneEnds.Add((lane.Id, _path.Count - 1));
                if (hop >= route.Count) break;
                int con = g.ConnectorTo(lane.Id, route[hop]);
                if (con < 0)
                {
                    // Wrong lane for the turn: hop to a sibling lane that has it.
                    foreach (int sib in g.EdgeLanes[lane.EdgeId])
                        if (g.Lanes[sib].ToNode == lane.ToNode && (con = g.ConnectorTo(sib, route[hop])) >= 0) break;
                    if (con < 0) break;
                }
                foreach (var cp in g.Connectors[con].Points) _path.Add(new Vector3(cp.X, 0f, cp.Y));
                lane = g.Lanes[g.Connectors[con].ToLane];
                s = 0f;
            }
        }
    }
}
