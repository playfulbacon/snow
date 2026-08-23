using Snowfield.Config;
using Snowfield.Field;
using Snowfield.Sculpture;
using UnityEngine;

namespace Snowfield.Player
{
    /// <summary>
    /// Moves loose snowballs (which are small <see cref="SnowSculpture"/>s with a <see cref="Snowball"/> component):
    ///   Pushing  — the ball keeps its offset in the character's frame while the button is held, growing over fresh
    ///              snow (re-stamping its sphere) and pressing a trench into the field.
    ///   Carrying — the ball floats at the carry anchor; set down, fuse onto any sculpture surface, or throw.
    /// Fusing goes through <see cref="SculptureFactory.Fuse"/>, which promotes a loose target to a full grid.
    /// Driven by <see cref="SculptTool"/> in Empty Hand mode; owns no input itself.
    /// </summary>
    public class SnowballRoller : MonoBehaviour
    {
        public SculptFeelConfig config;
        [Tooltip("Character the ball is pushed by. Defaults to the SnowCharacter in parents.")]
        public SnowCharacter character;
        [Tooltip("Max gap between the character's capsule and the ball surface to start pushing (m).")]
        public float pushReach = 1.2f;
        [Tooltip("Max distance from the character to start a new ball on the ground (m). The ball spawns under the reticle.")]
        public float startReach = 3.5f;
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
        [Tooltip("Raycast mask used to find the ground under the ball.")]
        public LayerMask groundMask = ~0;

        public Snowball Ball { get; private set; }
        public bool IsPushing => Ball != null && Ball.Current == Snowball.State.Pushing;
        public bool IsCarrying => Ball != null && Ball.Current == Snowball.State.Carrying;
        public bool IsEngaged => Ball != null;
        public float Radius => Ball != null ? Ball.radius : 0f;

        Vector3 _lastCharPos;
        Vector3 _pushLocalOffset;   // ball centre in the character's frame (y ignored)
        Vector3 _lastTrenchPos;
        float _remeshAccumulator;

