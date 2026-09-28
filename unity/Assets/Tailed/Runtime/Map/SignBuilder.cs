using System.Collections.Generic;
using Tailed.Vehicles;
using UnityEngine;
using UnityEngine.Rendering;

namespace Tailed.Map
{
    /// <summary>Accumulates pixel-font sign text for the whole town into one mesh.</summary>
    public sealed class SignBuilder
    {
        readonly List<Vector3> _verts = new List<Vector3>();
        readonly List<Vector2> _uvs = new List<Vector2>();
        readonly List<Color> _colors = new List<Color>();
        readonly List<int> _tris = new List<int>();
        static Material _material;

        public static Material Material
        {
            get
            {
                if (_material) return _material;
                _material = new Material(Shader.Find("Tailed/SignText"));
                _material.SetTexture("_GlyphAtlas", Glyphs.Atlas);
                return _material;
            }
        }

        public void Text(string text, Vector3 centre, Vector3 facing, float height, Color color) =>
            Glyphs.AddText(_verts, _uvs, _colors, _tris, text, centre, facing, height, color.linear);

        public Mesh Build()
        {
            var mesh = new Mesh { name = "Signs", indexFormat = IndexFormat.UInt32 };
            mesh.SetVertices(_verts);
            mesh.SetUVs(0, _uvs);
            mesh.SetColors(_colors);
            mesh.SetTriangles(_tris, 0);
            mesh.RecalculateBounds();
            return mesh;
        }
    }
}
