using System;
using System.Collections.Generic;
using System.Linq;
using Tailed.Cockpit;
using Tailed.Core.Identity;
using Tailed.Core.Net;
using Tailed.Core.Rules;
using Tailed.Core.Traffic;
using Tailed.Core.Util;
using Tailed.Map;
using Tailed.Traffic;
using Tailed.Vehicles;
using UnityEngine;

namespace Tailed.Net
{
    public sealed class LobbyPlayer
    {
        public int Id;
        public string Name;
        public bool Ready;
        public Role Role;
        public int Score, Burns;
        public bool NeedsChopShop;
        public int PickedModel, PickedColor;
    }

    public sealed class DebriefData
    {
        public string Reason;
        public RoundOutcome Outcome;
        public int Locations, TailsIdentified;
        /// <summary>Each route stop in order: visited, and the team mark that found it (and who placed it).</summary>
        public readonly List<(int poi, bool visited, int pinId, int foundBy)> Stops = new List<(int, bool, int, int)>();
        /// <summary>Each flag the Mark filed: which car they described, and who it turned out to be (-1 traffic).</summary>
        public readonly List<(float time, int model, string plate, int hit)> Flags = new List<(float, int, string, int)>();
        public readonly List<(Vector2 pos, int routeIndex, float start, float end)> Dwells = new List<(Vector2, int, float, float)>();
        public readonly List<(int id, int player, Vector2 pos)> Pins = new List<(int, int, Vector2)>();
        public readonly List<(float time, ReplayEventType type, int player, int target, Vector2 pos)> Events = new List<(float, ReplayEventType, int, int, Vector2)>();
        public readonly Dictionary<int, List<Vector3>> Trails = new Dictionary<int, List<Vector3>>(); // x, t, z
    }

    /// <summary>
    /// Everything a player's machine knows (and only that): phase, role, own car, and role-filtered
    /// information from the host. Also owns the local car and uploads its pose. Runs on the host too.
    /// </summary>
    public sealed class ClientGame : MonoBehaviour
    {
        public static ClientGame Instance { get; private set; }

        public string PlayerName = "Player";
        public bool IsBot;
        public int MyId { get; private set; } = -1;
        public bool Connected => MyId >= 0;
        public Phase Phase { get; private set; } = Phase.Lobby;
        public float PhaseDuration { get; private set; }
        public float PhaseTime { get; private set; }
        public int Round { get; private set; }
        public int MarkId { get; private set; } = -1;
        public readonly List<LobbyPlayer> Players = new List<LobbyPlayer>();
        public LobbyPlayer Me => Players.FirstOrDefault(p => p.Id == MyId);
        public Role MyRole => Me?.Role ?? Role.None;

        // Mark-only
        public readonly List<int> Candidates = new List<int>();
        public int Safehouse = -1, StartPoi = -1;
        public readonly List<int> Route = new List<int>();
        public int NextCheckpoint, FlagTokens;
        public bool Dwelling, DwellIsCheckpoint, FlagWindowOpen;
        public float DwellProgress;
        public string LastFlagResult;

        // Tail-only
        public int RevealModel = -1, RevealColor = -1;
        /// <summary>Tails: the Mark's full description for this round (target overview), or null.</summary>
        public VehicleIdentity? Target;
        /// <summary>Tails, Marking phase: how many marks the team may place.</summary>
        public int MarksAllowed;
        /// <summary>Where this player drove during the round (1 Hz): a memory aid on the marking map.</summary>
        public readonly List<Vector2> MyTrail = new List<Vector2>();
        float _trailTimer;
        /// <summary>Bumped whenever <see cref="Target"/> changes (UI rebuilds its card).</summary>
        public int TargetVersion;
        public float RevealTime;
        public readonly Dictionary<int, (Vector2 pos, float heading, float service)> Team = new Dictionary<int, (Vector2, float, float)>();
        public readonly List<(int id, int player, Vector2 pos)> Pins = new List<(int, int, Vector2)>();
        public int TaggedVehicle = -1;
        public float TaggedUntil;
        public int BotCheatMarkVehicle = -1;

        public DebriefData Debrief;
        public int MyVehicleId { get; private set; } = -1;
        public event Action<string> NoticeReceived;
        public event Action PhaseChangedEvent;
        public readonly List<(string text, float time)> Notices = new List<(string, float)>();

        NetSession _net;
        float _sendTimer;
        float _hostTime, _hostTimeLocal;

        void Awake() => Instance = this;

        void Start()
        {
            _net = NetSession.Instance;
            _net.ClientReceived += OnMessage;
            _net.ConnectedToHost += SayHello;
            _net.DisconnectedFromHost += () => _lostHost = true; // handled in Update, not inside the transport callback
            if (_net.IsHost) SayHello();
        }

