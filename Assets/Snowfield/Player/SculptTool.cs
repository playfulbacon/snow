using System.Collections.Generic;
using Snowfield.Config;
using Snowfield.Sculpture;
using Snowfield.Voxel;
using Unity.Mathematics;
using UnityEngine;
using UnityEngine.InputSystem;

namespace Snowfield.Player
{
    /// <summary>
    /// The player's hands. One persistent state (Hand) plus a Tab accessory overlay. Left hand moves mass, right
    /// hand refines it — coarse in, fine out:
    ///   LMB — scoop: on a sculpture, bite out a chunk; on bare ground, a handful. Either way that snow is now in
    ///         your hands (mass continuity). While carrying: a tap lets go where the snow already is (fusing into
    ///         snow, falling otherwise), a hold charges a throw.
    ///   Shift+LMB — pat: blur + weld + a little compaction (works with a ball in hand too)
    ///   RMB drag — shave: surface-relative removal, crisp on packed snow, ragged and crumbly on powder.
    ///   Shift+RMB drag — score: a narrow groove along the stroke.
    ///   RMB hold, ball in hand — squeeze it packed (crunch, crunch, crunch); a packed ball comes up to a work
    ///         pose instead and RMB shaves/scores it in the palms.
    ///   Shaved-off snow sheds as loose lumps at your feet; anything you cut too thin (for its packing) or cut
    ///   free of the ground breaks off — see <see cref="SculptureStructure"/>.
    ///   Scroll — brush radius (accessory selection while the overlay is open) · Tab — accessory overlay
    /// Remesh on a timer while stroking; colliders rebuild on release. The HUD is a separate object reading this.
    /// </summary>
    public class SculptTool : MonoBehaviour
    {
        public enum BrushOp { None, Add, Carve, Smooth, Shave, Score }

        public SculptFeelConfig config;
        public Camera viewCamera;
        public LayerMask sculptMask = ~0;
        [Tooltip("How far from the character the brush can reach (the ray itself starts at the camera).")]
        public float maxReach = 4f;
        [Tooltip("Reach is measured from here. Defaults to the player root this tool sits under, else the camera.")]
        public Transform reachOrigin;
        [Tooltip("Visual brush cursor; scaled to brush diameter.")]
        public Transform cursor;
        [Tooltip("Stretchy arm IK. The hands follow whatever snow is in play; null just means no arms.")]
        public HandRig hands;
        [Tooltip("Holding LMB longer than this starts a throw charge; a shorter tap places/drops the carried object.")]
        public float throwTapThreshold = 0.2f;
        [Tooltip("A carried ball rolls on the ground only while the cursor points within this distance of you; otherwise it is held overhead.")]
        public float rollEngageDistance = 2.2f;
        [Tooltip("While shaving, colliders re-cook this often (s) so a long drag keeps finding the receding surface.")]
        public float shaveColliderRefresh = 0.5f;
        [Header("Brush cursor colours")]
        [Tooltip("Cursor while smoothing (Shift held).")]
        public Color cursorAddColor = new Color(0.4f, 0.7f, 1f, 0.25f);
        [Tooltip("Default cursor: this sphere is the chunk LMB will scoop out.")]
        public Color cursorCarveColor = new Color(1f, 0.35f, 0.3f, 0.3f);
        [Tooltip("Cursor while shaving/scoring (RMB).")]
        public Color cursorShaveColor = new Color(1f, 0.85f, 0.3f, 0.3f);

        public ToolMode Mode { get; private set; } = ToolMode.Hand;
        public BrushOp CurrentOp { get; private set; } = BrushOp.None;
        public bool IsSculpting { get; private set; }

        // ---- aim results (refreshed every frame) ----
        public SnowSculpture Target { get; set; }
        public SculptureProp AimedProp { get; private set; }
        /// <summary>A loose, resting snowball under the reticle (it is also <see cref="Target"/>).</summary>
        public Snowball AimedSnowball { get; private set; }
        /// <summary>Hit point/normal of whatever the centre ray struck (sculpture, ball, ground...).</summary>
        public float3 BrushPoint { get; private set; }
        public float3 BrushNormal { get; private set; }
        /// <summary>Aiming at sculpture snow (not a prop).</summary>
        public bool HasHit { get; private set; }
        /// <summary>Aiming at the ground within reach.</summary>
        public bool HasGroundHit { get; private set; }
        public Vector3 GroundPoint { get; private set; }
        /// <summary>Diagnostic: collider the centre ray hit this frame.</summary>
        public string AimedColliderPath { get; private set; } = "";
        /// <summary>Packing (0 powder … 1 packed) of the snow under the cursor this frame; 0 with no hit. HUD/tuning readout.</summary>
        public float AimedPack { get; private set; }

        public SnowballRoller Roller { get; private set; }
        /// <summary>0..1 while charging a throw (LMB held with a carried ball); 0 otherwise. Drives the HUD ring.</summary>
        public float ThrowCharge { get; private set; }
        /// <summary>0..1 while squeezing a held ball (RMB held); 0 otherwise. Drives the hands' clench and the HUD ring.</summary>
        public float SqueezeProgress { get; private set; }
        /// <summary>What LMB would do right now (HUD prompt). Recomputed every frame.</summary>
        public CursorAction PrimaryAction { get; private set; }
        /// <summary>What RMB would do right now (HUD prompt). Recomputed every frame.</summary>
        public CursorAction SecondaryAction { get; private set; }
        /// <summary>Reserved for a third key; unused in the current scheme.</summary>
        public CursorAction TertiaryAction { get; private set; }

        /// <summary>Radius multiplier on top of config, driven by scroll. Session-only.</summary>
        public float radiusScale = 1f;

