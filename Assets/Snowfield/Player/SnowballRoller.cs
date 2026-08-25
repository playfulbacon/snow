using Snowfield.Config;
using Snowfield.Field;
using Snowfield.Sculpture;
using UnityEngine;

namespace Snowfield.Player
{
    /// <summary>
    /// Moves snow around the field:
    ///   Pushing  — a loose snowball keeps its offset in the character's frame while the button is held, growing over
    ///              fresh snow (re-stamping its sphere) and pressing a trench into the field.
    ///   Carrying — a loose snowball OR a whole fixed sculpture floats at the carry anchor (held by the grab point);
    ///              set it down on the ground, fuse it into another sculpture, or (balls only) throw it.
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
        [Tooltip("Gap between the character's capsule and the ball surface while rolling (m).")]
        public float pushGap = 0.35f;
        [Tooltip("Max distance from the character to start a new ball on the ground (m). The ball spawns under the reticle.")]
        public float startReach = 3.5f;
        [Tooltip("Authored hold point: the carried object's grab point sits here. Make it a child of the Player so it turns with you.")]
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

        /// <summary>Whatever is engaged (ball or sculpture); null when hands are free.</summary>
        public SnowSculpture Carried { get; private set; }
        /// <summary>The engaged object as a loose snowball, or null when carrying a fixed sculpture.</summary>
        public Snowball Ball { get; private set; }
        public bool IsEngaged => Carried != null;
        public bool IsPushing => Ball != null && Ball.Current == Snowball.State.Pushing;
        public bool IsCarrying => Carried != null && !IsPushing;
        public bool IsCarryingBall => IsCarrying && Ball != null;
        public bool IsCarryingSculpture => IsCarrying && Ball == null;
        public float Radius => Ball != null ? Ball.radius : 0f;

