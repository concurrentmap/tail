using System.Collections.Generic;
using System.Linq;
using Tailed.Core.Identity;
using Tailed.Core.Net;
using Tailed.Core.Roads;
using Tailed.Core.Rules;
using Tailed.Core.Traffic;
using Tailed.Core.Util;
using Tailed.Map;
using Tailed.Traffic;
using UnityEngine;

namespace Tailed.Net
{
    /// <summary>
    /// Host authority (architecture §7–§8): owns the <see cref="Match"/>, feeds it car samples and
    /// line-of-sight, replicates traffic per client with interest management, and sends each client
    /// only what its role may know. Player cars live in the traffic sim as external vehicles, in the
    /// same id space as NPCs.
    /// </summary>
    public sealed class HostGame : MonoBehaviour
    {
        public static HostGame Instance { get; private set; }
        public Match Match { get; private set; }
        public bool CheatBots;
        /// <summary>Short rounds for automated tests (-tailed-quick).</summary>
        public bool Quick;
        public int AutoStartPlayers;

        NetSession _net;
        TrafficRunner _runner;
        TrafficSim Sim => _runner.Sim;
        LaneGraph Graph => _runner.Town.Lanes;

        sealed class ClientState
        {
            public ulong Id;
            public readonly Dictionary<int, int> Known = new Dictionary<int, int>(); // vehicle id → identity version sent
            public float TagCooldown;
            public bool IsBot;
            /// <summary>Spotters have no car: stream traffic around the CCTV camera they're watching.</summary>
            public Vec2? SpotterCentre;
        }

        readonly Dictionary<ulong, ClientState> _clients = new Dictionary<ulong, ClientState>();
        readonly Dictionary<int, int> _identityVersion = new Dictionary<int, int>();
        readonly Dictionary<int, List<Vector3>> _trails = new Dictionary<int, List<Vector3>>();
        float _matchTick, _snapshotTimer, _teamTimer, _statusTimer, _lobbyTimer, _trailTimer, _decoyTimer;
        const float InterestRadius = 450f;

        void Awake() => Instance = this;

        void Start()
        {
            _net = NetSession.Instance;
            _runner = TrafficRunner.Instance;
            _net.HostReceived += OnMessage;
            _net.ClientConnected += id => { };
            _net.ClientDisconnected += OnDisconnected;
            Sim.Spawned += v => _identityVersion[v.Id] = (_identityVersion.TryGetValue(v.Id, out var n) ? n : 0) + 1;
            foreach (var v in Sim.Vehicles) if (v.Alive) _identityVersion[v.Id] = 1;
            NewMatch();
        }

        void OnDestroy()
        {
            if (_net != null) _net.HostReceived -= OnMessage;
            if (Match != null) Match.Event -= OnMatchEvent;
        }

        void NewMatch()
        {
            if (Match != null) Match.Event -= OnMatchEvent;
            var cfg = new MatchConfig();
            if (Quick) { cfg.VaryConditions = false; cfg.Checkpoints = 2; cfg.DwellSeconds = 6f; cfg.RoundSeconds = 360f; cfg.DebriefSeconds = 60f; cfg.MarkingSeconds = 20f; cfg.MotorPoolSeconds = 15f; cfg.BriefingSeconds = 10f; cfg.PickupSeconds = 4f; }
            Match = new Match(Graph, cfg, (ulong)_runner.Town.Seed * 31UL + 7UL);
            Match.Event += OnMatchEvent;
        }

        // ---- incoming ----------------------------------------------------------------

