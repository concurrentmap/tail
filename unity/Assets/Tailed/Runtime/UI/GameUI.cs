using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using Tailed.Cockpit;
using Tailed.Core.Identity;
using Tailed.Core.Roads;
using Tailed.Core.Rules;
using Tailed.Game;
using Tailed.Map;
using Tailed.Net;
using Tailed.Traffic;
using Tailed.Vehicles;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.InputSystem;
using UnityEngine.UI;

namespace Tailed.UI
{
    /// <summary>All game screens and the driving HUD, rebuilt when the phase/role/overlay state changes.</summary>
    public sealed class GameUI : MonoBehaviour
    {
        Canvas _canvas;
        RectTransform _screen, _overlay;
        string _key;
        float _refresh;

        // Dynamic widgets (valid for the current screen).
        Text _timer, _status, _notices, _help, _banner, _center;
        Image _progressFill;
        RectTransform _progress, _arrow, _tagMarker;
        MapView _minimap, _map;

        // Overlays.
        bool _mapOpen, _flagOpen, _notepadOpen, _helpOpen = true, _swapOpen;
        /// <summary>Paper map out on your lap (M): drive with it, but it covers the lower view.</summary>
        bool _mapOut;
        GameObject _lapPaper;
        Text _lapInfo, _compass;
        float _lapRefresh;
        string _lapStreet;
        int _swapModel = 2, _swapColor = 5;
        Text _cctv, _voice, _navLabel, _flagStatus;
        List<Vector2> _navPoints;
        string _street;
        float _navTimer;
        InputField _notepad, _plateField;
        string _notes = "";
        int _flagModel = 0;
        readonly List<Texture2D> _snapshots = new List<Texture2D>();
        RawImage _enlarged;
        float _plateHold;
        bool _plateSent;

        // Menu / briefing state.
        InputField _nameField, _addrField;
        readonly List<int> _routeDraft = new List<int>();

        static ClientGame C => ClientGame.Instance;
        static GameBootstrap Boot => GameBootstrap.Instance;

        static GameUI _instance;
        public static string NavText { get; private set; } = "";
        /// <summary>Dev / bridge: toggle overlays for screenshots.</summary>
        public static string DevToggleMap() { _instance._mapOpen = !_instance._mapOpen; return $"map {_instance._mapOpen}"; }
        public static string DevToggleLapMap() { _instance._mapOut = !_instance._mapOut; return $"lap map {_instance._mapOut}"; }
        /// <summary>Dev / bridge: run the reveal show again from the top (seconds in, optional via overloads).</summary>
        public static string DevRevealRestart()
        {
            var g = _instance;
            if (g._reveal == null) return "no reveal";
            g._reveal.SetActive(true);
            g._revealDone = false;
            g._revealFired = -1;
            g._revealStart = Time.time;
            return $"reveal restarted ({g._revealSteps.Count} steps, {g._revealSteps.Sum(x => x.Duration):0.0}s)";
        }

        public static string DevReplayPause() { _instance._replayPlaying = !_instance._replayPlaying; return $"replay playing {_instance._replayPlaying}"; }
        public static string DevReplayAtHalf() => DevReplayAt(0.5f);
        public static string DevReplayAt75() => DevReplayAt(0.75f);
        public static string DevReplayAtEnd() => DevReplayAt(1f);
        static string DevReplayAt(float f) { _instance._replayPlaying = false; _instance._replayT = f * _instance._replayMax; _instance._refresh = 0f; return $"replay at {_instance._replayT:0}s of {_instance._replayMax:0}s"; }
        public static string DevLapZoom3() { _instance._lapZoom = 3f; _instance._mapOut = true; return "lap map 3x"; }
        public static string DevToggleFlag() { _instance._flagOpen = !_instance._flagOpen; return $"flag {_instance._flagOpen}"; }
        public static string DevToggleSwap() { _instance._swapOpen = !_instance._swapOpen; return $"swap {_instance._swapOpen}"; }

        void Start()
        {
            _instance = this;
            _canvas = UiKit.Canvas("GameUI", 10);
            _screen = UiKit.Stretch(_canvas.transform, "Screen");
            _overlay = UiKit.Stretch(_canvas.transform, "Overlay");
        }

        // ---- state → screen ----------------------------------------------------------

        string Key()
        {
            var mode = Boot != null ? Boot.Current : GameBootstrap.Mode.Menu;
            if (mode == GameBootstrap.Mode.Menu || mode == GameBootstrap.Mode.Offline) return $"{mode}|{_helpOpen}|{_mapOut}|{Boot?.LastError}";
            if (C == null || !C.Connected) return "connecting";
            return $"{C.Phase}|{C.MyRole}|{C.TargetVersion}|{_mapOut}|{_mapOpen}|{_flagOpen}|{_notepadOpen}|{_helpOpen}|{(C.Debrief != null)}|{C.Me?.NeedsChopShop}|{_swapOpen}|{_swapModel}|{_swapColor}";
        }

        void Update()
        {
            if (_canvas == null) return;
            HandleKeys();
            var key = Key();
            if (key != _key)
            {
                _key = key;
                Rebuild();
            }
            UpdateDynamic();
        }

        void Rebuild()
        {
            UiKit.Clear(_screen);
            UiKit.Clear(_overlay);
            _timer = _status = _notices = _help = _banner = _center = _flagStatus = _navLabel = _voice = _cctv = null;
            _progress = _arrow = _tagMarker = null;
            _progressFill = null;
            _minimap = _map = null;
            _lapPaper = null; _lapInfo = _compass = null;
            _targetCard = null;
            _notepad = _plateField = null;
            _enlarged = null;

            var mode = Boot != null ? Boot.Current : GameBootstrap.Mode.Menu;
            bool blocking = false;
            if (mode == GameBootstrap.Mode.Menu) { BuildMenu(); blocking = true; }
            else if (mode == GameBootstrap.Mode.Offline) BuildOffline();
            else if (C == null || !C.Connected)
            {
                string to = Boot.Current == GameBootstrap.Mode.Client ? $" to {Boot.JoinAddress}" : "";
                _center = UiKit.LabelAt(_screen, $"Connecting{to}...", 48, Color.white, new Vector2(0.5f, 0.5f), new Vector2(0, 60), new Vector2(1200, 100), TextAnchor.MiddleCenter);
                UiKit.ButtonAt(_screen, "CANCEL", new Vector2(0.5f, 0.5f), new Vector2(0, -60), new Vector2(300, 76), () => Boot.ReturnToMenu(), UiKit.MarkRed);
                blocking = true;
            }
            else
            {
                switch (C.Phase)
                {
                    case Phase.Lobby: BuildLobby(); blocking = true; break;
                    case Phase.MotorPool: BuildMotorPool(); blocking = true; break;
                    case Phase.Briefing: BuildBriefing(); blocking = true; break;
                    case Phase.Pickup: BuildPickup(); break;
                    case Phase.Driving: BuildDriving(); break;
                    case Phase.Marking: BuildMarking(); blocking = true; break;
                    case Phase.Debrief: BuildDebrief(); blocking = true; break;
                    case Phase.MatchOver: BuildMatchOver(); blocking = true; break;
                }
                if (C.Phase == Phase.Driving || C.Phase == Phase.Pickup)
                {
                    if (_mapOpen) { BuildMapOverlay(); blocking = true; }
                    if (_flagOpen) { BuildFlagForm(); blocking = true; }
                    if (_notepadOpen) { BuildNotepad(); blocking = true; }
                    if (_swapOpen) { BuildSwapPanel(); blocking = true; }
                }
            }
            if (C == null || (C.Phase != Phase.Driving && C.Phase != Phase.Pickup)) { BayBeacon.Hide(); RouteBeacons.Hide(); }
            // Common: notices feed + help line.
            _notices = UiKit.LabelAt(_overlay, "", 26, Color.white, new Vector2(0f, 0f), new Vector2(30, 30), new Vector2(900, 260), TextAnchor.LowerLeft);
            SetBlocking(blocking);
        }

        void SetBlocking(bool blocking)
        {
            CarInput.Blocked = blocking;
            CameraDirector.LookBlocked = blocking;
            if (blocking) Cursor.lockState = CursorLockMode.None;
            Cursor.visible = Cursor.lockState != CursorLockMode.Locked;
        }

        // ---- menu / lobby ------------------------------------------------------------

        void BuildMenu()
        {
            bool error = !string.IsNullOrEmpty(Boot.LastError);
            var bg = UiKit.Panel(_screen, "Menu", UiKit.Ink, new Vector2(0.5f, 0.5f), Vector2.zero, new Vector2(900, error ? 980 : 880));
            UiKit.LabelAt(bg.transform, "TAILED", 110, UiKit.Accent, new Vector2(0.5f, 1f), new Vector2(0, -40), new Vector2(800, 130), TextAnchor.UpperCenter);
            UiKit.LabelAt(bg.transform, "One Mark. Many Tails. Drive like an NPC.", 28, Color.white, new Vector2(0.5f, 1f), new Vector2(0, -170), new Vector2(800, 40), TextAnchor.UpperCenter);
            UiKit.LabelAt(bg.transform, "Your name", 24, Color.white, new Vector2(0.5f, 1f), new Vector2(-230, -240), new Vector2(300, 34));
            _nameField = UiKit.Input(bg.transform, "Name", new Vector2(0.5f, 1f), new Vector2(0, -300), new Vector2(760, 64));
            _nameField.text = Boot.PlayerName;
            if (error)
                UiKit.LabelAt(bg.transform, Boot.LastError, 24, new Color(1f, 0.55f, 0.5f), new Vector2(0.5f, 0f), new Vector2(0, 16), new Vector2(840, 80), TextAnchor.MiddleCenter);
            UiKit.ButtonAt(bg.transform, "HOST GAME", new Vector2(0.5f, 1f), new Vector2(0, -400), new Vector2(760, 80), () =>
            {
                Boot.LastError = null;
                Boot.PlayerName = _nameField.text;
                Boot.StartHost(NetSession.DefaultPort);
            }, UiKit.Good);
            _addrField = UiKit.Input(bg.transform, "Host address (e.g. 192.168.1.20)", new Vector2(0.5f, 1f), new Vector2(-150, -500), new Vector2(460, 70));
            _addrField.text = "127.0.0.1";
            UiKit.ButtonAt(bg.transform, "JOIN", new Vector2(0.5f, 1f), new Vector2(230, -500), new Vector2(290, 70), () =>
            {
                Boot.LastError = null;
                Boot.PlayerName = _nameField.text;
                Boot.StartClient(_addrField.text);
            }, UiKit.TailBlue);
            UiKit.ButtonAt(bg.transform, "Drive around (offline)", new Vector2(0.5f, 1f), new Vector2(0, -600), new Vector2(760, 64), () => Boot.StartOffline(), UiKit.Button, 26);
            UiKit.ButtonAt(bg.transform, Optics.HardcoreMirrors ? "Mirror plates: REALISTIC (reversed text)" : "Mirror plates: READABLE (text un-reversed)",
                new Vector2(0.5f, 1f), new Vector2(0, -700), new Vector2(760, 64), () => { Optics.HardcoreMirrors = !Optics.HardcoreMirrors; _key = null; },
                Optics.HardcoreMirrors ? UiKit.MarkRed : UiKit.Button, 24);
            UiKit.ButtonAt(bg.transform, Optics.PlateReadouts ? "Plate readouts: ON (read plates you look at)" : "Plate readouts: OFF (pixels only)",
                new Vector2(0.5f, 1f), new Vector2(0, -780), new Vector2(760, 64), () => { Optics.PlateReadouts = !Optics.PlateReadouts; _key = null; },
                UiKit.Button, 24);
        }

        void BuildOffline()
        {
            _help = UiKit.LabelAt(_screen, "", 22, Color.white, new Vector2(1f, 1f), new Vector2(-30, -30), new Vector2(620, 400), TextAnchor.UpperRight);
            BuildLapMap();
        }