        Vector3 _lastCharPos;
        Vector3 _pushLocalOffset;   // ball centre in the character's frame (y ignored)
        Vector3 _lastTrenchPos;
        float _remeshAccumulator;
        Vector3 _grabLocal;         // grab point in the carried object's frame
        Vector3 _bottomLocal;       // lowest point of the carried object (under the grab point), in its frame
        float _footOffset;          // carried object's transform height above the ground under it at pick-up

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
            Engage(ball.Sculpture, ball.Centre);
            ball.SetState(Snowball.State.Pushing);
        }

        /// <summary>Scoop a handful of snow off the field straight into the hands; leaves a divot.</summary>
        public void ScoopFrom(Vector3 groundPoint)
        {
            if (IsEngaged) return;
            var factory = SculptureFactory.Instance;
            if (factory == null) return;
            float r = config != null ? config.scoopRadius : 0.12f;
            var ball = factory.CreateSnowball(groundPoint + Vector3.up * r, r);
            Engage(ball.Sculpture, ball.Centre);
            ball.SetState(Snowball.State.Carrying);
            var terrain = SnowTerrain.Instance;
            if (terrain != null && config != null)
                terrain.StampDepression(groundPoint, r * 1.6f, config.scoopDivotDepth, 0.6f);
        }

        /// <summary>Shift pressed while carrying a ball: put it on the ground ahead and roll it.</summary>
        public void BeginRollFromCarry()
        {
            if (!IsCarryingBall || character == null) return;
            var t = character.transform;
            Vector3 ahead = t.position + t.forward * (CapsuleRadius + pushGap + Ball.radius);
            ahead.y = GroundHeightAt(ahead) + Ball.radius;
            Ball.transform.position = ahead;
            _lastCharPos = t.position;
            _lastTrenchPos = ahead;
            _pushLocalOffset = t.InverseTransformPoint(ahead);
            _pushLocalOffset.y = 0f;
            Ball.SetState(Snowball.State.Pushing);
        }

        /// <summary>Shift released while rolling: lift the ball back into the hands.</summary>
        public void ReturnToCarry()
        {
            if (!IsPushing) return;
            Ball.Sculpture.Remesh();
            Ball.SetState(Snowball.State.Carrying);
        }

        /// <summary>Set the carried object down on the ground directly beneath where it hangs.</summary>
        public void DropHere()
        {
            if (!IsCarrying) return;
            var t = Carried.transform;
            Vector3 grabWorld = t.TransformPoint(_grabLocal);
            Vector3 ground = new Vector3(grabWorld.x, GroundHeightAt(grabWorld), grabWorld.z);
            PlaceOnGround(ground);
        }

        public void StartPushing(Snowball ball)
        {
            if (IsEngaged || ball == null || !ball.IsLoose || ball.IsFlying) return;
            Engage(ball.Sculpture, ball.Centre);
            ball.SetState(Snowball.State.Pushing);
        }

        /// <summary>Pick up a loose ball (by its centre) or a fixed sculpture (by the aimed point).</summary>
        public void PickUp(SnowSculpture sculpture, Vector3 grabPoint)
        {
            if (IsEngaged || sculpture == null) return;
            var ball = sculpture.GetComponent<Snowball>();
            if (ball != null)
            {
                if (!ball.IsLoose || ball.IsFlying) return;
                grabPoint = ball.Centre;
            }
            Engage(sculpture, grabPoint);
            if (ball != null) ball.SetState(Snowball.State.Carrying);
        }

        void Engage(SnowSculpture sculpture, Vector3 grabPoint)
        {
            Carried = sculpture;
            Ball = sculpture.GetComponent<Snowball>();
            SetInteractable(sculpture, false);
            _grabLocal = sculpture.transform.InverseTransformPoint(grabPoint);
            _footOffset = sculpture.transform.position.y - GroundHeightAt(grabPoint);
            // Bottom: a ball's underside; a sculpture's grid floor (its snow starts at ground level).
            Vector3 bottomWorld = Ball != null
                ? Ball.Centre - Vector3.up * Ball.radius
                : new Vector3(grabPoint.x, sculpture.transform.TransformPoint(sculpture.gridOffset).y, grabPoint.z);
            _bottomLocal = sculpture.transform.InverseTransformPoint(bottomWorld);
            _lastCharPos = character != null ? character.transform.position : Vector3.zero;
            _lastTrenchPos = grabPoint;
            _remeshAccumulator = 0f;
            if (character != null)
            {
                _pushLocalOffset = character.transform.InverseTransformPoint(grabPoint);
                _pushLocalOffset.y = 0f;
            }
        }

        /// <summary>Leave the carried object exactly where it is (push release / mode change).</summary>
        public void Release()
        {
            if (!IsEngaged) return;
            Rest();
        }

        /// <summary>Set the carried object down so its grab point is over <paramref name="groundPoint"/> at its original ground clearance.</summary>
        public void PlaceOnGround(Vector3 groundPoint)
        {
            if (!IsCarrying) return;
            var t = Carried.transform;
            if (Ball != null)
            {
                t.position = groundPoint + Vector3.up * Ball.radius;
            }
            else
            {
                Vector3 grabWorld = t.TransformPoint(_grabLocal);
                Vector3 shift = groundPoint - grabWorld;
                shift.y = (groundPoint.y + _footOffset) - t.position.y;
                t.position += shift;
            }
            Rest();
        }

        /// <summary>Fuse the carried object into a sculpture surface at the aimed point (loose balls included).</summary>
        public void AttachTo(SnowSculpture target, Vector3 surfacePoint, Vector3 surfaceNormal)
        {
            if (!IsCarrying || target == null || target == Carried) return;
            var factory = SculptureFactory.Instance;
            if (factory == null) return;
            var carried = Carried;
            var ball = Ball;
            Carried = null; Ball = null;
            if (ball != null)
                carried.transform.position = AttachCentre(ball, surfacePoint, surfaceNormal);
            else
            {
                Vector3 grabWorld = carried.transform.TransformPoint(_grabLocal);
                carried.transform.position += surfacePoint - grabWorld;
            }
            SetInteractable(carried, true);
            factory.Fuse(target, carried);
        }

        void Rest()
        {
            var carried = Carried;
            var ball = Ball;
            Carried = null; Ball = null;
            SetInteractable(carried, true);          // enable first: PhysX only tracks live colliders
            carried.Remesh();
            carried.ForceRebuildAllColliders();      // it moved/grew while its colliders were off
            Physics.SyncTransforms();
            if (ball != null) ball.SetState(Snowball.State.Resting);
        }

        static void SetInteractable(SnowSculpture s, bool on)
        {
            int layer = on ? 0 : LayerMask.NameToLayer("Ignore Raycast");
            foreach (var t in s.GetComponentsInChildren<Transform>(true)) t.gameObject.layer = layer;
            s.SetCollidersEnabled(on);
            foreach (var c in s.GetComponentsInChildren<Collider>(true))
                if (c.GetComponentInParent<SculptureProp>() != null) c.enabled = on; // props ride along
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
            var terrain = SnowTerrain.Instance;
            Vector3 dir = moved > 0.0005f ? delta.normalized : character.transform.forward;
            // Ride on the snow the ball is rolling ONTO (leading edge), not the trench it just pressed under itself.
            Vector3 ahead = goal + dir * Ball.radius;

            if (moved > 0.0005f && config != null)
            {
                bool fresh = terrain == null || terrain.IsFreshAt(ahead, config.footprintDepth * 1.5f);
                Vector3 axis = Vector3.Cross(Vector3.up, dir);
                t.Rotate(axis, moved / Ball.radius * Mathf.Rad2Deg, Space.World);
                if (fresh && Ball.radius < config.snowballMaxRadius)
                    Ball.Grow(Mathf.Min(config.snowballMaxRadius, Ball.radius + config.snowballGrowthPerMetre * moved));
            }
            // Height from the heightmap itself (continuous), not the collider (re-cooked only a few times a second).
            float sink = config != null ? Ball.radius * config.rollTrenchDepthFraction * 0.5f : 0f;
            float groundY = terrain != null ? terrain.SampleHeight(ahead) : GroundHeightAt(goal);
            float targetY = groundY + Ball.radius - sink;
            goal.y = Mathf.Lerp(t.position.y, targetY, 1f - Mathf.Exp(-12f * Time.deltaTime));
            t.position = goal;
            StampTrench(t.position, Ball.radius, ref _lastTrenchPos);

            Ball.Sculpture.Remesh(); // every frame: the ball's grid is small and stepped growth reads as snapping
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

        /// <summary>Carrying: the object's bottom sits on the anchor, or its grab point goes to a preview position.</summary>
        public void UpdateCarrying(Vector3? previewGrabPoint)
        {
            if (!IsCarrying) return;
            var t = Carried.transform;
            Vector3 desired;
            if (previewGrabPoint.HasValue)
                desired = t.position + (previewGrabPoint.Value - t.TransformPoint(_grabLocal));
            else
                desired = t.position + (CarryPosition() - t.TransformPoint(_bottomLocal));
            t.position = Vector3.Lerp(t.position, desired, 1f - Mathf.Exp(-18f * Time.deltaTime));
        }

        /// <summary>Where the carried object's bottom rests while held.</summary>
        public Vector3 CarryPosition()
        {
            if (carryAnchor != null) return carryAnchor.position;
            var t = character != null ? character.transform : transform;
            float eye = character != null ? character.EyeHeight : 1.6f;
            return t.position + t.forward * carryOffset.x + Vector3.up * (eye + carryOffset.y);
        }

        /// <summary>Launch the carried ball from <paramref name="origin"/> along <paramref name="direction"/> with power 0..1. Sculptures are not thrown.</summary>
        public void Throw(Vector3 origin, Vector3 direction, float power)
        {
            if (!IsCarryingBall) return;
            direction = direction.normalized;
            var ball = Ball;
            Carried = null; Ball = null;
            ball.transform.position = origin + direction * (throwStartDistance + ball.radius);
            float speed = Mathf.Lerp(throwSpeedMin, throwSpeedMax, Mathf.Clamp01(power));
            ball.Launch(direction * speed + Vector3.up * throwLift);
        }

        /// <summary>Where the carried object's grab point previews when aimed at a surface.</summary>
        public Vector3 AttachCentre(Vector3 point, Vector3 normal) =>
            Ball != null ? AttachCentre(Ball, point, normal) : point + normal * 0.02f;
        static Vector3 AttachCentre(Snowball b, Vector3 point, Vector3 normal) => point + normal * (b.radius * (1f - b.fuseSink));

        /// <summary>Where the carried object's grab point previews when aimed at the ground.</summary>
        public Vector3 GroundCentre(Vector3 groundPoint)
        {
            if (Ball != null) return groundPoint + Vector3.up * Ball.radius;
            var t = Carried.transform;
            Vector3 grabWorld = t.TransformPoint(_grabLocal);
            float grabAboveRoot = grabWorld.y - t.position.y;
            return new Vector3(groundPoint.x, groundPoint.y + _footOffset + grabAboveRoot, groundPoint.z);
        }

        float GroundHeightAt(Vector3 p)
        {
            // Terrain data first: a raycast can land on another sculpture's snow and inflate the "ground" height.
            var terrain = SnowTerrain.Instance;
            if (terrain != null && terrain.IsCreated) return terrain.SampleHeight(p);
            if (Physics.Raycast(p + Vector3.up * 3f, Vector3.down, out var hit, 8f, groundMask, QueryTriggerInteraction.Ignore))
                return hit.point.y;
            return character != null ? character.transform.position.y : 0f;
        }
    }
}
