using System.Collections.Generic;
using Tailed.Core.Identity;
using Tailed.Map;
using UnityEngine;

namespace Tailed.Vehicles
{
    /// <summary>Key dimensions of a generated car, in its local space (x right, y up, z forward, origin on the ground).</summary>
    public sealed class CarShape
    {
        public float Length, Width, Height, Clearance, Belt, WheelRadius, WheelBase;
        public float CabinRear, CabinFront, RoofRear, RoofFront, CabinHalfWidth;
        /// <summary>Driver's eye (left-hand drive: right-hand traffic).</summary>
        public Vector3 Eye;
        public Vector3 RearViewMirror, LeftMirror, RightMirror;
        public Vector3 FrontPlate, RearPlate;
        // Style (for the Blender art pipeline): body features and nose/tail slope lengths (m).
        public string Style;
        public bool PickupBed, CargoBox, RoofRails, Bus, BoxBody;
        public float Hood, Trunk, CabRoofHeight;
    }

    /// <summary>
    /// Builds chunky low-poly cars from a side-profile description per body style. Silhouettes are
    /// deliberately exaggerated so each model reads at mirror distance (GD §11). Meshes are cached
    /// per (model, colour); submesh 0 = body (Tailed/ToonVehicle), submesh 1 = glass.
    /// </summary>
    public static class VehicleMeshFactory
    {
        sealed class Style
        {
            public float Belt, CabinStart, CabinEnd, RoofRearInset, RoofFrontInset, Hood, Trunk, CabinWidth = 0.9f;
            public bool PickupBed, CargoBox, RoofRails, Bus, BoxBody;
            public float RideHeight = 0.5f; // clearance as a fraction of wheel radius
            /// <summary>Cab roof as a fraction of overall height (lorries: the box is taller than the cab).</summary>
            public float CabRoof = 1f;
        }

        static readonly Dictionary<BodyStyle, Style> Styles = new Dictionary<BodyStyle, Style>
        {
            [BodyStyle.Sedan] = new Style { Belt = 0.56f, CabinStart = 0.24f, CabinEnd = 0.7f, RoofRearInset = 0.09f, RoofFrontInset = 0.11f, Hood = 0.05f, Trunk = 0.04f },
            [BodyStyle.Hatch] = new Style { Belt = 0.55f, CabinStart = 0.07f, CabinEnd = 0.66f, RoofRearInset = 0.05f, RoofFrontInset = 0.13f, Hood = 0.06f, Trunk = 0.0f },
            [BodyStyle.Wagon] = new Style { Belt = 0.55f, CabinStart = 0.05f, CabinEnd = 0.7f, RoofRearInset = 0.015f, RoofFrontInset = 0.11f, Hood = 0.05f, Trunk = 0.0f },
            [BodyStyle.Suv] = new Style { Belt = 0.62f, CabinStart = 0.06f, CabinEnd = 0.72f, RoofRearInset = 0.02f, RoofFrontInset = 0.07f, Hood = 0.02f, Trunk = 0.0f, CabinWidth = 0.93f, RoofRails = true, RideHeight = 1.0f },
            [BodyStyle.Pickup] = new Style { Belt = 0.6f, CabinStart = 0.4f, CabinEnd = 0.7f, RoofRearInset = 0.01f, RoofFrontInset = 0.06f, Hood = 0.03f, Trunk = 0.0f, PickupBed = true, RideHeight = 0.95f },
            [BodyStyle.Van] = new Style { Belt = 0.45f, CabinStart = 0.0f, CabinEnd = 0.82f, RoofRearInset = 0.0f, RoofFrontInset = 0.1f, Hood = 0.02f, Trunk = 0.0f, CargoBox = true, CabinWidth = 0.97f },
            [BodyStyle.Cube] = new Style { Belt = 0.42f, CabinStart = 0.03f, CabinEnd = 0.85f, RoofRearInset = 0.0f, RoofFrontInset = 0.04f, Hood = 0.02f, Trunk = 0.0f, CabinWidth = 0.97f },
            [BodyStyle.Bus] = new Style { Belt = 0.36f, CabinStart = 0.012f, CabinEnd = 0.985f, RoofRearInset = 0.0f, RoofFrontInset = 0.012f, Hood = 0.004f, Trunk = 0.004f, CabinWidth = 0.99f, RideHeight = 0.55f, Bus = true },
            [BodyStyle.BoxTruck] = new Style { Belt = 0.4f, CabinStart = 0.7f, CabinEnd = 0.95f, RoofRearInset = 0.0f, RoofFrontInset = 0.05f, Hood = 0.02f, Trunk = 0.004f, CabinWidth = 0.95f, RideHeight = 0.7f, BoxBody = true, CabRoof = 0.8f },
            [BodyStyle.Coupe] = new Style { Belt = 0.52f, CabinStart = 0.3f, CabinEnd = 0.66f, RoofRearInset = 0.17f, RoofFrontInset = 0.12f, Hood = 0.06f, Trunk = 0.05f, CabinWidth = 0.86f },
        };

        static readonly Dictionary<int, Mesh> BodyCache = new Dictionary<int, Mesh>();
        static readonly Dictionary<int, Mesh> DriverCache = new Dictionary<int, Mesh>();
        static readonly Dictionary<int, CarShape> ShapeCache = new Dictionary<int, CarShape>();

        static readonly Color Tyre = new Color(0.12f, 0.12f, 0.13f);
        static readonly Color Hub = new Color(0.75f, 0.76f, 0.78f);
        static readonly Color Trim = new Color(0.18f, 0.18f, 0.2f);
        static readonly Color Glass = new Color(0.55f, 0.7f, 0.82f, 0.28f);
        static readonly Color Interior = new Color(0.22f, 0.22f, 0.25f);
        static readonly Color BedColor = new Color(0.25f, 0.24f, 0.24f);

