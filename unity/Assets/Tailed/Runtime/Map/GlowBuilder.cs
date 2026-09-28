using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;

namespace Tailed.Map
{
    /// <summary>Soft radial light pools (street lamps at night), one mesh, drawn with Tailed/NightGlow.</summary>
    public sealed class GlowBuilder
    {
        readonly List<Vector3> _verts = new List<Vector3>();
        readonly List<Color> _colors = new List<Color>();
        readonly List<int> _tris = new List<int>();
        static Material _material;
        public static Material Material => _material ? _material : _material = new Material(Shader.Find("Tailed/NightGlow"));

        public void Disc(Vector3 centre, float radius, Color colour, int sides = 16)
        {
            int c = _verts.Count;
            _verts.Add(centre);
            _colors.Add(colour.linear);
            for (int i = 0; i < sides; i++)
            {
                float a = i * Mathf.PI * 2f / sides;
                _verts.Add(centre + new Vector3(Mathf.Cos(a), 0f, Mathf.Sin(a)) * radius);
                _colors.Add(Color.black);
            }
            for (int i = 0; i < sides; i++)
            {
                _tris.Add(c);
                _tris.Add(c + 1 + (i + 1) % sides);
                _tris.Add(c + 1 + i);
            }
        }

        public Mesh Build()
        {
            var m = new Mesh { name = "LampGlow", indexFormat = IndexFormat.UInt32 };
            m.SetVertices(_verts);
            m.SetColors(_colors);
            m.SetTriangles(_tris, 0);
            m.RecalculateBounds();
            return m;
        }
    }
}