        void BuildLobby()
        {
            var bg = UiKit.Panel(_screen, "Lobby", UiKit.Ink, new Vector2(0.5f, 0.5f), Vector2.zero, new Vector2(1000, 760));
            UiKit.LabelAt(bg.transform, "LOBBY", 70, UiKit.Accent, new Vector2(0.5f, 1f), new Vector2(0, -30), new Vector2(900, 90), TextAnchor.UpperCenter);
            _status = UiKit.LabelAt(bg.transform, "", 30, Color.white, new Vector2(0.5f, 1f), new Vector2(0, -140), new Vector2(900, 400), TextAnchor.UpperLeft);
            string ips = string.Join("  ", LocalAddresses());
            UiKit.LabelAt(bg.transform, NetSession.Instance.IsHost ? $"Friends join: {ips} (port {NetSession.DefaultPort})" : "Waiting for the host to start...", 24, UiKit.Accent,
                new Vector2(0.5f, 0f), new Vector2(0, 150), new Vector2(900, 60), TextAnchor.MiddleCenter);
            if (NetSession.Instance.IsHost)
                UiKit.ButtonAt(bg.transform, "START MATCH", new Vector2(0.5f, 0f), new Vector2(-110, 70), new Vector2(560, 80), () => C.StartMatch(), UiKit.Good);
            UiKit.ButtonAt(bg.transform, NetSession.Instance.IsHost ? "CLOSE" : "LEAVE", new Vector2(0.5f, 0f), NetSession.Instance.IsHost ? new Vector2(300, 70) : new Vector2(0, 70),
                new Vector2(220, 80), () => Boot.ReturnToMenu(), UiKit.MarkRed, 28);
        }

        static IEnumerable<string> LocalAddresses()
        {
            try
            {
                return Dns.GetHostEntry(Dns.GetHostName()).AddressList.Where(a => a.AddressFamily == AddressFamily.InterNetwork).Select(a => a.ToString()).Take(3);
            }
            catch { return new[] { "127.0.0.1" }; }
        }

        // ---- motor pool --------------------------------------------------------------

        void BuildMotorPool()
        {
            var bg = UiKit.Panel(_screen, "MotorPool", UiKit.Ink, new Vector2(0.5f, 0.5f), Vector2.zero, new Vector2(1600, 900));
            _timer = UiKit.LabelAt(bg.transform, "", 40, Color.white, new Vector2(1f, 1f), new Vector2(-30, -30), new Vector2(300, 60), TextAnchor.UpperRight);
            if (C.MyRole == Role.Mark)
            {
                UiKit.LabelAt(bg.transform, "YOU ARE THE MARK", 80, UiKit.MarkRed, new Vector2(0.5f, 0.5f), new Vector2(0, 150), new Vector2(1400, 120), TextAnchor.MiddleCenter);
                UiKit.LabelAt(bg.transform, "Soon you'll plan a route of errands across town and drive it.\nThe others will try to follow you and map every stop.\nAt each stop, check your mirrors, then flag suspicious cars by model + plate to burn them.\nNobody knows which car you are. Blend in.",
                    32, Color.white, new Vector2(0.5f, 0.5f), new Vector2(0, -80), new Vector2(1400, 300), TextAnchor.MiddleCenter);
                return;
            }
            UiKit.LabelAt(bg.transform, "MOTOR POOL — pick your tailing car", 54, UiKit.TailBlue, new Vector2(0.5f, 1f), new Vector2(0, -30), new Vector2(1300, 70), TextAnchor.UpperCenter);
            UiKit.LabelAt(bg.transform, "Common cars and neutral paint blend in — and cost more. Budget: 5.", 28, Color.white, new Vector2(0.5f, 1f), new Vector2(0, -105), new Vector2(1300, 40), TextAnchor.UpperCenter);
            var me = C.Me;
            for (int m = 0; m < VehicleCatalog.Models.Length; m++)
            {
                var model = VehicleCatalog.Models[m];
                if (model.NpcOnly) continue; // buses and lorries are never players
                int id = m;
                var pos = new Vector2(-600 + (m % 4) * 400, -230 - (m / 4) * 150);
                bool sel = me != null && me.PickedModel == m;
                var mb = UiKit.ButtonAt(bg.transform, $"{model.FullName} · cost {model.Cost}", new Vector2(0.5f, 1f), pos, new Vector2(370, 140),
                    () => C.PickVehicle(id, C.Me?.PickedColor ?? 4), sel ? UiKit.TailBlue : UiKit.Button, 24);
                Thumb(mb, id);
            }
            for (int c = 0; c < VehicleCatalog.Colors.Length; c++)
            {
                var col = VehicleCatalog.Colors[c];
                int id = c;
                var b = UiKit.ButtonAt(bg.transform, c <= 3 ? "+1" : "", new Vector2(0.5f, 1f), new Vector2(-660 + c * 120, -560), new Vector2(100, 90),
                    () => C.PickVehicle(C.Me?.PickedModel ?? 0, id), new Color(col.R, col.G, col.B), 26);
                if (me != null && me.PickedColor == c) b.GetComponent<Outline>().effectColor = UiKit.Accent;
                if (me != null && me.PickedColor == c) b.GetComponent<Outline>().effectDistance = new Vector2(6, -6);
            }
            _status = UiKit.LabelAt(bg.transform, "", 34, Color.white, new Vector2(0.5f, 0f), new Vector2(-250, 150), new Vector2(1000, 120), TextAnchor.MiddleLeft);
            UiKit.ButtonAt(bg.transform, me != null && me.Ready ? "READY ✓" : "READY", new Vector2(0.5f, 0f), new Vector2(520, 110), new Vector2(360, 100),
                () => { C.SetReady(!(C.Me?.Ready ?? false)); _key = null; }, me != null && me.Ready ? UiKit.Good : UiKit.Button);
        }

        // ---- briefing / pickup -------------------------------------------------------

        void BuildBriefing()
        {
            var bg = UiKit.Panel(_screen, "Briefing", UiKit.Ink, new Vector2(0.5f, 0.5f), Vector2.zero, new Vector2(1800, 1000));
            _timer = UiKit.LabelAt(bg.transform, "", 40, Color.white, new Vector2(1f, 1f), new Vector2(-30, -30), new Vector2(300, 60), TextAnchor.UpperRight);
            _map = MapView.Create(bg.transform, new Vector2(0f, 0.5f), new Vector2(40, 0), 900);
            var town = TownBuilder.Instance;
            if (C.MyRole == Role.Mark)
            {
                UiKit.LabelAt(bg.transform, "PLAN YOUR ROUTE", 54, UiKit.MarkRed, new Vector2(1f, 1f), new Vector2(-40, -100), new Vector2(820, 70), TextAnchor.UpperLeft);
                UiKit.LabelAt(bg.transform, "Click the stops in the order you'll visit them.\nThe safehouse is always last.\nTip: an order that doubles back is harder to follow.\nNo sat-nav: you get each stop's name and address, and find your own way.\nAt a real stop a gold marker flashes over your car for a few seconds (fake stops show none).\nWin: flag more Tails (model + plate, at your stops) than the stops they find.", 26, Color.white,
                    new Vector2(1f, 1f), new Vector2(-40, -180), new Vector2(820, 280));
                _status = UiKit.LabelAt(bg.transform, "", 28, Color.white, new Vector2(1f, 1f), new Vector2(-40, -470), new Vector2(820, 380));
                UiKit.ButtonAt(bg.transform, "Reset", new Vector2(1f, 0f), new Vector2(-640, 70), new Vector2(240, 80), () => { _routeDraft.Clear(); _key = null; });
                UiKit.ButtonAt(bg.transform, "CONFIRM ROUTE", new Vector2(1f, 0f), new Vector2(-250, 70), new Vector2(420, 80), () =>
                {
                    var order = _routeDraft.Concat(C.Candidates.Where(x => !_routeDraft.Contains(x))).ToList();
                    C.SubmitRoute(order);
                }, UiKit.Good);
                _map.Clicked += (world, button) =>
                {
                    int best = -1; float bestD = 60f;
                    foreach (int poi in C.Candidates)
                    {
                        var c = town.Lanes.Sites[poi].Centre;
                        float d = Vector2.Distance(world, new Vector2(c.X, c.Y));
                        if (d < bestD) { bestD = d; best = poi; }
                    }
                    if (best >= 0 && !_routeDraft.Contains(best)) { _routeDraft.Add(best); _key = null; }
                };
                foreach (int poi in C.Candidates)
                {
                    var c = town.Lanes.Sites[poi].Centre;
                    int idx = _routeDraft.IndexOf(poi);
                    _map.Marker(new Vector2(c.X, c.Y), idx >= 0 ? UiKit.MarkRed : UiKit.Accent, 30, idx >= 0 ? $"{idx + 1}. {town.Network.Pois[poi].Name}" : town.Network.Pois[poi].Name, 18, diamond: true);
                }
                if (C.Safehouse >= 0)
                {
                    var s = town.Lanes.Sites[C.Safehouse].Centre;
                    _map.Marker(new Vector2(s.X, s.Y), Color.white, 34, $"SAFEHOUSE: {town.Network.Pois[C.Safehouse].Name}", 18);
                }
                if (C.StartPoi >= 0)
                {
                    var s = town.Lanes.Sites[C.StartPoi].Centre;
                    _map.Marker(new Vector2(s.X, s.Y), UiKit.Good, 24, "START", 18);
                }
                var pts = _routeDraft.Select(p => { var c = town.Lanes.Sites[p].Centre; return new Vector2(c.X, c.Y); }).ToList();
                if (C.StartPoi >= 0) { var s = town.Lanes.Sites[C.StartPoi].Centre; pts.Insert(0, new Vector2(s.X, s.Y)); }
                _map.Line(pts, new Color(1, 0.3f, 0.3f, 0.8f), 5);
            }
            else
            {
                UiKit.LabelAt(bg.transform, "THE MARK IS PLANNING...", 54, UiKit.TailBlue, new Vector2(1f, 1f), new Vector2(-40, -100), new Vector2(820, 70), TextAnchor.UpperLeft);
                UiKit.LabelAt(bg.transform, "Memorise your target. Follow without being noticed: drive like traffic.\nAt a real errand a gold marker flashes over the Mark's car for a few seconds. Remember where!\nWhen the drive ends, your team marks the Mark's stops on the map. Hold I to see this card again.",
                    26, Color.white, new Vector2(1f, 1f), new Vector2(-40, -180), new Vector2(780, 260));
                if (BuildTargetCard(bg.transform, new Vector2(1f, 0f), new Vector2(-40, 40)) == null)
                    UiKit.LabelAt(bg.transform, "Waiting for the Mark's description...", 30, UiKit.Accent, new Vector2(1f, 0f), new Vector2(-40, 200), new Vector2(820, 60));
                foreach (var poi in town.Network.Pois)
                {
                    var c = town.Lanes.Sites[poi.Id].Centre;
                    _map.Marker(new Vector2(c.X, c.Y), new Color(1, 1, 1, 0.6f), 14, poi.Type == PoiType.ChopShop ? "Body Shop" : poi.Type == PoiType.Petrol ? "Gas" : null, 14);
                }
            }
        }

        // ---- target overview (Tails) ---------------------------------------------------

        GameObject _targetCard;
        string _portraitPlate;
        (Texture2D car, Texture2D driver) _portraits;

        /// <summary>
        /// The Mark's description for Tails: the exact car (paint, plate) and driver (hat) rendered from
        /// the game's own meshes, plus the words to call it out with. Null if not revealed yet.
        /// </summary>
        GameObject BuildTargetCard(Transform parent, Vector2 anchor, Vector2 pos)
        {
            if (C.Target == null) return null;
            var id = C.Target.Value;
            if (_portraitPlate != id.Plate) { _portraits = ModelThumbnails.Portraits(id); _portraitPlate = id.Plate; }
            var card = UiKit.Panel(parent, "TargetCard", new Color(0.1f, 0.1f, 0.14f, 0.96f), anchor, pos, new Vector2(820, 470));
            UiKit.LabelAt(card.transform, "YOUR TARGET", 34, UiKit.MarkRed, new Vector2(0f, 1f), new Vector2(20, -10), new Vector2(500, 46));
            var carImg = UiKit.Rect(card.transform, "Car", new Vector2(0f, 1f), new Vector2(0f, 1f), new Vector2(0f, 1f), new Vector2(20, -62), new Vector2(480, 270)).gameObject.AddComponent<RawImage>();
            carImg.texture = _portraits.car;
            var driverImg = UiKit.Rect(card.transform, "Driver", new Vector2(0f, 1f), new Vector2(0f, 1f), new Vector2(0f, 1f), new Vector2(520, -62), new Vector2(270, 270)).gameObject.AddComponent<RawImage>();
            driverImg.texture = _portraits.driver;
            var model = VehicleCatalog.Models[id.ModelId];
            string hat = (HatType)id.Hat == HatType.None ? "driver: no hat" : $"driver in a {VehicleCatalog.Colors[id.HatColorId].Name.ToLower()} {HatName((HatType)id.Hat)}";
            UiKit.LabelAt(card.transform, $"<b>{VehicleCatalog.Colors[id.ColorId].Name} {model.FullName}</b>  ·  {hat}", 28, Color.white,
                new Vector2(0f, 0f), new Vector2(20, 76), new Vector2(780, 44));
            UiKit.LabelAt(card.transform, $"PLATE  <b>{PlateFormat.Display(id.Plate)}</b>", 40, UiKit.Accent, new Vector2(0f, 0f), new Vector2(20, 18), new Vector2(780, 56));
            return card.gameObject;
        }

