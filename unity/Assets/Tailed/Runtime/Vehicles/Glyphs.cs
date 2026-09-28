using System.Collections.Generic;
using UnityEngine;

namespace Tailed.Vehicles
{
    /// <summary>
    /// Plate/sign glyph atlas: a bold sans rasterised at runtime from the built-in font into an 8×8 grid
    /// of 128 px cells, mipmapped (plate legibility blur = mip level). Glyphs are drawn 1.3× wide so that
    /// plates (which squeeze a cell into a narrow slot) read like a condensed plate font and signs (which
    /// squeeze by 0.72) look natural. Glyph index: 0-9, A-Z = 10-35, '?' = 36, blank = 37.
    /// </summary>
    public static class Glyphs
    {
        public const int Cell = 128, Grid = 8;
        const string Charset = "0123456789ABCDEFGHIJKLMNOPQRSTUVWXYZ?";
        static Texture2D _atlas;

        public static int Index(char c)
        {
            c = char.ToUpperInvariant(c);
            if (c >= '0' && c <= '9') return c - '0';
            if (c >= 'A' && c <= 'Z') return 10 + c - 'A';
            if (c == '?') return 36;
            return 37;
        }

        public static Rect UvRect(int glyph)
        {
            float size = 1f / Grid;
            return new Rect((glyph % Grid) * size, (glyph / Grid) * size, size, size);
        }

        static Texture2D _sdf;
        /// <summary>SDF spread in mip-0 texels: the field saturates this far either side of an edge.</summary>
        public const float SdfSpread = 16f;

        /// <summary>
        /// Signed-distance-field version of <see cref="Atlas"/> for plates (R8, 0.5 = glyph edge, higher =
        /// inside). Thresholding a distance field keeps ink dark and plate light at any size, where a
        /// mip-blurred bitmap averages them into grey — plates stay high-contrast down to ~5 px glyphs.
        /// </summary>
        public static Texture2D SdfAtlas
        {
            get
            {
                if (_sdf != null) return _sdf;
                var src = Atlas.GetPixels32(0);
                int n = Cell * Grid;
                var inside = new float[n * n];
                var outside = new float[n * n];
                const float Inf = 1e20f;
                for (int i = 0; i < src.Length; i++)
                {
                    bool ink = src[i].r >= 128;
                    inside[i] = ink ? Inf : 0f;   // distance to the nearest non-ink texel
                    outside[i] = ink ? 0f : Inf;  // distance to the nearest ink texel
                }
                Edt(inside, n);
                Edt(outside, n);
                var bytes = new byte[n * n];
                for (int i = 0; i < bytes.Length; i++)
                {
                    float d = Mathf.Sqrt(outside[i]) - Mathf.Sqrt(inside[i]); // + outside, − inside
                    bytes[i] = (byte)Mathf.Clamp(Mathf.RoundToInt((0.5f - d / (2f * SdfSpread)) * 255f), 0, 255);
                }
                _sdf = new Texture2D(n, n, TextureFormat.R8, true, true)
                {
                    name = "GlyphSdf", filterMode = FilterMode.Trilinear, wrapMode = TextureWrapMode.Clamp, anisoLevel = 16,
                };
                _sdf.SetPixelData(bytes, 0);
                _sdf.Apply(true, true);
                return _sdf;
            }
        }

        /// <summary>Exact squared Euclidean distance transform in place (Felzenszwalb &amp; Huttenlocher), n×n.</summary>
        static void Edt(float[] grid, int n)
        {
            var f = new float[n];
            var d = new float[n];
            var v = new int[n];
            var z = new float[n + 1];
            for (int x = 0; x < n; x++)
            {
                for (int y = 0; y < n; y++) f[y] = grid[y * n + x];
                Edt1(f, d, v, z, n);
                for (int y = 0; y < n; y++) grid[y * n + x] = d[y];
            }
            for (int y = 0; y < n; y++)
            {
                System.Array.Copy(grid, y * n, f, 0, n);
                Edt1(f, d, v, z, n);
                System.Array.Copy(d, 0, grid, y * n, n);
            }
        }

