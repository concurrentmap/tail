using System.Collections.Generic;
using System.Linq;
using Tailed.Core.Identity;
using Tailed.Core.Roads;
using Tailed.Core.Rules;
using Tailed.Core.Util;
using Xunit;

namespace Tailed.Core.Tests
{
    public class MatchTests
    {
        readonly LaneGraph _g = LaneGraph.Build(TownGenerator.Generate(new TownConfig { Seed = 42 }));
        readonly Dictionary<int, CarSample> _cars = new Dictionary<int, CarSample>();

        Match NewMatch(int players = 3)
        {
            var m = new Match(_g, new MatchConfig(), 7);
            for (int i = 1; i <= players; i++) m.AddPlayer(i, "P" + i);
            var ids = new IdentityService(1);
            m.StartMatch();
            foreach (var p in m.Players) p.Identity = ids.RandomCar();
            return m;
        }

        void Tick(Match m, float seconds)
        {
            for (float t = 0; t < seconds; t += 0.25f)
                m.Tick(0.25f, id => _cars.TryGetValue(id, out var c) ? c : (CarSample?)null);
        }

        void ToDriving(Match m)
        {
            foreach (var t in m.Players.Where(p => p.Role == Role.Tail)) m.SetReady(t.Id, true);
            Assert.Equal(Phase.Briefing, m.Phase);
            Assert.True(m.SubmitRoute(m.MarkId, m.CandidateCheckpoints.Reverse().ToList()));
            Assert.Equal(Phase.Pickup, m.Phase);
            Tick(m, m.Cfg.PickupSeconds + 0.5f);
            Assert.Equal(Phase.Driving, m.Phase);
        }

        Vec2 BayOf(int poi) => Match.BayCentre(_g.Bays[_g.Sites[poi].Bays[0]]);

        void DwellAt(Match m, Vec2 pos, float seconds)
        {
            _cars[m.MarkId] = new CarSample { Position = pos, Speed = 0f, OnRoad = false };
            Tick(m, seconds);
            _cars[m.MarkId] = new CarSample { Position = pos + new Vec2(30, 0), Speed = 10f, OnRoad = true };
            Tick(m, 0.5f);
        }

        [Fact]
        public void LateJoiners_PickACarOrSpot()
        {
            var m = NewMatch(2);
            var early = m.AddPlayer(10, "Early"); // motor pool still open: a Tail who picks a car
            Assert.Equal(Role.Tail, early.Role);
            early.Identity = new IdentityService(2).RandomCar();
            ToDriving(m);
            var late = m.AddPlayer(11, "Late");   // mid-round: watches the cameras
            Assert.Equal(Role.Spotter, late.Role);
            Assert.Null(m.AddPin(11, _g.Sites[0].Centre)); // nobody marks during the drive
        }

        [Fact]
        public void Roles_RotateAndRoutesAreSpreadOut()
        {
            var m = NewMatch(3);
            Assert.Equal(Phase.MotorPool, m.Phase);
            Assert.Equal(Role.Mark, m.Player(1).Role);
            Assert.All(m.Players.Where(p => p.Id != 1), p => Assert.Equal(Role.Tail, p.Role));
            Assert.True(m.CandidateCheckpoints.Count >= 3);
            Assert.DoesNotContain(m.Safehouse, m.CandidateCheckpoints);
            foreach (var a in m.CandidateCheckpoints)
            foreach (var b in m.CandidateCheckpoints)
                if (a != b) Assert.True(Vec2.Distance(_g.Sites[a].Centre, _g.Sites[b].Centre) > 250f);
        }

        [Fact]
        public void MotorPool_EnforcesBudget()
        {
            var m = NewMatch();
            Assert.False(m.PickVehicle(2, 0, 0));  // silver sedan: 5 + 1 > 5
            Assert.True(m.PickVehicle(2, 0, 5));   // red sedan: 5
            Assert.True(m.PickVehicle(2, 6, 0));   // silver cube: 1 + 1
            Assert.False(m.PickVehicle(1, 6, 5));  // the Mark doesn't pick
        }

        /// <summary>Drive the Mark through every remaining stop (from <paramref name="from"/>) to the safehouse.</summary>
        void CompleteRoute(Match m, int from)
        {
            var route = m.Route.ToList();
            for (int i = from; i < route.Count; i++)
            {
                _cars[m.MarkId] = new CarSample { Position = BayOf(route[i]), Speed = 0f };
                Tick(m, m.Cfg.DwellSeconds + 2f);
                if (m.Phase == Phase.Driving) { _cars[m.MarkId] = new CarSample { Position = BayOf(route[i]) + new Vec2(40, 0), Speed = 10f, OnRoad = true }; Tick(m, 0.5f); }
            }
        }

