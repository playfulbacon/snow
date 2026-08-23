using Snowfield.Config;
using Snowfield.Field;
using Snowfield.Sculpture;
using UnityEngine;

namespace Snowfield.Player
{
    /// <summary>A snowball resting on the field. Push it (hold LMB) or pick it up (RMB).</summary>
    public class DroppedSnowball : MonoBehaviour
    {
        public float radius;
        public Vector3 GroundPoint => transform.position - Vector3.up * radius;
    }

    /// <summary>
    /// A snowball in flight. Physics-driven until it either splats into a sculpture (density stamp, prop destroyed)
    /// or comes to rest, at which point the Rigidbody is removed and it is an ordinary <see cref="DroppedSnowball"/>.
    /// </summary>
    public class ThrownSnowball : MonoBehaviour
    {
        public float radius;
        public float attachSink = 0.45f;
        public float attachShoulder = 0.75f;
        [Tooltip("Seconds below the rest speed before the ball is considered landed.")]
        public float restTime = 0.35f;
        public float restSpeed = 0.08f;
        [Header("Snow landing")]
        [Tooltip("Fraction of velocity kept on the first ground contact. Snow swallows most of it.")]
        [Range(0f, 1f)] public float impactKeep = 0.25f;
        [Tooltip("Linear damping applied while touching the ground (fresh snow drags hard).")]
        public float groundDamping = 8f;
        public float groundAngularDamping = 8f;

        Rigidbody _rb;
        float _slowFor;
        Vector3 _lastTrenchPos;
        bool _onGround;

        void Awake() { _rb = GetComponent<Rigidbody>(); _lastTrenchPos = transform.position; }

        void FixedUpdate()
        {
            if (_rb.linearVelocity.magnitude < restSpeed) _slowFor += Time.fixedDeltaTime; else _slowFor = 0f;
            if (_slowFor >= restTime || _rb.IsSleeping()) { Land(); return; }
            if (_onGround) SnowballRoller.StampTrench(transform.position, radius, ref _lastTrenchPos);
        }

        void OnCollisionEnter(Collision col)
        {
            var sculpture = col.collider.GetComponentInParent<SnowSculpture>();
            if (sculpture == null)
            {
                // Landed in snow: bleed off most of the energy at once, then drag hard while in contact.
                _rb.linearVelocity *= impactKeep;
                _rb.angularVelocity *= impactKeep;
                _rb.linearDamping = groundDamping;
                _rb.angularDamping = groundAngularDamping;
                _onGround = true;
                return;
            }
            var contact = col.GetContact(0);
            Vector3 centre = contact.point + contact.normal * (radius * (1f - attachSink));
            sculpture.StampSphere(centre, radius, attachShoulder);
            sculpture.Remesh();
            sculpture.RebuildColliders();
            Destroy(gameObject);
        }

        void OnCollisionExit(Collision col)
        {
            if (col.collider.GetComponentInParent<SnowSculpture>() != null) return;
            _onGround = false;
            if (_rb != null) { _rb.linearDamping = 0f; _rb.angularDamping = 1.5f; } // brief hop after a bounce
        }

        void Land()
        {
            Destroy(this); // first: the Rigidbody cannot go while this component still holds it
            Destroy(_rb);
            var dropped = GetComponent<DroppedSnowball>();
            if (dropped == null) dropped = gameObject.AddComponent<DroppedSnowball>();
            dropped.radius = radius;
        }
    }

    /// <summary>
    /// Faked snowball rolling. One ball can be engaged at a time, in one of two ways:
    ///   Pushing  — the ball keeps its offset from the character (in the character's frame) while the button is held,
    ///              growing with distance over fresh snow and pressing a trench into the field.
    ///   Carrying — the ball floats at the carry anchor; set down, attach to a sculpture, stack on another ball, or throw.
    /// Attaching/stacking stamps density into a sculpture (creating one when needed) and destroys the prop.
    /// Driven by <see cref="SculptTool"/> in Empty Hand mode; owns no input itself.
    /// </summary>
    public class SnowballRoller : MonoBehaviour
    {
        public enum State { None, Pushing, Carrying }

