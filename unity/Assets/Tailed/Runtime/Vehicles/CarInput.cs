using UnityEngine;
using UnityEngine.InputSystem;

namespace Tailed.Vehicles
{
    /// <summary>
    /// Keyboard + gamepad driving controls. W/S throttle/brake (S reverses when stopped), A/D steer,
    /// Space handbrake, H horn, Z/C indicators, X hazards. Gamepad: RT/LT, left stick, A handbrake,
    /// d-pad left/right indicators, d-pad down hazards, B horn. Assists: L / right-stick lane-keep,
    /// K / d-pad up adaptive cruise.
    /// </summary>
    public sealed class CarInput : MonoBehaviour
    {
        PlayerCar _car;
        float _steer;
        public static bool Blocked; // UI has focus (notepad, flag form)

        void Awake() => _car = GetComponent<PlayerCar>();

        void Update()
        {
            var kb = Keyboard.current;
            var pad = Gamepad.current;
            float throttle = 0f, brake = 0f, steer = 0f;
            bool handbrake = false, horn = false;

            if (kb != null && !Blocked)
            {
                if (kb.wKey.isPressed || kb.upArrowKey.isPressed) throttle = 1f;
                if (kb.sKey.isPressed || kb.downArrowKey.isPressed) brake = 1f;
                if (kb.aKey.isPressed || kb.leftArrowKey.isPressed) steer -= 1f;
                if (kb.dKey.isPressed || kb.rightArrowKey.isPressed) steer += 1f;
                handbrake = kb.spaceKey.isPressed;
                horn = kb.hKey.isPressed;
                if (kb.zKey.wasPressedThisFrame) { _car.IndicateLeft = !_car.IndicateLeft; _car.IndicateRight = false; }
                if (kb.cKey.wasPressedThisFrame) { _car.IndicateRight = !_car.IndicateRight; _car.IndicateLeft = false; }
                if (kb.xKey.wasPressedThisFrame) _car.Hazards = !_car.Hazards;
                if (kb.lKey.wasPressedThisFrame) DriveAssist.LaneKeep = !DriveAssist.LaneKeep;
                if (kb.kKey.wasPressedThisFrame) DriveAssist.ToggleCruise(_car);
            }
            if (pad != null)
            {
                throttle = Mathf.Max(throttle, pad.rightTrigger.ReadValue());
                brake = Mathf.Max(brake, pad.leftTrigger.ReadValue());
                float stick = pad.leftStick.ReadValue().x;
                if (Mathf.Abs(stick) > 0.08f) steer = stick;
                handbrake |= pad.buttonSouth.isPressed;
                horn |= pad.buttonEast.isPressed;
                if (pad.dpad.left.wasPressedThisFrame) { _car.IndicateLeft = !_car.IndicateLeft; _car.IndicateRight = false; }
                if (pad.dpad.right.wasPressedThisFrame) { _car.IndicateRight = !_car.IndicateRight; _car.IndicateLeft = false; }
                if (pad.dpad.down.wasPressedThisFrame) _car.Hazards = !_car.Hazards;
                if (pad.dpad.up.wasPressedThisFrame) DriveAssist.ToggleCruise(_car);
                if (pad.rightStickButton.wasPressedThisFrame) DriveAssist.LaneKeep = !DriveAssist.LaneKeep;
            }

            // Keyboard steering ramps so taps don't jerk the wheel.
            _steer = Mathf.MoveTowards(_steer, steer, Time.deltaTime * (Mathf.Abs(steer) > 0.01f ? 3.5f : 5f));
            _car.Throttle = throttle;
            _car.Brake = brake;
            _car.Steer = _steer;
            _car.Handbrake = handbrake;
            _car.Horn = horn;

            // Indicators cancel themselves after the wheel returns from a turn, like a real stalk.
            if ((_car.IndicateLeft && _steer < -0.5f) || (_car.IndicateRight && _steer > 0.5f)) _selfCancelArmed = true;
            if (_selfCancelArmed && Mathf.Abs(_steer) < 0.1f) { _car.IndicateLeft = _car.IndicateRight = false; _selfCancelArmed = false; }
        }

        bool _selfCancelArmed;
    }
}
