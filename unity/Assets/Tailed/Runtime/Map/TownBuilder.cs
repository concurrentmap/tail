using System;
using System.Collections.Generic;
using System.Linq;
using Tailed.Core.Identity;
using Tailed.Core.Roads;
using Tailed.Core.Traffic;
using Tailed.Core.Util;
using Tailed.Vehicles;
using UnityEngine;

namespace Tailed.Map
{
    /// <summary>
    /// Generates the fictional town from a seed and builds all static geometry.
    /// Roads are negative space: an asphalt slab with raised kerbed blocks on top.
    /// </summary>
    public sealed class TownBuilder : MonoBehaviour
    {
        public int Seed = 42;
        public Material ToonMaterial;
        public Material DebugMaterial;

        public static TownBuilder Instance { get; private set; }
        public RoadNetwork Network { get; private set; }
        public LaneGraph Lanes { get; private set; }
        public SignalLamps Lamps { get; private set; }
        /// <summary>Raised after every (re)build — traffic and game systems re-attach to the new town.</summary>
        public static event Action<TownBuilder> Built;

        Dictionary<int, Poi> _poiByCell;
        /// <summary>Decor cars (driveways, loading yards): model, colour, centre, facing.</summary>
        readonly List<(int model, int color, Vec2 at, Vec2 facing)> _parked = new List<(int, int, Vec2, Vec2)>();

        const float KerbHeight = 0.15f, LotHeight = 0.17f, MarkingY = 0.02f;

        void Awake()
        {
            Instance = this;
            Build();
        }

        /// <summary>Regenerate with the next seed (dev key / bridge: Tailed.Map.TownBuilder.NextSeed).</summary>
        public static string NextSeed()
        {
            Instance.Seed++;
            Instance.Build();
            return $"seed={Instance.Seed}";
        }

        public void Build()
        {
            for (int i = transform.childCount - 1; i >= 0; i--) Destroy(transform.GetChild(i).gameObject);

            Network = TownGenerator.Generate(new TownConfig { Seed = (ulong)Seed });
            Lanes = LaneGraph.Build(Network);
            _poiByCell = new Dictionary<int, Poi>();
            _parked.Clear();
            PolePositions.Clear();
            OldFarPoles.Clear();
            foreach (var poi in Network.Pois) _poiByCell[poi.CellIndex] = poi;

            var ground = new MeshBuilder();
            var blocks = new MeshBuilder();
            var markings = new MeshBuilder();
            var buildings = new MeshBuilder();
            var nature = new MeshBuilder();
            var props = new MeshBuilder();
            var lamps = new MeshBuilder();
            var lampRecords = new List<SignalLamps.Lamp>();
            var signs = new SignBuilder();

            BuildGround(ground);
            BuildOutskirts(blocks, nature);
            PickLandmarks();
            for (int ci = 0; ci < Network.Cells.Count; ci++)
            {
                if (_poiByCell.TryGetValue(ci, out var poi)) BuildPoi(poi, blocks, buildings, markings, signs);
                else if (ci == _clockTowerCell) BuildClockTower(Network.Cells[ci], blocks, buildings, nature);
                else if (ci == _waterTowerCell) BuildWaterTower(Network.Cells[ci], blocks, buildings, signs);
                else BuildCell(Network.Cells[ci], blocks, buildings, nature);
            }
            var glow = new GlowBuilder();
            BuildStreetFurniture(props, glow, signs, nature);
            BuildMarkings(markings);
            BuildProps(props, lamps, lampRecords, signs);
            BuildKerbStops(buildings, props, markings, signs);

            Emit("Ground", ground, castShadows: false);
            Emit("Blocks", blocks, castShadows: false);
            Emit("Markings", markings, castShadows: false);
            Emit("Buildings", buildings, castShadows: true);
            Emit("Nature", nature, castShadows: true);
            Emit("Props", props, castShadows: true);
            var lampGo = Emit("SignalLamps", lamps, castShadows: false);
            lampGo.GetComponent<MeshRenderer>().sharedMaterial = DebugMaterial; // unlit vertex colour = glowing lamps
            // Lamp colours change at runtime: static batching would bake a copy and freeze them.
            lampGo.isStatic = false;
            Lamps = new SignalLamps(lampGo.GetComponent<MeshFilter>().sharedMesh, lampRecords);
            var glowGo = new GameObject("LampGlow");
            glowGo.transform.SetParent(transform, false);
            glowGo.AddComponent<MeshFilter>().sharedMesh = glow.Build();
            var glowMr = glowGo.AddComponent<MeshRenderer>();
            glowMr.sharedMaterial = GlowBuilder.Material;
            glowMr.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            var signGo = new GameObject("Signs");
            signGo.transform.SetParent(transform, false);
            signGo.AddComponent<MeshFilter>().sharedMesh = signs.Build();
            signGo.AddComponent<MeshRenderer>().sharedMaterial = SignBuilder.Material;

            var debug = new GameObject("LaneDebug").AddComponent<LaneDebugView>();
            debug.transform.SetParent(transform, false);
            debug.Build(Lanes, DebugMaterial);
            SpawnParkedCars();
            Clouds.Build(transform, Network, Seed);
            Built?.Invoke(this);
        }

        Material _foliage;
        /// <summary>Toon with wind sway, for trees.</summary>
        Material FoliageMaterial
        {
            get
            {
                if (_foliage != null) return _foliage;
                _foliage = new Material(ToonMaterial) { name = "Foliage" };
                _foliage.SetFloat("_Wind", 1f);
                return _foliage;
            }
        }

        GameObject Emit(string name, MeshBuilder mb, bool castShadows)
        {
            var go = new GameObject(name);
            go.transform.SetParent(transform, false);
            go.isStatic = true;
            go.AddComponent<MeshFilter>().sharedMesh = mb.Build(name);
            var mr = go.AddComponent<MeshRenderer>();
            mr.sharedMaterial = name == "Nature" ? FoliageMaterial : ToonMaterial;
            mr.shadowCastingMode = castShadows ? UnityEngine.Rendering.ShadowCastingMode.On : UnityEngine.Rendering.ShadowCastingMode.Off;
            if (name == "Ground" || name == "Blocks" || name == "Buildings")
            {
                go.AddComponent<MeshCollider>().sharedMesh = go.GetComponent<MeshFilter>().sharedMesh;
                go.layer = name == "Buildings" ? Layers.Buildings : Layers.Ground;
            }
            return go;
        }

        // ---- POIs --------------------------------------------------------------------

