using System;
using System.Collections.Generic;
using System.Linq;
using Tailed.Core.Identity;
using Tailed.Core.Roads;
using Tailed.Core.Util;

namespace Tailed.Core.Rules
{
    public enum Role : byte { None, Mark, Tail, Spotter }

    /// <summary>
    /// Round flow: ... Driving → Marking (Tails mark where they think the Mark stopped) → Debrief
    /// (the reveal: locations found vs Tails identified, winner crowned; then the replay).
    /// </summary>
    public enum Phase : byte { Lobby, MotorPool, Briefing, Pickup, Driving, Marking, Debrief, MatchOver }

    public enum RoundOutcome : byte { Draw, TailsWin, MarkWins }

    /// <summary>Per-round conditions (GD §10): night hurts plate legibility; rain shrinks everything.</summary>
    public enum TimeOfDay : byte { Day, Dusk, Night }
    public enum Weather : byte { Clear, Rain }

    [Serializable]
    public sealed class MatchConfig
    {
        public int Checkpoints = 5;
        public float DwellSeconds = 12f;
        public int FlagTokens = 5;
        public int PlateMatchesRequired = 5;
        public int TailBudget = 5;
        public float MotorPoolSeconds = 45f, BriefingSeconds = 40f, PickupSeconds = 6f, DebriefSeconds = 120f; // time to watch the replay and argue about it; the host can skip
        public float RoundSeconds = 16f * 60f;
        /// <summary>After the drive: time for the Tails to mark the Mark's stops on the map.</summary>
        public float MarkingSeconds = 75f;
        /// <summary>A mark within this distance of a stop's business identifies it.</summary>
        public float MarkRadius = 45f;
        /// <summary>How long the "errand" marker shows on the Mark's car at a real stop.</summary>
        public float StopMarkerSeconds = 6f;
        /// <summary>Randomise time of day / weather per round (off = always clear day).</summary>
        public bool VaryConditions = true;

        public float ChopShopSeconds = 5f, PlateSwapSeconds = 8f;
        public int PlateSwapsPerTail = 1;
        public float BayRadius = 4f;
    }

    public sealed class PlayerInfo
    {
        public int Id;
        public string Name;
        public bool Ready;
        public Role Role;
        /// <summary>External vehicle id in the traffic sim (-1 until spawned).</summary>
        public int VehicleId = -1;
        public VehicleIdentity Identity;
        public int PickedModel = 0, PickedColor = 3;
        public int Burns;
        public bool NeedsChopShop;
        public int PlateSwapsLeft;
        public int MatchScore;
        public float ServiceTimer; // chop shop / plate swap progress
        public int CarSwaps;
    }

    /// <summary>What the host knows about a player's car right now.</summary>
    public struct CarSample
    {
        public Vec2 Position;
        public float Speed;
        /// <summary>True if the car is on a road lane (not parked on a forecourt or kerb).</summary>
        public bool OnRoad;
    }

    public sealed class Dwell
    {
        public float Start, End;
        public Vec2 Position;
        /// <summary>POI id if this was a real checkpoint (index into route), else -1 (dry-cleaning stop).</summary>
        public int Poi = -1;
        public int RouteIndex = -1;
        public bool Completed;
    }

    public sealed class Pin
    {
        public int Id, Player;
        public Vec2 Position;
        public float Time;
    }

    public sealed class FlagResult
    {
        public bool Hit;
        public int BurnedPlayer = -1;
        public int TokensLeft;
        public string Message;
    }

    /// <summary>One of the Mark's route stops at the reveal: visited? found by the Tails' marks?</summary>
    public sealed class StopResult
    {
        public int RouteIndex, Poi;
        public bool Visited;
        /// <summary>The mark that found it (-1 none) and who placed it.</summary>
        public int PinId = -1, FoundBy = -1;
    }

    /// <summary>A flag the Mark filed (hidden until the reveal).</summary>
    public sealed class FlagRecord
    {
        public float Time;
        public int ModelId;
        public string Plate;
        /// <summary>The Tail it identified, or -1 (it was just traffic).</summary>
        public int Hit = -1;
    }