        static void Edt1(float[] f, float[] d, int[] v, float[] z, int n)
        {
            int k = 0;
            v[0] = 0; z[0] = float.NegativeInfinity; z[1] = float.PositiveInfinity;
            for (int q = 1; q < n; q++)
            {
                float s = ((f[q] + q * q) - (f[v[k]] + v[k] * v[k])) / (2f * (q - v[k]));
                while (s <= z[k])
                {
                    k--;
                    s = ((f[q] + q * q) - (f[v[k]] + v[k] * v[k])) / (2f * (q - v[k]));
                }
                k++;
                v[k] = q; z[k] = s; z[k + 1] = float.PositiveInfinity;
            }
            k = 0;
            for (int q = 0; q < n; q++)
            {
                while (z[k + 1] < q) k++;
                d[q] = (q - v[k]) * (q - v[k]) + f[v[k]];
            }
        }

        public static Texture2D Atlas
        {
            get
            {
                if (_atlas != null) return _atlas;
                int px = Cell * Grid;
                const int fontSize = 120;
                var font = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");
                font.RequestCharactersInTexture(Charset, fontSize, FontStyle.Bold);
                font.GetCharacterInfo('H', out var cap, fontSize, FontStyle.Bold);
                float capHeight = Mathf.Max(1, cap.maxY - cap.minY);
                float scale = Cell * 0.78f / capHeight;

                var rt = RenderTexture.GetTemporary(px, px, 0, RenderTextureFormat.ARGB32, RenderTextureReadWrite.Linear);
                var prev = RenderTexture.active;
                RenderTexture.active = rt;
                GL.Clear(true, true, Color.black);
                var mat = new Material(Shader.Find("Tailed/FontBlit")) { mainTexture = font.material.mainTexture };
                mat.SetPass(0);
                GL.PushMatrix();
                GL.LoadPixelMatrix(0, px, 0, px);
                GL.Begin(GL.QUADS);
                foreach (char ch in Charset)
                {
                    if (!font.GetCharacterInfo(ch, out var ci, fontSize, FontStyle.Bold)) continue;
                    int g = Index(ch);
                    float ox = (g % Grid) * Cell, oy = (g / Grid) * Cell;
                    float w = Mathf.Min((ci.maxX - ci.minX) * scale * 1.3f, Cell * 0.92f);
                    float h = (ci.maxY - ci.minY) * scale;
                    float x0 = ox + (Cell - w) * 0.5f;
                    float y0 = oy + Cell * 0.11f + (ci.minY - cap.minY) * scale; // shared baseline
                    GL.TexCoord(ci.uvBottomLeft); GL.Vertex3(x0, y0, 0);
                    GL.TexCoord(ci.uvTopLeft); GL.Vertex3(x0, y0 + h, 0);
                    GL.TexCoord(ci.uvTopRight); GL.Vertex3(x0 + w, y0 + h, 0);
                    GL.TexCoord(ci.uvBottomRight); GL.Vertex3(x0 + w, y0, 0);
                }
                GL.End();
                GL.PopMatrix();
                _atlas = new Texture2D(px, px, TextureFormat.RGBA32, true, true)
                {
                    name = "GlyphAtlas", filterMode = FilterMode.Trilinear, wrapMode = TextureWrapMode.Clamp, anisoLevel = 8,
                };
                _atlas.ReadPixels(new Rect(0, 0, px, px), 0, 0);
                _atlas.Apply(true, false);
                RenderTexture.active = prev;
                RenderTexture.ReleaseTemporary(rt);
                Object.Destroy(mat);
                return _atlas;
            }
        }

        /// <summary>
        /// Plates are 1.7× real size (0.92 × 0.24 m, ~17 cm characters): readable unaided at twice the
        /// distance, which is what makes them work at tailing range without HUD help. Legibility
        /// thresholds (Optics, Tailed/Plate) scale with this.
        /// </summary>
        public const float PlateScale = 1.7f;
        public const float PlateWidth = 0.54f * PlateScale, PlateHeight = 0.14f * PlateScale;

