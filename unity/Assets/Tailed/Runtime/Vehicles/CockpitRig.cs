using Tailed.Core.Identity;
using Tailed.Map;
using UnityEngine;

namespace Tailed.Vehicles
{
    /// <summary>
    /// The live bits of the local car's cockpit: a steering wheel that turns with the input, floating
    /// mitten hands on it (PEAK-style, no arms) that shuffle round the rim, hit the horn and flick the
    /// indicator stalk, working speedo and rev needles, and a dashboard bobblehead that wobbles with
    /// the car. Everything is built from the car's shape, so it fits every model.
    /// </summary>
    public sealed class CockpitRig : MonoBehaviour
    {
        const float RimRadius = 0.17f, MaxWheelDeg = 140f, MaxHandDeg = 80f, HandScale = 0.72f;
        /// <summary>Dev screenshots: hold the wheel / horn regardless of input.</summary>
        public static float? DebugSteer
        {
            get => Time.realtimeSinceStartup < _debugUntil ? _debugSteer : null;
            set { _debugSteer = value; _debugUntil = value.HasValue ? Time.realtimeSinceStartup + 20f : 0f; }
        }
        public static bool DebugHorn;
        static float? _debugSteer;
        static float _debugUntil;

        PlayerCar _car;
        Transform _wheel, _left, _right, _speedNeedle, _revNeedle, _bobHead;
        float _angle, _hornBlend, _flickL, _flickR, _rev;
        bool _wasLeft, _wasRight;
        Vector3 _bob, _bobVel, _lastVel;

        static readonly Color Rim = new Color(0.14f, 0.14f, 0.16f), Hub = new Color(0.25f, 0.25f, 0.28f);
        static readonly Color Dial = new Color(0.95f, 0.94f, 0.9f), Needle = new Color(0.95f, 0.3f, 0.15f);
        static readonly Color GripTone = new Color(0.22f, 0.2f, 0.19f), Shroud = new Color(0.16f, 0.16f, 0.18f), Bezel = new Color(0.8f, 0.82f, 0.85f);

        public static CockpitRig Build(PlayerCar car)
        {
            var old = car.transform.Find("CockpitRig");
            if (old != null) Destroy(old.gameObject);
            var go = new GameObject("CockpitRig");
            go.transform.SetParent(car.transform, false);
            var rig = go.AddComponent<CockpitRig>();
            rig._car = car;
            rig.Assemble();
            return rig;
        }

