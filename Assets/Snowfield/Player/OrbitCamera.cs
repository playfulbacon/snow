using UnityEngine;
using UnityEngine.InputSystem;

namespace Snowfield.Player
{
    /// <summary>
    /// Over-the-shoulder orbit camera. Right-drag or locked cursor to look. Scroll is reserved for brush size,
    /// so zoom is on +/- keys. Holding RMB to look conflicts with RMB = smooth; so look is always-on when the
    /// cursor is locked (Tab toggles lock) and the brush uses the screen centre.
    /// </summary>
    public class OrbitCamera : MonoBehaviour
    {
        [Tooltip("The camera this rig moves. Defaults to Camera.main.")]
        public Transform cameraTransform;
        [Tooltip("What to orbit. Defaults to the SnowCharacter in this object's parents.")]
        public Transform target;
        public Vector3 targetOffset = new Vector3(0f, 1.4f, 0f);
        public float distance = 3.5f;
        public float minDistance = 1.5f, maxDistance = 7f;
        public float sensitivity = 0.12f;
        public float minPitch = -20f, maxPitch = 70f;
        public float shoulder = 0.45f;
        public LayerMask collisionMask = ~0;

        float _yaw, _pitch = 20f;
        public bool CursorLocked { get; private set; }

        void Awake()
        {
            if (cameraTransform == null && Camera.main != null) cameraTransform = Camera.main.transform;
            if (target == null)
            {
                var ch = GetComponentInParent<SnowCharacter>();
                if (ch != null) target = ch.transform;
            }
        }

        void Start()
        {
            if (cameraTransform != null) _yaw = cameraTransform.eulerAngles.y;
            SetCursorLock(true);
        }

        public void SetCursorLock(bool locked)
        {
            CursorLocked = locked;
            Cursor.lockState = locked ? CursorLockMode.Locked : CursorLockMode.None;
            Cursor.visible = !locked;
        }

        void LateUpdate()
        {
            if (target == null) return;
            var kb = Keyboard.current;
            var mouse = Mouse.current;

            if (kb != null && kb.tabKey.wasPressedThisFrame) SetCursorLock(!CursorLocked);
            if (kb != null && kb.escapeKey.wasPressedThisFrame) SetCursorLock(false);

            if (mouse != null && CursorLocked)
            {
                Vector2 d = mouse.delta.ReadValue();
                _yaw += d.x * sensitivity;
                _pitch = Mathf.Clamp(_pitch - d.y * sensitivity, minPitch, maxPitch);
            }
            if (kb != null)
            {
                if (kb.equalsKey.isPressed || kb.numpadPlusKey.isPressed) distance -= 3f * Time.deltaTime;
                if (kb.minusKey.isPressed || kb.numpadMinusKey.isPressed) distance += 3f * Time.deltaTime;
                distance = Mathf.Clamp(distance, minDistance, maxDistance);
            }

            Snap();
        }

        /// <summary>Place the camera for the current yaw/pitch/distance without reading input. Safe in edit mode.</summary>
        public void Snap()
        {
            if (target == null || cameraTransform == null) return;
            var rot = Quaternion.Euler(_pitch, _yaw, 0f);
            Vector3 pivot = target.position + targetOffset;
            Vector3 desired = pivot + rot * new Vector3(shoulder, 0f, -distance);

            // Pull in if something is between pivot and camera (ignore the player's own collider via mask).
            Vector3 dir = desired - pivot;
            float len = dir.magnitude;
            if (Physics.SphereCast(pivot, 0.2f, dir / len, out var hit, len, collisionMask, QueryTriggerInteraction.Ignore))
                desired = pivot + dir / len * Mathf.Max(hit.distance - 0.05f, 0.3f);

            cameraTransform.SetPositionAndRotation(desired, rot);
        }
    }
}
