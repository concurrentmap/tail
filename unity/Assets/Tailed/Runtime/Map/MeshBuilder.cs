using System.Collections.Generic;
using Tailed.Core.Util;
using UnityEngine;
using UnityEngine.Rendering;

namespace Tailed.Map
{
    /// <summary>
    /// Accumulates flat-shaded, vertex-coloured geometry (no shared vertices between faces,
    /// which gives the faceted low-poly look). Colours are given in sRGB and converted.
    /// </summary>
    public sealed class MeshBuilder
    {
        readonly List<Vector3> _verts = new List<Vector3>();
        readonly List<Vector3> _normals = new List<Vector3>();
        readonly List<Color> _colors = new List<Color>();
        readonly List<List<int>> _submeshes = new List<List<int>> { new List<int>() };
        List<int> _tris;

        public MeshBuilder() => _tris = _submeshes[0];

        public int VertexCount => _verts.Count;

        /// <summary>Subsequent geometry goes to this submesh (e.g. 1 = glass).</summary>
        public int Submesh
        {
            set
            {
                while (_submeshes.Count <= value) _submeshes.Add(new List<int>());
                _tris = _submeshes[value];
            }
        }

        /// <summary>Vertex colour alpha tags light type for the vehicle shader (1 = plain body).</summary>
        public const float TagHeadlight = 0.2f, TagBrake = 0.4f, TagLeft = 0.6f, TagRight = 0.8f;

        public static Vector3 V3(Vec2 p, float y) => new Vector3(p.X, y, p.Y);

        /// <summary>Planar convex polygon; points in any consistent winding, face oriented to <paramref name="up"/>.</summary>
        public void Polygon(IList<Vector3> pts, Color color, Vector3 up)
        {
            var n = Vector3.Cross(pts[1] - pts[0], pts[2] - pts[0]).normalized;
            bool flip = Vector3.Dot(n, up) < 0f;
            if (flip) n = -n;
            color = color.linear;
            int b = _verts.Count;
            foreach (var p in pts) { _verts.Add(p); _normals.Add(n); _colors.Add(color); }
            for (int i = 1; i + 1 < pts.Count; i++)
            {
                if (flip) { _tris.Add(b); _tris.Add(b + i + 1); _tris.Add(b + i); }
                else { _tris.Add(b); _tris.Add(b + i); _tris.Add(b + i + 1); }
            }
        }

        /// <summary>Quad a-b-c-d facing away from <paramref name="inside"/> (for walls) or towards up.</summary>
        public void Quad(Vector3 a, Vector3 b, Vector3 c, Vector3 d, Color color, Vector3 facing) =>
            Polygon(new[] { a, b, c, d }, color, facing);

        /// <summary>Flat ground polygon at height y.</summary>
        public void Flat(IList<Vec2> footprint, float y, Color color)
        {
            var pts = new Vector3[footprint.Count];
            for (int i = 0; i < pts.Length; i++) pts[i] = V3(footprint[i], y);
            Polygon(pts, color, Vector3.up);
        }

        /// <summary>Convex prism: walls + top. Footprint counter-clockwise seen from above.</summary>
        public void Extrude(IList<Vec2> footprint, float y0, float y1, Color wall, Color top)
        {
            Flat(footprint, y1, top);
            Vector3 centre = Vector3.zero;
            foreach (var p in footprint) centre += V3(p, 0f);
            centre /= footprint.Count;
            for (int i = 0; i < footprint.Count; i++)
            {
                var p = footprint[i];
                var q = footprint[(i + 1) % footprint.Count];
                var mid = V3(Vec2.Lerp(p, q, 0.5f), 0f);
                Quad(V3(p, y0), V3(q, y0), V3(q, y1), V3(p, y1), wall, mid - centre);
            }
        }

        /// <summary>Oriented box on the ground. <paramref name="forward"/> is the local depth axis.</summary>
        public void Box(Vec2 centre, Vec2 forward, float width, float depth, float y0, float y1, Color wall, Color top)
        {
            Extrude(Rect(centre, forward, width, depth), y0, y1, wall, top);
        }

