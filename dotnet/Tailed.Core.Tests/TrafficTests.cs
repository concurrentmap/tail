using System;
using System.Collections.Generic;
using System.Linq;
using Tailed.Core.Identity;
using Tailed.Core.Roads;
using Tailed.Core.Traffic;
using Tailed.Core.Util;
using Xunit;
using Xunit.Abstractions;

namespace Tailed.Core.Tests
{
    public class TrafficTests
    {
        readonly ITestOutputHelper _out;
        public TrafficTests(ITestOutputHelper output) => _out = output;

        static TrafficSim NewSim(ulong seed, int population = 420)
        {
            var net = TownGenerator.Generate(new TownConfig { Seed = seed });
            var sim = new TrafficSim(LaneGraph.Build(net), seed) { TargetPopulation = population };
            sim.Populate();
            return sim;
        }

        [Fact]
        public void Router_ProducesContiguousRoutes()
        {
            var sim = NewSim(3, 0);
            var g = sim.Graph;
            var rng = new Rng(9);
            for (int i = 0; i < 200; i++)
            {
                var a = g.Lanes[rng.NextInt(g.Lanes.Count)];
                var b = g.Lanes[rng.NextInt(g.Lanes.Count)];
                int start = sim.Router.Directed(a.EdgeId, a.ToNode), goal = sim.Router.Directed(b.EdgeId, b.ToNode);
                var route = sim.Router.Route(start, goal, start == goal, 0.3f, ref rng);
                Assert.NotNull(route);
                int node = a.ToNode, prevEdge = a.EdgeId;
                foreach (int e in route)
                {
                    var edge = g.Network.Edges[e];
                    Assert.True(edge.A == node || edge.B == node, "route must be contiguous");
                    Assert.NotEqual(prevEdge, e); // no U-turns
                    node = edge.Other(node);
                    prevEdge = e;
                }
                if (route.Count > 0) Assert.Equal(b.EdgeId, route[^1]);
                Assert.Equal(b.ToNode, route.Count > 0 ? node : a.ToNode);
            }
        }

        [Fact]
        public void Signals_NeverShowConflictingGreens()
        {
            var sim = NewSim(5, 0);
            foreach (var j in sim.Graph.Junctions.Values.Where(x => x.Control == JunctionControl.Signal))
            {
                for (int step = 0; step < 2400; step++)
                {
                    var green = sim.Graph.Network.Nodes[j.NodeId].Edges.Where(e => sim.Signal(j.NodeId, e) != SignalState.Red).ToList();
                    Assert.True(green.Count == 0 || j.Phases.Any(p => green.All(e => p.GreenEdges.Contains(e))),
                        $"junction {j.NodeId}: greens {string.Join(",", green)} span phases");
                    sim.Step(0.05f);
                }
            }
        }

        [Theory]
        [InlineData(42UL)]
        [InlineData(7UL)]
        [InlineData(1234UL)]
        public void Soak_TrafficFlowsWithoutOverlapsOrGridlock(ulong seed)
        {
            var sim = NewSim(seed);
            var g = sim.Graph;
            int parkedSeen = 0, connectorsEntered = 0, despawned = 0;
            var wasOnConnector = new Dictionary<int, bool>();
            sim.Despawned += _ => despawned++;
            var parked = new HashSet<int>();
            double speedSum = 0; long speedSamples = 0;
            const float dt = 0.05f;
            const int minutes = 6;

            for (int step = 0; step < minutes * 60 * 20; step++)
            {
                sim.Step(dt);
                if (step % 10 != 0) continue;

                foreach (var v in sim.Vehicles.Where(v => v.Alive && !v.IsExternal))
                {
                    speedSum += v.Speed; speedSamples++;
                    if (v.Mode == VehicleMode.Parked && parked.Add(v.GetHashCode())) parkedSeen++;
                    bool onCon = v.Mode == VehicleMode.Connector;
                    if (onCon && !(wasOnConnector.TryGetValue(v.Id, out var w) && w)) connectorsEntered++;
                    wasOnConnector[v.Id] = onCon;
                }

                // No two vehicles overlap on the same lane.
                foreach (var grp in sim.Vehicles.Where(v => v.Alive && v.Mode == VehicleMode.Lane).GroupBy(v => v.Lane))
                {
                    var sorted = grp.OrderBy(v => v.S).ToList();
                    for (int i = 1; i < sorted.Count; i++)
                    {
                        float gap = sorted[i].S - sorted[i].Length - sorted[i - 1].S;
                        Assert.True(gap > -0.5f, $"overlap on lane {grp.Key} at t={sim.Time:0.0}: gap {gap:0.00} (vehicles {sorted[i - 1].Id},{sorted[i].Id})");
                    }
                }
            }

            var alive = sim.Vehicles.Where(v => v.Alive && !v.IsExternal).ToList();
            int stuck = alive.Count(v => v.StuckTimer > 60f);
            double avgSpeed = speedSum / speedSamples;
            _out.WriteLine($"alive={alive.Count} avgSpeed={avgSpeed:0.0} m/s stuck>60s={stuck} parkedSeen={parkedSeen} " +
                           $"junctionEntries={connectorsEntered} despawned={despawned} " +
                           $"modes={string.Join(",", alive.GroupBy(v => v.Mode).Select(x => $"{x.Key}:{x.Count()}"))}");

            Assert.True(alive.Count > sim.TargetPopulation * 0.9, "population collapsed");
            Assert.True(avgSpeed > 4.0, $"traffic too slow: {avgSpeed:0.0} m/s");
            Assert.True(stuck < alive.Count * 0.03, $"{stuck} vehicles stuck for over a minute");
            Assert.True(parkedSeen > 20, "NPCs should park at POIs");
            Assert.True(connectorsEntered > 3000);
        }