        static string HatName(HatType h) => h switch
        {
            HatType.Cap => "cap", HatType.Beanie => "beanie", HatType.TopHat => "top hat", HatType.Cowboy => "cowboy hat",
            HatType.Party => "party hat", HatType.Crown => "crown", _ => "hat",
        };

        void BuildPickup()
        {
            BuildDriving();
            string text;
            if (C.MyRole == Role.Mark)
                text = "Get ready — your errands start now.\nYou're parked at the start. Drive to stop 1 when the timer ends.";
            else
            {
                var town = TownBuilder.Instance;
                string car = C.RevealModel >= 0 ? $"{VehicleCatalog.Colors[C.RevealColor].Name} {VehicleCatalog.Models[C.RevealModel].FullName}" : "?";
                string where = C.StartPoi >= 0 ? town.Network.Pois[C.StartPoi].Name : "?";
                text = $"TARGET: {car}\nLeaving {where} — it's on your map.";
            }
            _banner = UiKit.LabelAt(_screen, text, 52, UiKit.Accent, new Vector2(0.5f, 0.5f), new Vector2(0, 260), new Vector2(1600, 200), TextAnchor.MiddleCenter);
            if (C.MyRole != Role.Mark) BuildTargetCard(_screen, new Vector2(0.5f, 0.5f), new Vector2(0, -60));
        }

        // ---- driving -----------------------------------------------------------------

        void BuildDriving()
        {
            var roleColor = C.MyRole == Role.Mark ? UiKit.MarkRed : C.MyRole == Role.Spotter ? UiKit.Accent : UiKit.TailBlue;
            // Below the chase-view mirror strip, which owns the top of the screen.
            var top = UiKit.Panel(_screen, "Top", UiKit.Ink, new Vector2(0f, 1f), new Vector2(290, -300), new Vector2(520, 160));
            UiKit.LabelAt(top.transform, C.MyRole.ToString().ToUpper(), 44, roleColor, new Vector2(0f, 1f), new Vector2(20, -12), new Vector2(300, 60));
            _timer = UiKit.LabelAt(top.transform, "", 44, Color.white, new Vector2(1f, 1f), new Vector2(-20, -12), new Vector2(200, 60), TextAnchor.UpperRight);
            _status = UiKit.LabelAt(top.transform, "", 24, Color.white, new Vector2(0f, 0f), new Vector2(20, 12), new Vector2(480, 60), TextAnchor.LowerLeft);
            BuildLapMap();
            if (C.MyRole != Role.Mark && C.Phase == Phase.Driving)
            {
                _targetCard = BuildTargetCard(_screen, new Vector2(0.5f, 0.5f), new Vector2(0, 40));
                if (_targetCard != null) _targetCard.SetActive(false);
            }
            // Below the chase-view mirror strip.
            _help = UiKit.LabelAt(_screen, "", 20, new Color(1, 1, 1, 0.9f), new Vector2(1f, 1f), new Vector2(-30, -230), new Vector2(560, 420), TextAnchor.UpperRight);
            _center = UiKit.LabelAt(_screen, "", 36, UiKit.Accent, new Vector2(0.5f, 1f), new Vector2(0, -250), new Vector2(1200, 120), TextAnchor.UpperCenter);
            _progress = UiKit.Rect(_screen, "Progress", new Vector2(0.5f, 1f), new Vector2(0.5f, 1f), new Vector2(0.5f, 1f), new Vector2(0, -380), new Vector2(600, 26));
            _progress.gameObject.AddComponent<Image>().color = UiKit.Ink;
            _progressFill = UiKit.Rect(_progress, "Fill", new Vector2(0, 0), new Vector2(0, 1), new Vector2(0, 0.5f), Vector2.zero, new Vector2(0, 0)).gameObject.AddComponent<Image>();
            _progressFill.color = UiKit.Accent;
            _progress.gameObject.SetActive(false);
            if (C.MyRole == Role.Spotter)
            {
                // CCTV look: red REC dot, camera name, controls.
                _cctv = UiKit.LabelAt(_screen, "", 34, new Color(1f, 0.3f, 0.3f), new Vector2(0f, 0f), new Vector2(40, 40) + new Vector2(480, 60), new Vector2(960, 120), TextAnchor.LowerLeft);
                UiKit.LabelAt(_screen, "SPOTTER — A/D: switch camera · W/S: zoom · tell your team what you see", 26, Color.white,
                    new Vector2(0.5f, 0f), new Vector2(0, 40), new Vector2(1400, 40), TextAnchor.LowerCenter);
            }
            _voice = UiKit.LabelAt(_screen, "", 24, Color.white, new Vector2(0.5f, 0f), new Vector2(0, 20) + new Vector2(0, 30), new Vector2(1200, 40), TextAnchor.LowerCenter);
            _navLabel = UiKit.LabelAt(_screen, "", 34, UiKit.Accent, new Vector2(0.5f, 0f), new Vector2(0, 110), new Vector2(1400, 50), TextAnchor.LowerCenter);
            _tagMarker = UiKit.Rect(_screen, "Tag", new Vector2(0, 0), new Vector2(0, 0), new Vector2(0.5f, 0.5f), Vector2.zero, new Vector2(44, 44));
            _tagMarker.gameObject.AddComponent<Image>().color = new Color(1f, 0.2f, 0.9f, 0.9f);
            _tagMarker.localRotation = Quaternion.Euler(0, 0, 45);
            _tagMarker.gameObject.SetActive(false);
        }

        void BuildMapOverlay()
        {
            var bg = UiKit.Panel(_screen, "MapOverlay", new Color(0, 0, 0, 0.75f), new Vector2(0.5f, 0.5f), Vector2.zero, new Vector2(1920, 1080));
            _map = MapView.Create(bg.transform, new Vector2(0.5f, 0.5f), new Vector2(-330, 0), 980);
            string info = C.MyRole == Role.Mark
                ? "Your route. Red = next stop.\nFake stops show no gold marker — but a Tail who wasn't watching may still mark them."
                : "Remember where the Mark stops (the gold marker flashes at real errands).\nYou'll mark them with your team when the drive ends.";
            UiKit.LabelAt(bg.transform, info + "\n\nMouse wheel: zoom · drag: pan\nM / Esc: close map", 28, Color.white, new Vector2(1f, 0.5f), new Vector2(-60, 0), new Vector2(520, 700), TextAnchor.MiddleLeft);
        }

        /// <summary>Put a model thumbnail in the top of a button; the label drops to the bottom strip.</summary>
        static void Thumb(Button b, int modelId)
        {
            var label = b.GetComponentInChildren<Text>();
            label.alignment = TextAnchor.LowerCenter;
            ((RectTransform)label.transform).offsetMin = new Vector2(4, 4);
            var rt = UiKit.Rect(b.transform, "Thumb", new Vector2(0.5f, 1f), new Vector2(0.5f, 1f), new Vector2(0.5f, 1f), new Vector2(0, -4), new Vector2(0, 0));
            var parent = (RectTransform)b.transform;
            float h = parent.sizeDelta.y - 40f;
            rt.sizeDelta = new Vector2(h * 16f / 9f, h);
            var img = rt.gameObject.AddComponent<RawImage>();
            img.texture = ModelThumbnails.Get(modelId);
            img.raycastTarget = false;
        }

        static bool AtRentalBay()
        {
            var car = PlayerCar.Local;
            var town = TownBuilder.Instance;
            if (car == null || town == null || Mathf.Abs(car.ForwardSpeed) > 0.6f) return false;
            var p = new Core.Util.Vec2(car.transform.position.x, car.transform.position.z);
            foreach (var bay in town.Lanes.Bays)
                if (town.Network.Pois[bay.PoiId].Type == PoiType.RentalLot && Core.Util.Vec2.Distance(Match.BayCentre(bay), p) < 4f) return true;
            return false;
        }

        void BuildSwapPanel()
        {
            var bg = UiKit.Panel(_screen, "Swap", UiKit.Ink, new Vector2(0.5f, 0.5f), Vector2.zero, new Vector2(1600, 700));
            UiKit.LabelAt(bg.transform, "RENTALS — swap into a different car (-40 pts)", 46, UiKit.TailBlue, new Vector2(0.5f, 1f), new Vector2(0, -30), new Vector2(1400, 60), TextAnchor.UpperCenter);
            for (int m = 0; m < VehicleCatalog.Models.Length; m++)
            {
                int id = m;
                var model = VehicleCatalog.Models[m];
                if (model.NpcOnly) continue; // buses and lorries are never players
                var sb = UiKit.ButtonAt(bg.transform, $"{model.FullName} · cost {model.Cost}", new Vector2(0.5f, 1f), new Vector2(-600 + (m % 4) * 400, -150 - (m / 4) * 140), new Vector2(370, 130),
                    () => { _swapModel = id; }, _swapModel == m ? UiKit.TailBlue : UiKit.Button, 22);
                Thumb(sb, id);
            }
            for (int c = 0; c < VehicleCatalog.Colors.Length; c++)
            {
                int id = c;
                var col = VehicleCatalog.Colors[c];
                var b = UiKit.ButtonAt(bg.transform, c <= 3 ? "+1" : "", new Vector2(0.5f, 1f), new Vector2(-660 + c * 120, -460), new Vector2(100, 90), () => { _swapColor = id; }, new Color(col.R, col.G, col.B), 26);
                if (_swapColor == c) { b.GetComponent<Outline>().effectColor = UiKit.Accent; b.GetComponent<Outline>().effectDistance = new Vector2(6, -6); }
            }
            int cost = Match.Cost(_swapModel, _swapColor);
            UiKit.ButtonAt(bg.transform, cost <= 5 ? $"SWAP (cost {cost}/5)" : $"Over budget ({cost}/5)", new Vector2(0.5f, 0f), new Vector2(0, 70), new Vector2(520, 80),
                () => { if (Match.Cost(_swapModel, _swapColor) <= 5) { C.CarSwap(_swapModel, _swapColor); _swapOpen = false; } }, cost <= 5 ? UiKit.Good : UiKit.MarkRed);
        }

        void BuildNotepad()
        {
            var bg = UiKit.Panel(_screen, "Notepad", new Color(0.98f, 0.93f, 0.6f, 1f), new Vector2(0f, 0.5f), new Vector2(260, 0), new Vector2(480, 520));
            UiKit.LabelAt(bg.transform, "NOTEPAD (Tab to close)", 24, Color.black, new Vector2(0.5f, 1f), new Vector2(0, -10), new Vector2(440, 34), TextAnchor.UpperCenter);
            _notepad = UiKit.Input(bg.transform, "Plates, cars, hunches...", new Vector2(0.5f, 0.5f), new Vector2(0, -20), new Vector2(440, 440), 28, multiline: true);
            _notepad.text = _notes;
            _notepad.onValueChanged.AddListener(v => _notes = v);
            _notepad.ActivateInputField();
        }

