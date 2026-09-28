using System.Collections.Generic;
using Tailed.Core.Traffic;
using UnityEngine;

namespace Tailed.Map
{
    /// <summary>
    /// Every traffic light in town is one mesh; lamp states are vertex colours updated from the
    /// simulation (only when something changed). Lamps render unlit with HDR colours, so they bloom.
    /// </summary>
    public sealed class SignalLamps
    {
        public struct Lamp
        {
            public int Node, Edge;
            /// <summary>First vertex of each lamp's front face (<see cref="Sides"/> vertices each).</summary>
            public int Red, Amber, Green, Sides;
        }

        static readonly Color Off = new Color(0.08f, 0.09f, 0.08f);
        static readonly Color RedOn = new Color(4f, 0.35f, 0.25f);
        static readonly Color AmberOn = new Color(4f, 2.2f, 0.2f);
        static readonly Color GreenOn = new Color(0.3f, 4f, 1.2f);

        readonly Mesh _mesh;
        readonly List<Lamp> _lamps;
        readonly Color[] _colors;
        readonly SignalState[] _state;

        public SignalLamps(Mesh mesh, List<Lamp> lamps)
        {
            _mesh = mesh;
            _lamps = lamps;
            _colors = mesh.colors;
            _state = new SignalState[lamps.Count];
            for (int i = 0; i < _state.Length; i++) _state[i] = (SignalState)255;
        }

        public void Refresh(System.Func<int, int, SignalState> stateOf)
        {
            bool dirty = false;
            for (int i = 0; i < _lamps.Count; i++)
            {
                var l = _lamps[i];
                var st = stateOf(l.Node, l.Edge);
                if (st == _state[i]) continue;
                _state[i] = st;
                dirty = true;
                Fill(l.Red, l.Sides, st == SignalState.Red ? RedOn : Off);
                Fill(l.Amber, l.Sides, st == SignalState.Amber ? AmberOn : Off);
                Fill(l.Green, l.Sides, st == SignalState.Green ? GreenOn : Off);
            }
            if (dirty) _mesh.SetColors(_colors);
        }

        public string Describe()
        {
            int red = 0, amber = 0, green = 0, other = 0;
            foreach (var st in _state) { if (st == SignalState.Red) red++; else if (st == SignalState.Amber) amber++; else if (st == SignalState.Green) green++; else other++; }
            var sample = _lamps.Count > 0 ? _colors[_lamps[0].Red] : default;
            var meshColors = _mesh.colors;
            int lit = 0;
            foreach (var l in _lamps) if (meshColors[l.Red].r > 1f || meshColors[l.Amber].r > 1f || meshColors[l.Green].g > 1f) lit++;
            var r = UnityEngine.Object.FindObjectsByType<MeshFilter>(FindObjectsSortMode.None);
            int users = 0; foreach (var mf in r) if (mf.sharedMesh == _mesh) users++;
            return $"lamps={_lamps.Count} red={red} amber={amber} green={green} unset={other} litInMesh={lit} meshUsers={users} name={_mesh.name}";
        }

        void Fill(int start, int count, Color c)
        {
            for (int k = 0; k < count; k++) _colors[start + k] = c;
        }
    }
}