        AccessoryPlacer _placer;
        bool _lockedLastFrame;
        float _lmbDownTime = -1f;
        float _tickAccumulator;
        float _remeshAccumulator;
        readonly HashSet<IBrushTarget> _dirtyTargets = new HashSet<IBrushTarget>();
        readonly List<IBrushTarget> _strokeTargets = new List<IBrushTarget>();
        readonly Collider[] _overlapScratch = new Collider[32];

        // ---- shave stroke state ----
        Vector3 _lastStampPoint;
        Vector3 _lastStampNormal;
        bool _hasLastStamp;
        float _colliderRefresh;
        readonly List<SculptureNet.ShaveStamp> _stamps = new List<SculptureNet.ShaveStamp>();
        readonly Dictionary<SnowSculpture, (int3 min, int3 max)> _strokeRegions = new Dictionary<SnowSculpture, (int3, int3)>();
        /// <summary>Snow shaved off but not yet shed at the feet (m³), and its packing (mass-weighted).</summary>
        float _crumbs, _crumbPackMass;

        // ---- RMB with a ball in hand ----
        enum RmbMode { None, Squeeze, Work }
        RmbMode _rmbMode;
        float _squeezeHeld;
        int _squeezeStage;
        float _squeezePack0;

        void Awake()
        {
            if (viewCamera == null) viewCamera = Camera.main;
            if (reachOrigin == null)
                reachOrigin = transform.root != transform ? transform.root
                            : viewCamera != null ? viewCamera.transform : transform;
            _placer = GetComponent<AccessoryPlacer>();
            if (_placer == null) _placer = gameObject.AddComponent<AccessoryPlacer>();
            Roller = GetComponent<SnowballRoller>();
            if (Roller == null) Roller = gameObject.AddComponent<SnowballRoller>();
            if (Roller.config == null) Roller.config = config;
            // Same self-healing as the placer/roller above: a scene that predates the arms still gets them.
            if (hands == null) hands = GetComponentInParent<HandRig>();
            if (hands == null && reachOrigin != null) hands = reachOrigin.gameObject.AddComponent<HandRig>();
            if (hands != null && hands.config == null) hands.config = config;
        }

        void Update()
        {
            if (config == null || viewCamera == null) return;
            var mouse = Mouse.current;
            var kb = Keyboard.current;

            // Sculpting only in mouse-look mode: while the cursor is free, LMB is the relock gesture
            // (PlayerController.HandleCursor) and must not scoop. The relock happens synchronously in the
            // player's Update and script order between the two is unspecified, so also swallow the FIRST
            // locked frame — that is the frame carrying the relock click.
            bool locked = Cursor.lockState == CursorLockMode.Locked;
            bool justLocked = locked && !_lockedLastFrame;
            _lockedLastFrame = locked;
            if (!locked || justLocked)
            {
                if (IsSculpting) { IsSculpting = false; Flush(); }
                HideBrushCursor();
                if (_placer != null) _placer.HidePreview();
                PrimaryAction = SecondaryAction = TertiaryAction = CursorAction.None;
                ThrowCharge = 0f;
                _lmbDownTime = -1f;
                EndRmb();
                return;
            }

            if (kb != null && kb.tabKey.wasPressedThisFrame)
            {
                Mode = Mode == ToolMode.Accessory ? ToolMode.Hand : ToolMode.Accessory;
                _placer.HidePreview();
                HideBrushCursor();
                EndRmb();
            }
            HandleScroll(mouse);
            Aim();

            if (Mode == ToolMode.Accessory) UpdateAccessory(mouse, kb);
            else UpdateHand(mouse, kb);

            ComputeActions(kb);
            DriveHands();

            // timed remesh while a stroke is in progress
            if (_dirtyTargets.Count > 0)
            {
                _remeshAccumulator += Time.deltaTime;
                if (_remeshAccumulator >= 1f / config.remeshHz)
                {
                    _remeshAccumulator = 0f;
                    foreach (var t in _dirtyTargets)
                        if (!(t is Component c && c == null)) t.Remesh();
                }
                if (IsShaveOp(CurrentOp))
                {
                    // A shave plane follows the collider surface; without a mid-stroke cook a long drag would
                    // keep landing on the pre-cut surface and stop biting. On release Flush cooks anyway.
                    _colliderRefresh += Time.deltaTime;
                    if (_colliderRefresh >= shaveColliderRefresh)
                    {
                        _colliderRefresh = 0f;
                        foreach (var t in _dirtyTargets)
                            if (!(t is Component c && c == null)) t.RebuildColliders();
                    }
                }
            }
        }

        static bool IsShaveOp(BrushOp op) => op == BrushOp.Shave || op == BrushOp.Score;

        // ---------- prompts ----------