        void Assemble()
        {
            var s = _car.Shape;
            var id = _car.Identity;
            // A real driving position: wheel a forearm ahead and well below the eye, rim leaning back
            // towards the driver, so its top half and your hands sit at the bottom of the view.
            float dashZ = s.CabinFront - 0.55f;
            var centre = new Vector3(s.Eye.x, s.Eye.y - 0.28f, Mathf.Min(s.Eye.z + 0.34f, dashZ - 0.1f));
            // The rim stands upright (face level, not tipped down towards the lap).
            transform.localPosition = centre;
            transform.localRotation = Quaternion.LookRotation(Vector3.forward, Vector3.up);
            var up = transform.localRotation * Vector3.up;

            // Column into the dash (doesn't turn): a round shroud running forward and down (the local frame
            // leans back with the rim), with an indicator stalk on the left and a wiper stalk on the right.
            var column = new MeshBuilder();
            var colDir = Quaternion.Inverse(transform.localRotation) * new Vector3(0f, -0.4f, 0.92f).normalized; // car space → rim space
            column.Capsule(new Vector3(0f, 0f, 0.03f), colDir * 0.34f, 0.028f, Hub);
            column.Capsule(colDir * 0.06f + new Vector3(0f, 0f, 0.03f), colDir * 0.16f, 0.048f, Shroud);
            foreach (float side in new[] { -1f, 1f })
            {
                var root = colDir * 0.08f + new Vector3(side * 0.04f, 0.01f, 0.04f);
                var tip = root + new Vector3(side * 0.1f, 0.012f, -0.01f);
                column.Capsule(root, tip, 0.007f, Rim, 8);
                column.SmoothSphere(tip, new Vector3(0.012f, 0.012f, 0.012f), side < 0 ? Needle : Rim, 10, 7);
            }
            Part("Column", column);

            // The wheel: a fat, round, toy-like rim with grippy thumb rests, three rounded spokes, a
            // padded hub and a horn pad in the car's own colour with a little badge.
            var wheel = new MeshBuilder();
            wheel.Torus(Vector3.zero, RimRadius, 0.022f, Rim, 56, 14);
            foreach (float deg in new[] { 0f, 180f })
                wheel.Torus(Vector3.zero, RimRadius, 0.0245f, GripTone, 8, 14, (deg - 14f) * Mathf.Deg2Rad, (deg + 14f) * Mathf.Deg2Rad);
            // Stitched twelve o'clock marker, so the rotation reads at a glance.
            wheel.Torus(Vector3.zero, RimRadius, 0.0235f, new Color(0.98f, 0.78f, 0.2f), 6, 14, 84f * Mathf.Deg2Rad, 96f * Mathf.Deg2Rad);
            foreach (float deg in new[] { 0f, 180f, 270f })
            {
                float a = deg * Mathf.Deg2Rad;
                var dir = new Vector3(Mathf.Cos(a), Mathf.Sin(a), 0f);
                wheel.Capsule(dir * 0.05f + Vector3.forward * 0.012f, dir * (RimRadius - 0.012f) + Vector3.forward * 0.004f, deg == 270f ? 0.016f : 0.013f, Hub, 10);
            }
            wheel.SmoothSphere(new Vector3(0f, -0.004f, 0.01f), new Vector3(0.072f, 0.062f, 0.034f), Hub, 20, 12);
            var paint = VehicleMeshFactory.BodyColor(id.ColorId);
            wheel.SmoothSphere(new Vector3(0f, -0.002f, -0.014f), new Vector3(0.052f, 0.043f, 0.022f), paint, 20, 12);
            wheel.Torus(new Vector3(0f, 0.004f, -0.034f), 0.011f, 0.0035f, Bezel, 16, 6);
            wheel.SmoothSphere(new Vector3(0f, 0.004f, -0.035f), new Vector3(0.009f, 0.009f, 0.004f), new Color(0.98f, 0.78f, 0.2f), 10, 6);
            _wheel = Part("Wheel", wheel);

            var skin = VehicleMeshFactory.SkinTones[id.Skin % VehicleMeshFactory.SkinTones.Length];
            var shirt = VehicleMeshFactory.BodyColor(id.ShirtColorId % VehicleCatalog.Colors.Length);
            _left = Part("HandL", Hand(+1f, skin, shirt));
            _right = Part("HandR", Hand(-1f, skin, shirt));
            _left.localScale = _right.localScale = Vector3.one * HandScale;

            BuildGauges(s, centre, up, dashZ);
            BuildBobblehead(s, id);
        }


        /// <summary>Mitten hand in grip frame: x along the rim, y radially out, z towards the dash. Thumb towards +x·thumb.</summary>
        static MeshBuilder Hand(float thumb, Color skin, Color shirt)
        {
            // One chunky mitten wrapped round the rim (no fingers), a thumb nub, and a shirt cuff.
            var mb = new MeshBuilder();
            mb.SmoothSphere(new Vector3(0f, 0.004f, -0.004f), new Vector3(0.068f, 0.05f, 0.056f), skin, 18, 12);
            mb.SmoothSphere(new Vector3(thumb * 0.05f, 0.032f, -0.03f), new Vector3(0.02f, 0.02f, 0.028f), skin, 10, 8);
            mb.SmoothSphere(new Vector3(0f, -0.045f, -0.045f), new Vector3(0.034f, 0.03f, 0.03f), shirt, 12, 8);
            return mb;
        }

