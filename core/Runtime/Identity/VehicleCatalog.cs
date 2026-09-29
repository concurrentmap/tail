using System;
using Tailed.Core.Util;

namespace Tailed.Core.Identity
{
    /// <summary>Silhouette family. Every model must be identifiable by shape alone (GD §11, §13).</summary>
    public enum BodyStyle : byte { Hatch, Sedan, Wagon, Suv, Pickup, Van, Cube, Coupe, Bus, BoxTruck }

    public sealed class VehicleModel
    {
        public int Id;
        public string Make, Name;
        public BodyStyle Style;
        /// <summary>Metres: bumper to bumper, mirror-less width, roof height.</summary>
        public float Length, Width, Height;
        public float WheelRadius, WheelBase;
        /// <summary>Fleet frequency weight (common cars blend in).</summary>
        public float Rarity;
        /// <summary>Motor-pool cost for Tails (GD §3): common is expensive, loud is cheap.</summary>
        public int Cost;
        public float AccelFactor = 1f, TopSpeedFactor = 1f;
        /// <summary>Town traffic only (buses, lorries): not in the motor pool, never parks in a bay.</summary>
        public bool NpcOnly;
        /// <summary>Fixed livery colour, or -1 for the fleet colour distribution.</summary>
        public int Livery = -1;
        public string FullName => $"{Make} {Name}";
    }

    public sealed class VehicleColor
    {
        public int Id;
        public string Name;
        public float R, G, B;
        public float Weight;
    }

    public enum HatType : byte { None, Cap, Beanie, TopHat, Cowboy, Party, Crown, Bucket, Headband }
    public enum GlassesType : byte { None, Round, Sunglasses, Chunky }

    /// <summary>Everything an observer can learn about a vehicle. Players and NPCs are indistinguishable.</summary>
    [Serializable]
    public struct VehicleIdentity : IEquatable<VehicleIdentity>
    {
        public byte ModelId, ColorId;
        public byte Hat, HatColorId;
        /// <summary>The driver (PEAK-style base character + accessories): skin tone, shirt colour, eyes, mouth, glasses.</summary>
        public byte Skin, ShirtColorId, Eyes, Mouth, Glasses;
        /// <summary>Canonical 7-char plate.</summary>
        public string Plate;

        public bool Equals(VehicleIdentity o) =>
            ModelId == o.ModelId && ColorId == o.ColorId && Hat == o.Hat && HatColorId == o.HatColorId && Plate == o.Plate &&
            Skin == o.Skin && ShirtColorId == o.ShirtColorId && Eyes == o.Eyes && Mouth == o.Mouth && Glasses == o.Glasses;
        public override bool Equals(object obj) => obj is VehicleIdentity o && Equals(o);
        public override int GetHashCode() => HashCode.Combine(ModelId, ColorId, Hat, HatColorId, Plate, Skin, ShirtColorId, HashCode.Combine(Eyes, Mouth, Glasses));

        /// <summary>Key for the assembled driver mesh (everything that changes how the driver looks).</summary>
        public int DriverKey => ((((Hat * 16 + HatColorId) * 8 + Skin) * 16 + ShirtColorId) * 4 + Eyes) * 64 + Mouth * 16 + Glasses;
    }

