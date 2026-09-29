using System;
using Tailed.Core.Identity;
using Tailed.Core.Traffic;
using Tailed.Core.Util;

namespace Tailed.Core.Net
{
    /// <summary>
    /// One vehicle's replicated state. Lane-graph vehicles send topology (≈9 bytes); free vehicles
    /// (players, disrupted NPCs) send a quantised pose (≈11 bytes). Players and NPCs share the same
    /// id space and format, so nothing on the wire says who is human (architecture §7.2).
    /// </summary>
    public struct VehicleState
    {
        public ushort Id;
        public VehicleMode Mode;
        /// <summary>Lane, connector or bay id depending on mode.</summary>
        public ushort Ref;
        public float S, Speed, Lateral;
        public VehicleFlags Flags;
        /// <summary>Free-pose modes (External).</summary>
        public Vec2 Position, Heading;
    }

    public static class TrafficCodec
    {
        public const float PosOffset = 400f, PosScale = 20f;     // 5 cm, covers -400..2876 m
        const float SScale = 20f, SpeedScale = 4f, LatScale = 20f;

        public static void Write(ByteWriter w, in VehicleState v)
        {
            w.U16(v.Id);
            w.U8((byte)v.Mode);
            if (v.Mode == VehicleMode.External)
            {
                w.U16(Q(v.Position.X + PosOffset, PosScale));
                w.U16(Q(v.Position.Y + PosOffset, PosScale));
                float ang = MathF.Atan2(v.Heading.Y, v.Heading.X);
                w.U16((ushort)Math.Round((ang + MathF.PI) / (2f * MathF.PI) * 65535f));
            }
            else
            {
                w.U16(v.Ref);
                w.U16(Q(v.S + 10f, SScale)); // parking paths can start slightly negative
                w.I8((sbyte)Math.Clamp(Math.Round(v.Lateral * LatScale), -127, 127));
            }
            w.U8((byte)Math.Clamp(Math.Round(Math.Abs(v.Speed) * SpeedScale), 0, 255));
            w.U8((byte)v.Flags);
        }

        public static VehicleState Read(ByteReader r)
        {
            var v = new VehicleState { Id = r.U16(), Mode = (VehicleMode)r.U8() };
            if (v.Mode == VehicleMode.External)
            {
                v.Position = new Vec2(r.U16() / PosScale - PosOffset, r.U16() / PosScale - PosOffset);
                float ang = r.U16() / 65535f * 2f * MathF.PI - MathF.PI;
                v.Heading = new Vec2(MathF.Cos(ang), MathF.Sin(ang));
            }
            else
            {
                v.Ref = r.U16();
                v.S = r.U16() / SScale - 10f;
                v.Lateral = r.I8() / LatScale;
            }
            v.Speed = r.U8() / SpeedScale;
            v.Flags = (VehicleFlags)r.U8();
            return v;
        }

        public static VehicleState FromSim(SimVehicle v)
        {
            var s = new VehicleState { Id = (ushort)v.Id, Mode = v.Mode, Speed = v.Speed, Flags = v.Flags, S = v.S, Lateral = v.VisualLateral };
            switch (v.Mode)
            {
                case VehicleMode.Lane: s.Ref = (ushort)v.Lane; break;
                case VehicleMode.Connector: s.Ref = (ushort)v.Connector; break;
                case VehicleMode.External: s.Position = v.Position; s.Heading = v.Heading; break;
                default: s.Ref = (ushort)v.Bay; break;
            }
            // Disrupted NPCs still sit on the lane graph; nothing special to send.
            return s;
        }

        public static void WriteIdentity(ByteWriter w, in VehicleIdentity id)
        {
            w.U8(id.ModelId); w.U8(id.ColorId); w.U8(id.Hat); w.U8(id.HatColorId);
            w.U8(id.Skin); w.U8(id.ShirtColorId); w.U8((byte)(id.Eyes << 4 | id.Mouth)); w.U8(id.Glasses);
            w.Str(id.Plate);
        }

        public static VehicleIdentity ReadIdentity(ByteReader r)
        {
            var id = new VehicleIdentity { ModelId = r.U8(), ColorId = r.U8(), Hat = r.U8(), HatColorId = r.U8() };
            id.Skin = r.U8(); id.ShirtColorId = r.U8();
            byte face = r.U8();
            id.Eyes = (byte)(face >> 4); id.Mouth = (byte)(face & 15);
            id.Glasses = r.U8();
            id.Plate = r.Str();
            return id;
        }

        static ushort Q(float v, float scale) => (ushort)Math.Clamp(Math.Round(v * scale), 0, 65535);
    }
}