        [Fact]
        public void Indicators_AlwaysReflectIntent()
        {
            var sim = NewSim(42);
            var g = sim.Graph;
            int turnsChecked = 0, laneChangesChecked = 0, idleChecked = 0;
            for (int step = 0; step < 20 * 180; step++)
            {
                sim.Step(0.05f);
                foreach (var v in sim.Vehicles)
                {
                    if (!v.Alive || v.IsExternal || v.DisruptedTimer > 0f) continue;
                    bool left = (v.Flags & VehicleFlags.IndicateLeft) != 0, right = (v.Flags & VehicleFlags.IndicateRight) != 0;
                    Assert.False(left && right, $"vehicle {v.Id} signalling both ways");
                    string where = $"vehicle {v.Id} ({v.Profile.Type}) {v.Mode} t={sim.Time:0.0}";

                    if (v.Mode == VehicleMode.Connector)
                    {
                        var con = g.Connectors[v.Connector];
                        bool junction = g.Network.Nodes[con.NodeId].Degree >= 3;
                        if (con.Turn == TurnType.Straight || !junction) { Assert.False(left || right, $"{where}: signalling while going straight/following a bend"); continue; }
                        turnsChecked++;
                        if (!TrafficSim.Signals(v, con.NodeId)) { Assert.False(left || right, where); continue; }
                        Assert.True(con.Turn != TurnType.Right ? left && !right : right && !left, $"{where}: turning {con.Turn} but signalling {(left ? "left" : right ? "right" : "nothing")}");
                    }
                    else if (v.Mode == VehicleMode.Lane)
                    {
                        var lane = g.Lanes[v.Lane];
                        if (MathF.Abs(v.Lateral) > 0.3f)
                        {
                            laneChangesChecked++;
                            if (TrafficSim.Signals(v, 300000 + lane.EdgeId))
                                Assert.True(v.Lateral > 0f ? right : left, $"{where}: changing lanes {(v.Lateral > 0 ? "right" : "left")} without signalling");
                            continue;
                        }
                        bool nearTurn = v.NextConnector >= 0 && lane.Length - v.S < 50f && g.Connectors[v.NextConnector].Turn != TurnType.Straight;
                        bool nearBay = v.DestBay >= 0 && g.Bays[v.DestBay].AccessLane == v.Lane && g.Bays[v.DestBay].EntryS - v.S < 30f;
                        bool kerbStop = v.KerbStop >= 0 || v.KerbTimer > 0f;
                        if (!nearTurn && !nearBay && !kerbStop && v.PendingLane < 0)
                        {
                            idleChecked++;
                            Assert.False(left || right, $"{where}: signalling with nothing planned");
                        }
                    }
                }
            }
            Assert.True(turnsChecked > 2000 && laneChangesChecked > 200 && idleChecked > 50000,
                $"coverage: turns {turnsChecked}, lane changes {laneChangesChecked}, idle {idleChecked}");
        }