        [Fact]
        public void FullRound_HiddenFlags_MarkingAtTheEnd_LocationsVersusTails()
        {
            var m = NewMatch(3);
            ToDriving(m);
            var route = m.Route.ToList();
            var tail = m.Player(2);

            // Flags outside a stop are refused and cost nothing.
            m.SubmitFlag(m.MarkId, tail.Identity.ModelId, tail.Identity.Plate);
            Assert.Equal(m.Cfg.FlagTokens, m.FlagTokens);

            // A fake stop, then the first real one; the errand marker shows briefly at the real stop only.
            var fake = new Vec2(300, 300);
            DwellAt(m, fake, m.Cfg.DwellSeconds + 3);
            Assert.Equal(0, m.NextCheckpoint);
            _cars[m.MarkId] = new CarSample { Position = BayOf(route[0]), Speed = 0f };
            Tick(m, 3f);
            Assert.True(m.StopMarkerActive);
            Assert.True(m.FlagWindowOpen);

            // Flags cost a token each and say nothing about the result.
            var miss = m.SubmitFlag(m.MarkId, (tail.Identity.ModelId + 1) % 8, tail.Identity.Plate);
            var hit = m.SubmitFlag(m.MarkId, tail.Identity.ModelId, tail.Identity.Plate.Substring(0, 5) + "??"); // 5 of 7 is enough
            Assert.False(miss.Hit); Assert.False(hit.Hit);
            Assert.Equal(m.Cfg.FlagTokens - 2, m.FlagTokens);
            Assert.Equal(new[] { -1, tail.Id }, m.Flags.Select(f => f.Hit));
            Assert.False(tail.NeedsChopShop);                  // no mid-game burns
            Tick(m, m.Cfg.StopMarkerSeconds);
            Assert.False(m.StopMarkerActive);                 // brief
            Tick(m, m.Cfg.DwellSeconds);                      // finish the errand

            // No marking during the drive.
            Assert.Null(m.AddPin(2, _g.Sites[route[0]].Centre));

            _cars[m.MarkId] = new CarSample { Position = BayOf(route[0]) + new Vec2(40, 0), Speed = 10f, OnRoad = true };
            Tick(m, 1f);
            Assert.Equal(1, m.NextCheckpoint);
            var kinds = m.Timeline.Select(e => e.Type).ToList();
            Assert.Equal(new[] { ReplayEventType.FlagMiss, ReplayEventType.FlagHit, ReplayEventType.Checkpoint }, kinds);

            CompleteRoute(m, 1);
            Assert.Equal(Phase.Marking, m.Phase);
            Assert.Contains("safehouse", m.DriveEndReason);

            // The Tail team marks on one shared map, up to one mark per route stop.
            Assert.Null(m.AddPin(m.MarkId, _g.Sites[route[0]].Centre));                     // not the Mark
            Assert.NotNull(m.AddPin(2, _g.Sites[route[0]].Centre + new Vec2(10, 5)));
            Assert.NotNull(m.AddPin(3, _g.Sites[route[1]].Centre + new Vec2(25, 0)));
            Assert.NotNull(m.AddPin(3, fake));                                               // wasted on the fake stop
            while (m.Pins.Count < m.MarksAllowed) Assert.NotNull(m.AddPin(2, new Vec2(5, 5 + m.Pins.Count)));
            Assert.Null(m.AddPin(2, _g.Sites[route[2]].Centre));                             // team limit reached
            Assert.True(m.RemovePin(2, m.Pins.First(x => x.Player == 3 && x.Position.Equals(fake)).Id)); // any Tail can move marks
            Assert.NotNull(m.AddPin(2, _g.Sites[route[2]].Centre));

            Tick(m, m.Cfg.MarkingSeconds + 0.5f);
            Assert.Equal(Phase.Debrief, m.Phase);
            var r = m.Results.Last();
            Assert.Equal(3, r.LocationsIdentified);
            Assert.Equal(1, r.TailsIdentified);
            Assert.Equal(RoundOutcome.TailsWin, r.Outcome);
            Assert.Equal(2, r.Stops[0].FoundBy);
            Assert.Equal(3, r.Stops[1].FoundBy);
            Assert.All(r.Stops, st => Assert.True(st.Visited));
            Assert.Equal(1, m.Players.First(p => p.Id == 2).MatchScore); // Tails' round win
            Assert.Equal(0, m.Mark.MatchScore);
        }