        void OnMessage(ulong sender, Msg type, ByteReader r)
        {
            int pid = (int)sender;
            var p = Match.Player(pid);
            switch (type)
            {
                case Msg.Hello:
                {
                    string name = r.Str();
                    bool bot = r.Bool();
                    _clients[sender] = new ClientState { Id = sender, IsBot = bot };
                    if (p == null) Match.AddPlayer(pid, name);
                    var w = _net.Begin(Msg.Welcome);
                    w.I32(pid);
                    w.I32(_runner.Town.Seed);
                    _net.SendToClient(sender, w);
                    SendLobby();
                    SendPhase();
                    // Joining mid-round as a Spotter: they still get the target description.
                    if (Match.Phase == Phase.Briefing || Match.Phase == Phase.Pickup || Match.Phase == Phase.Driving)
                        SendTargetReveal(new[] { sender });
                    if (AutoStartPlayers > 0 && Match.Players.Count >= AutoStartPlayers && Match.Phase == Phase.Lobby) Match.StartMatch();
                    break;
                }
                case Msg.StartMatch:
                    if (sender == _net.LocalId && Match.CanStart) Match.StartMatch();
                    break;
                case Msg.Advance:
                    if (sender == _net.LocalId) Match.Advance();
                    break;
                case Msg.Ready:
                    Match.SetReady(pid, r.Bool());
                    SendLobby();
                    break;
                case Msg.PickVehicle:
                {
                    bool ok = Match.PickVehicle(pid, r.U8(), r.U8());
                    if (!ok) Notice(sender, "Over budget — pick something cheaper");
                    SendLobby();
                    break;
                }
                case Msg.SubmitRoute:
                {
                    int n = r.U8();
                    var order = new List<int>();
                    for (int i = 0; i < n; i++) order.Add(r.U16());
                    if (!Match.SubmitRoute(pid, order)) Notice(sender, "Route rejected");
                    break;
                }
                case Msg.CarState:
                {
                    if (p == null || p.VehicleId < 0) break;
                    var pos = new Vector3(r.F32(), r.F32(), r.F32());
                    float yaw = r.U16() / 65535f * 360f;
                    float speed = r.F32();
                    var flags = (VehicleFlags)r.U8();
                    var fwd = Quaternion.Euler(0, yaw, 0) * Vector3.forward;
                    Sim.UpdateExternal(p.VehicleId, new Vec2(pos.x, pos.z), new Vec2(fwd.x, fwd.z), speed, flags);
                    break;
                }
                case Msg.Impact:
                    _runner.ReportImpact(r.U16(), r.F32());
                    break;
                case Msg.AddPin:
                {
                    var pin = Match.AddPin(pid, new Vec2(r.F32(), r.F32()));
                    if (pin == null) Notice(sender, p != null && p.NeedsChopShop ? "You're burned — get to a body shop first" : "Can't place a pin now");
                    break;
                }
                case Msg.RemovePin:
                    Match.RemovePin(pid, r.I32());
                    break;
                case Msg.Flag:
                {
                    int model = r.U8();
                    string plate = r.Str();
                    var res = Match.SubmitFlag(pid, model, plate);
                    var w = _net.Begin(Msg.FlagResult);
                    w.Bool(res.Hit);
                    w.U8((byte)res.TokensLeft);
                    w.Str(res.Message);
                    _net.SendToClient(sender, w);
                    if (res.Hit) SendLobby();
                    break;
                }
                case Msg.Tag:
                {
                    int vid = r.U16();
                    if (!_clients.TryGetValue(sender, out var cs) || p == null || p.Role != Role.Tail) break;
                    if (cs.TagCooldown > 0f) { Notice(sender, $"Tag recharging ({cs.TagCooldown:0}s)"); break; }
                    var mark = Match.Mark;
                    var tagAt = p.VehicleId >= 0 ? Sim.Vehicles[p.VehicleId].Position : default;
                    bool tagHit = mark != null && vid == mark.VehicleId;
                    Match.Log(tagHit ? ReplayEventType.TagHit : ReplayEventType.TagMiss, p.Id, tagAt);
                    if (tagHit)
                    {
                        var w = _net.Begin(Msg.Tagged);
                        w.U16((ushort)vid);
                        w.F32(15f);
                        w.Str(p.Name);
                        _net.SendToClients(TailClients(), w);
                        cs.TagCooldown = 20f;
                    }
                    else
                    {
                        Notice(sender, "Tag failed — that's not the Mark");
                        cs.TagCooldown = 20f;
                    }
                    break;
                }
                case Msg.Voice:
                    RouteVoice(p, r);
                    break;
                case Msg.SpotterView:
                    if (_clients.TryGetValue(sender, out var sc)) sc.SpotterCentre = new Vec2(r.F32(), r.F32());
                    break;
                case Msg.CarSwap:
                {
                    int model = r.U8(), color = r.U8();
                    var sample = Sample(p);
                    if (p == null || !sample.HasValue || !Match.TryCarSwap(pid, sample.Value, model, color))
                    {
                        Notice(sender, "Car swap only works stopped in a Rentals bay, within budget");
                        break;
                    }
                    Sim.Identities.Release(p.Identity);
                    SetIdentity(p, Sim.Identities.Create(model, color));
                    Notice(sender, $"Swapped into a {VehicleCatalog.Colors[color].Name} {VehicleCatalog.Models[model].FullName} ({PlateFormat.Display(p.Identity.Plate)})");
                    break;
                }
                case Msg.ServiceHold:
                {
                    float held = r.F32();
                    if (p == null || p.VehicleId < 0) break;
                    var v = Sim.Vehicles[p.VehicleId];
                    if (Match.TryPlateSwap(pid, Sample(p).GetValueOrDefault(), held))
                    {
                        var id = p.Identity;
                        id.Plate = Sim.Identities.NewPlate(id.Plate);
                        SetIdentity(p, id);
                        Notice(sender, $"New plates: {PlateFormat.Display(id.Plate)}");
                    }
                    break;
                }
            }
        }