        void BuildFlagForm()
        {
            var bg = UiKit.Panel(_screen, "Flag", UiKit.Ink, new Vector2(0.5f, 0.5f), Vector2.zero, new Vector2(1500, 900));
            UiKit.LabelAt(bg.transform, "FILE A FLAG", 60, UiKit.MarkRed, new Vector2(0.5f, 1f), new Vector2(0, -20), new Vector2(1000, 80), TextAnchor.UpperCenter);
            _flagStatus = UiKit.LabelAt(bg.transform, "", 28, Color.white, new Vector2(0.5f, 1f), new Vector2(0, -100), new Vector2(1300, 40), TextAnchor.UpperCenter);
            for (int m = 0; m < VehicleCatalog.Models.Length; m++)
            {
                int id = m;
                var model = VehicleCatalog.Models[m];
                if (model.NpcOnly) continue; // buses and lorries are never players
                var fb = UiKit.ButtonAt(bg.transform, model.FullName, new Vector2(0.5f, 1f), new Vector2(-540 + (m % 4) * 360, -170 - (m / 4) * 124), new Vector2(340, 116),
                    () => { _flagModel = id; _key = null; }, _flagModel == m ? UiKit.MarkRed : UiKit.Button, 22);
                Thumb(fb, id);
            }
            UiKit.LabelAt(bg.transform, "Plate (use ? for characters you didn't catch — 5 of 7 must match):", 24, Color.white, new Vector2(0.5f, 1f), new Vector2(-150, -420), new Vector2(1000, 34));
            _plateField = UiKit.Input(bg.transform, "ABC 12??", new Vector2(0.5f, 1f), new Vector2(-150, -480), new Vector2(700, 76), 44);
            _plateField.characterLimit = 8;
            _plateField.onValidateInput = (text, index, ch) => char.IsLetterOrDigit(ch) || ch == '?' || ch == ' ' ? char.ToUpperInvariant(ch) : '\0';
            UiKit.ButtonAt(bg.transform, "FLAG IT", new Vector2(0.5f, 1f), new Vector2(430, -480), new Vector2(360, 76), () =>
            {
                if (!C.FlagWindowOpen) { C.Notices.Add(("You can only flag while dwelling at a checkpoint", Time.time)); return; }
                C.Flag(_flagModel, _plateField.text);
            }, UiKit.MarkRed);
            UiKit.LabelAt(bg.transform, "Snapshots (F while driving) — click to enlarge:", 24, Color.white, new Vector2(0.5f, 0f), new Vector2(-300, 250), new Vector2(900, 34));
            for (int i = 0; i < _snapshots.Count; i++)
            {
                var tex = _snapshots[i];
                var rt = UiKit.Rect(bg.transform, "Snap", new Vector2(0.5f, 0f), new Vector2(0.5f, 0f), new Vector2(0.5f, 0f), new Vector2(-600 + i * 240, 90), new Vector2(224, 126));
                var img = rt.gameObject.AddComponent<RawImage>();
                img.texture = tex;
                var b = rt.gameObject.AddComponent<Button>();
                b.onClick.AddListener(() => ShowEnlarged(tex));
            }
            UiKit.LabelAt(bg.transform, "G / Esc: close", 22, new Color(1, 1, 1, 0.7f), new Vector2(1f, 1f), new Vector2(-20, -20), new Vector2(300, 30), TextAnchor.UpperRight);
        }

        void ShowEnlarged(Texture2D tex)
        {
            if (_enlarged != null) Destroy(_enlarged.gameObject);
            var rt = UiKit.Rect(_overlay, "Enlarged", new Vector2(0.5f, 0.5f), new Vector2(0.5f, 0.5f), new Vector2(0.5f, 0.5f), Vector2.zero, new Vector2(1600, 900));
            _enlarged = rt.gameObject.AddComponent<RawImage>();
            _enlarged.texture = tex;
            var b = rt.gameObject.AddComponent<Button>();
            b.onClick.AddListener(() => { Destroy(_enlarged.gameObject); _enlarged = null; });
        }

        // ---- debrief -----------------------------------------------------------------

        // ---- marking (after the drive) --------------------------------------------------
        // Tails, together on one map, mark where they think the Mark stopped. No scoring happens
        // during the drive: this and the Mark's hidden flags decide the round.

        Text _markingInfo;

        void BuildMarking()
        {
            var bg = UiKit.Panel(_screen, "Marking", UiKit.Ink, new Vector2(0.5f, 0.5f), Vector2.zero, new Vector2(1880, 1040));
            _map = MapView.Create(bg.transform, new Vector2(0f, 0.5f), new Vector2(20, 0), 980);
            _timer = UiKit.LabelAt(bg.transform, "", 40, Color.white, new Vector2(1f, 1f), new Vector2(-30, -20), new Vector2(300, 60), TextAnchor.UpperRight);
            bool tail = C.MyRole == Role.Tail || C.MyRole == Role.Spotter;
            UiKit.LabelAt(bg.transform, tail ? "WHERE DID THE MARK STOP?" : "THE TAILS ARE MARKING...", 50, tail ? UiKit.TailBlue : UiKit.MarkRed,
                new Vector2(1f, 1f), new Vector2(-40, -90), new Vector2(820, 70));
            string help = tail
                ? "Mark the places the Mark stopped, together, on this shared map.\n\nLEFT CLICK: place a mark (snaps to the business).\nRIGHT CLICK: remove the nearest mark.\nMOUSE WHEEL: zoom · DRAG: pan.\n\nYour own route is drawn to jog your memory. Talk it through!\nEvery stop you find counts for your team; every Tail the Mark identified counts against."
                : "They're trying to remember your stops.\nYou'll find out at the reveal whether your flags caught anyone.";
            UiKit.LabelAt(bg.transform, help, 26, Color.white, new Vector2(1f, 1f), new Vector2(-40, -180), new Vector2(820, 420));
            _markingInfo = UiKit.LabelAt(bg.transform, "", 34, UiKit.Accent, new Vector2(1f, 0f), new Vector2(-40, 60), new Vector2(820, 120));
            if (tail)
                _map.Clicked += (world, button) =>
                {
                    var town = TownBuilder.Instance;
                    if (button == PointerEventData.InputButton.Left)
                    {
                        var near = town.Network.Pois.Select(poi => town.Lanes.Sites[poi.Id].Centre)
                            .Select(c => new Vector2(c.X, c.Y)).OrderBy(c => (c - world).sqrMagnitude).FirstOrDefault();
                        C.AddPin((near - world).magnitude < 60f ? near : world);
                    }
                    else if (button == PointerEventData.InputButton.Right)
                    {
                        var nearest = C.Pins.OrderBy(p => (p.pos - world).sqrMagnitude).FirstOrDefault();
                        if (nearest.id != 0 && (nearest.pos - world).magnitude < 80f) C.RemovePin(nearest.id);
                    }
                    _refresh = 0f;
                };
            _refresh = 0f;
        }

        void DrawMarking()
        {
            if (_map == null) return;
            _map.ClearMarkers();
            var town = TownBuilder.Instance;
            StreetLabels(_map);
            bool tail = C.MyRole == Role.Tail || C.MyRole == Role.Spotter;
            if (C.MyTrail.Count > 1) _map.Line(C.MyTrail, new Color(0.3f, 0.75f, 1f, 0.8f), 3f);
            foreach (var poi in town.Network.Pois)
            {
                var c = town.Lanes.Sites[poi.Id].Centre;
                _map.Marker(new Vector2(c.X, c.Y), new Color(1, 1, 1, 0.45f), 10);
            }
            if (tail)
            {
                foreach (var pin in C.Pins)
                    _map.Marker(pin.pos, UiKit.Accent, 26, PlayerName(pin.player), 16, diamond: true);
                if (_markingInfo != null) _markingInfo.text = $"Team marks: {C.Pins.Count} / {Mathf.Max(C.MarksAllowed, C.Pins.Count)}";
            }
            else
            {
                for (int i = 0; i < C.Route.Count; i++)
                {
                    var c = town.Lanes.Sites[C.Route[i]].Centre;
                    bool done = i < C.NextCheckpoint;
                    _map.Marker(new Vector2(c.X, c.Y), done ? UiKit.MarkRed : new Color(0.5f, 0.5f, 0.5f), 24,
                        $"{(i == C.Route.Count - 1 ? "SAFEHOUSE" : (i + 1).ToString())}{(done ? "" : " (not reached)")}", 16, diamond: true);
                }
            }
        }

        // ---- the reveal (party-game style) -----------------------------------------------
        // Stop by stop: did the Tails find it? Flag by flag: who was it? Then the numbers face off
        // and the winner is crowned. Afterwards the replay underneath is there to argue over.

        sealed class RevealStep { public float Duration; public string Header, Body, Result; public Color ResultColor; public AudioClip Sound; public bool CountLocation, CountTail; }
        readonly List<RevealStep> _revealSteps = new List<RevealStep>();
        GameObject _reveal;
        Text _revealHeader, _revealBody, _revealResult, _revealScore, _revealHint;
        RectTransform _confetti;
        readonly List<(RectTransform rt, Vector2 vel, float spin)> _confettiBits = new List<(RectTransform, Vector2, float)>();
        float _revealStart;
        int _revealFired = -1;
        bool _revealDone;

        static string OutcomeText(DebriefData d) =>
            d.Outcome == RoundOutcome.TailsWin ? "TAILS WIN!" : d.Outcome == RoundOutcome.MarkWins ? "THE MARK WINS!" : "IT'S A DRAW!";

        DebriefData _revealFor;

        void BuildReveal(DebriefData d)
        {
            // A UI rebuild mid-debrief (F1, etc.) resumes the show rather than restarting it.
            bool resume = ReferenceEquals(_revealFor, d);
            if (resume && _revealDone) return;
            _revealFor = d;
            var town = TownBuilder.Instance;
            _revealSteps.Clear();
            _revealSteps.Add(new RevealStep { Duration = 2f, Header = "THE REVEAL", Body = d.Reason });
            _revealSteps.Add(new RevealStep { Duration = 1.6f, Header = "LOCATIONS", Body = "Did the Tails find the Mark's stops?" });
            for (int i = 0; i < d.Stops.Count; i++)
            {
                var st = d.Stops[i];
                var site = town.Lanes.Sites[st.poi];
                string grid = MapView.Grid != null ? MapView.Grid.CellName(site.Centre) : "";
                var step = new RevealStep
                {
                    Duration = 2.6f, Header = i == d.Stops.Count - 1 ? "THE SAFEHOUSE" : $"STOP {i + 1}",
                    Body = $"{town.Network.Pois[st.poi].Name}  ·  {grid}",
                };
                if (!st.visited) { step.Result = "Never reached"; step.ResultColor = new Color(0.6f, 0.6f, 0.6f); step.Sound = null; }
                else if (st.foundBy >= 0) { step.Result = $"FOUND by {PlayerName(st.foundBy)}!"; step.ResultColor = UiKit.Good; step.Sound = Audio.Sfx.Ding; step.CountLocation = true; }
                else { step.Result = "Missed!"; step.ResultColor = UiKit.MarkRed; step.Sound = Audio.Sfx.Buzz; }
                _revealSteps.Add(step);
            }
            _revealSteps.Add(new RevealStep { Duration = 1.6f, Header = "IDENTIFICATIONS", Body = d.Flags.Count == 0 ? "The Mark didn't flag anyone." : "Who did the Mark flag?" });
            var caught = new HashSet<int>();
            for (int i = 0; i < d.Flags.Count; i++)
            {
                var f = d.Flags[i];
                var model = VehicleCatalog.Models[Mathf.Clamp(f.model, 0, VehicleCatalog.Models.Length - 1)];
                var step = new RevealStep { Duration = 2.6f, Header = $"FLAG {i + 1}", Body = $"\"{model.FullName}, plate {f.plate}\"" };
                if (f.hit < 0) { step.Result = "Just traffic!"; step.ResultColor = new Color(0.7f, 0.7f, 0.7f); step.Sound = Audio.Sfx.Buzz; }
                else if (!caught.Add(f.hit)) { step.Result = $"{PlayerName(f.hit)} — again!"; step.ResultColor = UiKit.MarkRed; step.Sound = Audio.Sfx.Ding; }
                else { step.Result = $"It was {PlayerName(f.hit)}!"; step.ResultColor = UiKit.MarkRed; step.Sound = Audio.Sfx.Ding; step.CountTail = true; }
                _revealSteps.Add(step);
            }
            string winners = d.Outcome == RoundOutcome.TailsWin
                ? string.Join(", ", C.Players.Where(p => p.Id != C.MarkId).Select(p => p.Name))
                : d.Outcome == RoundOutcome.MarkWins ? PlayerName(C.MarkId) : "Nobody — this time";
            _revealSteps.Add(new RevealStep
            {
                Duration = 5f, Header = OutcomeText(d), Body = $"{d.Locations} location{(d.Locations == 1 ? "" : "s")} found  vs  {d.TailsIdentified} Tail{(d.TailsIdentified == 1 ? "" : "s")} identified",
                Result = "★ " + winners + " ★", ResultColor = d.Outcome == RoundOutcome.TailsWin ? UiKit.TailBlue : d.Outcome == RoundOutcome.MarkWins ? UiKit.MarkRed : UiKit.Accent,
                Sound = Audio.Sfx.Fanfare,
            });

            var root = UiKit.Panel(_screen, "Reveal", new Color(0.05f, 0.05f, 0.09f, 0.97f), new Vector2(0.5f, 0.5f), Vector2.zero, new Vector2(1920, 1080));
            _reveal = root.gameObject;
            _confetti = UiKit.Stretch(root.transform, "Confetti");
            _revealHeader = UiKit.LabelAt(root.transform, "", 96, UiKit.Accent, new Vector2(0.5f, 0.5f), new Vector2(0, 250), new Vector2(1800, 140), TextAnchor.MiddleCenter);
            _revealBody = UiKit.LabelAt(root.transform, "", 46, Color.white, new Vector2(0.5f, 0.5f), new Vector2(0, 80), new Vector2(1800, 160), TextAnchor.MiddleCenter);
            _revealResult = UiKit.LabelAt(root.transform, "", 72, Color.white, new Vector2(0.5f, 0.5f), new Vector2(0, -90), new Vector2(1800, 120), TextAnchor.MiddleCenter);
            _revealScore = UiKit.LabelAt(root.transform, "", 44, Color.white, new Vector2(0.5f, 0.5f), new Vector2(0, -300), new Vector2(1800, 80), TextAnchor.MiddleCenter);
            _revealHint = UiKit.LabelAt(root.transform, "Space: skip to the result · after it: the replay", 22, new Color(1, 1, 1, 0.5f), new Vector2(0.5f, 0f), new Vector2(0, 30), new Vector2(1200, 40), TextAnchor.MiddleCenter);
            _confettiBits.Clear();
            if (resume) return;
            _revealStart = Time.time;
            _revealFired = -1;
            _revealDone = false;
        }

