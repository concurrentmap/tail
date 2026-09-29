using System.Collections.Generic;
using System.Linq;
using Tailed.Core.Roads;
using Tailed.Map;
using Tailed.Traffic;
using UnityEngine;
using UnityEngine.InputSystem;

namespace Tailed.Cameras
{
    /// <summary>
    /// Camera presets for inspection and automated screenshots. Ctrl+F5..F9 in play mode, or from
    /// WSL: tools/bridge.sh execute Tailed.Cameras.DevViews.Street
    /// </summary>
    public sealed class DevViews : MonoBehaviour
    {
        static int _streetIndex;

        void Update()
        {
            // Dev presets on Ctrl+F5..F9 (Ctrl+F12 = new town, offline only): never clash with game keys.
            var kb = Keyboard.current;
            if (kb == null || !kb.ctrlKey.isPressed) return;
            if (kb.f5Key.wasPressedThisFrame) Overview();
            if (kb.f6Key.wasPressedThisFrame) Downtown();
            if (kb.f7Key.wasPressedThisFrame) Suburb();
            if (kb.f8Key.wasPressedThisFrame) Industrial();
            if (kb.f9Key.wasPressedThisFrame) Street();
            if (kb.f12Key.wasPressedThisFrame && Tailed.Net.NetSession.Instance == null) TownBuilder.NextSeed();
        }

        /// <summary>Vertex counts of the town's combined meshes.</summary>
        public static string MeshStats()
        {
            var sb = new System.Text.StringBuilder();
            foreach (Transform c in TownBuilder.Instance.transform)
            {
                int v = 0, n = 0;
                foreach (var mf in c.GetComponentsInChildren<MeshFilter>()) if (mf.sharedMesh != null) { v += mf.sharedMesh.vertexCount; n++; }
                if (n > 0) sb.Append($"{c.name}={v / 1000}k/{n} ");
            }
            foreach (var k in new[] { "House_Bungalow", "House_TwoStorey", "Fence", "FenceBoard", "Hedge", "Tree_Round", "Tree_Pine", "Flowerbed", "Bins" })
                sb.Append($"| {k}={Tailed.Map.ArtKit.Get(k)?.vertexCount} ");
            return sb.ToString();
        }

        /// <summary>Frame counter / timing probe for automated checks.</summary>
        public static string Stats()
        {
            var sim = Tailed.Traffic.TrafficRunner.Instance?.Sim;
            return $"frame={Time.frameCount} t={Time.realtimeSinceStartup:0.0} dt={Time.smoothDeltaTime * 1000f:0.0}ms " +
                   $"vehicles={(sim != null ? sim.NpcCount : -1)} simTime={(sim != null ? sim.Time : 0):0.0}";
        }

        static GameObject _lineup;

        /// <summary>All models in a row on the grass outside town, side-on: silhouette check.</summary>
        public static string Lineup() => LineupAt(side: true);
        public static string LineupRear() => LineupAt(side: false);

        static float _lineupEnd;

        static string LineupAt(bool side)
        {
            const float x0 = -170f, gap = 7.5f, z = 60f;
            int n = Tailed.Core.Identity.VehicleCatalog.Models.Length;
            if (_lineup == null)
            {
                _lineup = new GameObject("Lineup");
                var ids = new Tailed.Core.Identity.IdentityService(99);
                float x = x0;
                for (int m = 0; m < n; m++)
                {
                    var model = Tailed.Core.Identity.VehicleCatalog.Models[m];
                    var v = Tailed.Vehicles.VehicleView.Create(_lineup.transform);
                    v.Bind(1000 + m, ids.Create(m, model.Livery >= 0 ? model.Livery : (m * 5) % Tailed.Core.Identity.VehicleCatalog.Colors.Length));
                    if (m > 0) x += Mathf.Max(gap, model.Length + 3f);
                    v.SetPose(new Vector3(x, 0f, z), Vector3.right, 0f, Tailed.Core.Traffic.VehicleFlags.None, 0f);
                }
                _lineupEnd = x;
            }
            float mid = (x0 + _lineupEnd) * 0.5f;
            return side ? Place(new Vector3(mid, 1.4f, z - (_lineupEnd - x0) * 0.62f), new Vector3(mid, 1.0f, z), 50f)
                        : Place(new Vector3(x0 - 12f, 1.3f, z), new Vector3(x0, 0.9f, z), 40f);
        }

        static int _plateDist;

        /// <summary>
        /// Unzoomed (cockpit FOV) look at the first lineup car's rear plate from the driver's eye height,
        /// cycling 15/25/35/45/55 m. No readouts: this is the plate itself.
        /// </summary>
        public static string PlateDistance()
        {
            LineupAt(false);
            float[] d = { 15f, 25f, 35f, 45f, 55f };
            float dist = d[_plateDist++ % d.Length];
            var car = _lineup.transform.GetChild(0);
            var plate = car.position + Vector3.up * 0.45f - car.forward * 2.3f;
            var eye = plate - car.forward * dist + Vector3.up * 0.8f;
            return Place(eye, plate + Vector3.up * 0.8f, 60f) + $" {dist} m";
        }

        static int _plateCheck;

        /// <summary>Close three-quarter view of each lineup model's plates, alternating rear/front (cycles).</summary>
        public static string PlateCheck()
        {
            LineupAt(false);
            int n = _lineup.transform.childCount;
            int i = _plateCheck / 2 % n;
            bool front = _plateCheck % 2 == 1;
            _plateCheck++;
            var car = _lineup.transform.GetChild(i);
            var v = car.GetComponent<Tailed.Vehicles.VehicleView>();
            float half = Tailed.Core.Identity.VehicleCatalog.Models[v.Identity.ModelId].Length * 0.5f;
            var end = car.position + car.forward * (front ? half : -half) + Vector3.up * 0.4f;
            var p = end + car.forward * (front ? 3.2f : -3.2f) + car.right * 2.4f + Vector3.up * 0.5f;
            return Place(p, end, 45f) + $" {Tailed.Core.Identity.VehicleCatalog.Models[v.Identity.ModelId].FullName} {(front ? "front" : "rear")}";
        }

