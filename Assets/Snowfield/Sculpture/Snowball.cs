using UnityEngine;

namespace Snowfield.Sculpture
{
    /// <summary>
    /// Marks a <see cref="SnowSculpture"/> as a snowball: a small grid centred on the ball that can be pushed, carried
    /// and thrown while it is <see cref="IsLoose"/>. Brushing keeps it loose; attaching anything (another ball, an
    /// accessory) fixes it. Growth re-stamps the sphere, so fresh snow covers carving on a ball you keep rolling.
    /// Input and push/carry motion live in the player's SnowballRoller; this owns radius, state and flight.
    /// </summary>
    [RequireComponent(typeof(SnowSculpture))]
    public class Snowball : MonoBehaviour
    {
        public enum State { Resting, Pushing, Carrying, Flying }

        public float radius = 0.15f;
        [Range(0f, 1f)] public float stampShoulder = 0.75f;
        [Range(0f, 1f)] public float fuseSink = 0.45f;

        [Header("Flight")]
        [Tooltip("Packed snow (kg/m3), used for the flight mass. A ball with a real weight shoves what it hits instead of pinging off it.")]
        public float density = 350f;
        public float restTime = 0.35f;
        public float restSpeed = 0.08f;
        [Tooltip("Fraction of the speed INTO a surface kept on contact — the bounce. Snow swallows nearly all of it.")]
        [Range(0f, 1f)] public float impactKeep = 0.12f;
        [Tooltip("Fraction of the speed ALONG a surface kept on contact — the skid. This is what makes a flat throw roll out instead of dying where it lands.")]
        [Range(0f, 1f)] public float slideKeep = 0.35f;
        [Tooltip("Speed-proportional resistance once the ball is down — snow being compacted. Handles the fast end of a skid; it can never stop the ball on its own.")]
        public float groundDamping = 3f;
        [Tooltip("Plough resistance while rolling (m/s2): the snow the ball shoves aside, which is a roughly CONSTANT force rather than one that fades with speed. This is what actually brings the ball to a stop, and it sets the steepest slope the ball can hold — about 18 degrees at 2.9.")]
        public float ploughResistance = 2.9f;
        public float groundAngularDamping = 3f;
        [Tooltip("Speed INTO a surface (m/s) that a contact must carry to count as a real impact and take the tangential bite. Below it the ball is only rolling over a seam and keeps its speed.")]
        public float impactSpeedThreshold = 1.5f;
        [Tooltip("Seconds out of contact before the ball counts as airborne again. Without this, a roll across terrain seams flickers its rolling resistance off and coasts forever.")]
        public float groundGrace = 0.12f;
        [Tooltip("Backstop for slopes too steep for the plough to hold: below this speed (m/s) for creepTime, the ball is planted where it is. Well under any real roll, so it never cuts one short.")]
        public float creepSpeed = 1f;
        public float creepTime = 2f;
        [Tooltip("Spin drag in the air. Low, so a thrown ball visibly keeps turning the whole way.")]
        public float airAngularDamping = 0.15f;
        [Tooltip("Seconds spent easing onto the snow surface after landing, instead of popping there in one frame.")]
        public float settleTime = 0.15f;

        /// <summary>Hook for the field: (ball centre, radius). Set by whoever owns the terrain (SnowballRoller).</summary>
        public static System.Action<Vector3, float> TrenchStamper;
        /// <summary>
        /// Hook for the field: (ball centre, radius) → desired resting centre Y, or NaN for "leave it". The physics
        /// floor can sit below the visible snow surface; this lifts a landed ball onto the snow. Set by SnowballRoller.
        /// </summary>
        public static System.Func<Vector3, float, float> RestHeightAdjuster;

        public State Current { get; private set; } = State.Resting;
        public bool IsLoose { get; private set; } = true;
        public bool IsFlying => Current == State.Flying;
        public SnowSculpture Sculpture => _sculpture != null ? _sculpture : (_sculpture = GetComponent<SnowSculpture>());
        public Vector3 Centre => transform.position;
        public Vector3 GroundPoint => transform.position - Vector3.up * radius;
        /// <summary>Flight mass (kg) this ball would have at its current radius. Throw power scales against it.</summary>
        public float Mass => Mathf.Max(0.2f, 4f / 3f * Mathf.PI * radius * radius * radius * density);

