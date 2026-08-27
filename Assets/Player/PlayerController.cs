using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.Rendering;

namespace SnowDays
{
    // First-person controller: WASD + mouse look, LeftShift run, Space jump, Q crouch, E tiptoe, Esc frees the cursor.
    [RequireComponent(typeof(CharacterController))]
    public class PlayerController : MonoBehaviour
    {
        [Header("Movement")]
        [SerializeField] private float m_WalkSpeed = 2.5f;
        [SerializeField] private float m_RunSpeed = 5.5f;
        [SerializeField] private float m_JumpHeight = 1.1f;
        [SerializeField] private float m_Acceleration = 14f;
        [SerializeField, Range(0f, 1f)] private float m_AirControl = 0.4f;
        [SerializeField, Range(0.1f, 1f)] private float m_BackpedalFactor = 0.75f;
        [SerializeField, Range(0.1f, 1f)] private float m_StrafeFactor = 0.9f;

        [Header("Stance")]
        // Snowfield's crouch (Q) / tiptoe (E), re-expressed as fractions of the authored standing
        // height (its SnowCharacter stood 1.8 m, crouched to 1.0, tiptoed to 2.15).
        [SerializeField, Range(0.3f, 1f)] private float m_CrouchHeightScale = 0.56f;
        [SerializeField, Range(1f, 1.5f)] private float m_TiptoeHeightScale = 1.19f;
        [SerializeField, Range(0.2f, 1f)] private float m_CrouchSpeedMultiplier = 0.6f;
        [SerializeField, Range(0.2f, 1f)] private float m_TiptoeSpeedMultiplier = 0.7f;
        // Exponential smoothing rate toward the stance height.
        [SerializeField] private float m_StanceSpeed = 10f;

        [Header("Animation")]
        // Authored root-motion speeds of the source clips (m/s); PlayerSceneSetup measures and overwrites these.
        [SerializeField] private ClipSpeeds m_WalkClipSpeeds = new ClipSpeeds { forward = 1.53f, backward = 2.29f, strafe = 2.65f };
        [SerializeField] private ClipSpeeds m_RunClipSpeeds = new ClipSpeeds { forward = 2.75f, backward = 2.29f, strafe = 2.65f };
        [SerializeField] private float m_MinPlaybackRate = 0.7f;
        // Sprint needs speed/authored ~3.3x with the ithappy clips (authored
        // ~1.0-1.8 m/s); clamping lower makes planted feet slide.
        [SerializeField] private float m_MaxPlaybackRate = 3.5f;

        [Header("Look")]
        [SerializeField] private float m_LookSensitivity = 0.12f;
        [SerializeField] private float m_PitchLimit = 85f;
        [SerializeField] private Transform m_CameraPivot;

        [Header("Rig")]
        [SerializeField] private Animator m_Animator;
        [SerializeField] private SkinnedMeshRenderer m_BodyMesh;
        [SerializeField] private SkinnedMeshRenderer m_FirstPersonMesh;
        [SerializeField] private bool m_IsLocal = true;

        private static readonly int MoveXHash = Animator.StringToHash("MoveX");
        private static readonly int MoveYHash = Animator.StringToHash("MoveY");
        private static readonly int SpeedHash = Animator.StringToHash("Speed");
        private static readonly int LocomotionSpeedHash = Animator.StringToHash("LocomotionSpeed");
        private static readonly int GroundedHash = Animator.StringToHash("IsGrounded");
        private static readonly int JumpHash = Animator.StringToHash("Jump");

        private const float AnimDamp = 0.12f;
        private const float CoyoteTime = 0.15f;
        private const float JumpBuffer = 0.15f;
        private const float GroundedGrace = 0.12f;

        private CharacterController m_Controller;
        private Vector3 m_PlanarVelocity;
        private float m_VerticalVelocity;
        private float m_Pitch;
        private float m_LastGroundedTime = -10f;
        private float m_JumpPressedTime = -10f;
        private float m_StandHeight;
        private float m_StandCenterY;
        private float m_StandPivotY;
        private float m_CurrentHeight;

        public enum Stance { Stand, Crouch, Tiptoe }
        /// <summary>Hold-based: Q crouches, E tiptoes, releasing returns to standing.</summary>
        public Stance CurrentStance { get; private set; }