        void ComputeActions(Keyboard kb)
        {
            CursorAction p = CursorAction.None, s = CursorAction.None;
            bool onSnow = HasHit && Target != null;
            bool shift = kb != null && kb.leftShiftKey.isPressed;

            if (Mode == ToolMode.Accessory)
            {
                p = AimedProp != null ? CursorAction.RetrieveAccessory
                  : onSnow ? CursorAction.PlaceAccessory : CursorAction.None;
            }
            else if (Roller.IsCarrying)
            {
                bool onOther = onSnow && Target != Roller.Carried;
                if (Roller.InWorkPose) p = CursorAction.None;
                else if (shift && onOther && Roller.IsCarryingBall) p = CursorAction.Smooth;
                else p = ThrowCharge > 0f ? CursorAction.Throw
                       : onOther ? CursorAction.AttachSnowball
                       : CursorAction.Drop;
                if (Roller.IsCarryingBall)
                {
                    if (_rmbMode == RmbMode.Work) s = shift ? CursorAction.Score : CursorAction.Shave;
                    else if (HeldBallPacked()) s = CursorAction.CarveInHand;
                    else if (_rmbMode == RmbMode.Squeeze || Roller.Ball.Sculpture.MeanCompaction() < 250f) s = CursorAction.Squeeze;
                }
            }
            else if (onSnow)
            {
                p = shift ? CursorAction.Smooth : CursorAction.ScoopSnow;
                s = shift ? CursorAction.Score : CursorAction.Shave;
            }
            else if (HasGroundHit && Roller.CanReachGround(GroundPoint) && !shift)
            {
                p = CursorAction.ScoopSnow;
            }
            PrimaryAction = p;
            SecondaryAction = s;
            TertiaryAction = CursorAction.None;
        }

        bool HeldBallPacked() => Roller.IsCarryingBall && Roller.Ball.Sculpture.MeanCompaction() >= config.packedThreshold;

        // ---------- hands ----------

        /// <summary>Reach and hold poses are measured from here (the player root).</summary>
        Transform Body => reachOrigin != null ? reachOrigin : transform;

        /// <summary>
        /// Aim the arms at whatever snow is in play. The rule is just "the hands are where the snow is": a carried
        /// ball is already positioned by the roller (anchor, feet, attach preview), so the hands go to it rather
        /// than the other way round — which makes a scoop read for free, since the chunk is born under the cursor
        /// and flies back to the carry anchor with the hand chasing it. Arms stretch to cover the difference.
        /// </summary>
        void DriveHands()
        {
            if (hands == null || !hands.IsReady) return;
            // One hand stays free while the other holds; with empty hands the right one does everything.
            HandRig.Side free = Roller.IsCarrying ? HandRig.Side.Left : HandRig.Side.Right;

            Vector3 goal = default, aim = default;
            float weight = 0f;
            if (CurrentOp == BrushOp.Smooth && Target != null)
            {
                // Patting: palm on the surface, bobbing off it, fingers lying along it rather than driven into it.
                Vector3 n = (Vector3)BrushNormal;
                goal = (Vector3)BrushPoint + n * (Mathf.Abs(Mathf.Sin(Time.time * Mathf.PI * config.handPatRate))
                                                  * config.handPatAmplitude);
                aim = Vector3.ProjectOnPlane(goal - Body.position, n);
                weight = 1f;
            }
            else if (IsShaveOp(CurrentOp) && Target != null)
            {
                // Shaving: the edge of the hand rides the surface, dragged along the stroke.
                Vector3 n = (Vector3)BrushNormal;
                goal = (Vector3)BrushPoint + n * 0.01f;
                Vector3 along = _hasLastStamp ? (Vector3)BrushPoint - _lastStampPoint : goal - Body.position;
                aim = Vector3.ProjectOnPlane(along.sqrMagnitude > 1e-6f ? along : goal - Body.position, n);
                weight = 1f;
            }
            else if (Mode == ToolMode.Accessory && AimedProp != null)
            {
                goal = AimedProp.transform.position;      // about to pull this one back off
                aim = goal - Body.position;
                weight = 1f;
            }
            else if (Mode == ToolMode.Accessory && HasHit)
            {
                goal = (Vector3)BrushPoint;
                aim = -(Vector3)BrushNormal;              // about to press one in
                weight = 1f;
            }
            else if (Mode == ToolMode.Hand && !Roller.IsCarrying && HasHit && Target != null)
            {
                // Ready pose: the red cursor is the bite LMB would take, so the hand drifts over it. Ground
                // scooping gets none — you look at the snow by your feet constantly and the arm would never settle.
                goal = (Vector3)BrushPoint;
                aim = goal - Body.position;
                weight = config.handHoverWeight;
            }

            if (Roller.IsCarrying)
            {
                Vector3 centre = Roller.HoldCentre;
                float radius = Roller.HoldRadius;
                // The second hand joins only once the ball is worth it — and not if it is busy, or cocked back.
                // A squeeze always takes both: that is what a squeeze is.
                bool squeezing = SqueezeProgress > 0f;
                bool bothHands = squeezing || (Roller.TwoHandedCarry && weight <= 0f && ThrowCharge <= 0f);
                Vector3 right = HoldPoint(centre, radius, HandRig.Side.Right, bothHands);
                hands.Reach(HandRig.Side.Right, right, 1f, centre - right);
                if (bothHands)
                {
                    Vector3 left = HoldPoint(centre, radius, HandRig.Side.Left, bothHands);
                    hands.Reach(HandRig.Side.Left, left, 1f, centre - left);
                }
            }

            if (weight > 0f) hands.Reach(free, goal, weight, aim);
        }

        /// <summary>
        /// Where a hand grips held snow, sunk a little into the surface. A two-handed ball is gripped from the
        /// sides; a handful sits in the one palm — under it, or on top of one down at your feet. Either way the
        /// hand goes over a ball the shoulder looks down on and under one held above. A squeeze clenches both
        /// hands into the ball as it shrinks.
        /// </summary>
        Vector3 HoldPoint(Vector3 centre, float radius, HandRig.Side side, bool bothHands)
        {
            Vector3 shoulder = hands.ShoulderPosition(side);
            Vector3 toShoulder = shoulder - centre;
            Vector3 lateral = Body.right * (side == HandRig.Side.Left ? -1f : 1f);
            Vector3 toBody = Vector3.ProjectOnPlane(toShoulder, Vector3.up).normalized;
            // Over the top only for snow properly down at your feet — merely below the shoulder line is still
            // something you cup from underneath, and on this rig the shoulders are most of the way up the body.
            bool overTheTop = toShoulder.y > (shoulder.y - Body.position.y) * 0.35f;
            Vector3 vertical = (overTheTop ? Vector3.up : Vector3.down)
                             * (bothHands ? (overTheTop ? 0.45f : 0.15f) : 0.9f);
            Vector3 dir = lateral * (bothHands ? 0.85f : 0.2f) + toBody * (bothHands ? 0.3f : 0.25f) + vertical;
            float grip = 0.95f - 0.3f * SqueezeProgress;
            return centre + dir.normalized * (radius * grip);
        }

