using UnityEngine;
#if ENABLE_INPUT_SYSTEM
using UnityEngine.InputSystem;
#endif

namespace Voxels
{
    public sealed class FlyCamera : MonoBehaviour
    {
        [SerializeField] float _moveSpeed = 30f;
        [SerializeField] float _sprintMultiplier = 3f;
        [SerializeField] float _lookSensitivity = 0.12f;

        float _yaw;
        float _pitch;
        bool _lookEnabled;

        void Start()
        {
            Vector3 euler = transform.eulerAngles;
            _yaw = euler.y;
            _pitch = NormalizePitch(euler.x);
            SetCursorLocked(true);
        }

        static float NormalizePitch(float angle)
        {
            angle %= 360f;
            return angle > 180f ? angle - 360f : angle;
        }

        void Update()
        {
#if ENABLE_INPUT_SYSTEM
            UpdateWithInputSystem();
#else
            UpdateWithLegacyInput();
#endif
            transform.rotation = Quaternion.Euler(_pitch, _yaw, 0f);
        }

#if ENABLE_INPUT_SYSTEM
        void UpdateWithInputSystem()
        {
            Keyboard keyboard = Keyboard.current;
            if (keyboard == null)
                return;

            Mouse mouse = Mouse.current;

            if (keyboard.escapeKey.wasPressedThisFrame)
                SetCursorLocked(false);
            if (mouse != null && mouse.leftButton.wasPressedThisFrame)
                SetCursorLocked(true);

            if (_lookEnabled && mouse != null)
            {
                Vector2 delta = mouse.delta.ReadValue() * _lookSensitivity;
                _yaw += delta.x;
                _pitch = Mathf.Clamp(_pitch - delta.y, -89f, 89f);
            }

            Vector3 move = Vector3.zero;
            if (keyboard.wKey.isPressed) move += transform.forward;
            if (keyboard.sKey.isPressed) move -= transform.forward;
            if (keyboard.dKey.isPressed) move += transform.right;
            if (keyboard.aKey.isPressed) move -= transform.right;
            if (keyboard.spaceKey.isPressed) move += Vector3.up;
            if (keyboard.leftCtrlKey.isPressed) move -= Vector3.up;

            if (move.sqrMagnitude < 0.0001f)
                return;

            float speed = _moveSpeed * (keyboard.leftShiftKey.isPressed ? _sprintMultiplier : 1f);
            transform.position += move.normalized * (speed * Time.deltaTime);
        }
#endif

#if !ENABLE_INPUT_SYSTEM
        void UpdateWithLegacyInput()
        {
            if (Input.GetKeyDown(KeyCode.Escape)) SetCursorLocked(false);
            if (Input.GetMouseButtonDown(0)) SetCursorLocked(true);

            if (_lookEnabled)
            {
                _yaw += Input.GetAxis("Mouse X") * _lookSensitivity * 10f;
                _pitch = Mathf.Clamp(_pitch - Input.GetAxis("Mouse Y") * _lookSensitivity * 10f, -89f, 89f);
            }

            Vector3 move = Vector3.zero;
            if (Input.GetKey(KeyCode.W)) move += transform.forward;
            if (Input.GetKey(KeyCode.S)) move -= transform.forward;
            if (Input.GetKey(KeyCode.D)) move += transform.right;
            if (Input.GetKey(KeyCode.A)) move -= transform.right;
            if (Input.GetKey(KeyCode.Space)) move += Vector3.up;
            if (Input.GetKey(KeyCode.LeftControl)) move -= Vector3.up;

            if (move.sqrMagnitude < 0.0001f)
                return;

            float speed = _moveSpeed * (Input.GetKey(KeyCode.LeftShift) ? _sprintMultiplier : 1f);
            transform.position += move.normalized * (speed * Time.deltaTime);
        }
#endif

        void OnDisable()
        {
            SetCursorLocked(false);
        }

        void SetCursorLocked(bool locked)
        {
            _lookEnabled = locked;
            Cursor.lockState = locked ? CursorLockMode.Locked : CursorLockMode.None;
            Cursor.visible = !locked;
        }
    }
}