    public static class VehicleCatalog
    {
        public static readonly VehicleModel[] Models =
        {
            new VehicleModel { Id = 0, Make = "Sedova", Name = "Classic", Style = BodyStyle.Sedan, Length = 4.6f, Width = 1.8f, Height = 1.45f, WheelRadius = 0.33f, WheelBase = 2.7f, Rarity = 25, Cost = 5 },
            new VehicleModel { Id = 1, Make = "Mesa", Name = "Trailhound", Style = BodyStyle.Suv, Length = 4.7f, Width = 1.9f, Height = 1.75f, WheelRadius = 0.38f, WheelBase = 2.8f, Rarity = 20, Cost = 5, AccelFactor = 0.9f },
            new VehicleModel { Id = 2, Make = "Honker", Name = "Pip", Style = BodyStyle.Hatch, Length = 3.9f, Width = 1.72f, Height = 1.5f, WheelRadius = 0.3f, WheelBase = 2.45f, Rarity = 16, Cost = 4 },
            new VehicleModel { Id = 3, Make = "Bramble", Name = "Longhorn", Style = BodyStyle.Pickup, Length = 5.3f, Width = 1.95f, Height = 1.8f, WheelRadius = 0.4f, WheelBase = 3.2f, Rarity = 11, Cost = 3, AccelFactor = 0.85f },
            new VehicleModel { Id = 4, Make = "Parcel", Name = "Boxer", Style = BodyStyle.Van, Length = 5.2f, Width = 2.0f, Height = 2.3f, WheelRadius = 0.36f, WheelBase = 3.1f, Rarity = 10, Cost = 3, AccelFactor = 0.75f, TopSpeedFactor = 0.9f },
            new VehicleModel { Id = 5, Make = "Hearth", Name = "Wagon", Style = BodyStyle.Wagon, Length = 4.8f, Width = 1.82f, Height = 1.5f, WheelRadius = 0.33f, WheelBase = 2.8f, Rarity = 9, Cost = 3 },
            new VehicleModel { Id = 6, Make = "Kobo", Name = "Cube", Style = BodyStyle.Cube, Length = 3.5f, Width = 1.6f, Height = 1.8f, WheelRadius = 0.28f, WheelBase = 2.3f, Rarity = 5, Cost = 1, AccelFactor = 0.85f, TopSpeedFactor = 0.9f },
            new VehicleModel { Id = 7, Make = "Zest", Name = "Vector", Style = BodyStyle.Coupe, Length = 4.4f, Width = 1.85f, Height = 1.25f, WheelRadius = 0.34f, WheelBase = 2.55f, Rarity = 4, Cost = 1, AccelFactor = 1.3f, TopSpeedFactor = 1.1f },
            // Town traffic only. Buses are placed by the traffic director, never drawn at random.
            new VehicleModel { Id = 8, Make = "Metro", Name = "Bus", Style = BodyStyle.Bus, Length = 10.5f, Width = 2.45f, Height = 3.0f, WheelRadius = 0.5f, WheelBase = 6.0f, Rarity = 0, NpcOnly = true, Livery = 10, AccelFactor = 0.6f, TopSpeedFactor = 0.85f },
            new VehicleModel { Id = 9, Make = "Haulwell", Name = "Box Truck", Style = BodyStyle.BoxTruck, Length = 7.2f, Width = 2.3f, Height = 3.1f, WheelRadius = 0.45f, WheelBase = 4.2f, Rarity = 3, NpcOnly = true, AccelFactor = 0.6f, TopSpeedFactor = 0.85f },
        };

        public static readonly VehicleColor[] Colors =
        {
            new VehicleColor { Id = 0, Name = "Silver", R = 0.74f, G = 0.76f, B = 0.78f, Weight = 18 },
            new VehicleColor { Id = 1, Name = "White", R = 0.93f, G = 0.93f, B = 0.9f, Weight = 18 },
            new VehicleColor { Id = 2, Name = "Black", R = 0.14f, G = 0.14f, B = 0.16f, Weight = 15 },
            new VehicleColor { Id = 3, Name = "Grey", R = 0.45f, G = 0.47f, B = 0.5f, Weight = 15 },
            new VehicleColor { Id = 4, Name = "Navy", R = 0.18f, G = 0.26f, B = 0.46f, Weight = 8 },
            new VehicleColor { Id = 5, Name = "Red", R = 0.78f, G = 0.16f, B = 0.14f, Weight = 8 },
            new VehicleColor { Id = 6, Name = "Beige", R = 0.8f, G = 0.72f, B = 0.56f, Weight = 5 },
            new VehicleColor { Id = 7, Name = "Green", R = 0.22f, G = 0.45f, B = 0.28f, Weight = 4 },
            new VehicleColor { Id = 8, Name = "Sky", R = 0.45f, G = 0.68f, B = 0.88f, Weight = 3 },
            new VehicleColor { Id = 9, Name = "Orange", R = 0.95f, G = 0.52f, B = 0.12f, Weight = 2 },
            new VehicleColor { Id = 10, Name = "Yellow", R = 0.97f, G = 0.82f, B = 0.2f, Weight = 2 },
            new VehicleColor { Id = 11, Name = "Pink", R = 0.95f, G = 0.55f, B = 0.7f, Weight = 1 },
        };

