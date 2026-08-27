using Snowfield.Player;
using Snowfield.Sculpture;
using UnityEngine;

namespace Snowfield.Net
{
    /// <summary>
    /// Drives a sculpture that a REMOTE player is moving. Two modes:
    ///   Carried — eased toward the owner's ~10 Hz pose stream; grows the ball when the streamed radius does;
    ///     presses roll trenches into the local snow window from the replicated motion (no extra traffic —
    ///     exactly the SnowballRoller trench rule, derived from the synced transform).
    ///   Flight — a kinematic ballistic arc from the throw event. Physics flights never replay (CLAUDE.md), so
    ///     this is cosmetic: the authoritative splat/rest event snaps the ball to its true outcome and removes this.
    /// </summary>
    public sealed class RemoteBallDrive : MonoBehaviour
    {
        public static RemoteBallDrive Ensure(SnowSculpture s)
        {
            var drive = s.GetComponent<RemoteBallDrive>();
            if (drive == null) drive = s.gameObject.AddComponent<RemoteBallDrive>();
            return drive;
        }

        public static void Clear(SnowSculpture s)
        {
            var drive = s != null ? s.GetComponent<RemoteBallDrive>() : null;
            if (drive != null) Destroy(drive);
        }

        public bool IsCarriedMode => !_flying;

        /// <summary>True once a carried-pose update has actually arrived (a fresh drive has seen none).</summary>
        public bool CarriedActive { get; private set; }

        SnowSculpture _sculpture;
        Snowball _ball;
        bool _hasTarget;
        Vector3 _targetPos;
        Quaternion _targetRot;

        bool _flying;
        Vector3 _velocity;
        Vector3 _spin; // rad/s, world axis * speed

        Vector3 _lastTrench;

        void Awake()
        {
            _sculpture = GetComponent<SnowSculpture>();
            _ball = GetComponent<Snowball>();
            _lastTrench = transform.position;
        }

        public void SetCarriedTarget(Vector3 pos, Quaternion rot, float radius, bool carried)
        {
            _flying = false;
            _hasTarget = true;
            CarriedActive = carried;
            _targetPos = pos;
            _targetRot = rot;
            if (_ball != null && radius > _ball.radius + 0.0025f)
            {
                _ball.Grow(radius);
                _sculpture.Remesh();
            }
        }

        public void BeginFlight(Vector3 velocity, Vector3 spin)
        {
            _flying = true;
            _hasTarget = false;
            _velocity = velocity;
            _spin = spin;
            _lastTrench = transform.position;
        }

        void Update()
        {
            float dt = Time.deltaTime;
            if (_flying)
            {
                _velocity += Physics.gravity * dt;
                Vector3 next = transform.position + _velocity * dt;

                // Don't visibly tunnel into the field while waiting for the authoritative rest event: slide
                // along the snow surface once the arc reaches it, shedding speed like a real skid would.
                float radius = _ball != null ? _ball.radius : 0.15f;
                var ground = SnowGround.Instance;
                if (ground != null && ground.IsCreated)
                {
                    float surfaceY = ground.SampleHeight(next) + radius;
                    if (next.y < surfaceY)
                    {
                        next.y = surfaceY;
                        if (_velocity.y < 0f) _velocity.y = 0f;
                        _velocity *= Mathf.Exp(-2.5f * dt);
                        StampTrench(next, radius);
                    }
                }
                transform.position = next;
                if (_spin.sqrMagnitude > 1e-6f)
                    transform.Rotate(_spin.normalized, _spin.magnitude * Mathf.Rad2Deg * dt, Space.World);
                return;
            }

            if (!_hasTarget) return;
            float k = 1f - Mathf.Exp(-14f * dt);
            Vector3 prev = transform.position;
            transform.position = Vector3.Lerp(prev, _targetPos, k);
            transform.rotation = Quaternion.Slerp(transform.rotation, _targetRot, k);

            // Trench when the replicated ball is rolling along the ground (not lifted to a hand or preview).
            if (_ball != null)
            {
                var ground = SnowGround.Instance;
                if (ground != null && ground.IsCreated)
                {
                    float bottom = transform.position.y - _ball.radius;
                    float surface = ground.SampleHeight(transform.position);
                    if (bottom <= surface + _ball.radius * 0.35f)
                        StampTrench(transform.position, _ball.radius);
                }
            }
        }

        void StampTrench(Vector3 centre, float radius)
        {
            Vector3 d = centre - _lastTrench; d.y = 0f;
            if (d.magnitude < radius * 0.5f) return;
            _lastTrench = centre;
            var ground = SnowGround.Instance;
            var cfg = _sculpture != null ? _sculpture.Config : null;
            if (ground == null || cfg == null) return;
            ground.StampDepression(centre, radius * 0.9f, radius * cfg.rollTrenchDepthFraction, 0.6f);
        }
    }
}
