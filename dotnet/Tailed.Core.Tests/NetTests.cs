using System;
using System.Linq;
using Tailed.Core.Identity;
using Tailed.Core.Net;
using Tailed.Core.Roads;
using Tailed.Core.Traffic;
using Tailed.Core.Util;
using Xunit;

namespace Tailed.Core.Tests
{
    public class NetTests
    {
        [Fact]
        public void Primitives_RoundTrip()
        {
            var w = new ByteWriter(4);
            w.U8(200); w.I8(-5); w.U16(65000); w.I32(-123456); w.U64(ulong.MaxValue - 7); w.F32(3.14159f); w.Str("Honker Pip ✓"); w.Bool(true);
            var r = new ByteReader(w.ToArray());
            Assert.Equal(200, r.U8()); Assert.Equal(-5, r.I8()); Assert.Equal(65000, r.U16()); Assert.Equal(-123456, r.I32());
            Assert.Equal(ulong.MaxValue - 7, r.U64()); Assert.Equal(3.14159f, r.F32()); Assert.Equal("Honker Pip ✓", r.Str()); Assert.True(r.Bool());
            Assert.True(r.AtEnd);
            Assert.Throws<FormatException>(() => r.U8());
        }

        [Fact]
        public void TrafficRecords_AreCompact_AndReconstructPosesWithinCentimetres()
        {
            var net = TownGenerator.Generate(new TownConfig { Seed = 42 });
            var g = LaneGraph.Build(net);
            var sim = new TrafficSim(g, 42) { TargetPopulation = 300 };
            sim.Populate();
            for (int i = 0; i < 20 * 60; i++) sim.Step(0.05f);

            var w = new ByteWriter();
            var alive = sim.Vehicles.Where(v => v.Alive).ToList();
            foreach (var v in alive) TrafficCodec.Write(w, TrafficCodec.FromSim(v));
            Assert.True(w.Length <= alive.Count * 11, $"{w.Length} bytes for {alive.Count} vehicles");

            var r = new ByteReader(w.ToArray());
            foreach (var v in alive)
            {
                var s = TrafficCodec.Read(r);
                Assert.Equal(v.Id, s.Id);
                Assert.Equal(v.Mode, s.Mode);
                int lane = s.Mode == VehicleMode.Lane ? s.Ref : -1, con = s.Mode == VehicleMode.Connector ? s.Ref : -1;
                int bay = s.Mode >= VehicleMode.ParkIn && s.Mode <= VehicleMode.ParkOut ? s.Ref : -1;
                VehiclePose.Compute(g, s.Mode, lane, con, bay, s.S, v.Length, s.Lateral, out var pos, out _);
                Assert.True(Vec2.Distance(pos, v.Position) < 0.1f, $"vehicle {v.Id} ({v.Mode}) off by {Vec2.Distance(pos, v.Position):0.00} m");
                Assert.InRange(s.Speed, v.Speed - 0.15f, v.Speed + 0.15f);
            }
            Assert.True(r.AtEnd);
        }

        [Fact]
        public void ExternalPose_RoundTrips()
        {
            var st = new VehicleState { Id = 7, Mode = VehicleMode.External, Position = new Vec2(812.37f, 1333.9f), Heading = new Vec2(-0.6f, 0.8f), Speed = 13.2f, Flags = VehicleFlags.IndicateLeft };
            var w = new ByteWriter();
            TrafficCodec.Write(w, st);
            Assert.Equal(11, w.Length);
            var back = TrafficCodec.Read(new ByteReader(w.ToArray()));
            Assert.True(Vec2.Distance(st.Position, back.Position) < 0.05f);
            Assert.True(Vec2.Dot(st.Heading, back.Heading) > 0.9999f);
            Assert.Equal(VehicleFlags.IndicateLeft, back.Flags);
        }

        [Fact]
        public void Identity_RoundTrips()
        {
            var id = new IdentityService(3).Random();
            var w = new ByteWriter();
            TrafficCodec.WriteIdentity(w, id);
            Assert.Equal(id, TrafficCodec.ReadIdentity(new ByteReader(w.ToArray())));
        }
    }
}
