using Snowfield.Config;
using Snowfield.Sculpture;
using UnityEngine;

namespace Snowfield.Player
{
    /// <summary>A snowball resting on the field. Push it (hold LMB) or pick it up (RMB).</summary>
    public class DroppedSnowball : MonoBehaviour
    {
        public float radius;
    }

    /// <summary>
    /// A snowball in flight. Physics-driven until it either splats into a sculpture (density stamp, prop destroyed)
    /// or comes to rest, at which point the Rigidbody is removed and it is an ordinary <see cref="DroppedSnowball"/>.
    /// </summary>
    [RequireComponent(typeof(Rigidbody))]
    public class ThrownSnowball : MonoBehaviour
    {
        public float radius;
        public float attachSink = 0.45f;
        public float attachShoulder = 0.75f;
        [Tooltip("Seconds below the rest speed before the ball is considered landed.")]
        public float restTime = 0.35f;
        public float restSpeed = 0.08f;

        Rigidbody _rb;
        float _slowFor;

        void Awake() => _rb = GetComponent<Rigidbody>();

        void FixedUpdate()
        {
            if (_rb.linearVelocity.magnitude < restSpeed) _slowFor += Time.fixedDeltaTime; else _slowFor = 0f;
            if (_slowFor >= restTime || _rb.IsSleeping()) Land();
        }

        void OnCollisionEnter(Collision col)
        {
            var sculpture = col.collider.GetComponentInParent<SnowSculpture>();
            if (sculpture == null) return;
            var contact = col.GetContact(0);
            Vector3 centre = contact.point + contact.normal * (radius * (1f - attachSink));
            sculpture.StampSphere(centre, radius, attachShoulder);
            sculpture.Remesh();
            sculpture.RebuildColliders();
            Destroy(gameObject);
        }

        void Land()
        {
            Destroy(_rb);
            var dropped = GetComponent<DroppedSnowball>();
            if (dropped == null) dropped = gameObject.AddComponent<DroppedSnowball>();
            dropped.radius = radius;
            Destroy(this);
        }
    }

    /// <summary>
    /// Faked snowball rolling. One ball can be engaged at a time, in one of two ways:
    ///   Pushing  — the ball rolls ahead of the character while the button is held, growing with distance.
    ///   Carrying — the ball floats at hand height; it can be set down on the ground or attached to a sculpture.
    /// Attaching stamps a density sphere into the target sculpture and destroys the prop. No physics, no voxels while rolling.
    /// Driven by <see cref="SculptTool"/> in Empty Hand mode; owns no input itself.
    /// </summary>
    public class SnowballRoller : MonoBehaviour
    {
        public enum State { None, Pushing, Carrying }

        public SculptFeelConfig config;
        [Tooltip("Character the ball is pushed in front of. Defaults to the SnowCharacter in parents.")]
        public SnowCharacter character;
        [Tooltip("Material for the ball. Defaults to the first sculpture's snow material.")]
        public Material snowMaterial;
        [Tooltip("Gap between the character's capsule and the ball surface while pushing (m).")]
        public float pushGap = 0.35f;
        [Tooltip("Where a carried ball floats: forward of the character, and above the eye line (m).")]
        public Vector2 carryOffset = new Vector2(0.55f, 0.35f);

        [Header("Throw")]
        public float throwSpeedMin = 3f;
        public float throwSpeedMax = 11f;
        [Tooltip("Extra upward speed so a flat throw still arcs.")]
        public float throwLift = 1.5f;
        [Tooltip("Seconds of holding to reach full power.")]
        public float chargeTime = 1.2f;
        [Tooltip("How deep the ball sinks into the target when attached, as a fraction of its radius.")]
        [Range(0f, 1f)] public float attachSink = 0.45f;
        [Range(0f, 1f)] public float attachShoulder = 0.75f;
        [Tooltip("Raycast mask used to find the ground under the ball.")]
        public LayerMask groundMask = ~0;

        public State Current { get; private set; } = State.None;
        public bool IsPushing => Current == State.Pushing;
        public bool IsCarrying => Current == State.Carrying;
        public bool IsEngaged => Current != State.None;
        public float Radius { get; private set; }

        DroppedSnowball _ball;
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

        // ---------- engage / release ----------

        /// <summary>Make a fresh ball on the ground ahead and start pushing it.</summary>
        public void StartNew()
        {
            if (IsEngaged) return;
            float r = config != null ? config.snowballStartRadius : 0.15f;
            var go = GameObject.CreatePrimitive(PrimitiveType.Sphere);
            go.name = "Snowball";
            if (snowMaterial != null) go.GetComponent<MeshRenderer>().sharedMaterial = snowMaterial;
            var ball = go.AddComponent<DroppedSnowball>();
            ball.radius = r;
            go.transform.localScale = Vector3.one * r * 2f;
            Engage(ball, State.Pushing);
            _ballT.position = PushRestPosition();
        }

        public void StartPushing(DroppedSnowball ball) => Engage(ball, State.Pushing);
        public void PickUp(DroppedSnowball ball) => Engage(ball, State.Carrying);

        void Engage(DroppedSnowball ball, State state)
        {
            if (IsEngaged || ball == null) return;
            _ball = ball;
            _ballT = ball.transform;
            Radius = ball.radius;
            SetInteractable(false);
            _lastCharPos = character != null ? character.transform.position : Vector3.zero;
            Current = state;
        }