    public sealed class RoundResult
    {
        public int Round, Mark;
        public string Reason;
        public RoundOutcome Outcome;
        public int LocationsIdentified, TailsIdentified;
        public readonly List<StopResult> Stops = new List<StopResult>();
        public readonly List<FlagRecord> Flags = new List<FlagRecord>();
        public bool TailsWin => Outcome == RoundOutcome.TailsWin;
    }

    public enum ReplayEventType : byte { Checkpoint, FlagMiss, FlagHit, PlateSwap, CarSwap, ChopShop, TagHit, TagMiss }

    /// <summary>A moment for the debrief replay: when, who, where (and who it hit, for flags).</summary>
    public struct ReplayEvent
    {
        public float Time;
        public ReplayEventType Type;
        public int Player, Target;
        public Vec2 Position;
    }

    /// <summary>Events for the network layer to broadcast (to all, or filtered by role).</summary>
    public enum MatchEventType : byte
    {
        PhaseChanged, RolesAssigned, CheckpointReached, DwellStarted, DwellEnded, FlagResolved, Burned,
        PinsChanged, ChopShopDone, PlateSwapped, RoundOver, Notice,
    }

    public sealed class MatchEvent
    {
        public MatchEventType Type;
        public int Player = -1;
        public string Text;
        public MatchEvent(MatchEventType type, int player = -1, string text = null) { Type = type; Player = player; Text = text; }
    }

    /// <summary>
    /// Host-authoritative match state machine (GD §3–§8). Pure logic: the host feeds it player car
    /// samples, line-of-sight answers and player requests; it emits events for the network layer.
    /// </summary>
    public sealed class Match
    {
        public readonly MatchConfig Cfg;
        public readonly List<PlayerInfo> Players = new List<PlayerInfo>();
        public Phase Phase { get; private set; } = Phase.Lobby;
        public float PhaseTime { get; private set; }
        public float PhaseDuration { get; private set; }
        public int Round { get; private set; } = -1;
        public int MarkId { get; private set; } = -1;
        public TimeOfDay TimeOfDay { get; private set; }
        public Weather Weather { get; private set; }

        // Round state (host-only knowledge; the net layer decides who hears what).
        public readonly List<int> Route = new List<int>();        // POI ids in the Mark's order; last = safehouse
        public int NextCheckpoint { get; private set; }
        public int StartPoi { get; private set; } = -1;
        public int FlagTokens { get; private set; }
        public readonly List<Dwell> Dwells = new List<Dwell>();
        public readonly List<Pin> Pins = new List<Pin>();
        public readonly List<RoundResult> Results = new List<RoundResult>();
        /// <summary>Flags filed this round (results stay hidden until the reveal).</summary>
        public readonly List<FlagRecord> Flags = new List<FlagRecord>();
        /// <summary>Why the drive ended (shown at the reveal).</summary>
        public string DriveEndReason { get; private set; } = "";
        /// <summary>This round's notable moments, for the debrief replay (pins and stops carry their own times).</summary>
        public readonly List<ReplayEvent> Timeline = new List<ReplayEvent>();

        /// <summary>Record a replay moment (the host adds ones Match can't see, e.g. tags).</summary>
        public void Log(ReplayEventType type, int player, Vec2 pos, int target = -1) =>
            Timeline.Add(new ReplayEvent { Time = PhaseTime, Type = type, Player = player, Target = target, Position = pos });
        public Dwell CurrentDwell { get; private set; }

        public event Action<MatchEvent> Event;

        readonly RoadNetwork _net;
        readonly LaneGraph _g;
        Rng _rng;
        int _pinIds;
        float _stillTime;
        readonly List<int> _candidateCheckpoints = new List<int>();

        public Match(LaneGraph graph, MatchConfig cfg, ulong seed)
        {
            _g = graph;
            _net = graph.Network;
            Cfg = cfg;
            _rng = new Rng(seed ^ 0x5EEDUL);
        }

        public PlayerInfo Player(int id) => Players.FirstOrDefault(p => p.Id == id);
        public IEnumerable<PlayerInfo> Tails => Players.Where(p => p.Role == Role.Tail || p.Role == Role.Spotter);
        public PlayerInfo Mark => Player(MarkId);

        /// <summary>POIs the Mark must choose an order for (excludes the fixed final safehouse).</summary>
        public IReadOnlyList<int> CandidateCheckpoints => _candidateCheckpoints;
        public int Safehouse { get; private set; } = -1;