        void OnDisconnected(ulong id)
        {
            _clients.Remove(id);
            var p = Match.Player((int)id);
            if (p != null && p.VehicleId >= 0) Sim.Remove(p.VehicleId);
            Match.RemovePlayer((int)id);
            SendLobby();
        }

        /// <summary>
        /// Voice routing (GD §7): team radio goes to the other Tails/Spotters; proximity voice reaches
        /// players whose cars are within earshot, which depends on both cars' windows.
        /// </summary>
        void RouteVoice(PlayerInfo speaker, ByteReader r)
        {
            if (speaker == null) return;
            byte flags = r.U8();
            ushort seq = r.U16();
            var payload = new byte[r.Remaining];
            for (int i = 0; i < payload.Length; i++) payload[i] = r.U8();
            bool radio = (flags & 1) != 0, speakerOpen = (flags & 2) != 0, proximity = (flags & 4) != 0;
            bool teamSpeaker = speaker.Role == Role.Tail || speaker.Role == Role.Spotter;

            void Forward(PlayerInfo to, byte outFlags, Vec2 pos, float gain)
            {
                var w = _net.Begin(Msg.VoiceOut);
                w.I32(speaker.Id); w.U8(outFlags); w.U16(seq);
                w.F32(pos.X); w.F32(pos.Y); w.F32(gain);
                w.Bytes(payload, payload.Length);
                _net.SendToClient((ulong)to.Id, w, reliable: false);
            }

            foreach (var to in Match.Players)
            {
                if (to.Id == speaker.Id || !_clients.ContainsKey((ulong)to.Id)) continue;
                if (radio && teamSpeaker && (to.Role == Role.Tail || to.Role == Role.Spotter))
                    Forward(to, 1, default, 1f);
                if (proximity && speaker.VehicleId >= 0 && to.VehicleId >= 0)
                {
                    var a = Sim.Vehicles[speaker.VehicleId];
                    var b = Sim.Vehicles[to.VehicleId];
                    bool listenerOpen = (b.Flags & VehicleFlags.WindowsOpen) != 0;
                    float range = speakerOpen && listenerOpen ? 16f : speakerOpen || listenerOpen ? 9f : 3f;
                    float d = Vec2.Distance(a.Position, b.Position);
                    if (d < range) Forward(to, 0, a.Position, Mathf.Clamp01(1.2f - d / range));
                }
            }
        }

        // ---- match events ------------------------------------------------------------