        /// <summary>Leave both hands on the snow they just let go of, so a fuse or a drop still reads as a press.</summary>
        void HoldPulse()
        {
            if (hands == null || !hands.IsReady || !Roller.IsCarrying) return;
            Vector3 centre = Roller.HoldCentre;
            float radius = Roller.HoldRadius;
            bool bothHands = Roller.TwoHandedCarry;
            for (int i = 0; i < 2; i++)
            {
                var side = (HandRig.Side)i;
                if (side == HandRig.Side.Left && !bothHands) continue; // it was never on the ball
                Vector3 p = HoldPoint(centre, radius, side, bothHands);
                hands.Pulse(side, p, config.handFollowThrough, centre - p);
            }
        }

        /// <summary>Leave the working hand on a spot it just touched for a beat.</summary>
        void PulseFree(Vector3 point, Vector3 aim)
        {
            if (hands == null || !hands.IsReady) return;
            hands.Pulse(Roller.IsCarrying ? HandRig.Side.Left : HandRig.Side.Right,
                point, config.handFollowThrough, aim);
        }

        // ---------- input ----------

        void HandleScroll(Mouse mouse)
        {
            if (mouse == null) return;
            float scroll = mouse.scroll.ReadValue().y;
            if (math.abs(scroll) <= 0.01f) return;
            int dir = scroll > 0 ? 1 : -1;
            if (Mode == ToolMode.Accessory)
                _placer.Step(-dir); // scroll down = next, matches reading order
            else
                radiusScale = math.clamp(radiusScale * (dir > 0 ? 1.15f : 1f / 1.15f), 0.25f, 4f);
        }

        // ---------- aim ----------

        void Aim()
        {
            HasHit = false;
            HasGroundHit = false;
            Target = null;
            AimedProp = null;
            AimedSnowball = null;
            AimedColliderPath = "";
            AimedPack = 0f;
            var ray = viewCamera.ViewportPointToRay(new Vector3(0.5f, 0.5f, 0f));
            Vector3 origin = reachOrigin != null ? reachOrigin.position + Vector3.up : ray.origin;
            float rayLength = maxReach + Vector3.Distance(ray.origin, origin);

            if (!Physics.Raycast(ray, out var hit, rayLength, sculptMask, QueryTriggerInteraction.Collide)) return;
            if (Vector3.Distance(hit.point, origin) > maxReach) return;

            BrushPoint = hit.point;
            BrushNormal = hit.normal;
            AimedColliderPath = hit.collider.transform.parent != null
                ? hit.collider.transform.parent.name + "/" + hit.collider.name : hit.collider.name;

            AimedProp = hit.collider.GetComponentInParent<SculptureProp>();
            var s = hit.collider.GetComponentInParent<SnowSculpture>();
            if (s != null)
            {
                var flying = s.GetComponent<Snowball>();
                if (flying != null && flying.IsFlying) { AimedColliderPath = ""; return; } // airborne: not a target for anything
                Target = s;
                HasHit = AimedProp == null; // aiming at a prop is not a snow hit
                var ball = s.GetComponent<Snowball>();
                if (ball != null && ball.IsLoose && !ball.IsFlying) AimedSnowball = ball;
                if (HasHit) AimedPack = SculptFeelConfig.Pack(s.CompactionUnderSurface(BrushPoint, BrushNormal));
                return;
            }
            if (hit.normal.y > 0.6f)
            {
                HasGroundHit = true;
                GroundPoint = hit.point;
            }
        }

        // ---------- hand: brush, scoop, carry, roll, throw, squeeze, shave ----------

