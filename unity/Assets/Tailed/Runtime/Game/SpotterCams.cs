using System.Collections.Generic;
using System.Linq;
using Tailed.Cockpit;
using Tailed.Core.Roads;
using Tailed.Map;
using Tailed.Net;
using UnityEngine;
using UnityEngine.InputSystem;

namespace Tailed.Game
{
    /// <summary>
    /// Twice-burned Tails become Spotters (GD §6): no car, but they flick through CCTV cameras mounted
    /// at the big signalised junctions and call out what they see. Plates are judged from the camera.
    /// A/D or ←/→ cycle cameras, W/S zoom.
    /// </summary>
    public sealed class SpotterCams : MonoBehaviour
    {
        public static SpotterCams Instance { get; private set; }
        public bool Active { get; private set; }
        public int Index { get; private set; }
        public readonly List<(Vector3 pos, Quaternion rot, string name)> Cams = new List<(Vector3, Quaternion, string)>();
        float _fov = 55f, _sendTimer;
        int _builtSeed = int.MinValue;

        void Awake() => Instance = this;

        void Build()
        {
            var town = TownBuilder.Instance;
            if (town == null || town.Lanes == null || _builtSeed == town.Seed) return;
            _builtSeed = town.Seed;
            Cams.Clear();
            var net = town.Network;
            var grid = new MapGrid(net);
            int n = 0;
            foreach (var j in town.Lanes.Junctions.Values.OrderBy(x => x.NodeId))
            {
                var node = net.Nodes[j.NodeId];
                if (j.Control != JunctionControl.Signal) continue;
                if (node.Edges.Count(e => net.Edges[e].Class == RoadClass.Arterial) < 3) continue;
                foreach (int e in node.Edges)
                {
                    var d = net.Dir(net.Edges[e], node.Id);
                    var p = node.Position + d * 12f + d.PerpRight * 9f;
                    var target = node.Position + d * 55f;
                    var pos = new Vector3(p.X, 7.5f, p.Y);
                    var rot = Quaternion.LookRotation(new Vector3(target.X, 1f, target.Y) - pos);
                    // Named like a callout: map grid square of what it watches, the street, the direction.
                    Cams.Add((pos, rot, $"CAM {++n:00} · {grid.CellName(target)} · {net.Edges[e].StreetName}, looking {MapGrid.Heading(d)}"));
                }
            }
        }

        static string Compass(Core.Util.Vec2 d) =>
            Mathf.Abs(d.X) > Mathf.Abs(d.Y) ? (d.X > 0 ? "east" : "west") : (d.Y > 0 ? "north" : "south");

        public void SetActive(bool on)
        {
            if (on == Active) return;
            Active = on;
            Build();
            var dir = CameraDirector.Instance;
            if (dir != null) dir.enabled = !on;
            var fly = Camera.main != null ? Camera.main.GetComponent<Cameras.FlyCamera>() : null;
            if (fly != null) fly.enabled = false;
            if (!on && dir != null) dir.SetFree(false);
        }

        void LateUpdate()
        {
            if (!Active || Cams.Count == 0) return;
            var kb = Keyboard.current;
            if (kb != null)
            {
                if (kb.dKey.wasPressedThisFrame || kb.rightArrowKey.wasPressedThisFrame) Index = (Index + 1) % Cams.Count;
                if (kb.aKey.wasPressedThisFrame || kb.leftArrowKey.wasPressedThisFrame) Index = (Index + Cams.Count - 1) % Cams.Count;
                if (kb.wKey.isPressed) _fov = Mathf.Max(12f, _fov - 30f * Time.deltaTime);
                if (kb.sKey.isPressed) _fov = Mathf.Min(70f, _fov + 30f * Time.deltaTime);
            }
            var cam = Camera.main;
            var c = Cams[Index];
            cam.transform.SetPositionAndRotation(c.pos, c.rot);
            cam.fieldOfView = _fov;
            var optics = cam.GetComponent<OpticsCamera>();
            if (optics != null) { optics.Eye = null; optics.UseGazeCone = false; }

            // Tell the host where we're looking so it streams the traffic around this camera.
            if ((_sendTimer -= Time.deltaTime) <= 0f && ClientGame.Instance != null)
            {
                _sendTimer = 0.5f;
                ClientGame.Instance.Send(Msg.SpotterView, w => { w.F32(c.pos.x); w.F32(c.pos.z); }, reliable: false);
            }
        }
    }
}
