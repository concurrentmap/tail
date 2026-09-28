using Tailed.Cameras;
using Tailed.Vehicles;
using UnityEngine;
using UnityEngine.InputSystem;

namespace Tailed.Cockpit
{
    public enum CameraMode { Cockpit, Chase, Free }

    /// <summary>Which mirror the driver is glancing at (held keys 1/2/3, d-pad up for the rear-view).</summary>
    public enum Glance { None, Left, Rear, Right }

    /// <summary>
    /// Main camera rig (GD §12). Cockpit: at the driver's eye, mouse/right-stick head-look (up to
    /// over-the-shoulder), Q/E lean. Chase: forward-biased, height-capped. V toggles; F2 = dev free cam.
    /// Plates are always judged from the driver's eye (OpticsCamera), never from the chase camera.
    /// </summary>
    [RequireComponent(typeof(Camera))]
    public sealed class CameraDirector : MonoBehaviour
    {
        public PlayerCar Target;
        public CameraMode Mode = CameraMode.Cockpit;
        public float MouseSensitivity = 0.12f;

        public static CameraDirector Instance { get; private set; }
        public float HeadYaw => _yaw;
        /// <summary>Current mirror glance (also drives the enlarged mirror in chase view).</summary>
        public Glance Glance { get; private set; }
        /// <summary>Right mouse / right-stick click: the eye "focuses" (narrow FOV) — how a human reads a plate.</summary>
        public bool Focusing { get; private set; }
        public const float BaseFov = 72f, FocusFov = 24f;
        /// <summary>Fraction of the horizontal view a glanced-at mirror fills.</summary>
        public const float GlanceFill = 0.72f, GlanceFocusFill = 1f;
        float _fov = BaseFov;

        float _yaw, _pitch, _lean, _idle, _glanceYaw, _glancePitch, _glanceBlend;
        Vector3 _chaseVel;
        OpticsCamera _optics;
        FlyCamera _fly;
        Camera _cam;
        public static bool LookBlocked;
        /// <summary>Dev/screenshot override for head yaw/pitch (null = normal control).</summary>
        public static Vector2? DebugLook;
        public static float DebugZoom = 1f;
        public static Glance? DebugGlance;
        public static bool DebugFocus;

        const float ChaseDistance = 7.5f, ChaseHeight = 2.6f, MaxChaseHeight = 3.4f;
        static readonly float GazeCone = Mathf.Cos(100f * Mathf.Deg2Rad);

        void Awake()
        {
            Instance = this;
            _cam = GetComponent<Camera>();
            _optics = GetComponent<OpticsCamera>() ?? gameObject.AddComponent<OpticsCamera>();
            _fly = GetComponent<FlyCamera>();
        }

        public void SetFree(bool free)
        {
            Mode = free ? CameraMode.Free : CameraMode.Cockpit;
            if (_fly != null) _fly.enabled = free;
            if (free && _fly != null) _fly.SyncAngles();
        }

        void Update()
        {
            var kb = Keyboard.current;
            if (kb != null)
            {
                if (kb.f2Key.wasPressedThisFrame) SetFree(Mode != CameraMode.Free);
                if (kb.vKey.wasPressedThisFrame && Mode != CameraMode.Free)
                    Mode = Mode == CameraMode.Cockpit ? CameraMode.Chase : CameraMode.Cockpit;
            }
            if (_fly != null) _fly.enabled = Mode == CameraMode.Free || Target == null;

            // Mouse look needs a locked cursor: click to capture, Esc to release.
            var mouse = Mouse.current;
            if (mouse != null && mouse.leftButton.wasPressedThisFrame && !LookBlocked && Target != null && Mode != CameraMode.Free)
                Cursor.lockState = CursorLockMode.Locked;
            if (kb != null && kb.escapeKey.wasPressedThisFrame) Cursor.lockState = CursorLockMode.None;
            Cursor.visible = Cursor.lockState != CursorLockMode.Locked;
        }

        void LateUpdate()
        {
            if (Target == null || Mode == CameraMode.Free) { _optics.Eye = null; _optics.UseGazeCone = false; return; }
            float dt = Time.deltaTime;
            ReadLook(dt);

            // Head pose drives the eye (used by mirrors and plate legibility in both modes).
            var eye = Target.Eye;
            eye.localPosition = Target.Shape.Eye + new Vector3(_lean * 0.16f, 0f, Mathf.Abs(_lean) * 0.04f);
            // A glance blends over the player's own head direction without overwriting it.
            float yaw = Mathf.Lerp(_yaw, _glanceYaw, _glanceBlend), pitch = Mathf.Lerp(_pitch, _glancePitch, _glanceBlend);
            eye.localRotation = Quaternion.Euler(pitch, yaw, 0f);
            _optics.Eye = eye;

            if (Mode == CameraMode.Cockpit)
            {
                transform.SetPositionAndRotation(eye.position, eye.rotation);
                _cam.nearClipPlane = 0.05f;
                float target = BaseFov;
                if (Glance != Glance.None) target = GlanceFov(MirrorPoint(Glance), MirrorWidth(Glance), Focusing ? GlanceFocusFill : GlanceFill);
                else if (Focusing) target = FocusFov;
                target /= Mathf.Max(DebugZoom, 1f);
                _fov = Mathf.Lerp(_fov, target, 1f - Mathf.Exp(-dt * 14f));
                _cam.fieldOfView = _fov;
                _optics.UseGazeCone = false;
            }
            else
            {
                var car = Target.transform;
                var flatFwd = Vector3.ProjectOnPlane(car.forward, Vector3.up).normalized;
                var orbit = Quaternion.AngleAxis(_yaw * 0.6f, Vector3.up) * flatFwd;
                var desired = car.position - orbit * ChaseDistance + Vector3.up * ChaseHeight;
                desired.y = Mathf.Min(desired.y, car.position.y + MaxChaseHeight);
                transform.position = Vector3.SmoothDamp(transform.position, desired, ref _chaseVel, 0.12f);
                var look = car.position + Vector3.up * 1.2f + flatFwd * 4f;
                transform.rotation = Quaternion.Slerp(transform.rotation, Quaternion.LookRotation(look - transform.position), 1f - Mathf.Exp(-dt * 10f));
                _cam.nearClipPlane = 0.2f;
                _cam.fieldOfView = 62f;
                _optics.GazeCone = GazeCone;
                _optics.UseGazeCone = true;
            }
        }