        public static CarShape Shape(int modelId)
        {
            if (ShapeCache.TryGetValue(modelId, out var cached)) return cached;
            var m = VehicleCatalog.Models[modelId];
            var st = Styles[m.Style];
            float L = m.Length, H = m.Height;
            var s = new CarShape
            {
                Length = L, Width = m.Width, Height = H, WheelRadius = m.WheelRadius, WheelBase = m.WheelBase,
                Clearance = m.WheelRadius * st.RideHeight,
                Belt = H * st.Belt,
                CabinRear = -L * 0.5f + L * st.CabinStart,
                CabinFront = -L * 0.5f + L * st.CabinEnd,
                CabinHalfWidth = m.Width * 0.5f * st.CabinWidth,
            };
            s.Style = m.Style.ToString();
            s.PickupBed = st.PickupBed; s.CargoBox = st.CargoBox; s.RoofRails = st.RoofRails; s.Bus = st.Bus; s.BoxBody = st.BoxBody;
            s.Hood = L * st.Hood; s.Trunk = L * st.Trunk; s.CabRoofHeight = H * st.CabRoof;
            s.RoofRear = s.CabinRear + L * st.RoofRearInset;
            s.RoofFront = s.CabinFront - L * st.RoofFrontInset;
            float seatZ = Mathf.Lerp(s.RoofFront, s.CabinFront, 0.2f) - 0.55f;
            float cabH = H * st.CabRoof;
            // Seated eye roughly 55% of the way from beltline to roof (~30 cm under a sedan roof).
            s.Eye = new Vector3(-m.Width * 0.2f, s.Belt + (cabH - s.Belt) * (st.Bus ? 0.3f : 0.55f), seatZ);
            // Rear-view mirror hangs from the top of the windscreen, above the eye line.
            // Hangs below the roof cap (its underside is 0.16 m down on the Blender-built bodies), so the
            // whole glass shows through the windscreen instead of being cut off by the headliner.
            s.RearViewMirror = new Vector3(0f, cabH - 0.22f, Mathf.Lerp(s.RoofFront, s.CabinFront, 0.12f) - 0.05f);
            // Door-mounted wing mirrors, behind the A-pillar base so the driver sees them through the side glass.
            s.LeftMirror = new Vector3(-m.Width * 0.5f - 0.16f, s.Belt + 0.06f, s.CabinFront - 0.32f);
            s.RightMirror = new Vector3(m.Width * 0.5f + 0.16f, s.Belt + 0.06f, s.CabinFront - 0.32f);
            // Big plates sit on the bumper, as high as the flat end face allows; on low noses/tails
            // they hang a little lower (a bracket in the body mesh fills any gap behind them).
            // Nose/tail face tops: low cars keep at least 0.34 m of flat face (matches the Blender builder).
            float noseTop = Mathf.Max(s.Belt - L * st.Hood * 1.5f, s.Clearance + 0.34f);
            float tailTop = Mathf.Max(s.Belt - L * st.Trunk * 1.5f, s.Clearance + 0.34f);
            s.FrontPlate = new Vector3(0f, PlateY(s.Clearance, noseTop, 0.26f), L * 0.5f + 0.07f);
            s.RearPlate = new Vector3(0f, PlateY(s.Clearance, tailTop, 0.34f), -L * 0.5f - 0.07f);
            ShapeCache[modelId] = s;
            return s;
        }

        static float PlateY(float clearance, float faceTop, float preferred)
        {
            float half = Glyphs.PlateHeight * 0.5f;
            float y = Mathf.Min(clearance + preferred, faceTop - 0.02f - half);
            return Mathf.Max(y, clearance - 0.06f + half);
        }

        public static Color BodyColor(int colorId)
        {
            var c = VehicleCatalog.Colors[colorId];
            return new Color(c.R, c.G, c.B, 1f);
        }

        /// <summary>Body + glass mesh for a model in a colour (cached).</summary>
        public static Mesh Body(int modelId, int colorId)
        {
            int key = modelId * 64 + colorId;
            if (BodyCache.TryGetValue(key, out var mesh)) return mesh;
            mesh = BuildBody(modelId, BodyColor(colorId), interior: false);
            BodyCache[key] = mesh;
            return mesh;
        }

        /// <summary>Body with cockpit interior panels, for the local player's car.</summary>
        public static Mesh PlayerBody(int modelId, int colorId) => BuildBody(modelId, BodyColor(colorId), interior: true);

        /// <summary>Resource name of a model's Blender-built mesh (tools/blender/build_assets.py).</summary>
        public static string ArtName(int modelId) => (VehicleCatalog.Models[modelId].Make + VehicleCatalog.Models[modelId].Name).Replace(" ", "");

        static readonly Color PaintMarker = new Color(1f, 0f, 1f, 1f);