        void UpdateHand(Mouse mouse, Keyboard kb)
        {
            bool lmb = mouse != null && mouse.leftButton.isPressed;
            bool lmbDown = mouse != null && mouse.leftButton.wasPressedThisFrame;
            bool lmbUp = mouse != null && mouse.leftButton.wasReleasedThisFrame;
            bool rmb = mouse != null && mouse.rightButton.isPressed;
            bool rmbDown = mouse != null && mouse.rightButton.wasPressedThisFrame;
            bool shift = kb != null && kb.leftShiftKey.isPressed;
            _placer.HidePreview();

            // --- carrying: LMB tap lets go where the snow is, hold charges a throw; Shift+LMB still smooths ---
            if (Roller.IsCarrying)
            {
                // RMB with a ball in hand: squeeze a soft ball, or bring a packed one up and carve it. The mode
                // latches on the press so a squeeze that finishes mid-hold does not slide straight into carving.
                if (Roller.IsCarryingBall && rmb && !lmb)
                {
                    if (rmbDown || _rmbMode == RmbMode.None) BeginRmb();
                }
                else if (_rmbMode != RmbMode.None) EndRmb();

                if (_rmbMode == RmbMode.Squeeze) { UpdateSqueeze(); HideBrushCursor(); return; }
                if (_rmbMode == RmbMode.Work)
                {
                    Roller.UpdateCarrying(null);
                    UpdateBrush(mouse, shift);
                    return;
                }

                bool onSnow = HasHit && Target != null && Target != Roller.Carried;
                // Shift doubles as the run key now, so it only means "keep the snow in hand and smooth" when
                // it can actually smooth (a ball in hand, aiming at snow) — otherwise a sprinting LMB tap
                // would be dead and the HUD's Drop/Throw prompt a lie.
                bool sculptingHold = shift && Roller.IsCarryingBall && onSnow;
                bool letGo = !sculptingHold;
                if (letGo && lmbDown) _lmbDownTime = Time.time;
                bool charging = letGo && lmb && _lmbDownTime >= 0f && Roller.IsCarryingBall
                                && Time.time - _lmbDownTime > throwTapThreshold;
                ThrowCharge = charging
                    ? Mathf.Min(1f, (Time.time - _lmbDownTime - throwTapThreshold) / Mathf.Max(0.05f, Roller.chargeTime))
                    : 0f;

                bool sculpting = sculptingHold && lmb; // brushing keeps the snow in hand
                bool cursorNear = false;
                if (HasGroundHit && reachOrigin != null)
                {
                    Vector3 d = GroundPoint - reachOrigin.position; d.y = 0f;
                    cursorNear = d.magnitude <= rollEngageDistance;
                }
                Vector3? preview = (charging || sculpting) ? null
                                 : onSnow ? Roller.AttachCentre(BrushPoint, BrushNormal) : (Vector3?)null;
                Roller.UpdateCarrying(preview, liftToHand: charging || sculpting,
                                      rollOnGround: cursorNear && !charging && !sculpting && !onSnow);

                if (letGo && lmbUp && _lmbDownTime >= 0f)
                {
                    float held = Time.time - _lmbDownTime;
                    _lmbDownTime = -1f;
                    ThrowCharge = 0f;
                    if (Roller.IsCarryingBall && held > throwTapThreshold)
                    {
                        var cam = viewCamera.transform;
                        Roller.Throw(cam.position, cam.forward,
                            Mathf.Min(1f, (held - throwTapThreshold) / Mathf.Max(0.05f, Roller.chargeTime)));
                        // The ball is gone next frame; the arm follows through past where it let go.
                        if (hands != null)
                            hands.Pulse(HandRig.Side.Right, cam.position + cam.forward * 1.1f,
                                config.handFollowThrough, cam.forward);
                    }
                    else if (onSnow) { HoldPulse(); Roller.PlaceWhereItIs(Target); } // exactly where it sits
                    else { HoldPulse(); Roller.DropFalling(); }                      // let go; gravity takes it
                    return;
                }

                // A ball in hand still leaves you free to smooth; a whole sculpture fills both hands.
                if (sculpting) { UpdateBrush(mouse, true); return; }
                HideBrushCursor();
                return;
            }

            ThrowCharge = 0f;
            _lmbDownTime = -1f;
            if (_rmbMode != RmbMode.None) EndRmb();

            // --- free hands, LMB: scoop a chunk out of the aimed sculpture, or a handful off the ground ---
            if (lmbDown && !shift && !rmb)
            {
                if (HasHit && Target != null) { ScoopChunk(CurrentRadius()); return; }
                if (HasGroundHit && Roller.CanReachGround(GroundPoint))
                {
                    Roller.ScoopFrom(GroundPoint);
                    HideBrushCursor();
                    return;
                }
            }

            UpdateBrush(mouse, shift);
        }

        // ---------- RMB with a ball in hand: squeeze / work pose ----------

        void BeginRmb()
        {
            if (!Roller.IsCarryingBall) return;
            if (HeldBallPacked())
            {
                _rmbMode = RmbMode.Work;
                Roller.SetWorkPose(true);
            }
            else
            {
                _rmbMode = RmbMode.Squeeze;
                _squeezeHeld = 0f;
                _squeezeStage = 0;
                _squeezePack0 = SculptFeelConfig.Pack(Roller.Ball.Sculpture.MeanCompaction());
            }
        }

        void EndRmb()
        {
            if (_rmbMode == RmbMode.Work)
            {
                if (IsSculpting) { IsSculpting = false; Flush(); }
                Roller.SetWorkPose(false);
                HideBrushCursor();
            }
            _rmbMode = RmbMode.None;
            _squeezeHeld = 0f;
            _squeezeStage = 0;
            SqueezeProgress = 0f;
        }

        /// <summary>
        /// Hold to pack: over squeezeTime the ball crunches through the configured stages, each one shrinking it
        /// and blending its compaction toward fully packed, so the last stage lands exactly on 255. Lossy on
        /// purpose — the volume it loses is the price of workability.
        /// </summary>
        void UpdateSqueeze()
        {
            if (!Roller.IsCarryingBall) { EndRmb(); return; }
            Roller.UpdateCarrying(null, liftToHand: true);
            int stages = Mathf.Max(1, config.squeezeStages);
            _squeezeHeld += Time.deltaTime;
            SqueezeProgress = Mathf.Clamp01(_squeezeHeld / Mathf.Max(0.05f, config.squeezeTime));
            int due = Mathf.Min(stages, Mathf.FloorToInt(SqueezeProgress * stages + 1e-4f));
            while (_squeezeStage < due)
            {
                _squeezeStage++;
                SqueezeStage(_squeezeStage, stages);
            }
        }

