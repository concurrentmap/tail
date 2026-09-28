using System;
using System.Collections.Generic;
using Tailed.Core.Util;

namespace Tailed.Core.Roads
{
    public enum TurnType : byte { Straight, Right, Left, UTurn }

    public enum JunctionControl : byte
    {
        /// <summary>Degree-2 bend: no junction logic.</summary>
        None,
        /// <summary>Main road flows; side approaches stop.</summary>
        Priority,
        AllWayStop,
        Signal,
    }

    public enum ApproachControl : byte { Free, Stop, Signal }

    /// <summary>A straight lane between the setbacks of its edge's two junctions. Right-hand traffic.</summary>
    public sealed class Lane
    {
        public int Id, EdgeId, FromNode, ToNode;
        /// <summary>0 = kerbside (rightmost), LanesInDirection-1 = next to the centreline.</summary>
        public int Index, LanesInDirection;
        public RoadClass Class;
        public Vec2 Start, End, Direction;
        public float Length, Width, SpeedLimit;
        public ApproachControl Control;
        /// <summary>Lane one step towards the kerb / centreline in the same direction, or -1.</summary>
        public int KerbNeighbor = -1, CentreNeighbor = -1;
        public readonly List<int> Outgoing = new List<int>();
        public readonly List<int> Incoming = new List<int>();

        public Vec2 PointAt(float s) => Start + Direction * s;
    }

    /// <summary>Path through a junction from the end of one lane to the start of another.</summary>
    public sealed class Connector
    {
        public int Id, NodeId, FromLane, ToLane;
        public TurnType Turn;
        public Vec2[] Points;
        public float Length;
        /// <summary>Connectors at the same junction whose paths cross or merge with this one.</summary>
        public readonly List<int> Conflicts = new List<int>();
    }

    public sealed class SignalPhase
    {
        /// <summary>Edges whose incoming lanes have green in this phase.</summary>
        public int[] GreenEdges;
        public float Green, Amber = 3f, AllRed = 1.5f;
        public float Duration => Green + Amber + AllRed;
    }

    public sealed class Junction
    {
        public int NodeId;
        public JunctionControl Control;
        /// <summary>Distance from node centre to where lanes start/end, per incident edge id.</summary>
        public readonly Dictionary<int, float> Setback = new Dictionary<int, float>();
        public readonly List<int> IncomingLanes = new List<int>();
        public readonly List<int> Connectors = new List<int>();
        /// <summary>Signal phases (Signal control only).</summary>
        public readonly List<SignalPhase> Phases = new List<SignalPhase>();
        /// <summary>For Priority control: the two edges forming the main road.</summary>
        public int MainEdgeA = -1, MainEdgeB = -1;
    }

    /// <summary>
    /// A parking bay on a POI forecourt, parallel to the street. Vehicles leave the kerbside
    /// access lane on an S-curve, stop in the bay, and rejoin further along (drive-through).
    /// </summary>
    public sealed class ParkingBay
    {
        public int Id, PoiId, AccessLane;
        public Vec2 Position, Heading;
        /// <summary>Arc length on the access lane where the entry path leaves / the exit path rejoins.</summary>
        public float EntryS, ExitS;
        public Vec2[] EntryPath, ExitPath;
        public float EntryLength, ExitLength;
    }

    /// <summary>
    /// A kerbside stop on a lane: bus shelters, and where delivery vans double-park.
    /// Vehicles stop in the lane itself (nudged to the kerb), not in a bay.
    /// </summary>
    public sealed class KerbStop
    {
        public int Id, Lane;
        public float S;
        public bool BusStop;
    }

    public sealed class PoiSite
    {
        public int PoiId, AccessLane;
        public readonly List<int> Bays = new List<int>();
        /// <summary>Forecourt centre and the direction into the lot.</summary>
        public Vec2 Centre, Inward;
    }

    /// <summary>Runtime traffic topology derived from a <see cref="RoadNetwork"/>.</summary>
    public sealed class LaneGraph
    {
        public const float CornerClearance = 5f;
        public const int ConnectorSamples = 10;

        public readonly RoadNetwork Network;
        public readonly List<Lane> Lanes = new List<Lane>();
        public readonly List<Connector> Connectors = new List<Connector>();
        public readonly Dictionary<int, Junction> Junctions = new Dictionary<int, Junction>();
        /// <summary>Lane ids per edge, A→B lanes first (kerb to centre), then B→A.</summary>
        public readonly Dictionary<int, List<int>> EdgeLanes = new Dictionary<int, List<int>>();
        public readonly List<ParkingBay> Bays = new List<ParkingBay>();
        /// <summary>Indexed by POI id.</summary>
        public readonly List<PoiSite> Sites = new List<PoiSite>();
        public readonly List<KerbStop> KerbStops = new List<KerbStop>();
        /// <summary>Kerb stops per lane, sorted by S.</summary>
        public readonly Dictionary<int, List<int>> LaneStops = new Dictionary<int, List<int>>();