        public SculptFeelConfig config;
        [Tooltip("Character the ball is pushed by. Defaults to the SnowCharacter in parents.")]
        public SnowCharacter character;
        [Tooltip("Material for the ball. Defaults to the sculpture factory's snow material.")]
        public Material snowMaterial;
        [Tooltip("Max gap between the character's capsule and the ball surface to start pushing (m).")]
        public float pushReach = 1.2f;
        [Tooltip("Authored hold point: the carried ball's centre sits here. Make it a child of the Player so it turns with you.")]
        public Transform carryAnchor;
        [Tooltip("Fallback if no anchor is set: forward of the character, and above the eye line (m).")]
        public Vector2 carryOffset = new Vector2(0.55f, 0.35f);
        [Tooltip("A thrown ball starts this far in front of the camera (plus its radius) so it flies along the reticle.")]
        public float throwStartDistance = 0.25f;

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
        Vector3 _pushLocalOffset;   // ball centre in the character's frame (y ignored)
        Vector3 _lastTrenchPos;

        void Awake()
        {
            if (character == null) character = GetComponentInParent<SnowCharacter>();
            if (character == null) character = FindAnyObjectByType<SnowCharacter>();
            if (snowMaterial == null)
            {
                var f = SculptureFactory.Instance != null ? SculptureFactory.Instance : FindAnyObjectByType<SculptureFactory>();
                if (f != null) snowMaterial = f.snowMaterial;
                else { var s = FindAnyObjectByType<SnowSculpture>(); if (s != null) snowMaterial = s.SnowMaterial; }
            }
        }

        float CapsuleRadius
        {
            get
            {
                var cc = character != null ? character.GetComponent<CharacterController>() : null;
                return cc != null ? cc.radius : 0.35f;
            }
        }

        // ---------- reach ----------

        public bool CanPush(DroppedSnowball ball)
        {
            if (ball == null || character == null) return false;
            Vector3 d = ball.transform.position - character.transform.position; d.y = 0f;
            return d.magnitude - ball.radius - CapsuleRadius <= pushReach;
        }

        public bool CanReachGround(Vector3 groundPoint)
        {
            if (character == null) return false;
            Vector3 d = groundPoint - character.transform.position; d.y = 0f;
            return d.magnitude - CapsuleRadius <= pushReach + (config != null ? config.snowballStartRadius : 0.15f);
        }

        // ---------- engage / release ----------

        /// <summary>Make a fresh ball resting on <paramref name="groundPoint"/> and start pushing it from there.</summary>
        public void StartNew(Vector3 groundPoint)
        {
            if (IsEngaged) return;
            float r = config != null ? config.snowballStartRadius : 0.15f;
            var go = GameObject.CreatePrimitive(PrimitiveType.Sphere);
            go.name = "Snowball";
            if (snowMaterial != null) go.GetComponent<MeshRenderer>().sharedMaterial = snowMaterial;
            var ball = go.AddComponent<DroppedSnowball>();
            ball.radius = r;
            go.transform.localScale = Vector3.one * r * 2f;
            go.transform.position = groundPoint + Vector3.up * r;
            Engage(ball, State.Pushing);
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
            _lastTrenchPos = _ballT.position;
            if (character != null)
            {
                _pushLocalOffset = character.transform.InverseTransformPoint(_ballT.position);
                _pushLocalOffset.y = 0f;
            }
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
            Consume(sculpture, AttachCentre(surfacePoint, surfaceNormal));
        }

        /// <summary>Carried ball onto a resting ball: the resting ball becomes a new sculpture and the carried one fuses on top.</summary>
        public void StackOnto(DroppedSnowball bottom)
        {
            if (!IsCarrying || bottom == null || bottom == _ball) return;
            Vector3 centre = StackCentre(bottom);
            var sculpture = ConvertToSculpture(bottom);
            if (sculpture == null) return;
            Consume(sculpture, centre);
        }

        /// <summary>Turn a resting ball into a brand-new sculpture (grid centred on it). Returns null without a factory.</summary>
        public SnowSculpture ConvertToSculpture(DroppedSnowball ball)
        {
            if (ball == null) return null;
            var factory = SculptureFactory.Instance;
            if (factory == null) { Debug.LogWarning("[Snowfield] No SculptureFactory in the scene"); return null; }
            if (ball == _ball) { _ball = null; _ballT = null; Current = State.None; }
            var sculpture = factory.CreateAt(ball.GroundPoint);
            sculpture.StampSphere(ball.transform.position, ball.radius, attachShoulder);
            sculpture.Remesh();
            sculpture.RebuildColliders();
            Destroy(ball.gameObject);
            return sculpture;
        }