        /// <summary>House with a gable roof whose ridge runs along the width axis.</summary>
        public void GableHouse(Vec2 centre, Vec2 forward, float width, float depth, float wallHeight, float roofHeight,
                               Color wall, Color roof)
        {
            var f = forward.Normalized;
            var r = f.PerpRight;
            Extrude(Rect(centre, f, width, depth), 0f, wallHeight, wall, wall);
            float overhang = 0.4f, hw = width * 0.5f + overhang, hd = depth * 0.5f + overhang;
            float yEave = wallHeight - 0.2f, yRidge = wallHeight + roofHeight;
            Vector3 P(float x, float z, float y) => V3(centre + r * x + f * z, y);
            // Roof slopes (front and back), then gable triangles.
            Quad(P(-hw, hd, yEave), P(hw, hd, yEave), P(hw, 0, yRidge), P(-hw, 0, yRidge), roof, V3(f, 1f));
            Quad(P(hw, -hd, yEave), P(-hw, -hd, yEave), P(-hw, 0, yRidge), P(hw, 0, yRidge), roof, V3(-f, 1f));
            float gw = width * 0.5f;
            Polygon(new[] { P(gw, depth * 0.5f, wallHeight), P(gw, -depth * 0.5f, wallHeight), P(gw, 0, yRidge - 0.3f) }, wall, V3(r, 0f));
            Polygon(new[] { P(-gw, -depth * 0.5f, wallHeight), P(-gw, depth * 0.5f, wallHeight), P(-gw, 0, yRidge - 0.3f) }, wall, V3(-r, 0f));
        }

        /// <summary>Upright n-gon prism (trunks, poles) or cone when topRadius is 0.</summary>
        public void Cylinder(Vector3 basePos, float radius, float topRadius, float height, int sides, Color color)
        {
            var ring0 = new Vector3[sides];
            var ring1 = new Vector3[sides];
            for (int i = 0; i < sides; i++)
            {
                float a = i * Mathf.PI * 2f / sides;
                var d = new Vector3(Mathf.Cos(a), 0f, Mathf.Sin(a));
                ring0[i] = basePos + d * radius;
                ring1[i] = basePos + d * topRadius + Vector3.up * height;
            }
            for (int i = 0; i < sides; i++)
            {
                int k = (i + 1) % sides;
                var outward = (ring0[i] + ring0[k]) * 0.5f - basePos;
                if (topRadius > 0.001f)
                    Quad(ring0[i], ring0[k], ring1[k], ring1[i], color, outward);
                else
                    Polygon(new[] { ring0[i], ring0[k], ring1[i] }, color, outward + Vector3.up * 0.3f);
            }
            if (topRadius > 0.001f) Polygon(ring1, color, Vector3.up);
        }

        /// <summary>Axis-aligned-in-plane thin panel (signs), centred, facing <paramref name="facing"/>.</summary>
        public void Panel(Vector3 centre, Vector3 facing, float halfSize, int sides, float thickness, Color front, Color back)
        {
            facing = new Vector3(facing.x, 0f, facing.z).normalized;
            var side = Vector3.Cross(Vector3.up, facing);
            var f = new Vector3[sides];
            var b = new Vector3[sides];
            for (int i = 0; i < sides; i++)
            {
                float a = (i + 0.5f) * Mathf.PI * 2f / sides;
                var p = centre + side * (Mathf.Cos(a) * halfSize) + Vector3.up * (Mathf.Sin(a) * halfSize);
                f[i] = p + facing * thickness * 0.5f;
                b[i] = p - facing * thickness * 0.5f;
            }
            Polygon(f, front, facing);
            Polygon(b, back, -facing);
        }

        /// <summary>
        /// Extrude a convex side profile (points as (z forward, y up), any winding) across x ∈ [-hw, hw].
        /// </summary>
        public void ProfileExtrude(IList<Vector2> profile, float halfWidth, Color color, Vector3 offset = default)
        {
            int n = profile.Count;
            var right = new Vector3[n];
            var left = new Vector3[n];
            Vector2 centre = Vector2.zero;
            foreach (var p in profile) centre += p;
            centre /= n;
            for (int i = 0; i < n; i++)
            {
                right[i] = offset + new Vector3(halfWidth, profile[i].y, profile[i].x);
                left[i] = offset + new Vector3(-halfWidth, profile[i].y, profile[i].x);
            }
            Polygon(right, color, Vector3.right);
            Polygon(left, color, Vector3.left);
            for (int i = 0; i < n; i++)
            {
                int k = (i + 1) % n;
                var mid = (profile[i] + profile[k]) * 0.5f - centre;
                Quad(right[i], right[k], left[k], left[i], color, new Vector3(0f, mid.y, mid.x));
            }
        }