        void OnMatchEvent(MatchEvent e)
        {
            switch (e.Type)
            {
                case MatchEventType.PhaseChanged:
                    OnPhase();
                    SendPhase();
                    SendLobby();
                    break;
                case MatchEventType.RolesAssigned:
                    SendLobby();
                    break;
                case MatchEventType.Notice:
                    NoticeAll(e.Text);
                    break;
                case MatchEventType.CheckpointReached:
                    Notice(Match.MarkId, $"Checkpoint done: {e.Text}");
                    SendRouteStatus();
                    break;
                case MatchEventType.Burned:
                {
                    var p = Match.Player(e.Player);
                    NoticeAll($"{p.Name} was BURNED by the Mark!");
                    if (e.Text == "spotter")
                    {
                        // Out of the car: they watch the traffic cameras now.
                        if (p.VehicleId >= 0) { Sim.Remove(p.VehicleId); p.VehicleId = -1; }
                        Notice(Match.MarkId, "Heads up: a camera operator is watching the traffic cameras.");
                    }
                    Notice(p.Id, e.Text == "spotter"
                        ? "Burned twice — you're a Spotter now. Watch the traffic cameras."
                        : "You've been made! Get to a Body Shop for a new car.");
                    SendLobby();
                    break;
                }
                case MatchEventType.ChopShopDone:
                {
                    var p = Match.Player(e.Player);
                    var ids = Sim.Identities;
                    ids.Release(p.Identity);
                    SetIdentity(p, ids.RandomCar());
                    Notice(p.Id, $"New car: {VehicleCatalog.Colors[p.Identity.ColorId].Name} {VehicleCatalog.Models[p.Identity.ModelId].FullName}, plate {PlateFormat.Display(p.Identity.Plate)}");
                    SendLobby();
                    break;
                }
                case MatchEventType.PinsChanged:
                    SendPins();
                    break;
                case MatchEventType.RoundOver:
                    NoticeAll(e.Text);
                    SendDebrief();
                    break;
            }
        }

        void OnPhase()
        {
            switch (Match.Phase)
            {
                case Phase.MotorPool:
                    // New round: clear old cars and trails.
                    foreach (var p in Match.Players)
                        if (p.VehicleId >= 0) { Sim.Remove(p.VehicleId); p.VehicleId = -1; }
                    _trails.Clear();
                    break;
                case Phase.Briefing:
                {
                    var w = _net.Begin(Msg.Briefing);
                    w.U8((byte)Match.CandidateCheckpoints.Count);
                    foreach (int poi in Match.CandidateCheckpoints) w.U16((ushort)poi);
                    w.U16((ushort)Match.Safehouse);
                    w.U16((ushort)Match.StartPoi);
                    SendTo(Match.MarkId, w);
                    // The Mark's car is decided now so Tails can study it during the briefing.
                    AssignIdentities();
                    SendTargetReveal(TailClients());
                    break;
                }
                case Phase.Marking:
                {
                    var w = _net.Begin(Msg.MarkingInfo);
                    w.U8((byte)Match.MarksAllowed);
                    _net.SendToClients(TailClients(), w);
                    break;
                }
                case Phase.Pickup:
                    {
                        var env = _net.Begin(Msg.Environment);
                        env.U8((byte)Match.TimeOfDay);
                        env.U8((byte)Match.Weather);
                        _net.SendToClients(AllClients, env);
                    }
                    SpawnCars();
                    {
                        var mark = Match.Mark;
                        var w = _net.Begin(Msg.PickupReveal);
                        w.U8(mark.Identity.ModelId);
                        w.U8(mark.Identity.ColorId);
                        w.U16((ushort)Match.StartPoi);
                        _net.SendToClients(TailClients(), w);
                        if (CheatBots)
                        {
                            var c = _net.Begin(Msg.BotCheat);
                            c.U16((ushort)mark.VehicleId);
                            _net.SendToClients(_clients.Values.Where(x => x.IsBot).Select(x => x.Id), c);
                        }
                    }
                    SendRouteStatus();
                    break;
            }
        }

        int _identitiesRound = -1;

        /// <summary>This round's cars: a random everyday car for the Mark, the motor-pool pick for each Tail.</summary>
        void AssignIdentities()
        {
            if (_identitiesRound == Match.Round) return;
            _identitiesRound = Match.Round;
            var ids = Sim.Identities;
            foreach (var p in Match.Players)
            {
                if (p.Identity.Plate != null) ids.Release(p.Identity);
                p.Identity = p.Role == Role.Mark ? ids.RandomCar() : ids.Create(p.PickedModel, p.PickedColor);
            }
        }