        void BuildGauges(CarShape s, Vector3 wheel, Vector3 up, float dashZ)
        {
            // Put the dials where the driver actually sees them: on the sight line from the eye through
            // the upper opening of the wheel (between hub and rim), at the dash face. The wheel then
            // frames them instead of covering them, whatever the car's proportions.
            var eye = s.Eye;
            var through = wheel + up * 0.1f;
            var dir = through - eye;
            var dial = eye + dir * ((dashZ - 0.045f - eye.z) / dir.z);
            var root = new GameObject("Gauges").transform;
            root.SetParent(transform.parent, false);
            root.localPosition = dial;
            root.localRotation = Quaternion.LookRotation(dial - eye, Vector3.up);
            var mb = new MeshBuilder();
            var housing = new Color(0.1f, 0.1f, 0.12f);
            float drop = Mathf.Clamp(dial.y - (s.Belt + 0.03f), 0.065f, 0.1f); // just the pod: it sits on the dash top
            mb.LocalBox(new Vector3(-0.125f, -drop, 0.0f), new Vector3(0.125f, 0.065f, 0.16f), housing);
            mb.LocalBox(new Vector3(-0.135f, 0.06f, -0.055f), new Vector3(0.135f, 0.078f, 0.16f), housing); // hood against glare
            mb.LocalBox(new Vector3(-0.118f, -0.058f, -0.002f), new Vector3(0.118f, 0.058f, 0.002f), new Color(0.05f, 0.05f, 0.06f)); // face
            foreach (float x in new[] { -0.058f, 0.058f })
            {
                mb.Panel(new Vector3(x, 0f, -0.005f), Vector3.back, 0.045f, 28, 0.01f, Dial, Dial);
                mb.Torus(new Vector3(x, 0f, -0.012f), 0.048f, 0.006f, Bezel, 32, 8);
                for (int k = 0; k <= 8; k++)
                {
                    float a = Mathf.Lerp(220f, -40f, k / 8f) * Mathf.Deg2Rad;
                    var d = new Vector3(Mathf.Cos(a), Mathf.Sin(a), 0f);
                    mb.Beam(new Vector3(x, 0f, -0.012f) + d * 0.033f, new Vector3(x, 0f, -0.012f) + d * 0.041f, 0.004f, Rim);
                }
            }
            var mr = Part("Binnacle", mb, root);
            _speedNeedle = NeedleAt(root, -0.058f);
            _revNeedle = NeedleAt(root, 0.058f);
        }

        Transform NeedleAt(Transform root, float x)
        {
            var mb = new MeshBuilder();
            mb.Beam(new Vector3(0f, -0.006f, 0f), new Vector3(0f, 0.036f, 0f), 0.005f, Needle);
            mb.Panel(Vector3.zero, Vector3.back, 0.009f, 8, 0.006f, Rim, Rim);
            var t = Part("Needle", mb, root);
            t.localPosition = new Vector3(x, 0f, -0.016f);
            return t;
        }

        void BuildBobblehead(CarShape s, VehicleIdentity id)
        {
            var root = new GameObject("Bobblehead").transform;
            root.SetParent(transform.parent, false);
            root.localPosition = new Vector3(-s.Eye.x * 0.35f, s.Belt + 0.065f, s.CabinFront - 0.42f);
            root.localScale = Vector3.one * 0.85f;
            var body = new MeshBuilder();
            var col = VehicleMeshFactory.BodyColor((id.ColorId + 3) % VehicleCatalog.Colors.Length);
            body.SmoothCylinder(Vector3.zero, 0.03f, 0.03f, 0.012f, Rim, 12);
            body.SmoothSphere(new Vector3(0f, 0.04f, 0f), new Vector3(0.022f, 0.032f, 0.02f), col, 10, 8);
            Part("BobBody", body, root);
            var head = new MeshBuilder();
            head.SmoothSphere(new Vector3(0f, 0.035f, 0f), new Vector3(0.036f, 0.036f, 0.036f), VehicleMeshFactory.SkinTones[(id.Skin + 2) % VehicleMeshFactory.SkinTones.Length], 12, 9);
            head.SmoothSphere(new Vector3(-0.012f, 0.042f, 0.032f), new Vector3(0.006f, 0.008f, 0.004f), Rim, 6, 4);
            head.SmoothSphere(new Vector3(0.012f, 0.042f, 0.032f), new Vector3(0.006f, 0.008f, 0.004f), Rim, 6, 4);
            head.SmoothSphere(new Vector3(0f, 0.068f, 0f), new Vector3(0.03f, 0.012f, 0.03f), col, 10, 6);
            _bobHead = Part("BobHead", head, root);
            _bobHead.localPosition = new Vector3(0f, 0.07f, 0f);
            // Face the driver.
            root.localRotation = Quaternion.LookRotation(new Vector3(s.Eye.x - root.localPosition.x, 0f, -1f), Vector3.up);
        }

        Transform Part(string name, MeshBuilder mb, Transform parent = null)
        {
            var go = new GameObject(name);
            go.layer = gameObject.layer = _car.gameObject.layer;
            go.transform.SetParent(parent != null ? parent : transform, false);
            go.AddComponent<MeshFilter>().sharedMesh = mb.Build(name);
            var mr = go.AddComponent<MeshRenderer>();
            mr.sharedMaterial = VehicleMaterials.Driver;
            mr.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            return go.transform;
        }