        void BuildPoi(Poi poi, MeshBuilder blocks, MeshBuilder buildings, MeshBuilder markings, SignBuilder signs)
        {
            var cell = Network.Cells[poi.CellIndex];
            var site = Lanes.Sites[poi.Id];
            var rng = new Rng((ulong)Seed * 104729UL + (ulong)poi.Id);
            // The whole block is a paved lot at road level: cars drive straight on.
            blocks.Flat(RoundedCellPolygon(cell, 0f, 6f), 0.012f, Palette.Forecourt);

            Vec2 inward = site.Inward, along = inward.PerpLeft;
            // Bays: painted outlines where a parked car's centre sits.
            foreach (int b in site.Bays)
            {
                var bay = Lanes.Bays[b];
                Vec2 c = BayCentre(bay);
                Vec2 f = bay.Heading * 3.2f, r = bay.Heading.PerpRight * 1.5f;
                Vec2 a0 = c - f - r, a1 = c + f - r, a2 = c + f + r, a3 = c - f + r;
                Strip(markings, a0, a1, 0.15f, Palette.LineWhite, 0.03f);
                Strip(markings, a3, a2, 0.15f, Palette.LineWhite, 0.03f);
                Strip(markings, a0, a3, 0.15f, Palette.LineWhite, 0.03f);
            }

            var style = PoiStyle(poi.Type);
            float depth = poi.Type == PoiType.Warehouse || poi.Type == PoiType.Depot ? 22f : 12f;
            float width = poi.Type == PoiType.Motel ? 34f : 22f;
            float height = poi.Type == PoiType.Motel ? 7f : poi.Type == PoiType.Warehouse || poi.Type == PoiType.Depot ? 9f : 5f;
            Vec2 shopCentre = site.Centre + inward * (9f + depth * 0.5f);
            buildings.Box(shopCentre, inward, width, depth, 0f, height, style, Palette.Roof(style));
            // Awning + shopfront glass facing the forecourt.
            Vec2 front = shopCentre - inward * (depth * 0.5f + 0.06f);
            buildings.Quad(MeshBuilder.V3(front - along * (width * 0.4f), 0.4f), MeshBuilder.V3(front + along * (width * 0.4f), 0.4f),
                           MeshBuilder.V3(front + along * (width * 0.4f), 2.6f), MeshBuilder.V3(front - along * (width * 0.4f), 2.6f),
                           Palette.Glass, MeshBuilder.V3(-inward, 0f));
            buildings.Box(front - inward * 1.2f, inward, width * 0.9f, 2.4f, 2.8f, 3.2f, Palette.Awnings[poi.Id % Palette.Awnings.Length], Palette.Awnings[poi.Id % Palette.Awnings.Length]);

            if (poi.Type == PoiType.Petrol || poi.Type == PoiType.CarWash)
            {
                // Canopy over the bays.
                float len = site.Bays.Count * LaneGraph.BaySpacing + 4f;
                Vec2 cc = site.Centre - along * 0f;
                buildings.Box(cc, inward, len, 9f, 4.6f, 5.1f, Palette.Canopy, Palette.Canopy);
                foreach (float t in new[] { -0.45f, 0.45f })
                foreach (float d in new[] { -3.5f, 3.5f })
                    buildings.Box(cc + along * (len * t) + inward * d, inward, 0.35f, 0.35f, 0f, 4.6f, Palette.Pole, Palette.Pole);
            }

            // Tall sign by the road with the business name in the pixel font.
            Vec2 signAt = site.Centre - inward * 3.2f + along * (site.Bays.Count * LaneGraph.BaySpacing * 0.5f + 5f);
            buildings.Box(signAt, inward, 0.3f, 0.3f, 0f, 5.4f, Palette.Pole, Palette.Pole);
            float boardW = Mathf.Max(4f, poi.Name.Length * 0.52f + 1f);
            // Board faces the street: wide along the kerb, thin towards the lot.
            buildings.Box(signAt, inward, boardW, 0.35f, 5.2f, 6.8f, Palette.SignBoard, Palette.SignBoard);
            foreach (float side in new[] { 1f, -1f })
                signs.Text(poi.Name, MeshBuilder.V3(signAt, 5.6f) + MeshBuilder.V3(-inward * side, 0f) * 0.19f, MeshBuilder.V3(-inward * side, 0f), 0.62f, style);
            signs.Text(poi.Name, MeshBuilder.V3(shopCentre - inward * (depth * 0.5f + 0.1f), height - 1.4f), MeshBuilder.V3(-inward, 0f), 0.9f, Color.white);
        }

        // ---- landmarks ---------------------------------------------------------------

        int _clockTowerCell = -1, _waterTowerCell = -1;

        public Vector3 LandmarkPosition(bool clock)
        {
            int ci = clock ? _clockTowerCell : _waterTowerCell;
            if (ci < 0) return Vector3.zero;
            var c = Bilinear(CellPolygon(Network.Cells[ci], RoadSpec.SidewalkWidth), 0.5f, 0.5f);
            return MeshBuilder.V3(c, 0f);
        }

        void PickLandmarks()
        {
            var centre = Network.Size * 0.5f;
            float best = float.MaxValue;
            _clockTowerCell = _waterTowerCell = -1;
            for (int i = 0; i < Network.Cells.Count; i++)
            {
                var c = Network.Cells[i];
                if (_poiByCell.ContainsKey(i)) continue;
                var mid = new Vec2((c.X + 0.5f) * Network.CellSize, (c.Y + 0.5f) * Network.CellSize);
                if (c.District == District.Downtown && Vec2.Distance(mid, centre) < best) { best = Vec2.Distance(mid, centre); _clockTowerCell = i; }
            }
            var rng = new Rng((ulong)Seed * 613UL + 5UL);
            var industrial = Enumerable.Range(0, Network.Cells.Count).Where(i => Network.Cells[i].District == District.Industrial && !_poiByCell.ContainsKey(i)).ToList();
            if (industrial.Count > 0) _waterTowerCell = industrial[rng.NextInt(industrial.Count)];
        }

        /// <summary>Town square with a clock tower: visible from most of downtown, a natural meeting point for callouts.</summary>
        void BuildClockTower(Cell cell, MeshBuilder blocks, MeshBuilder buildings, MeshBuilder nature)
        {
            blocks.Extrude(RoundedCellPolygon(cell, 0f, 6f), 0f, KerbHeight, Palette.Kerb, Palette.Sidewalk);
            var lot = CellPolygon(cell, RoadSpec.SidewalkWidth);
            blocks.Flat(RoundedCellPolygon(cell, RoadSpec.SidewalkWidth, 3f), LotHeight, Palette.Plaza);
            var c = Bilinear(lot, 0.5f, 0.5f);
            var rng = new Rng((ulong)Seed * 17UL + 3UL);
            // Paving ring, planters, the tower.
            buildings.Extrude(Circle(c, 14f, 24), LotHeight, LotHeight + 0.05f, Palette.Kerb, new Color(0.84f, 0.8f, 0.72f));
            for (int i = 0; i < 8; i++)
            {
                float a = i * Mathf.PI / 4f;
                var p = c + new Vec2(Mathf.Cos(a), Mathf.Sin(a)) * 20f;
                buildings.Box(p, new Vec2(1, 0), 2.2f, 2.2f, LotHeight, LotHeight + 0.6f, Palette.Kerb, new Color(0.45f, 0.35f, 0.25f));
                Tree(nature, p, ref rng, 0.8f);
            }
            const float w = 7f, h = 34f;
            var stone = new Color(0.92f, 0.86f, 0.72f);
            buildings.Box(c, new Vec2(1, 0), w, w, LotHeight, h, stone, stone);
            buildings.Box(c, new Vec2(1, 0), w + 1f, w + 1f, h, h + 1.2f, new Color(0.75f, 0.68f, 0.55f), new Color(0.75f, 0.68f, 0.55f));
            buildings.Cylinder(MeshBuilder.V3(c, h + 1.2f), (w + 1f) * 0.62f, 0f, 9f, 4, new Color(0.3f, 0.5f, 0.45f));
            foreach (var d in new[] { new Vec2(1, 0), new Vec2(-1, 0), new Vec2(0, 1), new Vec2(0, -1) })
            {
                var face = MeshBuilder.V3(c + d * (w * 0.5f + 0.05f), 0f) + Vector3.up * (h - 5f);
                var n = MeshBuilder.V3(d, 0f);
                buildings.Panel(face, n, 2.6f, 24, 0.1f, Color.white, stone);
                // Hands at ten past ten.
                var side = Vector3.Cross(Vector3.up, n);
                buildings.Beam(face + n * 0.1f, face + n * 0.1f + (Vector3.up * 0.8f - side * 1.3f).normalized * 1.6f, 0.18f, Color.black);
                buildings.Beam(face + n * 0.1f, face + n * 0.1f + (Vector3.up * 0.8f + side * 1.4f).normalized * 2.1f, 0.14f, Color.black);
            }
        }

