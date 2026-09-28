using System;

namespace Tailed.Core.Util
{
    /// <summary>
    /// Ground-plane vector. X = east, Y = north (maps to Unity x/z).
    /// </summary>
    [Serializable]
    public readonly struct Vec2 : IEquatable<Vec2>
    {
        public readonly float X, Y;

        public Vec2(float x, float y) { X = x; Y = y; }

        public static readonly Vec2 Zero = new Vec2(0f, 0f);

        public static Vec2 operator +(Vec2 a, Vec2 b) => new Vec2(a.X + b.X, a.Y + b.Y);
        public static Vec2 operator -(Vec2 a, Vec2 b) => new Vec2(a.X - b.X, a.Y - b.Y);
        public static Vec2 operator -(Vec2 a) => new Vec2(-a.X, -a.Y);
        public static Vec2 operator *(Vec2 a, float s) => new Vec2(a.X * s, a.Y * s);
        public static Vec2 operator *(float s, Vec2 a) => new Vec2(a.X * s, a.Y * s);
        public static Vec2 operator /(Vec2 a, float s) => new Vec2(a.X / s, a.Y / s);

        public float Length => MathF.Sqrt(X * X + Y * Y);
        public float LengthSq => X * X + Y * Y;
        public Vec2 Normalized { get { float l = Length; return l > 1e-6f ? this / l : Zero; } }

        /// <summary>Rotated 90° counter-clockwise.</summary>
        public Vec2 PerpLeft => new Vec2(-Y, X);
        /// <summary>Rotated 90° clockwise — the kerb side for right-hand traffic.</summary>
        public Vec2 PerpRight => new Vec2(Y, -X);

        public static float Dot(Vec2 a, Vec2 b) => a.X * b.X + a.Y * b.Y;
        /// <summary>2D cross product (z of the 3D cross). Positive when b is counter-clockwise of a.</summary>
        public static float Cross(Vec2 a, Vec2 b) => a.X * b.Y - a.Y * b.X;
        public static float Distance(Vec2 a, Vec2 b) => (a - b).Length;
        public static Vec2 Lerp(Vec2 a, Vec2 b, float t) => a + (b - a) * t;

        /// <summary>Signed angle from a to b in radians, positive counter-clockwise (a left turn).</summary>
        public static float SignedAngle(Vec2 a, Vec2 b) => MathF.Atan2(Cross(a, b), Dot(a, b));

        /// <summary>Proper intersection of segments ab and cd (touching endpoints count).</summary>
        public static bool SegmentsIntersect(Vec2 a, Vec2 b, Vec2 c, Vec2 d)
        {
            Vec2 r = b - a, s = d - c;
            float denom = Cross(r, s);
            if (MathF.Abs(denom) < 1e-9f) return false; // parallel; collinear overlap ignored
            float t = Cross(c - a, s) / denom;
            float u = Cross(c - a, r) / denom;
            return t >= 0f && t <= 1f && u >= 0f && u <= 1f;
        }

        /// <summary>Intersection of infinite lines p + t·r and q + u·s.</summary>
        public static bool LineIntersection(Vec2 p, Vec2 r, Vec2 q, Vec2 s, out Vec2 hit)
        {
            float denom = Cross(r, s);
            if (MathF.Abs(denom) < 1e-9f) { hit = p; return false; }
            hit = p + r * (Cross(q - p, s) / denom);
            return true;
        }

        public bool Equals(Vec2 o) => X == o.X && Y == o.Y;
        public override bool Equals(object obj) => obj is Vec2 o && Equals(o);
        public override int GetHashCode() => HashCode.Combine(X, Y);
        public override string ToString() => $"({X:0.##}, {Y:0.##})";
    }
}