        /// <summary>
        /// The Blender-built body (if present): a copy with the paint marker swapped for the car's colour,
        /// plus the cockpit interior for the player's own car. Null → fall back to the generated body.
        /// </summary>
        static Mesh ArtBody(int modelId, Color paint, bool interior)
        {
            var src = Resources.Load<Mesh>("Art/Vehicles/" + ArtName(modelId) + (interior ? "_Player" : ""));
            if (src == null || !src.isReadable) return null;
            var verts = new List<Vector3>(src.vertices);
            var normals = new List<Vector3>(src.normals);
            var colors = new List<Color>(src.colors);
            for (int i = 0; i < colors.Count; i++)
            {
                var c = colors[i];
                if (c.r > 0.95f && c.g < 0.05f && c.b > 0.95f) colors[i] = new Color(paint.r, paint.g, paint.b, 1f);
            }
            var body = new List<int>(src.GetTriangles(0));
            var glass = src.subMeshCount > 1 ? new List<int>(src.GetTriangles(1)) : new List<int>();
            if (interior)
            {
                var mb = new MeshBuilder();
                AddInterior(mb, Shape(modelId), Styles[VehicleCatalog.Models[modelId].Style]);
                var extra = mb.Build("Interior");
                int offset = verts.Count;
                verts.AddRange(extra.vertices);
                normals.AddRange(extra.normals);
                colors.AddRange(extra.colors);
                foreach (int t in extra.GetTriangles(0)) body.Add(t + offset);
                Object.Destroy(extra);
            }
            var mesh = new Mesh { name = "Car_" + ArtName(modelId), indexFormat = UnityEngine.Rendering.IndexFormat.UInt32 };
            mesh.SetVertices(verts);
            mesh.SetNormals(normals);
            mesh.SetColors(colors);
            mesh.subMeshCount = 2;
            mesh.SetTriangles(body, 0);
            mesh.SetTriangles(glass, 1);
            mesh.RecalculateBounds();
            return mesh;
        }