        /// <summary>Leave the ball exactly where it is (push release).</summary>
        public void Release()
        {
            if (!IsEngaged) return;
            Rest();
        }

        /// <summary>Set a carried ball down resting on the ground at a point.</summary>
        public void PlaceOnGround(Vector3 groundPoint)
        {
            if (!IsCarrying) return;
            _ballT.position = groundPoint + Vector3.up * Radius;
            Rest();
        }

        /// <summary>Stamp the engaged ball into a sculpture at the aimed surface point and consume it.</summary>
        public void AttachTo(SnowSculpture sculpture, Vector3 surfacePoint, Vector3 surfaceNormal)
        {
            if (!IsEngaged || sculpture == null) return;
            sculpture.StampSphere(AttachCentre(surfacePoint, surfaceNormal), Radius, attachShoulder);
            sculpture.Remesh();
            sculpture.RebuildColliders();
            Destroy(_ball.gameObject);
            _ball = null; _ballT = null;
            Current = State.None;
        }

        void Rest()
        {
            _ball.radius = Radius;
            SetInteractable(true);
            _ball = null; _ballT = null;
            Current = State.None;
        }

        void SetInteractable(bool on)
        {
            var go = _ball.gameObject;
            go.layer = on ? 0 : LayerMask.NameToLayer("Ignore Raycast");
            var col = go.GetComponent<SphereCollider>();
            if (col == null) { col = go.AddComponent<SphereCollider>(); col.radius = 0.5f; } // unit sphere mesh; scale carries size
            col.enabled = on;
        }

        // ---------- per-frame ----------

        /// <summary>Pushing: roll the ball ahead of the character; grows with distance walked.</summary>
        public void UpdatePushing()
        {
            if (!IsPushing || character == null) return;
            Vector3 charPos = character.transform.position;
            Vector3 delta = charPos - _lastCharPos; delta.y = 0f;
            float moved = delta.magnitude;
            _lastCharPos = charPos;

            if (moved > 0.0005f && config != null)
            {
                Radius = Mathf.Min(config.snowballMaxRadius, Radius + config.snowballGrowthPerMetre * moved);
                _ballT.localScale = Vector3.one * Radius * 2f;
                Vector3 axis = Vector3.Cross(Vector3.up, delta.normalized);
                _ballT.Rotate(axis, moved / Radius * Mathf.Rad2Deg, Space.World);
            }
            _ballT.position = Vector3.Lerp(_ballT.position, PushRestPosition(), 1f - Mathf.Exp(-14f * Time.deltaTime));
        }

        /// <summary>Carrying: float above the head, or preview at a target position.</summary>
        public void UpdateCarrying(Vector3? previewCentre)
        {
            if (!IsCarrying) return;
            Vector3 goal = previewCentre ?? CarryPosition();
            _ballT.position = Vector3.Lerp(_ballT.position, goal, 1f - Mathf.Exp(-18f * Time.deltaTime));
        }

        public Vector3 CarryPosition()
        {
            var t = character != null ? character.transform : transform;
            float eye = character != null ? character.EyeHeight : 1.6f;
            return t.position + t.forward * carryOffset.x + Vector3.up * (eye + carryOffset.y + Radius);
        }

        /// <summary>Launch the carried ball along <paramref name="direction"/> with power 0..1.</summary>
        public void Throw(Vector3 direction, float power)
        {
            if (!IsCarrying) return;
            var go = _ball.gameObject;
            go.layer = 0;
            var col = go.GetComponent<SphereCollider>();
            if (col == null) { col = go.AddComponent<SphereCollider>(); col.radius = 0.5f; }
            col.enabled = true;
            var rb = go.GetComponent<Rigidbody>();
            if (rb == null) rb = go.AddComponent<Rigidbody>();
            rb.mass = Mathf.Max(0.2f, Radius * Radius * Radius * 60f);
            rb.angularDamping = 1.5f;
            rb.interpolation = RigidbodyInterpolation.Interpolate;
            rb.collisionDetectionMode = CollisionDetectionMode.Continuous;
            var thrown = go.AddComponent<ThrownSnowball>();
            thrown.radius = Radius;
            thrown.attachSink = attachSink;
            thrown.attachShoulder = attachShoulder;
            _ball.radius = Radius;

            float speed = Mathf.Lerp(throwSpeedMin, throwSpeedMax, Mathf.Clamp01(power));
            rb.linearVelocity = direction.normalized * speed + Vector3.up * throwLift;

            _ball = null; _ballT = null;
            Current = State.None;
        }

        public Vector3 AttachCentre(Vector3 point, Vector3 normal) => point + normal * (Radius * (1f - attachSink));
        public Vector3 GroundCentre(Vector3 groundPoint) => groundPoint + Vector3.up * Radius;

        Vector3 PushRestPosition()
        {
            var t = character != null ? character.transform : transform;
            float capsuleR = 0.35f;
            var cc = character != null ? character.GetComponent<CharacterController>() : null;
            if (cc != null) capsuleR = cc.radius;
            Vector3 p = t.position + t.forward * (capsuleR + pushGap + Radius);
            float groundY = t.position.y;
            if (Physics.Raycast(p + Vector3.up * 3f, Vector3.down, out var hit, 6f, groundMask, QueryTriggerInteraction.Ignore))
                groundY = hit.point.y;
            p.y = groundY + Radius;
            return p;
        }
    }
}
