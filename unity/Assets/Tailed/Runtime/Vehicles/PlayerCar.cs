using Tailed.Core.Identity;
using Tailed.Core.Traffic;
using Tailed.Core.Util;
using Tailed.Map;
using Tailed.Traffic;
using UnityEngine;

namespace Tailed.Vehicles
{
    /// <summary>
    /// Physics car for the local player: raycast suspension, simple tyre model, arcade assists.
    /// Heavy enough to feel like a car, forgiving enough for friends who don't drive games.
    /// Inputs are written by <see cref="CarInput"/> or <see cref="Autopilot"/>.
    /// </summary>
    [RequireComponent(typeof(Rigidbody))]
    public sealed class PlayerCar : MonoBehaviour
    {
        // Inputs.
        [Range(-1, 1)] public float Steer;
        [Range(0, 1)] public float Throttle, Brake;
        public bool Handbrake, Horn;
        public bool IndicateLeft, IndicateRight, Hazards;
        /// <summary>Holding brake at a standstill reverses (human controls). Off for the autopilot.</summary>
        public bool ReverseOnBrake = true;
        /// <summary>Windows down: your voice (and theirs) carries further (Y toggles).</summary>
        public bool WindowsOpen;
        /// <summary>Mark only, set by the host: briefly on at a real stop (the gold errand marker).</summary>
        public bool Errand;

        public VehicleIdentity Identity { get; private set; }
        public CarShape Shape { get; private set; }
        public Transform Eye { get; private set; }
        /// <summary>Id of this car's external vehicle in the traffic sim (-1 if none).</summary>
        public int SimId { get; set; } = -1;
        public float ForwardSpeed { get; private set; }
        public VehicleFlags Flags { get; private set; }
        public bool Grounded { get; private set; }

        public static PlayerCar Local { get; private set; }

        Rigidbody _rb;
        Vector3[] _wheels;
        float[] _compression;
        MeshFilter _bodyFilter, _plateFilter;
        AudioSource _hornSource, _fxSource;
        bool _lastBlink;
        float _engineForce, _topSpeed;

        const float Mass = 1350f, SpringRest = 0.32f, MaxSteerDeg = 34f;

        public static PlayerCar Create(VehicleIdentity identity, Vector3 position, Quaternion rotation, bool local)
        {
            var go = new GameObject(local ? "PlayerCar (local)" : "PlayerCar");
            go.layer = Layers.PlayerCar;
            go.transform.SetPositionAndRotation(position, rotation);
            var rb = go.AddComponent<Rigidbody>();
            rb.mass = Mass;
            rb.interpolation = RigidbodyInterpolation.Interpolate;
            rb.collisionDetectionMode = CollisionDetectionMode.ContinuousDynamic;
            rb.linearDamping = 0.02f;
            rb.angularDamping = 0.6f;
            var car = go.AddComponent<PlayerCar>();
            car.SetIdentity(identity);
            if (local)
            {
                Local = car;
                // Own GameObject: OnAudioFilterRead replaces the output of the AudioSource beside it.
                var engine = new GameObject("Engine");
                engine.transform.SetParent(go.transform, false);
                engine.AddComponent<AudioSource>();
                engine.AddComponent<Tailed.Audio.EngineAudio>().Car = car;
                car._hornSource = Tailed.Audio.Sfx.Source(go, false);
                car._hornSource.clip = Tailed.Audio.Sfx.Horn;
                car._hornSource.loop = true;
                car._fxSource = Tailed.Audio.Sfx.Source(go, false);
            }
            return car;
        }