        [Fact]
        public void MarkWins_WhenMoreTailsIdentifiedThanLocations_AndTieIsADraw()
        {
            RoundOutcome Play(int tailsToFlag, int stopsToMark)
            {
                var m = NewMatch(3);
                ToDriving(m);
                var route = m.Route.ToList();
                _cars[m.MarkId] = new CarSample { Position = BayOf(route[0]), Speed = 0f };
                Tick(m, 3f);
                foreach (var t in m.Players.Where(p => p.Role == Role.Tail).Take(tailsToFlag))
                    m.SubmitFlag(m.MarkId, t.Identity.ModelId, t.Identity.Plate);
                m.SubmitFlag(m.MarkId, 0, "ZZZ9999"); // just traffic
                CompleteRoute(m, 0);
                for (int i = 0; i < stopsToMark; i++) m.AddPin(2, _g.Sites[route[i]].Centre);
                m.Advance();
                Assert.Equal(Phase.Debrief, m.Phase);
                Assert.Equal(tailsToFlag, m.Results.Last().TailsIdentified);
                Assert.Equal(stopsToMark, m.Results.Last().LocationsIdentified);
                return m.Results.Last().Outcome;
            }
            Assert.Equal(RoundOutcome.MarkWins, Play(2, 1));
            Assert.Equal(RoundOutcome.Draw, Play(1, 1));
            Assert.Equal(RoundOutcome.TailsWin, Play(0, 1));
        }

        [Fact]
        public void CarSwap_OnlyAtRentalBays()
        {
            var m = NewMatch(2);
            ToDriving(m);
            var rental = _g.Network.Pois.First(p => p.Type == PoiType.RentalLot).Id;
            var petrol = _g.Network.Pois.First(p => p.Type == PoiType.Petrol).Id;
            Assert.False(m.TryCarSwap(2, new CarSample { Position = BayOf(petrol), Speed = 0f }, 2, 5));
            Assert.False(m.TryCarSwap(2, new CarSample { Position = BayOf(rental), Speed = 5f }, 2, 5));
            Assert.False(m.TryCarSwap(2, new CarSample { Position = BayOf(rental), Speed = 0f }, 0, 0)); // over budget
            Assert.True(m.TryCarSwap(2, new CarSample { Position = BayOf(rental), Speed = 0f }, 2, 5));
        }

        [Fact]
        public void Conditions_VaryAcrossRounds()
        {
            var seen = new HashSet<(TimeOfDay, Weather)>();
            for (ulong seed = 1; seed < 40; seed++)
            {
                var m = new Match(_g, new MatchConfig(), seed);
                m.AddPlayer(1, "a"); m.AddPlayer(2, "b");
                m.StartMatch();
                seen.Add((m.TimeOfDay, m.Weather));
            }
            Assert.True(seen.Count >= 4, "expected a mix of times of day and weather");
            var fixedCfg = new Match(_g, new MatchConfig { VaryConditions = false }, 3);
            fixedCfg.AddPlayer(1, "a"); fixedCfg.AddPlayer(2, "b"); fixedCfg.StartMatch();
            Assert.Equal((TimeOfDay.Day, Weather.Clear), (fixedCfg.TimeOfDay, fixedCfg.Weather));
        }

        [Fact]
        public void Timeout_GoesToMarking_ThenDebrief_AndMatchRotatesMarks()
        {
            var m = NewMatch(2);
            ToDriving(m);
            Tick(m, m.Cfg.RoundSeconds + 1f);
            Assert.Equal(Phase.Marking, m.Phase);
            Tick(m, m.Cfg.MarkingSeconds + 1f);
            Assert.Equal(Phase.Debrief, m.Phase);
            Assert.Equal(RoundOutcome.Draw, m.Results.Last().Outcome); // nothing visited, nobody flagged
            Tick(m, m.Cfg.DebriefSeconds + 1f);
            Assert.Equal(Phase.MotorPool, m.Phase);
            Assert.Equal(2, m.MarkId);
            for (int i = 0; i < 6 && m.Phase != Phase.MatchOver; i++) m.Advance();
            Assert.Equal(Phase.MatchOver, m.Phase);
        }
    }
}