        /// <summary>Water tower over the industrial zone, the town name painted on the tank.</summary>
        void BuildWaterTower(Cell cell, MeshBuilder blocks, MeshBuilder buildings, SignBuilder signs)
        {
            blocks.Extrude(RoundedCellPolygon(cell, 0f, 6f), 0f, KerbHeight, Palette.Kerb, Palette.Sidewalk);
            var lot = CellPolygon(cell, RoadSpec.SidewalkWidth);
            blocks.Flat(RoundedCellPolygon(cell, RoadSpec.SidewalkWidth, 3f), LotHeight, Palette.Yard);
            var c = Bilinear(lot, 0.5f, 0.5f);
            var steel = new Color(0.62f, 0.72f, 0.8f);
            float legH = 24f;
            for (int i = 0; i < 4; i++)
            {
                float a = Mathf.PI / 4f + i * Mathf.PI / 2f;
                var foot = MeshBuilder.V3(c + new Vec2(Mathf.Cos(a), Mathf.Sin(a)) * 7f, LotHeight);
                var top = MeshBuilder.V3(c + new Vec2(Mathf.Cos(a), Mathf.Sin(a)) * 4.5f, legH);
                buildings.Beam(foot, top, 0.5f, new Color(0.45f, 0.5f, 0.55f));
            }
            buildings.SmoothCylinder(MeshBuilder.V3(c, legH), 6.5f, 6.5f, 7f, steel, 32);
            buildings.SmoothCylinder(MeshBuilder.V3(c, legH + 7f), 6.5f, 0.6f, 3f, new Color(0.5f, 0.58f, 0.66f), 32);
            buildings.SmoothCylinder(MeshBuilder.V3(c, legH - 1.2f), 1.2f, 6.5f, 1.2f, new Color(0.5f, 0.58f, 0.66f), 32, cap: false);
            string name = TownName.ToUpperInvariant();
            foreach (var d in new[] { new Vec2(1, 0), new Vec2(-1, 0), new Vec2(0, 1), new Vec2(0, -1) })
                signs.Text(name, MeshBuilder.V3(c + d * 6.55f, legH + 2.6f), MeshBuilder.V3(d, 0f), Mathf.Min(1.8f, 10f / (name.Length * 0.72f)), new Color(0.15f, 0.25f, 0.45f));
        }

        static readonly string[] TownNames = { "Tailton", "Mirrorvale", "Plateburg", "Little Tailing", "Rearview Falls", "Shadow Creek", "Glancebury" };
        public string TownName => TownNames[(int)((uint)Seed % (uint)TownNames.Length)];

        static Vec2[] Circle(Vec2 c, float r, int n)
        {
            var pts = new Vec2[n];
            for (int i = 0; i < n; i++) { float a = i * Mathf.PI * 2f / n; pts[i] = c + new Vec2(Mathf.Cos(a), Mathf.Sin(a)) * r; }
            return pts;
        }

        // ---- street furniture ----------------------------------------------------------

        /// <summary>Street lamps (lit at night) along every street, name signs at junctions, downtown planters.</summary>
        void BuildStreetFurniture(MeshBuilder props, GlowBuilder glow, SignBuilder signs, MeshBuilder nature)
        {
            var lampPole = new Color(0.3f, 0.32f, 0.36f);
            var lampHead = new Color(0.9f, 0.88f, 0.8f, 0.9f); // alpha 0.9 → glows at night
            foreach (var e in Network.Edges)
            {
                Vec2 a = Network.Nodes[e.A].Position, b = Network.Nodes[e.B].Position;
                Vec2 d = (b - a).Normalized, n = d.PerpRight;
                float len = Vec2.Distance(a, b), hw = RoadSpec.HalfWidth(e.Class);
                float s0 = Lanes.Junctions[e.A].Setback[e.Id] + 8f, s1 = len - Lanes.Junctions[e.B].Setback[e.Id] - 8f;
                float spacing = e.Class == RoadClass.Arterial ? 28f : 36f;
                int count = Mathf.Max(1, Mathf.FloorToInt((s1 - s0) / spacing) + 1);
                for (int i = 0; i < count; i++)
                {
                    float t = count == 1 ? (s0 + s1) * 0.5f : Mathf.Lerp(s0, s1, i / (float)(count - 1));
                    foreach (float side in new[] { 1f, -1f })
                    {
                        // Stagger sides on local streets.
                        if (e.Class == RoadClass.Local && (i % 2 == 0) != (side > 0)) continue;
                        Vec2 outward = n * side;
                        Vec2 at = a + d * t + outward * (hw + 0.7f);
                        props.SmoothCylinder(MeshBuilder.V3(at, KerbHeight), 0.08f, 0.06f, 6.6f, lampPole, 8);
                        Vec2 headAt = at - outward * 1.6f;
                        props.Beam(MeshBuilder.V3(at, 6.5f), MeshBuilder.V3(headAt, 6.5f), 0.08f, lampPole);
                        props.Box(headAt, outward, 0.35f, 0.7f, 6.25f, 6.45f, lampPole, lampPole);
                        props.Card(MeshBuilder.V3(headAt, 6.24f), Vector3.down, MeshBuilder.V3(d, 0f), 0.3f, 0.6f, lampHead);
                        glow.Disc(MeshBuilder.V3(headAt, 0.05f), 8f, new Color(0.95f, 0.72f, 0.4f));
                    }
                }
            }

            // Street name blades at every real junction, on the corner between the two named streets.
            var blade = new Color(0.12f, 0.45f, 0.28f);
            foreach (var j in Lanes.Junctions.Values)
            {
                var node = Network.Nodes[j.NodeId];
                if (node.Degree < 3) continue;
                RoadEdge ew = null, ns = null;
                foreach (int eid in node.Edges)
                {
                    var e = Network.Edges[eid];
                    var dir = Network.Dir(e, node.Id);
                    if (Mathf.Abs(dir.X) > Mathf.Abs(dir.Y)) { if (ew == null) ew = e; } else if (ns == null) ns = e;
                }
                if (ew == null || ns == null) continue;
                Vec2 d1 = Network.Dir(ns, node.Id), d2 = Network.Dir(ew, node.Id);
                Vec2 corner = node.Position + d1 * RoadSpec.HalfWidth(ew.Class) + d2 * RoadSpec.HalfWidth(ns.Class);
                Vec2 pole = corner + (d1 + d2).Normalized * 2.4f;
                props.SmoothCylinder(MeshBuilder.V3(pole, KerbHeight), 0.05f, 0.05f, 3.4f, Palette.Pole, 8);
                Blade(props, signs, pole, d1, 3.05f, ns.StreetName, blade);
                Blade(props, signs, pole, d2, 3.35f, ew.StreetName, blade);
            }
        }

        static void Blade(MeshBuilder mb, SignBuilder signs, Vec2 pole, Vec2 along, float y, string text, Color color)
        {
            float length = Mathf.Max(1.4f, text.Length * 0.17f + 0.3f);
            Vec2 centre = pole + along * (length * 0.5f);
            mb.Box(centre, along, 0.04f, length, y - 0.13f, y + 0.13f, color, color);
            foreach (float s in new[] { 1f, -1f })
            {
                var nrm = MeshBuilder.V3(along.PerpRight * s, 0f);
                signs.Text(text, MeshBuilder.V3(centre, y - 0.085f) + nrm * 0.025f, nrm, 0.17f, Color.white);
            }
        }

        public static Vec2 BayCentre(ParkingBay bay) => bay.Position - bay.Heading * 2.2f;