        /// <summary>Change model/colour/plate in place (car swap, plate swap, chop shop).</summary>
        public void SetIdentity(VehicleIdentity identity)
        {
            Identity = identity;
            Shape = VehicleMeshFactory.Shape(identity.ModelId);
            var model = VehicleCatalog.Models[identity.ModelId];
            _engineForce = 6200f * model.AccelFactor;
            _topSpeed = 32f * model.TopSpeedFactor;
            _rb = GetComponent<Rigidbody>();
            _rb.centerOfMass = new Vector3(0f, Shape.Clearance + 0.15f, 0.1f);

            if (_bodyFilter == null)
            {
                _bodyFilter = MakeChild("Body", VehicleMaterials.Body, VehicleMaterials.Glass);
                _plateFilter = MakeChild("Plates", VehicleMaterials.Plate);
                Eye = new GameObject("Eye").transform;
                Eye.SetParent(transform, false);
                gameObject.AddComponent<BoxCollider>();
            }
            if (_bodyFilter.sharedMesh != null) Destroy(_bodyFilter.sharedMesh);
            _bodyFilter.sharedMesh = VehicleMeshFactory.PlayerBody(identity.ModelId, identity.ColorId);
            if (_plateFilter.sharedMesh != null) Destroy(_plateFilter.sharedMesh);
            _plateFilter.sharedMesh = Glyphs.PlateMesh(identity.Plate, Shape.FrontPlate, Shape.RearPlate);
            Eye.localPosition = Shape.Eye;

            var box = GetComponent<BoxCollider>();
            float bottom = Shape.Clearance + 0.12f; // clears kerbs
            box.center = new Vector3(0f, (bottom + Shape.Height) * 0.5f, 0f);
            box.size = new Vector3(Shape.Width, Shape.Height - bottom, Shape.Length);

            float hx = Shape.Width * 0.5f - 0.15f, hz = Shape.WheelBase * 0.5f;
            _wheels = new[] { new Vector3(-hx, 0, hz), new Vector3(hx, 0, hz), new Vector3(-hx, 0, -hz), new Vector3(hx, 0, -hz) };
            _compression = new float[4];
        }

        MeshFilter MakeChild(string name, params Material[] mats)
        {
            var go = new GameObject(name);
            go.transform.SetParent(transform, false);
            go.layer = gameObject.layer;
            var mf = go.AddComponent<MeshFilter>();
            go.AddComponent<MeshRenderer>().sharedMaterials = mats;
            return mf;
        }

        void FixedUpdate()
        {
            float dt = Time.fixedDeltaTime;
            var up = transform.up;
            ForwardSpeed = Vector3.Dot(_rb.linearVelocity, transform.forward);
            float speed = _rb.linearVelocity.magnitude;
            float steerDeg = Steer * MaxSteerDeg * Mathf.Lerp(1f, 0.35f, Mathf.Clamp01(speed / 28f));
            int grounded = 0;

            // Reverse when holding brake at a standstill.
            bool reversing = ReverseOnBrake && Brake > 0.1f && ForwardSpeed < 0.8f && Throttle < 0.1f;
            float drive = reversing ? -Brake * 0.5f : Throttle;
            float brake = reversing ? 0f : Brake;

            for (int i = 0; i < 4; i++)
            {
                bool front = i < 2, rear = !front;
                float r = Shape.WheelRadius;
                var mount = transform.TransformPoint(_wheels[i] + Vector3.up * (r + SpringRest));
                float rayLen = SpringRest + r;
                if (!Physics.Raycast(mount, -up, out var hit, rayLen, ~(1 << Layers.PlayerCar | 1 << Layers.Vehicles), QueryTriggerInteraction.Ignore))
                {
                    _compression[i] = 0f;
                    continue;
                }
                grounded++;
                float compression = 1f - (hit.distance - r) / SpringRest;
                float compVel = (compression - _compression[i]) / dt;
                _compression[i] = compression;
                float load = Mathf.Max(0f, compression * 42000f + compVel * 3000f);
                _rb.AddForceAtPosition(up * load, hit.point);

                var wheelRot = front ? Quaternion.AngleAxis(steerDeg, up) : Quaternion.identity;
                var fwd = wheelRot * transform.forward;
                var side = Vector3.Cross(up, fwd);
                var vel = _rb.GetPointVelocity(hit.point);
                float vLong = Vector3.Dot(vel, fwd), vLat = Vector3.Dot(vel, side);

                // Lateral grip: cancel sideways slip, limited by friction (less on the handbrake).
                float grip = (rear && Handbrake) ? 0.35f : 1.15f;
                float maxLat = grip * load;
                float lat = Mathf.Clamp(-vLat * Mass * 0.25f / dt * 0.9f, -maxLat, maxLat);

                // Longitudinal: engine on all wheels (front-biased), brakes, rolling resistance.
                float engine = drive * _engineForce * (front ? 0.3f : 0.2f) * Mathf.Clamp01(1f - Mathf.Abs(ForwardSpeed) / _topSpeed);
                float braking = -Mathf.Sign(vLong) * Mathf.Min(Mathf.Abs(vLong) * Mass * 0.25f / dt, (brake * 3600f) + (Handbrake && rear ? 3000f : 0f) + 60f);
                float longF = Mathf.Clamp(engine + braking, -1.2f * load, 1.2f * load);

                _rb.AddForceAtPosition(side * lat + fwd * longF, hit.point);
            }
            Grounded = grounded >= 2;

            // Arcade assists: keep upright, damp yaw wobble, a little downforce.
            if (Grounded) _rb.AddForce(-up * speed * 18f);
            var tilt = Vector3.Cross(up, Vector3.up);
            _rb.AddTorque(tilt * 9000f - _rb.angularVelocity * 400f * (Grounded ? 0.2f : 1f));

            UpdateFlags();
            if (SimId >= 0 && TrafficRunner.Instance != null && TrafficRunner.Instance.Sim != null)
            {
                var p = transform.position;
                var f = transform.forward;
                TrafficRunner.Instance.Sim.UpdateExternal(SimId, new Vec2(p.x, p.z), new Vec2(f.x, f.z), speed, Flags);
            }
        }

