using UnityEngine;
using UnityEngine.InputSystem;

namespace Voxels
{
    /// <summary>
    /// Персонаж от первого/третьего лица. Корень — игрок с CharacterController,
    /// камера — дочерний объект, поэтому третий вид — это просто смещение камеры.
    /// V переключает вид.
    /// </summary>
    [RequireComponent(typeof(CharacterController))]
    public sealed class PlayerController : MonoBehaviour
    {
        [SerializeField] float _walkSpeed = 5f;
        [SerializeField] float _jumpHeight = 1.2f;
        [SerializeField] float _gravity = -22f;
        [SerializeField] float _lookSensitivity = 0.1f;

        [SerializeField] Vector3 _firstPersonOffset = new Vector3(0f, 1.6f, 0f);
        [SerializeField] Vector3 _thirdPersonOffset = new Vector3(0f, 1.9f, -4.5f);

        public Transform CameraTransform;

        CharacterController _controller;
        VoxelEngine _engine;
        Transform _camera;
        float _vertical;
        float _yaw;
        float _pitch;
        bool _groundedOnce;
        bool _thirdPerson;

        void Awake()
        {
            _controller = GetComponent<CharacterController>();
            _yaw = transform.eulerAngles.y;

            var fly = GetComponentInChildren<FlyCamera>();
            if (fly != null)
                fly.enabled = false;

            _camera = CameraTransform != null ? CameraTransform : GetComponentInChildren<Camera>()?.transform;
            ApplyView();

            Cursor.lockState = CursorLockMode.Locked;
            Cursor.visible = false;
        }

        void Update()
        {
            Keyboard keyboard = Keyboard.current;
            Mouse mouse = Mouse.current;

            if (keyboard == null || mouse == null)
                return;

            if (keyboard.escapeKey.wasPressedThisFrame)
            {
                Cursor.lockState = CursorLockMode.None;
                Cursor.visible = true;
            }

            if (Cursor.lockState != CursorLockMode.Locked && mouse.leftButton.wasPressedThisFrame)
            {
                Cursor.lockState = CursorLockMode.Locked;
                Cursor.visible = false;
            }

            if (keyboard.vKey.wasPressedThisFrame)
            {
                _thirdPerson = !_thirdPerson;
                ApplyView();
            }

            if (Cursor.lockState == CursorLockMode.Locked)
            {
                Vector2 delta = mouse.delta.ReadValue() * _lookSensitivity;
                _yaw += delta.x;
                _pitch = Mathf.Clamp(_pitch - delta.y, -89f, 89f);

                transform.rotation = Quaternion.Euler(0f, _yaw, 0f);

                if (_camera != null)
                    _camera.localRotation = Quaternion.Euler(_pitch, 0f, 0f);
            }

            Vector2 input = Vector2.zero;

            if (keyboard.wKey.isPressed) input.y += 1f;
            if (keyboard.sKey.isPressed) input.y -= 1f;
            if (keyboard.dKey.isPressed) input.x += 1f;
            if (keyboard.aKey.isPressed) input.x -= 1f;

            input = Vector2.ClampMagnitude(input, 1f);

            Vector3 forward = transform.forward;
            forward.y = 0f;
            forward.Normalize();

            Vector3 right = transform.right;
            right.y = 0f;
            right.Normalize();

            Vector3 velocity = (forward * input.y + right * input.x) * _walkSpeed;

            if (!_groundedOnce)
            {
                if (_engine == null)
                    _engine = Object.FindAnyObjectByType<VoxelEngine>();

                bool groundReady = true;

                if (_engine != null && _engine.CurrentWorld != null)
                {
                    int px = Mathf.FloorToInt(transform.position.x);
                    int pz = Mathf.FloorToInt(transform.position.z);
                    int surface = _engine.CurrentWorld.GetSurfaceHeight(px, pz);

                    groundReady = _engine.CurrentWorld.GetBlock(px, surface, pz) != BlockType.Air;
                }

                if (groundReady)
                    _vertical += _gravity * Time.deltaTime;
                else
                    _vertical = 0f;

                if (_controller.isGrounded)
                    _groundedOnce = true;
            }
            else
            {
                if (_controller.isGrounded && _vertical < 0f)
                    _vertical = -2f;

                if (_controller.isGrounded && keyboard.spaceKey.wasPressedThisFrame)
                    _vertical = Mathf.Sqrt(_jumpHeight * -2f * _gravity);

                _vertical += _gravity * Time.deltaTime;
            }

            velocity.y = _vertical;
            _controller.Move(velocity * Time.deltaTime);
        }

        void ApplyView()
        {
            if (_camera == null)
                return;

            _camera.localPosition = _thirdPerson ? _thirdPersonOffset : _firstPersonOffset;
            _camera.localRotation = Quaternion.Euler(_pitch, 0f, 0f);
        }
    }
}