        /// <summary>Zoomed (8° FOV) look at the first lineup car's rear plate from 12 m / 28 m.</summary>
        public static string PlateAt12() => PlateAt(12f);
        public static string PlateAt28() => PlateAt(28f);

        static string PlateAt(float d)
        {
            LineupAt(false);
            var car = _lineup.transform.GetChild(0);
            var plate = car.position + Vector3.up * 0.45f - car.forward * 2.3f;
            return Place(plate - car.forward * d + Vector3.up * 0.3f, plate, 8f);
        }

        public static string ClockTower() => Landmark(true);
        public static string WaterTower() => Landmark(false);

        static string Landmark(bool clock)
        {
            var p = TownBuilder.Instance.LandmarkPosition(clock);
            return Place(p + new Vector3(-55f, 18f, -55f), p + Vector3.up * 16f, 55f);
        }

        /// <summary>Show the bay beacon on the forecourt nearest the local car, then look at it.</summary>
        public static string BeaconTest()
        {
            var car = Tailed.Vehicles.PlayerCar.Local;
            if (car == null) return "no local car";
            var g = TownBuilder.Instance.Lanes;
            var p = new Core.Util.Vec2(car.transform.position.x, car.transform.position.z);
            var bay = g.Bays.OrderBy(b => Core.Util.Vec2.Distance(b.Position, p)).First();
            var c = Tailed.Core.Rules.Match.BayCentre(bay);
            Tailed.Game.BayBeacon.Show(new Vector3(c.X, 0.02f, c.Y), new Color(0.95f, 0.3f, 0.3f));
            var at = new Vector3(c.X, 0f, c.Y);
            return Place(at + new Vector3(-18f, 9f, -18f), at + Vector3.up * 3f, 50f);
        }

        public static string LampStats() => TownBuilder.Instance.Lamps != null ? TownBuilder.Instance.Lamps.Describe() : "no lamps";

        /// <summary>Skip the menu into offline driving (dev screenshots).</summary>
        public static string GoOffline()
        {
            var b = Tailed.Game.GameBootstrap.Instance;
            if (b == null) return "no bootstrap";
            if (b.Current == Tailed.Game.GameBootstrap.Mode.Menu) b.StartOffline();
            return b.Current.ToString();
        }

        /// <summary>Close-up of a lineup car's driver through the windscreen (character detail check).</summary>
        public static string DriverCloseup()
        {
            LineupAt(true);
            var car = _lineup.transform.GetChild(1);
            var shape = Tailed.Vehicles.VehicleMeshFactory.Shape(1);
            var head = car.TransformPoint(new Vector3(shape.Eye.x, shape.Eye.y - 0.1f, shape.Eye.z));
            return Place(head + car.forward * 3.2f + car.right * 0.6f + Vector3.up * 0.15f, head, 26f);
        }

        /// <summary>Hand the local car to the autopilot (or back to the player).</summary>
        public static string ToggleAutopilot()
        {
            var car = Tailed.Vehicles.PlayerCar.Local;
            if (car == null) return "no local car";
            var ap = car.GetComponent<Tailed.Vehicles.Autopilot>() ?? car.gameObject.AddComponent<Tailed.Vehicles.Autopilot>();
            var input = car.GetComponent<Tailed.Vehicles.CarInput>();
            bool on = !(ap.enabled && (input == null || !input.enabled)) || !ap.enabled;
            ap.enabled = on;
            if (input != null) input.enabled = !on;
            return $"autopilot {(on ? "on" : "off")}";
        }

        /// <summary>Hand the car to the driver aids: autopilot off, lane-keep + cruise on at ~40 km/h.</summary>
        public static string AssistTest()
        {
            var car = Tailed.Vehicles.PlayerCar.Local;
            if (car == null) return "no local car";
            var ap = car.GetComponent<Tailed.Vehicles.Autopilot>();
            if (ap != null) ap.enabled = false;
            var input = car.GetComponent<Tailed.Vehicles.CarInput>();
            if (input != null) input.enabled = true;
            Tailed.Vehicles.DriveAssist.LaneKeep = true;
            Tailed.Vehicles.DriveAssist.Cruise = true;
            Tailed.Vehicles.DriveAssist.CruiseSpeed = 11f;
            return "assists on";
        }

        /// <summary>Assist state plus how far the car is from its lane centre.</summary>
        public static string AssistStatus()
        {
            var car = Tailed.Vehicles.PlayerCar.Local;
            if (car == null) return "no local car";
            var p = car.transform.position; var f = car.transform.forward;
            string lat = "off-lane";
            if (TrafficRunner.Instance.Sim.Index.Project(new Tailed.Core.Util.Vec2(p.x, p.z), new Tailed.Core.Util.Vec2(f.x, f.z), out var proj))
                lat = $"{(proj.OnConnector ? "connector" : "lane")} {proj.Id} lateral={proj.Lateral:0.00}";
            return $"{Tailed.Vehicles.DriveAssist.Status} speed={car.ForwardSpeed:0.0} {lat} steer={car.Steer:0.00} thr={car.Throttle:0.00} brk={car.Brake:0.00}";
        }

        public static string CarStatus()
        {
            var car = Tailed.Vehicles.PlayerCar.Local;
            if (car == null) return "no local car";
            var p = car.transform.position;
            var ap = car.GetComponent<Tailed.Vehicles.Autopilot>();
            var rb = car.GetComponent<Rigidbody>();
            return $"pos=({p.x:0.0},{p.y:0.00},{p.z:0.0}) yaw={car.transform.eulerAngles.y:0} yawRate={rb.angularVelocity.y * Mathf.Rad2Deg:0} aim={(ap ? ap.LastAimAngle : 0):0} " +
                   $"vel={rb.linearVelocity.magnitude:0.0} speed={car.ForwardSpeed:0.0} grounded={car.Grounded} up={car.transform.up.y:0.00} " +
                   $"throttle={car.Throttle:0.0} brake={car.Brake:0.0} steer={car.Steer:0.00}";
        }