        static Mesh BuildBody(int modelId, Color paint, bool interior)
        {
            var art = ArtBody(modelId, paint, interior);
            if (art != null) return art;
            var m = VehicleCatalog.Models[modelId];
            var st = Styles[m.Style];
            var s = Shape(modelId);
            var mb = new MeshBuilder();
            float L = s.Length, hw = s.Width * 0.5f, zr = -L * 0.5f, zf = L * 0.5f;
            float belt = s.Belt, clr = s.Clearance, H = s.Height;
            float hood = L * st.Hood, trunk = L * st.Trunk;

            // Lower body: convex hexagon side profile with rounded corners (no hard pixel-y edges).
            mb.ProfileExtrude(MeshBuilder.Chamfer(new[]
            {
                new Vector2(zr, clr), new Vector2(zf, clr), new Vector2(zf, belt - hood * 1.5f),
                new Vector2(zf - hood * 4f, belt), new Vector2(zr + trunk * 4f, belt), new Vector2(zr, belt - trunk * 1.5f),
            }, 0.12f, 3), hw, paint);

            // Bumpers.
            mb.LocalBox(new Vector3(-hw - 0.02f, clr - 0.02f, zf - 0.12f), new Vector3(hw + 0.02f, clr + 0.18f, zf + 0.06f), Trim);
            mb.LocalBox(new Vector3(-hw - 0.02f, clr - 0.02f, zr - 0.06f), new Vector3(hw + 0.02f, clr + 0.18f, zr + 0.12f), Trim);

            // Cabin: glass trapezoid prism with an opaque roof and pillars.
            float cw = s.CabinHalfWidth, roofY = H * st.CabRoof;
            var cab = new[]
            {
                new Vector2(s.CabinRear, belt), new Vector2(s.CabinFront, belt),
                new Vector2(s.RoofFront, roofY - 0.06f), new Vector2(s.RoofRear, roofY - 0.06f),
            };
            if (st.Bus)
            {
                // Bus: one long glass band, a thick roof, window posts every ~1.3 m, a door on the kerb side.
                mb.Submesh = 1;
                mb.ProfileExtrude(cab, cw - 0.01f, Glass);
                mb.Submesh = 0;
                mb.LocalBox(new Vector3(-cw, roofY - 0.18f, s.RoofRear), new Vector3(cw, roofY + 0.05f, s.RoofFront), paint);
                int posts = Mathf.RoundToInt((s.CabinFront - s.CabinRear) / 1.3f);
                for (int i = 1; i < posts; i++)
                {
                    float z = Mathf.Lerp(s.CabinRear, s.CabinFront, i / (float)posts);
                    foreach (float x in new[] { -cw, cw }) mb.LocalBox(new Vector3(x - 0.04f, belt, z - 0.06f), new Vector3(x + 0.04f, roofY - 0.1f, z + 0.06f), paint);
                }
                var door = new Color(0.2f, 0.22f, 0.25f);
                mb.LocalBox(new Vector3(hw - 0.02f, clr + 0.15f, zf - 2.2f), new Vector3(hw + 0.02f, roofY - 0.2f, zf - 1.1f), door);
                mb.LocalBox(new Vector3(hw - 0.02f, clr + 0.15f, -0.4f), new Vector3(hw + 0.02f, roofY - 0.2f, 0.7f), door);
                // Destination board above the windscreen, a stripe along the sides.
                mb.LocalBox(new Vector3(-cw * 0.7f, roofY - 0.42f, zf - 0.02f), new Vector3(cw * 0.7f, roofY - 0.2f, zf + 0.03f), new Color(0.1f, 0.1f, 0.1f));
                mb.LocalBox(new Vector3(-hw - 0.01f, belt - 0.35f, zr + 0.3f), new Vector3(hw + 0.01f, belt - 0.2f, zf - 0.3f), new Color(0.2f, 0.45f, 0.75f));
            }
            else if (st.BoxBody)
            {
                // Lorry: cab up front, a tall off-white box behind with a roller door.
                mb.Submesh = 1;
                mb.ProfileExtrude(MeshBuilder.Chamfer(cab, 0.05f, 2), cw, Glass);
                mb.Submesh = 0;
                mb.LocalBox(new Vector3(-cw - 0.02f, roofY - 0.08f, s.RoofRear - 0.02f), new Vector3(cw + 0.02f, roofY, s.RoofFront + 0.02f), paint);
                var box = new Color(0.9f, 0.9f, 0.86f);
                float boxFront = s.CabinRear - 0.12f;
                mb.LocalBox(new Vector3(-hw, clr + 0.35f, zr), new Vector3(hw, H, boxFront), box);
                mb.LocalBox(new Vector3(-hw * 0.85f, clr + 0.45f, zr - 0.03f), new Vector3(hw * 0.85f, H - 0.12f, zr), new Color(0.72f, 0.72f, 0.7f));
                foreach (float x in new[] { -hw - 0.01f, hw + 0.01f })
                    mb.LocalBox(new Vector3(x - 0.01f, H - 0.9f, zr + 0.5f), new Vector3(x + 0.01f, H - 0.35f, boxFront - 0.5f), paint);
            }
            else if (st.CargoBox)
            {
                // Van: opaque cargo box, glass only for the front cab.
                float split = Mathf.Lerp(s.CabinRear, s.RoofFront, 0.72f);
                mb.ProfileExtrude(new[] { new Vector2(s.CabinRear, belt), new Vector2(split, belt), new Vector2(split, roofY), new Vector2(s.RoofRear, roofY) }, cw, paint);
                mb.Submesh = 1;
                mb.ProfileExtrude(new[] { new Vector2(split, belt), new Vector2(s.CabinFront, belt), new Vector2(s.RoofFront, roofY - 0.06f), new Vector2(split, roofY - 0.06f) }, cw - 0.01f, Glass);
                mb.Submesh = 0;
                mb.LocalBox(new Vector3(-cw, roofY - 0.08f, split), new Vector3(cw, roofY, s.RoofFront + 0.02f), paint);
            }
            else
            {
                mb.Submesh = 1;
                mb.ProfileExtrude(MeshBuilder.Chamfer(cab, 0.06f, 2), cw, Glass);
                mb.Submesh = 0;
                mb.LocalBox(new Vector3(-cw - 0.02f, roofY - 0.08f, s.RoofRear - 0.02f), new Vector3(cw + 0.02f, roofY, s.RoofFront + 0.02f), paint);
            }
            // Pillars (A and C, plus B mid-cabin).
            float pt = 0.07f;
            foreach (float x in new[] { -cw, cw })
            {
                mb.Beam(new Vector3(x, belt, s.CabinFront), new Vector3(x, roofY - 0.05f, s.RoofFront), pt, paint);
                mb.Beam(new Vector3(x, belt, s.CabinRear), new Vector3(x, roofY - 0.05f, s.RoofRear), pt * 1.6f, paint);
                float bz = Mathf.Lerp(s.CabinRear, s.CabinFront, 0.5f);
                mb.Beam(new Vector3(x, belt, bz), new Vector3(x, roofY - 0.05f, Mathf.Lerp(s.RoofRear, s.RoofFront, 0.5f)), pt, paint);
            }

            if (st.RoofRails)
                foreach (float x in new[] { -cw * 0.8f, cw * 0.8f })
                    mb.LocalBox(new Vector3(x - 0.03f, roofY, s.RoofRear + 0.1f), new Vector3(x + 0.03f, roofY + 0.07f, s.RoofFront - 0.1f), Trim);

            if (st.PickupBed)
            {
                // Open bed behind the cab: dark floor, body-coloured walls.
                float bedFloor = belt - 0.35f;
                mb.LocalBox(new Vector3(-hw + 0.08f, bedFloor, zr + 0.08f), new Vector3(hw - 0.08f, bedFloor + 0.02f, s.CabinRear), BedColor);
                mb.LocalBox(new Vector3(-hw, belt, zr), new Vector3(-hw + 0.08f, belt + 0.12f, s.CabinRear), paint);
                mb.LocalBox(new Vector3(hw - 0.08f, belt, zr), new Vector3(hw, belt + 0.12f, s.CabinRear), paint);
                mb.LocalBox(new Vector3(-hw, belt, zr), new Vector3(hw, belt + 0.12f, zr + 0.08f), paint);
            }

            // Wheels.
            foreach (float z in new[] { -s.WheelBase * 0.5f, s.WheelBase * 0.5f })
            foreach (float x in new[] { -hw + 0.12f, hw - 0.12f })
            {
                mb.SmoothWheel(new Vector3(x, s.WheelRadius, z), s.WheelRadius, 0.26f, Tyre, Hub, 22);
            }

            // Lights (vertex alpha tags drive the vehicle shader).
            float ly = belt - hood * 1.5f - 0.12f;
            var head = new Color(1f, 0.97f, 0.8f, MeshBuilder.TagHeadlight);
            var tail = new Color(0.8f, 0.08f, 0.08f, MeshBuilder.TagBrake);
            var indL = new Color(1f, 0.55f, 0.05f, MeshBuilder.TagLeft);
            var indR = new Color(1f, 0.55f, 0.05f, MeshBuilder.TagRight);
            // Lamps sit outboard of the (wide) plates.
            foreach (float x in new[] { -hw * 0.78f, hw * 0.78f })
            {
                mb.Card(new Vector3(x, ly, zf + 0.012f), Vector3.forward, Vector3.up, hw * 0.34f, 0.14f, head);
                mb.Card(new Vector3(x, belt - trunk * 1.5f - 0.14f, zr - 0.012f), Vector3.back, Vector3.up, hw * 0.3f, 0.15f, tail);
            }
            // Plate brackets: dark backing so an oversized plate reads as mounted, not floating.
            foreach (var pp in new[] { s.FrontPlate, s.RearPlate })
            {
                float pw = Glyphs.PlateWidth * 0.5f + 0.025f, ph = Glyphs.PlateHeight * 0.5f + 0.025f;
                float zOut = pp.z > 0f ? pp.z - 0.006f : pp.z + 0.006f, zIn = pp.z > 0f ? zf - 0.15f : zr + 0.15f;
                mb.LocalBox(new Vector3(-pw, pp.y - ph, Mathf.Min(zOut, zIn)), new Vector3(pw, pp.y + ph, Mathf.Max(zOut, zIn)), Trim);
            }
            mb.Card(new Vector3(-hw * 0.95f, ly, zf + 0.013f), Vector3.forward, Vector3.up, 0.1f, 0.1f, indL);
            mb.Card(new Vector3(hw * 0.95f, ly, zf + 0.013f), Vector3.forward, Vector3.up, 0.1f, 0.1f, indR);
            mb.Card(new Vector3(-hw * 0.95f, belt - 0.3f, zr - 0.013f), Vector3.back, Vector3.up, 0.1f, 0.1f, indL);
            mb.Card(new Vector3(hw * 0.95f, belt - 0.3f, zr - 0.013f), Vector3.back, Vector3.up, 0.1f, 0.1f, indR);

            // Wing mirror housings on NPCs; the player car gets oriented housings from MirrorSystem.
            if (!interior)
                foreach (var mp in new[] { s.LeftMirror, s.RightMirror })
                    mb.LocalBox(mp + new Vector3(-0.1f, -0.07f, -0.02f), mp + new Vector3(0.1f, 0.07f, 0.08f), Trim);

            if (interior) AddInterior(mb, s, st);

            var mesh = mb.Build($"Car_{m.Make}{m.Name}");
            return mesh;
        }

