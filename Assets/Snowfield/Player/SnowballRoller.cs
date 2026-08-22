using Snowfield.Config;
using Snowfield.Sculpture;
using UnityEngine;

namespace Snowfield.Player
{
    /// <summary>Marker + size for a snowball left lying on the field. Pick it up by aiming and clicking.</summary>
    public class DroppedSnowball : MonoBehaviour
    {
        public float radius;
    }

    /// <summary>
    /// Faked snowball rolling. The held ball is a sphere prop pushed ahead of the character; it grows with distance
    /// travelled and spins to sell the roll. Attaching stamps a density sphere into the target sculpture and destroys
    /// the prop (reuses the brush plumbing). No physics, no voxels while rolling.
    /// Driven by <see cref="SculptTool"/> in Empty Hand mode; owns no input itself.
    /// </summary>
    public class SnowballRoller : MonoBehaviour
    {
        public SculptFeelConfig config;
        [Tooltip("Character the ball is pushed in front of. Defaults to the SnowCharacter in parents.")]
        public SnowCharacter character;
        [Tooltip("Material for the ball. Defaults to the first sculpture's snow material.")]
        public Material snowMaterial;
        [Tooltip("Gap between the character's capsule and the ball surface (m).")]
        public float pushGap = 0.35f;
        [Tooltip("How deep the ball sinks into the target when attached, as a fraction of its radius.")]
        [Range(0f, 1f)] public float attachSink = 0.45f;
        [Range(0f, 1f)] public float attachShoulder = 0.75f;
        [Tooltip("Raycast mask used to find the ground under the ball.")]
        public LayerMask groundMask = ~0;

        public bool IsHolding => _ball != null;
        public float Radius { get; private set; }

        GameObject _ball;
        Transform _ballT;
        Vector3 _lastCharPos;

        void Awake()
        {
            if (character == null) character = GetComponentInParent<SnowCharacter>();
            if (character == null) character = FindAnyObjectByType<SnowCharacter>();
            if (snowMaterial == null)
            {
                var s = FindAnyObjectByType<SnowSculpture>();
                if (s != null) snowMaterial = s.SnowMaterial;
            }
        }

        // ---------- lifecycle of a held ball ----------

        public void StartNew()
        {
            if (IsHolding) return;
            Spawn(config != null ? config.snowballStartRadius : 0.15f);
        }

        public void PickUp(DroppedSnowball dropped)
        {
            if (IsHolding || dropped == null) return;
            float r = dropped.radius;
            Destroy(dropped.gameObject);
            Spawn(r);
        }

        void Spawn(float radius)
        {
            _ball = GameObject.CreatePrimitive(PrimitiveType.Sphere);
            _ball.name = "HeldSnowball";
            Destroy(_ball.GetComponent<Collider>());
            _ball.layer = LayerMask.NameToLayer("Ignore Raycast");
            if (snowMaterial != null) _ball.GetComponent<MeshRenderer>().sharedMaterial = snowMaterial;
            _ballT = _ball.transform;
            Radius = radius;
            _ballT.localScale = Vector3.one * Radius * 2f;
            _lastCharPos = character != null ? character.transform.position : Vector3.zero;
            _ballT.position = RestPosition();
        }

        public void Drop()
        {
            if (!IsHolding) return;
            _ball.name = "Snowball";
            _ball.layer = 0;
            var col = _ball.AddComponent<SphereCollider>();
            col.radius = 0.5f; // primitive sphere mesh is unit diameter; scale carries the size
            _ball.AddComponent<DroppedSnowball>().radius = Radius;
            _ballT.position = RestPosition();
            _ball = null; _ballT = null;
        }

        /// <summary>Stamp the held ball into a sculpture at the aimed surface point and consume it.</summary>
        public void AttachTo(SnowSculpture sculpture, Vector3 surfacePoint, Vector3 surfaceNormal)
        {
            if (!IsHolding || sculpture == null) return;
            Vector3 centre = AttachCentre(surfacePoint, surfaceNormal);
            sculpture.StampSphere(centre, Radius, attachShoulder);
            sculpture.Remesh();
            sculpture.RebuildColliders();
            Destroy(_ball);
            _ball = null; _ballT = null;
        }

        // ---------- per-frame ----------

        /// <summary>Roll the ball ahead of the character; grows with distance walked. Call when not previewing an attach.</summary>
        public void UpdateRolling()
        {
            if (!IsHolding || character == null) return;
            Vector3 charPos = character.transform.position;
            Vector3 delta = charPos - _lastCharPos; delta.y = 0f;
            float moved = delta.magnitude;
            _lastCharPos = charPos;

            if (moved > 0.0005f && config != null)
            {
                Radius = Mathf.Min(config.snowballMaxRadius, Radius + config.snowballGrowthPerMetre * moved);
                _ballT.localScale = Vector3.one * Radius * 2f;
                // spin about the axis perpendicular to travel, by arc length / radius
                Vector3 axis = Vector3.Cross(Vector3.up, delta.normalized);
                _ballT.Rotate(axis, moved / Radius * Mathf.Rad2Deg, Space.World);
            }

            _ballT.position = Vector3.Lerp(_ballT.position, RestPosition(), 1f - Mathf.Exp(-14f * Time.deltaTime));
        }

        /// <summary>Show the ball where it would fuse into the aimed surface.</summary>
        public void UpdateAttachPreview(Vector3 surfacePoint, Vector3 surfaceNormal)
        {
            if (!IsHolding) return;
            _lastCharPos = character != null ? character.transform.position : _lastCharPos; // no growth while previewing
            _ballT.position = Vector3.Lerp(_ballT.position, AttachCentre(surfacePoint, surfaceNormal), 1f - Mathf.Exp(-20f * Time.deltaTime));
        }

        Vector3 AttachCentre(Vector3 point, Vector3 normal) => point + normal * (Radius * (1f - attachSink));

        Vector3 RestPosition()
        {
            Vector3 fwd = character != null ? character.transform.forward : Vector3.forward;
            Vector3 basePos = (character != null ? character.transform.position : Vector3.zero);
            float capsuleR = 0.35f;
            var cc = character != null ? character.GetComponent<CharacterController>() : null;
            if (cc != null) capsuleR = cc.radius;
            Vector3 p = basePos + fwd * (capsuleR + pushGap + Radius);
            float groundY = basePos.y;
            if (Physics.Raycast(p + Vector3.up * 3f, Vector3.down, out var hit, 6f, groundMask, QueryTriggerInteraction.Ignore))
                groundY = hit.point.y;
            p.y = groundY + Radius;
            return p;
        }
    }
}