        /// <summary>Top-down over the local car with the autopilot path drawn.</summary>
        public static string AboveCar()
        {
            var car = Tailed.Vehicles.PlayerCar.Local;
            if (car == null) return "no local car";
            Tailed.Vehicles.Autopilot.DebugDraw = true;
            var p = car.transform.position;
            return Place(p + Vector3.up * 45f + Vector3.back * 0.01f, p, 55f);
        }

        static GameObject _range;
        static readonly (int lane, float gap)[] RangeSlots =
        {
            (0, 5f), (0, 10f), (0, 16f), (0, 23f), (0, 32f), (1, 8f), (1, 13f), (1, 19f), (1, 27f),
            (2, 8f), (2, 14f), (2, 20f), (2, 26f), // lane 2 = ahead of us, same lane
        };

        /// <summary>
        /// Mirror legibility test rig: clears traffic, parks the local car on a long straight arterial
        /// kerb lane and lines up known plates behind it (same lane and the lane to the left).
        /// </summary>
        public static string MirrorRange()
        {
            var car = Tailed.Vehicles.PlayerCar.Local;
            var runner = Tailed.Traffic.TrafficRunner.Instance;
            if (car == null || runner?.Sim == null) return "no local car / sim";
            runner.Sim.TargetPopulation = 0;
            foreach (var v in runner.Sim.Vehicles.ToList()) if (v.Alive && !v.IsExternal) runner.Sim.Remove(v.Id);
            var g = runner.Town.Lanes;
            var size = g.Network.Size;
            bool Boundary(Core.Util.Vec2 q) => q.X < 1f || q.Y < 1f || q.X > size.X - 1f || q.Y > size.Y - 1f;
            var lane = g.Lanes.Where(l => l.Class == RoadClass.Arterial && l.Index == 0 && l.Length > 80f && l.CentreNeighbor >= 0 &&
                                          !Boundary(g.Network.Nodes[l.FromNode].Position) && !Boundary(g.Network.Nodes[l.ToNode].Position))
                              .OrderByDescending(l => l.Length).First();
            var inner = g.Lanes[lane.CentreNeighbor];
            float s = lane.Length - 20f;
            var dir = new Vector3(lane.Direction.X, 0, lane.Direction.Y);
            var p = lane.PointAt(s);
            car.Place(new Vector3(p.X, 0.3f, p.Y), Quaternion.LookRotation(dir));
            var ap = car.GetComponent<Tailed.Vehicles.Autopilot>();
            if (ap != null) ap.enabled = false;

            if (_range != null) Object.Destroy(_range);
            _range = new GameObject("MirrorRange");
            var ids = new Tailed.Core.Identity.IdentityService(1234);
            float rear = s - car.Shape.Length * 0.5f;
            var lines = new List<string>();
            for (int i = 0; i < RangeSlots.Length; i++)
            {
                var (li, gap) = RangeSlots[i];
                var l = li == 1 ? inner : lane;
                var id = ids.Create(i % 3 == 0 ? 0 : i % 3 == 1 ? 1 : 2, 1 + i % 3);
                float len = Tailed.Core.Identity.VehicleCatalog.Models[id.ModelId].Length;
                float front = s + car.Shape.Length * 0.5f;
                var c = li == 2 ? l.PointAt(front + gap + len * 0.5f) : l.PointAt(rear - gap - len * 0.5f);
                var view = Tailed.Vehicles.VehicleView.Create(_range.transform);
                view.Bind(2000 + i, id);
                view.SetPose(new Vector3(c.X, 0f, c.Y), dir, 0f, Tailed.Core.Traffic.VehicleFlags.None, 0f);
                _rangePlates[i] = (view.transform, id.Plate);
                lines.Add($"{(li == 0 ? "behind" : li == 1 ? "left" : "ahead")} {gap:0}m: {Tailed.Core.Identity.PlateFormat.Display(id.Plate)}");
            }
            return string.Join("; ", lines);
        }

        /// <summary>Hide one lane of the range (0 = same lane, 1 = left lane) to isolate a mirror.</summary>
        public static string RangeHideSameLane() => HideLane(0);
        public static string RangeHideLeftLane() => HideLane(1);

        static int _soloIndex = -1;

        /// <summary>Show only the next same-lane range car (cycles 5 → 32 m); returns its gap and plate.</summary>
        public static string RangeNextSolo()
        {
            var same = Enumerable.Range(0, RangeSlots.Length).Where(i => RangeSlots[i].lane == 0).ToList();
            _soloIndex = (_soloIndex + 1) % same.Count;
            for (int i = 0; i < RangeSlots.Length; i++)
                if (_rangePlates[i].car != null) _rangePlates[i].car.gameObject.SetActive(i == same[_soloIndex]);
            var k = same[_soloIndex];
            return $"{RangeSlots[k].gap:0}m {Tailed.Core.Identity.PlateFormat.Display(_rangePlates[k].plate)}";
        }

        /// <summary>Write the glyph atlas (mip 0) to a PNG next to the player log / project out dir.</summary>
        public static string DumpAtlas()
        {
            var a = Tailed.Vehicles.Glyphs.Atlas;
            var px = a.GetPixels(0);
            var t = new Texture2D(a.width, a.height, TextureFormat.RGBA32, false);
            t.SetPixels(px);
            t.Apply();
            var path = System.IO.Path.Combine(Application.persistentDataPath, "atlas.png");
            System.IO.File.WriteAllBytes(path, t.EncodeToPNG());
            return path;
        }

        /// <summary>Host only: skip to the next match phase (e.g. end the round → debrief).</summary>
        public static string HostAdvance()
        {
            var c = Tailed.Net.ClientGame.Instance;
            if (c == null || Tailed.Net.NetSession.Instance == null || !Tailed.Net.NetSession.Instance.IsHost) return "not host";
            c.Advance();
            return "advanced";
        }