        void Awake()
        {
            if (character == null) character = GetComponentInParent<SnowCharacter>();
            if (character == null) character = FindAnyObjectByType<SnowCharacter>();
            Snowball.TrenchStamper = StampTrenchAt;
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

        public bool CanPush(Snowball ball)
        {
            if (ball == null || character == null || !ball.IsLoose) return false;
            Vector3 d = ball.Centre - character.transform.position; d.y = 0f;
            return d.magnitude - ball.radius - CapsuleRadius <= pushReach;
        }

        public bool CanReachGround(Vector3 groundPoint)
        {
            if (character == null) return false;
            Vector3 d = groundPoint - character.transform.position; d.y = 0f;
            return d.magnitude - CapsuleRadius <= startReach;
        }

        // ---------- engage / release ----------

        /// <summary>Make a fresh ball resting on <paramref name="groundPoint"/> and start pushing it from there.</summary>
        public void StartNew(Vector3 groundPoint)
        {
            if (IsEngaged) return;
            var factory = SculptureFactory.Instance;
            if (factory == null) { Debug.LogWarning("[Snowfield] No SculptureFactory in the scene"); return; }
            float r = config != null ? config.snowballStartRadius : 0.15f;
            var ball = factory.CreateSnowball(groundPoint + Vector3.up * r, r);
            Engage(ball, Snowball.State.Pushing);
        }

        public void StartPushing(Snowball ball) => Engage(ball, Snowball.State.Pushing);
        public void PickUp(Snowball ball) => Engage(ball, Snowball.State.Carrying);

        void Engage(Snowball ball, Snowball.State state)
        {
            if (IsEngaged || ball == null || !ball.IsLoose || ball.IsFlying) return;
            Ball = ball;
            ball.SetInteractable(false);
            ball.SetState(state);
            _lastCharPos = character != null ? character.transform.position : Vector3.zero;
            _lastTrenchPos = ball.Centre;
            _remeshAccumulator = 0f;
            if (character != null)
            {
                _pushLocalOffset = character.transform.InverseTransformPoint(ball.Centre);
                _pushLocalOffset.y = 0f;
            }
        }

        /// <summary>Leave the ball exactly where it is (push release / mode change).</summary>
        public void Release()
        {
            if (!IsEngaged) return;
            Rest();
        }

        /// <summary>Set a carried ball down resting on the ground at a point.</summary>
        public void PlaceOnGround(Vector3 groundPoint)
        {
            if (!IsCarrying) return;
            Ball.transform.position = groundPoint + Vector3.up * Ball.radius;
            Rest();
        }

        /// <summary>Fuse the carried ball into a sculpture surface at the aimed point (loose balls included).</summary>
        public void AttachTo(SnowSculpture target, Vector3 surfacePoint, Vector3 surfaceNormal)
        {
            if (!IsCarrying || target == null) return;
            var factory = SculptureFactory.Instance;
            if (factory == null) return;
            var ball = Ball;
            Ball = null;
            ball.transform.position = AttachCentre(ball, surfacePoint, surfaceNormal);
            ball.SetInteractable(true);
            factory.Fuse(target, ball);
        }

        void Rest()
        {
            var ball = Ball;
            Ball = null;
            ball.SetInteractable(true);               // enable first: PhysX only tracks live colliders
            ball.Sculpture.Remesh();
            ball.Sculpture.ForceRebuildAllColliders(); // the ball moved/grew while its colliders were off
            Physics.SyncTransforms();
            ball.SetState(Snowball.State.Resting);
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
            var t = Ball.transform;

            if (moved > 0.0005f && config != null)
            {
                var terrain = SnowTerrain.Instance;
                bool fresh = terrain == null || terrain.IsFreshAt(goal, config.footprintDepth * 1.5f);
                Vector3 axis = Vector3.Cross(Vector3.up, delta.normalized);
                t.Rotate(axis, moved / Ball.radius * Mathf.Rad2Deg, Space.World);
                if (fresh && Ball.radius < config.snowballMaxRadius)
                {
                    goal.y = GroundHeightAt(goal) + Ball.radius;
                    t.position = goal;
                    Ball.Grow(Mathf.Min(config.snowballMaxRadius, Ball.radius + config.snowballGrowthPerMetre * moved));
                }
            }
            goal.y = GroundHeightAt(goal) + Ball.radius;
            t.position = goal;
            StampTrench(t.position, Ball.radius, ref _lastTrenchPos);

            _remeshAccumulator += Time.deltaTime;
            if (config != null && _remeshAccumulator >= 1f / config.remeshHz)
            {
                _remeshAccumulator = 0f;
                Ball.Sculpture.Remesh();
            }
        }

        static void StampTrenchAt(Vector3 ballCentre, float radius)
        {
            var terrain = SnowTerrain.Instance;
            if (terrain == null || terrain.Config == null) return;
            terrain.StampDepression(ballCentre, radius * 0.9f, radius * terrain.Config.rollTrenchDepthFraction, 0.6f);
        }

        /// <summary>Press a trench under a rolling ball every half-radius of travel.</summary>
        public static void StampTrench(Vector3 ballCentre, float radius, ref Vector3 lastStamp)
        {
            Vector3 d = ballCentre - lastStamp; d.y = 0f;
            if (d.magnitude < radius * 0.5f) return;
            lastStamp = ballCentre;
            StampTrenchAt(ballCentre, radius);
        }

        /// <summary>Carrying: float at the anchor, or preview at a target position.</summary>
        public void UpdateCarrying(Vector3? previewCentre)
        {
            if (!IsCarrying) return;
            Vector3 goal = previewCentre ?? CarryPosition();
            var t = Ball.transform;
            t.position = Vector3.Lerp(t.position, goal, 1f - Mathf.Exp(-18f * Time.deltaTime));
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
            var ball = Ball;
            Ball = null;
            ball.transform.position = origin + direction * (throwStartDistance + ball.radius);
            float speed = Mathf.Lerp(throwSpeedMin, throwSpeedMax, Mathf.Clamp01(power));
            ball.Launch(direction * speed + Vector3.up * throwLift);
        }

        public Vector3 AttachCentre(Vector3 point, Vector3 normal) => Ball != null ? AttachCentre(Ball, point, normal) : point;
        static Vector3 AttachCentre(Snowball b, Vector3 point, Vector3 normal) => point + normal * (b.radius * (1f - b.fuseSink));
        public Vector3 GroundCentre(Vector3 groundPoint) => groundPoint + Vector3.up * Radius;

        float GroundHeightAt(Vector3 p)
        {
            if (Physics.Raycast(p + Vector3.up * 3f, Vector3.down, out var hit, 8f, groundMask, QueryTriggerInteraction.Ignore))
                return hit.point.y;
            var terrain = SnowTerrain.Instance;
            return terrain != null ? terrain.SampleHeight(p) : (character != null ? character.transform.position.y : 0f);
        }
    }
}