        // ---- lobby -------------------------------------------------------------------

        public PlayerInfo AddPlayer(int id, string name)
        {
            var p = new PlayerInfo { Id = id, Name = string.IsNullOrWhiteSpace(name) ? $"Player {id}" : name.Trim() };
            // Joining mid-match: pick a car if the motor pool is still open, otherwise watch the
            // traffic cameras as a Spotter until the next round (which assigns roles afresh).
            if (Phase == Phase.MotorPool) p.Role = Role.Tail;
            else if (Phase != Phase.Lobby && Phase != Phase.MatchOver) p.Role = Role.Spotter;
            Players.Add(p);
            Emit(MatchEventType.Notice, -1, $"{p.Name} joined");
            return p;
        }

        public void RemovePlayer(int id)
        {
            var p = Player(id);
            if (p == null) return;
            Players.Remove(p);
            Emit(MatchEventType.Notice, -1, $"{p.Name} left");
            if (Phase == Phase.Driving || Phase == Phase.Pickup || Phase == Phase.MotorPool || Phase == Phase.Briefing)
            {
                if (id == MarkId)
                {
                    bool drove = Phase == Phase.Driving;
                    EndDrive($"The Mark ({p.Name}) left the game");
                    if (!drove) EndRound(); // nothing to mark yet
                }
                else if (!Tails.Any()) { EndDrive("Every Tail left the game"); EndRound(); }
            }
            else if (Phase == Phase.Marking && !Tails.Any()) EndRound();
        }

        public bool CanStart => Phase == Phase.Lobby || Phase == Phase.MatchOver ? Players.Count >= 2 : false;

        public void StartMatch()
        {
            if (Players.Count < 2) throw new InvalidOperationException("need at least 2 players");
            foreach (var p in Players) p.MatchScore = 0;
            Results.Clear();
            Round = -1;
            NextRound();
        }

        void NextRound()
        {
            Round++;
            if (Round >= Players.Count) { SetPhase(Phase.MatchOver, 0f); return; }
            MarkId = Players[Round % Players.Count].Id;
            foreach (var p in Players)
            {
                p.Role = p.Id == MarkId ? Role.Mark : Role.Tail;
                p.Burns = 0;
                p.NeedsChopShop = false;
                p.PlateSwapsLeft = Cfg.PlateSwapsPerTail;
                p.ServiceTimer = 0f;
                p.CarSwaps = 0;
            }
            if (Cfg.VaryConditions)
            {
                TimeOfDay = (TimeOfDay)_rng.PickWeighted(new[] { 5f, 2f, 3f });
                Weather = _rng.NextDouble() < 0.3 ? Weather.Rain : Weather.Clear;
            }
            else { TimeOfDay = TimeOfDay.Day; Weather = Weather.Clear; }
            Route.Clear();
            Dwells.Clear();
            Pins.Clear();
            Flags.Clear();
            Timeline.Clear();
            CurrentDwell = null;
            NextCheckpoint = 0;
            FlagTokens = Cfg.FlagTokens;
            PickRoundPois();
            Emit(MatchEventType.RolesAssigned);
            SetPhase(Phase.MotorPool, Cfg.MotorPoolSeconds);
        }

        void PickRoundPois()
        {
            // Spread-out checkpoint candidates; a quiet Motel-ish POI as safehouse; a start POI.
            var eligible = _net.Pois.Where(p => p.IsCheckpointCandidate).Select(p => p.Id).ToList();
            Shuffle(eligible);
            _candidateCheckpoints.Clear();
            foreach (int id in eligible)
            {
                if (_candidateCheckpoints.Count >= Cfg.Checkpoints) break;
                var c = _g.Sites[id].Centre;
                if (_candidateCheckpoints.All(o => Vec2.Distance(_g.Sites[o].Centre, c) > 250f)) _candidateCheckpoints.Add(id);
            }
            var rest = eligible.Where(i => !_candidateCheckpoints.Contains(i)).ToList();
            int quiet = rest.FindIndex(i => _net.Pois[i].Type == PoiType.Motel || _net.Pois[i].Type == PoiType.Laundromat);
            Safehouse = quiet >= 0 ? rest[quiet] : rest.Count > 0 ? rest[0] : _candidateCheckpoints[_candidateCheckpoints.Count - 1];
            rest.Remove(Safehouse);
            _candidateCheckpoints.Remove(Safehouse);
            int far = rest.FindIndex(i => Vec2.Distance(_g.Sites[i].Centre, _g.Sites[Safehouse].Centre) > 500f);
            StartPoi = far >= 0 ? rest[far] : rest.Count > 0 ? rest[0] : _candidateCheckpoints[0];
        }

