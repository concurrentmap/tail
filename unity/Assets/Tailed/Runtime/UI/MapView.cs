using System;
using System.Collections.Generic;
using Tailed.Core.Roads;
using Tailed.Core.Util;
using Tailed.Map;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;

namespace Tailed.UI
{
    /// <summary>
    /// Stylised top-down town map (baked once per seed from the road network) with a marker layer.
    /// Markers are rebuilt by the owner each refresh via <see cref="Marker"/>. Clicks report world XZ.
    /// </summary>
    public sealed class MapView : MonoBehaviour, IPointerClickHandler, IScrollHandler, IDragHandler, IBeginDragHandler
    {
        public event Action<Vector2, PointerEventData.InputButton> Clicked;
        RawImage _image;
        RectTransform _markers, _grid;
        public static MapGrid Grid { get; private set; }
        /// <summary>Side length (UI px) the map was created at.</summary>
        public float Size { get; private set; }
        static Texture2D _texture;
        static int _textureSeed = int.MinValue;
        static Vector2 _min, _size;

        public static MapView Create(Transform parent, Vector2 anchor, Vector2 pos, float size)
        {
            var frame = UiKit.Panel(parent, "Map", UiKit.Ink, anchor, pos, new Vector2(size + 12, size + 12));
            frame.gameObject.AddComponent<RectMask2D>(); // zoomed-in content stays inside the frame
            var rt = UiKit.Stretch(frame.transform, "MapImage", 6);
            var view = rt.gameObject.AddComponent<MapView>();
            view._image = rt.gameObject.AddComponent<RawImage>();
            view.Size = size;
            view._grid = UiKit.Stretch(rt, "Grid");
            view._markers = UiKit.Stretch(rt, "Markers");
            view.Refresh();
            view.GridLabels(size);
            return view;
        }

        public void Refresh()
        {
            var town = TownBuilder.Instance;
            if (town == null || town.Network == null) return;
            if (_textureSeed != town.Seed || _texture == null) Bake(town);
            _image.texture = _texture;
        }

        public void ClearMarkers() => UiKit.Clear(_markers);

        // ---- zoom & pan ----------------------------------------------------------------
        // The map content scales up inside its (masked) frame; markers, lines and labels are
        // counter-scaled so they keep their on-screen size — zooming spreads things out.

        public const float MaxZoom = 6f;
        public float Zoom { get; private set; } = 1f;
        Vector2 _pan;

        public void SetZoom(float zoom) => ZoomAbout(Vector2.zero, zoom, keepCentre: true);

        /// <summary>Zoom keeping the content point under <paramref name="frameLocal"/> fixed.</summary>
        void ZoomAbout(Vector2 frameLocal, float zoom, bool keepCentre = false)
        {
            zoom = Mathf.Clamp(zoom, 1f, MaxZoom);
            if (!keepCentre) _pan = frameLocal - zoom / Zoom * (frameLocal - _pan);
            else _pan *= zoom / Zoom;
            Zoom = zoom;
            ApplyView();
        }

        /// <summary>Keep a world position in the middle of the view (the lap map follows your car).</summary>
        public void CentreOn(Vector2 world)
        {
            _pan = -WorldToLocal(world) * Zoom;
            ApplyView();
        }

        void ApplyView()
        {
            var rt = (RectTransform)transform;
            var half = rt.rect.size * 0.5f;
            float limitX = (Zoom - 1f) * half.x, limitY = (Zoom - 1f) * half.y;
            _pan = new Vector2(Mathf.Clamp(_pan.x, -limitX, limitX), Mathf.Clamp(_pan.y, -limitY, limitY));
            rt.localScale = new Vector3(Zoom, Zoom, 1f);
            rt.anchoredPosition = _pan;
            foreach (Transform c in _markers) FitNode(c);
            foreach (Transform c in _grid) FitNode(c);
        }

        /// <summary>Counter-scale an overlay object (its top-level node under the markers/grid layer).</summary>
        void Fit(Transform t)
        {
            while (t.parent != null && t.parent != _markers && t.parent != _grid) t = t.parent;
            FitNode(t);
        }

        void FitNode(Transform t) => t.localScale = t.name == "Seg" ? new Vector3(1f, 1f / Zoom, 1f) : new Vector3(1f / Zoom, 1f / Zoom, 1f);

        public void OnScroll(PointerEventData e)
        {
            var frame = (RectTransform)transform.parent;
            if (!RectTransformUtility.ScreenPointToLocalPointInRectangle(frame, e.position, e.pressEventCamera, out var local)) return;
            ZoomAbout(local, Zoom * (e.scrollDelta.y > 0 ? 1.25f : 0.8f));
        }

        public void OnBeginDrag(PointerEventData e) { }

        public void OnDrag(PointerEventData e)
        {
            var frame = (RectTransform)transform.parent;
            _pan += e.delta / Mathf.Max(frame.lossyScale.x, 1e-4f);
            ApplyView();
        }