        /// <summary>Cockpit panels the first-person camera sees (dashboard, doors, headliner, seats, wheel).</summary>
        static void AddInterior(MeshBuilder mb, CarShape s, Style st)
        {
            float L = s.Length, hw = s.Width * 0.5f, zr = -L * 0.5f, zf = L * 0.5f;
            float belt = s.Belt, clr = s.Clearance, cw = s.CabinHalfWidth, roofY = s.CabRoofHeight;

                // Dashboard, door cards, headliner, headrests, steering wheel: what the cockpit camera sees.
                // Sculpted dash: padded roll facing the driver, a top pad sloping down to the windscreen,
                // and a lighter lower (knee) panel.
                {
                    float dz = s.CabinFront - 0.55f, cf = s.CabinFront + 0.05f;
                    mb.ProfileExtrude(MeshBuilder.Chamfer(new[]
                    {
                        new Vector2(dz, belt - 0.12f), new Vector2(cf, belt - 0.12f), new Vector2(cf, belt - 0.02f),
                        new Vector2(dz + 0.2f, belt + 0.07f), new Vector2(dz + 0.02f, belt + 0.07f),
                        new Vector2(dz - 0.04f, belt + 0.02f), new Vector2(dz - 0.04f, belt - 0.08f),
                    }, 0.025f, 2), cw - 0.02f, DashTop);
                    mb.LocalBox(new Vector3(-cw + 0.02f, belt - 0.3f, dz + 0.02f), new Vector3(cw - 0.02f, belt - 0.1f, cf), Interior);
                    mb.LocalBox(new Vector3(-cw + 0.03f, belt - 0.115f, dz - 0.035f), new Vector3(cw - 0.03f, belt - 0.095f, dz + 0.03f), new Color(0.55f, 0.53f, 0.5f)); // trim strip
                }
                mb.Polygon(new[] { new Vector3(-hw + 0.06f, clr + 0.1f, zr + 0.2f), new Vector3(-hw + 0.06f, clr + 0.1f, zf - 0.2f), new Vector3(-hw + 0.06f, belt, zf - 0.2f), new Vector3(-hw + 0.06f, belt, zr + 0.2f) }, Interior, Vector3.right);
                mb.Polygon(new[] { new Vector3(hw - 0.06f, clr + 0.1f, zr + 0.2f), new Vector3(hw - 0.06f, clr + 0.1f, zf - 0.2f), new Vector3(hw - 0.06f, belt, zf - 0.2f), new Vector3(hw - 0.06f, belt, zr + 0.2f) }, Interior, Vector3.left);
                mb.Polygon(new[] { new Vector3(-cw, roofY - 0.1f, s.RoofRear), new Vector3(cw, roofY - 0.1f, s.RoofRear), new Vector3(cw, roofY - 0.1f, s.RoofFront), new Vector3(-cw, roofY - 0.1f, s.RoofFront) }, new Color(0.78f, 0.76f, 0.72f), Vector3.down);
                mb.Polygon(new[] { new Vector3(-cw, clr + 0.12f, zr + 0.2f), new Vector3(cw, clr + 0.12f, zr + 0.2f), new Vector3(cw, clr + 0.12f, zf - 0.3f), new Vector3(-cw, clr + 0.12f, zf - 0.3f) }, Interior, Vector3.up);
                float seatZ = s.Eye.z - 0.3f;
                float dashZ0 = s.CabinFront - 0.55f, cabFront = s.CabinFront + 0.05f;
                float floor = clr + 0.13f;
                float cushion = Mathf.Max(floor + 0.2f, belt - 0.4f); // seat cushion top
                // Carpeted floor, transmission tunnel, and the footwell/toe board under the dash.
                mb.LocalBox(new Vector3(-cw + 0.02f, clr + 0.1f, s.CabinRear + 0.05f), new Vector3(cw - 0.02f, floor, cabFront), Carpet);
                mb.LocalBox(new Vector3(-0.13f, floor, seatZ - 0.6f), new Vector3(0.13f, floor + 0.13f, dashZ0 + 0.2f), Carpet);
                mb.ProfileExtrude(new[]
                {
                    new Vector2(dashZ0 + 0.3f, floor), new Vector2(cabFront, floor), new Vector2(cabFront, belt - 0.3f), new Vector2(dashZ0 + 0.03f, belt - 0.3f),
                }, cw - 0.03f, Carpet);
                // Pedals on the driver's side (brake and throttle), and the handbrake by the console.
                foreach (var (dx, w, h) in new[] { (-0.1f, 0.09f, 0.08f), (0.09f, 0.06f, 0.14f) })
                {
                    var pad = new Vector3(s.Eye.x + dx, floor + 0.12f, dashZ0 + 0.12f);
                    mb.Beam(pad + new Vector3(0f, 0.02f, 0.02f), pad + new Vector3(0f, 0.2f, 0.14f), 0.018f, Trim);
                    mb.LocalBox(pad - new Vector3(w * 0.5f, h * 0.5f, 0.015f), pad + new Vector3(w * 0.5f, h * 0.5f, 0.015f), new Color(0.3f, 0.3f, 0.32f));
                }
                var brake = new Vector3(s.Eye.x * 0.45f, floor + 0.14f, seatZ + 0.35f);
                mb.Beam(brake, brake + new Vector3(0f, 0.08f, 0.2f), 0.03f, Trim);
                // Front seats: pedestal, cushion with bolsters, full-height back; passenger headrest.
                foreach (float x in new[] { s.Eye.x, -s.Eye.x })
                {
                    mb.LocalBox(new Vector3(x - 0.18f, floor, seatZ + 0.05f), new Vector3(x + 0.18f, cushion - 0.1f, seatZ + 0.42f), Trim);
                    mb.LocalBox(new Vector3(x - 0.25f, cushion - 0.12f, seatZ), new Vector3(x + 0.25f, cushion, seatZ + 0.52f), Seat);
                    foreach (float side in new[] { -1f, 1f })
                        mb.LocalBox(new Vector3(x + side * 0.25f - 0.045f, cushion - 0.02f, seatZ + 0.05f), new Vector3(x + side * 0.25f + 0.045f, cushion + 0.05f, seatZ + 0.48f), Seat);
                    mb.LocalBox(new Vector3(x - 0.24f, cushion - 0.05f, seatZ - 0.14f), new Vector3(x + 0.24f, belt + 0.15f, seatZ), Seat);
                    foreach (float side in new[] { -1f, 1f })
                        mb.LocalBox(new Vector3(x + side * 0.24f - 0.04f, cushion + 0.05f, seatZ - 0.12f), new Vector3(x + side * 0.24f + 0.04f, belt + 0.05f, seatZ + 0.05f), Seat);
                    mb.LocalBox(new Vector3(x - 0.2f, cushion + 0.02f, seatZ - 0.005f), new Vector3(x + 0.2f, belt + 0.12f, seatZ + 0.002f), SeatInsert);
                }
                mb.LocalBox(new Vector3(-s.Eye.x - 0.12f, belt + 0.2f, seatZ - 0.12f), new Vector3(-s.Eye.x + 0.12f, belt + 0.38f, seatZ - 0.02f), Seat);
                mb.Beam(new Vector3(-s.Eye.x - 0.06f, belt + 0.14f, seatZ - 0.07f), new Vector3(-s.Eye.x - 0.06f, belt + 0.21f, seatZ - 0.07f), 0.015f, Trim);
                mb.Beam(new Vector3(-s.Eye.x + 0.06f, belt + 0.14f, seatZ - 0.07f), new Vector3(-s.Eye.x + 0.06f, belt + 0.21f, seatZ - 0.07f), 0.015f, Trim);
                // Door cards: armrest, door pocket and pull handle on each side.
                foreach (float side in new[] { -1f, 1f })
                {
                    float xo = side * (hw - 0.06f), xi = side * (hw - 0.15f);
                    float a0 = Mathf.Min(xo, xi), a1 = Mathf.Max(xo, xi);
                    mb.LocalBox(new Vector3(a0, belt - 0.24f, seatZ - 0.1f), new Vector3(a1, belt - 0.17f, seatZ + 0.55f), Seat);
                    mb.LocalBox(new Vector3(Mathf.Min(xo, side * (hw - 0.11f)), floor + 0.08f, seatZ + 0.45f), new Vector3(Mathf.Max(xo, side * (hw - 0.11f)), floor + 0.26f, dashZ0 - 0.05f), Trim);
                    mb.LocalBox(new Vector3(Mathf.Min(xo, side * (hw - 0.09f)), belt - 0.1f, seatZ + 0.5f), new Vector3(Mathf.Max(xo, side * (hw - 0.09f)), belt - 0.05f, seatZ + 0.62f), new Color(0.6f, 0.6f, 0.62f));
                    mb.LocalBox(new Vector3(a0, belt - 0.02f, s.CabinRear + 0.1f), new Vector3(a1, belt + 0.01f, cabFront - 0.1f), SeatInsert); // window-line trim
                }
                float rearZ = Mathf.Lerp(s.CabinRear, seatZ, 0.4f);
                // Closes the tub behind the rear seats (the body shell is open over the cabin).
                mb.LocalBox(new Vector3(-cw, clr + 0.1f, s.CabinRear + 0.02f), new Vector3(cw, belt + 0.02f, s.CabinRear + 0.07f), Interior);
                if (!st.CargoBox && !st.PickupBed && !st.BoxBody && !st.Bus)
                {
                    // Rear bench: cushion and back; headrests are what you look past in the rear-view mirror.
                    mb.LocalBox(new Vector3(-cw + 0.06f, cushion - 0.14f, rearZ), new Vector3(cw - 0.06f, cushion - 0.02f, Mathf.Min(rearZ + 0.5f, seatZ - 0.35f)), Seat);
                    mb.LocalBox(new Vector3(-cw + 0.06f, floor, rearZ + 0.05f), new Vector3(cw - 0.06f, cushion - 0.14f, Mathf.Min(rearZ + 0.45f, seatZ - 0.4f)), Carpet);
                    mb.LocalBox(new Vector3(-cw + 0.06f, cushion - 0.1f, rearZ - 0.14f), new Vector3(cw - 0.06f, belt + 0.08f, rearZ), Seat);
                    foreach (float x in new[] { -cw * 0.55f, 0f, cw * 0.55f })
                        mb.LocalBox(new Vector3(x - 0.1f, belt + 0.1f, rearZ - 0.12f), new Vector3(x + 0.1f, belt + 0.22f, rearZ - 0.02f), Seat);
                }
                else if (st.CargoBox)
                {
                    // Van: bulkhead behind the seats with a small window, cargo floor beyond.
                    float bz = seatZ - 0.25f;
                    mb.LocalBox(new Vector3(-cw + 0.02f, floor, bz - 0.04f), new Vector3(cw - 0.02f, belt + 0.1f, bz), Trim);
                    mb.LocalBox(new Vector3(-cw + 0.02f, belt + 0.45f, bz - 0.04f), new Vector3(cw - 0.02f, roofY - 0.12f, bz), Trim);
                    foreach (float x in new[] { -cw + 0.02f, cw - 0.28f })
                        mb.LocalBox(new Vector3(x, belt + 0.1f, bz - 0.04f), new Vector3(x + 0.26f, belt + 0.45f, bz), Trim);
                }
                // (The steering wheel, hands and gauges are live: CockpitRig.)
                // Centre console with a gear stick, and dash detail: vents, radio, glovebox seam.
                float dashZ = s.CabinFront - 0.55f;
                mb.LocalBox(new Vector3(-0.11f, clr + 0.12f, seatZ + 0.1f), new Vector3(0.11f, belt - 0.22f, dashZ), Interior);
                mb.LocalBox(new Vector3(-0.1f, cushion + 0.05f, seatZ - 0.05f), new Vector3(0.1f, cushion + 0.12f, seatZ + 0.35f), Seat); // centre armrest
                mb.LocalBox(new Vector3(-0.1f, belt - 0.24f, dashZ - 0.06f), new Vector3(0.1f, belt - 0.02f, dashZ + 0.01f), new Color(0.2f, 0.2f, 0.22f));
                mb.LocalBox(new Vector3(-0.075f, belt - 0.1f, dashZ - 0.075f), new Vector3(0.075f, belt - 0.05f, dashZ - 0.055f), new Color(0.3f, 0.75f, 0.85f));
                var stick = new Vector3(0f, belt - 0.22f, dashZ - 0.28f);
                mb.Beam(stick, stick + new Vector3(0f, 0.16f, -0.03f), 0.022f, Trim);
                mb.SmoothSphere(stick + new Vector3(0f, 0.18f, -0.035f), new Vector3(0.035f, 0.035f, 0.035f), new Color(0.15f, 0.15f, 0.17f), 10, 7);
                foreach (float x in new[] { -cw + 0.12f, -0.2f, 0.2f, cw - 0.12f })
                    mb.LocalBox(new Vector3(x - 0.06f, belt - 0.06f, dashZ - 0.012f), new Vector3(x + 0.06f, belt - 0.01f, dashZ + 0.01f), new Color(0.12f, 0.12f, 0.13f));
                float glove = -s.Eye.x;
                mb.LocalBox(new Vector3(glove - 0.2f, belt - 0.2f, dashZ - 0.008f), new Vector3(glove + 0.2f, belt - 0.19f, dashZ + 0.005f), new Color(0.2f, 0.2f, 0.22f));
        }