        static Color PoiStyle(PoiType t)
        {
            switch (t)
            {
                case PoiType.Petrol: return new Color(0.95f, 0.85f, 0.3f);
                case PoiType.Diner: return new Color(0.95f, 0.55f, 0.6f);
                case PoiType.Laundromat: return new Color(0.55f, 0.8f, 0.95f);
                case PoiType.CarWash: return new Color(0.45f, 0.75f, 0.95f);
                case PoiType.RentalLot: return new Color(0.5f, 0.85f, 0.5f);
                case PoiType.ChopShop: return new Color(0.55f, 0.5f, 0.48f);
                case PoiType.Motel: return new Color(0.95f, 0.7f, 0.45f);
                case PoiType.Warehouse: case PoiType.Depot: case PoiType.ScrapYard: return new Color(0.66f, 0.7f, 0.76f);
                default: return new Color(0.85f, 0.75f, 0.95f);
            }
        }

        // ---- ground & blocks ---------------------------------------------------------

        void BuildGround(MeshBuilder mb)
        {
            float m = RoadSpec.HalfWidth(RoadClass.Arterial);
            var s = Network.Size;
            mb.Flat(new[] { new Vec2(-m, -m), new Vec2(s.X + m, -m), new Vec2(s.X + m, s.Y + m), new Vec2(-m, s.Y + m) }, 0f, Palette.Asphalt);
            const float far = 3000f;
            mb.Flat(new[] { new Vec2(-far, -far), new Vec2(far, -far), new Vec2(far, far), new Vec2(-far, far) }, -0.05f, Palette.Countryside);
        }

        /// <summary>Kerbed sidewalk outside the boundary road, then a treeline marking the edge of the map.</summary>
        void BuildOutskirts(MeshBuilder blocks, MeshBuilder nature)
        {
            float m = RoadSpec.HalfWidth(RoadClass.Arterial), w = RoadSpec.SidewalkWidth;
            var s = Network.Size;
            float x0 = -m, y0 = -m, x1 = s.X + m, y1 = s.Y + m;
            Vec2[] Rect(float ax, float ay, float bx, float by) => new[] { new Vec2(ax, ay), new Vec2(bx, ay), new Vec2(bx, by), new Vec2(ax, by) };
            blocks.Extrude(Rect(x0 - w, y0 - w, x1 + w, y0), 0f, KerbHeight, Palette.Kerb, Palette.Sidewalk);
            blocks.Extrude(Rect(x0 - w, y1, x1 + w, y1 + w), 0f, KerbHeight, Palette.Kerb, Palette.Sidewalk);
            blocks.Extrude(Rect(x0 - w, y0, x0, y1), 0f, KerbHeight, Palette.Kerb, Palette.Sidewalk);
            blocks.Extrude(Rect(x1, y0, x1 + w, y1), 0f, KerbHeight, Palette.Kerb, Palette.Sidewalk);

            var rng = new Rng((ulong)Seed * 31UL + 17UL);
            float perimeter = 2f * (s.X + s.Y);
            for (float d = 0f; d < perimeter; d += 9f)
            {
                for (int row = 0; row < 3; row++)
                {
                    float t = d + rng.Range(-4f, 4f), off = m + w + 6f + row * 11f + rng.Range(0f, 6f);
                    Vec2 p;
                    if (t < s.X) p = new Vec2(t, -off);
                    else if (t < s.X + s.Y) p = new Vec2(s.X + off, t - s.X);
                    else if (t < 2 * s.X + s.Y) p = new Vec2(2 * s.X + s.Y - t, s.Y + off);
                    else p = new Vec2(-off, perimeter - t);
                    Tree(nature, p, ref rng, 1.5f);
                }
            }
        }

        /// <summary>
        /// Cell outline for rendering, with corners rounded where two streets meet (road corner radius).
        /// Layout code keeps using the exact quad from <see cref="CellPolygon"/>.
        /// </summary>
        Vec2[] RoundedCellPolygon(Cell cell, float extra, float radius)
        {
            var q = CellPolygon(cell, extra);
            var pts = new List<Vec2>();
            for (int k = 0; k < 4; k++)
            {
                int prev = (k + 3) % 4;
                bool round = cell.Sides[prev] >= 0 && cell.Sides[k] >= 0;
                Vec2 p = q[k], a = q[prev], b = q[(k + 1) % 4];
                if (!round) { pts.Add(p); continue; }
                float ra = Math.Min(radius, Vec2.Distance(a, p) * 0.4f), rb = Math.Min(radius, Vec2.Distance(p, b) * 0.4f);
                Vec2 s0 = p + (a - p).Normalized * ra, s1 = p + (b - p).Normalized * rb;
                const int steps = 6;
                for (int i = 0; i <= steps; i++)
                {
                    float t = i / (float)steps;
                    pts.Add(s0 * ((1 - t) * (1 - t)) + p * (2 * (1 - t) * t) + s1 * (t * t));
                }
            }
            return pts.ToArray();
        }

        Vec2[] CellPolygon(Cell cell, float extra)
        {
            var c = new Vec2[4];
            for (int k = 0; k < 4; k++) c[k] = Network.Nodes[cell.Corners[k]].Position;
            var inset = new float[4];
            for (int k = 0; k < 4; k++)
                inset[k] = cell.Sides[k] < 0 ? 0f : RoadSpec.HalfWidth(Network.Edges[cell.Sides[k]].Class) + extra;
            return InsetQuad(c, inset);
        }

        /// <summary>Offset each side of a convex CCW quad inwards by its own distance.</summary>
        static Vec2[] InsetQuad(Vec2[] c, float[] inset)
        {
            var result = new Vec2[4];
            for (int k = 0; k < 4; k++)
            {
                int prev = (k + 3) % 4;
                Vec2 dPrev = (c[k] - c[prev]).Normalized, dCur = (c[(k + 1) % 4] - c[k]).Normalized;
                Vec2 pPrev = c[prev] + dPrev.PerpLeft * inset[prev];
                Vec2 pCur = c[k] + dCur.PerpLeft * inset[k];
                Vec2.LineIntersection(pPrev, dPrev, pCur, dCur, out result[k]);
            }
            return result;
        }

        void BuildCell(Cell cell, MeshBuilder blocks, MeshBuilder buildings, MeshBuilder nature)
        {
            var rng = new Rng((ulong)Seed * 7919UL + (ulong)(cell.Y * Network.CellsX + cell.X));
            var sidewalk = RoundedCellPolygon(cell, 0f, 6f);
            var lot = CellPolygon(cell, RoadSpec.SidewalkWidth);
            var lotShape = RoundedCellPolygon(cell, RoadSpec.SidewalkWidth, 3f);
            blocks.Extrude(sidewalk, 0f, KerbHeight, Palette.Kerb, Palette.Sidewalk);

            switch (cell.District)
            {
                case District.Downtown:
                    blocks.Flat(lotShape, LotHeight, Palette.Plaza);
                    Downtown(cell, lot, ref rng, buildings);
                    SidewalkTrees(cell, ref rng, buildings, nature);
                    break;
                case District.Suburb:
                    blocks.Flat(lotShape, LotHeight, Palette.Lawn);
                    Suburb(cell, lot, ref rng, buildings, nature);
                    break;
                case District.Industrial:
                    blocks.Flat(lotShape, LotHeight, Palette.Yard);
                    Industrial(lot, ref rng, buildings);
                    break;
                case District.Park:
                    blocks.Flat(lotShape, LotHeight, Palette.ParkGrass);
                    for (int i = 0; i < 14; i++) Tree(nature, Bilinear(lot, rng.Range(0.1f, 0.9f), rng.Range(0.1f, 0.9f)), ref rng, 1.2f);
                    break;
            }
        }

        static Vec2 Bilinear(Vec2[] q, float u, float v) =>
            Vec2.Lerp(Vec2.Lerp(q[0], q[1], u), Vec2.Lerp(q[3], q[2], u), v);

