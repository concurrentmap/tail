using System;
using System.Collections.Generic;
using Tailed.Core.Util;

namespace Tailed.Core.Roads
{
    /// <summary>
    /// Uniform grid over lanes and connectors. Projects arbitrary positions (player cars, which
    /// are physics-driven) onto the lane graph so NPCs can follow, yield to and honk at them.
    /// </summary>
    public sealed class LaneIndex
    {
        const float CellSize = 20f;
        readonly LaneGraph _g;
        readonly Dictionary<long, List<int>> _lanes = new Dictionary<long, List<int>>();
        readonly Dictionary<long, List<int>> _connectors = new Dictionary<long, List<int>>();

        public LaneIndex(LaneGraph g)
        {
            _g = g;
            foreach (var l in g.Lanes)
                for (float s = 0; ; s += CellSize * 0.5f)
                {
                    Add(_lanes, l.PointAt(Math.Min(s, l.Length)), l.Id);
                    if (s >= l.Length) break;
                }
            foreach (var c in g.Connectors)
                if (c.Turn != TurnType.UTurn) // nobody drifts onto a U-turn by accident
                foreach (var p in c.Points) Add(_connectors, p, c.Id);
        }

        static long Key(int x, int y) => ((long)x << 32) ^ (uint)y;

        static void Add(Dictionary<long, List<int>> map, Vec2 p, int id)
        {
            int cx = (int)MathF.Floor(p.X / CellSize), cy = (int)MathF.Floor(p.Y / CellSize);
            for (int dx = -1; dx <= 1; dx++)
            for (int dy = -1; dy <= 1; dy++)
            {
                long k = Key(cx + dx, cy + dy);
                if (!map.TryGetValue(k, out var list)) map[k] = list = new List<int>();
                if (list.Count == 0 || list[list.Count - 1] != id) list.Add(id);
            }
        }

        public struct Projection
        {
            public bool OnConnector;
            public int Id;
            /// <summary>Arc length of the projected point.</summary>
            public float S;
            /// <summary>Signed lateral distance (positive = left of travel).</summary>
            public float Lateral;
        }

        /// <summary>
        /// Nearest lane or connector whose direction agrees with <paramref name="heading"/> (within
        /// ~65°) and which is within its own width. False if the point is off-road.
        /// </summary>
        public bool Project(Vec2 p, Vec2 heading, out Projection result)
        {
            result = default;
            float best = float.MaxValue;
            long key = Key((int)MathF.Floor(p.X / CellSize), (int)MathF.Floor(p.Y / CellSize));

            if (_lanes.TryGetValue(key, out var lanes))
                foreach (int id in lanes)
                {
                    var l = _g.Lanes[id];
                    if (Vec2.Dot(heading, l.Direction) < 0.4f) continue;
                    float s = Vec2.Dot(p - l.Start, l.Direction);
                    if (s < -1f || s > l.Length + 1f) continue;
                    float lat = Vec2.Cross(l.Direction, p - l.Start);
                    float tol = l.Width * 0.55f;
                    if (MathF.Abs(lat) > tol || MathF.Abs(lat) >= best) continue;
                    best = MathF.Abs(lat);
                    result = new Projection { OnConnector = false, Id = id, S = Math.Clamp(s, 0f, l.Length), Lateral = lat };
                }

            if (_connectors.TryGetValue(key, out var cons))
                foreach (int id in cons)
                {
                    var c = _g.Connectors[id];
                    float acc = 0f;
                    for (int i = 0; i + 1 < c.Points.Length; i++)
                    {
                        Vec2 a = c.Points[i], b = c.Points[i + 1];
                        float seg = Vec2.Distance(a, b);
                        Vec2 d = (b - a) / Math.Max(seg, 1e-4f);
                        float t = Vec2.Dot(p - a, d);
                        if (t >= -0.5f && t <= seg + 0.5f && Vec2.Dot(heading, d) >= 0.4f)
                        {
                            float lat = Vec2.Cross(d, p - a);
                            if (MathF.Abs(lat) < 1.8f && MathF.Abs(lat) < best)
                            {
                                best = MathF.Abs(lat);
                                result = new Projection { OnConnector = true, Id = id, S = acc + Math.Clamp(t, 0f, seg), Lateral = lat };
                            }
                        }
                        acc += seg;
                    }
                }

            return best < float.MaxValue;
        }
    }
}
