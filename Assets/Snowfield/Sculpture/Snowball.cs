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
        public float restTime = 0.35f;
        public float restSpeed = 0.08f;
        [Tooltip("Fraction of velocity kept on the first ground contact. Snow swallows most of it.")]
        [Range(0f, 1f)] public float impactKeep = 0.25f;
        public float groundDamping = 8f;
        public float groundAngularDamping = 8f;

        /// <summary>Hook for the field: (ball centre, radius). Set by whoever owns the terrain (SnowballRoller).</summary>
        public static System.Action<Vector3, float> TrenchStamper;

        public State Current { get; private set; } = State.Resting;
        public bool IsLoose { get; private set; } = true;
        public bool IsFlying => Current == State.Flying;
        public SnowSculpture Sculpture => _sculpture != null ? _sculpture : (_sculpture = GetComponent<SnowSculpture>());
        public Vector3 Centre => transform.position;
        public Vector3 GroundPoint => transform.position - Vector3.up * radius;

        SnowSculpture _sculpture;
        Rigidbody _rb;
        SphereCollider _flightCollider;
        float _slowFor;
        bool _onGround;
        Vector3 _lastTrenchPos;

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

        public void Launch(Vector3 velocity)
        {
            SetInteractable(true);
            Sculpture.SetCollidersEnabled(false);
            Sculpture.ClearColliderMeshes(); // a dynamic body may not carry concave mesh colliders, even disabled ones
            _flightCollider = gameObject.AddComponent<SphereCollider>();
            _flightCollider.radius = radius;
            _rb = gameObject.AddComponent<Rigidbody>();
            _rb.mass = Mathf.Max(0.2f, radius * radius * radius * 60f);
            _rb.angularDamping = 1.5f;
            _rb.interpolation = RigidbodyInterpolation.Interpolate;
            _rb.collisionDetectionMode = CollisionDetectionMode.Continuous;
            _rb.linearVelocity = velocity;
            _slowFor = 0f;
            _onGround = false;
            _lastTrenchPos = transform.position;
            Current = State.Flying;
        }

        void FixedUpdate()
        {
            if (!IsFlying || _rb == null) return;
            if (_rb.linearVelocity.magnitude < restSpeed) _slowFor += Time.fixedDeltaTime; else _slowFor = 0f;
            if (_slowFor >= restTime || _rb.IsSleeping()) { Land(); return; }
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
                _rb.linearVelocity *= impactKeep;
                _rb.angularVelocity *= impactKeep;
                _rb.linearDamping = groundDamping;
                _rb.angularDamping = groundAngularDamping;
                _onGround = true;
                return;
            }
            // Splat: sink a little into the surface, then fuse into the target (promoting it if it is a loose ball).
            var contact = col.GetContact(0);
            transform.position = contact.point + contact.normal * (radius * (1f - fuseSink));
            var factory = SculptureFactory.Instance;
            if (factory != null) factory.Fuse(target, this);
            else Destroy(gameObject);
        }

        void OnCollisionExit(Collision col)
        {
            if (!IsFlying) return;
            if (col.collider.GetComponentInParent<SnowSculpture>() != null) return;
            _onGround = false;
            if (_rb != null) { _rb.linearDamping = 0f; _rb.angularDamping = 1.5f; }
        }

        void Land()
        {
            if (_rb != null) { _rb.isKinematic = true; Destroy(_rb); } // Destroy is deferred; kinematic bodies accept concave meshes meanwhile
            if (_flightCollider != null) Destroy(_flightCollider);
            _rb = null; _flightCollider = null;
            Sculpture.SetCollidersEnabled(true);
            Sculpture.ForceRebuildAllColliders();
            Physics.SyncTransforms();
            Current = State.Resting;
        }
    }
}