        /// <summary>Square-section beam between two points (pillars, arms).</summary>
        public void Beam(Vector3 a, Vector3 b, float thickness, Color color)
        {
            var axis = (b - a).normalized;
            var side = Vector3.Cross(axis, Mathf.Abs(axis.y) < 0.9f ? Vector3.up : Vector3.right).normalized * (thickness * 0.5f);
            var up = Vector3.Cross(side, axis).normalized * (thickness * 0.5f);
            Vector3[] ring = { side + up, -side + up, -side - up, side - up };
            for (int i = 0; i < 4; i++)
            {
                int k = (i + 1) % 4;
                Quad(a + ring[i], a + ring[k], b + ring[k], b + ring[i], color, ring[i] + ring[k]);
            }
        }

        /// <summary>Cylinder lying along x (wheels).</summary>
        public void CylinderX(Vector3 centre, float radius, float width, int sides, Color side, Color face)
        {
            var a = new Vector3[sides];
            var b = new Vector3[sides];
            for (int i = 0; i < sides; i++)
            {
                float t = (i + 0.5f) * Mathf.PI * 2f / sides;
                var o = new Vector3(0f, Mathf.Cos(t) * radius, Mathf.Sin(t) * radius);
                a[i] = centre + o + Vector3.right * (width * 0.5f);
                b[i] = centre + o - Vector3.right * (width * 0.5f);
            }
            Polygon(a, face, Vector3.right);
            Polygon(b, face, Vector3.left);
            for (int i = 0; i < sides; i++)
            {
                int k = (i + 1) % sides;
                Quad(a[i], a[k], b[k], b[i], side, (a[i] + a[k]) * 0.5f - (centre + Vector3.right * (width * 0.5f)));
            }
        }

        /// <summary>Low-poly UV sphere (heads, lamps).</summary>
        public void Sphere(Vector3 centre, float radius, Color color, int slices = 8, int stacks = 5)
        {
            for (int j = 0; j < stacks; j++)
            {
                float v0 = Mathf.PI * j / stacks, v1 = Mathf.PI * (j + 1) / stacks;
                for (int i = 0; i < slices; i++)
                {
                    float u0 = 2f * Mathf.PI * i / slices, u1 = 2f * Mathf.PI * (i + 1) / slices;
                    Vector3 P(float u, float v) => centre + new Vector3(Mathf.Sin(v) * Mathf.Cos(u), Mathf.Cos(v), Mathf.Sin(v) * Mathf.Sin(u)) * radius;
                    var p00 = P(u0, v0); var p10 = P(u1, v0); var p11 = P(u1, v1); var p01 = P(u0, v1);
                    var outward = (p00 + p11) * 0.5f - centre;
                    if (j == 0) Polygon(new[] { p00, p11, p01 }, color, outward);
                    else if (j == stacks - 1) Polygon(new[] { p00, p10, p11 }, color, outward);
                    else Quad(p00, p10, p11, p01, color, outward);
                }
            }
        }

        // ---- smooth-shaded primitives (characters, tyres): shared vertices, radial normals ----

        int Vert(Vector3 p, Vector3 n, Color linear)
        {
            _verts.Add(p); _normals.Add(n); _colors.Add(linear);
            return _verts.Count - 1;
        }