        // Body hides from the local camera (shadow only); first-person hands/feet render for the owner only.
        public bool IsLocal
        {
            get => m_IsLocal;
            set { m_IsLocal = value; ApplyVisibility(); }
        }

        private void Awake()
        {
            m_Controller = GetComponent<CharacterController>();
            if (m_Animator == null) m_Animator = GetComponentInChildren<Animator>();
            if (m_CameraPivot == null) m_CameraPivot = transform.Find("CameraPivot");
            m_StandHeight = m_Controller.height;
            m_StandCenterY = m_Controller.center.y; // authored center sits 2 cm above height/2; preserve it exactly
            m_CurrentHeight = m_StandHeight;
            if (m_CameraPivot != null) m_StandPivotY = m_CameraPivot.localPosition.y;
        }

        private void Start()
        {
            ApplyVisibility();
            // Settle onto the ground so frame 1 doesn't read airborne and play a fall animation.
            m_Controller.Move(Vector3.down * 0.1f);
            m_LastGroundedTime = Time.time;
            if (m_IsLocal) SetCursorLocked(true);
        }

        private void Update()
        {
            if (!m_IsLocal) return;

            var keyboard = Keyboard.current;
            var mouse = Mouse.current;
            if (keyboard == null || mouse == null) return;

            HandleCursor(keyboard, mouse);
            if (Cursor.lockState == CursorLockMode.Locked) Look(mouse);
            UpdateStance(keyboard);
            Move(keyboard);
            Animate();
        }

        // Recomputed every frame from held keys; Q wins over E. Height changes keep the AUTHORED capsule
        // bottom (center.y - height/2, 2 cm above the transform) fixed, so the feet never shift and the
        // standing pose is bit-identical to the pre-stance scene.
        private void UpdateStance(Keyboard keyboard)
        {
            CurrentStance = Stance.Stand;
            if (Cursor.lockState == CursorLockMode.Locked)
            {
                if (keyboard.qKey.isPressed) CurrentStance = Stance.Crouch;
                else if (keyboard.eKey.isPressed) CurrentStance = Stance.Tiptoe;
            }

            float target = m_StandHeight * (CurrentStance == Stance.Crouch ? m_CrouchHeightScale
                         : CurrentStance == Stance.Tiptoe ? m_TiptoeHeightScale : 1f);

            // Growing (tiptoe, or standing back up) must not push the capsule into an overhang: clamp the
            // target to the clearance above the top sphere. Height writes bypass collision resolution, so
            // without this the next Move jitters against overlap recovery under porch roofs.
            if (target > m_CurrentHeight + 0.001f)
            {
                float need = target - m_CurrentHeight;
                Vector3 topCentre = transform.position + m_Controller.center
                    + Vector3.up * (m_Controller.height * 0.5f - m_Controller.radius);
                if (Physics.SphereCast(topCentre, m_Controller.radius * 0.95f, Vector3.up, out RaycastHit hit,
                        need + m_Controller.skinWidth, Physics.DefaultRaycastLayers, QueryTriggerInteraction.Ignore))
                    target = Mathf.Max(m_CurrentHeight, m_CurrentHeight + hit.distance - m_Controller.skinWidth);
            }

            m_CurrentHeight = Mathf.Lerp(m_CurrentHeight, target, 1f - Mathf.Exp(-m_StanceSpeed * Time.deltaTime));

            m_Controller.height = m_CurrentHeight;
            Vector3 center = m_Controller.center;
            center.y = m_StandCenterY + (m_CurrentHeight - m_StandHeight) * 0.5f;
            m_Controller.center = center;
            if (m_CameraPivot != null)
            {
                Vector3 pivot = m_CameraPivot.localPosition;
                pivot.y = m_StandPivotY * (m_CurrentHeight / m_StandHeight);
                m_CameraPivot.localPosition = pivot;
            }
        }

        private float StanceSpeedMultiplier => CurrentStance == Stance.Crouch ? m_CrouchSpeedMultiplier
            : CurrentStance == Stance.Tiptoe ? m_TiptoeSpeedMultiplier : 1f;

