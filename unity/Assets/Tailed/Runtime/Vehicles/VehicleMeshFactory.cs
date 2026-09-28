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
            s.RoofRear = s.CabinRear + L * st.RoofRearInset;
            s.RoofFront = s.CabinFront - L * st.RoofFrontInset;
            float seatZ = Mathf.Lerp(s.RoofFront, s.CabinFront, 0.2f) - 0.55f;
            float cabH = H * st.CabRoof;
            // Seated eye roughly 55% of the way from beltline to roof (~30 cm under a sedan roof).
            s.Eye = new Vector3(-m.Width * 0.2f, s.Belt + (cabH - s.Belt) * (st.Bus ? 0.3f : 0.55f), seatZ);
            // Rear-view mirror hangs from the top of the windscreen, above the eye line.
            s.RearViewMirror = new Vector3(0f, cabH - 0.13f, Mathf.Lerp(s.RoofFront, s.CabinFront, 0.12f) - 0.05f);
            // Door-mounted wing mirrors, behind the A-pillar base so the driver sees them through the side glass.
            s.LeftMirror = new Vector3(-m.Width * 0.5f - 0.16f, s.Belt + 0.06f, s.CabinFront - 0.32f);
            s.RightMirror = new Vector3(m.Width * 0.5f + 0.16f, s.Belt + 0.06f, s.CabinFront - 0.32f);
            // Big plates sit on the bumper, as high as the flat end face allows; on low noses/tails
            // they hang a little lower (a bracket in the body mesh fills any gap behind them).
            s.FrontPlate = new Vector3(0f, PlateY(s.Clearance, s.Belt - L * st.Hood * 1.5f, 0.26f), L * 0.5f + 0.07f);
            s.RearPlate = new Vector3(0f, PlateY(s.Clearance, s.Belt - L * st.Trunk * 1.5f, 0.34f), -L * 0.5f - 0.07f);
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

        static Mesh BuildBody(int modelId, Color paint, bool interior)
        {
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

            if (interior)
            {
                // Dashboard, door cards, headliner, headrests, steering wheel: what the cockpit camera sees.
                mb.LocalBox(new Vector3(-cw + 0.02f, belt - 0.25f, s.CabinFront - 0.55f), new Vector3(cw - 0.02f, belt + 0.06f, s.CabinFront + 0.05f), Interior);
                mb.Polygon(new[] { new Vector3(-hw + 0.06f, clr + 0.1f, zr + 0.2f), new Vector3(-hw + 0.06f, clr + 0.1f, zf - 0.2f), new Vector3(-hw + 0.06f, belt, zf - 0.2f), new Vector3(-hw + 0.06f, belt, zr + 0.2f) }, Interior, Vector3.right);
                mb.Polygon(new[] { new Vector3(hw - 0.06f, clr + 0.1f, zr + 0.2f), new Vector3(hw - 0.06f, clr + 0.1f, zf - 0.2f), new Vector3(hw - 0.06f, belt, zf - 0.2f), new Vector3(hw - 0.06f, belt, zr + 0.2f) }, Interior, Vector3.left);
                mb.Polygon(new[] { new Vector3(-cw, roofY - 0.1f, s.RoofRear), new Vector3(cw, roofY - 0.1f, s.RoofRear), new Vector3(cw, roofY - 0.1f, s.RoofFront), new Vector3(-cw, roofY - 0.1f, s.RoofFront) }, new Color(0.78f, 0.76f, 0.72f), Vector3.down);
                mb.Polygon(new[] { new Vector3(-cw, clr + 0.12f, zr + 0.2f), new Vector3(cw, clr + 0.12f, zr + 0.2f), new Vector3(cw, clr + 0.12f, zf - 0.3f), new Vector3(-cw, clr + 0.12f, zf - 0.3f) }, Interior, Vector3.up);
                float seatZ = s.Eye.z - 0.3f;
                foreach (float x in new[] { s.Eye.x, -s.Eye.x })
                {
                    // Seat backs only: the driver's own headrest is behind their head, never in view.
                    mb.LocalBox(new Vector3(x - 0.24f, belt - 0.35f, seatZ - 0.12f), new Vector3(x + 0.24f, belt + 0.15f, seatZ), Interior);
                }
                // Rear seat headrests are what you look past in the rear-view mirror.
                float rearZ = Mathf.Lerp(s.CabinRear, seatZ, 0.4f);
                if (!st.CargoBox && !st.PickupBed)
                    foreach (float x in new[] { -cw * 0.55f, 0f, cw * 0.55f })
                        mb.LocalBox(new Vector3(x - 0.1f, belt + 0.1f, rearZ - 0.08f), new Vector3(x + 0.1f, belt + 0.2f, rearZ), Interior);
                var wheelC = new Vector3(s.Eye.x, belt + 0.02f, s.CabinFront - 0.62f);
                for (int i = 0; i < 10; i++)
                {
                    float a0 = i * Mathf.PI * 0.2f, a1 = (i + 1) * Mathf.PI * 0.2f;
                    Vector3 R(float a) => wheelC + new Vector3(Mathf.Cos(a) * 0.19f, Mathf.Sin(a) * 0.19f * 0.9f, Mathf.Sin(a) * 0.19f * -0.4f);
                    mb.Beam(R(a0), R(a1), 0.035f, Trim);
                }
            }

            var mesh = mb.Build($"Car_{m.Make}{m.Name}");
            return mesh;
        }

        /// <summary>Bean driver with a hat. Cosmetics come from the shared NPC pool (GD §13).</summary>
        public static Mesh Driver(VehicleIdentity id)
        {
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