        SnowSculpture _sculpture;
        Rigidbody _rb;
        SphereCollider _flightCollider;
        float _slowFor;
        bool _onGround;
        float _airborneFor;
        float _creepFor;
        Vector3 _lastTrenchPos;
        Vector3 _settleFrom;
        float _settleTargetY;
        float _settleFor = -1f;

        public void SetState(State s) => Current = s;

        /// <summary>No longer pushable/carryable. Called when something is attached to this ball.</summary>
        public void Fix() { IsLoose = false; Current = State.Resting; }

        /// <summary>Re-stamp the sphere at a new radius (max with existing density). Remesh is the caller's job.</summary>
        public void Grow(float newRadius)
        {
            radius = newRadius;
            Sculpture.StampSphere(transform.position, radius, stampShoulder);
        }

        /// <summary>While engaged (pushed/carried) the ball must not block rays or the player.</summary>
        public void SetInteractable(bool on)
        {
            int layer = on ? 0 : LayerMask.NameToLayer("Ignore Raycast");
            foreach (var t in GetComponentsInChildren<Transform>(true)) t.gameObject.layer = layer;
            Sculpture.SetCollidersEnabled(on);
        }

        // ---------- flight ----------

        /// <summary>
        /// Begin a physics flight. <paramref name="spin"/> is angular velocity in rad/s; <paramref name="ignore"/> is
        /// anything the ball must pass straight through — the thrower above all, since the ball leaves the hand only
        /// centimetres from their own capsule and a ball that clips its thrower reads as a throw that died for nothing.
        /// </summary>
        public void Launch(Vector3 velocity, Vector3 spin = default, Collider[] ignore = null)
        {
            _settleFor = -1f;
            SetInteractable(true);
            Sculpture.SetCollidersEnabled(false);
            Sculpture.ClearColliderMeshes(); // a dynamic body may not carry concave mesh colliders, even disabled ones
            _flightCollider = gameObject.AddComponent<SphereCollider>();
            _flightCollider.radius = radius;
            if (ignore != null)
                foreach (var c in ignore)
                    // Both colliders have to be live or PhysX rejects the pair and Unity logs it as an error.
                    if (c != null && c.enabled && c.gameObject.activeInHierarchy)
                        Physics.IgnoreCollision(_flightCollider, c, true);
            _rb = gameObject.AddComponent<Rigidbody>();
            _rb.mass = Mass;
            _rb.angularDamping = airAngularDamping;
            _rb.interpolation = RigidbodyInterpolation.Interpolate;
            _rb.collisionDetectionMode = CollisionDetectionMode.Continuous;
            _rb.linearVelocity = velocity;
            _rb.angularVelocity = spin;
            _slowFor = 0f;
            _onGround = false;
            _airborneFor = groundGrace;
            _creepFor = 0f;
            _lastTrenchPos = transform.position;
            Current = State.Flying;
        }

        void FixedUpdate()
        {
            if (!IsFlying || _rb == null) return;
            // Grounded is a timer, not an edge. PhysX drops and remakes contact constantly as a ball rolls over
            // terrain seams, so keying rolling resistance off Enter/Exit leaves it switched off half the roll.
            _airborneFor += Time.fixedDeltaTime;
            bool grounded = _airborneFor < groundGrace;
            if (grounded != _onGround)
            {
                _onGround = grounded;
                _rb.linearDamping = grounded ? groundDamping : 0f;
                _rb.angularDamping = grounded ? groundAngularDamping : airAngularDamping;
            }
            if (_onGround && ploughResistance > 0f)
            {
                // Damping alone is proportional to speed, so on a slope it only ever asymptotes — and it asymptotes
                // ABOVE restSpeed, so the ball never settles and trundles downhill for ever. A ploughing ball is
                // resisted by a roughly constant force instead, and a constant force has a stall threshold.
                Vector3 v = _rb.linearVelocity;
                Vector3 travel = new Vector3(v.x, 0f, v.z);
                float drop = ploughResistance * Time.fixedDeltaTime;
                _rb.linearVelocity = travel.magnitude <= drop
                    ? new Vector3(0f, v.y, 0f)          // zero it outright; overshooting crawls backwards and never settles
                    : v - travel.normalized * drop;
            }
            if (_rb.linearVelocity.magnitude < restSpeed) _slowFor += Time.fixedDeltaTime; else _slowFor = 0f;
            if (_slowFor >= restTime || _rb.IsSleeping()) { Land(); return; }
            // Past the slope the plough can hold, the ball settles at a slow equilibrium instead of resting, and
            // would creep downhill for ever — never landing, never getting its mesh colliders back. Plant it.
            if (_onGround && _rb.linearVelocity.magnitude < creepSpeed) _creepFor += Time.fixedDeltaTime; else _creepFor = 0f;
            if (_creepFor >= creepTime) { Land(); return; }
            if (_onGround && TrenchStamper != null)
            {
                Vector3 d = transform.position - _lastTrenchPos; d.y = 0f;
                if (d.magnitude >= radius * 0.5f) { _lastTrenchPos = transform.position; TrenchStamper(transform.position, radius); }
            }
        }