        /// <summary>Target overview for Tails: the Mark's full description (car, paint, plate, driver, hat).</summary>
        void SendTargetReveal(IEnumerable<ulong> to)
        {
            var mark = Match.Mark;
            if (mark == null || mark.Identity.Plate == null) return;
            var w = _net.Begin(Msg.TargetReveal);
            TrafficCodec.WriteIdentity(w, mark.Identity);
            _net.SendToClients(to, w);
        }

        void SpawnCars()
        {
            var start = Graph.Sites[Match.StartPoi];
            var mark = Match.Mark;
            AssignIdentities();
            foreach (var p in Match.Players)
                if (p.VehicleId >= 0) { Sim.Remove(p.VehicleId); p.VehicleId = -1; }
            // Mark: in the start POI's first bay. Tails: on lanes 200–400 m away, spread out.
            var bay = Graph.Bays[start.Bays[0]];
            var markPos = Match.BayCentre(bay);
            Place(mark, markPos, bay.Heading);
            var rng = new Rng((ulong)(Match.Round + 1) * 977UL + (ulong)_runner.Town.Seed);
            var used = new List<Vec2>();
            foreach (var p in Match.Players.Where(x => x.Role == Role.Tail))
            {
                for (int tries = 0; tries < 500; tries++)
                {
                    var lane = Graph.Lanes[rng.NextInt(Graph.Lanes.Count)];
                    if (lane.Length < 50f || lane.Index != 0) continue;
                    var pos = lane.PointAt(lane.Length * 0.4f);
                    float d = Vec2.Distance(pos, markPos);
                    if (d < 200f || d > 420f || used.Any(u => Vec2.Distance(u, pos) < 60f)) continue;
                    used.Add(pos);
                    Place(p, pos, lane.Direction);
                    break;
                }
            }
        }

        void Place(PlayerInfo p, Vec2 pos, Vec2 heading)
        {
            var v = Sim.AddExternal(p.Identity);
            p.VehicleId = v.Id;
            Sim.UpdateExternal(v.Id, pos, heading, 0f, VehicleFlags.None);
            _identityVersion[v.Id] = (_identityVersion.TryGetValue(v.Id, out var n) ? n : 0) + 1;
            if ((ulong)p.Id == _net.LocalId) _runner.SetLocallyDriven(v.Id, true);
            else _runner.SetLocallyDriven(v.Id, false);
            _trails[p.Id] = new List<Vector3>();
            var w = _net.Begin(Msg.Assign);
            w.U16((ushort)v.Id);
            TrafficCodec.WriteIdentity(w, p.Identity);
            w.Bool(true); // teleport
            w.F32(pos.X); w.F32(pos.Y);
            w.F32(Mathf.Atan2(heading.X, heading.Y) * Mathf.Rad2Deg);
            SendTo(p.Id, w);
        }

        void SetIdentity(PlayerInfo p, VehicleIdentity id)
        {
            p.Identity = id;
            var v = Sim.Vehicles[p.VehicleId];
            v.Identity = id;
            v.Model = VehicleCatalog.Models[id.ModelId];
            v.Length = v.Model.Length;
            _identityVersion[v.Id]++;
            if (_runner.TryGetView(v.Id, out var view)) view.Bind(v.Id, id);
            var w = _net.Begin(Msg.Assign);
            w.U16((ushort)v.Id);
            TrafficCodec.WriteIdentity(w, id);
            w.Bool(false);
            SendTo(p.Id, w);
        }

        // ---- tick --------------------------------------------------------------------