        void Shuffle(List<int> list)
        {
            for (int k = list.Count - 1; k > 0; k--) { int r = _rng.NextInt(k + 1); (list[k], list[r]) = (list[r], list[k]); }
        }

        // ---- motor pool & briefing ---------------------------------------------------

        public static int Cost(int modelId, int colorId) =>
            VehicleCatalog.Models[modelId].Cost + (colorId <= 3 ? 1 : 0); // neutral paint blends in: costs extra

        public bool PickVehicle(int playerId, int modelId, int colorId)
        {
            var p = Player(playerId);
            if (p == null || Phase != Phase.MotorPool || p.Role != Role.Tail) return false;
            if (modelId < 0 || modelId >= VehicleCatalog.Models.Length || VehicleCatalog.Models[modelId].NpcOnly || colorId < 0 || colorId >= VehicleCatalog.Colors.Length) return false;
            if (Cost(modelId, colorId) > Cfg.TailBudget) return false;
            p.PickedModel = modelId;
            p.PickedColor = colorId;
            return true;
        }

        public void SetReady(int playerId, bool ready)
        {
            var p = Player(playerId);
            if (p == null) return;
            p.Ready = ready;
            if (Phase == Phase.MotorPool && Players.Where(x => x.Role == Role.Tail).All(x => x.Ready)) SetPhase(Phase.Briefing, Cfg.BriefingSeconds);
        }

        /// <summary>The Mark's chosen checkpoint order (a permutation of the candidates).</summary>
        public bool SubmitRoute(int playerId, IList<int> order)
        {
            if (playerId != MarkId || Phase != Phase.Briefing) return false;
            if (order.Count != _candidateCheckpoints.Count || order.Except(_candidateCheckpoints).Any() || order.Distinct().Count() != order.Count) return false;
            Route.Clear();
            Route.AddRange(order);
            Route.Add(Safehouse);
            SetPhase(Phase.Pickup, Cfg.PickupSeconds);
            return true;
        }

        // ---- driving -----------------------------------------------------------------

        public void Tick(float dt, Func<int, CarSample?> sample)
        {
            PhaseTime += dt;
            switch (Phase)
            {
                case Phase.MotorPool:
                    if (PhaseTime >= PhaseDuration) SetPhase(Phase.Briefing, Cfg.BriefingSeconds);
                    break;
                case Phase.Briefing:
                    if (PhaseTime >= PhaseDuration)
                    {
                        // Out of time: keep the suggested order.
                        Route.Clear();
                        Route.AddRange(_candidateCheckpoints);
                        Route.Add(Safehouse);
                        SetPhase(Phase.Pickup, Cfg.PickupSeconds);
                    }
                    break;
                case Phase.Pickup:
                    if (PhaseTime >= PhaseDuration) SetPhase(Phase.Driving, Cfg.RoundSeconds);
                    break;
                case Phase.Driving:
                    TickDriving(dt, sample);
                    if (Phase == Phase.Driving && PhaseTime >= PhaseDuration) EndDrive("Time's up — the Mark didn't make it to the safehouse");
                    break;
                case Phase.Marking:
                    if (PhaseTime >= PhaseDuration) EndRound();
                    break;
                case Phase.Debrief:
                    if (PhaseTime >= PhaseDuration) NextRound();
                    break;
            }
        }

