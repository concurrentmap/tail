using Tailed.Core.Traffic;
using Tailed.Core.Util;
using Tailed.Map;
using Tailed.Traffic;
using UnityEngine;

namespace Tailed.Vehicles
{
    /// <summary>
    /// Optional driver aids that make driving like traffic a learnable skill (GD §9 "motion tells").
    /// Lane-keep (L) nudges the wheel towards the lane centre when you aren't steering; adaptive
    /// cruise (K) holds a set speed, keeps a sensible gap to whatever is ahead, eases off for
    /// junctions and stops at red lights and stop signs (press W to go). Both give way to the driver
    /// instantly: steering overrides lane-keep, an indicator pauses it for the lane change, braking
    /// cancels cruise, and throttle overrides it (letting go resumes the set speed, or a higher one).
    /// Runs after <see cref="CarInput"/> and only adjusts what it wrote.
    /// </summary>
    [DefaultExecutionOrder(10)]
    public sealed class DriveAssist : MonoBehaviour
    {
        public static bool LaneKeep, Cruise;
        public static float CruiseSpeed;
        /// <summary>What the assist is doing right now, for the HUD ("" when off).</summary>
        public static string Status = "";

        PlayerCar _car;
        float _leaderGap = -1f, _leaderSpeed, _prevGap;
        bool _overriding;

        void Awake() => _car = GetComponent<PlayerCar>();

        public static void ToggleCruise(PlayerCar car)
        {
            Cruise = !Cruise;
            if (Cruise) CruiseSpeed = Mathf.Max(car != null ? car.ForwardSpeed : 0f, 8f);
        }

        void Update()
        {
            var input = GetComponent<CarInput>();
            if (input == null || !input.enabled) { Status = ""; return; }
            float speed = _car.ForwardSpeed;
            var parts = new System.Text.StringBuilder();

            if (Cruise)
            {
                if (_car.Brake > 0.1f || _car.Handbrake) Cruise = false; // brake pedal cancels, like the real thing
                else if (_car.Throttle > 0.1f) _overriding = true;       // driver is accelerating past the set speed
                else
                {
                    if (_overriding) { CruiseSpeed = Mathf.Max(CruiseSpeed, speed); _overriding = false; }
                    Follow(speed);
                }
                if (Cruise) parts.Append($"CRUISE {CruiseSpeed * 3.6f:0} km/h");
            }
            if (LaneKeep)
            {
                bool steering = Mathf.Abs(_car.Steer) > 0.15f;
                bool signalling = _car.IndicateLeft || _car.IndicateRight;
                bool active = !steering && !signalling && speed > 4f && KeepLane(speed);
                if (parts.Length > 0) parts.Append("  ");
                parts.Append(active ? "LANE ●" : "LANE ○");
            }
            Status = parts.ToString();
        }

        /// <summary>IDM towards the set speed against whatever is ahead (vehicle or player car).</summary>
        void Follow(float speed)
        {
            float dt = Mathf.Max(Time.deltaTime, 1e-3f);
            var t = transform;
            float front = _car.Shape.Length * 0.5f;
            var origin = t.position + t.up * 0.8f + t.forward * (front + 0.2f);
            int mask = 1 << Layers.Vehicles | 1 << Layers.PlayerCar | 1 << Layers.Buildings;
            float gap = -1f;
            if (Physics.BoxCast(origin, new Vector3(_car.Shape.Width * 0.5f, 0.4f, 0.1f), t.forward, out var hit, t.rotation, 70f, mask, QueryTriggerInteraction.Ignore) &&
                hit.collider.attachedRigidbody != _car.GetComponent<Rigidbody>())
                gap = hit.distance;
            if (gap >= 0f)
            {
                // Leader speed from how the gap changes (smoothed; the ray can hop between cars).
                float closing = _leaderGap >= 0f ? (_prevGap - gap) / dt : 0f;
                _leaderSpeed = Mathf.Lerp(_leaderSpeed, Mathf.Max(0f, speed - closing), 1f - Mathf.Exp(-dt * 4f));
                _prevGap = gap;
            }
            _leaderGap = gap;

            var p = new IdmParams { DesiredSpeed = CruiseSpeed, TimeHeadway = 1.6f, MinGap = 3f, MaxAccel = 1.6f, ComfortDecel = 2.2f, Delta = 4f };
            float a = gap >= 0f ? Idm.Accel(p, speed, gap, _leaderSpeed) : Idm.FreeRoadAccel(p, speed);
            a = Mathf.Min(a, JunctionAccel(p, speed));
            // Rough pedal model: ~4.5 m/s² at full throttle (less near top speed), ~10 m/s² at full brake.
            if (a >= -0.2f) { _car.Throttle = Mathf.Clamp01(a / 4.5f + 0.06f); _car.Brake = 0f; }
            else { _car.Throttle = 0f; _car.Brake = Mathf.Clamp01(-a / 10f); }
        }