        Vector3 MirrorPoint(Glance g) => g == Glance.Left ? Target.Shape.LeftMirror : g == Glance.Right ? Target.Shape.RightMirror : Target.Shape.RearViewMirror;
        static float MirrorWidth(Glance g) => g == Glance.Rear ? 0.3f : 0.21f;

        /// <summary>Vertical FOV at which a mirror of width w at local point p fills <see cref="GlanceFill"/> of the view.</summary>
        float GlanceFov(Vector3 localPoint, float width, float fill)
        {
            float d = Vector3.Distance(localPoint, Target.Eye.localPosition);
            float mirrorAngle = 2f * Mathf.Atan(width * 0.5f / d);
            float hFov = mirrorAngle / fill;
            float vFov = 2f * Mathf.Atan(Mathf.Tan(hFov * 0.5f) / Mathf.Max(_cam.aspect, 0.5f)) * Mathf.Rad2Deg;
            return Mathf.Clamp(vFov, 8f, BaseFov);
        }

        void ReadGlance()
        {
            var kb = Keyboard.current;
            var pad = Gamepad.current;
            var mouse = Mouse.current;
            var g = Glance.None;
            if (!LookBlocked && kb != null)
            {
                if (kb.digit1Key.isPressed) g = Glance.Left;
                else if (kb.digit2Key.isPressed) g = Glance.Rear;
                else if (kb.digit3Key.isPressed) g = Glance.Right;
            }
            if (pad != null && pad.dpad.up.isPressed) g = Glance.Rear;
            if (DebugGlance.HasValue) g = DebugGlance.Value;
            Glance = g;
            Focusing = DebugFocus || !LookBlocked && ((mouse != null && mouse.rightButton.isPressed && Cursor.lockState == CursorLockMode.Locked) ||
                                        (pad != null && pad.rightStickButton.isPressed));
        }

        void ReadLook(float dt)
        {
            ReadGlance();
            Vector2 look = Vector2.zero;
            float lean = 0f;
            var mouse = Mouse.current;
            var kb = Keyboard.current;
            var pad = Gamepad.current;
            // Finer look control while zoomed in.
            float zoomScale = _fov / BaseFov;
            if (mouse != null && !LookBlocked && Cursor.lockState == CursorLockMode.Locked) look += mouse.delta.ReadValue() * MouseSensitivity * zoomScale;
            if (pad != null) look += pad.rightStick.ReadValue() * new Vector2(160f, 100f) * dt;
            if (kb != null && !LookBlocked)
            {
                if (kb.qKey.isPressed) lean -= 1f;
                if (kb.eKey.isPressed) lean += 1f;
            }
            if (pad != null) lean += (pad.rightShoulder.isPressed ? 1f : 0f) - (pad.leftShoulder.isPressed ? 1f : 0f);

            if (look.sqrMagnitude > 0.0001f) _idle = 0f; else _idle += dt;
            _yaw = Mathf.Clamp(_yaw + look.x, -155f, 155f);
            _pitch = Mathf.Clamp(_pitch - look.y, -50f, 40f);
            // Eyes drift back to the road when you stop looking around and the car is moving.
            if (_idle > 1.5f && Mathf.Abs(Target.ForwardSpeed) > 2f)
            {
                _yaw = Mathf.Lerp(_yaw, 0f, 1f - Mathf.Exp(-dt * 2.5f));
                _pitch = Mathf.Lerp(_pitch, 0f, 1f - Mathf.Exp(-dt * 2.5f));
            }
            _lean = Mathf.MoveTowards(_lean, Mathf.Clamp(lean, -1f, 1f), dt * 4f);

            // Glancing: the head turns to the mirror quickly (a real glance takes ~0.2 s), then back.
            if (Glance != Glance.None && Mode == CameraMode.Cockpit)
            {
                var d = MirrorPoint(Glance) - Target.Eye.localPosition;
                float gy = Mathf.Atan2(d.x, d.z) * Mathf.Rad2Deg;
                float gp = -Mathf.Atan2(d.y, new Vector2(d.x, d.z).magnitude) * Mathf.Rad2Deg;
                float k = 1f - Mathf.Exp(-dt * 18f);
                _glanceYaw = Mathf.Lerp(_glanceYaw, gy, k);
                _glancePitch = Mathf.Lerp(_glancePitch, gp, k);
                _glanceBlend = Mathf.MoveTowards(_glanceBlend, 1f, dt * 6f);
            }
            else _glanceBlend = Mathf.MoveTowards(_glanceBlend, 0f, dt * 6f);
            if (_glanceBlend > 0f) _idle = 0f;
            if (DebugLook.HasValue) { _yaw = DebugLook.Value.x; _pitch = DebugLook.Value.y; }
        }
    }
}
