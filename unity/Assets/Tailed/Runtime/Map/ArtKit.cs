using System.Collections.Generic;
using Tailed.Core.Util;
using UnityEngine;

namespace Tailed.Map
{
    /// <summary>
    /// The Blender-built town kit (tools/blender/build_town.py → Resources/Art/Town): houses, yard
    /// dressing, car-park props and trees, appended into TownBuilder's combined meshes with their
    /// marker colours swapped per instance. Kit colours are authored as sRGB, like the procedural
    /// town, and converted to linear here.
    /// </summary>
    public static class ArtKit
    {
        public static readonly string[] Houses =
            { "House_Bungalow", "House_TwoStorey", "House_Cottage", "House_Ranch", "House_AFrame", "House_Modern", "House_Gambrel" };
        public static readonly string[] Trees = { "Tree_Round", "Tree_Oak", "Tree_Pine", "Tree_Poplar", "Tree_Blossom" };
        public static readonly string[] Backyard = { "Swing", "Trampoline", "Shed", "WashingLine", "Pool", "Bbq" };

        /// <summary>Per-instance marker replacements (sRGB).</summary>
        public struct Swap
        {
            public Color Wall, Roof, Trim, Leaf;
        }

        static readonly Dictionary<string, Mesh> Cache = new Dictionary<string, Mesh>();

        public static Mesh Get(string name)
        {
            if (Cache.TryGetValue(name, out var m)) return m;
            m = Resources.Load<Mesh>("Art/Town/" + name);
            if (m != null && !m.isReadable) m = null;
            Cache[name] = m;
            return m;
        }

        public static bool Available => Get("House_Bungalow") != null;

        /// <summary>
        /// Append <paramref name="name"/> with its local origin at <paramref name="at"/> (ground height
        /// <paramref name="y"/>), local +z pointing along <paramref name="facing"/>. Mirror flips x.
        /// </summary>
        public static bool Place(MeshBuilder mb, string name, Vec2 at, float y, Vec2 facing, Swap swap, float scale = 1f, bool mirror = false)
        {
            var mesh = Get(name);
            if (mesh == null) return false;
            var rot = Quaternion.LookRotation(MeshBuilder.V3(facing, 0f), Vector3.up);
            var m = Matrix4x4.TRS(MeshBuilder.V3(at, y), rot, new Vector3(mirror ? -scale : scale, scale, scale));
            Color wall = swap.Wall.linear, roof = swap.Roof.linear, trim = swap.Trim.linear, leaf = swap.Leaf.linear;
            Color leaf2 = (swap.Leaf * 0.8f).linear;
            mb.Append(mesh, m, c => Recolor(c, trim, leaf, leaf2, wall, roof));
            return true;
        }

        /// <summary>Kit colour → linear, with the markers replaced (replacements already linear).</summary>
        public static Color Recolor(Color c, Color trim, Color leaf, Color leaf2, Color wall = default, Color roof = default)
        {
            bool hi(float v) => v > 0.95f;
            bool lo(float v) => v < 0.05f;
            if (hi(c.r) && lo(c.g) && hi(c.b)) return wall;
            if (lo(c.r) && hi(c.g) && hi(c.b)) return roof;
            if (hi(c.r) && hi(c.g) && lo(c.b)) return trim;
            if (lo(c.r) && lo(c.b) && hi(c.g)) return leaf;
            if (lo(c.r) && lo(c.b) && Mathf.Abs(c.g - 0.5f) < 0.05f) return leaf2;
            var l = new Color(c.r, c.g, c.b).linear;
            l.a = c.a;
            return l;
        }

        /// <summary>Local bounds of a kit piece (for footprints and colliders).</summary>
        public static Bounds BoundsOf(string name)
        {
            var m = Get(name);
            return m != null ? m.bounds : new Bounds(Vector3.zero, Vector3.one);
        }
    }
}
