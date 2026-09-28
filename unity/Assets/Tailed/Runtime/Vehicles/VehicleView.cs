using System.Collections.Generic;
using Tailed.Core.Identity;
using Tailed.Core.Traffic;
using UnityEngine;

namespace Tailed.Vehicles
{
    /// <summary>Shared materials for vehicles, created once from shaders found at runtime.</summary>
    public static class VehicleMaterials
    {
        static Material _body, _glass, _plate, _driver;
        public static Material Body => _body ? _body : _body = Make("Tailed/ToonVehicle");
        public static Material Glass => _glass ? _glass : _glass = Make("Tailed/ToonGlass");
        public static Material Driver => _driver ? _driver : _driver = Make("Tailed/Toon");
        public static Material Plate
        {
            get
            {
                if (_plate) return _plate;
                _plate = Make("Tailed/Plate");
                _plate.SetTexture("_GlyphAtlas", Glyphs.SdfAtlas);
                return _plate;
            }
        }

        static Material Make(string shader)
        {
            var s = Shader.Find(shader);
            if (s == null) throw new System.InvalidOperationException($"Shader {shader} missing (add it to Always Included Shaders)");
            return new Material(s) { enableInstancing = true };
        }
    }

    /// <summary>
    /// Visual + kinematic collider for a simulated vehicle (NPC or remote player). The pose comes from
    /// outside; this adds suspension "juice" (pitch under braking, roll in turns, bounce) and lamps.
    /// </summary>
    public sealed class VehicleView : MonoBehaviour
    {
        public int VehicleId { get; private set; } = -1;
        /// <summary>Every bound, visible vehicle view (plate readouts look here).</summary>
        public static readonly HashSet<VehicleView> Active = new HashSet<VehicleView>();
        public VehicleIdentity Identity { get; private set; }

        MeshFilter _body, _driver, _plate;
        MeshRenderer _bodyRenderer;
        Rigidbody _rb;
        BoxCollider _box;
        Transform _chassis;
        MaterialPropertyBlock _mpb;
        static readonly int LightsId = Shader.PropertyToID("_Lights");
        float _pitch, _roll, _pitchVel, _rollVel, _prevSpeed, _prevYaw, _yawOffset;
        Vector3 _lastPos;
        bool _hasLastPos;
        VehicleFlags _flags;
        AudioSource _horn;
        ErrandMarker _errand;
        public static float Headlights; // 0 day .. 1 night, set by the environment
        /// <summary>Last rendered speed and acceleration (engine sound).</summary>
        public float Speed => _prevSpeed;
        public float Accel { get; private set; }

        public static VehicleView Create(Transform parent)
        {
            var go = new GameObject("Vehicle");
            go.transform.SetParent(parent, false);
            var v = go.AddComponent<VehicleView>();
            v._chassis = new GameObject("Chassis").transform;
            v._chassis.SetParent(go.transform, false);
            v._body = v.Child("Body", VehicleMaterials.Body, VehicleMaterials.Glass);
            v._bodyRenderer = v._body.GetComponent<MeshRenderer>();
            v._driver = v.Child("Driver", VehicleMaterials.Driver);
            v._plate = v.Child("Plates", VehicleMaterials.Plate);
            v._rb = go.AddComponent<Rigidbody>();
            v._rb.isKinematic = true;
            v._rb.interpolation = RigidbodyInterpolation.None;
            v._box = go.AddComponent<BoxCollider>();
            v._mpb = new MaterialPropertyBlock();
            return v;
        }

        MeshFilter Child(string name, params Material[] mats)
        {
            var go = new GameObject(name);
            go.transform.SetParent(_chassis, false);
            var mf = go.AddComponent<MeshFilter>();
            var mr = go.AddComponent<MeshRenderer>();
            mr.sharedMaterials = mats;
            return mf;
        }

        public void Bind(int vehicleId, VehicleIdentity identity)
        {
            VehicleId = vehicleId;
            if (!identity.Equals(Identity) || _body.sharedMesh == null)
            {
                Identity = identity;
                var shape = VehicleMeshFactory.Shape(identity.ModelId);
                _body.sharedMesh = VehicleMeshFactory.Body(identity.ModelId, identity.ColorId);
                _driver.sharedMesh = VehicleMeshFactory.Driver(identity);
                _driver.transform.localPosition = new Vector3(shape.Eye.x, shape.Eye.y - 0.62f, shape.Eye.z);
                if (_plate.sharedMesh != null) Destroy(_plate.sharedMesh);
                _plate.sharedMesh = Glyphs.PlateMesh(identity.Plate, shape.FrontPlate, shape.RearPlate);
                _box.center = new Vector3(0f, (shape.Clearance + shape.Height) * 0.5f, 0f);
                _box.size = new Vector3(shape.Width, shape.Height - shape.Clearance, shape.Length);
            }
            _pitch = _roll = _pitchVel = _rollVel = _yawOffset = 0f;
            _hasLastPos = false;
            _driver.gameObject.SetActive(true);
            gameObject.name = $"Vehicle {vehicleId} {identity.Plate}";
            gameObject.SetActive(true);
            Active.Add(this);
        }

