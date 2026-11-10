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
        [Tooltip("Authored hold point for snow that needs both hands (and for carried sculptures). Overhead. Child of the Player so it turns with you.")]
        public Transform carryAnchor;
        [Tooltip("Hold point for snow small enough for one hand — down at your side rather than hoisted overhead. Falls back to carryAnchor.")]
        public Transform palmAnchor;
        [Tooltip("Fallback if no anchor is set: forward of the character, and above the eye line (m).")]
        public Vector2 carryOffset = new Vector2(0.55f, 0.35f);
        [Tooltip("A thrown ball starts this far in front of the camera (plus its radius) so it flies along the reticle.")]
        public float throwStartDistance = 0.25f;

        [Header("Throw")]
        public float throwSpeedMin = 7f;
        public float throwSpeedMax = 30f;
        [Tooltip("Degrees above your aim that a throw leaves the hand, so a flat one still arcs. An ANGLE, not an added speed: a fixed upward speed bends a weak throw far more than a hard one, so the ball never lands where the reticle said it would.")]
        [Range(0f, 30f)] public float throwLiftDegrees = 4f;
        [Tooltip("Ball radius that throws at full speed (m). Anything heavier than this leaves the hand slower.")]
        public float throwReferenceRadius = 0.15f;
        [Tooltip("How hard size slows a throw. 0 = size is irrelevant (every ball flies the same, which is the tell that nothing is being simulated), 1 = speed falls with the cube root of the mass, 1.5 = with its square root, roughly what an arm does.")]
        [Range(0f, 2f)] public float throwMassFalloff = 1f;
        [Tooltip("How much of your own motion the throw carries. Throwing while sprinting should send it further than throwing from standstill.")]
        [Range(0f, 1f)] public float throwInheritVelocity = 0.7f;
        [Tooltip("Backspin at full power (turns per second). Barely readable on a smooth sphere, obvious on a lumpy marching-cubes one.")]
        public float throwSpin = 2.5f;
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
        /// <summary>
        /// Whether what is carried takes both hands — the one place that question is answered. Drives both the
        /// hands (<see cref="SculptTool"/>) and where the snow is held: only something you need both arms for
        /// gets hoisted overhead. A whole sculpture always does.
        /// </summary>
        public bool TwoHandedCarry => Ball == null || config == null || Ball.radius >= config.handTwoHandedRadius;
        /// <summary>
        /// Snow too heavy to get off the ground. With no cap on how big a ball can grow, this is what keeps a
        /// waist-high boulder from floating over your head: past the lift capacity the ball stays down, rolls
        /// instead of being held, cannot be thrown, and fuses where it sits. Another player with their hands on
        /// it raises the capacity, so a ball one of you could only shove, two of you can hoist.
        /// </summary>
        public bool TooHeavyToLift => Ball != null && config != null && !CanLift(Ball.Sculpture);
        /// <summary>What the hands are holding onto: a ball's centre, a sculpture's grab point.</summary>
        public Vector3 HoldCentre => Carried == null ? Vector3.zero
            : Ball != null ? Ball.Centre : Carried.transform.TransformPoint(_grabLocal);
        /// <summary>How wide the hands have to spread around <see cref="HoldCentre"/> (m).</summary>
        public float HoldRadius => Ball != null ? Ball.radius : 0.2f;

        Vector3 _lastTrenchPos;
        Vector3 _grabLocal;         // grab point in the carried object's frame
        Vector3 _bottomLocal;       // lowest point of the carried object (under the grab point), in its frame
        float _footOffset;          // carried object's transform height above the ground under it at pick-up

        SculptTool _tool;

        void Awake()
        {
            if (character == null) character = transform.root != transform ? transform.root : transform;
            _tool = GetComponent<SculptTool>();   // the aim: what the player's hands are reaching for
            Snowball.TrenchStamper = StampTrenchAt;
            Snowball.RestHeightAdjuster = RestingCentreY;
            // Sculpture cannot see SnowGround, so hand the drape a ground probe the same way.
            DrapeCurve.GroundSampler = GroundHeightForDrape;
        }

        void OnDestroy()
        {
            if (Snowball.TrenchStamper != null && ReferenceEquals(Snowball.TrenchStamper.Target, this))
                Snowball.TrenchStamper = null;
            if (Snowball.RestHeightAdjuster != null && ReferenceEquals(Snowball.RestHeightAdjuster.Target, this))
                Snowball.RestHeightAdjuster = null;
            if (DrapeCurve.GroundSampler != null && ReferenceEquals(DrapeCurve.GroundSampler.Target, this))
                DrapeCurve.GroundSampler = null;
        }

        /// <summary>Ground height under a strand particle; -infinity when there is no field, so nothing clamps.</summary>
        float GroundHeightForDrape(Vector3 p)
        {
            var ground = SnowGround.Instance;
            return ground != null && ground.IsCreated ? ground.SampleHeight(p) : float.NegativeInfinity;
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

        // ---------- what you can get off the ground ----------

        /// <summary>
        /// Weight of a thing, in kg. A ball answers from its nominal radius (free, and the number the player can
        /// see); anything else is measured from its density, which <see cref="SnowSculpture.Mass"/> caches.
        /// </summary>
        public static float MassOf(SnowSculpture s)
        {
            if (s == null) return 0f;
            var ball = s.GetComponent<Snowball>();
            return ball != null ? ball.Mass : s.Mass;
        }

        /// <summary>
        /// How much this group can get off the ground (kg): one pair of arms per player with their hands on it.
        /// Every pusher derives the same number from the same facts, so nobody has to be told whether a lift is
        /// allowed - and a snowman two people cannot shift becomes liftable the moment a third steps up to it.
        /// </summary>
        public float LiftCapacity(SnowSculpture s) =>
            config == null ? float.MaxValue : config.liftMass * SnowPush.Arms(s);

        /// <summary>Whether the arms currently on <paramref name="s"/> can pick it up at all.</summary>
        public bool CanLift(SnowSculpture s) => s != null && MassOf(s) <= LiftCapacity(s);

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
            SculptureNet.RaiseGroundScooped(groundPoint, ball);
        }

        /// <summary>
        /// Mass continuity: take a freshly cut chunk (already filled with the snow that left the sculpture) into the
        /// hands. With hands free the chunk itself is carried, shape and all; with a ball already held the chunk's
        /// volume is packed into it and the chunk is discarded.
        /// </summary>
        public void TakeChunk(Snowball chunk, float volume)
        {
            if (chunk == null) return;
            float maxR = config != null ? config.MaxBallRadius : 0.6f;
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
            ball.Launch(Vector3.zero, Vector3.zero, ThrowerColliders()); // gravity does the rest; Land() restores its colliders
            SculptureNet.RaiseThrown(ball, Vector3.zero, Vector3.zero);
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
            SculptureNet.RaiseGrabbed(sculpture);
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
            SculptureNet.RaiseRested(carried, carried.transform.position, carried.transform.rotation);
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

            // Too heavy to lift overrides every caller: there is nothing a cursor can ask for that gets a
            // boulder off the ground. The tool knows this too (no throw charge, no attach preview), so this is
            // a backstop rather than the rule being enforced in two places.
            if (Ball != null && (TooHeavyToLift || (rollOnGround && !liftToHand && !previewGrabPoint.HasValue)))
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

            // How fast this much snow will go, for everyone with their hands on it. Published before the move so
            // the character controller is already capped to it: the ball is glued in front of the player, so a
            // ball that cannot keep up has to slow the player down rather than be left behind by them.
            float limit = PushSpeedFor(Ball.Mass, Ball.Sculpture);
            SnowPush.Set(Ball.Sculpture, limit);
            _pushedThisFrame = true;

            Vector3 goal = ch.position + ch.forward * (CapsuleRadius + pushGap + Ball.radius);
            Vector3 prev = t.position;
            Vector3 newPos = Vector3.Lerp(prev, goal, 1f - Mathf.Exp(-14f * Time.deltaTime));

            Vector3 delta = newPos - prev; delta.y = 0f;
            // The weight bites here: however far the glue would like the ball to travel this frame, the arms
            // behind it only manage so much. A boulder visibly trails your facing while you shuffle it round.
            float maxStep = limit * Time.deltaTime;
            if (delta.magnitude > maxStep && maxStep > 0f)
            {
                delta = delta.normalized * maxStep;
                newPos = prev + delta;
            }
            float moved = delta.magnitude;
            Vector3 dir = moved > 0.0005f ? delta.normalized : ch.forward;
            Vector3 ahead = newPos + dir * Ball.radius; // ride the snow it rolls ONTO, not its own trench

            if (moved > 0.0005f && config != null)
            {
                bool fresh = terrain == null || terrain.IsFreshAt(ahead, config.footprintDepth * 1.5f);
                Vector3 axis = Vector3.Cross(Vector3.up, dir);
                t.Rotate(axis, moved / Ball.radius * Mathf.Rad2Deg, Space.World);
                // No design cap on ball size any more — only the ball's own grid wall. What stops you is the
                // line above: every centimetre of radius is snow you then have to shove.
                if (fresh && Ball.radius < config.MaxBallRadius)
                    Ball.Grow(Mathf.Min(config.MaxBallRadius, Ball.radius + config.snowballGrowthPerMetre * moved));
            }

            float sink = config != null ? Ball.radius * config.rollTrenchDepthFraction * 0.5f : 0f;
            float groundY = terrain != null && terrain.IsCreated ? terrain.SampleHeight(ahead) : GroundHeightAt(newPos);
            newPos.y = Mathf.Lerp(prev.y, groundY + Ball.radius - sink, 1f - Mathf.Exp(-12f * Time.deltaTime));
            t.position = newPos;
            StampTrench(newPos, Ball.radius, ref _lastTrenchPos);

            Ball.Sculpture.Remesh(); // every frame: the ball's grid is small and stepped growth reads as snapping
        }

        // ---------- weight and strength ----------

        /// <summary>Set by <see cref="RollAtFeet"/>; consumed in LateUpdate, after every Update has had its say.</summary>
        bool _pushedThisFrame;

        /// <summary>
        /// Top speed this ball can be rolled at by everyone currently pushing it. Derived from the same three
        /// numbers on every machine (the ball's mass, the config's power, the head count), so two players
        /// shoving the same boulder agree about how fast it is going without anyone being authoritative.
        /// </summary>
        float PushSpeedFor(float massKg, SnowSculpture s)
        {
            if (config == null) return float.PositiveInfinity;
            return SnowPush.SpeedFor(massKg, SnowPush.Arms(s), config.pushPower, config.pushMinSpeed);
        }

        /// <summary>
        /// Who is pushing what, settled once a frame. A ball being rolled was claimed in <see cref="RollAtFeet"/>
        /// during someone's Update; with empty hands the player may instead be leaning on a ball another player
        /// is rolling, which is the whole of co-operative pushing — their arms are added to its head count by
        /// the net layer, and both of them end up capped to the same, higher, speed.
        /// </summary>
        void LateUpdate()
        {
            if (_pushedThisFrame) { _pushedThisFrame = false; return; }
            if (character == null || config == null) { SnowPush.Clear(); return; }

            if (IsCarrying)
            {
                // A whole sculpture in your arms sets your pace out of the same budget as shoving one: a snowman
                // you can just about lift is a waddle, and help makes it a walk. A ball small enough to palm is
                // not worth slowing anyone down for.
                if (Ball == null) SnowPush.Set(Carried, PushSpeedFor(Carried.Mass, Carried));
                else SnowPush.Clear();
                return;
            }

            // Hands free: your arms count toward whatever they are on. A ball another player is rolling is the
            // co-operative push; a resting sculpture you are standing against and looking at is the co-operative
            // LIFT, and it has to be claimable before anyone picks it up or two people could never start one
            // together. A claim on something resting costs no speed - you are only leaning on it.
            Snowball help = null;
            float nearest = float.MaxValue;
            var carried = Snowball.CarriedBalls;
            for (int i = 0; i < carried.Count; i++)
            {
                var ball = carried[i];
                if (ball == null || ball == Ball) continue;
                Vector3 to = ball.Centre - character.position; to.y = 0f;
                float flat = to.magnitude;
                float gap = flat - ball.radius - CapsuleRadius;
                if (gap > config.pushHelpRange || gap >= nearest) continue;
                // Facing it: standing beside someone else's boulder is not pushing it.
                if (flat > 0.001f && Vector3.Dot(character.forward, to / flat) < 0.4f) continue;
                nearest = gap;
                help = ball;
            }

            if (help != null) { SnowPush.Set(help.Sculpture, PushSpeedFor(help.Mass, help.Sculpture)); return; }

            var within = ClaimableAtReticle();
            if (within != null) SnowPush.Set(within, float.PositiveInfinity);
            else SnowPush.Clear();
        }

        /// <summary>
        /// The resting snow the player has their hands on: what the reticle is on, close enough to touch. Taken
        /// from the tool's aim rather than a physics query of its own - the sculpture under your cursor is the
        /// one you are about to try to lift, and asking the aim costs nothing.
        /// </summary>
        SnowSculpture ClaimableAtReticle()
        {
            if (_tool == null || !_tool.HasHit || _tool.Target == null) return null;
            if (_tool.Target == Carried) return null;
            Vector3 to = (Vector3)_tool.BrushPoint - character.position; to.y = 0f;
            return to.magnitude - CapsuleRadius <= config.pushHelpRange ? _tool.Target : null;
        }

        void OnDisable()
        {
            // A limit left set by a disabled roller would cap the player's speed with nothing pushing anything.
            if (SnowPush.Pushing != null) SnowPush.Clear();
            _pushedThisFrame = false;
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
            if (!TwoHandedCarry && palmAnchor != null) return palmAnchor.position;
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
            power = Mathf.Clamp01(power);
            direction = direction.normalized;
            var ball = Ball;
            Carried = null; Ball = null;
            ball.transform.position = origin + direction * (throwStartDistance + ball.radius);

            // Lift as an angle about the horizontal axis across the aim, so the arc keeps its shape at every power.
            Vector3 across = Vector3.Cross(Vector3.up, direction);
            Vector3 launchDir = across.sqrMagnitude > 1e-6f
                ? Quaternion.AngleAxis(-throwLiftDegrees, across.normalized) * direction
                : direction;

            // Heavier snow leaves the hand slower: the arm has a fixed amount to give, and a ball that flies the
            // same whatever its size is the loudest way of saying nothing about it is being simulated.
            float sizeScale = Mathf.Clamp(
                Mathf.Pow(throwReferenceRadius / Mathf.Max(0.01f, ball.radius), throwMassFalloff), 0.2f, 1.4f);
            float speed = Mathf.Lerp(throwSpeedMin, throwSpeedMax, power) * sizeScale;

            // Your own momentum goes with it. A grounded CharacterController reports a constant downward stick
            // velocity, so only the planar part and genuine upward motion are worth inheriting.
            var cc = Capsule;
            Vector3 carry = cc != null ? cc.velocity : Vector3.zero;
            carry.y = Mathf.Max(0f, carry.y);

            Vector3 spinAxis = Vector3.Cross(launchDir, Vector3.up); // backspin: the top of the ball turns back toward you
            Vector3 spin = spinAxis.sqrMagnitude > 1e-6f
                ? spinAxis.normalized * (throwSpin * 2f * Mathf.PI * Mathf.Lerp(0.35f, 1f, power))
                : Vector3.zero;

            Vector3 velocity = launchDir * speed + carry * throwInheritVelocity;
            ball.Launch(velocity, spin, ThrowerColliders());
            SculptureNet.RaiseThrown(ball, velocity, spin);
        }

        /// <summary>
        /// Everything on the thrower a launched ball must pass through. The ball is born a few centimetres outside
        /// the capsule and you can outrun a soft throw, so without this a throw regularly clips its own thrower —
        /// which the flight code reads as a landing and stops dead in your face for no visible reason.
        /// </summary>
        Collider[] ThrowerColliders() =>
            character != null ? character.GetComponentsInChildren<Collider>(true) : System.Array.Empty<Collider>();

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