        void SqueezeStage(int stage, int stages)
        {
            var ball = Roller.Ball;
            if (ball == null) return;
            // Total shrink is what this ball still has to give: a half-packed ball loses half as much again.
            float total = Mathf.Pow(1f - config.squeezeShrink * (1f - _squeezePack0), 1f / 3f);
            float linear = Mathf.Pow(total, 1f / stages);
            float blend = 1f / (stages - stage + 1);
            ball.Sculpture.Squeeze(linear, blend);
            ball.radius *= linear;
            ball.Sculpture.Remesh();
            SculptureNet.RaiseSqueezed(ball, linear, blend);
            SnowAudio.Play(SnowAudio.Kind.Crunch, ball.Centre, stage / (float)stages);
        }

        /// <summary>
        /// One discrete bite. The chunk is built from the snow the brush sphere actually overlaps - so a bite at the
        /// edge of a sculpture hands you a half-sphere - and the same kernel is then removed from every grid under it.
        /// Small bites in packed snow use the crisp shoulder and leave edges, not scoops.
        /// </summary>
        void ScoopChunk(float radius)
        {
            var factory = SculptureFactory.Instance;
            if (factory == null) return;
            GatherStrokeTargets(Target, radius);
            float shoulder = config.BiteShoulder(AimedPack, radius);

            var chunk = factory.CreateEmptySnowball(BrushPoint, radius);
            foreach (var t in _strokeTargets)
                if (t is SnowSculpture s && s != null && s != chunk.Sculpture)
                    chunk.Sculpture.ExtractFrom(s, BrushPoint, radius, shoulder);

            float volume = chunk.Sculpture.DensityVolume();
            if (volume <= 1e-5f)
            {
                Destroy(chunk.gameObject);           // nothing but air under the cursor...
                PulseFree(BrushPoint, (Vector3)BrushPoint - Body.position); // ...but the grab still happened
                return;
            }

            foreach (var t in _strokeTargets)
            {
                if (t is Component c && c == null) continue;
                t.ApplyAdd(BrushPoint, radius, -255f, shoulder); // full-strength one-shot removal
                t.Remesh();
                t.RebuildColliders();
            }
            Roller.TakeChunk(chunk, volume);
            CollectNetTargets(chunk.Sculpture);
            SculptureNet.RaiseScooped(new SculptureNet.ScoopInfo
            { point = BrushPoint, radius = radius, shoulder = shoulder, targets = _netTargets, chunk = chunk, resultRadius = chunk.radius });
            SnowAudio.Play(SnowAudio.Kind.Pat, BrushPoint, AimedPack, 0.8f);

            // A bite is a removal: what it left too thin, or cut free, comes off now.
            foreach (var s in _netTargets)
            {
                if (s == null || s.Grid == null) continue;
                float3 c = s.WorldToVoxel(BrushPoint);
                if (!s.Grid.SphereAabb(c, s.MetresToVoxels(radius), out var min, out var max)) continue;
                CheckStructure(s, min, max);
            }
        }

        readonly List<SnowSculpture> _netTargets = new List<SnowSculpture>();

        /// <summary>The stroke targets as sculptures, for the network seam. <paramref name="exclude"/> may be null.</summary>
        void CollectNetTargets(SnowSculpture exclude)
        {
            _netTargets.Clear();
            foreach (var t in _strokeTargets)
                if (t is SnowSculpture s && s != null && s != exclude)
                    _netTargets.Add(s);
        }

        /// <summary>The stroke loop: Shift+LMB pats, RMB shaves, Shift+RMB scores. Multi-target.</summary>
        void UpdateBrush(Mouse mouse, bool shift)
        {
            bool lmb = mouse != null && mouse.leftButton.isPressed;
            bool rmb = mouse != null && mouse.rightButton.isPressed;

            BrushOp op = BrushOp.None;
            if (lmb && shift && !rmb) op = BrushOp.Smooth;
            else if (rmb && !lmb && (!Roller.IsCarrying || _rmbMode == RmbMode.Work)) op = shift ? BrushOp.Score : BrushOp.Shave;
            if (op != CurrentOp && IsSculpting) { IsSculpting = false; Flush(); } // switching verbs mid-hold ends the stroke
            CurrentOp = op;

            IBrushTarget target = Target;
            // In the work pose the only thing you can carve is the ball in your hands.
            if (_rmbMode == RmbMode.Work && Target != Roller.Carried) target = null;

            float radius = CurrentRadius();
            if (cursor != null)
            {
                // Aiming at a prop still shows the cursor so the brush feels continuous over accessories.
                bool show = target != null;
                cursor.gameObject.SetActive(show);
                if (show)
                {
                    cursor.position = BrushPoint;
                    cursor.localScale = Vector3.one * (rmb && !lmb && shift ? radius * config.scoreRadiusFraction : radius) * 2f;
                    SetCursorColor(rmb && !lmb ? cursorShaveColor : shift ? cursorAddColor : cursorCarveColor);
                }
            }

            bool pressing = op != BrushOp.None;
            if (pressing && target != null)
            {
                if (!IsSculpting)
                {
                    IsSculpting = true;
                    _tickAccumulator = 1f / config.ticksPerSecond; // first tick immediate
                    _hasLastStamp = false;
                    _colliderRefresh = 0f;
                    _strokeRegions.Clear();
                }
                if (IsShaveOp(op)) { ShaveTick(op, target, radius, shift); return; }

                // Adding at the wall of a fixed sculpture: grow the grid first so the stroke continues seamlessly.
                var targetBall = Target != null ? Target.GetComponent<Snowball>() : null;
                if (op == BrushOp.Add && Target != null && (targetBall == null || !targetBall.IsLoose)
                    && SculptureFactory.Instance != null
                    && !Target.ContainsWorldSphere(BrushPoint, radius, config.regrowMarginVoxels))
                {
                    var grown = SculptureFactory.Instance.Regrow(Target,
                        new Bounds(BrushPoint, Vector3.one * (radius * 2f + config.regrowMarginVoxels * config.voxelSize * 2f)));
                    if (grown != Target) { Target = grown; target = grown; }
                }
                GatherStrokeTargets(target, radius);
                foreach (var t in _strokeTargets) _dirtyTargets.Add(t);
                _tickAccumulator += Time.deltaTime;
                float tickDt = 1f / config.ticksPerSecond;
                int ticks = 0;
                while (_tickAccumulator >= tickDt && ticks < 8) { _tickAccumulator -= tickDt; ticks++; }
                for (int i = 0; i < ticks; i++)
                    foreach (var t in _strokeTargets)
                        ApplyTick(t, op, BrushPoint, radius);
                if (ticks > 0)
                {
                    // Tick counts ride the event: the ≤8/frame clamp makes them a local-framerate artifact
                    // that peers must never re-derive from their own dt.
                    CollectNetTargets(null);
                    if (_netTargets.Count > 0)
                        SculptureNet.RaiseStroke(new SculptureNet.StrokeInfo
                        { op = (int)op, point = BrushPoint, radius = radius, ticks = ticks, targets = _netTargets });
                    if (op == BrushOp.Smooth) SnowAudio.Play(SnowAudio.Kind.Pat, BrushPoint, AimedPack, 0.5f);
                }
            }
            else if (IsSculpting && !pressing)
            {
                IsSculpting = false;
                Flush();
            }
        }