        void OnCollisionEnter(Collision col)
        {
            if (!IsFlying) return;
            var target = col.collider.GetComponentInParent<SnowSculpture>();
            if (target == null || target == Sculpture)
            {
                Bite(col.GetContact(0).normal);
                _airborneFor = 0f;
                return;
            }
            // Splat: sink a little into the surface, then fuse into the target (promoting it if it is a loose ball).
            var contact = col.GetContact(0);
            transform.position = contact.point + contact.normal * (radius * (1f - fuseSink));
            var factory = SculptureFactory.Instance;
            if (factory != null) factory.Fuse(target, this);
            else Destroy(gameObject);
        }

        /// <summary>
        /// Snow contact, split along the surface: the speed driving the ball INTO it is swallowed (snow barely
        /// bounces), the speed ALONG it mostly survives and becomes rolling spin. Scaling the whole velocity instead
        /// — which is what a single factor does — kills a glancing hit as hard as a head-on one, so every throw
        /// stops the instant it touches down no matter how flat it came in.
        /// </summary>
        void Bite(Vector3 normal)
        {
            Vector3 v = _rb.linearVelocity;
            float into = Vector3.Dot(v, normal);
            Vector3 along = v - normal * into;
            // Only a genuine slam takes the tangential bite. A rolling ball re-enters contact over and over, and
            // biting each time would brake it to a stop that nothing on screen accounts for.
            float keep = -into >= impactSpeedThreshold ? slideKeep : 1f;
            _rb.linearVelocity = along * keep + normal * (into < 0f ? -into * impactKeep : into);

            Vector3 axis = Vector3.Cross(normal, along);
            if (axis.sqrMagnitude > 1e-6f && radius > 1e-4f)
                _rb.angularVelocity = axis.normalized * (along.magnitude * keep / radius); // roll, not skid
            else
                _rb.angularVelocity *= keep;
        }

        void OnCollisionStay(Collision col)
        {
            if (IsFlying) _airborneFor = 0f; // still touching; FixedUpdate's grace timer decides when that lapses
        }

        void Land()
        {
            if (_rb != null) { _rb.isKinematic = true; Destroy(_rb); } // Destroy is deferred; kinematic bodies accept concave meshes meanwhile
            if (_flightCollider != null) { _flightCollider.enabled = false; Destroy(_flightCollider); } // disable now: the adjuster raycasts
            _rb = null; _flightCollider = null;
            if (RestHeightAdjuster != null)
            {
                float y = RestHeightAdjuster(transform.position, radius);
                // Ease onto the snow rather than teleport: the sphere collider rides the terrain under the shell,
                // so a landed ball is always a snow-depth low, and snapping that gap in one frame is a visible pop.
                if (!float.IsNaN(y) && y > transform.position.y)
                {
                    if (settleTime > 0f) { _settleFrom = transform.position; _settleTargetY = y; _settleFor = 0f; }
                    else transform.position = new Vector3(transform.position.x, y, transform.position.z);
                }
            }
            Sculpture.SetCollidersEnabled(true);
            Sculpture.ForceRebuildAllColliders();
            Physics.SyncTransforms();
            Current = State.Resting;
        }

        void Update()
        {
            if (_settleFor < 0f) return;
            if (Current != State.Resting) { _settleFor = -1f; return; } // picked up mid-settle
            _settleFor += Time.deltaTime;
            float t = Mathf.Clamp01(_settleFor / Mathf.Max(0.01f, settleTime));
            transform.position = new Vector3(_settleFrom.x,
                Mathf.Lerp(_settleFrom.y, _settleTargetY, t * t * (3f - 2f * t)), _settleFrom.z);
            if (t < 1f) return;
            _settleFor = -1f;
            Physics.SyncTransforms();
        }
    }
}