        void OnDestroy()
        {
            if (_net != null) _net.ClientReceived -= OnMessage;
            DestroyLocalCar();
        }

        static string JoinPort()
        {
            var a = Tailed.Game.GameBootstrap.Instance.JoinAddress ?? "";
            int colon = a.LastIndexOf(':');
            return colon > 0 ? a.Substring(colon + 1) : NetSession.DefaultPort.ToString();
        }

        bool _lostHost;
        float _joinWait;
        const float JoinTimeout = 12f;

        /// <summary>Drop back to the menu when the host goes away or never answers.</summary>
        void WatchConnection()
        {
            if (_net == null || _net.IsHost || Tailed.Game.GameBootstrap.Instance == null) return;
            if (_lostHost)
            {
                Tailed.Game.GameBootstrap.Instance.ReturnToMenu(Connected ? "Lost connection to the host (they left, or the network dropped)."
                                                              : $"Couldn't reach the host at {Tailed.Game.GameBootstrap.Instance.JoinAddress}.");
                return;
            }
            if (!Connected && (_joinWait += Time.unscaledDeltaTime) > JoinTimeout)
                Tailed.Game.GameBootstrap.Instance.ReturnToMenu($"No answer from {Tailed.Game.GameBootstrap.Instance.JoinAddress} after {JoinTimeout:0} s. " +
                                                    $"Check the address, that the host is in the lobby, and that their firewall allows UDP port {JoinPort()}.");
        }

        public void SayHello()
        {
            var w = _net.Begin(Msg.Hello);
            w.Str(PlayerName);
            w.Bool(IsBot);
            _net.SendToHost(w);
        }

        /// <summary>Estimated current host sim time.</summary>
        public float HostTime => _hostTime + (Time.time - _hostTimeLocal);

        void OnMessage(ulong sender, Msg type, ByteReader r)
        {
            switch (type)
            {
                case Msg.Welcome:
                {
                    MyId = r.I32();
                    int seed = r.I32();
                    var town = TownBuilder.Instance;
                    if (town.Seed != seed) { town.Seed = seed; town.Build(); }
                    break;
                }
                case Msg.Lobby:
                {
                    int n = r.U8();
                    var prevRole = MyRole;
                    Players.Clear();
                    for (int i = 0; i < n; i++)
                        Players.Add(new LobbyPlayer
                        {
                            Id = r.I32(), Name = r.Str(), Ready = r.Bool(), Role = (Role)r.U8(), Score = r.I32(), Burns = r.U8(),
                            NeedsChopShop = r.Bool(), PickedModel = r.U8(), PickedColor = r.U8(),
                        });
                    if (MyRole == Role.Spotter && prevRole != Role.Spotter) BecomeSpotter();
                    if (MyRole != Role.Spotter && Game.SpotterCams.Instance != null && Game.SpotterCams.Instance.Active) Game.SpotterCams.Instance.SetActive(false);
                    break;
                }
                case Msg.PhaseInfo:
                {
                    var phase = (Phase)r.U8();
                    PhaseDuration = r.F32();
                    PhaseTime = r.F32();
                    Round = r.I32();
                    MarkId = r.I32();
                    _hostTime = r.F32();
                    _hostTimeLocal = Time.time;
                    var runner = TrafficRunner.Instance;
                    if (!_net.IsHost && runner != null && runner.Sim != null) runner.Sim.SyncTime(HostTime);
                    if (phase != Phase) { Phase = phase; OnPhaseChanged(); }
                    break;
                }
                case Msg.Briefing:
                {
                    Candidates.Clear();
                    int n = r.U8();
                    for (int i = 0; i < n; i++) Candidates.Add(r.U16());
                    Safehouse = r.U16();
                    StartPoi = r.U16();
                    break;
                }
                case Msg.RouteStatus:
                {
                    Route.Clear();
                    int n = r.U8();
                    for (int i = 0; i < n; i++) Route.Add(r.U16());
                    NextCheckpoint = r.U8();
                    FlagTokens = r.U8();
                    Dwelling = r.Bool();
                    DwellIsCheckpoint = r.Bool();
                    DwellProgress = r.F32();
                    FlagWindowOpen = r.Bool();
                    if (PlayerCar.Local != null) PlayerCar.Local.Errand = r.Bool() && MyRole == Role.Mark;
                    break;
                }
                case Msg.Assign:
                {
                    int vid = r.U16();
                    var id = TrafficCodec.ReadIdentity(r);
                    bool teleport = r.Bool();
                    MyVehicleId = vid;
                    if (teleport)
                    {
                        var pos = new Vector3(r.F32(), 0.6f, r.F32());
                        var rot = Quaternion.Euler(0, r.F32(), 0);
                        SpawnLocalCar(vid, id, pos, rot);
                    }
                    else if (PlayerCar.Local != null) PlayerCar.Local.SetIdentity(id);
                    break;
                }
                case Msg.MarkingInfo:
                    MarksAllowed = r.U8();
                    break;
                case Msg.TargetReveal:
                    Target = TrafficCodec.ReadIdentity(r);
                    TargetVersion++;
                    break;
                case Msg.PickupReveal:
                    RevealModel = r.U8();
                    RevealColor = r.U8();
                    StartPoi = r.U16();
                    RevealTime = Time.time;
                    break;
                case Msg.TeamPositions:
                {
                    Team.Clear();
                    int n = r.U8();
                    for (int i = 0; i < n; i++) Team[r.I32()] = (new Vector2(r.F32(), r.F32()), r.F32(), r.F32());
                    break;
                }
                case Msg.Pins:
                {
                    Pins.Clear();
                    int n = r.U16();
                    for (int i = 0; i < n; i++) Pins.Add((r.I32(), r.I32(), new Vector2(r.F32(), r.F32())));
                    break;
                }
                case Msg.FlagResult:
                {
                    bool hit = r.Bool();
                    FlagTokens = r.U8();
                    LastFlagResult = r.Str();
                    AddNotice(LastFlagResult);
                    break;
                }
                case Msg.Notice:
                    AddNotice(r.Str());
                    break;
                case Msg.Tagged:
                    TaggedVehicle = r.U16();
                    TaggedUntil = Time.time + r.F32();
                    AddNotice($"{r.Str()} tagged the Mark!");
                    break;
                case Msg.VoiceOut:
                    Audio.VoiceChat.Instance?.Receive(r);
                    break;
                case Msg.Environment:
                    Game.WorldEnvironment.Instance?.Apply((TimeOfDay)r.U8(), (Weather)r.U8());
                    break;
                case Msg.BotCheat:
                    BotCheatMarkVehicle = r.U16();
                    break;
                case Msg.Debrief:
                    Debrief = ReadDebrief(r);
                    break;
                case Msg.TrafficSpawn:
                case Msg.TrafficDespawn:
                case Msg.TrafficSnapshot:
                    TrafficReplica.Instance?.Handle(type, r);
                    break;
            }
        }

