using Snowfield.Field;
using Snowfield.Sculpture;
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
        [Tooltip("Jump height in metres (Space).")]
        public float jumpHeight = 0.9f;
        [Tooltip("Grace period after leaving the ground where a jump still fires (s).")]
        public float coyoteTime = 0.12f;
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
        float _lastGroundedTime;
        float _lastSnowTouchTime = -999f;
        float _walkedSinceStep;
        int _stepSide = 1;

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
                bool canJump = Time.time - _lastGroundedTime <= coyoteTime
                            || Time.time - _lastSnowTouchTime <= coyoteTime; // touching a sculpture counts: climb by hopping
                if (kb.spaceKey.wasPressedThisFrame && canJump)
                {
                    _verticalVelocity = Mathf.Sqrt(2f * -gravity * jumpHeight);
                    _lastGroundedTime = -999f; // consume
                    _lastSnowTouchTime = -999f;
                }
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

            if (_cc.isGrounded)
            {
                _lastGroundedTime = Time.time;
                if (_verticalVelocity < 0f) _verticalVelocity = -2f;
            }
            _verticalVelocity += gravity * Time.deltaTime;

            Vector3 before = transform.position;
            Vector3 delta = (move + Vector3.up * _verticalVelocity) * Time.deltaTime;
            _cc.Move(delta);
            Velocity = move;
            StampFootprints(transform.position - before);
        }

        /// <summary>Press alternating footprints into the field every footstepSpacing metres walked on the ground.</summary>
        void StampFootprints(Vector3 worldDelta)
        {
            var terrain = SnowTerrain.Instance;
            if (terrain == null || terrain.Config == null || !_cc.isGrounded) return;
            worldDelta.y = 0f;
            _walkedSinceStep += worldDelta.magnitude;
            var cfg = terrain.Config;
            if (_walkedSinceStep < cfg.footstepSpacing) return;
            _walkedSinceStep = 0f;
            _stepSide = -_stepSide;
            Vector3 foot = transform.position + transform.right * (_stepSide * 0.12f);
            terrain.StampDepression(foot, cfg.footprintRadius, cfg.footprintDepth, 0.5f);
        }

        void OnControllerColliderHit(ControllerColliderHit hit)
        {
            if (hit.collider.GetComponentInParent<SnowSculpture>() != null)
                _lastSnowTouchTime = Time.time;
        }

        void ApplyHeight()
        {
            if (_cc == null) return;
            _cc.height = CurrentHeight;
            _cc.center = new Vector3(0f, CurrentHeight * 0.5f, 0f);
        }
    }
}