        public const float ForecourtOffset = 4f; // beyond the sidewalk
        public const float BaySpacing = 9f, BayEntryRun = 20f;

        LaneGraph(RoadNetwork net) => Network = net;

        public static LaneGraph Build(RoadNetwork net)
        {
            var g = new LaneGraph(net);
            foreach (var node in net.Nodes)
                if (node.Degree > 0) g.Junctions[node.Id] = new Junction { NodeId = node.Id };

            foreach (var j in g.Junctions.Values) g.ComputeSetbacks(j);
            foreach (var e in net.Edges) g.BuildLanes(e);
            foreach (var j in g.Junctions.Values)
            {
                g.ClassifyControl(j);
                g.BuildConnectors(j);
                g.ComputeConflicts(j);
            }
            foreach (var poi in net.Pois) g.BuildSite(poi);
            g.BuildKerbStops();
            return g;
        }

        void BuildSite(Poi poi)
        {
            var cell = Network.Cells[poi.CellIndex];
            int edge = cell.Sides[poi.Side];
            // Side k runs Corners[k] → Corners[k+1] counter-clockwise, so the cell is on the right
            // of traffic driving Corners[k+1] → Corners[k].
            int from = cell.Corners[(poi.Side + 1) % 4];
            var lane = Lanes[LanesLeaving(edge, from)[0]];
            Vec2 right = lane.Direction.PerpRight;
            float off = lane.Width * 0.5f + RoadSpec.SidewalkWidth + ForecourtOffset;
            var site = new PoiSite { PoiId = poi.Id, AccessLane = lane.Id, Inward = right };

            float first = BayEntryRun + 4f;
            int count = Math.Min(5, (int)((lane.Length - first - BayEntryRun - 4f) / BaySpacing) + 1);
            float mid = 0f;
            for (int i = 0; i < count; i++)
            {
                float s = first + i * BaySpacing;
                var bay = new ParkingBay
                {
                    Id = Bays.Count, PoiId = poi.Id, AccessLane = lane.Id,
                    Position = lane.PointAt(s) + right * off, Heading = lane.Direction,
                    EntryS = s - BayEntryRun, ExitS = s + BayEntryRun,
                };
                bay.EntryPath = SCurve(lane.PointAt(bay.EntryS), bay.Position, lane.Direction, out bay.EntryLength);
                bay.ExitPath = SCurve(bay.Position, lane.PointAt(bay.ExitS), lane.Direction, out bay.ExitLength);
                Bays.Add(bay);
                site.Bays.Add(bay.Id);
                mid += s;
            }
            site.Centre = lane.PointAt(mid / Math.Max(1, count)) + right * off;
            Sites.Add(site);
        }

        /// <summary>
        /// Kerb stops on long kerb lanes clear of junctions and bay forecourts. Every third one is a
        /// bus stop (gets a shelter); the rest are just places a van can pull over.
        /// </summary>
        void BuildKerbStops()
        {
            var bayRanges = new Dictionary<int, List<(float, float)>>();
            foreach (var b in Bays)
            {
                if (!bayRanges.TryGetValue(b.AccessLane, out var r)) bayRanges[b.AccessLane] = r = new List<(float, float)>();
                r.Add((b.EntryS - 14f, b.ExitS + 14f));
            }
            foreach (var lane in Lanes)
            {
                if (lane.Index != 0 || lane.Length < 70f) continue;
                if (Network.Nodes[lane.ToNode].Degree <= 2 && Network.Nodes[lane.FromNode].Degree <= 2) continue;
                float s = lane.Length * 0.55f;
                bool clear = true;
                if (bayRanges.TryGetValue(lane.Id, out var ranges))
                    foreach (var (a, b) in ranges) if (s + 8f > a && s - 16f < b) { clear = false; break; }
                if (!clear) continue;
                uint h = (uint)(lane.Id * 2654435761u);
                bool bus = lane.Class != RoadClass.Local ? (h >> 7) % 2 == 0 : (h >> 7) % 5 == 0;
                var k = new KerbStop { Id = KerbStops.Count, Lane = lane.Id, S = s, BusStop = bus };
                KerbStops.Add(k);
                LaneStops[lane.Id] = new List<int> { k.Id };
            }
        }