        void OnDestroy()
        {
            foreach (var mf in GetComponentsInChildren<MeshFilter>()) if (mf.sharedMesh != null) Destroy(mf.sharedMesh);
            if (transform.parent == null) return;
            foreach (var n in new[] { "Gauges", "Bobblehead" })
            {
                var t = transform.parent.Find(n);
                if (t == null) continue;
                foreach (var mf in t.GetComponentsInChildren<MeshFilter>()) if (mf.sharedMesh != null) Destroy(mf.sharedMesh);
                Destroy(t.gameObject);
            }
        }

        void LateUpdate()
        {
            if (_car == null) return;
            float dt = Time.deltaTime;
            // Wheel: steering right turns it clockwise as the driver sees it (negative about the column).
            if (Mathf.Abs(_car.Steer) > 0.05f) DebugSteer = null; // the driver's hands win over a screenshot pose
            float target = -(DebugSteer ?? _car.Steer) * MaxWheelDeg;
            _angle = Mathf.Lerp(_angle, target, 1f - Mathf.Exp(-dt * 18f));
            _wheel.localRotation = Quaternion.AngleAxis(_angle, Vector3.forward);

            // Hands ride the rim, then slip (hand over hand) once it's turned past what a wrist allows.
            float hand = Mathf.Clamp(_angle, -MaxHandDeg, MaxHandDeg);
            _hornBlend = Mathf.MoveTowards(_hornBlend, _car.Horn || DebugHorn ? 1f : 0f, dt * 9f);
            if (_car.IndicateLeft && !_wasLeft || _car.IndicateRight && !_wasRight) _flickL = 1f;
            _wasLeft = _car.IndicateLeft; _wasRight = _car.IndicateRight;
            _flickL = Mathf.MoveTowards(_flickL, 0f, dt * 4f);
            Grip(_left, 155f + hand, _flickL, 0f);
            Grip(_right, 25f + hand, 0f, _hornBlend);

            // Gauges.
            float speed = Mathf.Abs(_car.ForwardSpeed);
            _speedNeedle.localRotation = Quaternion.AngleAxis(Mathf.Lerp(130f, -130f, speed / 45f), Vector3.forward);
            // Pretend gearbox: revs climb through each gear and drop at the shift.
            float gear = speed / 9f, frac = gear - Mathf.Floor(gear);
            float revTarget = Mathf.Clamp01(0.15f + (speed < 0.5f ? 0f : 0.25f + frac * 0.55f) + _car.Throttle * 0.15f);
            _rev = Mathf.Lerp(_rev, revTarget, 1f - Mathf.Exp(-dt * 8f));
            _revNeedle.localRotation = Quaternion.AngleAxis(Mathf.Lerp(130f, -130f, _rev), Vector3.forward);

            // Bobblehead: a damped spring driven by the car's acceleration (in its own frame).
            var rb = _car.GetComponent<Rigidbody>();
            if (rb != null && dt > 0f)
            {
                var vel = _car.transform.InverseTransformDirection(rb.linearVelocity);
                var acc = (vel - _lastVel) / dt;
                _lastVel = vel;
                var force = new Vector3(-acc.x, 0f, -acc.z) * 0.0025f - _bob * 90f - _bobVel * 3.5f;
                _bobVel += force * dt;
                _bob += _bobVel * dt;
                _bob = Vector3.ClampMagnitude(_bob, 0.35f);
                _bobHead.localRotation = Quaternion.Euler(_bob.z * 120f, 0f, -_bob.x * 120f);
            }
        }

        /// <summary>Place a hand on the rim at <paramref name="deg"/> (0 = three o'clock), blending to the stalk or the horn.</summary>
        void Grip(Transform t, float deg, float flick, float horn)
        {
            float a = deg * Mathf.Deg2Rad;
            var onRim = new Vector3(Mathf.Cos(a), Mathf.Sin(a), 0f) * RimRadius;
            var rot = Quaternion.AngleAxis(deg - 90f, Vector3.forward);
            var pos = onRim;
            if (flick > 0f)
            {
                // Quick reach to the stalk behind the rim and back.
                float k = Mathf.Sin(flick * Mathf.PI);
                pos = Vector3.Lerp(onRim, new Vector3(-0.12f, 0.03f, 0.06f), k * 0.8f);
            }
            if (horn > 0f)
            {
                pos = Vector3.Lerp(pos, new Vector3(0.02f, 0f, -0.05f), horn);
                rot = Quaternion.Slerp(rot, Quaternion.Euler(0f, 0f, -20f), horn);
            }
            t.localPosition = pos;
            t.localRotation = rot;
        }
    }
}