        /// <summary>
        /// Shave/score is stamped along the drag path, not per tick: a stamp lands every shaveStampSpacing radii
        /// of cursor travel over the surface, so a fast drag does not gap and a still cursor takes one layer.
        /// Params derive from the packing under the cursor (crisp and thin on packed snow, deep, ragged and
        /// slumping on powder) and are sent verbatim so peers replay the identical cut.
        /// </summary>
        void ShaveTick(BrushOp op, IBrushTarget aimed, float radius, bool score)
        {
            float stampRadius = score
                ? Mathf.Max(radius * config.scoreRadiusFraction, config.voxelSize * 2f)
                : radius;
            Vector3 point = BrushPoint, normal = BrushNormal;
            _stamps.Clear();
            if (!_hasLastStamp)
            {
                _stamps.Add(new SculptureNet.ShaveStamp { point = point, normal = normal });
            }
            else
            {
                float spacing = Mathf.Max(config.shaveStampSpacing * stampRadius, config.voxelSize * 0.5f);
                Vector3 travel = point - _lastStampPoint;
                int k = Mathf.Min(8, Mathf.FloorToInt(travel.magnitude / spacing));
                for (int i = 1; i <= k; i++)
                {
                    float t = i / (float)k;
                    _stamps.Add(new SculptureNet.ShaveStamp
                    {
                        point = Vector3.Lerp(_lastStampPoint, point, t),
                        normal = Vector3.Slerp(_lastStampNormal, normal, t).normalized,
                    });
                }
                if (k == 0) return; // not far enough yet
            }
            _lastStampPoint = point; _lastStampNormal = normal; _hasLastStamp = true;

            float pack = AimedPack;
            float slump = config.Slump(pack);
            for (int i = 0; i < _stamps.Count; i++)
            {
                var st = _stamps[i];
                st.prm = SculptureShave.ParamsFor(config, Target != null ? Target.Info.voxelSize : config.voxelSize, pack, score, st.point);
                _stamps[i] = st;
            }

            GatherStrokeTargets(aimed, stampRadius);
            float removed = 0f;
            foreach (var t in _strokeTargets)
            {
                if (!(t is SnowSculpture s) || s == null) continue;
                _dirtyTargets.Add(s);
                removed += SculptureShave.ApplyStamps(s, stampRadius, slump, _stamps, out int3 min, out int3 max);
                if (math.all(max > min)) GrowRegion(s, min, max);
            }
            CollectNetTargets(null);
            if (_netTargets.Count > 0)
                SculptureNet.RaiseShaved(new SculptureNet.ShaveInfo
                { radius = stampRadius, slump = slump, stamps = _stamps, targets = _netTargets });
            SnowAudio.Play(SnowAudio.Kind.Shave, point, pack, score ? 0.6f : 1f);

            if (removed > 0f) AddCrumbs(removed, pack);
        }

        void GrowRegion(SnowSculpture s, int3 min, int3 max)
        {
            if (_strokeRegions.TryGetValue(s, out var r))
                _strokeRegions[s] = (math.min(r.min, min), math.max(r.max, max));
            else
                _strokeRegions[s] = (min, max);
        }

        // ---------- crumbs: shaved-off snow sheds at your feet ----------

        void AddCrumbs(float volume, float pack)
        {
            _crumbs += volume;
            _crumbPackMass += volume * pack;
            float lump = config.ShedVolume(pack);
            while (_crumbs >= lump && lump > 0f) ShedLump(lump);
        }

        /// <summary>Drop one loose, fluffy lump of the accumulated shavings at the player's feet (conservation; the pile is the tell).</summary>
        void ShedLump(float volume)
        {
            var factory = SculptureFactory.Instance;
            if (factory == null || volume <= 0f) { _crumbs = 0f; _crumbPackMass = 0f; return; }
            volume = Mathf.Min(volume, _crumbs);
            _crumbPackMass = Mathf.Max(0f, _crumbPackMass - volume * (_crumbs > 0f ? _crumbPackMass / _crumbs : 0f));
            _crumbs -= volume;

            float r = SculptureStructure.SnowballRadius(volume);
            var body = Body;
            Vector3 lateral = body.right * Random.Range(-0.3f, 0.3f);
            Vector3 foot = body.position + body.forward * (0.35f + r) + lateral;
            var ground = SnowGround.Instance;
            float groundY = ground != null && ground.IsCreated ? ground.SampleHeight(foot) : body.position.y;
            Vector3 pos = new Vector3(foot.x, groundY + r + 0.25f, foot.z);
            Vector3 vel = body.forward * Random.Range(0.2f, 0.6f) + lateral * 0.5f;

            var lump = factory.CreateSnowball(pos, r, config.compactionScooped);
            lump.Launch(vel, Vector3.zero, Roller.OwnColliders());
            SculptureNet.RaiseShed(lump, vel);
            SnowAudio.Play(SnowAudio.Kind.Crumble, pos, 0f, 0.5f);
        }