        static Vec2[] SCurve(Vec2 a, Vec2 b, Vec2 dir, out float length)
        {
            float d = Vec2.Dot(b - a, dir) * 0.45f;
            return Bezier(a, a + dir * d, b - dir * d, b, out length);
        }

        static Vec2[] Bezier(Vec2 p0, Vec2 p1, Vec2 p2, Vec2 p3, out float length)
        {
            var pts = new Vec2[ConnectorSamples + 1];
            length = 0f;
            for (int k = 0; k <= ConnectorSamples; k++)
            {
                float t = k / (float)ConnectorSamples, u = 1f - t;
                pts[k] = p0 * (u * u * u) + p1 * (3f * u * u * t) + p2 * (3f * u * t * t) + p3 * (t * t * t);
                if (k > 0) length += Vec2.Distance(pts[k - 1], pts[k]);
            }
            return pts;
        }

        /// <summary>Point and unit tangent at arc length <paramref name="s"/> along a sampled path.</summary>
        public static Vec2 PathPoint(Vec2[] pts, float s, out Vec2 tangent)
        {
            for (int i = 0; i + 1 < pts.Length; i++)
            {
                float seg = Vec2.Distance(pts[i], pts[i + 1]);
                if (s <= seg || i + 2 == pts.Length)
                {
                    tangent = (pts[i + 1] - pts[i]).Normalized;
                    return Vec2.Lerp(pts[i], pts[i + 1], seg > 1e-5f ? Math.Min(1f, s / seg) : 0f);
                }
                s -= seg;
            }
            tangent = new Vec2(1, 0);
            return pts[pts.Length - 1];
        }

        /// <summary>Connector from <paramref name="laneId"/> onto <paramref name="edgeId"/>, or -1.</summary>
        public int ConnectorTo(int laneId, int edgeId)
        {
            foreach (int c in Lanes[laneId].Outgoing)
                if (Connectors[c].Turn != TurnType.UTurn && Lanes[Connectors[c].ToLane].EdgeId == edgeId) return c;
            return -1;
        }

        /// <summary>The U-turn connector from this lane, or -1 (only the centre-most lane has one).</summary>
        public int UTurnFrom(int laneId)
        {
            foreach (int c in Lanes[laneId].Outgoing)
                if (Connectors[c].Turn == TurnType.UTurn) return c;
            return -1;
        }

        /// <summary>A random way out of this lane, never the U-turn (that's a deliberate choice).</summary>
        public int RandomOutgoing(int laneId, ref Rng rng)
        {
            var outs = Lanes[laneId].Outgoing;
            int n = 0;
            foreach (int c in outs) if (Connectors[c].Turn != TurnType.UTurn) n++;
            if (n == 0) return -1;
            int pick = rng.NextInt(n);
            foreach (int c in outs)
                if (Connectors[c].Turn != TurnType.UTurn && pick-- == 0) return c;
            return -1;
        }

        // ---- geometry ----------------------------------------------------------------

        void ComputeSetbacks(Junction j)
        {
            var node = Network.Nodes[j.NodeId];
            float maxHalf = 0f;
            foreach (int e in node.Edges) maxHalf = Math.Max(maxHalf, RoadSpec.HalfWidth(Network.Edges[e].Class));

            float setback;
            if (node.Degree >= 3)
                setback = maxHalf + CornerClearance;
            else
            {
                // Bend: pull lanes back enough that the inside kerbs don't overlap.
                var e0 = Network.Edges[node.Edges[0]];
                var e1 = Network.Edges[node.Edges[1]];
                float turn = MathF.PI - MathF.Abs(Vec2.SignedAngle(Network.Dir(e0, j.NodeId), Network.Dir(e1, j.NodeId)));
                setback = 2f + maxHalf * MathF.Tan(Math.Min(turn, 1.4f) * 0.5f);
            }
            foreach (int e in node.Edges) j.Setback[e] = setback;
        }

        void BuildLanes(RoadEdge e)
        {
            var ids = new List<int>();
            EdgeLanes[e.Id] = ids;
            AddDirection(e, e.A, e.B, ids);
            AddDirection(e, e.B, e.A, ids);
        }

