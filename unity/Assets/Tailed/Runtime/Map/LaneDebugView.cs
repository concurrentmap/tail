using System.Collections.Generic;
using Tailed.Core.Roads;
using Tailed.Core.Util;
using UnityEngine;
using UnityEngine.InputSystem;

namespace Tailed.Map
{
    /// <summary>
    /// Line overlay of the lane graph. F1 toggles. Lanes: blue (arterial) / cyan (local) with an
    /// arrowhead; connectors: white straight, orange left, green right; stop lines red, signals yellow.
    /// </summary>
    public sealed class LaneDebugView : MonoBehaviour
    {
        static readonly Color ArterialLane = new Color(0.2f, 0.4f, 1f);
        static readonly Color LocalLane = new Color(0.1f, 0.9f, 1f);
        static readonly Color Straight = Color.white;
        static readonly Color Left = new Color(1f, 0.55f, 0.1f);
        static readonly Color Right = new Color(0.3f, 1f, 0.3f);
        static readonly Color StopTick = new Color(1f, 0.1f, 0.1f);
        static readonly Color SignalTick = new Color(1f, 0.9f, 0.1f);
        const float Y = 0.3f;

        public static bool Visible { get; private set; }
        MeshRenderer _renderer;

        public void Build(LaneGraph g, Material material)
        {
            var verts = new List<Vector3>();
            var colors = new List<Color>();
            void Line(Vec2 a, Vec2 b, Color c)
            {
                verts.Add(new Vector3(a.X, Y, a.Y)); colors.Add(c);
                verts.Add(new Vector3(b.X, Y, b.Y)); colors.Add(c);
            }

            foreach (var l in g.Lanes)
            {
                var c = l.Class == RoadClass.Arterial ? ArterialLane : LocalLane;
                Line(l.Start, l.End, c);
                Vec2 back = l.End - l.Direction * 2f;
                Line(l.End, back + l.Direction.PerpLeft * 0.8f, c);
                Line(l.End, back + l.Direction.PerpRight * 0.8f, c);
                if (l.Control != ApproachControl.Free)
                {
                    Vec2 r = l.Direction.PerpRight * (l.Width * 0.5f);
                    Line(l.End - r, l.End + r, l.Control == ApproachControl.Stop ? StopTick : SignalTick);
                }
            }
            foreach (var con in g.Connectors)
            {
                var c = con.Turn == TurnType.Left ? Left : con.Turn == TurnType.Right ? Right : Straight;
                for (int i = 0; i + 1 < con.Points.Length; i++) Line(con.Points[i], con.Points[i + 1], c);
            }

            var mesh = new Mesh { name = "LaneDebug", indexFormat = UnityEngine.Rendering.IndexFormat.UInt32 };
            mesh.SetVertices(verts);
            mesh.SetColors(colors);
            var idx = new int[verts.Count];
            for (int i = 0; i < idx.Length; i++) idx[i] = i;
            mesh.SetIndices(idx, MeshTopology.Lines, 0);
            gameObject.AddComponent<MeshFilter>().sharedMesh = mesh;
            _renderer = gameObject.AddComponent<MeshRenderer>();
            _renderer.sharedMaterial = material;
            _renderer.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            _renderer.enabled = Visible;
        }

        void Update()
        {
            if (Keyboard.current != null && Keyboard.current.f1Key.wasPressedThisFrame) Toggle();
            if (_renderer.enabled != Visible) _renderer.enabled = Visible;
        }

        /// <summary>Bridge: tools/bridge.sh execute Tailed.Map.LaneDebugView.Toggle</summary>
        public static string Toggle()
        {
            Visible = !Visible;
            return $"lane debug {(Visible ? "on" : "off")}";
        }
    }
}