        static DebriefData ReadDebrief(ByteReader r)
        {
            var d = new DebriefData { Reason = r.Str(), Outcome = (RoundOutcome)r.U8(), Locations = r.U8(), TailsIdentified = r.U8() };
            int n = r.U8();
            for (int i = 0; i < n; i++) d.Stops.Add((r.U16(), r.Bool(), r.I32(), r.I32()));
            n = r.U8();
            for (int i = 0; i < n; i++) d.Flags.Add((r.F32(), r.U8(), r.Str(), r.I32()));
            n = r.U8();
            for (int i = 0; i < n; i++) d.Dwells.Add((new Vector2(r.F32(), r.F32()), r.I16(), r.F32(), r.F32()));
            n = r.U16();
            for (int i = 0; i < n; i++) d.Pins.Add((r.I32(), r.I32(), new Vector2(r.F32(), r.F32())));
            n = r.U16();
            for (int i = 0; i < n; i++)
            {
                float t = r.F32(); var type = (ReplayEventType)r.U8(); int who = r.I32(), target = r.I32();
                d.Events.Add((t, type, who, target, new Vector2(r.F32(), r.F32())));
            }
            n = r.U8();
            for (int i = 0; i < n; i++)
            {
                int pid = r.I32();
                int m = r.U16();
                var list = new List<Vector3>(m);
                for (int k = 0; k < m; k++)
                    list.Add(new Vector3(r.U16() / 10f - TrafficCodec.PosOffset, 0, r.U16() / 10f - TrafficCodec.PosOffset) + new Vector3(0, r.U16() / 10f, 0));
                d.Trails[pid] = list;
            }
            return d;
        }

        void OnPhaseChanged()
        {
            if (Phase == Phase.MotorPool)
            {
                Debrief = null;
                Route.Clear();
                Candidates.Clear();
                Pins.Clear();
                RevealModel = -1;
                Target = null;
                TargetVersion++;
                MyTrail.Clear();
                BotCheatMarkVehicle = -1;
                DestroyLocalCar();
            }
            if (Phase == Phase.Lobby || Phase == Phase.MatchOver) DestroyLocalCar();
            if (Phase != Phase.Driving && Game.SpotterCams.Instance != null && Game.SpotterCams.Instance.Active) Game.SpotterCams.Instance.SetActive(false);
            if (Phase == Phase.MotorPool) Game.WorldEnvironment.Instance?.Apply(TimeOfDay.Day, Weather.Clear);
            PhaseChangedEvent?.Invoke();
        }