        /// <summary>Smooth UV sphere, optionally squashed per axis (ellipsoid).</summary>
        public void SmoothSphere(Vector3 centre, Vector3 radii, Color color, int slices = 18, int stacks = 12)
        {
            var c = color.linear;
            int b = _verts.Count;
            for (int j = 0; j <= stacks; j++)
            {
                float v = Mathf.PI * j / stacks;
                for (int i = 0; i <= slices; i++)
                {
                    float u = 2f * Mathf.PI * i / slices;
                    var unit = new Vector3(Mathf.Sin(v) * Mathf.Cos(u), Mathf.Cos(v), Mathf.Sin(v) * Mathf.Sin(u));
                    var n = new Vector3(unit.x / radii.x, unit.y / radii.y, unit.z / radii.z).normalized;
                    Vert(centre + Vector3.Scale(unit, radii), n, c);
                }
            }
            int row = slices + 1;
            for (int j = 0; j < stacks; j++)
            for (int i = 0; i < slices; i++)
            {
                int a0 = b + j * row + i, a1 = a0 + 1, b0 = a0 + row, b1 = b0 + 1;
                _tris.Add(a0); _tris.Add(a1); _tris.Add(b0);
                _tris.Add(a1); _tris.Add(b1); _tris.Add(b0);
            }
        }

        /// <summary>Smooth-sided upright cylinder / frustum with a flat top cap.</summary>
        public void SmoothCylinder(Vector3 basePos, float radius, float topRadius, float height, Color color, int sides = 20, bool cap = true)
        {
            var c = color.linear;
            int b = _verts.Count;
            float slope = (radius - topRadius) / Mathf.Max(height, 1e-4f);
            for (int i = 0; i <= sides; i++)
            {
                float a = 2f * Mathf.PI * i / sides;
                var d = new Vector3(Mathf.Cos(a), 0f, Mathf.Sin(a));
                var n = (d + Vector3.up * slope).normalized;
                Vert(basePos + d * radius, n, c);
                Vert(basePos + d * topRadius + Vector3.up * height, n, c);
            }
            for (int i = 0; i < sides; i++)
            {
                int a0 = b + i * 2, a1 = a0 + 1, b0 = a0 + 2, b1 = a0 + 3;
                _tris.Add(a0); _tris.Add(a1); _tris.Add(b0);
                _tris.Add(a1); _tris.Add(b1); _tris.Add(b0);
            }
            if (cap && topRadius > 0.001f)
            {
                var ring = new Vector3[sides];
                for (int i = 0; i < sides; i++)
                {
                    float a = 2f * Mathf.PI * i / sides;
                    ring[i] = basePos + new Vector3(Mathf.Cos(a), 0f, Mathf.Sin(a)) * topRadius + Vector3.up * height;
                }
                Polygon(ring, color, Vector3.up);
            }
        }

        /// <summary>Smooth tyre lying along x, with flat side walls.</summary>
        public void SmoothWheel(Vector3 centre, float radius, float width, Color tyre, Color hub, int sides = 20)
        {
            var c = tyre.linear;
            int b = _verts.Count;
            float hw = width * 0.5f;
            for (int i = 0; i <= sides; i++)
            {
                float t = 2f * Mathf.PI * i / sides;
                var n = new Vector3(0f, Mathf.Cos(t), Mathf.Sin(t));
                Vert(centre + n * radius + Vector3.right * hw, n, c);
                Vert(centre + n * radius - Vector3.right * hw, n, c);
            }
            for (int i = 0; i < sides; i++)
            {
                int a0 = b + i * 2, a1 = a0 + 1, b0 = a0 + 2, b1 = a0 + 3;
                _tris.Add(a0); _tris.Add(a1); _tris.Add(b0);
                _tris.Add(a1); _tris.Add(b1); _tris.Add(b0);
            }
            var outer = new Vector3[sides];
            var inner = new Vector3[sides];
            for (int i = 0; i < sides; i++)
            {
                float t = 2f * Mathf.PI * (i + 0.5f) / sides;
                var o = new Vector3(0f, Mathf.Cos(t), Mathf.Sin(t)) * radius;
                outer[i] = centre + o + Vector3.right * hw;
                inner[i] = centre + o - Vector3.right * hw;
            }
            Polygon(outer, tyre, Vector3.right);
            Polygon(inner, tyre, Vector3.left);
            var hubRing = new Vector3[sides];
            for (int i = 0; i < sides; i++)
            {
                float t = 2f * Mathf.PI * (i + 0.5f) / sides;
                hubRing[i] = centre + new Vector3(0f, Mathf.Cos(t), Mathf.Sin(t)) * (radius * 0.58f);
            }
            var outerHub = System.Array.ConvertAll(hubRing, p => p + Vector3.right * (hw + 0.012f));
            var innerHub = System.Array.ConvertAll(hubRing, p => p - Vector3.right * (hw + 0.012f));
            Polygon(outerHub, hub, Vector3.right);
            Polygon(innerHub, hub, Vector3.left);
        }

