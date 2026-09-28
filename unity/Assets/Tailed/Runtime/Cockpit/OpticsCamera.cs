using UnityEngine;

namespace Tailed.Cockpit
{
    /// <summary>
    /// Per-camera optical parameters for the plate shader (architecture §9.3). Cameras without this
    /// component use their own position as the eye, magnification 1, no gaze cone.
    /// </summary>
    public sealed class OpticsCamera : MonoBehaviour
    {
        /// <summary>Where the driver's eye is. Null = this camera's position.</summary>
        public Transform Eye;
        public float Magnification = 1f;
        /// <summary>cos(half-angle) of the driver's field of view around <see cref="Eye"/>.forward (may be negative).</summary>
        public float GazeCone;
        /// <summary>Apply <see cref="GazeCone"/> (third person: the camera isn't where the eyes are).</summary>
        public bool UseGazeCone;
        /// <summary>This camera renders a mirror view (its image is shown reversed).</summary>
        public bool IsMirror;
    }
}
