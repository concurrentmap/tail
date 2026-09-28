using System;
using System.Collections.Generic;
using Tailed.Core.Roads;
using Tailed.Core.Util;

namespace Tailed.Core.Traffic
{
    /// <summary>
    /// Dijkstra over directed edges (edge + travel direction), never U-turning at a junction.
    /// Costs are travel time plus junction penalties, jittered per request so drivers vary routes.
    /// </summary>
    public sealed class Router
    {
        readonly RoadNetwork _net;
        readonly LaneGraph _g;
        readonly float[] _dist;
        readonly int[] _prev;
        readonly MinHeap _heap = new MinHeap();

        public Router(LaneGraph g)
        {
            _g = g;
            _net = g.Network;
            _dist = new float[_net.Edges.Count * 2];
            _prev = new int[_net.Edges.Count * 2];
        }

        /// <summary>Directed edge index: travelling along <paramref name="edge"/> towards <paramref name="toNode"/>.</summary>
        public int Directed(int edge, int toNode) => edge * 2 + (toNode == _net.Edges[edge].B ? 0 : 1);
        public int EdgeOf(int directed) => directed >> 1;
        public int ToNodeOf(int directed) { var e = _net.Edges[directed >> 1]; return (directed & 1) == 0 ? e.B : e.A; }

        /// <summary>
        /// Edges to drive after the current one to arrive on <paramref name="targetDirected"/>.
        /// Empty if already on it and <paramref name="mustLeave"/> is false. Null if unreachable.
        /// </summary>
        public List<int> Route(int startDirected, int targetDirected, bool mustLeave, float jitter, ref Rng rng)
        {
            if (startDirected == targetDirected && !mustLeave) return new List<int>();
            for (int i = 0; i < _dist.Length; i++) { _dist[i] = float.MaxValue; _prev[i] = -1; }
            _heap.Clear();
            ulong salt = rng.NextULong();
            // Seed with the successors of the start edge (the route lists edges *after* it), so a
            // target equal to the start is reached by going round the block.
            Expand(startDirected, 0f, jitter, salt, StartMark);

            while (_heap.Count > 0)
            {
                var (cur, d) = _heap.Pop();
                if (d > _dist[cur]) continue;
                if (cur == targetDirected) break;
                Expand(cur, d, jitter, salt, cur);
            }
            if (_dist[targetDirected] == float.MaxValue) return null;

            var path = new List<int>();
            for (int cur = targetDirected; ; cur = _prev[cur])
            {
                path.Add(EdgeOf(cur));
                if (_prev[cur] == StartMark) break;
            }
            path.Reverse();
            return path;
        }

        const int StartMark = -2;

        void Expand(int cur, float d, float jitter, ulong salt, int prevMark)
        {
            int node = ToNodeOf(cur), curEdge = EdgeOf(cur);
            var j = _g.Junctions[node];
            foreach (int e in _net.Nodes[node].Edges)
            {
                if (e == curEdge) continue;
                int next = Directed(e, _net.Edges[e].Other(node));
                var edge = _net.Edges[e];
                float cost = _net.Length(edge) / RoadSpec.SpeedLimit(edge.Class) + Penalty(j, curEdge, e);
                cost *= 1f + jitter * Hash01(salt, next);
                float nd = d + cost;
                if (nd < _dist[next]) { _dist[next] = nd; _prev[next] = prevMark; _heap.Push(next, nd); }
            }
        }

        float Penalty(Junction j, int fromEdge, int toEdge)
        {
            float p;
            switch (j.Control)
            {
                case JunctionControl.Signal: p = 12f; break;
                case JunctionControl.AllWayStop: p = 6f; break;
                case JunctionControl.Priority: p = (fromEdge == j.MainEdgeA || fromEdge == j.MainEdgeB) ? 1f : 6f; break;
                default: p = 0f; break;
            }
            var node = _net.Nodes[j.NodeId];
            var inDir = -_net.Dir(_net.Edges[fromEdge], node.Id);
            var outDir = _net.Dir(_net.Edges[toEdge], node.Id);
            if (Vec2.Cross(inDir, outDir) > 0.5f) p += 5f; // left turn
            return p;
        }

        static float Hash01(ulong salt, int x)
        {
            ulong z = salt ^ ((ulong)x * 0x9E3779B97F4A7C15UL);
            z = (z ^ (z >> 30)) * 0xBF58476D1CE4E5B9UL;
            z ^= z >> 27;
            return (z >> 40) / (float)(1UL << 24);
        }

        sealed class MinHeap
        {
            readonly List<(int item, float key)> _a = new List<(int, float)>();
            public int Count => _a.Count;
            public void Clear() => _a.Clear();

            public void Push(int item, float key)
            {
                _a.Add((item, key));
                int i = _a.Count - 1;
                while (i > 0)
                {
                    int p = (i - 1) / 2;
                    if (_a[p].key <= _a[i].key) break;
                    (_a[p], _a[i]) = (_a[i], _a[p]);
                    i = p;
                }
            }

            public (int, float) Pop()
            {
                var top = _a[0];
                _a[0] = _a[_a.Count - 1];
                _a.RemoveAt(_a.Count - 1);
                int i = 0;
                while (true)
                {
                    int l = 2 * i + 1, r = l + 1, m = i;
                    if (l < _a.Count && _a[l].key < _a[m].key) m = l;
                    if (r < _a.Count && _a[r].key < _a[m].key) m = r;
                    if (m == i) break;
                    (_a[m], _a[i]) = (_a[i], _a[m]);
                    i = m;
                }
                return top;
            }
        }
    }
}