        /// <summary>Round off the corners of a convex polygon (2D side profile) by <paramref name="r"/> metres.</summary>
        public static Vector2[] Chamfer(IList<Vector2> pts, float r, int steps = 2)
        {
            var result = new List<Vector2>();
            int n = pts.Count;
            for (int i = 0; i < n; i++)
            {
                Vector2 prev = pts[(i + n - 1) % n], p = pts[i], next = pts[(i + 1) % n];
                float rp = Mathf.Min(r, Vector2.Distance(prev, p) * 0.4f), rn = Mathf.Min(r, Vector2.Distance(p, next) * 0.4f);
                Vector2 a = p + (prev - p).normalized * rp, b = p + (next - p).normalized * rn;
                for (int k = 0; k <= steps; k++)
                {
                    float t = k / (float)steps;
                    // Quadratic Bézier with the original corner as control point.
                    result.Add((1 - t) * (1 - t) * a + 2 * (1 - t) * t * p + t * t * b);
                }
            }
            return result.ToArray();
        }

        /// <summary>Axis-aligned box in local space.</summary>
        public void LocalBox(Vector3 min, Vector3 max, Color color)
        {
            var c = (min + max) * 0.5f;
            Vector3 V(float x, float y, float z) => new Vector3(x, y, z);
            Quad(V(min.x, min.y, max.z), V(max.x, min.y, max.z), V(max.x, max.y, max.z), V(min.x, max.y, max.z), color, Vector3.forward);
            Quad(V(max.x, min.y, min.z), V(min.x, min.y, min.z), V(min.x, max.y, min.z), V(max.x, max.y, min.z), color, Vector3.back);
            Quad(V(max.x, min.y, max.z), V(max.x, min.y, min.z), V(max.x, max.y, min.z), V(max.x, max.y, max.z), color, Vector3.right);
            Quad(V(min.x, min.y, min.z), V(min.x, min.y, max.z), V(min.x, max.y, max.z), V(min.x, max.y, min.z), color, Vector3.left);
            Quad(V(min.x, max.y, min.z), V(min.x, max.y, max.z), V(max.x, max.y, max.z), V(max.x, max.y, min.z), color, Vector3.up);
            Quad(V(min.x, min.y, max.z), V(min.x, min.y, min.z), V(max.x, min.y, min.z), V(max.x, min.y, max.z), color, Vector3.down);
        }

        /// <summary>Single quad facing <paramref name="normal"/>, centred, size (w × h) in the plane.</summary>
        public void Card(Vector3 centre, Vector3 normal, Vector3 up, float w, float h, Color color)
        {
            var r = Vector3.Cross(up, normal).normalized * (w * 0.5f);
            var u = up.normalized * (h * 0.5f);
            Quad(centre - r - u, centre + r - u, centre + r + u, centre - r + u, color, normal);
        }

        public static Vec2[] Rect(Vec2 centre, Vec2 forward, float width, float depth)
        {
            var f = forward.Normalized * (depth * 0.5f);
            var r = forward.Normalized.PerpRight * (width * 0.5f);
            // Counter-clockwise from above (x east, y north).
            return new[] { centre - f + r, centre + f + r, centre + f - r, centre - f - r };
        }

        public Mesh Build(string name)
        {
            var mesh = new Mesh { name = name, indexFormat = IndexFormat.UInt32 };
            mesh.SetVertices(_verts);
            mesh.SetNormals(_normals);
            mesh.SetColors(_colors);
            mesh.subMeshCount = _submeshes.Count;
            for (int i = 0; i < _submeshes.Count; i++) mesh.SetTriangles(_submeshes[i], i);
            mesh.RecalculateBounds();
            return mesh;
        }
    }
}