        static Vec2[] SubQuad(Vec2[] q, float u0, float u1, float v0, float v1) => new[]
        {
            Bilinear(q, u0, v0), Bilinear(q, u1, v0), Bilinear(q, u1, v1), Bilinear(q, u0, v1),
        };

        void Downtown(Cell cell, Vec2[] lot, ref Rng rng, MeshBuilder mb)
        {
            int nu = 2 + rng.NextInt(2), nv = 2 + rng.NextInt(2);
            const float gap = 0.015f;
            for (int a = 0; a < nu; a++)
            for (int b = 0; b < nv; b++)
            {
                var fp = SubQuad(lot, a / (float)nu + gap, (a + 1) / (float)nu - gap, b / (float)nv + gap, (b + 1) / (float)nv - gap);
                int floors = 3 + rng.NextInt(8);
                if (rng.NextDouble() < 0.15) floors += 6; // the odd tower
                float h = 4.5f + floors * 3.2f;
                var body = Palette.Pick(Palette.Pastels, ref rng);
                var shop = Palette.Pick(Palette.Awnings, ref rng);
                mb.Extrude(fp, LotHeight, 4.5f, shop, shop);
                mb.Extrude(fp, 4.5f, h, body, Palette.Roof(body));
                WindowBands(mb, fp, floors, 4.5f);
                // Rooftop clutter reads well from a distance.
                var c = Bilinear(fp, 0.5f, 0.5f);
                mb.Box(c + new Vec2(rng.Range(-3f, 3f), rng.Range(-3f, 3f)), new Vec2(1, 0), 3f, 4f, h, h + 1.6f, Palette.RooftopUnit, Palette.RooftopUnit);
            }
        }

        /// <summary>Planter trees down the middle of downtown sidewalks.</summary>
        void SidewalkTrees(Cell cell, ref Rng rng, MeshBuilder buildings, MeshBuilder nature)
        {
            var q = CellPolygon(cell, 0f);
            for (int k = 0; k < 4; k++)
            {
                if (cell.Sides[k] < 0) continue;
                Vec2 p = q[k], r = q[(k + 1) % 4];
                Vec2 inward = (r - p).Normalized.PerpLeft;
                float len = Vec2.Distance(p, r);
                for (float t = 14f; t < len - 12f; t += 16f)
                {
                    var at = Vec2.Lerp(p, r, t / len) + inward * 1.6f;
                    buildings.Box(at, inward, 1.1f, 1.1f, KerbHeight, KerbHeight + 0.45f, Palette.Kerb, new Color(0.4f, 0.3f, 0.22f));
                    Tree(nature, at, ref rng, 0.6f);
                }
            }
        }

        static void WindowBands(MeshBuilder mb, Vec2[] fp, int floors, float baseY)
        {
            Vec2 centre = Bilinear(fp, 0.5f, 0.5f);
            for (int k = 0; k < fp.Length; k++)
            {
                Vec2 p = fp[k], q = fp[(k + 1) % fp.Length];
                Vec2 outward = (Vec2.Lerp(p, q, 0.5f) - centre).Normalized * 0.06f;
                Vec2 a = Vec2.Lerp(p, q, 0.08f) + outward, b = Vec2.Lerp(p, q, 0.92f) + outward;
                for (int f = 0; f < floors; f++)
                {
                    float y0 = baseY + f * 3.2f + 1.0f, y1 = y0 + 1.3f;
                    mb.Quad(MeshBuilder.V3(a, y0), MeshBuilder.V3(b, y0), MeshBuilder.V3(b, y1), MeshBuilder.V3(a, y1),
                            Palette.Glass, MeshBuilder.V3(outward, 0f));
                }
            }
        }

        void Suburb(Cell cell, Vec2[] lot, ref Rng rng, MeshBuilder mb, MeshBuilder nature)
        {
            for (int k = 0; k < 4; k++)
            {
                if (cell.Sides[k] < 0) continue; // no street on this side
                Vec2 p = lot[k], q = lot[(k + 1) % 4];
                Vec2 along = (q - p).Normalized, inward = along.PerpLeft;
                float sideLen = Vec2.Distance(p, q);
                // Leave the corners clear (the perpendicular street's houses live there).
                int houses = Mathf.Max(2, Mathf.FloorToInt((sideLen - 24f) / 17f));
                for (int h = 0; h < houses; h++)
                {
                    float t = (12f + (h + 0.5f) * (sideLen - 24f) / houses) / sideLen;
                    float w = rng.Range(8f, 11f), d = rng.Range(8f, 10f);
                    Vec2 centre = Vec2.Lerp(p, q, t) + inward * (5f + d * 0.5f);
                    var wall = Palette.Pick(Palette.Pastels, ref rng);
                    var roof = Palette.Pick(Palette.HouseRoofs, ref rng);
                    float wallH = rng.NextDouble() < 0.4 ? 5.8f : 3.2f;
                    mb.GableHouse(centre, inward, w, d, wallH + LotHeight, rng.Range(2.2f, 3.2f), wall, roof);
                    // Front door facing the street.
                    Vec2 door = centre - inward * (d * 0.5f + 0.03f);
                    mb.Quad(MeshBuilder.V3(door - along * 0.5f, LotHeight), MeshBuilder.V3(door + along * 0.5f, LotHeight),
                            MeshBuilder.V3(door + along * 0.5f, LotHeight + 2.1f), MeshBuilder.V3(door - along * 0.5f, LotHeight + 2.1f),
                            Palette.Door, MeshBuilder.V3(-inward, 0f));
                    if (rng.NextDouble() < 0.5) Tree(nature, Vec2.Lerp(p, q, t) + along * (w * 0.5f + 2.5f) + inward * 3f, ref rng, 0.8f);
                    // Driveway with the household car on it (most houses): front yard, beside the door.
                    if (rng.NextDouble() < 0.6)
                    {
                        Vec2 pad = Vec2.Lerp(p, q, t) - along * (w * 0.28f) + inward * 2.6f;
                        mb.Flat(MeshBuilder.Rect(pad, inward, 2.9f, 5.1f), LotHeight + 0.012f, Palette.Driveway);
                        _parked.Add((HouseholdCar(ref rng), PickColor(ref rng), pad, rng.NextDouble() < 0.7 ? inward : -inward));
                    }
                }
            }
            for (int i = 0; i < 3; i++) Tree(nature, Bilinear(lot, rng.Range(0.35f, 0.65f), rng.Range(0.35f, 0.65f)), ref rng, 1f);
        }

        void Industrial(Vec2[] lot, ref Rng rng, MeshBuilder mb)
        {
            var fp = SubQuad(lot, 0.08f, 0.92f, 0.12f, 0.88f);
            float h = rng.Range(7f, 11f);
            var wall = Palette.Pick(Palette.Warehouses, ref rng);
            mb.Extrude(fp, LotHeight, h, wall, Palette.Roof(wall));
            // Roller doors on the first side.
            Vec2 p = fp[0], q = fp[1], centre = Bilinear(fp, 0.5f, 0.5f);
            Vec2 outward = (Vec2.Lerp(p, q, 0.5f) - centre).Normalized * 0.05f;
            for (int dIdx = 0; dIdx < 3; dIdx++)
            {
                Vec2 a = Vec2.Lerp(p, q, 0.15f + dIdx * 0.25f) + outward, b = Vec2.Lerp(p, q, 0.3f + dIdx * 0.25f) + outward;
                mb.Quad(MeshBuilder.V3(a, LotHeight), MeshBuilder.V3(b, LotHeight), MeshBuilder.V3(b, 4.5f), MeshBuilder.V3(a, 4.5f),
                        Palette.RollerDoor, MeshBuilder.V3(outward, 0f));
                // A van or lorry at some of the doors, parked alongside the wall.
                if (rng.NextDouble() < 0.45)
                {
                    int model = rng.NextDouble() < 0.5 ? LorryModel : VanModel;
                    Vec2 at = Vec2.Lerp(a, b, 0.5f) + outward.Normalized * 2.4f;
                    int color = model == LorryModel ? 1 : PickColor(ref rng);
                    _parked.Add((model, color, at, (q - p).Normalized * (rng.NextDouble() < 0.5 ? 1f : -1f)));
                }
            }
            for (int i = 0; i < 2; i++)
            {
                var c = Bilinear(fp, rng.Range(0.25f, 0.75f), rng.Range(0.25f, 0.75f));
                mb.Box(c, new Vec2(1, 0), 4f, 6f, h, h + 2f, Palette.RooftopUnit, Palette.RooftopUnit);
            }
        }