        void UpdateReveal()
        {
            if (_reveal == null || _revealDone) return;
            var kb = Keyboard.current;
            float t = Time.time - _revealStart;
            float total = _revealSteps.Sum(x => x.Duration);
            if (kb != null && kb.spaceKey.wasPressedThisFrame)
            {
                if (t < total - _revealSteps[_revealSteps.Count - 1].Duration) { _revealStart = Time.time - (total - _revealSteps[_revealSteps.Count - 1].Duration); t = Time.time - _revealStart; }
                else t = total;
            }
            if (t >= total + 0.5f) { _reveal.SetActive(false); _revealDone = true; return; }

            // Which step, and how far into it; counts so far.
            int idx = 0; float acc = 0f;
            while (idx < _revealSteps.Count - 1 && t >= acc + _revealSteps[idx].Duration) { acc += _revealSteps[idx].Duration; idx++; }
            float local = t - acc;
            var step = _revealSteps[idx];
            const float Suspense = 1.1f;
            bool shown = step.Result == null || local >= Suspense || idx == _revealSteps.Count - 1 && local >= 0.9f;
            int locs = 0, tails = 0;
            for (int i = 0; i <= idx; i++)
            {
                bool counted = i < idx || shown;
                if (counted && _revealSteps[i].CountLocation) locs++;
                if (counted && _revealSteps[i].CountTail) tails++;
            }
            _revealHeader.text = step.Header;
            _revealBody.text = step.Body;
            _revealResult.text = shown ? step.Result ?? "" : new string('.', 1 + (int)(local * 4f) % 3);
            _revealResult.color = shown ? step.ResultColor : Color.white;
            _revealScore.text = $"<color=#5AA9FF>LOCATIONS FOUND  {locs}</color>      <color=#FF6060>TAILS IDENTIFIED  {tails}</color>";
            // Punch the result in.
            float pop = shown ? 1f + 0.25f * Mathf.Exp(-(local - Suspense) * 8f) : 1f;
            _revealResult.rectTransform.localScale = Vector3.one * Mathf.Max(1f, pop);
            // Sounds: a drumroll through the suspense, then the result sting (once per step).
            if (!shown && step.Result != null && (int)(local * 14f) != (int)((local - Time.deltaTime) * 14f)) Audio.Sfx.PlayUi(Audio.Sfx.Drum, 0.6f);
            if (shown && _revealFired < idx)
            {
                _revealFired = idx;
                if (step.Sound != null) Audio.Sfx.PlayUi(step.Sound);
                if (idx == _revealSteps.Count - 1) SpawnConfetti(step.ResultColor);
            }
            UpdateConfetti();
        }

        void SpawnConfetti(Color main)
        {
            var palette = new[] { main, UiKit.Accent, Color.white, UiKit.Good, new Color(1f, 0.5f, 0.8f) };
            for (int i = 0; i < 90; i++)
            {
                var rt = UiKit.Rect(_confetti, "Bit", new Vector2(0.5f, 0.5f), new Vector2(0.5f, 0.5f), new Vector2(0.5f, 0.5f),
                    new Vector2(Random.Range(-900f, 900f), Random.Range(560f, 900f)), new Vector2(Random.Range(10f, 20f), Random.Range(16f, 30f)));
                var img = rt.gameObject.AddComponent<Image>();
                img.color = palette[i % palette.Length];
                img.raycastTarget = false;
                _confettiBits.Add((rt, new Vector2(Random.Range(-60f, 60f), Random.Range(-420f, -220f)), Random.Range(-360f, 360f)));
            }
        }

        void UpdateConfetti()
        {
            float dt = Time.deltaTime;
            for (int i = 0; i < _confettiBits.Count; i++)
            {
                var (rt, vel, spin) = _confettiBits[i];
                if (rt == null) continue;
                rt.anchoredPosition += (vel + new Vector2(Mathf.Sin(Time.time * 3f + i) * 40f, 0f)) * dt;
                rt.localRotation = Quaternion.Euler(0, 0, rt.localEulerAngles.z + spin * dt);
            }
        }

        // ---- debrief replay ----------------------------------------------------------
        // Everyone's trails on the gridded map, replayed on a scrubbable clock, with stops, pins,
        // flags and tags appearing when they happened — the "you were two cars behind him!" screen.

        float _replayT, _replayMax, _replaySpeed = 10f;
        bool _replayPlaying = true;
        ScrubBar _scrub;
        Text _replayNow, _replayLog, _replayClock;
        readonly Dictionary<int, Color> _replayColors = new Dictionary<int, Color>();
        static readonly float[] ReplaySpeeds = { 1f, 5f, 10f, 30f };
        // Tails: warm/green hues that stand apart from the Mark's red and the map's blue grid.
        static readonly Color[] TrailColors =
        {
            new Color(0.2f, 0.85f, 0.3f), new Color(1f, 0.6f, 0.1f), new Color(0.95f, 0.3f, 0.9f),
            new Color(1f, 0.92f, 0.2f), new Color(0.1f, 0.9f, 0.85f), new Color(0.6f, 0.4f, 0.2f),
        };

        void BuildDebrief()
        {
            var bg = UiKit.Panel(_screen, "Debrief", UiKit.Ink, new Vector2(0.5f, 0.5f), Vector2.zero, new Vector2(1880, 1040));
            const float mapSize = 880f;
            _map = MapView.Create(bg.transform, new Vector2(0f, 1f), new Vector2(20, -20), mapSize);
            _scrub = ScrubBar.Create(bg.transform, new Vector2(0f, 0f), new Vector2(20, 66), new Vector2(mapSize + 12, 40));
            _replayClock = UiKit.LabelAt(bg.transform, "", 24, Color.white, new Vector2(0f, 0f), new Vector2(20, 18), new Vector2(mapSize + 12, 40), TextAnchor.MiddleLeft);
            _timer = UiKit.LabelAt(bg.transform, "", 30, Color.white, new Vector2(1f, 1f), new Vector2(-24, -16), new Vector2(300, 44), TextAnchor.UpperRight);
            UiKit.LabelAt(bg.transform, "DEBRIEF", 50, UiKit.Accent, new Vector2(1f, 1f), new Vector2(-340, -10), new Vector2(560, 64), TextAnchor.UpperRight);
            var d = C.Debrief;
            _replayColors.Clear();
            if (d != null)
            {
                var town = TownBuilder.Instance;
                _replayMax = Mathf.Max(1f, d.Trails.Values.Where(t => t.Count > 0).Select(t => t[t.Count - 1].y).DefaultIfEmpty(1f).Max());
                _replayT = 0f;
                _replayPlaying = true;
                int ci = 0;
                foreach (var id in d.Trails.Keys.OrderBy(k => k))
                    _replayColors[id] = id == C.MarkId ? UiKit.MarkRed : TrailColors[ci++ % TrailColors.Length];

                // Summary: the result, stop by stop, and the flags.
                var lines = new List<string> { $"<b>{OutcomeText(d)}</b>  (locations {d.Locations} · Tails identified {d.TailsIdentified})", d.Reason };
                for (int i = 0; i < d.Stops.Count; i++)
                {
                    var st = d.Stops[i];
                    bool safe = i == d.Stops.Count - 1;
                    string res = !st.visited ? "never reached" : st.foundBy >= 0 ? $"found by {PlayerName(st.foundBy)}" : "missed";
                    lines.Add($"  {(safe ? "Safehouse" : $"Stop {i + 1}")}: {town.Network.Pois[st.poi].Name} — {res}");
                }
                lines.Add("Round wins: " + string.Join(" · ", C.Players.OrderByDescending(p => p.Score).Select(p => $"{p.Name}{(p.Id == C.MarkId ? " (Mark)" : "")} {p.Score}")));
                UiKit.LabelAt(bg.transform, string.Join("\n", lines), 22, Color.white, new Vector2(1f, 1f), new Vector2(-24, -86), new Vector2(940, 250)).alignment = TextAnchor.UpperLeft;
                UiKit.LabelAt(bg.transform, "HIGHLIGHTS\n" + string.Join("\n", Highlights(d)), 22, UiKit.Accent, new Vector2(1f, 1f), new Vector2(-24, -340), new Vector2(940, 230));
                _replayNow = UiKit.LabelAt(bg.transform, "", 22, Color.white, new Vector2(1f, 1f), new Vector2(-24, -575), new Vector2(940, 170));
                _replayLog = UiKit.LabelAt(bg.transform, "", 21, new Color(0.85f, 0.88f, 0.95f), new Vector2(1f, 1f), new Vector2(-24, -750), new Vector2(940, 220));

                // Event ticks on the timeline.
                foreach (var dw in d.Dwells) _scrub.Tick(dw.start / _replayMax, dw.routeIndex >= 0 ? new Color(1f, 0.85f, 0.2f) : new Color(1f, 0.55f, 0.1f));
                foreach (var e in d.Events) _scrub.Tick(e.time / _replayMax, EventColor(e.type));
                _scrub.Seek += f => { _replayT = f * _replayMax; _refresh = 0f; };
            }
            UiKit.LabelAt(bg.transform, "Space: play/pause · ←/→: 15 s · 1–4: speed (1×/5×/10×/30×) · click the bar to jump", 20,
                new Color(1, 1, 1, 0.7f), new Vector2(1f, 0f), new Vector2(-24, 120), new Vector2(940, 34), TextAnchor.MiddleLeft);
            if (NetSession.Instance.IsHost)
                UiKit.ButtonAt(bg.transform, "NEXT ROUND", new Vector2(1f, 0f), new Vector2(-230, 30), new Vector2(400, 76), () => C.Advance(), UiKit.Good);
            if (d != null) BuildReveal(d);
        }

        static Color EventColor(ReplayEventType t) => t switch
        {
            ReplayEventType.FlagHit => UiKit.MarkRed,
            ReplayEventType.FlagMiss => new Color(1f, 0.5f, 0.5f),
            ReplayEventType.Checkpoint => new Color(1f, 0.85f, 0.2f),
            ReplayEventType.TagHit or ReplayEventType.TagMiss => new Color(0.3f, 1f, 0.8f),
            _ => new Color(0.8f, 0.8f, 0.8f),
        };

        string PlayerName(int id) => C.Players.FirstOrDefault(p => p.Id == id)?.Name ?? $"#{id}";

        string EventText(ReplayEventType t, int who, int target) => t switch
        {
            ReplayEventType.Checkpoint => "Mark completed a stop",
            ReplayEventType.FlagHit => $"Mark flagged {PlayerName(target)} — identified!",
            ReplayEventType.FlagMiss => "Mark flagged a car — miss",
            ReplayEventType.TagHit => $"{PlayerName(who)} tagged the Mark",
            ReplayEventType.TagMiss => $"{PlayerName(who)} tagged the wrong car",
            ReplayEventType.PlateSwap => $"{PlayerName(who)} swapped plates",
            ReplayEventType.CarSwap => $"{PlayerName(who)} swapped cars",
            ReplayEventType.ChopShop => $"{PlayerName(who)} got a respray",
            _ => t.ToString(),
        };

        /// <summary>Position (and travel direction) on a trail at replay time t; false before it starts.</summary>
        static bool TrailAt(List<Vector3> trail, float t, out Vector2 pos, out Vector2 dir)
        {
            pos = dir = Vector2.zero;
            if (trail == null || trail.Count == 0 || t < trail[0].y) return false;
            int lo = 0, hi = trail.Count - 1;
            if (t >= trail[hi].y) { lo = Mathf.Max(0, hi - 1); t = trail[hi].y; }
            else while (hi - lo > 1) { int mid = (lo + hi) / 2; if (trail[mid].y <= t) lo = mid; else hi = mid; }
            var a = trail[lo]; var b = trail[Mathf.Min(lo + 1, trail.Count - 1)];
            float k = b.y > a.y ? Mathf.Clamp01((t - a.y) / (b.y - a.y)) : 1f;
            pos = Vector2.Lerp(new Vector2(a.x, a.z), new Vector2(b.x, b.z), k);
            dir = new Vector2(b.x - a.x, b.z - a.z);
            return true;
        }