        static readonly Color DashTop = new Color(0.16f, 0.16f, 0.18f);
        static readonly Color Seat = new Color(0.3f, 0.3f, 0.34f), SeatInsert = new Color(0.4f, 0.38f, 0.36f), Carpet = new Color(0.14f, 0.14f, 0.16f);

        /// <summary>Skin tones for the driver (VehicleIdentity.Skin).</summary>
        public static readonly Color[] SkinTones =
        {
            new Color(0.98f, 0.84f, 0.70f), new Color(0.94f, 0.75f, 0.58f), new Color(0.80f, 0.58f, 0.42f),
            new Color(0.58f, 0.40f, 0.27f), new Color(0.38f, 0.26f, 0.18f), new Color(0.98f, 0.86f, 0.35f),
        };

        static readonly Dictionary<int, Mesh> ArtDriverCache = new Dictionary<int, Mesh>();

        /// <summary>
        /// PEAK-style driver: the Blender base character plus this identity's eyes, mouth, hat and
        /// glasses, with marker colours swapped for its skin, shirt and hat colours. Null if the art
        /// kit isn't in the build (falls back to the procedural bean).
        /// </summary>
        static Mesh ArtDriver(VehicleIdentity id)
        {
            if (ArtDriverCache.TryGetValue(id.DriverKey, out var cached)) return cached;
            var baseMesh = Resources.Load<Mesh>("Art/Characters/DriverBase");
            if (baseMesh == null || !baseMesh.isReadable) return null;
            var parts = new List<Mesh> { baseMesh };
            void Add(string name) { var m = Resources.Load<Mesh>("Art/Characters/" + name); if (m != null && m.isReadable) parts.Add(m); }
            Add("Eyes" + id.Eyes % VehicleCatalog.EyeStyles);
            Add("Mouth" + id.Mouth % VehicleCatalog.MouthStyles);
            if (id.Hat > 0) Add("Hat" + id.Hat);
            if (id.Glasses > 0) Add("Glasses" + id.Glasses);

            var skin = SkinTones[id.Skin % SkinTones.Length];
            var shirt = BodyColor(id.ShirtColorId % VehicleCatalog.Colors.Length);
            var hat = BodyColor(id.HatColorId % VehicleCatalog.Colors.Length);
            var accent = Color.Lerp(hat, Color.black, 0.35f);
            var verts = new List<Vector3>(); var normals = new List<Vector3>(); var colors = new List<Color>(); var tris = new List<int>();
            foreach (var m in parts)
            {
                int offset = verts.Count;
                verts.AddRange(m.vertices);
                normals.AddRange(m.normals);
                foreach (var c in m.colors)
                {
                    // Marker colours from the Blender kit → this driver's colours.
                    bool r = c.r > 0.95f, g = c.g > 0.95f, b = c.b > 0.95f, r0 = c.r < 0.05f, g0 = c.g < 0.05f, b0 = c.b < 0.05f;
                    Color o = c;
                    if (r0 && g && b0) o = skin;
                    else if (r0 && g && b) o = shirt;
                    else if (r && g && b0) o = hat;
                    else if (r0 && g0 && b) o = accent;
                    o.a = 1f;
                    colors.Add(o);
                }
                for (int sm = 0; sm < m.subMeshCount; sm++)
                    foreach (int t in m.GetTriangles(sm)) tris.Add(t + offset);
            }
            var mesh = new Mesh { name = "Driver", indexFormat = UnityEngine.Rendering.IndexFormat.UInt32 };
            mesh.SetVertices(verts);
            mesh.SetNormals(normals);
            mesh.SetColors(colors);
            mesh.SetTriangles(tris, 0);
            mesh.RecalculateBounds();
            ArtDriverCache[id.DriverKey] = mesh;
            return mesh;
        }

