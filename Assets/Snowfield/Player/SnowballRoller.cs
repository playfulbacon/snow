using Snowfield.Config;
using Snowfield.Sculpture;
using UnityEngine;

namespace Snowfield.Player
{
    /// <summary>
    /// Moves snow around the field. One object can be carried at a time:
    ///   Ball      — rides the ground in front of the character and rolls as you walk or turn (growing over fresh
    ///               snow, pressing a trench); lifts to preview when aiming at snow, to the hand when charging a throw.
    ///   Sculpture — floats at the carry anchor, held by the grab point.
    /// Fusing goes through <see cref="SculptureFactory.Fuse"/>, which promotes a loose target to a full grid.
    /// Driven by <see cref="SculptTool"/>; owns no input itself.
    /// </summary>
    public class SnowballRoller : MonoBehaviour
    {
        public SculptFeelConfig config;
        [Tooltip("Root of the character the ball rolls in front of. Defaults to this tool's hierarchy root.")]
        public Transform character;
        [Tooltip("Gap between the character's capsule and the ball surface while rolling (m).")]
        public float pushGap = 0.35f;
        [Tooltip("Max distance from the character to scoop or make a mound on the ground (m).")]
        public float startReach = 3.5f;
        [Tooltip("Authored hold point for carried sculptures (and a charging ball). Child of the Player so it turns with you.")]
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

        /// <summary>Whatever is carried (ball or sculpture); null when hands are free.</summary>
        public SnowSculpture Carried { get; private set; }
        /// <summary>The carried object as a loose snowball, or null when carrying a fixed sculpture.</summary>
        public Snowball Ball { get; private set; }
        public bool IsEngaged => Carried != null;
        public bool IsCarrying => Carried != null;
        public bool IsCarryingBall => Carried != null && Ball != null;
        public bool IsCarryingSculpture => Carried != null && Ball == null;
        public float Radius => Ball != null ? Ball.radius : 0f;

        Vector3 _lastTrenchPos;
        Vector3 _grabLocal;         // grab point in the carried object's frame
        Vector3 _bottomLocal;       // lowest point of the carried object (under the grab point), in its frame
        float _footOffset;          // carried object's transform height above the ground under it at pick-up

        void Awake()
        {
            if (character == null) character = transform.root != transform ? transform.root : transform;
            Snowball.TrenchStamper = StampTrenchAt;
            Snowball.RestHeightAdjuster = RestingCentreY;
        }

        void OnDestroy()
        {
            if (Snowball.TrenchStamper != null && ReferenceEquals(Snowball.TrenchStamper.Target, this))
                Snowball.TrenchStamper = null;
            if (Snowball.RestHeightAdjuster != null && ReferenceEquals(Snowball.RestHeightAdjuster.Target, this))
                Snowball.RestHeightAdjuster = null;
        }

        /// <summary>Where a landed ball's centre should rest: on the visible snow surface, sunk by its trench.</summary>
        float RestingCentreY(Vector3 centre, float radius)
        {
            var ground = SnowGround.Instance;
            if (ground == null || !ground.IsCreated) return float.NaN;
            float sink = config != null ? radius * config.rollTrenchDepthFraction * 0.5f : 0f;
            return ground.SampleHeight(centre) + radius - sink;
        }

        CharacterController Capsule => character != null ? character.GetComponent<CharacterController>() : null;

        float CapsuleRadius
        {
            get
            {
                var cc = Capsule;
                return cc != null ? cc.radius : 0.35f;
            }
        }

        public bool CanReachGround(Vector3 groundPoint)
        {
            if (character == null) return false;
            Vector3 d = groundPoint - character.position; d.y = 0f;
            return d.magnitude - CapsuleRadius <= startReach;
        }

        // ---------- engage / release ----------

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
            var ground = SnowGround.Instance;
            if (ground != null && config != null)
                ground.StampDepression(groundPoint, r * 1.6f, config.scoopDivotDepth, 0.6f);
        }

        /// <summary>
        /// Mass continuity: take a freshly cut chunk (already filled with the snow that left the sculpture) into the
        /// hands. With hands free the chunk itself is carried, shape and all; with a ball already held the chunk's
        /// volume is packed into it and the chunk is discarded.
        /// </summary>
        public void TakeChunk(Snowball chunk, float volume)
        {
            if (chunk == null) return;
            float maxR = config != null ? config.snowballMaxRadius : 0.6f;
            if (IsCarryingSculpture || volume <= 0f) { Destroy(chunk.gameObject); return; }

            if (Ball != null)
            {
                Ball.Grow(Mathf.Min(maxR, RadiusForVolume(VolumeOf(Ball.radius) + volume)));
                Ball.Sculpture.Remesh();
                Destroy(chunk.gameObject);
                return;
            }

            chunk.radius = Mathf.Clamp(RadiusForVolume(volume), 0.05f, maxR); // nominal size for carrying/rolling/throwing
            chunk.Sculpture.Remesh();
            chunk.Sculpture.RebuildColliders();
            Engage(chunk.Sculpture, chunk.Centre);
            chunk.SetState(Snowball.State.Carrying);
        }

        static float VolumeOf(float r) => 4f / 3f * Mathf.PI * r * r * r;
        static float RadiusForVolume(float v) => Mathf.Pow(Mathf.Max(0f, v) * 3f / (4f * Mathf.PI), 1f / 3f);