        string Where(Vector2 pos, Vector2 dir)
        {
            string grid = MapView.Grid != null ? MapView.Grid.CellName(new Core.Util.Vec2(pos.x, pos.y)) : "?";
            var street = Nav.StreetAt(new Vector3(pos.x, 0f, pos.y), dir.sqrMagnitude > 0.01f ? new Vector3(dir.x, 0f, dir.y) : Vector3.forward);
            return street != null ? $"{grid}, {street}" : grid;
        }

        /// <summary>Round-level stories: who stuck to the Mark, the closest shave, flags.</summary>
        List<string> Highlights(DebriefData d)
        {
            var list = new List<string>();
            if (!d.Trails.TryGetValue(C.MarkId, out var mark) || mark.Count < 2) return list;
            const float Near = 60f;
            (string name, float time)? glued = null;
            (string name, float dist, float at, Vector2 pos, Vector2 dir)? closest = null;
            foreach (var kv in d.Trails)
            {
                if (kv.Key == C.MarkId || kv.Value.Count < 2) continue;
                float near = 0f, best = float.MaxValue, bestAt = 0f; Vector2 bestPos = default, bestDir = default;
                for (int i = 1; i < mark.Count; i++)
                {
                    float t = mark[i].y, dt = mark[i].y - mark[i - 1].y;
                    if (!TrailAt(kv.Value, t, out var tp, out _)) continue;
                    var mp = new Vector2(mark[i].x, mark[i].z);
                    float dist = Vector2.Distance(mp, tp);
                    if (dist < Near) near += dt;
                    if (dist < best) { best = dist; bestAt = t; bestPos = mp; bestDir = mp - new Vector2(mark[i - 1].x, mark[i - 1].z); }
                }
                string name = PlayerName(kv.Key);
                list.Add($"• {name}: within {Near:0} m of the Mark for {UiKit.Clock(near)} — closest {best:0} m at {UiKit.Clock(bestAt)}");
                if (glued == null || near > glued.Value.time) glued = (name, near);
                if (closest == null || best < closest.Value.dist) closest = (name, best, bestAt, bestPos, bestDir);
            }
            if (glued != null && glued.Value.time > 5f) list.Insert(0, $"★ Glued on: {glued.Value.name} ({UiKit.Clock(glued.Value.time)} on the Mark's bumper)");
            if (closest != null && closest.Value.dist < 40f)
                list.Insert(0, $"★ Closest shave: {closest.Value.name}, {closest.Value.dist:0} m from the Mark at {UiKit.Clock(closest.Value.at)} ({Where(closest.Value.pos, closest.Value.dir)})");
            int hits = d.Events.Count(e => e.type == ReplayEventType.FlagHit), misses = d.Events.Count(e => e.type == ReplayEventType.FlagMiss);
            if (hits + misses > 0) list.Add($"• Mark's flags: {hits} hit, {misses} missed");
            int fakes = d.Dwells.Count(x => x.routeIndex < 0);
            if (fakes > 0) list.Add($"• Fake stops: {fakes} (pins there cost points)");
            return list;
        }

        void UpdateReplay()
        {
            if (_reveal != null && !_revealDone) return; // the show first
            var kb = Keyboard.current;
            if (kb != null)
            {
                if (kb.spaceKey.wasPressedThisFrame) _replayPlaying = !_replayPlaying;
                if (kb.leftArrowKey.wasPressedThisFrame) { _replayT = Mathf.Max(0f, _replayT - 15f); _refresh = 0f; }
                if (kb.rightArrowKey.wasPressedThisFrame) { _replayT = Mathf.Min(_replayMax, _replayT + 15f); _refresh = 0f; }
                if (kb.digit1Key.wasPressedThisFrame) _replaySpeed = ReplaySpeeds[0];
                if (kb.digit2Key.wasPressedThisFrame) _replaySpeed = ReplaySpeeds[1];
                if (kb.digit3Key.wasPressedThisFrame) _replaySpeed = ReplaySpeeds[2];
                if (kb.digit4Key.wasPressedThisFrame) _replaySpeed = ReplaySpeeds[3];
            }
            if (_replayPlaying)
            {
                _replayT += Time.deltaTime * _replaySpeed;
                if (_replayT > _replayMax + 3f * _replaySpeed) _replayT = 0f; // loop after a short hold on the end
            }
            if (_scrub != null) _scrub.SetFraction(_replayT / _replayMax);
            if (_replayClock != null)
                _replayClock.text = $"{(_replayPlaying ? "▶" : "❚❚")}  {UiKit.Clock(Mathf.Min(_replayT, _replayMax))} / {UiKit.Clock(_replayMax)}   {_replaySpeed:0}×";
        }

        void DrawDebriefMap()
        {
            var d = C.Debrief;
            if (_map == null || d == null) return;
            _map.ClearMarkers();
            var town = TownBuilder.Instance;
            float t = Mathf.Min(_replayT, _replayMax);
            const float Recent = 60f;

            // Stops appear when the Mark pulled in; checkpoints are numbered, fakes marked.
            foreach (var dw in d.Dwells.Where(x => x.start <= t))
            {
                if (dw.routeIndex >= 0)
                {
                    bool safe = dw.routeIndex == d.Stops.Count - 1;
                    var st = d.Stops.ElementAtOrDefault(dw.routeIndex);
                    _map.Marker(dw.pos, st.foundBy >= 0 ? UiKit.Good : new Color(1f, 0.85f, 0.2f), 26, safe ? "SAFE" : (dw.routeIndex + 1).ToString(), 20, diamond: true);
                }
                else _map.Marker(dw.pos, new Color(1f, 0.55f, 0.1f), 18, "fake stop", 15, diamond: true);
            }
            // The Tails' final marks (placed after the drive): green = found a stop, grey = a wrong guess.
            foreach (var pin in d.Pins)
                _map.Marker(pin.pos, d.Stops.Any(x => x.pinId == pin.id) ? UiKit.Good : new Color(0.6f, 0.6f, 0.6f), 13);

            // Trails: faint history, bright last minute, a dot + name at the playhead.
            var now = new Dictionary<int, (Vector2 pos, Vector2 dir)>();
            foreach (var kv in d.Trails)
            {
                var col = _replayColors.TryGetValue(kv.Key, out var c) ? c : Color.white;
                bool isMark = kv.Key == C.MarkId;
                var old = new List<Vector2>(); var recent = new List<Vector2>();
                foreach (var p in kv.Value)
                {
                    if (p.y > t) break;
                    var v = new Vector2(p.x, p.z);
                    if (p.y < t - Recent) old.Add(v);
                    else { if (recent.Count == 0 && old.Count > 0) recent.Add(old[old.Count - 1]); recent.Add(v); }
                }
                _map.Line(old, new Color(col.r, col.g, col.b, 0.45f), isMark ? 3.5f : 2.5f);
                if (!TrailAt(kv.Value, t, out var pos, out var dir)) continue;
                recent.Add(pos);
                _map.Line(recent, col, isMark ? 6f : 4.5f);
                now[kv.Key] = (pos, dir);
            }
            // Who's close to the Mark right now: a tether line and the gap.
            var nowLines = new List<string>();
            if (now.TryGetValue(C.MarkId, out var m))
            {
                nowLines.Add($"<b>At {UiKit.Clock(t)}</b> — the Mark is in {Where(m.pos, m.dir)}, heading {MapGrid.Heading(new Core.Util.Vec2(m.dir.x, m.dir.y))}");
                foreach (var kv in now.Where(x => x.Key != C.MarkId).OrderBy(x => Vector2.Distance(x.Value.pos, m.pos)))
                {
                    float dist = Vector2.Distance(kv.Value.pos, m.pos);
                    if (dist < 80f) _map.Line(new List<Vector2> { m.pos, kv.Value.pos }, new Color(1f, 1f, 0.4f, 0.9f), 2f);
                    nowLines.Add($"  {PlayerName(kv.Key)}: {dist:0} m {(dist < 25f ? "— right on his bumper!" : dist < 60f ? "— close" : "")}");
                }
            }
            foreach (var kv in now)
                _map.Marker(kv.Value.pos, _replayColors.TryGetValue(kv.Key, out var c) ? c : Color.white, kv.Key == C.MarkId ? 22 : 18,
                    PlayerName(kv.Key) + (kv.Key == C.MarkId ? " (Mark)" : ""), 17, diamond: kv.Key == C.MarkId);

            // Flags, tags and swaps pop up where they happened; recent ones are labelled.
            var log = new List<(float time, string text)>();
            foreach (var e in d.Events.Where(x => x.time <= t))
            {
                bool fresh = t - e.time < 25f;
                string label = e.type switch
                {
                    ReplayEventType.FlagHit => "FLAGGED " + PlayerName(e.target),
                    ReplayEventType.FlagMiss => "flag: miss",
                    ReplayEventType.TagHit => "TAG",
                    ReplayEventType.TagMiss => "bad tag",
                    ReplayEventType.Checkpoint => null,
                    _ => "swap",
                };
                if (label != null) _map.Marker(e.pos, EventColor(e.type), fresh ? 20 : 12, fresh ? label : null, 16);
                log.Add((e.time, EventText(e.type, e.player, e.target)));
            }
            foreach (var dw in d.Dwells.Where(x => x.start <= t && x.routeIndex < 0))
                log.Add((dw.start, $"Mark pulled a fake stop in {Where(dw.pos, Vector2.zero)}"));
            if (_replayNow != null) _replayNow.text = string.Join("\n", nowLines);
            if (_replayLog != null)
                _replayLog.text = "EVENTS\n" + string.Join("\n", log.OrderBy(l => l.time).Skip(Mathf.Max(0, log.Count - 8)).Select(l => $"{UiKit.Clock(l.time)}  {l.text}"));
        }

        void BuildMatchOver()
        {
            var bg = UiKit.Panel(_screen, "MatchOver", UiKit.Ink, new Vector2(0.5f, 0.5f), Vector2.zero, new Vector2(1000, 800));
            UiKit.LabelAt(bg.transform, "MATCH OVER", 80, UiKit.Accent, new Vector2(0.5f, 1f), new Vector2(0, -40), new Vector2(900, 100), TextAnchor.UpperCenter);
            var lines = C.Players.OrderByDescending(p => p.Score).Select((p, i) => $"{i + 1}. {p.Name} — {p.Score} round win{(p.Score == 1 ? "" : "s")}");
            UiKit.LabelAt(bg.transform, string.Join("\n", lines), 40, Color.white, new Vector2(0.5f, 1f), new Vector2(0, -180), new Vector2(800, 500), TextAnchor.UpperCenter);
            if (NetSession.Instance.IsHost)
                UiKit.ButtonAt(bg.transform, "PLAY AGAIN", new Vector2(0.5f, 0f), new Vector2(-140, 70), new Vector2(460, 80), () => C.StartMatch(), UiKit.Good);
            UiKit.ButtonAt(bg.transform, "LEAVE", new Vector2(0.5f, 0f), NetSession.Instance.IsHost ? new Vector2(250, 70) : new Vector2(0, 70), new Vector2(240, 80), () => Boot.ReturnToMenu(), UiKit.MarkRed);
        }

        // ---- per-frame ---------------------------------------------------------------

        void HandleKeys()
        {
            var kb = Keyboard.current;
            if (kb == null) return;
            bool typing = EventSystem.current != null && EventSystem.current.currentSelectedGameObject != null &&
                          EventSystem.current.currentSelectedGameObject.GetComponent<InputField>() != null;
            if (kb.f1Key.wasPressedThisFrame) _helpOpen = !_helpOpen;
            // F10: leave the session (hosting ends it for everyone).
            if (kb.f10Key.wasPressedThisFrame && Boot != null && Boot.Current != GameBootstrap.Mode.Menu && Boot.Current != GameBootstrap.Mode.Offline) { Boot.ReturnToMenu(); return; }
            bool driving = C != null && C.Connected && (C.Phase == Phase.Driving || C.Phase == Phase.Pickup);
            if (kb.tabKey.wasPressedThisFrame && driving) _notepadOpen = !_notepadOpen;
            if (typing) return;
            bool offline = Boot != null && Boot.Current == GameBootstrap.Mode.Offline;
            if ((driving || offline) && kb.mKey.wasPressedThisFrame)
            {
                // M: map out / stowed. Shift+M: spread it out full-screen (stopped, for pins).
                if (_mapOpen) _mapOpen = false;
                else if (kb.shiftKey.isPressed && driving) _mapOpen = true;
                else _mapOut = !_mapOut;
            }
            if (driving && kb.gKey.wasPressedThisFrame && C.MyRole == Role.Mark) _flagOpen = !_flagOpen;
            if (kb.escapeKey.wasPressedThisFrame) { _mapOpen = _flagOpen = _notepadOpen = _swapOpen = _mapOut = false; }
            if (driving && C.MyRole == Role.Tail && kb.rKey.wasPressedThisFrame && (AtRentalBay() || _swapOpen)) _swapOpen = !_swapOpen;
            if (driving && C.MyRole == Role.Mark && kb.fKey.wasPressedThisFrame && !_flagOpen) StartCoroutine(Snapshot());
            if (driving && C.MyRole == Role.Tail && kb.tKey.wasPressedThisFrame) TryTag();

            // Plate swap: hold P while stopped in a petrol station bay.
            if (driving && C.MyRole == Role.Tail && kb.pKey.isPressed && PlayerCar.Local != null && Mathf.Abs(PlayerCar.Local.ForwardSpeed) < 0.5f)
            {
                _plateHold += Time.deltaTime;
                if (_plateHold >= 8.2f && !_plateSent) { C.ServiceHold(_plateHold); _plateSent = true; }
            }
            else { _plateHold = 0f; _plateSent = false; }
        }