        void Update()
        {
            if (Sim == null) return;
            float dt = Time.deltaTime;
            foreach (var c in _clients.Values) c.TagCooldown -= dt;

            _matchTick += dt;
            if (_matchTick >= 0.1f)
            {
                Match.Tick(_matchTick, id => Sample(Match.Player(id)));
                _matchTick = 0f;
            }

            // Everyone's car is an observer: nothing pops in or out near any player.
            _runner.Observers.Clear();
            foreach (var p in Match.Players)
                if (p.VehicleId >= 0 && _runner.TryGetView(p.VehicleId, out var view)) _runner.Observers.Add(view.transform);
            if (Vehicles.PlayerCar.Local != null) _runner.Observers.Add(Vehicles.PlayerCar.Local.transform);

            if ((_snapshotTimer -= dt) <= 0f) { _snapshotTimer = 0.1f; ReplicateTraffic(); }
            if ((_teamTimer -= dt) <= 0f) { _teamTimer = 0.5f; SendTeamPositions(); }
            if ((_statusTimer -= dt) <= 0f) { _statusTimer = 0.25f; SendRouteStatus(); SendPhase(); }
            if ((_lobbyTimer -= dt) <= 0f) { _lobbyTimer = 2f; SendLobby(); }
            if (Match.Phase == Phase.Driving && (_trailTimer -= dt) <= 0f) { _trailTimer = 0.5f; RecordTrails(); }
            if (Match.Phase == Phase.Driving && (_decoyTimer -= dt) <= 0f) { _decoyTimer = 15f; SendDecoys(); }
        }

        CarSample? Sample(PlayerInfo p)
        {
            if (p == null || p.VehicleId < 0) return null;
            var v = Sim.Vehicles[p.VehicleId];
            if (!v.Alive) return null;
            bool onRoad = Sim.Index.Project(v.Position, v.Heading, out _);
            return new CarSample { Position = v.Position, Speed = v.Speed, OnRoad = onRoad };
        }

        void RecordTrails()
        {
            foreach (var p in Match.Players)
            {
                if (p.VehicleId < 0 || !_trails.TryGetValue(p.Id, out var trail)) continue;
                var v = Sim.Vehicles[p.VehicleId];
                trail.Add(new Vector3(v.Position.X, Match.PhaseTime, v.Position.Y));
            }
        }

        /// <summary>
        /// Traffic Director decoys (GD §9): nudge a couple of NPCs near the Mark towards the POI the Mark is
        /// heading for, so "that car has been behind me for three turns" is not proof of anything.
        /// </summary>
        void SendDecoys()
        {
            var m = Match.Mark;
            if (m == null || m.VehicleId < 0 || Match.NextCheckpoint >= Match.Route.Count) return;
            var mp = Sim.Vehicles[m.VehicleId].Position;
            int target = Match.Route[Match.NextCheckpoint];
            int sent = 0;
            foreach (var v in Sim.Vehicles)
            {
                if (sent >= 2) break;
                if (!v.Alive || v.IsExternal || v.Mode != VehicleMode.Lane || v.DestBay >= 0) continue;
                float d = Vec2.Distance(v.Position, mp);
                if (d < 30f || d > 160f) continue;
                if (Sim.SendToPoi(v.Id, target)) sent++;
            }
        }

        // ---- outgoing ----------------------------------------------------------------

        IEnumerable<ulong> AllClients => _clients.Keys;
        IEnumerable<ulong> TailClients() => Match.Players.Where(p => p.Role == Role.Tail || p.Role == Role.Spotter).Select(p => (ulong)p.Id).Where(_clients.ContainsKey);

        void SendTo(int playerId, ByteWriter w)
        {
            if (_clients.ContainsKey((ulong)playerId)) _net.SendToClient((ulong)playerId, w);
        }

        void Notice(ulong client, string text) => Notice((int)client, text);

        void Notice(int playerId, string text)
        {
            var w = _net.Begin(Msg.Notice);
            w.Str(text);
            SendTo(playerId, w);
        }

        void NoticeAll(string text)
        {
            var w = _net.Begin(Msg.Notice);
            w.Str(text);
            _net.SendToClients(AllClients, w);
        }

        void SendLobby()
        {
            var w = _net.Begin(Msg.Lobby);
            w.U8((byte)Match.Players.Count);
            foreach (var p in Match.Players)
            {
                w.I32(p.Id);
                w.Str(p.Name);
                w.Bool(p.Ready);
                w.U8((byte)p.Role);
                w.I32(p.MatchScore);
                w.U8((byte)p.Burns);
                w.Bool(p.NeedsChopShop);
                w.U8((byte)p.PickedModel);
                w.U8((byte)p.PickedColor);
            }
            _net.SendToClients(AllClients, w);
        }