        void BecomeSpotter()
        {
            DestroyLocalCar();
            Game.SpotterCams.Instance?.SetActive(true);
        }

        void AddNotice(string text)
        {
            Notices.Add((text, Time.time));
            if (Notices.Count > 8) Notices.RemoveAt(0);
            NoticeReceived?.Invoke(text);
            Debug.Log($"[Notice] {text}");
        }

        // ---- local car ---------------------------------------------------------------

        void SpawnLocalCar(int vehicleId, VehicleIdentity id, Vector3 pos, Quaternion rot)
        {
            DestroyLocalCar();
            var car = PlayerCar.Create(id, pos, rot, local: true);
            if (_net.IsHost)
            {
                car.SimId = vehicleId; // the host's own car updates the sim directly
                TrafficRunner.Instance.SetLocallyDriven(vehicleId, true);
            }
            car.gameObject.AddComponent<CarInput>().enabled = !IsBot;
            car.gameObject.AddComponent<DriveAssist>();
            if (IsBot) car.gameObject.AddComponent<Autopilot>();
            car.gameObject.AddComponent<MirrorSystem>().Init(car);
            PlateReadout.Create(car).transform.SetParent(car.transform, false);
            if (CameraDirector.Instance != null) { CameraDirector.Instance.Target = car; CameraDirector.Instance.SetFree(false); }
            PlayerCar.ImpactNotifier = OnImpact;
        }

        void DestroyLocalCar()
        {
            if (PlayerCar.Local != null) Destroy(PlayerCar.Local.gameObject);
            MyVehicleId = -1;
        }

        void OnImpact(Collision c)
        {
            if (_net.IsHost) return; // host reports straight into its own sim
            var view = c.collider.GetComponentInParent<VehicleView>();
            if (view == null) return;
            var w = _net.Begin(Msg.Impact);
            w.U16((ushort)view.VehicleId);
            w.F32(c.impulse.magnitude);
            _net.SendToHost(w);
        }

        void Update()
        {
            WatchConnection();
            if (_net == null) return;
            var car = PlayerCar.Local;
            if (car != null && Phase == Phase.Driving && (_trailTimer -= Time.deltaTime) <= 0f)
            {
                _trailTimer = 1f;
                MyTrail.Add(new Vector2(car.transform.position.x, car.transform.position.z));
            }
            if (car != null && !_net.IsHost && (_sendTimer -= Time.deltaTime) <= 0f)
            {
                _sendTimer = 0.05f;
                var w = _net.Begin(Msg.CarState);
                var p = car.transform.position;
                w.F32(p.x); w.F32(p.y); w.F32(p.z);
                w.U16((ushort)(Mathf.Repeat(car.transform.eulerAngles.y, 360f) / 360f * 65535f));
                w.F32(car.GetComponent<Rigidbody>().linearVelocity.magnitude);
                w.U8((byte)car.Flags);
                _net.SendToHost(w, reliable: false);
            }
        }

        // ---- requests ----------------------------------------------------------------

        public void Send(Msg type, Action<ByteWriter> fill = null, bool reliable = true)
        {
            var w = _net.Begin(type);
            fill?.Invoke(w);
            _net.SendToHost(w, reliable);
        }

        public void SetReady(bool ready) => Send(Msg.Ready, w => w.Bool(ready));
        public void PickVehicle(int model, int color) => Send(Msg.PickVehicle, w => { w.U8((byte)model); w.U8((byte)color); });
        public void SubmitRoute(IList<int> order) => Send(Msg.SubmitRoute, w => { w.U8((byte)order.Count); foreach (int p in order) w.U16((ushort)p); });
        public void AddPin(Vector2 pos) => Send(Msg.AddPin, w => { w.F32(pos.x); w.F32(pos.y); });
        public void RemovePin(int id) => Send(Msg.RemovePin, w => w.I32(id));
        public void Flag(int model, string plate) => Send(Msg.Flag, w => { w.U8((byte)model); w.Str(PlateFormat.Normalize(plate)); });
        public void Tag(int vehicleId) => Send(Msg.Tag, w => w.U16((ushort)vehicleId));
        public void ServiceHold(float seconds) => Send(Msg.ServiceHold, w => w.F32(seconds));
        public void CarSwap(int model, int color) => Send(Msg.CarSwap, w => { w.U8((byte)model); w.U8((byte)color); });
        public void StartMatch() => Send(Msg.StartMatch);
        public void Advance() => Send(Msg.Advance);
    }
}