        void TryTag()
        {
            var cam = Camera.main;
            if (cam == null) return;
            var ray = cam.ViewportPointToRay(new Vector3(0.5f, 0.5f));
            if (Physics.Raycast(ray, out var hit, 250f, 1 << Layers.Vehicles, QueryTriggerInteraction.Ignore))
            {
                var v = hit.collider.GetComponentInParent<VehicleView>();
                if (v != null) { C.Tag(v.VehicleId); return; }
            }
            C.Notices.Add(("Aim at a car (screen centre) and press T to tag it", Time.time));
        }

        IEnumerator Snapshot()
        {
            if (_snapshots.Count >= 6) { C.Notices.Add(("Out of film (6 snapshots)", Time.time)); yield break; }
            yield return new WaitForEndOfFrame();
            // Render the camera (not the HUD) to a texture: what you see is what you get, blur included.
            var cam = Camera.main;
            var rt = RenderTexture.GetTemporary(1280, 720, 24);
            var prev = cam.targetTexture;
            cam.targetTexture = rt;
            cam.Render();
            cam.targetTexture = prev;
            var active = RenderTexture.active;
            RenderTexture.active = rt;
            var tex = new Texture2D(1280, 720, TextureFormat.RGB24, false);
            tex.ReadPixels(new Rect(0, 0, 1280, 720), 0, 0);
            tex.Apply();
            RenderTexture.active = active;
            RenderTexture.ReleaseTemporary(rt);
            _snapshots.Add(tex);
            C.Notices.Add(($"Snapshot {_snapshots.Count}/6 taken", Time.time));
        }

        void UpdateDynamic()
        {
            var now = Time.time;
            // Nothing may cover a mirror you're glancing at (or a plate you're focusing on).
            var dir = CameraDirector.Instance;
            bool glancing = dir != null && dir.Mode != CameraMode.Free && (dir.Glance != Glance.None || (dir.Focusing && dir.Mode == CameraMode.Cockpit));
            if (_lapPaper != null) _lapPaper.SetActive(!glancing);
            if (_targetCard != null) _targetCard.SetActive(Keyboard.current != null && Keyboard.current.iKey.isPressed && !CarInput.Blocked);
            UpdateLapMap();
            if (_help != null) _help.enabled = !glancing && _lapPaper == null; // the lap map sits where the help is
            if (_notices != null && C != null)
                _notices.text = C.Phase == Phase.Debrief ? "" : string.Join("\n", C.Notices.Where(n => now - n.time < 12f).Select(n => n.text));

            if (_help != null)
            {
                string help = !_helpOpen ? "F1: help" :
                    "W/S drive · A/D steer · Space handbrake\nZ/C indicators · X hazards · H horn\nL: lane-keep · K: cruise (brake cancels)\nMouse: look (click to capture) · Q/E lean\nHold 1/2/3: glance left / rear-view / right mirror\nHold right mouse: focus (read plates)\nV: cockpit / chase camera · Tab: notepad\nM: map out / stow (wheel or +/-: zoom) · Shift+M: full map\n";
                if (C != null && C.MyRole == Role.Mark) help += "F: snapshot · G: flag form (at a checkpoint)\n";
                if (C != null && C.MyRole == Role.Tail) help += "Hold I: target card · T: tag the Mark (aim) · hold P at a Gas bay: plate swap · R at Rentals: swap car\nHold B: team radio\n";
                if (C != null && C.Connected) help += "Y: windows up/down · N: mute mic · F10: leave\n";
                help += "F1: hide help";
                _help.text = help;
            }
            if (C == null || !C.Connected) return;

            if (_timer != null) _timer.text = UiKit.Clock(C.PhaseDuration - C.PhaseTime);
            var town = TownBuilder.Instance;

            switch (C.Phase)
            {
                case Phase.Lobby:
                    if (_status != null)
                        _status.text = string.Join("\n", C.Players.Select(p => $"• {p.Name}{(p.Id == C.MyId ? " (you)" : "")}")) + (C.Players.Count < 2 ? "\n\nNeed at least 2 players." : "");
                    break;
                case Phase.MotorPool:
                    if (_status != null && C.Me != null)
                    {
                        var me = C.Me;
                        int cost = Match.Cost(me.PickedModel, me.PickedColor);
                        _status.text = $"Your car: {VehicleCatalog.Colors[me.PickedColor].Name} {VehicleCatalog.Models[me.PickedModel].FullName}  (cost {cost}/5)\n" +
                                       $"Ready: {C.Players.Count(p => p.Role == Role.Tail && p.Ready)}/{C.Players.Count(p => p.Role == Role.Tail)}";
                    }
                    break;
                case Phase.Briefing:
                    if (_status != null && C.MyRole == Role.Mark)
                        _status.text = string.Join("\n", _routeDraft.Select((p, i) => $"{i + 1}. {town.Network.Pois[p].Name}")) +
                                       (C.Safehouse >= 0 ? $"\nLast: {town.Network.Pois[C.Safehouse].Name} (safehouse)" : "");
                    break;
                case Phase.Marking:
                    if ((_refresh -= Time.deltaTime) <= 0f) { _refresh = 0.25f; DrawMarking(); }
                    break;
                case Phase.Debrief:
                    UpdateReveal();
                    UpdateReplay();
                    if ((_refresh -= Time.deltaTime) <= 0f) { _refresh = 0.1f; DrawDebriefMap(); }
                    break;
            }

            if (C.Phase == Phase.Driving || C.Phase == Phase.Pickup) UpdateDrivingHud();
        }

        /// <summary>Which bay the local player is being guided to, if any: the Mark's next stop, a burned Tail's body shop.</summary>
        int NavTargetBay(PlayerCar car)
        {
            var town = TownBuilder.Instance;
            if (car == null) return -1;
            var p = new Core.Util.Vec2(car.transform.position.x, car.transform.position.z);
            int Nearest(IEnumerable<int> bays) => bays.OrderBy(b => Core.Util.Vec2.Distance(Match.BayCentre(town.Lanes.Bays[b]), p)).DefaultIfEmpty(-1).First();
            if (C.MyRole == Role.Mark && C.NextCheckpoint < C.Route.Count) return Nearest(town.Lanes.Sites[C.Route[C.NextCheckpoint]].Bays);
            if (C.MyRole == Role.Tail && C.Me != null && C.Me.NeedsChopShop)
            {
                var shop = town.Network.Pois.Where(x => x.Type == PoiType.ChopShop).OrderBy(x => Core.Util.Vec2.Distance(town.Lanes.Sites[x.Id].Centre, p)).FirstOrDefault();
                if (shop != null) return Nearest(town.Lanes.Sites[shop.Id].Bays);
            }
            return -1;
        }

        void UpdateNav(PlayerCar car)
        {
            if ((_navTimer -= Time.deltaTime) > 0f) return;
            _navTimer = 0.5f;
            _street = car != null ? Nav.StreetAt(car.transform.position, car.transform.forward) : null;
            if (C.MyRole == Role.Mark && C.Route.Count > 0) RouteBeacons.Sync(C.Route, C.NextCheckpoint); else RouteBeacons.Hide();
            int bay = NavTargetBay(car);
            _navPoints = null;
            string text = "";
            if (bay >= 0)
            {
                // A destination, not directions: the name and address. You find your own way (map: M);
                // the bay itself only lights up once you're close enough to be looking for it.
                var town = TownBuilder.Instance;
                var b = town.Lanes.Bays[bay];
                var centre = Match.BayCentre(b);
                string street = town.Network.Edges[town.Lanes.Lanes[b.AccessLane].EdgeId].StreetName;
                string grid = MapView.Grid != null ? MapView.Grid.CellName(centre) : "";
                string what = C.MyRole == Role.Mark ? (C.NextCheckpoint == C.Route.Count - 1 ? "Safehouse" : $"Stop {C.NextCheckpoint + 1}") : "Body shop";
                float dist = Vector2.Distance(new Vector2(centre.X, centre.Y), new Vector2(car.transform.position.x, car.transform.position.z));
                text = dist < 25f ? "Pull into the marked bay" : $"{what}: {town.Network.Pois[b.PoiId].Name} — {grid}, {street}";
                if (dist < 80f) BayBeacon.Show(new Vector3(centre.X, 0.02f, centre.Y), C.MyRole == Role.Mark ? UiKit.MarkRed : new Color(1f, 0.55f, 0.1f));
                else BayBeacon.Hide();
            }
            else BayBeacon.Hide();
            if (_navLabel != null) _navLabel.text = DriveAssist.Status.Length > 0 ? (text.Length > 0 ? text + "\n" : "") + DriveAssist.Status : text;
            NavText = text;
        }

