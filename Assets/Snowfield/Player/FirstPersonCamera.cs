using UnityEngine;
using UnityEngine.InputSystem;

namespace Snowfield.Player
{
    /// <summary>
    /// First-person rig: parks the camera at the character's eye height, mouse X turns the character, mouse Y pitches
    /// the camera. Tab toggles cursor lock; Esc releases it. Scroll is left to the tools; +/- do nothing here.
    /// Lives on a child of the Player; moves a referenced camera.
    /// </summary>
    public class FirstPersonCamera : MonoBehaviour
    {
        [Tooltip("The camera this rig moves. Defaults to Camera.main.")]
        public Transform cameraTransform;
        [Tooltip("Character to ride. Defaults to the SnowCharacter in this object's parents.")]
        public SnowCharacter character;
        public float sensitivity = 0.12f;
        public float minPitch = -85f, maxPitch = 85f;
        [Tooltip("Forward offset of the eye from the capsule axis (m). A little avoids seeing the brush cursor clip.")]
        public float eyeForward = 0.1f;

        float _yaw, _pitch;
        public bool CursorLocked { get; private set; }

        void Awake()
        {
            if (cameraTransform == null && Camera.main != null) cameraTransform = Camera.main.transform;
            if (character == null) character = GetComponentInParent<SnowCharacter>();
        }

        void Start()
        {
            if (character != null) _yaw = character.transform.eulerAngles.y;
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
            if (character == null || cameraTransform == null) return;
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

            character.transform.rotation = Quaternion.Euler(0f, _yaw, 0f);
            Snap();
        }

        /// <summary>Place the camera for the current yaw/pitch without reading input. Safe in edit mode.</summary>
        public void Snap()
        {
            if (character == null || cameraTransform == null) return;
            var t = character.transform;
            Vector3 eye = t.position + Vector3.up * character.EyeHeight + t.forward * eyeForward;
            cameraTransform.SetPositionAndRotation(eye, Quaternion.Euler(_pitch, _yaw, 0f));
        }
    }
}