        /// <summary>Plate mesh: front and rear quads (m × m) with glyph indices in UV1/UV2 for Tailed/Plate.</summary>
        public static Mesh PlateMesh(string plate, Vector3 front, Vector3 rear)
        {
            // Deliberately oversized plates: the one high-detail element in the art style (GD §13).
            const float w = PlateWidth, h = PlateHeight;
            var verts = new List<Vector3>();
            var normals = new List<Vector3>();
            var uv = new List<Vector2>();
            var tris = new List<int>();
            var c = new int[7];
            for (int i = 0; i < 7; i++) c[i] = i < plate.Length ? Index(plate[i]) : 37;
            var chars0 = new Vector4(c[0], c[1], c[2], c[3]);
            var chars1 = new Vector4(c[4], c[5], c[6], 0);
            var u1 = new List<Vector4>();
            var u2 = new List<Vector4>();
            void Quad(Vector3 centre, Vector3 normal)
            {
                var right = Vector3.Cross(Vector3.up, normal).normalized * (w * 0.5f);
                var up = Vector3.up * (h * 0.5f);
                int b = verts.Count;
                // Text reads left-to-right when facing the plate: "right" of the viewer is -right here.
                verts.Add(centre + right - up); verts.Add(centre - right - up); verts.Add(centre - right + up); verts.Add(centre + right + up);
                uv.Add(new Vector2(0, 0)); uv.Add(new Vector2(1, 0)); uv.Add(new Vector2(1, 1)); uv.Add(new Vector2(0, 1));
                for (int k = 0; k < 4; k++) { normals.Add(normal); u1.Add(chars0); u2.Add(chars1); }
                tris.AddRange(new[] { b, b + 2, b + 1, b, b + 3, b + 2 });
            }
            Quad(front, Vector3.forward);
            Quad(rear, Vector3.back);
            var mesh = new Mesh { name = "Plate " + plate };
            mesh.SetVertices(verts);
            mesh.SetNormals(normals);
            mesh.SetUVs(0, uv);
            mesh.SetUVs(1, u1);
            mesh.SetUVs(2, u2);
            mesh.SetTriangles(tris, 0);
            // Winding: make sure faces point along their normals.
            FixWinding(mesh);
            mesh.RecalculateBounds();
            return mesh;
        }

        static void FixWinding(Mesh mesh)
        {
            var v = mesh.vertices;
            var n = mesh.normals;
            var t = mesh.triangles;
            for (int i = 0; i < t.Length; i += 3)
            {
                var face = Vector3.Cross(v[t[i + 1]] - v[t[i]], v[t[i + 2]] - v[t[i]]);
                if (Vector3.Dot(face, n[t[i]]) < 0f) (t[i + 1], t[i + 2]) = (t[i + 2], t[i + 1]);
            }
            mesh.triangles = t;
        }

        /// <summary>
        /// Adds a line of pixel-font text as quads (for signs). Text is centred on <paramref name="centre"/>,
        /// reading left to right for a viewer facing along -<paramref name="normal"/>.
        /// </summary>
        public static void AddText(List<Vector3> verts, List<Vector2> uvs, List<Color> colors, List<int> tris,
                                   string text, Vector3 centre, Vector3 normal, float charHeight, Color color)
        {
            text = text.ToUpperInvariant();
            float cw = charHeight * 0.72f; // squeezes the 1.3×-wide atlas glyphs back to natural proportions
            var right = Vector3.Cross(Vector3.up, normal).normalized;
            var up = Vector3.up;
            var start = centre + right * (cw * text.Length * 0.5f);
            for (int i = 0; i < text.Length; i++)
            {
                int g = Index(text[i]);
                if (g == 37) continue;
                var r = UvRect(g);
                // Crop the cell margins.
                float u0 = r.x + r.width * 0.03f, u1 = r.x + r.width * 0.97f;
                float v0 = r.y + r.height * 0.05f, v1 = r.y + r.height * 0.95f;
                var p = start - right * (cw * (i + 1)) + normal * 0.01f;
                int b = verts.Count;
                verts.Add(p + right * cw - up * 0f); verts.Add(p); verts.Add(p + up * charHeight); verts.Add(p + right * cw + up * charHeight);
                uvs.Add(new Vector2(u0, v0)); uvs.Add(new Vector2(u1, v0)); uvs.Add(new Vector2(u1, v1)); uvs.Add(new Vector2(u0, v1));
                for (int k = 0; k < 4; k++) colors.Add(color);
                tris.AddRange(new[] { b, b + 1, b + 2, b, b + 2, b + 3 });
            }
        }
    }
}
