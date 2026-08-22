using UnityEngine;
using UnityEngine.InputSystem;

namespace Snowfield.Player
{
    /// <summary>
    /// Minimal mover: WASD relative to the camera's yaw, gravity, CharacterController, and a stance
    /// (Q = crouch, E = tiptoe) that animates the capsule height and the eye height the camera rig reads.
    /// (Shift is reserved for mode cycling, so there is no run.)
    /// Deliberately plain; the feel budget goes to the brush, not locomotion.
    /// </summary>
    [RequireComponent(typeof(CharacterController))]
    public class SnowCharacter : MonoBehaviour
    {
        public float walkSpeed = 3.2f;
        public float turnSmoothing = 12f;
        public float gravity = -18f;
        [Tooltip("Rotate to face the movement direction (third person). Off for first person, where the camera rig sets yaw.")]
        public bool faceMovementDirection = true;

        [Header("Stance")]
        public float standHeight = 1.8f;
        public float crouchHeight = 1.0f;
        public float tiptoeHeight = 2.15f;
        [Tooltip("Eye sits this far below the top of the capsule.")]
        public float eyeBelowTop = 0.12f;
        [Tooltip("How quickly the stance changes (higher = snappier).")]
        public float stanceSpeed = 10f;
        [Range(0.2f, 1f)] public float crouchSpeedMultiplier = 0.6f;
        [Range(0.2f, 1f)] public float tiptoeSpeedMultiplier = 0.7f;

        [Tooltip("Camera whose yaw defines 'forward'. Defaults to Camera.main.")]
        public Transform cameraRig;

        CharacterController _cc;
        float _verticalVelocity;

        public Vector3 Velocity { get; private set; }
        public bool IsMoving => Velocity.sqrMagnitude > 0.01f;
        public float CurrentHeight { get; private set; }
        public float EyeHeight => CurrentHeight - eyeBelowTop;
        public enum Stance { Stand, Crouch, Tiptoe }
        public Stance CurrentStance { get; private set; }

        void Awake()
        {
            _cc = GetComponent<CharacterController>();
            if (cameraRig == null && Camera.main != null) cameraRig = Camera.main.transform;
            CurrentHeight = standHeight;
            ApplyHeight();
        }

        void Update()
        {
            var kb = Keyboard.current;
            Vector2 input = Vector2.zero;
            CurrentStance = Stance.Stand;
            if (kb != null)
            {
                if (kb.wKey.isPressed || kb.upArrowKey.isPressed) input.y += 1;
                if (kb.sKey.isPressed || kb.downArrowKey.isPressed) input.y -= 1;
                if (kb.dKey.isPressed || kb.rightArrowKey.isPressed) input.x += 1;
                if (kb.aKey.isPressed || kb.leftArrowKey.isPressed) input.x -= 1;
                if (kb.qKey.isPressed) CurrentStance = Stance.Crouch;
                else if (kb.eKey.isPressed) CurrentStance = Stance.Tiptoe;
            }
            input = Vector2.ClampMagnitude(input, 1f);

            // stance
            float targetHeight = CurrentStance switch
            {
                Stance.Crouch => crouchHeight,
                Stance.Tiptoe => tiptoeHeight,
                _ => standHeight,
            };
            CurrentHeight = Mathf.Lerp(CurrentHeight, targetHeight, 1f - Mathf.Exp(-stanceSpeed * Time.deltaTime));
            ApplyHeight();
            float speed = walkSpeed * (CurrentStance == Stance.Crouch ? crouchSpeedMultiplier
                                     : CurrentStance == Stance.Tiptoe ? tiptoeSpeedMultiplier : 1f);

            Vector3 fwd = cameraRig != null ? cameraRig.forward : transform.forward;
            fwd.y = 0; fwd.Normalize();
            Vector3 right = Vector3.Cross(Vector3.up, fwd);
            Vector3 move = (fwd * input.y + right * input.x) * speed;

            if (faceMovementDirection && move.sqrMagnitude > 0.001f)
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

        void ApplyHeight()
        {
            if (_cc == null) return;
            _cc.height = CurrentHeight;
            _cc.center = new Vector3(0f, CurrentHeight * 0.5f, 0f);
        }
    }
}