        void SendPhase()
        {
            var w = _net.Begin(Msg.PhaseInfo);
            w.U8((byte)Match.Phase);
            w.F32(Match.PhaseDuration);
            w.F32(Match.PhaseTime);
            w.I32(Match.Round);
            w.I32(Match.MarkId);
            w.F32(Sim.Time);
            _net.SendToClients(AllClients, w, reliable: false);
        }

        void SendRouteStatus()
        {
            var m = Match.Mark;
            if (m == null || Match.Route.Count == 0) return;
            var w = _net.Begin(Msg.RouteStatus);
            w.U8((byte)Match.Route.Count);
            foreach (int poi in Match.Route) w.U16((ushort)poi);
            w.U8((byte)Match.NextCheckpoint);
            w.U8((byte)Match.FlagTokens);
            var d = Match.CurrentDwell;
            w.Bool(d != null);
            w.Bool(d != null && d.RouteIndex >= 0);
            w.F32(d != null ? Mathf.Clamp01((Match.PhaseTime - d.Start) / Match.Cfg.DwellSeconds) : 0f);
            w.Bool(Match.FlagWindowOpen);
            w.Bool(Match.StopMarkerActive); // the Mark's car shows the errand marker
            SendTo(Match.MarkId, w);
        }

        void SendTeamPositions()
        {
            var tails = Match.Players.Where(p => (p.Role == Role.Tail || p.Role == Role.Spotter) && p.VehicleId >= 0).ToList();
            if (tails.Count == 0) return;
            var w = _net.Begin(Msg.TeamPositions);
            w.U8((byte)tails.Count);
            foreach (var p in tails)
            {
                var v = Sim.Vehicles[p.VehicleId];
                w.I32(p.Id);
                w.F32(v.Position.X);
                w.F32(v.Position.Y);
                w.F32(Mathf.Atan2(v.Heading.X, v.Heading.Y) * Mathf.Rad2Deg);
                w.F32(p.ServiceTimer);
            }
            _net.SendToClients(TailClients(), w, reliable: false);
        }

        void SendPins()
        {
            var w = _net.Begin(Msg.Pins);
            w.U16((ushort)Match.Pins.Count);
            foreach (var pin in Match.Pins)
            {
                w.I32(pin.Id);
                w.I32(pin.Player);
                w.F32(pin.Position.X);
                w.F32(pin.Position.Y);
            }
            _net.SendToClients(TailClients(), w);
        }