        /// <summary>Parked decor: nobody at the wheel.</summary>
        public void HideDriver() => _driver.gameObject.SetActive(false);

        public void Release()
        {
            if (_errand != null) _errand.Show(false);
            VehicleId = -1;
            Active.Remove(this);
            gameObject.SetActive(false);
        }

        /// <summary>
        /// Place the vehicle (ground position + heading) and animate body motion. With
        /// <paramref name="steerFromMotion"/> (lane-graph vehicles) the body turns towards where it's
        /// actually going, so lane wander and lane changes read as steering rather than sliding.
        /// </summary>
        public void SetPose(Vector3 position, Vector3 forward, float speed, VehicleFlags flags, float dt, bool steerFromMotion = true)
        {
            if (steerFromMotion && dt > 0f)
            {
                float target = 0f;
                var d = position - _lastPos;
                d.y = 0f;
                float moved = d.magnitude;
                if (_hasLastPos && speed > 0.8f && moved > 0.004f && moved < 3f)
                {
                    float a = Vector3.SignedAngle(forward, d, Vector3.up);
                    if (Mathf.Abs(a) < 25f) target = Mathf.Clamp(a, -14f, 14f);
                }
                _yawOffset = Mathf.Lerp(_yawOffset, target, 1f - Mathf.Exp(-dt * 6f));
                forward = Quaternion.Euler(0f, _yawOffset, 0f) * forward;
            }
            _lastPos = position;
            _hasLastPos = true;
            var rot = Quaternion.LookRotation(forward, Vector3.up);
            _rb.MovePosition(position);
            _rb.MoveRotation(rot);
            transform.SetPositionAndRotation(position, rot);

            if (dt > 0f)
            {
                float accel = (speed - _prevSpeed) / dt;
                Accel = Mathf.Lerp(Accel, accel, 1f - Mathf.Exp(-dt * 4f));
                float yaw = rot.eulerAngles.y;
                float yawRate = Mathf.DeltaAngle(_prevYaw, yaw) / dt;
                _prevSpeed = speed;
                _prevYaw = yaw;
                // Springy body: nose dives when braking, leans out of turns. Exaggerated on purpose.
                float targetPitch = Mathf.Clamp(accel * 0.6f, -3.5f, 3.5f);
                float targetRoll = Mathf.Clamp(yawRate * speed * 0.004f, -4f, 4f);
                Spring(ref _pitch, ref _pitchVel, targetPitch, dt);
                Spring(ref _roll, ref _rollVel, targetRoll, dt);
                float bounce = Mathf.Sin(Time.time * 9f + VehicleId) * Mathf.Min(speed, 12f) * 0.0012f;
                _chassis.localRotation = Quaternion.Euler(_pitch, 0f, _roll);
                _chassis.localPosition = new Vector3(0f, bounce, 0f);
            }

            bool errand = (flags & VehicleFlags.Errand) != 0;
            if (errand && _errand == null) _errand = ErrandMarker.Attach(transform, VehicleMeshFactory.Shape(Identity.ModelId).Height);
            if (_errand != null) _errand.Show(errand);

            bool honking = (flags & VehicleFlags.Horn) != 0;
            if (honking && _horn == null) { _horn = Tailed.Audio.Sfx.Source(gameObject, true, 90f); _horn.clip = Tailed.Audio.Sfx.Horn; _horn.loop = true; }
            if (_horn != null && honking != _horn.isPlaying) { if (honking) _horn.Play(); else _horn.Stop(); }

            if (flags != _flags || (flags & (VehicleFlags.IndicateLeft | VehicleFlags.IndicateRight | VehicleFlags.Hazard)) != 0 || Headlights > 0f)
            {
                _flags = flags;
                bool blink = (Time.time * 1.6f) % 1f < 0.5f;
                bool hazard = (flags & VehicleFlags.Hazard) != 0;
                float brake = (flags & VehicleFlags.Brake) != 0 ? 1f : Headlights * 0.35f;
                float left = blink && (hazard || (flags & VehicleFlags.IndicateLeft) != 0) ? 1f : 0f;
                float right = blink && (hazard || (flags & VehicleFlags.IndicateRight) != 0) ? 1f : 0f;
                _mpb.SetVector(LightsId, new Vector4(Headlights, brake, left, right));
                _bodyRenderer.SetPropertyBlock(_mpb);
            }
        }

        static void Spring(ref float x, ref float v, float target, float dt)
        {
            const float k = 60f, damping = 9f;
            v += ((target - x) * k - v * damping) * dt;
            x += v * dt;
        }
    }
}