        /// <summary>Show only the next car ahead (cycles 8 → 26 m) and look straight at it.</summary>
        public static string AheadNextSolo()
        {
            var ahead = Enumerable.Range(0, RangeSlots.Length).Where(i => RangeSlots[i].lane == 2).ToList();
            _aheadIndex = (_aheadIndex + 1) % ahead.Count;
            for (int i = 0; i < RangeSlots.Length; i++)
                if (_rangePlates[i].car != null) _rangePlates[i].car.gameObject.SetActive(i == ahead[_aheadIndex]);
            Tailed.Cockpit.CameraDirector.DebugGlance = null;
            var k = ahead[_aheadIndex];
            var car = Tailed.Vehicles.PlayerCar.Local;
            var target = _rangePlates[k].car.TransformPoint(Tailed.Vehicles.VehicleMeshFactory.Shape(0).RearPlate);
            var d = car.transform.InverseTransformPoint(target) - car.Shape.Eye;
            Tailed.Cockpit.CameraDirector.DebugLook = new Vector2(Mathf.Atan2(d.x, d.z) * Mathf.Rad2Deg, -Mathf.Atan2(d.y, new Vector2(d.x, d.z).magnitude) * Mathf.Rad2Deg);
            return $"{RangeSlots[k].gap:0}m {Tailed.Core.Identity.PlateFormat.Display(_rangePlates[k].plate)}";
        }

        static int _aheadIndex = -1;

        public static string FocusOn() { Tailed.Cockpit.CameraDirector.DebugFocus = true; return "focus"; }
        public static string FocusOff() { Tailed.Cockpit.CameraDirector.DebugFocus = false; return "no focus"; }

        static string HideLane(int lane)
        {
            for (int i = 0; i < RangeSlots.Length; i++)
                if (_rangePlates[i].car != null) _rangePlates[i].car.gameObject.SetActive(RangeSlots[i].lane != lane);
            return "ok";
        }

        static readonly (Transform car, string plate)[] _rangePlates = new (Transform, string)[RangeSlots.Length];

        public static string GlanceRear() => ForceGlance(Tailed.Cockpit.Glance.Rear);
        public static string GlanceLeft() => ForceGlance(Tailed.Cockpit.Glance.Left);
        public static string GlanceRight() => ForceGlance(Tailed.Cockpit.Glance.Right);
        public static string GlanceNone() { Tailed.Cockpit.CameraDirector.DebugGlance = null; return Cockpit(); }

        static string ForceGlance(Tailed.Cockpit.Glance g)
        {
            Tailed.Cockpit.CameraDirector.DebugLook = null;
            Tailed.Cockpit.CameraDirector.DebugZoom = 1f;
            Tailed.Cockpit.CameraDirector.DebugGlance = g;
            return Cockpit();
        }

        /// <summary>
        /// For each range car: optical distance via the glanced mirror, the plate shader's legibility, and
        /// how many pixels tall a plate character is on screen and in the mirror texture.
        /// </summary>
        public static string MirrorReport()
        {
            var ms = Tailed.Cockpit.MirrorSystem.Local;
            var dirc = Tailed.Cockpit.CameraDirector.Instance;
            if (ms == null || dirc == null) return "no mirrors";
            var g = dirc.Glance == Tailed.Cockpit.Glance.None ? Tailed.Cockpit.Glance.Rear : dirc.Glance;
            var cam = Camera.main;
            var eye = ms.VirtualEye(g);
            float mag = 1f / ms.ConvexOf(g);
            var rt = ms.RtOf(g);
            float rtPxPerRad = rt.height / ms.VerticalAngleOf(g);
            float screenPxPerRad = cam.pixelHeight / (cam.fieldOfView * Mathf.Deg2Rad) * mag;
            float charHeight = Tailed.Vehicles.Glyphs.PlateHeight * 0.72f * 28f / 32f;
            const float fullAt = 9f, goneAt = 34f;
            var lines = new List<string> { $"{g} mirror, screen {cam.pixelWidth}x{cam.pixelHeight}, fov {cam.fieldOfView:0.0}, {ms.Describe()}" };
            for (int i = 0; i < _rangePlates.Length; i++)
            {
                var (t, plate) = _rangePlates[i];
                if (t == null) continue;
                var shape = Tailed.Vehicles.VehicleMeshFactory.Shape(0);
                var platePos = t.TransformPoint(new Vector3(0f, 0.55f, 2.4f)); // front plate of the car behind
                float d = Vector3.Distance(eye, platePos);
                float eff = d / mag;
                float legibility = 1f - Mathf.Clamp01((eff - fullAt) / (goneAt - fullAt));
                float angle = charHeight / d;
                lines.Add($"{RangeSlots[i].gap:0}m {(RangeSlots[i].lane == 0 ? "same" : "left")} {plate}: optical {d:0.0}m eff {eff:0.0}m legib {legibility:0.00} " +
                          $"char {angle * screenPxPerRad:0.0}px screen / {angle * rtPxPerRad * mag:0.0}px mirror-tex");
            }
            return string.Join("\n", lines);
        }

        public static string Overview()
        {
            var size = TownBuilder.Instance.Network.Size;
            return Place(new Vector3(-0.15f * size.X, 0.55f * size.X, -0.15f * size.Y),
                         new Vector3(size.X * 0.5f, 0f, size.Y * 0.45f));
        }

        public static string Downtown() => Aerial(District.Downtown, 160f);
        public static string Suburb() => Aerial(District.Suburb, 110f);
        public static string Industrial() => Aerial(District.Industrial, 140f);

        /// <summary>Signal poles standing in a lane or junction (should be 0).</summary>
        public static string PoleAudit()
        {
            var t = TownBuilder.Instance;
            var bad = t.PolesOnRoad();
            var old = t.PolesOnRoad(t.OldFarPoles);
            return $"{t.PolePositions.Count} poles, {bad.Count} on the road (old far-pole placement: {old.Count} on the road" +
                   string.Concat(old.Take(3).Select(b => $" ({b.X:0},{b.Y:0})")) + ")" + string.Concat(bad.Take(5).Select(b => $" ({b.X:0},{b.Y:0})"));
        }

