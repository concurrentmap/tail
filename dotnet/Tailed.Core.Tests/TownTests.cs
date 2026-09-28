using System;
using System.Collections.Generic;
using System.Linq;
using Tailed.Core.Roads;
using Tailed.Core.Util;
using Xunit;

namespace Tailed.Core.Tests
{
    public class TownTests
    {
        static readonly ulong[] Seeds = { 1, 2, 3, 42, 1234, 99999 };

        static (RoadNetwork net, LaneGraph graph) Town(ulong seed)
        {
            var net = TownGenerator.Generate(new TownConfig { Seed = seed });
            return (net, LaneGraph.Build(net));
        }

        [Fact]
        public void Generator_IsDeterministic()
        {
            var a = TownGenerator.Generate(new TownConfig { Seed = 7 });
            var b = TownGenerator.Generate(new TownConfig { Seed = 7 });
            var c = TownGenerator.Generate(new TownConfig { Seed = 8 });
            Assert.Equal(a.Edges.Select(e => (e.A, e.B)), b.Edges.Select(e => (e.A, e.B)));
            Assert.Equal(a.Nodes.Select(n => n.Position), b.Nodes.Select(n => n.Position));
            Assert.NotEqual(a.Nodes.Select(n => n.Position), c.Nodes.Select(n => n.Position));
        }

        [Theory, MemberData(nameof(SeedData))]
        public void Network_HasAllDistricts_AndNoDeadEnds(ulong seed)
        {
            var (net, _) = Town(seed);
            foreach (District d in Enum.GetValues(typeof(District)))
                Assert.Contains(net.Cells, c => c.District == d);
            Assert.DoesNotContain(net.Nodes, n => n.Degree == 1);
            Assert.All(net.Edges, e => Assert.True(net.Length(e) > 50f));
        }

        [Theory, MemberData(nameof(SeedData))]
        public void Network_ThinsSuburbs_ButKeepsDowntownAndArterials(ulong seed)
        {
            var (net, _) = Town(seed);
            int lattice = (net.CellsX + 1) * net.CellsY + (net.CellsY + 1) * net.CellsX;
            Assert.InRange(net.Edges.Count, lattice * 0.6, lattice * 0.95);
            foreach (var cell in net.Cells.Where(c => c.District == District.Downtown))
                Assert.All(cell.Sides, s => Assert.NotEqual(-1, s));
            // Every arterial lattice line is continuous: 16 cells × 5 lines × 2 axes.
            Assert.Equal(160, net.Edges.Count(e => e.Class == RoadClass.Arterial));
        }

        [Theory, MemberData(nameof(SeedData))]
        public void Streets_AreNamedConsistentlyPerLine(ulong seed)
        {
            var (net, _) = Town(seed);
            Assert.All(net.Edges, e => Assert.False(string.IsNullOrEmpty(e.StreetName)));
            int nx = net.CellsX + 1;
            // Same lattice line → same name; different lines → different names.
            var lines = net.Edges.GroupBy(e => e.A % nx == e.B % nx ? ("v", e.A % nx) : ("h", e.A / nx));
            Assert.All(lines, g => Assert.Single(g.Select(e => e.StreetName).Distinct()));
            Assert.Equal(lines.Count(), lines.Select(g => g.First().StreetName).Distinct().Count());
            Assert.Contains(net.Edges, e => e.Class == RoadClass.Arterial && (e.StreetName.EndsWith(" St") || e.StreetName.EndsWith(" Ave")));
        }

        [Theory, MemberData(nameof(SeedData))]
        public void LaneGraph_IsStronglyConnected(ulong seed)
        {
            var (_, g) = Town(seed);
            // Edges: connectors (lane → next lane) and lane changes (either neighbour).
            var fwd = new List<int>[g.Lanes.Count];
            var rev = new List<int>[g.Lanes.Count];
            for (int i = 0; i < fwd.Length; i++) { fwd[i] = new List<int>(); rev[i] = new List<int>(); }
            void Link(int a, int b) { fwd[a].Add(b); rev[b].Add(a); }
            foreach (var c in g.Connectors) Link(c.FromLane, c.ToLane);
            foreach (var l in g.Lanes)
            {
                if (l.KerbNeighbor >= 0) Link(l.Id, l.KerbNeighbor);
                if (l.CentreNeighbor >= 0) Link(l.Id, l.CentreNeighbor);
            }
            Assert.Equal(g.Lanes.Count, Reach(fwd, 0));
            Assert.Equal(g.Lanes.Count, Reach(rev, 0));
        }

