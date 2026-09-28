using UnityEngine;
using UnityEngine.InputSystem;

namespace Tailed.Cameras
{
    /// <summary>Dev camera: WASD move, Q/E down/up, hold right mouse to look, Shift = fast, scroll = speed.</summary>
    public sealed class FlyCamera : MonoBehaviour
    {
        public float Speed = 30f;
        public float LookSensitivity = 0.15f;
        float _yaw, _pitch;

        void OnEnable() => SyncAngles();

        public void SyncAngles()
        {
            var e = transform.eulerAngles;
            _yaw = e.y;
            _pitch = e.x > 180f ? e.x - 360f : e.x;
        }

        void Update()
        {
            var kb = Keyboard.current;
            var mouse = Mouse.current;
            if (kb == null || mouse == null) return;

            if (mouse.rightButton.isPressed)
            {
                var delta = mouse.delta.ReadValue();
                _yaw += delta.x * LookSensitivity;
                _pitch = Mathf.Clamp(_pitch - delta.y * LookSensitivity, -89f, 89f);
                transform.rotation = Quaternion.Euler(_pitch, _yaw, 0f);
            }

            float scroll = mouse.scroll.ReadValue().y;
            if (scroll != 0f) Speed = Mathf.Clamp(Speed * (scroll > 0 ? 1.2f : 1f / 1.2f), 2f, 500f);

            var move = Vector3.zero;
            if (kb.wKey.isPressed) move += transform.forward;
            if (kb.sKey.isPressed) move -= transform.forward;
            if (kb.dKey.isPressed) move += transform.right;
            if (kb.aKey.isPressed) move -= transform.right;
            if (kb.eKey.isPressed) move += Vector3.up;
            if (kb.qKey.isPressed) move -= Vector3.up;
            float speed = Speed * (kb.leftShiftKey.isPressed ? 4f : 1f);
            transform.position += move * (speed * Time.unscaledDeltaTime);
        }
    }
}