        /// <summary>Road-atlas grid references around the edge: letters along top and bottom, numbers down the sides.</summary>
        void GridLabels(float size)
        {
            if (Grid == null) return;
            int font = Mathf.Clamp(Mathf.RoundToInt(size / 38f), 12, 26);
            var ink = new Color(0.1f, 0.3f, 0.7f, 1f);
            float inset = font * 0.8f;
            for (int c = 0; c < Grid.Cols; c++)
            {
                var centre = WorldToLocal(ToV2(Grid.CellCentre(c, 0)));
                var r = ((RectTransform)transform).rect;
                foreach (float y in new[] { r.yMax - inset, r.yMin + inset })
                    UiKit.LabelAt(_grid, MapGrid.ColName(c), font, ink, new Vector2(0.5f, 0.5f), new Vector2(centre.x, y), new Vector2(font * 2, font * 1.4f), TextAnchor.MiddleCenter).raycastTarget = false;
            }
            for (int row = 0; row < Grid.Rows; row++)
            {
                var centre = WorldToLocal(ToV2(Grid.CellCentre(0, row)));
                var r = ((RectTransform)transform).rect;
                foreach (float x in new[] { r.xMin + inset, r.xMax - inset })
                    UiKit.LabelAt(_grid, MapGrid.RowName(row), font, ink, new Vector2(0.5f, 0.5f), new Vector2(x, centre.y), new Vector2(font * 2, font * 1.4f), TextAnchor.MiddleCenter).raycastTarget = false;
            }
        }

        static Vector2 ToV2(Vec2 v) => new Vector2(v.X, v.Y);

        public Vector2 WorldToLocal(Vector2 world)
        {
            var r = ((RectTransform)transform).rect;
            var uv = (world - _min) / _size;
            return new Vector2((uv.x - 0.5f) * r.width, (uv.y - 0.5f) * r.height);
        }

        public RectTransform Marker(Vector2 world, Color color, float size, string label = null, int fontSize = 18, bool diamond = false)
        {
            var rt = UiKit.Rect(_markers, "Marker", new Vector2(0.5f, 0.5f), new Vector2(0.5f, 0.5f), new Vector2(0.5f, 0.5f), WorldToLocal(world), new Vector2(size, size));
            Fit(rt);
            var img = rt.gameObject.AddComponent<Image>();
            img.color = color;
            img.raycastTarget = false;
            if (diamond) rt.localRotation = Quaternion.Euler(0, 0, 45);
            var o = rt.gameObject.AddComponent<Outline>();
            o.effectColor = Color.black;
            o.effectDistance = new Vector2(2, -2);
            if (label != null)
            {
                var t = UiKit.LabelAt(_markers, label, fontSize, Color.white, new Vector2(0.5f, 0.5f), WorldToLocal(world) + new Vector2(0, size * 0.5f + 12) / Zoom, new Vector2(260, 30), TextAnchor.MiddleCenter);
                t.raycastTarget = false;
                Fit(t.transform);
            }
            return rt;
        }

        /// <summary>Plain text label (street names) at a world position.</summary>
        /// <summary>Set once street names have been drawn on the static layer (they never change).</summary>
        public bool HasStreetLabels;

        public void Label(Vector2 world, string text, int fontSize, Color color, float rotationDeg = 0f, bool persistent = false)
        {
            var t = UiKit.LabelAt(persistent ? _grid : _markers, text, fontSize, color, new Vector2(0.5f, 0.5f), WorldToLocal(world), new Vector2(260, 26), TextAnchor.MiddleCenter);
            t.raycastTarget = false;
            t.rectTransform.localRotation = Quaternion.Euler(0, 0, rotationDeg);
            Fit(t.transform);
            // Light halo so dark street names read over roads and blocks alike.
            var halo = t.GetComponent<Outline>() ?? t.gameObject.AddComponent<Outline>();
            halo.effectColor = new Color(1f, 1f, 0.95f, 0.85f);
            halo.effectDistance = new Vector2(1.5f, -1.5f);
        }

        /// <summary>Polyline in world XZ (trails, routes).</summary>
        public void Line(IList<Vector2> world, Color color, float width)
        {
            for (int i = 0; i + 1 < world.Count; i++)
            {
                var a = WorldToLocal(world[i]);
                var b = WorldToLocal(world[i + 1]);
                var d = b - a;
                if (d.sqrMagnitude < 0.25f) continue;
                var rt = UiKit.Rect(_markers, "Seg", new Vector2(0.5f, 0.5f), new Vector2(0.5f, 0.5f), new Vector2(0f, 0.5f), a, new Vector2(d.magnitude, width));
                rt.localRotation = Quaternion.Euler(0, 0, Mathf.Atan2(d.y, d.x) * Mathf.Rad2Deg);
                Fit(rt);
                var img = rt.gameObject.AddComponent<Image>();
                img.color = color;
                img.raycastTarget = false;
            }
        }

        public void OnPointerClick(PointerEventData e)
        {
            var rt = (RectTransform)transform;
            if (!RectTransformUtility.ScreenPointToLocalPointInRectangle(rt, e.position, e.pressEventCamera, out var local)) return;
            var r = rt.rect;
            var uv = new Vector2((local.x - r.xMin) / r.width, (local.y - r.yMin) / r.height);
            Clicked?.Invoke(_min + uv * _size, e.button);
        }