        [Theory, MemberData(nameof(SeedData))]
        public void LaneGraph_EveryLaneHasAWayOut_AndSaneGeometry(ulong seed)
        {
            var (_, g) = Town(seed);
            Assert.All(g.Lanes, l =>
            {
                Assert.NotEmpty(l.Outgoing);
                Assert.NotEmpty(l.Incoming);
                Assert.True(l.Length > 20f, $"lane {l.Id} too short: {l.Length}");
                Assert.True(Vec2.Dot(l.End - l.Start, l.Direction) > 0f, $"lane {l.Id} reversed by setbacks");
            });
            Assert.All(g.Connectors, c => Assert.InRange(c.Length, 1f, 60f));
        }

        [Theory, MemberData(nameof(SeedData))]
        public void Conflicts_AreSymmetric_AndSameJunction(ulong seed)
        {
            var (_, g) = Town(seed);
            foreach (var c in g.Connectors)
            foreach (int other in c.Conflicts)
            {
                Assert.Contains(c.Id, g.Connectors[other].Conflicts);
                Assert.Equal(c.NodeId, g.Connectors[other].NodeId);
                Assert.NotEqual(c.Id, other);
            }
        }

        [Fact]
        public void FourWayJunction_OppositeStraightsDontConflict_PerpendicularStraightsDo()
        {
            var (net, g) = Town(42);
            var j = g.Junctions.Values.First(x => net.Nodes[x.NodeId].Degree == 4 && x.Control == JunctionControl.Signal);
            var straights = j.Connectors.Select(id => g.Connectors[id]).Where(c => c.Turn == TurnType.Straight).ToList();
            foreach (var a in straights)
            foreach (var b in straights)
            {
                if (a.Id == b.Id || a.FromLane == b.FromLane) continue;
                float dot = Vec2.Dot(g.Lanes[a.FromLane].Direction, g.Lanes[b.FromLane].Direction);
                bool conflicts = a.Conflicts.Contains(b.Id);
                if (dot < -0.7f) Assert.False(conflicts, "opposing straights should pass each other");
                if (MathF.Abs(dot) < 0.5f) Assert.True(conflicts, "crossing straights must conflict");
            }
        }

        [Theory, MemberData(nameof(SeedData))]
        public void Junction_ControlIsConsistent(ulong seed)
        {
            var (net, g) = Town(seed);
            Assert.Contains(g.Junctions.Values, j => j.Control == JunctionControl.Signal);
            Assert.Contains(g.Junctions.Values, j => j.Control == JunctionControl.Priority);
            Assert.Contains(g.Junctions.Values, j => j.Control == JunctionControl.AllWayStop);
            foreach (var j in g.Junctions.Values)
            {
                var node = net.Nodes[j.NodeId];
                if (j.Control == JunctionControl.Signal)
                {
                    var green = j.Phases.SelectMany(p => p.GreenEdges).ToList();
                    Assert.Equal(node.Edges.OrderBy(e => e), green.OrderBy(e => e)); // each approach green exactly once
                }
                if (j.Control == JunctionControl.Priority)
                    Assert.Contains(j.IncomingLanes, l => g.Lanes[l].Control == ApproachControl.Stop);
                if (node.Degree == 2)
                    Assert.Equal(JunctionControl.None, j.Control);
            }
            // Arterial crossings are always signalled.
            foreach (var j in g.Junctions.Values)
                if (net.Nodes[j.NodeId].Edges.Count(e => net.Edges[e].Class == RoadClass.Arterial) >= 3)
                    Assert.Equal(JunctionControl.Signal, j.Control);
        }

        public static IEnumerable<object[]> SeedData() => Seeds.Select(s => new object[] { s });

        static int Reach(List<int>[] adj, int start)
        {
            var seen = new bool[adj.Length];
            var stack = new Stack<int>();
            stack.Push(start);
            seen[start] = true;
            int count = 1;
            while (stack.Count > 0)
                foreach (int n in adj[stack.Pop()])
                    if (!seen[n]) { seen[n] = true; count++; stack.Push(n); }
            return count;
        }

        [Theory]
        [InlineData(42UL)]
        [InlineData(7UL)]
        [InlineData(1234UL)]
        public void EveryStreetIsNamed_AndNamesDontRepeat(ulong seed)
        {
            var net = TownGenerator.Generate(new TownConfig { Seed = seed });
            Assert.All(net.Edges, e => Assert.False(string.IsNullOrWhiteSpace(e.StreetName), $"edge {e.Id} has no name"));
            // One name per lattice line: two different lines never share a name.
            int nx = net.CellsX + 1;
            var lineOf = new System.Collections.Generic.Dictionary<string, (bool, int)>();
            foreach (var e in net.Edges)
            {
                bool vertical = e.A % nx == e.B % nx;
                var line = (vertical, vertical ? e.A % nx : e.A / nx);
                if (lineOf.TryGetValue(e.StreetName, out var seen)) Assert.Equal(seen, line);
                else lineOf[e.StreetName] = line;
            }
        }
    }
}