        /// <summary>The driver mesh for an identity (Blender kit when present, else the procedural bean).</summary>
        public static Mesh Driver(VehicleIdentity id)
        {
            var art = ArtDriver(id);
            if (art != null) return art;
            int key = id.Hat * 4096 + id.HatColorId * 64 + (id.Plate != null ? id.Plate[6] % 8 : 0);
            if (DriverCache.TryGetValue(key, out var mesh)) return mesh;
            var mb = new MeshBuilder();
            var skinTones = new[] { new Color(0.96f, 0.8f, 0.66f), new Color(0.82f, 0.62f, 0.45f), new Color(0.55f, 0.38f, 0.26f), new Color(0.98f, 0.86f, 0.3f) };
            var shirt = new[] { new Color(0.3f, 0.5f, 0.85f), new Color(0.85f, 0.35f, 0.3f), new Color(0.35f, 0.7f, 0.4f), new Color(0.9f, 0.8f, 0.3f) };
            int variant = key % 8;
            var skin = skinTones[variant % skinTones.Length];
            // Smooth bean: rounded torso, round head, big friendly eyes with highlights, a little smile.
            var top = shirt[variant / 2 % shirt.Length];
            mb.SmoothCylinder(new Vector3(0, -0.05f, 0), 0.22f, 0.2f, 0.36f, top, 22, cap: false);
            mb.SmoothSphere(new Vector3(0, 0.31f, 0), new Vector3(0.2f, 0.09f, 0.2f), top, 22, 8);
            mb.SmoothSphere(new Vector3(0, 0.52f, 0), new Vector3(0.17f, 0.175f, 0.17f), skin, 22, 14);
            foreach (float ex in new[] { -0.06f, 0.06f })
            {
                mb.SmoothSphere(new Vector3(ex, 0.56f, 0.145f), new Vector3(0.036f, 0.044f, 0.02f), new Color(0.97f, 0.97f, 0.97f), 12, 8);
                mb.SmoothSphere(new Vector3(ex, 0.555f, 0.162f), new Vector3(0.02f, 0.026f, 0.01f), new Color(0.08f, 0.08f, 0.1f), 10, 6);
            }
            for (int k = 0; k < 5; k++)
            {
                float a = Mathf.Lerp(-0.6f, 0.6f, k / 4f);
                mb.SmoothSphere(new Vector3(Mathf.Sin(a) * 0.06f, 0.475f - Mathf.Cos(a) * 0.015f, 0.158f), new Vector3(0.011f, 0.011f, 0.008f), new Color(0.35f, 0.15f, 0.12f), 6, 4);
            }
            var hc = BodyColor(id.HatColorId);
            var hatTop = new Vector3(0, 0.62f, 0);
            switch ((HatType)id.Hat)
            {
                case HatType.Cap:
                    mb.SmoothCylinder(hatTop, 0.172f, 0.15f, 0.08f, hc, 20);
                    mb.LocalBox(hatTop + new Vector3(-0.1f, 0f, 0.1f), hatTop + new Vector3(0.1f, 0.02f, 0.25f), hc);
                    break;
                case HatType.Beanie:
                    mb.SmoothSphere(hatTop + new Vector3(0, -0.03f, 0), new Vector3(0.178f, 0.15f, 0.178f), hc, 20, 10);
                    mb.SmoothSphere(hatTop + new Vector3(0, 0.14f, 0), new Vector3(0.05f, 0.05f, 0.05f), Color.white, 10, 6);
                    break;
                case HatType.TopHat:
                    mb.SmoothCylinder(hatTop, 0.24f, 0.24f, 0.02f, Color.black, 22);
                    mb.SmoothCylinder(hatTop, 0.13f, 0.13f, 0.3f, Color.black, 22);
                    break;
                case HatType.Cowboy:
                    mb.SmoothCylinder(hatTop, 0.3f, 0.3f, 0.02f, hc, 22);
                    mb.SmoothCylinder(hatTop, 0.14f, 0.11f, 0.16f, hc, 22);
                    break;
                case HatType.Party:
                    mb.SmoothCylinder(hatTop - new Vector3(0, 0.02f, 0), 0.12f, 0.005f, 0.3f, hc, 18, cap: false);
                    break;
                case HatType.Crown:
                    mb.SmoothCylinder(hatTop, 0.15f, 0.17f, 0.12f, new Color(1f, 0.82f, 0.2f), 16);
                    break;
            }
            mesh = mb.Build("Driver");
            DriverCache[key] = mesh;
            return mesh;
        }
    }
}