        void TickDriving(float dt, Func<int, CarSample?> sample)
        {
            var mark = Mark;
            var ms = mark != null ? sample(mark.Id) : null;
            if (ms.HasValue)
            {
                bool still = ms.Value.Speed < 0.6f;
                _stillTime = still ? _stillTime + dt : 0f;
                int bayPoi = PoiAtBay(ms.Value.Position);
                bool parked = bayPoi >= 0 || !ms.Value.OnRoad;

                if (CurrentDwell == null && still && parked && _stillTime > 1.5f)
                {
                    CurrentDwell = new Dwell { Start = PhaseTime, Position = ms.Value.Position, Poi = -1 };
                    if (NextCheckpoint < Route.Count && bayPoi == Route[NextCheckpoint])
                    {
                        CurrentDwell.Poi = bayPoi;
                        CurrentDwell.RouteIndex = NextCheckpoint;
                    }
                    Dwells.Add(CurrentDwell);
                    Emit(MatchEventType.DwellStarted, mark.Id);
                }
                if (CurrentDwell != null)
                {
                    if (!still || Vec2.Distance(ms.Value.Position, CurrentDwell.Position) > 6f)
                    {
                        CurrentDwell.End = PhaseTime;
                        // Too-short stops that aren't checkpoints don't count as dry-cleaning either.
                        if (!CurrentDwell.Completed && CurrentDwell.End - CurrentDwell.Start < Cfg.DwellSeconds) Dwells.Remove(CurrentDwell);
                        CurrentDwell = null;
                        Emit(MatchEventType.DwellEnded, mark.Id);
                    }
                    else
                    {
                        if (!CurrentDwell.Completed && PhaseTime - CurrentDwell.Start >= Cfg.DwellSeconds)
                        {
                            CurrentDwell.Completed = true;
                            if (CurrentDwell.RouteIndex >= 0)
                            {
                                NextCheckpoint++;
                                Log(ReplayEventType.Checkpoint, mark.Id, CurrentDwell.Position);
                                Emit(MatchEventType.CheckpointReached, mark.Id, _net.Pois[CurrentDwell.Poi].Name);
                                if (NextCheckpoint >= Route.Count) { EndDrive("The Mark reached the safehouse"); return; }
                            }
                        }
                    }
                }
            }

            // Services: chop shop (burned Tails) and plate swaps happen by waiting in a bay.
            foreach (var p in Players.Where(x => x.Role == Role.Tail))
            {
                var s = sample(p.Id);
                if (!s.HasValue) continue;
                int poi = PoiAtBay(s.Value.Position);
                bool stopped = s.Value.Speed < 0.6f;
                if (p.NeedsChopShop && stopped && poi >= 0 && _net.Pois[poi].Type == PoiType.ChopShop)
                {
                    p.ServiceTimer += dt;
                    if (p.ServiceTimer >= Cfg.ChopShopSeconds)
                    {
                        p.NeedsChopShop = false;
                        p.ServiceTimer = 0f;
                        Log(ReplayEventType.ChopShop, p.Id, s.Value.Position);
                        Emit(MatchEventType.ChopShopDone, p.Id);
                    }
                }
                else if (!p.NeedsChopShop && p.ServiceTimer > 0f && !(stopped && poi >= 0)) p.ServiceTimer = 0f;
            }


        }

        /// <summary>POI whose bay (car-centre spot) contains <paramref name="pos"/>, else -1.</summary>
        public int PoiAtBay(Vec2 pos)
        {
            foreach (var b in _g.Bays)
                if (Vec2.Distance(BayCentre(b), pos) < Cfg.BayRadius) return b.PoiId;
            return -1;
        }

        public static Vec2 BayCentre(ParkingBay b) => b.Position - b.Heading * 2.2f;

        /// <summary>Plate swap request (Tail stopped at a petrol station bay for PlateSwapSeconds).</summary>
        public bool TryPlateSwap(int playerId, CarSample s, float heldSeconds)
        {
            var p = Player(playerId);
            if (p == null || Phase != Phase.Driving || p.Role != Role.Tail || p.PlateSwapsLeft <= 0) return false;
            int poi = PoiAtBay(s.Position);
            if (poi < 0 || _net.Pois[poi].Type != PoiType.Petrol || s.Speed > 0.6f || heldSeconds < Cfg.PlateSwapSeconds) return false;
            p.PlateSwapsLeft--;
            Log(ReplayEventType.PlateSwap, p.Id, s.Position);
            Emit(MatchEventType.PlateSwapped, p.Id);
            return true;
        }