        /// <summary>Street-level look at the first junction where the old far-pole placement was in the road.</summary>
        public static string OldPoleSpot()
        {
            var t = TownBuilder.Instance;
            var old = t.PolesOnRoad(t.OldFarPoles);
            if (old.Count == 0) return "none";
            var p = old[0];
            var at = new Vector3(p.X, 0f, p.Y);
            return Place(at + new Vector3(-18f, 5f, -14f), at + Vector3.up * 2f, 55f) + $" old pole spot ({p.X:0},{p.Y:0})";
        }

        /// <summary>Straight down over the same junction.</summary>
        public static string OldPoleSpotTop()
        {
            var t = TownBuilder.Instance;
            var old = t.PolesOnRoad(t.OldFarPoles);
            if (old.Count == 0) return "none";
            var at = new Vector3(old[0].X, 0f, old[0].Y);
            return Place(at + new Vector3(0f, 45f, -0.01f), at, 50f);
        }

        /// <summary>Frame any car currently showing the gold errand marker.</summary>
        public static string LookAtErrand()
        {
            foreach (var v in Tailed.Vehicles.VehicleView.Active)
            {
                var marker = v.GetComponentInChildren<Tailed.Vehicles.ErrandMarker>();
                if (marker == null) continue;
                var ground = v.transform.position;
                return Place(ground + new Vector3(10f, 3.5f, -10f), ground + Vector3.up * 2f, 50f);
            }
            return "no errand marker showing";
        }

        // ---- autopilot trip harness (bot AI checks) ----
        static int _apBay = -1;
        static float _apStart, _apDist;
        static readonly System.Random _apRng = new System.Random(5);

        /// <summary>Send the local car's autopilot to a random business bay 400–1200 m away.</summary>
        public static string ApGoToRandomBay()
        {
            var car = Tailed.Vehicles.PlayerCar.Local;
            if (car == null) return "no car";
            var ap = car.GetComponent<Tailed.Vehicles.Autopilot>() ?? car.gameObject.AddComponent<Tailed.Vehicles.Autopilot>();
            ap.enabled = true;
            ap.FollowTarget = null;
            var input = car.GetComponent<Tailed.Vehicles.CarInput>();
            if (input != null) input.enabled = false;
            var g = TownBuilder.Instance.Lanes;
            var p = car.transform.position;
            for (int tries = 0; tries < 200; tries++)
            {
                int b = _apRng.Next(g.Bays.Count);
                var c = Tailed.Core.Rules.Match.BayCentre(g.Bays[b]);
                float d = Vector2.Distance(new Vector2(c.X, c.Y), new Vector2(p.x, p.z));
                if (d < 400f || d > 1200f) continue;
                _apStart = Time.time; _apDist = d;
                ap.ParkAtSite(g.Bays[b].PoiId);
                _apBay = ap.TargetBay;
                return $"bay {b} at {d:0} m ({TownBuilder.Instance.Network.Pois[g.Bays[b].PoiId].Name})";
            }
            return "no bay found";
        }

        /// <summary>Progress of the current autopilot trip.</summary>
        public static string ApStatus()
        {
            var car = Tailed.Vehicles.PlayerCar.Local;
            var ap = car != null ? car.GetComponent<Tailed.Vehicles.Autopilot>() : null;
            if (ap == null || _apBay < 0) return "no trip";
            var c = Tailed.Core.Rules.Match.BayCentre(TownBuilder.Instance.Lanes.Bays[ap.TargetBay]);
            float d = Vector2.Distance(new Vector2(c.X, c.Y), new Vector2(car.transform.position.x, car.transform.position.z));
            return $"t={Time.time - _apStart:0} dist={d:0} arrived={ap.Arrived} speed={car.ForwardSpeed:0.0} {ap.DebugState}";
        }

        static int _apFixed;
        /// <summary>Trip to the bays that kept failing (Jolly Motel, Corner Diner), in turn.</summary>
        public static string ApGoToHardBay()
        {
            var car = Tailed.Vehicles.PlayerCar.Local;
            if (car == null) return "no car";
            var ap = car.GetComponent<Tailed.Vehicles.Autopilot>() ?? car.gameObject.AddComponent<Tailed.Vehicles.Autopilot>();
            ap.enabled = true; ap.FollowTarget = null;
            var g = TownBuilder.Instance.Lanes;
            int[] bays = { 40, 138 };
            int b = bays[_apFixed++ % bays.Length];
            _apStart = Time.time;
            ap.ParkAtSite(g.Bays[b].PoiId);
            _apBay = ap.TargetBay;
            return $"site {TownBuilder.Instance.Network.Pois[g.Bays[b].PoiId].Name} bay {ap.TargetBay}";
        }

        /// <summary>Top-down over the target bay with the autopilot path drawn.</summary>
        public static string AboveTargetBay()
        {
            var car = Tailed.Vehicles.PlayerCar.Local;
            var ap = car != null ? car.GetComponent<Tailed.Vehicles.Autopilot>() : null;
            if (ap == null || ap.TargetBay < 0) return "no target";
            Tailed.Vehicles.Autopilot.DebugDraw = true;
            var c = Tailed.Core.Rules.Match.BayCentre(TownBuilder.Instance.Lanes.Bays[ap.TargetBay]);
            var at = new Vector3(c.X, 0f, c.Y);
            return Place(at + Vector3.up * 38f + Vector3.back * 0.01f, at, 55f);
        }

        /// <summary>From behind the Mark's car, 25 m up, looking towards its next stop (route beacons check).</summary>
        public static string LookAtNextStop()
        {
            var c = Tailed.Net.ClientGame.Instance;
            var car = Tailed.Vehicles.PlayerCar.Local;
            if (c == null || car == null || c.Route.Count == 0) return "no route";
            int i = Mathf.Min(c.NextCheckpoint, c.Route.Count - 1);
            var site = TownBuilder.Instance.Lanes.Sites[c.Route[i]].Centre;
            var stop = new Vector3(site.X, 20f, site.Y);
            var from = car.transform.position + Vector3.up * 25f - (stop - car.transform.position).normalized * 30f;
            return Place(from, stop, 60f) + $" towards stop {i + 1}";
        }