        static readonly int VanModel = System.Array.FindIndex(VehicleCatalog.Models, m => m.Style == BodyStyle.Van);
        static readonly int LorryModel = System.Array.FindIndex(VehicleCatalog.Models, m => m.Style == BodyStyle.BoxTruck);

        static int HouseholdCar(ref Rng rng)
        {
            int m;
            do m = rng.PickWeighted(VehicleCatalog.ModelWeights()); while (VehicleCatalog.Models[m].NpcOnly);
            return m;
        }

        static int PickColor(ref Rng rng) => rng.PickWeighted(VehicleCatalog.ColorWeights());

        /// <summary>
        /// Real (solid, plated) but motionless cars. They're scenery and cover: a Tail can sit among
        /// them. Plates come from their own series so they never duplicate a moving car's.
        /// </summary>
        void SpawnParkedCars()
        {
            var root = new GameObject("ParkedCars").transform;
            root.SetParent(transform, false);
            var ids = new IdentityService((ulong)Seed ^ 0x9E3779B97F4A7C15UL);
            for (int i = 0; i < _parked.Count; i++)
            {
                var (model, color, at, facing) = _parked[i];
                var view = Tailed.Vehicles.VehicleView.Create(root);
                view.gameObject.layer = Layers.Vehicles;
                view.Bind(ParkedIdBase + i, ids.Create(model, color));
                view.HideDriver();
                view.SetPose(MeshBuilder.V3(at, LotHeight), MeshBuilder.V3(facing, 0f), 0f, VehicleFlags.None, 0f, steerFromMotion: false);
            }
        }

        /// <summary>Vehicle ids for parked decor (never in the traffic sim).</summary>
        public const int ParkedIdBase = 500000;

        /// <summary>
        /// Bus shelters (bench, roof, back panel, sign) at bus stops; yellow loading boxes where vans
        /// double-park. The traffic sim stops vehicles at these same points (LaneGraph.KerbStops).
        /// </summary>
        void BuildKerbStops(MeshBuilder solid, MeshBuilder props, MeshBuilder markings, SignBuilder signs)
        {
            foreach (var k in Lanes.KerbStops)
            {
                var lane = Lanes.Lanes[k.Lane];
                Vec2 d = lane.Direction, right = d.PerpRight;
                Vec2 kerb = lane.PointAt(k.S) + right * (lane.Width * 0.5f);
                if (!k.BusStop)
                {
                    Vec2 a = kerb - right * 0.25f + d * -5f, b = kerb - right * 0.25f + d * 9f;
                    Dashed(markings, a, b, 0.15f, 1f, 0.6f, Palette.LineYellow);
                    continue;
                }
                // Painted bay on the road.
                Vec2 r0 = kerb - d * 6f, r1 = kerb + d * 8f;
                Strip(markings, r0 - right * 2.6f, r1 - right * 2.6f, 0.14f, Palette.LineYellow);
                Strip(markings, r0 - right * 2.6f, r0, 0.14f, Palette.LineYellow);
                Strip(markings, r1 - right * 2.6f, r1, 0.14f, Palette.LineYellow);

                Vec2 c = kerb + right * (RoadSpec.SidewalkWidth * 0.62f);
                float y0 = KerbHeight;
                solid.Box(c + right * 0.62f, d, 0.08f, 3.8f, y0, y0 + 2.3f, Palette.ShelterPanel, Palette.ShelterPanel); // back
                foreach (float e in new[] { -1.9f, 1.9f })
                    solid.Box(c + d * e + right * 0.2f, d, 0.9f, 0.06f, y0 + 0.3f, y0 + 2.2f, Palette.ShelterPanel, Palette.ShelterPanel);
                props.Box(c, d, 1.6f, 4.2f, y0 + 2.35f, y0 + 2.5f, Palette.ShelterRoof, Palette.ShelterRoof);
                foreach (float e in new[] { -1.95f, 1.95f })
                foreach (float o in new[] { -0.6f, 0.62f })
                    props.SmoothCylinder(MeshBuilder.V3(c + d * e + right * o, y0), 0.05f, 0.05f, 2.35f, Palette.SignalPole, 8);
                props.Box(c + right * 0.4f, d, 0.4f, 2.4f, y0 + 0.45f, y0 + 0.52f, Palette.Bench, Palette.Bench);
                props.Box(c + d * 1.6f + right * 0.56f, d, 0.05f, 0.9f, y0 + 0.5f, y0 + 2.0f, Palette.Advert(k.Id), Palette.Advert(k.Id));
                // Stop flag facing approaching traffic.
                Vec2 poleAt = kerb + right * 0.5f - d * 3.2f;
                props.SmoothCylinder(MeshBuilder.V3(poleAt, y0), 0.05f, 0.05f, 2.6f, Palette.Pole, 10);
                var flag = MeshBuilder.V3(poleAt, y0 + 2.75f);
                Vector3 facing = -MeshBuilder.V3(d, 0f);
                props.Panel(flag, facing, 0.34f, 16, 0.03f, Palette.BusSign, Palette.Pole);
                signs.Text("BUS", flag + facing * 0.025f - Vector3.up * 0.07f, facing, 0.16f, Color.white);
                signs.Text("BUS", flag - facing * 0.025f - Vector3.up * 0.07f, -facing, 0.16f, Color.white);
            }
        }

        static void Tree(MeshBuilder mb, Vec2 at, ref Rng rng, float scale)
        {
            scale *= rng.Range(0.8f, 1.25f);
            var basePos = MeshBuilder.V3(at, LotHeight);
            var leaf = Palette.Pick(Palette.Foliage, ref rng);
            mb.Cylinder(basePos, 0.25f * scale, 0.2f * scale, 2f * scale, 5, Palette.Trunk);
            mb.Cylinder(basePos + Vector3.up * 1.6f * scale, 2.2f * scale, 0f, 3.2f * scale, 7, leaf);
            mb.Cylinder(basePos + Vector3.up * 3.2f * scale, 1.6f * scale, 0f, 2.6f * scale, 7, leaf);
        }

        // ---- markings & props --------------------------------------------------------