        void AddDirection(RoadEdge e, int from, int to, List<int> ids)
        {
            Vec2 a = Network.Nodes[from].Position, b = Network.Nodes[to].Position;
            Vec2 dir = (b - a).Normalized, right = dir.PerpRight;
            int n = RoadSpec.LanesPerDirection(e.Class);
            float w = RoadSpec.LaneWidth(e.Class);
            float sa = Junctions[from].Setback[e.Id], sb = Junctions[to].Setback[e.Id];
            int first = Lanes.Count;
            for (int i = 0; i < n; i++)
            {
                float offset = (n - i - 0.5f) * w;
                var lane = new Lane
                {
                    Id = Lanes.Count, EdgeId = e.Id, FromNode = from, ToNode = to,
                    Index = i, LanesInDirection = n, Class = e.Class,
                    Start = a + dir * sa + right * offset,
                    End = b - dir * sb + right * offset,
                    Direction = dir, Width = w, SpeedLimit = RoadSpec.SpeedLimit(e.Class),
                };
                lane.Length = Vec2.Distance(lane.Start, lane.End);
                if (i > 0) { lane.KerbNeighbor = first + i - 1; Lanes[first + i - 1].CentreNeighbor = lane.Id; }
                Lanes.Add(lane);
                ids.Add(lane.Id);
                Junctions[to].IncomingLanes.Add(lane.Id);
            }
        }

        /// <summary>Lanes on <paramref name="edgeId"/> travelling away from <paramref name="node"/>, kerb first.</summary>
        public List<int> LanesLeaving(int edgeId, int node)
        {
            var result = new List<int>();
            foreach (int id in EdgeLanes[edgeId]) if (Lanes[id].FromNode == node) result.Add(id);
            return result;
        }

        // ---- control -----------------------------------------------------------------

        void ClassifyControl(Junction j)
        {
            var node = Network.Nodes[j.NodeId];
            if (node.Degree <= 2) { j.Control = JunctionControl.None; SetApproach(j, _ => ApproachControl.Free); return; }

            int arterials = 0;
            foreach (int e in node.Edges) if (Network.Edges[e].Class == RoadClass.Arterial) arterials++;

            if (arterials >= 3 || (node.Degree == 4 && TouchesDistrict(j.NodeId, District.Downtown)))
                j.Control = JunctionControl.Signal;
            else if (arterials == 0 && node.Degree == 4)
                j.Control = JunctionControl.AllWayStop;
            else
                j.Control = JunctionControl.Priority;

            switch (j.Control)
            {
                case JunctionControl.AllWayStop:
                    SetApproach(j, _ => ApproachControl.Stop);
                    break;
                case JunctionControl.Priority:
                    (j.MainEdgeA, j.MainEdgeB) = MainRoad(node);
                    SetApproach(j, e => e == j.MainEdgeA || e == j.MainEdgeB ? ApproachControl.Free : ApproachControl.Stop);
                    break;
                case JunctionControl.Signal:
                    SetApproach(j, _ => ApproachControl.Signal);
                    BuildSignalPlan(j, node);
                    break;
            }
        }

        void SetApproach(Junction j, Func<int, ApproachControl> byEdge)
        {
            foreach (int l in j.IncomingLanes) Lanes[l].Control = byEdge(Lanes[l].EdgeId);
        }

        bool TouchesDistrict(int nodeId, District d)
        {
            int nx = Network.CellsX + 1, i = nodeId % nx, jj = nodeId / nx;
            for (int dy = -1; dy <= 0; dy++)
            for (int dx = -1; dx <= 0; dx++)
            {
                int cx = i + dx, cy = jj + dy;
                if (cx < 0 || cy < 0 || cx >= Network.CellsX || cy >= Network.CellsY) continue;
                if (Network.Cells[cy * Network.CellsX + cx].District == d) return true;
            }
            return false;
        }

        /// <summary>The pair of edges that best forms a through road: highest class, then straightest.</summary>
        (int, int) MainRoad(RoadNode node)
        {
            (int, int) best = (-1, -1);
            float bestScore = float.MinValue;
            for (int x = 0; x < node.Edges.Count; x++)
            for (int y = x + 1; y < node.Edges.Count; y++)
            {
                var ex = Network.Edges[node.Edges[x]];
                var ey = Network.Edges[node.Edges[y]];
                float straightness = -Vec2.Dot(Network.Dir(ex, node.Id), Network.Dir(ey, node.Id)); // 1 = opposite
                float score = (int)ex.Class + (int)ey.Class + straightness;
                if (score > bestScore) { bestScore = score; best = (ex.Id, ey.Id); }
            }
            return best;
        }