        static GameObject _parade;
        /// <summary>A row of 12 randomly dressed drivers facing the camera (character kit check).</summary>
        public static string DriverParade()
        {
            if (_parade != null) Object.Destroy(_parade);
            _parade = new GameObject("DriverParade");
            var ids = new Tailed.Core.Identity.IdentityService((ulong)Random.Range(1, 99999));
            var at = new Vector3(-150f, 0.2f, 140f);
            for (int i = 0; i < 12; i++)
            {
                var go = new GameObject("Driver");
                go.transform.SetParent(_parade.transform, false);
                go.transform.position = at + Vector3.right * (i - 5.5f) * 0.62f;
                go.transform.rotation = Quaternion.Euler(0f, 180f, 0f); // face -z, towards the camera
                go.AddComponent<MeshFilter>().sharedMesh = Tailed.Vehicles.VehicleMeshFactory.Driver(ids.RandomCar());
                go.AddComponent<MeshRenderer>().sharedMaterial = Tailed.Vehicles.VehicleMaterials.Driver;
            }
            return Place(at + new Vector3(0f, 0.6f, -5.2f), at + Vector3.up * 0.45f, 42f);
        }

        static int _poiTour;
        /// <summary>Visit one business of each type in turn, viewed from across the street.</summary>
        public static string PoiTour()
        {
            var t = TownBuilder.Instance;
            var types = t.Network.Pois.Select(p => p.Type).Distinct().OrderBy(x => x).ToList();
            var type = types[_poiTour++ % types.Count];
            var poi = t.Network.Pois.First(p => p.Type == type);
            var site = t.Lanes.Sites[poi.Id];
            var front = site.Centre + site.Inward * 9f;
            var eye = site.Centre - site.Inward * 22f + site.Inward.PerpLeft * 12f;
            return Place(new Vector3(eye.X, 7f, eye.Y), new Vector3(front.X, 3.5f, front.Y) + new Vector3(site.Inward.X, 0f, site.Inward.Y) * -2f, 60f) + $" {type} ({poi.Name})";
        }

        static int _poiClose;
        /// <summary>Close three-quarter view of one business of each type (cycles).</summary>
        public static string PoiClose()
        {
            var t = TownBuilder.Instance;
            var types = t.Network.Pois.Select(p => p.Type).Distinct().OrderBy(x => x).ToList();
            var type = types[_poiClose++ % types.Count];
            var poi = t.Network.Pois.First(p => p.Type == type);
            var site = t.Lanes.Sites[poi.Id];
            var front = site.Centre + site.Inward * 9f;
            var eye = front - site.Inward * 16f + site.Inward.PerpLeft * 11f;
            return Place(new Vector3(eye.X, 5f, eye.Y), new Vector3(front.X, 3f, front.Y) + new Vector3(site.Inward.X, 0f, site.Inward.Y) * 3f, 65f) + $" {type}";
        }

        public static string SteerLeft() { Tailed.Vehicles.CockpitRig.DebugSteer = -1f; return Cockpit() + " steer left"; }
        public static string SteerRight() { Tailed.Vehicles.CockpitRig.DebugSteer = 0.5f; return Cockpit() + " steer half right"; }
        public static string SteerStraight() { Tailed.Vehicles.CockpitRig.DebugSteer = 0f; Tailed.Cockpit.CameraDirector.DebugLook = new Vector2(0f, 8f); Tailed.Cockpit.CameraDirector.DebugZoom = 1f; return Cockpit() + " straight"; }
        public static string LookFootwell() { Tailed.Cockpit.CameraDirector.DebugLook = new Vector2(0f, 55f); Tailed.Cockpit.CameraDirector.DebugZoom = 1f; return Cockpit(); }
        public static string LookPassenger() { Tailed.Cockpit.CameraDirector.DebugLook = new Vector2(65f, 35f); Tailed.Cockpit.CameraDirector.DebugZoom = 1f; return Cockpit(); }
        public static string LookRearSeats() { Tailed.Cockpit.CameraDirector.DebugLook = new Vector2(170f, 18f); Tailed.Cockpit.CameraDirector.DebugZoom = 1f; return Cockpit(); }
        public static string SteerNone() { Tailed.Vehicles.CockpitRig.DebugSteer = null; Tailed.Vehicles.CockpitRig.DebugHorn = false; return Cockpit(); }
        public static string HornHold() { Tailed.Vehicles.CockpitRig.DebugHorn = true; return Cockpit() + " horn"; }
        /// <summary>Cockpit, looking down at the wheel and dash.</summary>
        public static string LookDash() { Tailed.Cockpit.CameraDirector.DebugLook = new Vector2(0f, 22f); Tailed.Cockpit.CameraDirector.DebugZoom = 1f; return Cockpit(); }

        static int _street;
        /// <summary>Cycle through suburb blocks: (centre, one street-side corner pair).</summary>
        static (Vector3 centre, Vector3 a, Vector3 b) NextSuburb()
        {
            var net = TownBuilder.Instance.Network;
            var cells = net.Cells.FindAll(c => c.District == Tailed.Core.Roads.District.Suburb);
            var cell = cells[(_street++ * 7) % cells.Count];
            Vector3 P(int k) { var v = net.Nodes[cell.Corners[k % 4]].Position; return new Vector3(v.X, 0f, v.Y); }
            var centre = (P(0) + P(1) + P(2) + P(3)) * 0.25f;
            return (centre, P(_street % 4), P(_street % 4 + 1));
        }

        /// <summary>Eye-level view along a suburban street (cycles blocks).</summary>
        public static string HouseStreet()
        {
            var (centre, a, b) = NextSuburb();
            var mid = Vector3.Lerp(a, b, 0.5f);
            var inward = (centre - mid).normalized;
            var along = (b - a).normalized;
            var eye = Vector3.Lerp(a, b, 0.15f) + inward * 3f + Vector3.up * 2.2f;
            return Place(eye, mid + inward * 16f + Vector3.up * 2f, 60f);
        }