        /// <summary>Car swap at a rental lot bay (GD §5): a fresh car within budget, for a score penalty.</summary>
        public bool TryCarSwap(int playerId, CarSample s, int modelId, int colorId)
        {
            var p = Player(playerId);
            if (p == null || Phase != Phase.Driving || p.Role != Role.Tail || p.NeedsChopShop || s.Speed > 0.6f) return false;
            int poi = PoiAtBay(s.Position);
            if (poi < 0 || _net.Pois[poi].Type != PoiType.RentalLot) return false;
            if (modelId < 0 || modelId >= VehicleCatalog.Models.Length || VehicleCatalog.Models[modelId].NpcOnly || colorId < 0 || colorId >= VehicleCatalog.Colors.Length) return false;
            if (Cost(modelId, colorId) > Cfg.TailBudget) return false;
            p.CarSwaps++;
            Log(ReplayEventType.CarSwap, p.Id, s.Position);
            return true;
        }

        // ---- flags -------------------------------------------------------------------

        /// <summary>Flags are only allowed while the Mark is dwelling at a real checkpoint (GD §4).</summary>
        public bool FlagWindowOpen => Phase == Phase.Driving && CurrentDwell != null && CurrentDwell.RouteIndex >= 0;

        /// <summary>
        /// The Mark files an identification (model + plate, ≥5 of 7 characters). It costs a token and
        /// is recorded silently: whether it hit a Tail is only revealed at the end of the round.
        /// </summary>
        public FlagResult SubmitFlag(int playerId, int modelId, string plate)
        {
            if (playerId != MarkId) return new FlagResult { Message = "Only the Mark can flag", TokensLeft = FlagTokens };
            if (!FlagWindowOpen) return new FlagResult { Message = "Flags can only be filed while dwelling at a checkpoint", TokensLeft = FlagTokens };
            if (FlagTokens <= 0) return new FlagResult { Message = "No flag tokens left", TokensLeft = 0 };
            var matcher = new PlateMatcher(Cfg.PlateMatchesRequired);
            var target = Players.FirstOrDefault(p => (p.Role == Role.Tail || p.Role == Role.Spotter) && p.Identity.Plate != null &&
                                                     matcher.IsHit(modelId, plate, p.Identity.ModelId, p.Identity.Plate));
            FlagTokens--;
            Flags.Add(new FlagRecord { Time = PhaseTime, ModelId = modelId, Plate = plate, Hit = target?.Id ?? -1 });
            Log(target != null ? ReplayEventType.FlagHit : ReplayEventType.FlagMiss, playerId, CurrentDwell.Position, target?.Id ?? -1);
            var filed = new FlagResult { Hit = false, TokensLeft = FlagTokens, Message = $"Flag filed ({FlagTokens} left) — you'll find out at the end." };
            Emit(MatchEventType.FlagResolved, playerId, filed.Message);
            return filed;
        }

        /// <summary>The brief "errand" marker: on the Mark's car for the first seconds of a real stop.</summary>
        public bool StopMarkerActive => Phase == Phase.Driving && CurrentDwell != null && CurrentDwell.RouteIndex >= 0 &&
                                        PhaseTime - CurrentDwell.Start < Cfg.StopMarkerSeconds;

        // ---- pins --------------------------------------------------------------------

        /// <summary>How many marks the Tail team may place: one per stop on the Mark's route.</summary>
        public int MarksAllowed => Route.Count;

        /// <summary>
        /// Tails mark where they think the Mark stopped — only in the Marking phase, on one shared team
        /// map, up to <see cref="MarksAllowed"/> in total.
        /// </summary>
        public Pin AddPin(int playerId, Vec2 pos)
        {
            var p = Player(playerId);
            if (p == null || (p.Role != Role.Tail && p.Role != Role.Spotter) || Phase != Phase.Marking) return null;
            if (Pins.Count >= MarksAllowed) return null;
            var pin = new Pin { Id = ++_pinIds, Player = playerId, Position = pos, Time = PhaseTime };
            Pins.Add(pin);
            Emit(MatchEventType.PinsChanged, playerId);
            return pin;
        }