        [Fact]
        public void Npcs_DriveLikePeople()
        {
            var sim = NewSim(42);
            var g = sim.Graph;
            int uTurns = 0, busStops = 0, doubleParks = 0, wandering = 0, laneSamples = 0, hesitations = 0;
            var seenUTurn = new HashSet<int>();
            var wasKerb = new Dictionary<int, bool>();
            var wasWaiting = new Dictionary<int, bool>();
            float maxWander = 0f;
            Assert.Contains(g.KerbStops, k => k.BusStop);
            for (int step = 0; step < 20 * 240; step++)
            {
                sim.Step(0.05f);
                foreach (var v in sim.Vehicles)
                {
                    if (!v.Alive || v.IsExternal) continue;
                    if (v.Mode == VehicleMode.Connector && g.Connectors[v.Connector].Turn == TurnType.UTurn && seenUTurn.Add(v.Id * 7919 + (int)(sim.Time / 30f)))
                        uTurns++;
                    bool kerb = v.KerbTimer > 0f;
                    if (kerb && !(wasKerb.TryGetValue(v.Id, out var k) && k))
                    {
                        if (v.Model.Style == BodyStyle.Bus) busStops++; else doubleParks++;
                        Assert.Equal(g.KerbStops[v.KerbStop].Lane, v.Lane);
                    }
                    wasKerb[v.Id] = kerb;
                    if (kerb) Assert.True(v.Model.Style == BodyStyle.Bus || (v.Flags & VehicleFlags.Hazard) != 0, "double-parked without hazards");
                    if (v.ReactionTimer > 0f && !(wasWaiting.TryGetValue(v.Id, out var w) && w)) hesitations++;
                    wasWaiting[v.Id] = v.ReactionTimer > 0f;
                    if (v.Mode == VehicleMode.Lane && v.KerbStop < 0 && !kerb)
                    {
                        laneSamples++;
                        if (MathF.Abs(v.Wander) > 0.05f) wandering++;
                        maxWander = MathF.Max(maxWander, MathF.Abs(v.Wander));
                    }
                }
            }
            _out.WriteLine($"uTurns={uTurns} busStops={busStops} doubleParks={doubleParks} hesitations={hesitations} " +
                           $"wandering={wandering * 100f / laneSamples:0}% maxWander={maxWander:0.00}");
            Assert.True(uTurns > 5, $"U-turns: {uTurns}");
            Assert.True(busStops > 5, $"bus stops: {busStops}");
            Assert.True(doubleParks > 2, $"double parks: {doubleParks}");
            Assert.True(hesitations > 200, $"hesitations: {hesitations}");
            Assert.True(wandering > laneSamples / 3, "most NPCs should be a little off-centre");
            Assert.True(maxWander <= TrafficSim.KerbShift + 0.01f, $"wander {maxWander:0.00} m leaves the lane");
        }

        [Fact]
        public void ExternalVehicle_IsFollowedNotDrivenThrough()
        {
            var sim = NewSim(11, 300);
            var g = sim.Graph;
            // Park a "player" in the middle of a busy arterial kerbside lane.
            var lane = g.Lanes.Where(l => l.Class == RoadClass.Arterial && l.Index == 0 && l.Length > 80f).OrderBy(l => l.Id).First();
            var player = sim.AddExternal(sim.Identities.RandomCar());
            var pos = lane.PointAt(lane.Length * 0.6f);
            for (int step = 0; step < 20 * 90; step++)
            {
                sim.UpdateExternal(player.Id, pos, lane.Direction, 0f, VehicleFlags.None);
                sim.Step(0.05f);
                foreach (var v in sim.Vehicles.Where(v => v.Alive && !v.IsExternal && v.Mode == VehicleMode.Lane && v.Lane == lane.Id))
                {
                    float playerFront = lane.Length * 0.6f + player.Length * 0.5f;
                    float playerRear = playerFront - player.Length;
                    Assert.False(v.S > playerRear + 0.3f && v.S - v.Length < playerFront - 0.3f,
                        $"NPC {v.Id} drove through the player at t={sim.Time:0.0}");
                }
            }
        }

        [Fact]
        public void PlayerParkedInABay_TakesIt_AndNpcsGoElsewhere()
        {
            var sim = NewSim(11, 300);
            var g = sim.Graph;
            var player = sim.AddExternal(sim.Identities.RandomCar());
            var bay = g.Bays.First(b => !sim.BayOccupied(b.Id));
            var centre = bay.Position - bay.Heading * 2.2f;
            for (int step = 0; step < 20 * 60; step++)
            {
                sim.UpdateExternal(player.Id, centre, bay.Heading, 0f, VehicleFlags.None);
                sim.Step(0.05f);
                Assert.True(sim.BayOccupied(bay.Id));
                foreach (var v in sim.Vehicles.Where(v => v.Alive && !v.IsExternal))
                {
                    Assert.NotEqual(bay.Id, v.DestBay);
                    Assert.False(v.Bay == bay.Id && (v.Mode == VehicleMode.ParkIn || v.Mode == VehicleMode.Parked), $"NPC {v.Id} parked on the player");
                }
            }
            // Driving off frees it again.
            sim.UpdateExternal(player.Id, g.Lanes[bay.AccessLane].PointAt(5f), g.Lanes[bay.AccessLane].Direction, 10f, VehicleFlags.None);
            sim.Step(0.05f);
            Assert.False(sim.BayOccupied(bay.Id) && sim.Vehicles.All(v => v.DestBay != bay.Id && v.Bay != bay.Id));
        }

        [Fact]
        public void Identities_FollowFleetDistribution()
        {
            var ids = new IdentityService(1);
            var counts = new int[VehicleCatalog.Models.Length];
            for (int i = 0; i < 5000; i++) counts[ids.Random().ModelId]++;
            Assert.True(counts[0] > counts[7] * 3, "sedans should vastly outnumber sports coupes");
        }
    }
}