        private static void HandleCursor(Keyboard keyboard, Mouse mouse)
        {
            if (keyboard.escapeKey.wasPressedThisFrame) SetCursorLocked(false);
            else if (Cursor.lockState != CursorLockMode.Locked && mouse.leftButton.wasPressedThisFrame) SetCursorLocked(true);
        }

        private static void SetCursorLocked(bool locked)
        {
            Cursor.lockState = locked ? CursorLockMode.Locked : CursorLockMode.None;
            Cursor.visible = !locked;
        }

        private void Look(Mouse mouse)
        {
            Vector2 delta = mouse.delta.ReadValue() * m_LookSensitivity;
            transform.Rotate(0f, delta.x, 0f);
            m_Pitch = Mathf.Clamp(m_Pitch - delta.y, -m_PitchLimit, m_PitchLimit);
            if (m_CameraPivot != null) m_CameraPivot.localRotation = Quaternion.Euler(m_Pitch, 0f, 0f);
        }

        private void Move(Keyboard keyboard)
        {
            Vector2 input = Vector2.zero;
            if (Cursor.lockState == CursorLockMode.Locked)
            {
                if (keyboard.wKey.isPressed) input.y += 1f;
                if (keyboard.sKey.isPressed) input.y -= 1f;
                if (keyboard.dKey.isPressed) input.x += 1f;
                if (keyboard.aKey.isPressed) input.x -= 1f;
                input = Vector2.ClampMagnitude(input, 1f);
                if (keyboard.spaceKey.wasPressedThisFrame) m_JumpPressedTime = Time.time;
            }

            bool grounded = m_Controller.isGrounded;
            if (grounded) m_LastGroundedTime = Time.time;

            // Run only while standing: crouch and tiptoe are deliberate, careful stances.
            bool run = keyboard.leftShiftKey.isPressed && CurrentStance == Stance.Stand;
            Vector3 wishDir = transform.right * input.x + transform.forward * input.y;
            float targetSpeed = (run ? m_RunSpeed : m_WalkSpeed) * StanceSpeedMultiplier
                * input.magnitude * DirectionSpeedScale(input);
            float accel = m_Acceleration * (grounded ? 1f : m_AirControl);
            m_PlanarVelocity = Vector3.MoveTowards(m_PlanarVelocity, wishDir * targetSpeed, accel * Time.deltaTime);

            if (grounded && m_VerticalVelocity < 0f) m_VerticalVelocity = -2f;

            bool canJump = Time.time - m_LastGroundedTime <= CoyoteTime && m_VerticalVelocity <= 0f;
            if (canJump && Time.time - m_JumpPressedTime <= JumpBuffer)
            {
                m_VerticalVelocity = Mathf.Sqrt(2f * -Physics.gravity.y * m_JumpHeight);
                m_JumpPressedTime = -10f;
                m_LastGroundedTime = -10f;
                if (m_Animator != null) m_Animator.SetTrigger(JumpHash);
            }

            m_VerticalVelocity += Physics.gravity.y * Time.deltaTime;
            m_Controller.Move((m_PlanarVelocity + Vector3.up * m_VerticalVelocity) * Time.deltaTime);

            if ((m_Controller.collisionFlags & CollisionFlags.Above) != 0 && m_VerticalVelocity > 0f)
                m_VerticalVelocity = 0f;

            SnapToSlope(grounded);
        }

        // Backpedal and strafe cap below forward speed, standard FPS tuning.
        private float DirectionSpeedScale(Vector2 input)
        {
            float ax = Mathf.Abs(input.x);
            float ay = Mathf.Abs(input.y);
            float sum = ax + ay;
            if (sum < 0.001f) return 1f;
            float fb = input.y >= 0f ? 1f : m_BackpedalFactor;
            return (ay * fb + ax * m_StrafeFactor) / sum;
        }

        // The constant stick velocity can't follow steep down-slopes at run speed; snap within stepOffset instead.
        private void SnapToSlope(bool wasGrounded)
        {
            if (!wasGrounded || m_VerticalVelocity > 0f || m_Controller.isGrounded) return;

            float snapDist = m_Controller.stepOffset + m_Controller.skinWidth;
            Vector3 bottomSphere = transform.position + m_Controller.center
                + Vector3.down * (m_Controller.height * 0.5f - m_Controller.radius);
            if (Physics.SphereCast(bottomSphere, m_Controller.radius, Vector3.down, out RaycastHit hit,
                    snapDist + 0.01f, ~0, QueryTriggerInteraction.Ignore)
                && Vector3.Angle(hit.normal, Vector3.up) <= m_Controller.slopeLimit)
            {
                m_Controller.Move(Vector3.down * snapDist);
            }
        }