        void UpdateDrivingHud()
        {
            var town = TownBuilder.Instance;
            var car = PlayerCar.Local;
            var me = C.Me;
            UpdateNav(car);
            if (_status != null)
            {
                if (C.MyRole == Role.Mark && C.Route.Count > 0 && C.NextCheckpoint < C.Route.Count)
                {
                    int poi = C.Route[C.NextCheckpoint];
                    bool safe = C.NextCheckpoint == C.Route.Count - 1;
                    var site = town.Lanes.Sites[poi].Centre;
                    float d = car != null ? Vector2.Distance(new Vector2(car.transform.position.x, car.transform.position.z), new Vector2(site.X, site.Y)) : 0f;
                    _status.text = $"{(safe ? "SAFEHOUSE" : $"Stop {C.NextCheckpoint + 1}/{C.Route.Count - 1}")}: {town.Network.Pois[poi].Name} · {d:0} m · flags {C.FlagTokens}" +
                                   (_street != null ? $"\non {_street}" : "");
                    if (_arrow != null && car != null)
                    {
                        var to = new Vector3(site.X, 0, site.Y) - car.transform.position;
                        float ang = Vector3.SignedAngle(Vector3.ProjectOnPlane(car.transform.forward, Vector3.up), to, Vector3.up);
                        _arrow.localRotation = Quaternion.Euler(0, 0, -ang);
                    }
                }
                else if (C.MyRole == Role.Tail && me != null)
                    _status.text = "Watch where the Mark stops" + (_street != null ? $"\non {_street}" : "");
                else if (C.MyRole == Role.Spotter) _status.text = "Spotter: watch the cameras, tell your team.";
            }

            if (_center != null)
            {
                string msg = "";
                float progress = -1f;
                if (C.MyRole == Role.Mark && C.Dwelling)
                {
                    msg = C.DwellIsCheckpoint ? (C.FlagWindowOpen ? "At checkpoint — hold still. Check your mirrors! G: file a flag" : "")
                                              : "Dry-cleaning stop (fake stop) — no marker; Tails who mark it waste a guess";
                    progress = C.DwellProgress;
                }
                if (C.MyRole == Role.Tail && _plateHold > 0.2f) { msg = "Swapping plates... (only works in a Gas bay)"; progress = _plateHold / 8f; }
                else if (C.MyRole == Role.Tail && me != null && !me.NeedsChopShop && AtRentalBay()) msg = "Rentals: press R to swap cars";
                if (C.MyRole == Role.Tail && me != null && me.NeedsChopShop && C.Team.TryGetValue(C.MyId, out var t) && t.service > 0f)
                { msg = "Body shop: new car incoming..."; progress = t.service / 5f; }
                _center.text = msg;
                if (_progress != null)
                {
                    _progress.gameObject.SetActive(progress >= 0f);
                    if (progress >= 0f) _progressFill.rectTransform.sizeDelta = new Vector2(600f * Mathf.Clamp01(progress), 0f);
                }
            }

            if (_banner != null && C.Phase != Phase.Pickup) _banner.text = "";
            if (_flagStatus != null)
                _flagStatus.text = (C.FlagWindowOpen ? "Flag window OPEN — you're dwelling at a checkpoint" : "Flags can only be filed while dwelling at a checkpoint") +
                                   $" · tokens left: {C.FlagTokens}";
            var vc = Tailed.Audio.VoiceChat.Instance;
            if (_voice != null && vc != null)
            {
                string windows = car != null ? (car.WindowsOpen ? "WINDOWS DOWN (you can be overheard)" : "windows up") : "";
                string mic = Tailed.Audio.VoiceChat.Muted ? "mic MUTED" : vc.Speaking ? "mic ●" : "mic ○";
                string radio = vc.Radio ? "  RADIO ON AIR" : "";
                var talkers = vc.RadioTalkers.Where(kv => Time.time - kv.Value < 0.4f).Select(kv => C.Players.FirstOrDefault(p => p.Id == kv.Key)?.Name).Where(n => n != null);
                string heard = talkers.Any() ? "   radio: " + string.Join(", ", talkers) : "";
                _voice.text = $"{windows}   {mic}{radio}{heard}";
                _voice.color = car != null && car.WindowsOpen ? UiKit.Accent : Color.white;
            }
            if (_cctv != null && SpotterCams.Instance != null && SpotterCams.Instance.Cams.Count > 0)
                _cctv.text = ((int)(Time.time * 2) % 2 == 0 ? "● REC  " : "     ") + SpotterCams.Instance.Cams[SpotterCams.Instance.Index].name;

            // Tagged Mark marker (for Tails who can see it).
            if (_tagMarker != null)
            {
                bool show = false;
                if (C.TaggedVehicle >= 0 && Time.time < C.TaggedUntil && C.MyRole != Role.Mark)
                {
                    VehicleView v = null;
                    if (TrafficReplica.Instance != null) TrafficReplica.Instance.TryGetView(C.TaggedVehicle, out v);
                    if (v == null) TrafficRunner.Instance.TryGetView(C.TaggedVehicle, out v);
                    var cam = Camera.main;
                    if (v != null && cam != null)
                    {
                        var wp = v.transform.position + Vector3.up * 2.6f;
                        var sp = cam.WorldToScreenPoint(wp);
                        bool los = !Physics.Linecast(cam.transform.position, wp, Layers.BuildingsMask);
                        if (sp.z > 0 && los)
                        {
                            show = true;
                            var scaler = _canvas.GetComponent<RectTransform>().localScale.x;
                            _tagMarker.anchoredPosition = new Vector2(sp.x, sp.y) / scaler;
                        }
                    }
                }
                _tagMarker.gameObject.SetActive(show);
            }

            if ((_refresh -= Time.deltaTime) <= 0f)
            {
                _refresh = 0.25f;
                DrawMapMarkers(_map, small: false);
            }
        }

        /// <summary>
        /// The lap map (M) and the dashboard compass. Stowed, you only get the compass: to know your
        /// grid square and street you pull the map out (or read the street signs).
        /// </summary>
        void BuildLapMap()
        {
            _compass = UiKit.LabelAt(_screen, "", 30, Color.white, new Vector2(0.5f, 0f), new Vector2(0, 26), new Vector2(360, 44), TextAnchor.MiddleCenter);
            var o = _compass.gameObject.AddComponent<Outline>();
            o.effectColor = new Color(0, 0, 0, 0.8f);
            o.effectDistance = new Vector2(2, -2);
            if (!_mapOut) return;
            // Held over on the passenger side: you can drive with it out, but it blocks that side of the view.
            const float size = 560f;
            var paper = UiKit.Panel(_screen, "LapMap", new Color(0.94f, 0.9f, 0.8f, 0.98f), new Vector2(1f, 0f), new Vector2(-24, 24), new Vector2(size + 40, size + 110));
            _lapPaper = paper.gameObject;
            _minimap = MapView.Create(paper.transform, new Vector2(0.5f, 0f), new Vector2(0, 14), size);
            _lapInfo = UiKit.LabelAt(paper.transform, "", 30, new Color(0.12f, 0.12f, 0.16f), new Vector2(0.5f, 1f), new Vector2(0, -12), new Vector2(size + 20, 70), TextAnchor.UpperCenter);
            _lapRefresh = 0f;
        }

        float _lapZoom = 1f;

        void UpdateLapMap()
        {
            var car = PlayerCar.Local;
            // Lap map zoom: mouse wheel or +/- (the cursor is captured while driving), centred on the car.
            if (_minimap != null && _lapPaper != null)
            {
                var kb = Keyboard.current; var mouse = Mouse.current;
                float wheel = mouse != null ? mouse.scroll.ReadValue().y : 0f;
                if (kb != null && (kb.equalsKey.wasPressedThisFrame || kb.numpadPlusKey.wasPressedThisFrame)) wheel = 1f;
                if (kb != null && (kb.minusKey.wasPressedThisFrame || kb.numpadMinusKey.wasPressedThisFrame)) wheel = -1f;
                if (Mathf.Abs(wheel) > 0.01f) _lapZoom = Mathf.Clamp(_lapZoom * (wheel > 0 ? 1.25f : 0.8f), 1f, MapView.MaxZoom);
                if (Mathf.Abs(_minimap.Zoom - _lapZoom) > 0.001f) _minimap.SetZoom(_lapZoom);
                if (car != null) _minimap.CentreOn(new Vector2(car.transform.position.x, car.transform.position.z));
            }
            if (_compass != null)
            {
                if (car == null) _compass.text = "";
                else
                {
                    var f = car.transform.forward;
                    var dir = new Core.Util.Vec2(f.x, f.z);
                    _compass.text = $"{MapGrid.HeadingShort(dir)}  {MapGrid.Bearing(dir):000}°";
                }
            }
            if (_minimap == null || (_lapRefresh -= Time.deltaTime) > 0f) return;
            _lapRefresh = 0.25f;
            if (car != null && _lapInfo != null && MapView.Grid != null)
            {
                var p = car.transform.position; var f = car.transform.forward;
                _lapStreet = Nav.StreetAt(p, f);
                var at = new Core.Util.Vec2(p.x, p.z);
                _lapInfo.text = $"<b>{MapView.Grid.CellName(at)}</b> · {_lapStreet ?? "off-road"} · heading {MapGrid.Heading(new Core.Util.Vec2(f.x, f.z))}";
            }
            bool inMatch = C != null && C.Connected && (C.Phase == Phase.Driving || C.Phase == Phase.Pickup);
            if (inMatch) { DrawMapMarkers(_minimap, small: false); return; }
            _minimap.ClearMarkers();
            StreetLabels(_minimap);
            if (car != null)
            {
                var p = car.transform.position;
                var m = _minimap.Marker(new Vector2(p.x, p.z), Color.white, 22);
                m.localRotation = Quaternion.Euler(0, 0, -car.transform.eulerAngles.y + 45);
            }
        }

        /// <summary>
        /// Street names along every stretch of every street: each connected run of a street gets a
        /// label, repeated about every 350 m, turned to run along the road.
        /// </summary>
        static void StreetLabels(MapView map)
        {
            if (map.HasStreetLabels) return;
            map.HasStreetLabels = true;
            float k = Mathf.Clamp(map.Size / 980f, 0.6f, 1f);
            var net = TownBuilder.Instance.Network;
            float Every = 350f * Mathf.Max(1f, 980f / map.Size); // same on-screen spacing on small maps
            foreach (var group in net.Edges.GroupBy(e => e.StreetName))
            {
                bool vertical = group.All(e => Mathf.Abs(net.Nodes[e.B].Position.X - net.Nodes[e.A].Position.X) < Mathf.Abs(net.Nodes[e.B].Position.Y - net.Nodes[e.A].Position.Y));
                // Order along the street, then split where consecutive edges don't share a node.
                var edges = group.OrderBy(e => vertical ? Mathf.Min(net.Nodes[e.A].Position.Y, net.Nodes[e.B].Position.Y)
                                                       : Mathf.Min(net.Nodes[e.A].Position.X, net.Nodes[e.B].Position.X)).ToList();
                var run = new List<RoadEdge>();
                void Flush()
                {
                    if (run.Count == 0) return;
                    float total = run.Sum(e => net.Length(e));
                    int labels = Mathf.Max(1, Mathf.RoundToInt(total / Every));
                    for (int i = 0; i < labels; i++)
                    {
                        float at = total * (i + 0.5f) / labels, acc = 0f;
                        foreach (var e in run)
                        {
                            float len = net.Length(e);
                            if (acc + len >= at)
                            {
                                var a = net.Nodes[e.A].Position; var b = net.Nodes[e.B].Position;
                                var p = Core.Util.Vec2.Lerp(a, b, (at - acc) / Mathf.Max(len, 1f));
                                map.Label(new Vector2(p.X, p.Y), group.Key, Mathf.Max(12, Mathf.RoundToInt((e.Class == RoadClass.Arterial ? 19 : 15) * k)),
                                    new Color(0.1f, 0.1f, 0.14f), vertical ? 90f : 0f, persistent: true);
                                break;
                            }
                            acc += len;
                        }
                    }
                    run.Clear();
                }
                foreach (var e in edges)
                {
                    if (run.Count > 0)
                    {
                        var last = run[run.Count - 1];
                        bool joined = e.A == last.A || e.A == last.B || e.B == last.A || e.B == last.B;
                        if (!joined) Flush();
                    }
                    run.Add(e);
                }
                Flush();
            }
        }

        void DrawMapMarkers(MapView map, bool small)
        {
            if (map == null) return;
            map.ClearMarkers();
            var town = TownBuilder.Instance;
            var car = PlayerCar.Local;
            float s = small ? 0.6f : 1f;
            if (C.MyRole == Role.Mark)
            {
                // Every stop, numbered: red to visit (the next one bigger), green once visited.
                for (int i = 0; i < C.Route.Count; i++)
                {
                    var c = town.Lanes.Sites[C.Route[i]].Centre;
                    bool next = i == C.NextCheckpoint, done = i < C.NextCheckpoint;
                    string name = i == C.Route.Count - 1 ? "SAFEHOUSE" : $"{i + 1}. {town.Network.Pois[C.Route[i]].Name}";
                    map.Marker(new Vector2(c.X, c.Y), done ? UiKit.Good : UiKit.MarkRed, (next ? 30 : 22) * s,
                        small ? null : done ? name + " (visited)" : name, 18, diamond: true);
                }
            }
            else
            {
                foreach (var pin in C.Pins) map.Marker(pin.pos, pin.player == C.MyId ? UiKit.TailBlue : new Color(0.6f, 0.8f, 1f), 16 * s);
                foreach (var kv in C.Team)
                {
                    if (kv.Key == C.MyId) continue;
                    var name = C.Players.FirstOrDefault(p => p.Id == kv.Key)?.Name;
                    map.Marker(kv.Value.pos, UiKit.Good, 18 * s, small ? null : name, 16);
                }
                if (C.Phase == Phase.Pickup && C.StartPoi >= 0)
                {
                    var c = town.Lanes.Sites[C.StartPoi].Centre;
                    map.Marker(new Vector2(c.X, c.Y), UiKit.MarkRed, 26 * s, small ? null : "Mark starts here", 18, diamond: true);
                }
                if (C.Me != null && C.Me.NeedsChopShop)
                    foreach (var poi in town.Network.Pois.Where(p => p.Type == PoiType.ChopShop))
                    {
                        var c = town.Lanes.Sites[poi.Id].Centre;
                        map.Marker(new Vector2(c.X, c.Y), new Color(1f, 0.5f, 0.1f), 24 * s, small ? null : poi.Name, 18, diamond: true);
                    }
                if (!small)
                    foreach (var poi in town.Network.Pois.Where(p => p.Type == PoiType.Petrol))
                    {
                        var c = town.Lanes.Sites[poi.Id].Centre;
                        map.Marker(new Vector2(c.X, c.Y), new Color(1f, 0.9f, 0.3f, 0.7f), 12, "Gas", 14);
                    }
            }
            if (_navPoints != null) map.Line(_navPoints, new Color(1f, 0.85f, 0.2f, 0.95f), small ? 4f : 6f);
            if (!small) StreetLabels(map);
            if (car != null)
            {
                var p = car.transform.position;
                var m = map.Marker(new Vector2(p.x, p.z), Color.white, 18 * s + 4);
                m.localRotation = Quaternion.Euler(0, 0, -car.transform.eulerAngles.y + 45);
            }
        }
    }
}