        /// <summary>High view over a suburb block.</summary>
        public static string SuburbHigh()
        {
            var (centre, a, b) = NextSuburb();
            var mid = Vector3.Lerp(a, b, 0.5f);
            var eye = mid + (mid - centre).normalized * 20f + (b - a).normalized * 25f + Vector3.up * 26f;
            return Place(eye, Vector3.Lerp(mid, centre, 0.35f), 60f);
        }

        /// <summary>Straight down onto a diner's bay row (wheel stops, lot dressing).</summary>
        public static string BaysTop()
        {
            var t = TownBuilder.Instance;
            var poi = t.Network.Pois.First(p => p.Type == Tailed.Core.Roads.PoiType.Diner);
            var c = t.Lanes.Sites[poi.Id].Centre;
            return Place(new Vector3(c.X, 45f, c.Y) + Vector3.forward * 0.01f, new Vector3(c.X, 0f, c.Y), 60f);
        }

        static int _carLow;
        /// <summary>Low three-quarter look at a parked car (bodywork below the belt line, underside).</summary>
        public static string CarLow()
        {
            var parked = GameObject.Find("ParkedCars");
            var car = parked.transform.GetChild((_carLow++ * 11) % parked.transform.childCount);
            var mr = car.GetComponentInChildren<MeshRenderer>();
            var c = mr.bounds.center;
            var t = mr.transform;
            var ground = mr.bounds.min.y;
            var p = c + t.right * 4.6f + t.forward * 3.2f;
            p.y = ground + 0.3f;
            return Place(p, new Vector3(c.x, ground + 0.45f, c.z), 55f);
        }

        /// <summary>Above a junction where an NPC is mid-turn (left/right), looking down at it.</summary>
        public static string WatchTurn()
        {
            var sim = TrafficRunner.Instance.Sim;
            foreach (var v in sim.Vehicles)
            {
                if (!v.Alive || v.IsExternal || v.Mode != Tailed.Core.Traffic.VehicleMode.Connector) continue;
                var con = sim.Graph.Connectors[v.Connector];
                if (con.Turn != Tailed.Core.Roads.TurnType.Left && con.Turn != Tailed.Core.Roads.TurnType.Right) continue;
                if (v.S > con.Length * 0.4f) continue;
                var node = sim.Graph.Network.Nodes[con.NodeId].Position;
                var c = new Vector3(node.X, 0f, node.Y);
                return Place(c + new Vector3(10f, 22f, -10f), c, 50f) + $" {con.Turn} v{v.Id} speed={v.Speed:0.0} profile={v.Profile.Type} tsf={v.Profile.TurnSpeedFactor:0.00} line={v.Profile.TurnLine:0.00}";
            }
            return "no turning vehicle";
        }

        public static string TimeScale3() { Time.timeScale = 3f; return "x3"; }

        static int _busStop, _driveway;

        /// <summary>Look back along the road at a bus stop (cycles through them). Arg: stop index.</summary>
        public static string BusStop()
        {
            var g = TownBuilder.Instance.Lanes;
            var stops = g.KerbStops.FindAll(k => k.BusStop);
            if (stops.Count == 0) return "no bus stops";
            var k = stops[_busStop++ % stops.Count];
            var lane = g.Lanes[k.Lane];
            var at = lane.PointAt(k.S + 22f) + lane.Direction.PerpRight * (lane.Width * 0.5f + 2.2f);
            var target = lane.PointAt(k.S - 4f);
            return Place(new Vector3(at.X, 2.2f, at.Y), new Vector3(target.X, 1.2f, target.Y), 55f) + $" stop {k.Id} lane {k.Lane}";
        }

        /// <summary>Low view of a suburban street with parked cars on driveways (cycles).</summary>
        public static string Driveways()
        {
            var parked = GameObject.Find("ParkedCars");
            if (parked == null || parked.transform.childCount == 0) return "no parked cars";
            Transform car;
            do car = parked.transform.GetChild((_driveway++ * 7) % parked.transform.childCount);
            while (Tailed.Core.Identity.VehicleCatalog.Models[car.GetComponent<Tailed.Vehicles.VehicleView>().Identity.ModelId].NpcOnly ||
                   Tailed.Core.Identity.VehicleCatalog.Models[car.GetComponent<Tailed.Vehicles.VehicleView>().Identity.ModelId].Style == Tailed.Core.Identity.BodyStyle.Van);
            var p = car.position - car.forward * 9f + car.right * 9f + Vector3.up * 8f;
            return Place(p, car.position + Vector3.up * 0.8f, 60f) + $" {parked.transform.childCount} parked";
        }

        /// <summary>Stats on the new behaviours: kerb-stopped, U-turning, hesitating vehicles.</summary>
        public static string Behaviour()
        {
            var sim = TrafficRunner.Instance.Sim;
            int kerb = 0, uturn = 0, waiting = 0, buses = 0;
            foreach (var v in sim.Vehicles)
            {
                if (!v.Alive || v.IsExternal) continue;
                if (v.KerbTimer > 0f) kerb++;
                if (v.Mode == Tailed.Core.Traffic.VehicleMode.Connector && sim.Graph.Connectors[v.Connector].Turn == Tailed.Core.Roads.TurnType.UTurn) uturn++;
                if (v.ReactionTimer > 0f) waiting++;
                if (v.Model.Style == Tailed.Core.Identity.BodyStyle.Bus) buses++;
            }
            return $"kerb={kerb} uturn={uturn} hesitating={waiting} buses={buses}";
        }