        /// <summary>Fuse the carried object into <paramref name="target"/> exactly where it currently sits — no repositioning.</summary>
        public void PlaceWhereItIs(SnowSculpture target)
        {
            if (!IsCarrying || target == null || target == Carried) return;
            var factory = SculptureFactory.Instance;
            if (factory == null) return;
            var carried = Carried;
            Carried = null; Ball = null;
            SetInteractable(carried, true);
            factory.Fuse(target, carried);
        }

        /// <summary>Let go: a ball falls from where it is under gravity; a sculpture is set down on the ground beneath it.</summary>
        public void DropFalling()
        {
            if (!IsCarrying) return;
            if (Ball == null) { DropHere(); return; }
            var ball = Ball;
            Carried = null; Ball = null;
            ball.Launch(Vector3.zero); // gravity does the rest; Land() restores its colliders
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
            _lastTrenchPos = grabPoint;
        }

        /// <summary>Leave the carried object exactly where it is.</summary>
        public void Release()
        {
            if (!IsEngaged) return;
            Rest();
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

        /// <summary>
        /// Carrying: a ball rolls on the ground only while <paramref name="rollOnGround"/> (cursor near the player);
        /// otherwise it is held overhead at the anchor. <paramref name="previewGrabPoint"/> overrides (attach preview);
        /// <paramref name="liftToHand"/> raises a charging ball to the anchor.
        /// </summary>
        public void UpdateCarrying(Vector3? previewGrabPoint, bool liftToHand = false, bool rollOnGround = false)
        {
            if (!IsCarrying) return;
            var t = Carried.transform;

            if (Ball != null && rollOnGround && !liftToHand && !previewGrabPoint.HasValue)
            {
                RollAtFeet();
                return;
            }

            Vector3 desired = previewGrabPoint.HasValue
                ? t.position + (previewGrabPoint.Value - t.TransformPoint(_grabLocal))
                : t.position + (CarryPosition() - t.TransformPoint(_bottomLocal));
            t.position = Vector3.Lerp(t.position, desired, 1f - Mathf.Exp(-18f * Time.deltaTime));
        }

        /// <summary>The carried ball touches the ground ahead of the character; moving or turning rolls and grows it.</summary>
        void RollAtFeet()
        {
            if (character == null) return;
            var ch = character;
            var t = Ball.transform;
            var terrain = SnowGround.Instance;

            Vector3 goal = ch.position + ch.forward * (CapsuleRadius + pushGap + Ball.radius);
            Vector3 prev = t.position;
            Vector3 newPos = Vector3.Lerp(prev, goal, 1f - Mathf.Exp(-14f * Time.deltaTime));

            Vector3 delta = newPos - prev; delta.y = 0f;
            float moved = delta.magnitude;
            Vector3 dir = moved > 0.0005f ? delta.normalized : ch.forward;
            Vector3 ahead = newPos + dir * Ball.radius; // ride the snow it rolls ONTO, not its own trench

            if (moved > 0.0005f && config != null)
            {
                bool fresh = terrain == null || terrain.IsFreshAt(ahead, config.footprintDepth * 1.5f);
                Vector3 axis = Vector3.Cross(Vector3.up, dir);
                t.Rotate(axis, moved / Ball.radius * Mathf.Rad2Deg, Space.World);
                if (fresh && Ball.radius < config.snowballMaxRadius)
                    Ball.Grow(Mathf.Min(config.snowballMaxRadius, Ball.radius + config.snowballGrowthPerMetre * moved));
            }

            float sink = config != null ? Ball.radius * config.rollTrenchDepthFraction * 0.5f : 0f;
            float groundY = terrain != null && terrain.IsCreated ? terrain.SampleHeight(ahead) : GroundHeightAt(newPos);
            newPos.y = Mathf.Lerp(prev.y, groundY + Ball.radius - sink, 1f - Mathf.Exp(-12f * Time.deltaTime));
            t.position = newPos;
            StampTrench(newPos, Ball.radius, ref _lastTrenchPos);

            Ball.Sculpture.Remesh(); // every frame: the ball's grid is small and stepped growth reads as snapping
        }

        void StampTrenchAt(Vector3 ballCentre, float radius)
        {
            var ground = SnowGround.Instance;
            if (ground == null || config == null) return;
            ground.StampDepression(ballCentre, radius * 0.9f, radius * config.rollTrenchDepthFraction, 0.6f);
        }

        /// <summary>Press a trench under a rolling ball every half-radius of travel.</summary>
        void StampTrench(Vector3 ballCentre, float radius, ref Vector3 lastStamp)
        {
            Vector3 d = ballCentre - lastStamp; d.y = 0f;
            if (d.magnitude < radius * 0.5f) return;
            lastStamp = ballCentre;
            StampTrenchAt(ballCentre, radius);
        }

        /// <summary>Where a carried sculpture's bottom (or a charging ball) rests while held.</summary>
        public Vector3 CarryPosition()
        {
            if (carryAnchor != null) return carryAnchor.position;
            var t = character != null ? character : transform;
            var cc = Capsule;
            float eye = cc != null ? cc.height - 0.15f : 1.6f; // just below the capsule top
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

        float GroundHeightAt(Vector3 p)
        {
            // Ground snow data first: a raycast can land on another sculpture's snow and inflate the "ground" height.
            var ground = SnowGround.Instance;
            if (ground != null && ground.IsCreated) return ground.SampleHeight(p);
            if (Physics.Raycast(p + Vector3.up * 3f, Vector3.down, out var hit, 8f, groundMask, QueryTriggerInteraction.Ignore))
                return hit.point.y;
            return character != null ? character.position.y : 0f;
        }
    }
}