        void UpdateFlags()
        {
            var flags = VehicleFlags.None;
            // Foot on the brake, including when held at a standstill (as every NPC does at a light).
            bool reversing = ReverseOnBrake && Brake > 0.1f && ForwardSpeed < 0.8f && Throttle < 0.1f;
            if ((Brake > 0.1f && ForwardSpeed > 0.3f) || (Mathf.Abs(ForwardSpeed) < 0.3f && Throttle < 0.1f && !reversing)) flags |= VehicleFlags.Brake;
            if (reversing && ForwardSpeed < -0.1f) flags |= VehicleFlags.Reverse;
            if (IndicateLeft) flags |= VehicleFlags.IndicateLeft;
            if (IndicateRight) flags |= VehicleFlags.IndicateRight;
            if (Hazards) flags |= VehicleFlags.Hazard;
            if (Horn) flags |= VehicleFlags.Horn;
            if (WindowsOpen) flags |= VehicleFlags.WindowsOpen;
            if (Errand) flags |= VehicleFlags.Errand;
            Flags = flags;
            var mr = _bodyFilter.GetComponent<MeshRenderer>();
            bool blink = (Time.time * 1.6f) % 1f < 0.5f;
            if (_hornSource != null && Horn != _hornSource.isPlaying) { if (Horn) _hornSource.Play(); else _hornSource.Stop(); }
            bool anyIndicator = IndicateLeft || IndicateRight || Hazards;
            if (_fxSource != null && anyIndicator && blink != _lastBlink) _fxSource.PlayOneShot(Tailed.Audio.Sfx.Tick, 0.5f);
            _lastBlink = blink;
            var mpb = new MaterialPropertyBlock();
            mpb.SetVector("_Lights", new Vector4(VehicleView.Headlights,
                (flags & VehicleFlags.Brake) != 0 ? 1f : VehicleView.Headlights * 0.35f,
                blink && (IndicateLeft || Hazards) ? 1f : 0f,
                blink && (IndicateRight || Hazards) ? 1f : 0f));
            mr.SetPropertyBlock(mpb);
        }

        void OnCollisionEnter(Collision c)
        {
            if (_fxSource != null && c.impulse.magnitude > 800f) _fxSource.PlayOneShot(Tailed.Audio.Sfx.Thump, Mathf.Clamp01(c.impulse.magnitude / 8000f));
            var view = c.collider.GetComponentInParent<VehicleView>();
            if (view != null && TrafficRunner.Instance != null) TrafficRunner.Instance.ReportImpact(view.VehicleId, c.impulse.magnitude);
            ImpactNotifier?.Invoke(c);
        }

        /// <summary>Hook for networking: remote/NPC impacts get forwarded to the host.</summary>
        public static System.Action<Collision> ImpactNotifier;

        /// <summary>Teleport safely (spawns, chop shop).</summary>
        public void Place(Vector3 position, Quaternion rotation)
        {
            _rb.position = position;
            _rb.rotation = rotation;
            transform.SetPositionAndRotation(position, rotation);
            _rb.linearVelocity = Vector3.zero;
            _rb.angularVelocity = Vector3.zero;
        }
    }
}
