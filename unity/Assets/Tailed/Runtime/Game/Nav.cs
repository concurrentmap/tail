using System.Collections.Generic;
using Tailed.Core.Roads;
using Tailed.Core.Util;
using Tailed.Traffic;
using UnityEngine;

namespace Tailed.Game
{
    /// <summary>
    /// Sat-nav for the local player: lane-accurate route from the car to a parking bay, as map points,
    /// plus the next manoeuvre ("Turn left onto Maple Rd in 120 m"). Uses the same router as the NPCs.
    /// </summary>
    public static class Nav
    {
        public struct Result
        {
            public List<Vector2> Points;
            public string Instruction;
            public float Distance;
        }

        public static bool ToBay(Vector3 carPos, Vector3 carForward, int bayId, out Result result)
        {
            result = default;
            var runner = TrafficRunner.Instance;
            if (runner?.Sim == null || runner.Town == null) return false;
            var g = runner.Town.Lanes;
            var sim = runner.Sim;
            var bay = g.Bays[bayId];
            var goal = g.Lanes[bay.AccessLane];
            var pts = new List<Vector2> { new Vector2(carPos.x, carPos.z) };

            // Where are we? A lane (or the lane a junction leads onto); off-road → nearest lane ahead.
            Lane lane;
            float s;
            if (sim.Index.Project(new Vec2(carPos.x, carPos.z), new Vec2(carForward.x, carForward.z), out var p))
            {
                if (p.OnConnector) { var con = g.Connectors[p.Id]; lane = g.Lanes[con.ToLane]; s = 0f; }
                else { lane = g.Lanes[p.Id]; s = p.S; }
            }
            else
            {
                lane = null; s = 0f; float best = 60f * 60f;
                foreach (var l in g.Lanes)
                {
                    float t = Mathf.Clamp(Vec2.Dot(new Vec2(carPos.x, carPos.z) - l.Start, l.Direction), 0f, l.Length);
                    var q = l.PointAt(t);
                    float d = (new Vector2(q.X, q.Y) - new Vector2(carPos.x, carPos.z)).sqrMagnitude;
                    if (d < best) { best = d; lane = l; s = t; }
                }
                if (lane == null) return false;
            }

            int start = sim.Router.Directed(lane.EdgeId, lane.ToNode), target = sim.Router.Directed(goal.EdgeId, goal.ToNode);
            var rng = new Rng(1);
            bool mustLeave = start == target && s > bay.EntryS - 3f;
            var route = sim.Router.Route(start, target, mustLeave, 0f, ref rng);
            if (route == null) return false;

            float dist = 0f, firstTurnAt = -1f;
            string firstTurn = null;
            Vector2 last = pts[0];
            void Add(Vec2 v) { var w = new Vector2(v.X, v.Y); dist += Vector2.Distance(last, w); last = w; pts.Add(w); }

            var net = g.Network;
            for (int hop = 0; hop <= route.Count; hop++)
            {
                bool final = hop == route.Count;
                float end = final ? bay.EntryS : lane.Length;
                for (float t = s; t < end; t += 8f) Add(lane.PointAt(t));
                Add(lane.PointAt(end));
                if (final) { foreach (var q in bay.EntryPath) Add(q); break; }
                int conId = g.ConnectorTo(lane.Id, route[hop]);
                if (conId < 0)
                    foreach (int sib in g.EdgeLanes[lane.EdgeId])
                        if (g.Lanes[sib].ToNode == lane.ToNode && (conId = g.ConnectorTo(sib, route[hop])) >= 0) break;
                if (conId < 0) break;
                var c = g.Connectors[conId];
                if (firstTurn == null && c.Turn != TurnType.Straight)
                {
                    firstTurnAt = dist;
                    firstTurn = $"Turn {(c.Turn == TurnType.Left ? "left" : "right")} onto {net.Edges[route[hop]].StreetName}";
                }
                foreach (var q in c.Points) Add(q);
                lane = g.Lanes[c.ToLane];
                s = 0f;
            }
            string here = net.Edges[lane.EdgeId].StreetName;
            result = new Result
            {
                Points = pts,
                Distance = dist,
                Instruction = firstTurn != null ? $"{firstTurn} in {Round(firstTurnAt)} m" : $"Destination ahead on the right, {Round(dist)} m",
            };
            return true;
        }

        static int Round(float m) => m < 100f ? Mathf.RoundToInt(m / 10f) * 10 : Mathf.RoundToInt(m / 50f) * 50;

        /// <summary>Name of the street the car is on (or the one it's turning onto).</summary>
        public static string StreetAt(Vector3 pos, Vector3 forward)
        {
            var runner = TrafficRunner.Instance;
            if (runner?.Sim == null) return null;
            var g = runner.Town.Lanes;
            if (!runner.Sim.Index.Project(new Vec2(pos.x, pos.z), new Vec2(forward.x, forward.z), out var p)) return null;
            int edge = p.OnConnector ? g.Lanes[g.Connectors[p.Id].ToLane].EdgeId : g.Lanes[p.Id].EdgeId;
            return g.Network.Edges[edge].StreetName;
        }
    }
}