        void SendDebrief()
        {
            var r = Match.Results.Last();
            var w = _net.Begin(Msg.Debrief);
            // The reveal: outcome and counts, every route stop (found? by whom), every flag (who it was).
            w.Str(r.Reason);
            w.U8((byte)r.Outcome);
            w.U8((byte)r.LocationsIdentified);
            w.U8((byte)r.TailsIdentified);
            w.U8((byte)r.Stops.Count);
            foreach (var st in r.Stops) { w.U16((ushort)st.Poi); w.Bool(st.Visited); w.I32(st.PinId); w.I32(st.FoundBy); }
            w.U8((byte)r.Flags.Count);
            foreach (var f in r.Flags) { w.F32(f.Time); w.U8((byte)f.ModelId); w.Str(f.Plate ?? ""); w.I32(f.Hit); }
            // The replay: every stop the Mark made (real and fake), the team's marks, the timeline, trails.
            var dwells = Match.Dwells.Where(d => d.Completed || d.RouteIndex >= 0).ToList();
            w.U8((byte)dwells.Count);
            foreach (var d in dwells) { w.F32(d.Position.X); w.F32(d.Position.Y); w.I16((short)d.RouteIndex); w.F32(d.Start); w.F32(d.End > 0f ? d.End : d.Start + Match.Cfg.DwellSeconds); }
            w.U16((ushort)Match.Pins.Count);
            foreach (var pin in Match.Pins) { w.I32(pin.Id); w.I32(pin.Player); w.F32(pin.Position.X); w.F32(pin.Position.Y); }
            // Timeline (flags, checkpoints, swaps, tags) for the replay.
            w.U16((ushort)Match.Timeline.Count);
            foreach (var e in Match.Timeline) { w.F32(e.Time); w.U8((byte)e.Type); w.I32(e.Player); w.I32(e.Target); w.F32(e.Position.X); w.F32(e.Position.Y); }
            // Trails: thin them out if a long round would overflow one message (6 B per point).
            int total = _trails.Values.Sum(t => t.Count);
            int budget = (NetSession.MaxMessageBytes - w.Length - 2048) / 6;
            int stride = Mathf.Max(1, Mathf.CeilToInt(total / (float)Mathf.Max(budget, 1)));
            w.U8((byte)_trails.Count);
            foreach (var kv in _trails)
            {
                w.I32(kv.Key);
                int n = Mathf.Min((kv.Value.Count + stride - 1) / stride, 65535);
                w.U16((ushort)n);
                for (int k = 0; k < n; k++)
                {
                    var t = kv.Value[Mathf.Min(k * stride, kv.Value.Count - 1)];
                    w.U16((ushort)Mathf.Clamp((t.x + TrafficCodec.PosOffset) * 10f, 0, 65535));
                    w.U16((ushort)Mathf.Clamp((t.z + TrafficCodec.PosOffset) * 10f, 0, 65535));
                    w.U16((ushort)Mathf.Clamp(t.y * 10f, 0, 65535));
                }
            }
            _net.SendToClients(AllClients, w);
        }

        void ReplicateTraffic()
        {
            foreach (var c in _clients.Values)
            {
                if (c.Id == _net.LocalId) continue; // the host renders the sim directly
                var p = Match.Player((int)c.Id);
                if (p == null) continue;
                Vec2 centre;
                if (p.VehicleId >= 0) centre = Sim.Vehicles[p.VehicleId].Position;
                else if (c.SpotterCentre.HasValue) centre = c.SpotterCentre.Value;
                else continue;

                var visible = new List<SimVehicle>();
                foreach (var v in Sim.Vehicles)
                {
                    if (!v.Alive || v.Id == p.VehicleId) continue;
                    if (Vec2.Distance(v.Position, centre) > InterestRadius) continue;
                    visible.Add(v);
                }

                // Despawns for vehicles that left interest (or died).
                var gone = c.Known.Keys.Where(id => !visible.Any(v => v.Id == id)).ToList();
                if (gone.Count > 0)
                {
                    var w = _net.Begin(Msg.TrafficDespawn);
                    w.U16((ushort)gone.Count);
                    foreach (int id in gone) { w.U16((ushort)id); c.Known.Remove(id); }
                    _net.SendToClient(c.Id, w);
                }

                // Spawns (identity) for new or changed vehicles.
                var fresh = visible.Where(v => !c.Known.TryGetValue(v.Id, out int ver) || ver != _identityVersion.GetValueOrDefault(v.Id)).ToList();
                if (fresh.Count > 0)
                {
                    var w = _net.Begin(Msg.TrafficSpawn);
                    w.U16((ushort)fresh.Count);
                    foreach (var v in fresh)
                    {
                        w.U16((ushort)v.Id);
                        TrafficCodec.WriteIdentity(w, v.Identity);
                        c.Known[v.Id] = _identityVersion.GetValueOrDefault(v.Id);
                    }
                    _net.SendToClient(c.Id, w);
                }

                // State snapshot, unreliable, split to stay under the MTU.
                for (int start = 0; start < visible.Count; start += 110)
                {
                    var w = _net.Begin(Msg.TrafficSnapshot);
                    w.F32(Sim.Time);
                    int n = Mathf.Min(110, visible.Count - start);
                    w.U16((ushort)n);
                    for (int i = start; i < start + n; i++) TrafficCodec.Write(w, TrafficCodec.FromSim(visible[i]));
                    _net.SendToClient(c.Id, w, reliable: false);
                }
            }
        }
    }
}