        void BuildSignalPlan(Junction j, RoadNode node)
        {
            var (a, b) = MainRoad(node);
            var axis1 = new List<int> { a, b };
            var axis2 = new List<int>();
            foreach (int e in node.Edges) if (e != a && e != b) axis2.Add(e);
            float Green(List<int> axis)
            {
                foreach (int e in axis) if (Network.Edges[e].Class == RoadClass.Arterial) return 28f;
                return 18f;
            }
            j.Phases.Add(new SignalPhase { GreenEdges = axis1.ToArray(), Green = Green(axis1) });
            j.Phases.Add(new SignalPhase { GreenEdges = axis2.ToArray(), Green = Green(axis2) });
        }

        // ---- connectors --------------------------------------------------------------

        void BuildConnectors(Junction j)
        {
            var node = Network.Nodes[j.NodeId];
            foreach (int inId in j.IncomingLanes)
            {
                var inLane = Lanes[inId];
                foreach (int outEdge in node.Edges)
                {
                    if (outEdge == inLane.EdgeId) continue; // U-turns only at cul-de-sacs (not generated yet)
                    var outs = LanesLeaving(outEdge, j.NodeId);
                    var outDir = Lanes[outs[0]].Direction;
                    var turn = Classify(inLane.Direction, outDir);

                    int target = -1;
                    if (node.Degree <= 2)
                        target = outs[Math.Min(inLane.Index, outs.Count - 1)];
                    else switch (turn)
                    {
                        case TurnType.Straight:
                            target = outs[Math.Min(inLane.Index, outs.Count - 1)];
                            break;
                        case TurnType.Right:
                            if (inLane.Index == 0) target = outs[0];
                            break;
                        case TurnType.Left:
                            if (inLane.Index == inLane.LanesInDirection - 1) target = outs[outs.Count - 1];
                            break;
                    }
                    if (target >= 0) AddConnector(j, inLane, Lanes[target], turn);
                }
                // U-turn back down the same street, from the centre-most lane (not at signals: no phase for it).
                if (node.Degree >= 3 && j.Control != JunctionControl.Signal && inLane.Index == inLane.LanesInDirection - 1)
                {
                    var back = LanesLeaving(inLane.EdgeId, j.NodeId);
                    if (back.Count > 0) AddConnector(j, inLane, Lanes[back[back.Count - 1]], TurnType.UTurn);
                }
            }
        }

        static TurnType Classify(Vec2 inDir, Vec2 outDir)
        {
            float angle = Vec2.SignedAngle(inDir, outDir);
            const float straight = 35f * MathF.PI / 180f;
            if (MathF.Abs(angle) < straight) return TurnType.Straight;
            if (MathF.Abs(angle) > 170f * MathF.PI / 180f) return TurnType.UTurn;
            return angle > 0 ? TurnType.Left : TurnType.Right;
        }

        void AddConnector(Junction j, Lane from, Lane to, TurnType turn)
        {
            Vec2 p0 = from.End, p3 = to.Start;
            float d = Vec2.Distance(p0, p3);
            // U-turns swing out into the junction box rather than pivoting on the spot.
            float handle = turn == TurnType.Straight ? d / 3f : turn == TurnType.UTurn ? Math.Max(7f, d * 1.2f) : d * 0.45f;
            Vec2 p1 = p0 + from.Direction * handle, p2 = p3 - to.Direction * handle;

            var pts = Bezier(p0, p1, p2, p3, out float len);
            var c = new Connector { Id = Connectors.Count, NodeId = j.NodeId, FromLane = from.Id, ToLane = to.Id, Turn = turn, Points = pts, Length = len };
            Connectors.Add(c);
            j.Connectors.Add(c.Id);
            from.Outgoing.Add(c.Id);
            to.Incoming.Add(c.Id);
        }

        void ComputeConflicts(Junction j)
        {
            var list = j.Connectors;
            for (int x = 0; x < list.Count; x++)
            for (int y = x + 1; y < list.Count; y++)
            {
                var a = Connectors[list[x]];
                var b = Connectors[list[y]];
                if (a.FromLane == b.FromLane) continue; // diverging from the same lane
                if (a.ToLane == b.ToLane || PathsCross(a.Points, b.Points))
                {
                    a.Conflicts.Add(b.Id);
                    b.Conflicts.Add(a.Id);
                }
            }
        }

        static bool PathsCross(Vec2[] p, Vec2[] q)
        {
            for (int i = 0; i + 1 < p.Length; i++)
            for (int k = 0; k + 1 < q.Length; k++)
                if (Vec2.SegmentsIntersect(p[i], p[i + 1], q[k], q[k + 1])) return true;
            return false;
        }
    }
}