        // ---------- structure ----------

        /// <summary>After a removal on a fixed sculpture: thin bits crumble into the shed pile, islands drop as balls.</summary>
        void CheckStructure(SnowSculpture s, int3 regionMin, int3 regionMax)
        {
            if (s == null || s.Grid == null) return;
            int versionBefore = s.Version;
            var result = SculptureStructure.Check(s, regionMin, regionMax);
            if (result.CrumbVolume > 0f)
            {
                AddCrumbs(result.CrumbVolume, 0f);
                SnowAudio.Play(SnowAudio.Kind.Crumble, s.VoxelToWorld((float3)(regionMin + regionMax) * 0.5f), 0f, 0.8f);
            }
            if (result.Detached.Count > 0)
                SnowAudio.Play(SnowAudio.Kind.Crumble, result.Detached[0].Centre, 0.5f, 1f);
            if (s.Version != versionBefore)
            {
                s.Remesh();
                s.RebuildColliders();
            }
        }

        public float CurrentRadius() => config.addRadius * radiusScale;

        public void ApplyTick(IBrushTarget t, BrushOp op, float3 point, float radius)
        {
            float addRate = config.addRatePerTick;
            switch (op)
            {
                case BrushOp.Add:
                    t.ApplyAdd(point, radius, addRate, config.addShoulder);
                    break;
                case BrushOp.Carve:
                    t.ApplyAdd(point, radius, -addRate, config.addShoulder);
                    break;
                case BrushOp.Smooth:
                    t.ApplySmooth(point, radius, config.smoothStrength, config.smoothShoulder);
                    break;
            }
        }

        /// <summary>Everything the brush sphere touches: the aimed target plus any other grids under the kernel.</summary>
        void GatherStrokeTargets(IBrushTarget aimed, float radius)
        {
            _strokeTargets.Clear();
            _strokeTargets.Add(aimed);
            int n = Physics.OverlapSphereNonAlloc(BrushPoint, radius, _overlapScratch, sculptMask, QueryTriggerInteraction.Ignore);
            for (int i = 0; i < n; i++)
            {
                var s = _overlapScratch[i].GetComponentInParent<SnowSculpture>();
                if (s == null) continue;
                var ball = s.GetComponent<Snowball>();
                if (ball != null && ball.IsFlying) continue;
                if (Roller != null && s == Roller.Carried && !Roller.InWorkPose) continue;
                if (!_strokeTargets.Contains(s)) _strokeTargets.Add(s);
            }
        }

        /// <summary>Finish a stroke: final remesh + collider cook for every touched grid, then the structural rules for a removal.</summary>
        public void Flush()
        {
            foreach (var t in _dirtyTargets)
            {
                if (t is Component c && c == null) continue; // destroyed mid-stroke (regrow/fuse)
                t.Remesh();
                t.RebuildColliders();
            }
            _dirtyTargets.Clear();
            _remeshAccumulator = 0f;
            _hasLastStamp = false;

            if (_strokeRegions.Count > 0)
            {
                foreach (var kv in _strokeRegions)
                    if (kv.Key != null) CheckStructure(kv.Key, kv.Value.min, kv.Value.max);
                _strokeRegions.Clear();
            }
            // Leftover shavings worth a lump go now rather than riding to the next stroke.
            float lump = config != null ? config.ShedVolume(_crumbs > 0f ? _crumbPackMass / _crumbs : 0f) : 0f;
            if (lump > 0f && _crumbs >= lump * 0.3f) ShedLump(_crumbs);
        }

        MaterialPropertyBlock _cursorBlock;
        void SetCursorColor(Color c)
        {
            var mr = cursor != null ? cursor.GetComponent<MeshRenderer>() : null;
            if (mr == null) return;
            _cursorBlock ??= new MaterialPropertyBlock();
            _cursorBlock.SetColor("_BaseColor", c);
            mr.SetPropertyBlock(_cursorBlock);
        }

        void HideBrushCursor()
        {
            CurrentOp = BrushOp.None;
            if (cursor != null) cursor.gameObject.SetActive(false);
        }

        // ---------- accessory overlay ----------

        void UpdateAccessory(Mouse mouse, Keyboard kb)
        {
            HideBrushCursor();
            ThrowCharge = 0f;

            // Hovering a placed accessory hides the ghost: this click takes that one back instead of adding another.
            _placer.UpdatePreview(HasHit && AimedProp == null, BrushPoint, BrushNormal);

            if (mouse == null || !mouse.leftButton.wasPressedThisFrame) return;
            if (AimedProp != null)
            {
                Vector3 at = AimedProp.transform.position; // read before it is unparented and destroyed
                _placer.Retrieve(AimedProp);
                PulseFree(at, at - Body.position);
            }
            else if (HasHit && Target != null)
            {
                _placer.Place(Target, BrushPoint, BrushNormal);
                PulseFree(BrushPoint, -(Vector3)BrushNormal);
            }
        }
    }
}