        void BuildMarkings(MeshBuilder mb)
        {
            foreach (var e in Network.Edges)
            {
                var ids = Lanes.EdgeLanes[e.Id];
                var fwd = Lanes.Lanes[ids[0]]; // kerbside A→B lane
                var j0 = Lanes.Junctions[e.A];
                var j1 = Lanes.Junctions[e.B];
                Vec2 a = Network.Nodes[e.A].Position, b = Network.Nodes[e.B].Position;
                Vec2 dir = fwd.Direction, right = dir.PerpRight;
                Vec2 s = a + dir * j0.Setback[e.Id], t = b - dir * j1.Setback[e.Id];

                if (e.Class == RoadClass.Arterial)
                {
                    Strip(mb, s + right * 0.15f, t + right * 0.15f, 0.12f, Palette.LineYellow);
                    Strip(mb, s - right * 0.15f, t - right * 0.15f, 0.12f, Palette.LineYellow);
                    float w = RoadSpec.LaneWidth(e.Class);
                    Dashed(mb, s + right * w, t + right * w, 0.12f, 3f, 5f, Palette.LineWhite);
                    Dashed(mb, s - right * w, t - right * w, 0.12f, 3f, 5f, Palette.LineWhite);
                }
                else
                {
                    Dashed(mb, s, t, 0.12f, 3f, 3f, Palette.LineYellow);
                }
            }

            foreach (var lane in Lanes.Lanes)
            {
                if (lane.Control == ApproachControl.Free) continue;
                Vec2 r = lane.Direction.PerpRight * (lane.Width * 0.5f);
                Vec2 back = lane.Direction * 0.25f;
                Strip(mb, lane.End - r - back, lane.End + r - back, 0.45f, Palette.LineWhite);
                if (lane.Control == ApproachControl.Signal && lane.Index == 0) Crosswalk(mb, lane);
            }
        }

        static void Crosswalk(MeshBuilder mb, Lane lane)
        {
            // Zebra kerb to kerb (both directions), between the stop line and the junction box.
            // Called for the kerbside lane only; x runs from this kerb across to the far kerb.
            Vec2 r = lane.Direction.PerpRight;
            Vec2 c0 = lane.End + lane.Direction * 1.0f, c1 = lane.End + lane.Direction * 4.0f;
            float roadWidth = 2f * lane.LanesInDirection * lane.Width;
            for (float x = 0.6f; x < roadWidth - 0.4f; x += 1.1f)
            {
                Vec2 o = r * (lane.Width * 0.5f - x);
                Strip(mb, c0 + o, c1 + o, 0.55f, Palette.LineWhite);
            }
        }

        static void Strip(MeshBuilder mb, Vec2 a, Vec2 b, float width, Color color, float y = MarkingY)
        {
            Vec2 n = (b - a).Normalized.PerpRight * (width * 0.5f);
            mb.Flat(new[] { a - n, b - n, b + n, a + n }, y, color);
        }

        static void Dashed(MeshBuilder mb, Vec2 a, Vec2 b, float width, float dash, float gap, Color color)
        {
            float len = Vec2.Distance(a, b);
            Vec2 d = (b - a) / len;
            for (float x = gap * 0.5f; x + dash < len; x += dash + gap)
                Strip(mb, a + d * x, a + d * (x + dash), width, color);
        }

        void BuildProps(MeshBuilder mb, MeshBuilder lamps, List<SignalLamps.Lamp> records, SignBuilder signs)
        {
            foreach (var lane in Lanes.Lanes)
            {
                if (lane.Index != 0 || lane.Control == ApproachControl.Free) continue;
                Vec2 d = lane.Direction, right = d.PerpRight;
                Vector3 facing = -MeshBuilder.V3(d, 0f);
                if (lane.Control == ApproachControl.Stop)
                {
                    // Stop sign: white-bordered red octagon with real lettering.
                    var pole = MeshBuilder.V3(lane.End + right * (lane.Width * 0.5f + 1.2f), KerbHeight);
                    mb.SmoothCylinder(pole, 0.045f, 0.045f, 2.2f, Palette.Pole, 10);
                    var c = pole + Vector3.up * 2.45f + facing * 0.06f;
                    mb.Panel(c, facing, 0.47f, 8, 0.03f, Color.white, Palette.Pole);
                    mb.Panel(c + facing * 0.02f, facing, 0.42f, 8, 0.01f, Palette.StopRed, Palette.StopRed);
                    signs.Text("STOP", c + facing * 0.035f - Vector3.up * 0.1f, facing, 0.2f, Color.white);
                    continue;
                }

                // Signalised approach. S = distance from the junction centre to the stop line.
                float S = Lanes.Junctions[lane.ToNode].Setback[lane.EdgeId];
                float roadHalf = lane.LanesInDirection * lane.Width;

                // Near side: post-mounted head beside the stop line, at eye level (visible when stopped first in line).
                Vec2 nearAt = lane.End + right * (lane.Width * 0.5f + 1.1f);
                mb.SmoothCylinder(MeshBuilder.V3(nearAt, KerbHeight), 0.09f, 0.08f, 3.6f, Palette.SignalPole, 12);
                SignalHead(mb, lamps, records, lane, nearAt - d * 0.25f, 2.35f, 0.8f);

                // Far side: mast arm past the junction with a head over each approach lane. The pole
                // stands on the far road's kerb (which may be wider than ours, or angled), not on a line
                // offset from our own lane — that put some poles in the far road's traffic lane.
                Vec2 farBase = lane.End + d * (2f * S + 0.5f);
                Vec2 farPole = FarPolePosition(lane, farBase, right);
                PolePositions.Add(farPole);
                OldFarPoles.Add(farBase + right * (lane.Width * 0.5f + 1.7f)); // the previous placement, for the audit
                PolePositions.Add(nearAt);
                mb.SmoothCylinder(MeshBuilder.V3(farPole, KerbHeight), 0.14f, 0.11f, 6.1f, Palette.SignalPole, 14);
                Vec2 farthest = farBase - right * ((lane.LanesInDirection - 1) * lane.Width);
                Vec2 armDir = (farthest - farPole).Normalized;
                float arm = Vec2.Distance(farPole, farthest) + 0.6f;
                mb.Box(farPole + armDir * (arm * 0.5f), armDir, 0.16f, arm, 5.8f, 5.98f, Palette.SignalPole, Palette.SignalPole);
                for (int l = 0; l < lane.LanesInDirection; l++)
                {
                    Vec2 over = farBase - right * (l * lane.Width);
                    // Hang each head from the arm, directly above its approach lane's extension.
                    Vec2 onArm = farPole + armDir * Vec2.Dot(over - farPole, armDir);
                    mb.Box(onArm, d, 0.06f, 0.06f, 5.55f, 5.8f, Palette.SignalPole, Palette.SignalPole); // hanger
                    SignalHead(mb, lamps, records, lane, onArm, 4.4f, 1f);
                }
            }
        }

        /// <summary>Every signal pole's ground position (for the audit: none may stand in a carriageway).</summary>
        public readonly List<Vec2> PolePositions = new List<Vec2>();
        public readonly List<Vec2> OldFarPoles = new List<Vec2>();

        /// <summary>
        /// Far-side signal pole: on the kerb of the road straight ahead, just past the junction. With no
        /// road straight ahead (top of a T), the verge beyond the junction is already off-road.
        /// </summary>
        Vec2 FarPolePosition(Lane lane, Vec2 farBase, Vec2 right)
        {
            foreach (int c in lane.Outgoing)
            {
                var con = Lanes.Connectors[c];
                if (con.Turn != TurnType.Straight) continue;
                var outLanes = Lanes.LanesLeaving(Lanes.Lanes[con.ToLane].EdgeId, lane.ToNode);
                var kerb = Lanes.Lanes[outLanes[0]]; // index 0 = kerb side
                return kerb.Start + kerb.Direction * 1.2f + kerb.Direction.PerpRight * (kerb.Width * 0.5f + 1.7f);
            }
            return farBase + right * (lane.Width * 0.5f + 1.7f);
        }