        static Color32 Blend(Color32 a, Color32 b, float t) => Color32.Lerp(a, b, t);

        static void Bake(TownBuilder town)
        {
            var net = town.Network;
            const int px = 1024;
            float margin = 60f;
            _min = new Vector2(-margin, -margin);
            _size = new Vector2(net.Size.X + 2 * margin, net.Size.Y + 2 * margin);
            var pixels = new Color32[px * px];
            var grass = new Color32(150, 196, 110, 255);
            for (int i = 0; i < pixels.Length; i++) pixels[i] = grass;

            Vector2Int ToPx(Vec2 w) => new Vector2Int((int)((w.X - _min.x) / _size.x * px), (int)((w.Y - _min.y) / _size.y * px));
            void Disc(int cx, int cy, int r, Color32 c)
            {
                for (int y = -r; y <= r; y++)
                for (int x = -r; x <= r; x++)
                {
                    if (x * x + y * y > r * r) continue;
                    int X = cx + x, Y = cy + y;
                    if (X >= 0 && Y >= 0 && X < px && Y < px) pixels[Y * px + X] = c;
                }
            }
            void FillQuad(Vec2[] q, Color32 c)
            {
                var p = Array.ConvertAll(q, ToPx);
                int minX = Mathf.Max(0, Mathf.Min(Mathf.Min(p[0].x, p[1].x), Mathf.Min(p[2].x, p[3].x)));
                int maxX = Mathf.Min(px - 1, Mathf.Max(Mathf.Max(p[0].x, p[1].x), Mathf.Max(p[2].x, p[3].x)));
                int minY = Mathf.Max(0, Mathf.Min(Mathf.Min(p[0].y, p[1].y), Mathf.Min(p[2].y, p[3].y)));
                int maxY = Mathf.Min(px - 1, Mathf.Max(Mathf.Max(p[0].y, p[1].y), Mathf.Max(p[2].y, p[3].y)));
                for (int y = minY; y <= maxY; y++)
                for (int x = minX; x <= maxX; x++)
                {
                    bool inside = true;
                    for (int k = 0; k < 4 && inside; k++)
                    {
                        var a = p[k]; var b = p[(k + 1) % 4];
                        inside = (b.x - a.x) * (y - a.y) - (b.y - a.y) * (x - a.x) >= 0;
                    }
                    if (inside) pixels[y * px + x] = c;
                }
            }

            foreach (var cell in net.Cells)
            {
                var q = new Vec2[4];
                for (int k = 0; k < 4; k++) q[k] = net.Nodes[cell.Corners[k]].Position;
                Color32 c = cell.District == District.Downtown ? new Color32(214, 204, 196, 255)
                          : cell.District == District.Industrial ? new Color32(186, 188, 196, 255)
                          : cell.District == District.Park ? new Color32(120, 184, 96, 255)
                          : new Color32(222, 222, 170, 255);
                FillQuad(q, c);
            }
            var road = new Color32(70, 74, 88, 255);
            var arterial = new Color32(50, 52, 64, 255);
            foreach (var e in net.Edges)
            {
                var a = ToPx(net.Nodes[e.A].Position);
                var b = ToPx(net.Nodes[e.B].Position);
                int r = e.Class == RoadClass.Arterial ? 5 : 3;
                float len = Vector2.Distance(a, b);
                for (float t = 0; t <= len; t += 0.7f)
                {
                    var p = Vector2.Lerp(a, b, t / Mathf.Max(len, 1f));
                    Disc((int)p.x, (int)p.y, r, e.Class == RoadClass.Arterial ? arterial : road);
                }
            }
            // Grid lines (the callout grid, MapGrid): thin, dark, over everything.
            Grid = new MapGrid(net);
            var gridInk = new Color32(40, 100, 210, 255); // atlas blue, distinct from roads
            for (int c = 0; c <= Grid.Cols; c++)
            {
                int x = ToPx(new Vec2(Grid.Origin.X + c * Grid.CellSize, 0)).x;
                for (int y = 0; y < px; y++)
                    for (int w = -1; w <= 0; w++) if (x + w >= 0 && x + w < px) pixels[y * px + x + w] = Blend(pixels[y * px + x + w], gridInk, 0.7f);
            }
            for (int r = 0; r <= Grid.Rows; r++)
            {
                int y = ToPx(new Vec2(0, Grid.Origin.Y + r * Grid.CellSize)).y;
                for (int x = 0; x < px; x++)
                    for (int w = -1; w <= 0; w++) if (y + w >= 0 && y + w < px) pixels[(y + w) * px + x] = Blend(pixels[(y + w) * px + x], gridInk, 0.7f);
            }
            if (_texture == null) _texture = new Texture2D(px, px, TextureFormat.RGBA32, true) { name = "TownMap", wrapMode = TextureWrapMode.Clamp };
            _texture.SetPixels32(pixels);
            _texture.Apply(true);
            _textureSeed = town.Seed;
        }
    }
}