        /// <summary>
        /// Junction awareness: a stationary virtual obstacle at the stop line for red (or a stoppable
        /// amber) and stop signs, and a slower target speed on the final approach. Holding W takes you
        /// through. Off-lane (car parks, inside the junction) it does nothing.
        /// </summary>
        float JunctionAccel(IdmParams p, float speed)
        {
            var sim = TrafficRunner.Instance != null ? TrafficRunner.Instance.Sim : null;
            if (sim == null) return float.MaxValue;
            var pos = transform.position;
            var fwd = transform.forward;
            if (!sim.Index.Project(new Vec2(pos.x, pos.z), new Vec2(fwd.x, fwd.z), out var proj) || proj.OnConnector) return float.MaxValue;
            var lane = sim.Graph.Lanes[proj.Id];
            float remaining = lane.Length - proj.S - _car.Shape.Length * 0.5f;
            if (remaining < -0.5f || remaining > 80f) return float.MaxValue;
            bool stop = lane.Control == Core.Roads.ApproachControl.Stop;
            if (lane.Control == Core.Roads.ApproachControl.Signal)
            {
                var st = sim.Signal(lane.ToNode, lane.EdgeId);
                stop = st == SignalState.Red || (st == SignalState.Amber && remaining > speed * speed / (2f * 3f));
            }
            if (stop) return Idm.Accel(p, speed, Mathf.Max(remaining + p.MinGap - 1f, 0.01f), 0f); // jam gap ends ~1 m short of the line
            if (sim.Graph.Network.Nodes[lane.ToNode].Degree >= 3 && remaining < 35f)
            {
                var slow = p;
                slow.DesiredSpeed = Mathf.Min(p.DesiredSpeed, 7f);
                return Idm.FreeRoadAccel(slow, speed);
            }
            return float.MaxValue;
        }

        /// <summary>Pure pursuit towards a point down the lane centre. False when not on a lane.</summary>
        bool KeepLane(float speed)
        {
            var sim = TrafficRunner.Instance != null ? TrafficRunner.Instance.Sim : null;
            if (sim == null) return false;
            var pos = transform.position;
            var fwd = transform.forward;
            if (!sim.Index.Project(new Vec2(pos.x, pos.z), new Vec2(fwd.x, fwd.z), out var proj)) return false;
            var g = sim.Graph;
            float look = Mathf.Max(7f, speed * 1.1f);
            Vec2 target;
            if (proj.OnConnector)
            {
                // Only plain bends (one way on): at a junction the turn is the driver's call.
                var con = g.Connectors[proj.Id];
                if (g.Network.Nodes[con.NodeId].Degree > 2) return false;
                if (!Ahead(g, con, proj.S + look, out target)) return false;
            }
            else
            {
                var lane = g.Lanes[proj.Id];
                if (Vector2.Angle(new Vector2(fwd.x, fwd.z), new Vector2(lane.Direction.X, lane.Direction.Y)) > 25f) return false;
                if (proj.S + look <= lane.Length) target = lane.PointAt(proj.S + look);
                else
                {
                    int next = BendOut(g, lane);
                    if (next < 0) return false; // hand back before a real junction
                    if (!Ahead(g, g.Connectors[next], proj.S + look - lane.Length, out target)) return false;
                }
            }
            var to = new Vector3(target.X - pos.x, 0f, target.Y - pos.z);
            float angle = Vector3.SignedAngle(fwd, to, Vector3.up);
            float curvature = 2f * Mathf.Sin(angle * Mathf.Deg2Rad) / to.magnitude;
            float steerDeg = Mathf.Atan(curvature * _car.Shape.WheelBase) * Mathf.Rad2Deg;
            // Same speed-sensitive steering as PlayerCar, inverted; limited so it feels like a nudge.
            float max = 34f * Mathf.Lerp(1f, 0.35f, Mathf.Clamp01(speed / 28f));
            _car.Steer = Mathf.Clamp(steerDeg / max, -0.6f, 0.6f);
            return true;
        }

        /// <summary>The single way on from a lane ending at a bend, or -1 at a junction.</summary>
        static int BendOut(Core.Roads.LaneGraph g, Core.Roads.Lane lane)
        {
            if (g.Network.Nodes[lane.ToNode].Degree > 2) return -1;
            foreach (int c in lane.Outgoing) if (g.Connectors[c].Turn != Core.Roads.TurnType.UTurn) return c;
            return -1;
        }

        /// <summary>Point <paramref name="s"/> metres along a bend connector, running on into the lane after it.</summary>
        static bool Ahead(Core.Roads.LaneGraph g, Core.Roads.Connector con, float s, out Vec2 point)
        {
            if (s <= con.Length) { point = Core.Roads.LaneGraph.PathPoint(con.Points, s, out _); return true; }
            var lane = g.Lanes[con.ToLane];
            point = lane.PointAt(Mathf.Min(s - con.Length, lane.Length));
            return true;
        }
    }
}