        /// <summary>Signal poles standing in a lane or inside a junction's paths (should be none).</summary>
        public List<Vec2> PolesOnRoad(List<Vec2> poles = null)
        {
            var bad = new List<Vec2>();
            foreach (var p in poles ?? PolePositions)
            {
                bool hit = false;
                foreach (var l in Lanes.Lanes)
                {
                    float s = Vec2.Dot(p - l.Start, l.Direction);
                    if (s < -0.5f || s > l.Length + 0.5f) continue;
                    if (MathF.Abs(Vec2.Cross(l.Direction, p - l.Start)) < l.Width * 0.5f + 0.3f) { hit = true; break; }
                }
                if (!hit)
                    foreach (var con in Lanes.Connectors)
                    {
                        if (con.Turn == TurnType.UTurn) continue;
                        foreach (var q in con.Points) if (Vec2.Distance(p, q) < 1.9f) { hit = true; break; }
                        if (hit) break;
                    }
                if (hit) bad.Add(p);
            }
            return bad;
        }

        /// <summary>Three-lamp head facing traffic on <paramref name="lane"/>, lamps registered for live state.</summary>
        static void SignalHead(MeshBuilder mb, MeshBuilder lamps, List<SignalLamps.Lamp> records, Lane lane, Vec2 at, float bottom, float scale)
        {
            Vec2 d = lane.Direction;
            Vector3 facing = -MeshBuilder.V3(d, 0f), up = Vector3.up;
            float w = 0.42f * scale, h = 1.12f * scale, depth = 0.3f * scale;
            mb.Box(at, d, w, depth, bottom, bottom + h, Palette.SignalHead, Palette.SignalHead);
            // Backplate with a reflective yellow border: reads at a glance, day or night.
            var mid = MeshBuilder.V3(at, 0f) + up * (bottom + h * 0.5f);
            mb.Card(mid + facing * 0.004f, facing, up, w + 0.34f * scale, h + 0.3f * scale, Palette.SignalBorder);
            mb.Card(mid + facing * 0.008f, facing, up, w + 0.24f * scale, h + 0.2f * scale, Palette.SignalHead);
            var rec = new SignalLamps.Lamp { Node = lane.ToNode, Edge = lane.EdgeId, Sides = 18 };
            float r = 0.125f * scale;
            var lampFace = MeshBuilder.V3(at, 0f) + facing * (depth * 0.5f + 0.012f);
            for (int k = 0; k < 3; k++)
            {
                float y = bottom + h * (0.8f - 0.3f * k);
                if (k == 0) rec.Red = lamps.VertexCount; else if (k == 1) rec.Amber = lamps.VertexCount; else rec.Green = lamps.VertexCount;
                lamps.Panel(lampFace + up * y, facing, r, 18, 0.02f, Palette.LampOff, Palette.SignalHead);
                // Visor over each lamp.
                mb.Box(at - d * (depth * 0.5f + 0.1f * scale), d, r * 2.3f, 0.2f * scale, y + r * 0.9f, y + r * 1.05f, Palette.SignalHead, Palette.SignalHead);
            }
            records.Add(rec);
        }
    }

    /// <summary>sRGB palette. Big, clean colour separation per material class (friendslop readability).</summary>
    static partial class Palette
    {
        public static readonly Color Asphalt = new Color(0.30f, 0.31f, 0.36f);
        public static readonly Color Countryside = new Color(0.56f, 0.74f, 0.40f);
        public static readonly Color Sidewalk = new Color(0.80f, 0.79f, 0.76f);
        public static readonly Color Kerb = new Color(0.68f, 0.67f, 0.65f);
        public static readonly Color Plaza = new Color(0.72f, 0.70f, 0.67f);
        public static readonly Color Lawn = new Color(0.52f, 0.76f, 0.36f);
        public static readonly Color ParkGrass = new Color(0.44f, 0.72f, 0.33f);
        public static readonly Color Yard = new Color(0.56f, 0.56f, 0.55f);
        /// <summary>Alpha 0.95 tags windows: the toon shader lights some of them at night.</summary>
        public static readonly Color Glass = new Color(0.30f, 0.42f, 0.58f, 0.95f);
        public static readonly Color Door = new Color(0.45f, 0.28f, 0.2f);
        public static readonly Color RollerDoor = new Color(0.42f, 0.44f, 0.48f);
        public static readonly Color RooftopUnit = new Color(0.62f, 0.64f, 0.66f);
        public static readonly Color Trunk = new Color(0.45f, 0.32f, 0.22f);
        public static readonly Color LineWhite = new Color(0.95f, 0.95f, 0.92f);
        public static readonly Color LineYellow = new Color(0.98f, 0.78f, 0.22f);
        public static readonly Color Pole = new Color(0.6f, 0.62f, 0.66f);
        public static readonly Color Driveway = new Color(0.66f, 0.65f, 0.63f);
        public static readonly Color ShelterPanel = new Color(0.62f, 0.76f, 0.82f);
        public static readonly Color ShelterRoof = new Color(0.2f, 0.42f, 0.66f);
        public static readonly Color Bench = new Color(0.58f, 0.4f, 0.26f);
        public static readonly Color BusSign = new Color(0.12f, 0.45f, 0.78f);
        static readonly Color[] Adverts = { new Color(0.95f, 0.55f, 0.35f), new Color(0.55f, 0.8f, 0.45f), new Color(0.95f, 0.8f, 0.3f), new Color(0.8f, 0.5f, 0.85f) };
        public static Color Advert(int i) => Adverts[i % Adverts.Length];
        public static readonly Color StopRed = new Color(0.86f, 0.16f, 0.16f);
        public static readonly Color SignalPole = new Color(0.25f, 0.27f, 0.3f);
        public static readonly Color SignalHead = new Color(0.16f, 0.17f, 0.16f);
        public static readonly Color LampRed = new Color(1f, 0.25f, 0.2f);
        public static readonly Color Forecourt = new Color(0.6f, 0.61f, 0.63f);
        public static readonly Color Canopy = new Color(0.95f, 0.95f, 0.92f);
        public static readonly Color SignBoard = new Color(0.2f, 0.22f, 0.3f);
        public static readonly Color SignalBorder = new Color(1f, 0.82f, 0.15f);
        public static readonly Color LampOff = new Color(0.3f, 0.32f, 0.3f);

        public static readonly Color[] Pastels =
        {
            new Color(0.96f, 0.76f, 0.62f), new Color(0.66f, 0.86f, 0.76f), new Color(0.62f, 0.78f, 0.92f),
            new Color(0.97f, 0.89f, 0.60f), new Color(0.80f, 0.72f, 0.90f), new Color(0.95f, 0.60f, 0.55f),
            new Color(0.95f, 0.92f, 0.84f), new Color(0.72f, 0.88f, 0.92f),
        };
        public static readonly Color[] Awnings =
        {
            new Color(0.85f, 0.3f, 0.3f), new Color(0.25f, 0.55f, 0.75f), new Color(0.3f, 0.65f, 0.45f),
            new Color(0.95f, 0.65f, 0.25f), new Color(0.55f, 0.4f, 0.7f),
        };
        public static readonly Color[] HouseRoofs =
        {
            new Color(0.75f, 0.33f, 0.28f), new Color(0.35f, 0.40f, 0.50f), new Color(0.50f, 0.35f, 0.25f),
            new Color(0.30f, 0.55f, 0.58f),
        };
        public static readonly Color[] Warehouses =
        {
            new Color(0.62f, 0.68f, 0.74f), new Color(0.75f, 0.72f, 0.62f), new Color(0.70f, 0.45f, 0.35f),
            new Color(0.55f, 0.62f, 0.55f),
        };
        public static readonly Color[] Foliage =
        {
            new Color(0.30f, 0.62f, 0.30f), new Color(0.42f, 0.70f, 0.30f), new Color(0.25f, 0.52f, 0.35f),
        };

        public static Color Pick(Color[] set, ref Rng rng) => set[rng.NextInt(set.Length)];
        public static Color Roof(Color wall) => Color.Lerp(wall, new Color(0.35f, 0.35f, 0.4f), 0.45f);
    }
}
