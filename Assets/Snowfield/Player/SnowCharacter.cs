using UnityEngine;
using UnityEngine.InputSystem;

namespace Snowfield.Player
{
    /// <summary>
    /// Minimal third-person mover: WASD relative to the camera's yaw, gravity, CharacterController.
    /// Deliberately plain; the feel budget goes to the brush, not locomotion.
    /// </summary>
    [RequireComponent(typeof(CharacterController))]
    public class SnowCharacter : MonoBehaviour
    {
        public float walkSpeed = 3.2f;
        public float runSpeed = 5.5f;
        public float turnSmoothing = 12f;
        public float gravity = -18f;

        [Tooltip("Camera whose yaw defines 'forward'. Defaults to Camera.main.")]
        public Transform cameraRig;

        CharacterController _cc;
        float _verticalVelocity;

        public Vector3 Velocity { get; private set; }
        public bool IsMoving => Velocity.sqrMagnitude > 0.01f;

        void Awake()
        {
            _cc = GetComponent<CharacterController>();
            if (cameraRig == null && Camera.main != null) cameraRig = Camera.main.transform;
        }

        void Update()
        {
            var kb = Keyboard.current;
            Vector2 input = Vector2.zero;
            bool run = false;
            if (kb != null)
            {
                if (kb.wKey.isPressed || kb.upArrowKey.isPressed) input.y += 1;
                if (kb.sKey.isPressed || kb.downArrowKey.isPressed) input.y -= 1;
                if (kb.dKey.isPressed || kb.rightArrowKey.isPressed) input.x += 1;
                if (kb.aKey.isPressed || kb.leftArrowKey.isPressed) input.x -= 1;
                run = kb.leftShiftKey.isPressed;
            }
            input = Vector2.ClampMagnitude(input, 1f);

            Vector3 fwd = cameraRig != null ? cameraRig.forward : transform.forward;
            fwd.y = 0; fwd.Normalize();
            Vector3 right = Vector3.Cross(Vector3.up, fwd);
            Vector3 move = (fwd * input.y + right * input.x) * (run ? runSpeed : walkSpeed);

            if (move.sqrMagnitude > 0.001f)
            {
                var target = Quaternion.LookRotation(move.normalized, Vector3.up);
                transform.rotation = Quaternion.Slerp(transform.rotation, target, 1f - Mathf.Exp(-turnSmoothing * Time.deltaTime));
            }

            if (_cc.isGrounded && _verticalVelocity < 0f) _verticalVelocity = -2f;
            _verticalVelocity += gravity * Time.deltaTime;

            Vector3 delta = (move + Vector3.up * _verticalVelocity) * Time.deltaTime;
            _cc.Move(delta);
            Velocity = move;
        }
    }
}