        /// <summary>Any Tail can move the team's marks around (it's one shared map).</summary>
        public bool RemovePin(int playerId, int pinId)
        {
            var p = Player(playerId);
            if (p == null || (p.Role != Role.Tail && p.Role != Role.Spotter) || Phase != Phase.Marking) return false;
            int n = Pins.RemoveAll(x => x.Id == pinId);
            if (n > 0) Emit(MatchEventType.PinsChanged, playerId);
            return n > 0;
        }

        // ---- scoring -----------------------------------------------------------------

        /// <summary>
        /// The reveal's numbers. Locations: stops the Mark actually completed that the Tails' marks
        /// found (a mark within <see cref="MatchConfig.MarkRadius"/> of the business; each mark finds at
        /// most one stop). Tails identified: distinct Tails the Mark's flags matched. More locations →
        /// Tails win; more Tails identified → the Mark wins; equal → draw.
        /// </summary>
        public RoundResult Score()
        {
            var r = new RoundResult { Round = Round, Mark = MarkId, Reason = DriveEndReason };
            var used = new HashSet<int>();
            for (int i = 0; i < Route.Count; i++)
            {
                var stop = new StopResult { RouteIndex = i, Poi = Route[i], Visited = i < NextCheckpoint };
                if (stop.Visited)
                {
                    var centre = _g.Sites[Route[i]].Centre;
                    var pin = Pins.Where(x => !used.Contains(x.Id) && Vec2.Distance(x.Position, centre) <= Cfg.MarkRadius)
                                  .OrderBy(x => Vec2.Distance(x.Position, centre)).FirstOrDefault();
                    if (pin != null) { used.Add(pin.Id); stop.PinId = pin.Id; stop.FoundBy = pin.Player; r.LocationsIdentified++; }
                }
                r.Stops.Add(stop);
            }
            r.Flags.AddRange(Flags);
            r.TailsIdentified = Flags.Where(f => f.Hit >= 0).Select(f => f.Hit).Distinct().Count();
            r.Outcome = r.LocationsIdentified > r.TailsIdentified ? RoundOutcome.TailsWin
                      : r.TailsIdentified > r.LocationsIdentified ? RoundOutcome.MarkWins : RoundOutcome.Draw;
            return r;
        }

        /// <summary>The drive is over (safehouse, time, someone left): Tails now mark the stops.</summary>
        void EndDrive(string reason)
        {
            if (Phase != Phase.Driving && Phase != Phase.Pickup && Phase != Phase.MotorPool && Phase != Phase.Briefing) return;
            DriveEndReason = reason;
            CurrentDwell = null;
            SetPhase(Phase.Marking, Cfg.MarkingSeconds);
            Emit(MatchEventType.Notice, -1, reason);
        }

        void EndRound()
        {
            if (Phase == Phase.Debrief || Phase == Phase.MatchOver) return;
            var r = Score();
            Results.Add(r);
            foreach (var p in Players)
            {
                bool mark = p.Id == MarkId;
                if ((mark && r.Outcome == RoundOutcome.MarkWins) || (!mark && r.Outcome == RoundOutcome.TailsWin)) p.MatchScore++;
            }
            SetPhase(Phase.Debrief, Cfg.DebriefSeconds);
            Emit(MatchEventType.RoundOver, -1, r.Reason);
        }

        /// <summary>Host override: skip to the next phase (dev / "everyone's ready").</summary>
        public void Advance()
        {
            switch (Phase)
            {
                case Phase.MotorPool: SetPhase(Phase.Briefing, Cfg.BriefingSeconds); break;
                case Phase.Briefing: PhaseTime = PhaseDuration; Tick(0f, _ => null); break;
                case Phase.Pickup: SetPhase(Phase.Driving, Cfg.RoundSeconds); break;
                case Phase.Driving: EndDrive("Round ended by the host"); break;
                case Phase.Marking: EndRound(); break;
                case Phase.Debrief: NextRound(); break;
            }
        }

        void SetPhase(Phase phase, float duration)
        {
            Phase = phase;
            PhaseTime = 0f;
            PhaseDuration = duration;
            if (phase == Phase.MotorPool) foreach (var p in Players) p.Ready = false;
            Emit(MatchEventType.PhaseChanged);
        }

        void Emit(MatchEventType t, int player = -1, string text = null) => Event?.Invoke(new MatchEvent(t, player, text));
    }
}