        public static readonly float[] HatWeights = { 45, 14, 10, 3, 5, 5, 1, 8, 6 };
        public static readonly float[] GlassesWeights = { 60, 15, 15, 10 };
        public const int SkinTones = 6, EyeStyles = 4, MouthStyles = 4;

        public static float[] ModelWeights()
        {
            var w = new float[Models.Length];
            for (int i = 0; i < w.Length; i++) w[i] = Models[i].Rarity;
            return w;
        }

        public static float[] ColorWeights()
        {
            var w = new float[Colors.Length];
            for (int i = 0; i < w.Length; i++) w[i] = Colors[i].Weight;
            return w;
        }

        /// <summary>Case-insensitive lookup by "Make Name", "Name" or "Make".</summary>
        public static int FindModel(string text)
        {
            if (string.IsNullOrWhiteSpace(text)) return -1;
            text = text.Trim();
            foreach (var m in Models)
                if (string.Equals(m.FullName, text, StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(m.Name, text, StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(m.Make, text, StringComparison.OrdinalIgnoreCase))
                    return m.Id;
            return -1;
        }
    }

    /// <summary>Issues identities from the fleet distribution. One per match; NPCs and players share it.</summary>
    public sealed class IdentityService
    {
        readonly PlateGenerator _plates;
        Rng _rng;
        readonly float[] _models = VehicleCatalog.ModelWeights();
        readonly float[] _colors = VehicleCatalog.ColorWeights();

        public IdentityService(ulong seed)
        {
            _rng = new Rng(seed ^ 0xA5A5A5A5UL);
            _plates = new PlateGenerator(seed);
        }

        public VehicleIdentity Random()
        {
            int model = _rng.PickWeighted(_models);
            int livery = VehicleCatalog.Models[model].Livery;
            return Create(model, livery >= 0 ? livery : _rng.PickWeighted(_colors));
        }

        /// <summary>A random identity a player can drive (no buses or lorries).</summary>
        public VehicleIdentity RandomCar()
        {
            int model;
            do model = _rng.PickWeighted(_models); while (VehicleCatalog.Models[model].NpcOnly);
            return Create(model, _rng.PickWeighted(_colors));
        }

        public VehicleIdentity Create(int modelId, int colorId) => new VehicleIdentity
        {
            ModelId = (byte)modelId,
            ColorId = (byte)colorId,
            Hat = (byte)_rng.PickWeighted(VehicleCatalog.HatWeights),
            HatColorId = (byte)_rng.NextInt(VehicleCatalog.Colors.Length), // any colour: hats are fun, fleets are grey
            Skin = (byte)_rng.NextInt(VehicleCatalog.SkinTones),
            ShirtColorId = (byte)_rng.NextInt(VehicleCatalog.Colors.Length),
            Eyes = (byte)_rng.NextInt(VehicleCatalog.EyeStyles),
            Mouth = (byte)_rng.NextInt(VehicleCatalog.MouthStyles),
            Glasses = (byte)_rng.PickWeighted(VehicleCatalog.GlassesWeights),
            Plate = _plates.Next(),
        };

        public string NewPlate(string old)
        {
            string plate = _plates.Next(); // issue first so the old plate can't come straight back
            _plates.Release(old);
            return plate;
        }

        public void Release(VehicleIdentity id) => _plates.Release(id.Plate);
    }
}