        void Consume(SnowSculpture sculpture, Vector3 centre)
        {
            sculpture.StampSphere(centre, Radius, attachShoulder);
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

        /// <summary>Pushing: the ball keeps its offset in the character's frame; grows over fresh snow; leaves a trench.</summary>
        public void UpdatePushing()
        {
            if (!IsPushing || character == null) return;
            Vector3 charPos = character.transform.position;
            Vector3 delta = charPos - _lastCharPos; delta.y = 0f;
            float moved = delta.magnitude;
            _lastCharPos = charPos;

            Vector3 goal = character.transform.TransformPoint(_pushLocalOffset);

            if (moved > 0.0005f && config != null)
            {
                var terrain = SnowTerrain.Instance;
                bool fresh = terrain == null || terrain.IsFreshAt(goal, config.footprintDepth * 1.5f);
                if (fresh) Radius = Mathf.Min(config.snowballMaxRadius, Radius + config.snowballGrowthPerMetre * moved);
                _ballT.localScale = Vector3.one * Radius * 2f;
                Vector3 axis = Vector3.Cross(Vector3.up, delta.normalized);
                _ballT.Rotate(axis, moved / Radius * Mathf.Rad2Deg, Space.World);
            }
            goal.y = GroundHeightAt(goal) + Radius;
            _ballT.position = goal;
            StampTrench(_ballT.position, Radius, ref _lastTrenchPos);
        }

        /// <summary>Press a trench under a rolling ball every half-radius of travel.</summary>
        public static void StampTrench(Vector3 ballCentre, float radius, ref Vector3 lastStamp)
        {
            var terrain = SnowTerrain.Instance;
            if (terrain == null || terrain.Config == null) return;
            Vector3 d = ballCentre - lastStamp; d.y = 0f;
            if (d.magnitude < radius * 0.5f) return;
            lastStamp = ballCentre;
            terrain.StampDepression(ballCentre, radius * 0.9f, radius * terrain.Config.rollTrenchDepthFraction, 0.6f);
        }

        /// <summary>Carrying: float at the anchor, or preview at a target position.</summary>
        public void UpdateCarrying(Vector3? previewCentre)
        {
            if (!IsCarrying) return;
            Vector3 goal = previewCentre ?? CarryPosition();
            _ballT.position = Vector3.Lerp(_ballT.position, goal, 1f - Mathf.Exp(-18f * Time.deltaTime));
        }

        public Vector3 CarryPosition()
        {
            if (carryAnchor != null) return carryAnchor.position;
            var t = character != null ? character.transform : transform;
            float eye = character != null ? character.EyeHeight : 1.6f;
            return t.position + t.forward * carryOffset.x + Vector3.up * (eye + carryOffset.y + Radius);
        }

        /// <summary>Launch the carried ball from <paramref name="origin"/> along <paramref name="direction"/> with power 0..1.</summary>
        public void Throw(Vector3 origin, Vector3 direction, float power)
        {
            if (!IsCarrying) return;
            direction = direction.normalized;
            _ballT.position = origin + direction * (throwStartDistance + Radius);
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
            rb.linearVelocity = direction * speed + Vector3.up * throwLift;

            _ball = null; _ballT = null;
            Current = State.None;
        }

        public Vector3 AttachCentre(Vector3 point, Vector3 normal) => point + normal * (Radius * (1f - attachSink));
        public Vector3 GroundCentre(Vector3 groundPoint) => groundPoint + Vector3.up * Radius;
        public Vector3 StackCentre(DroppedSnowball bottom) => bottom.transform.position + Vector3.up * (bottom.radius + Radius * (1f - attachSink));

        float GroundHeightAt(Vector3 p)
        {
            if (Physics.Raycast(p + Vector3.up * 3f, Vector3.down, out var hit, 8f, groundMask, QueryTriggerInteraction.Ignore))
                return hit.point.y;
            var terrain = SnowTerrain.Instance;
            return terrain != null ? terrain.SampleHeight(p) : (character != null ? character.transform.position.y : 0f);
        }
    }
}