        /// <summary>Driver's-eye view from a kerbside lane, cycling through lanes on each call.</summary>
        public static string Street()
        {
            var g = TownBuilder.Instance.Lanes;
            var net = TownBuilder.Instance.Network;
            var size = net.Size;
            bool OnBoundary(Core.Util.Vec2 p) => p.X < 1f || p.Y < 1f || p.X > size.X - 1f || p.Y > size.Y - 1f;
            var lanes = g.Lanes.Where(l => l.Index == 0 && l.Length > 60f &&
                                           !(OnBoundary(net.Nodes[l.FromNode].Position) && OnBoundary(net.Nodes[l.ToNode].Position))).ToList();
            var lane = lanes[(_streetIndex++ * 37) % lanes.Count];
            var p = lane.PointAt(8f);
            var ahead = lane.PointAt(lane.Length);
            return Place(new Vector3(p.X, 1.25f, p.Y), new Vector3(ahead.X, 1.0f, ahead.Y), fov: 70f);
        }

        /// <summary>Driver's eye, first in the queue at a signalised stop line (cycles approaches).</summary>
        public static string SignalApproach()
        {
            var g = TownBuilder.Instance.Lanes;
            var lanes = g.Lanes.Where(l => l.Control == ApproachControl.Signal && l.Index == 0 && l.Length > 50f).ToList();
            var lane = lanes[(_streetIndex++ * 7) % lanes.Count];
            var p = lane.PointAt(lane.Length - 3f);
            var d = new Vector3(lane.Direction.X, 0, lane.Direction.Y);
            var eye = new Vector3(p.X, 1.2f, p.Y) - Vector3.Cross(Vector3.up, d) * 0.4f;
            return Place(eye, eye + d * 20f + Vector3.up * 2.2f, 72f);
        }

        /// <summary>Top-down over a signalised 4-way junction (use with LaneDebugView.Toggle).</summary>
        public static string Junction()
        {
            var net = TownBuilder.Instance.Network;
            var j = TownBuilder.Instance.Lanes.Junctions.Values
                .Where(x => x.Control == JunctionControl.Signal && net.Nodes[x.NodeId].Degree == 4)
                .Skip(_streetIndex++ % 5).First();
            var p = net.Nodes[j.NodeId].Position;
            return Place(new Vector3(p.X, 55f, p.Y - 0.01f), new Vector3(p.X, 0f, p.Y));
        }

        static string Aerial(District d, float distance)
        {
            var net = TownBuilder.Instance.Network;
            var cells = net.Cells.Where(c => c.District == d).ToList();
            var cell = cells[cells.Count / 2];
            var target = new Vector3((cell.X + 0.5f) * net.CellSize, 0f, (cell.Y + 0.5f) * net.CellSize);
            return Place(target + new Vector3(-0.7f, 0.75f, -0.7f) * distance, target);
        }

        /// <summary>Back to the local car's cockpit / chase view.</summary>
        public static string Cockpit() => Drive(Tailed.Cockpit.CameraMode.Cockpit);
        public static string Chase() => Drive(Tailed.Cockpit.CameraMode.Chase);

        static string Drive(Tailed.Cockpit.CameraMode mode)
        {
            var d = Tailed.Cockpit.CameraDirector.Instance;
            if (d == null || d.Target == null) return "no local car";
            d.SetFree(false);
            d.Mode = mode;
            return mode.ToString();
        }

        /// <summary>Cockpit view with the head turned to a given yaw (for mirror checks in screenshots).</summary>
        public static string LookAtRearMirror() => LookAt(s => s.RearViewMirror, 1f);
        public static string LookAtLeftMirror() => LookAt(s => s.LeftMirror, 1f);
        public static string LookAtRightMirror() => LookAt(s => s.RightMirror, 1f);
        /// <summary>Zoomed (narrow FOV, as if leaning in) views to inspect mirror content.</summary>
        public static string ZoomRearMirror() => LookAt(s => s.RearViewMirror, 4.5f);
        public static string ZoomLeftMirror() => LookAt(s => s.LeftMirror, 4.5f);
        public static string ZoomRightMirror() => LookAt(s => s.RightMirror, 4.5f);
        public static string LookBack() { Tailed.Cockpit.CameraDirector.DebugLook = new Vector2(150f, 5f); Tailed.Cockpit.CameraDirector.DebugZoom = 1f; return Cockpit(); }
        public static string LookAhead() { Tailed.Cockpit.CameraDirector.DebugLook = null; Tailed.Cockpit.CameraDirector.DebugZoom = 1f; return Cockpit(); }

        static string LookAt(System.Func<Tailed.Vehicles.CarShape, Vector3> point, float zoom)
        {
            var car = Tailed.Vehicles.PlayerCar.Local;
            if (car == null) return "no local car";
            var d = point(car.Shape) - car.Shape.Eye;
            float yaw = Mathf.Atan2(d.x, d.z) * Mathf.Rad2Deg;
            float pitch = -Mathf.Atan2(d.y, new Vector2(d.x, d.z).magnitude) * Mathf.Rad2Deg;
            Tailed.Cockpit.CameraDirector.DebugLook = new Vector2(yaw, pitch);
            Tailed.Cockpit.CameraDirector.DebugZoom = zoom;
            return Cockpit() + $" yaw={yaw:0} pitch={pitch:0} zoom={zoom}";
        }

        /// <summary>Give the fly camera back to the keyboard/mouse after scripted shots.</summary>
        public static string FreeFly()
        {
            var fly = Camera.main != null ? Camera.main.GetComponent<FlyCamera>() : null;
            if (fly != null) fly.enabled = true;
            return "fly camera on";
        }

        static string Place(Vector3 position, Vector3 lookAt, float fov = 50f)
        {
            Tailed.Cockpit.CameraDirector.Instance?.SetFree(true);
            var cam = Camera.main;
            cam.transform.SetPositionAndRotation(position, Quaternion.LookRotation(lookAt - position));
            cam.fieldOfView = fov;
            var fly = cam.GetComponent<FlyCamera>();
            if (fly != null)
            {
                fly.SyncAngles();
                // Scripted shots (automation bridge): stray keyboard/mouse input on the focused test
                // window must not move the camera. Tailed.Cameras.DevViews.FreeFly re-enables it.
                if (Object.FindAnyObjectByType<Tailed.Game.PlayerBridge>() != null) fly.enabled = false;
            }
            return $"camera at {position} looking at {lookAt}";
        }
    }
}