        private void Animate()
        {
            if (m_Animator == null) return;

            Vector3 local = transform.InverseTransformDirection(m_PlanarVelocity);
            var planar = new Vector2(local.x, local.z);
            float speed = planar.magnitude;
            // Blend tree rings: walk ring at magnitude 1, run ring at 2.
            // Ring magnitude is normalized by the DIRECTION's own top speed
            // (backpedal/strafe cap below forward), so a full-speed backpedal
            // reaches the ring clip undiluted instead of stalling at 0.75
            // with a quarter of idle mixed into the stride.
            float dirFactor = DirectionSpeedScale(planar);
            float walkCap = Mathf.Max(m_WalkSpeed * dirFactor, 0.01f);
            float runCap = Mathf.Max(m_RunSpeed * dirFactor, walkCap + 0.01f);
            float norm = speed <= walkCap
                ? speed / walkCap
                : 1f + (speed - walkCap) / (runCap - walkCap);
            Vector2 animAxis = speed < 0.01f ? Vector2.zero : planar / speed * norm;

            m_Animator.SetFloat(MoveXHash, animAxis.x, AnimDamp, Time.deltaTime);
            m_Animator.SetFloat(MoveYHash, animAxis.y, AnimDamp, Time.deltaTime);
            m_Animator.SetFloat(SpeedHash, norm, AnimDamp, Time.deltaTime);
            m_Animator.SetFloat(LocomotionSpeedHash,
                LocomotionPlaybackRate(planar, speed, norm, walkCap, runCap), AnimDamp, Time.deltaTime);
            m_Animator.SetBool(GroundedHash, Time.time - m_LastGroundedTime <= GroundedGrace);
        }

        // Playback multiplier so foot speed matches travel speed; fades to 1 near idle so breathing stays real-time.
        private float LocomotionPlaybackRate(Vector2 planar, float speed, float norm, float walkCap, float runCap)
        {
            if (speed < 0.01f) return 1f;

            Vector2 dir = planar / speed;
            float authoredWalk = Mathf.Max(m_WalkClipSpeeds.For(dir), 0.01f);
            float target;
            if (speed <= walkCap)
            {
                // Idle<->walk blend scales pose speed with the blend, so the
                // rate stays cap/authored across the whole sub-walk range.
                target = walkCap / authoredWalk;
            }
            else
            {
                float t = Mathf.Clamp01((speed - walkCap) / Mathf.Max(runCap - walkCap, 0.01f));
                float authored = Mathf.Max(Mathf.Lerp(authoredWalk, m_RunClipSpeeds.For(dir), t), 0.01f);
                target = speed / authored;
            }
            target = Mathf.Clamp(target, m_MinPlaybackRate, m_MaxPlaybackRate);
            return Mathf.Lerp(1f, target, Mathf.Clamp01(norm / 0.3f));
        }

        [System.Serializable]
        private struct ClipSpeeds
        {
            public float forward;
            public float backward;
            public float strafe;

            // L1-weighted directional blend approximating the freeform-directional tree.
            public float For(Vector2 dir)
            {
                float ax = Mathf.Abs(dir.x);
                float ay = Mathf.Abs(dir.y);
                float sum = ax + ay;
                if (sum < 0.001f) return forward;
                float fb = dir.y >= 0f ? forward : backward;
                return (ay * fb + ax * strafe) / sum;
            }
        }

        private void ApplyVisibility()
        {
            if (m_BodyMesh != null)
                m_BodyMesh.shadowCastingMode = m_IsLocal ? ShadowCastingMode.ShadowsOnly : ShadowCastingMode.On;

            if (m_FirstPersonMesh != null)
            {
                m_FirstPersonMesh.enabled = m_IsLocal;
                m_FirstPersonMesh.shadowCastingMode = ShadowCastingMode.Off;
                m_FirstPersonMesh.updateWhenOffscreen = true;
            }
        }
    }
}
