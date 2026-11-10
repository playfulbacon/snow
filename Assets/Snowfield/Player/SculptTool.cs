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
    /// The player's hands. Tab opens an inventory grid; equipping an item keeps it in hand after the grid closes.
    ///   LMB — scoop: on a sculpture, bite out a chunk; on bare ground, a handful. Either way that snow is now in
    ///         your hands (mass continuity). While carrying: a tap lets go where the snow already is (fusing into
    ///         snow, falling otherwise), a hold charges a throw.
    ///   RMB — smooth/pat snow (works with a ball in hand too; the ball stays put)
    ///   Shift+LMB — planar shave on a sculpture; Shift+scroll turns the cut line
    ///   A carried ball rides the ground in front of you and rolls (grows) while the cursor points near you.
    ///   Scroll — brush radius with empty hands, otherwise switch held items · Tab — inventory grid
    /// Remesh on a timer while stroking; colliders rebuild on release.
    ///
    /// Every one of those is published as an <see cref="ActionPrompt"/> in <see cref="Actions"/> the frame it
    /// becomes available, and that list is the only thing the HUD reads. Adding an input here means adding a
    /// row there, which is what keeps a bound action from being one nothing on screen mentions.
    /// </summary>
    public class SculptTool : MonoBehaviour
    {
        public enum BrushOp { None, Add, Carve, Smooth }

        public SculptFeelConfig config;
        public Camera viewCamera;
        public LayerMask sculptMask = ~0;
        [Tooltip("Reach is measured from here. Defaults to the player root this tool sits under, else the camera.")]
        public Transform reachOrigin;
        [Tooltip("Visual brush cursor; scaled to brush diameter.")]
        public Transform cursor;
        [Tooltip("Screen overlay for the planar shave. Null just means no preview.")]
        public PlaneCutPreview planeCut;
        [Tooltip("Stretchy arm IK. The hands follow whatever snow is in play; null just means no arms.")]
        public HandRig hands;
        // maxReach, rollEngageDistance and throwTapThreshold live in SculptFeelConfig: they are feel numbers, and
        // on the component they could only be tuned in play mode and then lost on exit. Only the scene wiring
        // (camera, mask, reach origin, cursor, hands) belongs here.
        [Header("Brush cursor colours")]
        [Tooltip("Cursor while smoothing (Shift held).")]
        public Color cursorAddColor = new Color(0.4f, 0.7f, 1f, 0.25f);
        [Tooltip("Default cursor: this sphere is the chunk LMB will scoop out.")]
        public Color cursorCarveColor = new Color(1f, 0.35f, 0.3f, 0.3f);

        public ToolMode Mode { get; private set; } = ToolMode.Hand;
        public bool InventoryOpen { get; private set; }
        public BrushOp CurrentOp { get; private set; } = BrushOp.None;
        public bool IsSculpting { get; private set; }

        // ---- aim results (refreshed every frame) ----
        public SnowSculpture Target { get; set; }
        public SculptureProp AimedProp { get; private set; }
        public ItemPickup AimedPickup { get; private set; }
        /// <summary>A clickable point of the chain being placed, when the reticle is on one.</summary>
        public DrapeAnchor AimedAnchor { get; private set; }
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

        public SnowballRoller Roller { get; private set; }
        /// <summary>0..1 while charging a throw (LMB held with a carried ball); 0 otherwise. Drives the HUD ring.</summary>
        public float ThrowCharge { get; private set; }
        /// <summary>
        /// Everything the player can do this instant, each with the input that does it. Rebuilt every frame and
        /// read by the HUD. A list rather than the old fixed primary/secondary/tertiary slots: how many actions
        /// are on offer genuinely varies (aiming at bare ground is one, a sculpture is three), and the fixed
        /// slots meant an action either squeezed into a slot whose key icon was wrong or was never shown at all.
        /// </summary>
        public IReadOnlyList<ActionPrompt> Actions => _actions;

        /// <summary>The first offered action, or None. Kept for the playtest harness.</summary>
        public CursorAction PrimaryAction => _actions.Count > 0 ? _actions[0].Action : CursorAction.None;

        readonly List<ActionPrompt> _actions = new List<ActionPrompt>();

        /// <summary>Radius multiplier on top of config, driven by scroll. Session-only.</summary>
        public float radiusScale = 1f;
        [Tooltip("Degrees the planar-shave line turns per scroll notch.")]
        public float cutAngleStep = 7.5f;

        /// <summary>Planar shave: the on-screen angle of the cut line, degrees from screen-right.</summary>
        float _cutAngle;
        /// <summary>True while Shift is lining up a cut on a sculpture, so LMB chops instead of scooping.</summary>
        public bool AimingCut { get; private set; }

        AccessoryPlacer _placer;
        bool _lockedLastFrame;
        float _lmbDownTime = -1f;
        float _itemScroll;
        float _lastItemScrollTime = float.NegativeInfinity;
        float _nextItemScrollTime;
        float _tickAccumulator;
        float _remeshAccumulator;
        // Continuous scoop: the depth plane the drag started on, where the last bite was taken, and the rate cap.
        bool _scooping;
        Vector3 _scoopPlanePoint;
        Vector3 _scoopPlaneNormal;
        Vector3 _scoopLastBite;
        float _scoopNextTime;
        readonly HashSet<IBrushTarget> _dirtyTargets = new HashSet<IBrushTarget>();
        readonly List<IBrushTarget> _strokeTargets = new List<IBrushTarget>();
        readonly Collider[] _overlapScratch = new Collider[32];

        void Awake()
        {
            if (viewCamera == null) viewCamera = Camera.main;
            if (reachOrigin == null)
                reachOrigin = transform.root != transform ? transform.root
                            : viewCamera != null ? viewCamera.transform : transform;
            _placer = GetComponent<AccessoryPlacer>();
            if (_placer == null) _placer = gameObject.AddComponent<AccessoryPlacer>();
            if (GetComponent<ItemPickupSpawner>() == null) gameObject.AddComponent<ItemPickupSpawner>();
            Roller = GetComponent<SnowballRoller>();
            if (Roller == null) Roller = gameObject.AddComponent<SnowballRoller>();
            if (Roller.config == null) Roller.config = config;
            // Same self-healing as the placer/roller above: a scene that predates the arms still gets them.
            if (hands == null) hands = GetComponentInParent<HandRig>();
            if (hands == null && reachOrigin != null) hands = reachOrigin.gameObject.AddComponent<HandRig>();
            if (hands != null && hands.config == null) hands.config = config;
            var held = GetComponent<HeldAccessoryView>();
            if (held == null) held = gameObject.AddComponent<HeldAccessoryView>();
            held.tool = this;
        }

        void OnEnable() => GamePause.Changed += OnPauseChanged;

        void OnPauseChanged(bool paused)
        {
            if (paused) SetInventoryOpen(false);
        }

        public void SetInventoryOpen(bool open)
        {
            if (open && GamePause.IsPaused) return;
            if (InventoryOpen == open) return;
            InventoryOpen = open;
            if (_placer.ActiveChain != null) FinishChainWithFlourish();
            _placer.CancelPlacement();
            ResetItemScroll();
            if (IsSculpting || _scooping)
            {
                IsSculpting = false;
                _scooping = false;
                Flush();
            }
            CurrentOp = BrushOp.None;
            AimingCut = false;
            if (planeCut != null) planeCut.Hide();
            HideBrushCursor();
            _actions.Clear();
            ThrowCharge = 0f;
            _lmbDownTime = -1f;
            _lockedLastFrame = false;
            if (!GamePause.IsPaused)
            {
                Cursor.lockState = open ? CursorLockMode.None : CursorLockMode.Locked;
                Cursor.visible = open;
            }
        }

        public void HandleTab()
        {
            if (GamePause.IsPaused) return;
            if (InventoryOpen) SetInventoryOpen(false);
            else if (Mode == ToolMode.Accessory) SetMode(ToolMode.Hand);
            else SetInventoryOpen(true);
        }

        /// <summary>A grid click equips a stack and returns to the world.</summary>
        public void EquipInventorySlot(int slot)
        {
            if (!InventoryOpen || GamePause.IsPaused) return;
            if (slot < 0 || _placer.Inventory.CountAt(slot) == 0) return;
            _placer.Select(slot);
            SetMode(ToolMode.Accessory);
            SetInventoryOpen(false);
        }

        public void SetMode(ToolMode mode)
        {
            if (Mode == mode) return;
            if (_placer.ActiveChain != null) FinishChainWithFlourish();
            _placer.CancelPlacement();
            ResetItemScroll();
            _scooping = false;      // Tab mid-drag: the scoop is over, whatever the button is doing
            Mode = mode;
            HideBrushCursor();
        }

        void OnDisable()
        {
            GamePause.Changed -= OnPauseChanged;
            SetInventoryOpen(false);
            if (_placer != null) _placer.CancelPlacement();
            ResetItemScroll();
            _scooping = false;
        }

        void Update()
        {
            if (config == null || viewCamera == null) return;
            var mouse = Mouse.current;
            var kb = Keyboard.current;

            if (GamePause.IsPaused) return;
            if (kb != null && kb.tabKey.wasPressedThisFrame)
            {
                HandleTab();
                return;
            }
            if (InventoryOpen) return;
            if (Mode == ToolMode.Accessory && !_placer.HasSelectedItem && !_placer.IsBusy)
                SetMode(ToolMode.Hand);

            // Sculpting only in mouse-look mode: while the cursor is free, LMB is the relock gesture
            // (PlayerController.HandleCursor) and must not scoop. The relock happens synchronously in the
            // player's Update and script order between the two is unspecified, so also swallow the FIRST
            // locked frame — that is the frame carrying the relock click.
            bool locked = Cursor.lockState == CursorLockMode.Locked;
            bool justLocked = locked && !_lockedLastFrame;
            _lockedLastFrame = locked;
            if (!locked || justLocked)
            {
                ResetItemScroll();
                _scooping = false;
                if (IsSculpting) { IsSculpting = false; Flush(); }
                if (_placer != null && _placer.ActiveChain != null) FinishChainWithFlourish();
                HideBrushCursor();
                if (_placer != null && _placer.IsPlacing) _placer.CancelPlacement();
                _actions.Clear();
                ThrowCharge = 0f;
                _lmbDownTime = -1f;
                return;
            }

            HandleScroll(mouse, kb != null && kb.leftShiftKey.isPressed);
            Aim();

            if (Mode == ToolMode.Accessory) UpdateAccessory(mouse, kb);
            else UpdateHand(mouse, kb);

            ComputeActions(kb);
            DriveHands();
            NudgeDrapes();

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
            }
        }

        // ---------- prompts ----------

        /// <summary>
        /// The whole offer, rebuilt each frame: one row per thing you can do, in the order the hands would reach
        /// for them. Each row carries its own input, so a row can only appear with the key that actually performs
        /// it — which is the point. Every branch here mirrors a branch in <see cref="UpdateHand"/> or
        /// <see cref="UpdateAccessory"/>; if a condition drifts apart from its twin the prompt starts promising
        /// something the click will not do, so they are written to read the same way round.
        /// </summary>
        void ComputeActions(Keyboard kb)
        {
            _actions.Clear();
            bool onSnow = HasHit && Target != null;
            bool shift = kb != null && kb.leftShiftKey.isPressed;
            bool bothHandsBusy = Roller.IsCarrying && Roller.TwoHandedCarry;

            if (AimedPickup != null && !bothHandsBusy && !_placer.IsBusy && _placer.ActiveChain == null && (Mode == ToolMode.Accessory || !Roller.IsCarrying))
            {
                // A pickup under the cursor takes the click over, so it owns the LMB row: either collecting the
                // item, or saying why it cannot. "Inventory full" is not an action, but a blank row where the
                // click does nothing is how a player concludes the game is broken.
                Offer(AimedPickup.CanCollect(_placer.Inventory) ? CursorAction.CollectItem : CursorAction.StackFull,
                      InputGlyph.LeftMouse);
            }
            else if (Mode == ToolMode.Accessory)
            {
                if (_placer.IsBusy || bothHandsBusy) { /* mid-placement or both hands full: nothing on offer */ }
                else if (_placer.ActiveChain != null)
                {
                    // No prompt on another sculpture: the click would do nothing, so promising a point would lie.
                    var click = AimedAnchor != null && AimedAnchor.Chain == _placer.ActiveChain ? CursorAction.UsePoint
                              : onSnow && _placer.CanExtendOnto(Target) ? CursorAction.AddPoint : CursorAction.None;
                    Offer(click, InputGlyph.LeftMouse);
                    // Two ways to finish a strand came in from either side of the merge and both still work, so
                    // both are drawn. Shown rather than quietly aliased: an action reachable by a key nothing
                    // mentions is the thing this scheme exists to stop. Worth collapsing to one gesture.
                    Offer(CursorAction.FinishChain, InputGlyph.RightMouse);
                    Offer(CursorAction.FinishChain, InputGlyph.ShiftLeftMouse);
                }
                else
                {
                    // Selected is null with an empty inventory now, so HasSelectedItem has to be asked before
                    // its Drape — and the answer is a row of its own: with nothing to place, the honest prompt
                    // is where to get some, not a click that would do nothing.
                    Offer(AimedProp != null ? (_placer.CanRetrieve(AimedProp) ? CursorAction.RetrieveAccessory : CursorAction.None)
                        : onSnow ? (!_placer.HasSelectedItem ? CursorAction.FindItems
                            : _placer.Selected.Drape != null ? CursorAction.AddPoint : CursorAction.PlaceAccessory)
                        : CursorAction.None, InputGlyph.LeftMouse);
                }
                Offer(CursorAction.PickAccessory, InputGlyph.Scroll);
            }
            else if (_scooping)
            {
                // The button is already down and digging. Nothing else is on offer until it comes up, which is
                // exactly what UpdateHand does with it, so this is the whole row.
                Offer(CursorAction.ScoopAcross, InputGlyph.LeftMouse, hold: true);
            }
            else if (Roller.IsCarrying)
            {
                // LMB always lets the snow go where it is; a hold on a ball turns that into a throw — unless the
                // ball is too heavy to get off the ground, in which case there is no throw to promise.
                bool canThrow = Roller.IsCarryingBall && !Roller.TooHeavyToLift;
                Offer(ThrowCharge > 0f ? CursorAction.Throw
                    : (onSnow && Target != Roller.Carried) ? CursorAction.AttachSnowball
                    : CursorAction.Drop, InputGlyph.LeftMouse);
                if (canThrow && ThrowCharge <= 0f)
                    Offer(CursorAction.Throw, InputGlyph.LeftMouse, hold: true);
                // Smoothing with a ball still in hand: the ball stays put, RMB pats the snow you are aiming at.
                if (Roller.IsCarryingBall && !Roller.TooHeavyToLift && onSnow && Target != Roller.Carried)
                    Offer(CursorAction.Smooth, InputGlyph.RightMouse);
            }
            else if (onSnow)
            {
                // Shift lines up the planar shave and owns the click while it does, so scoop and slice are never
                // offered together — and neither is smoothing, which UpdateHand returns before ever reaching.
                if (shift)
                {
                    Offer(CursorAction.SliceSnow, InputGlyph.ShiftLeftMouse);
                    Offer(CursorAction.RotateSlice, InputGlyph.ShiftScroll);
                }
                else
                {
                    Offer(CursorAction.ScoopSnow, InputGlyph.LeftMouse);
                    if (config.scoopHoldSpacing > 0f)
                        Offer(CursorAction.ScoopAcross, InputGlyph.LeftMouse, hold: true);
                    // Whether you can carry it off is worth saying either way: "too heavy" is how a player
                    // learns that the limit is their own arms and that a friend changes the answer.
                    if (LiftOffered)
                        Offer(Roller.CanLift(Target) ? CursorAction.PickUpSnow : CursorAction.TooHeavy,
                              InputGlyph.PickUp);
                    Offer(CursorAction.Smooth, InputGlyph.RightMouse);
                    Offer(CursorAction.BrushRadius, InputGlyph.Scroll);
                }
            }
            else if (HasGroundHit && Roller.CanReachGround(GroundPoint) && !shift)
            {
                Offer(CursorAction.ScoopSnow, InputGlyph.LeftMouse);
                Offer(CursorAction.BrushRadius, InputGlyph.Scroll);
            }
        }

        void Offer(CursorAction action, InputGlyph input, bool hold = false)
        {
            if (action == CursorAction.None) return;
            _actions.Add(new ActionPrompt(action, input, hold));
        }

        // ---------- hands ----------

        /// <summary>Reach and hold poses are measured from here (the player root).</summary>
        Transform Body => reachOrigin != null ? reachOrigin : transform;

        Vector3 _lastBodyPos;

        /// <summary>Walking past a string of lights should set it swinging.</summary>
        void NudgeDrapes()
        {
            Vector3 here = Body.position;
            float moved = (here - _lastBodyPos).magnitude;
            _lastBodyPos = here;
            if (moved < 0.002f) return;                                                      // standing still knocks nothing
            float speed = moved / Mathf.Max(0.0001f, Time.deltaTime);
            DrapedAccessory.BumpAt(here + Vector3.up * 0.5f, config.drapeBumpRadius, Mathf.Clamp01(speed / 4f) * 0.8f);
        }

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
            else if (Mode == ToolMode.Hand && AimedPickup != null && !Roller.IsCarrying)
            {
                goal = AimedPickup.transform.position;
                aim = goal - Body.position;
                weight = config.handHoverWeight;
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
                bool bothHands = Roller.TwoHandedCarry && weight <= 0f && ThrowCharge <= 0f;
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
        /// hand goes over a ball the shoulder looks down on and under one held above.
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
            return centre + dir.normalized * (radius * 0.95f);
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

        void HandleScroll(Mouse mouse, bool shift)
        {
            if (mouse == null) return;
            float scroll = mouse.scroll.ReadValue().y;
            if (Mode == ToolMode.Accessory)
            {
                HandleItemScroll(scroll);
                return;
            }
            if (math.abs(scroll) <= 0.01f) return;
            int dir = scroll > 0 ? 1 : -1;
            if (shift && Mode == ToolMode.Hand)
                _cutAngle = Mathf.Repeat(_cutAngle + dir * cutAngleStep, 360f);
            else
                radiusScale = math.clamp(radiusScale * (dir > 0 ? 1.15f : 1f / 1.15f), 0.25f, 4f);
        }

        void HandleItemScroll(float scroll)
        {
            if (_placer.IsBusy || _placer.ActiveChain != null || !_placer.HasSelectedItem)
            {
                ResetItemScroll();
                return;
            }
            if (math.abs(scroll) <= 0.01f) return;
            float now = Time.unscaledTime;
            if (now < _nextItemScrollTime)
            {
                _itemScroll = 0f; // never queue a burst of delayed switches
                _lastItemScrollTime = now;
                return;
            }
            if (now - _lastItemScrollTime > config.accessoryScrollResetTime
                || (_itemScroll != 0f && Mathf.Sign(_itemScroll) != Mathf.Sign(scroll)))
                _itemScroll = 0f;
            _lastItemScrollTime = now;
            _itemScroll += scroll;
            if (math.abs(_itemScroll) < Mathf.Max(0.01f, config.accessoryScrollThreshold)) return;
            _placer.Step(_itemScroll > 0f ? -1 : 1);
            _itemScroll = 0f;
            _nextItemScrollTime = now + Mathf.Max(0f, config.accessoryScrollCooldown);
        }

        void ResetItemScroll()
        {
            _itemScroll = 0f;
            _lastItemScrollTime = float.NegativeInfinity;
            _nextItemScrollTime = 0f;
        }

        // ---------- aim ----------

        void Aim()
        {
            HasHit = false;
            HasGroundHit = false;
            Target = null;
            AimedProp = null;
            AimedPickup = null;
            AimedAnchor = null;
            AimedSnowball = null;
            AimedColliderPath = "";
            var ray = viewCamera.ViewportPointToRay(new Vector3(0.5f, 0.5f, 0f));
            Vector3 origin = reachOrigin != null ? reachOrigin.position + Vector3.up : ray.origin;
            float rayLength = config.maxReach + Vector3.Distance(ray.origin, origin);

            if (!Physics.Raycast(ray, out var hit, rayLength, sculptMask, QueryTriggerInteraction.Collide)) return;
            if (Vector3.Distance(hit.point, origin) > config.maxReach) return;

            BrushPoint = hit.point;
            BrushNormal = hit.normal;
            AimedColliderPath = hit.collider.transform.parent != null
                ? hit.collider.transform.parent.name + "/" + hit.collider.name : hit.collider.name;

            AimedAnchor = hit.collider.GetComponent<DrapeAnchor>();
            AimedProp = hit.collider.GetComponentInParent<SculptureProp>();
            AimedPickup = hit.collider.GetComponentInParent<ItemPickup>();
            if (AimedPickup != null) return;
            var s = hit.collider.GetComponentInParent<SnowSculpture>();
            if (s != null)
            {
                var flying = s.GetComponent<Snowball>();
                if (flying != null && flying.IsFlying) { AimedColliderPath = ""; return; } // airborne: not a target for anything
                Target = s;
                HasHit = AimedProp == null; // aiming at a prop is not a snow hit
                var ball = s.GetComponent<Snowball>();
                if (ball != null && ball.IsLoose && !ball.IsFlying) AimedSnowball = ball;
                return;
            }
            if (hit.normal.y > 0.6f)
            {
                HasGroundHit = true;
                GroundPoint = hit.point;
            }
        }

        // ---------- hand: brush, scoop, carry, roll, throw ----------

        void UpdateHand(Mouse mouse, Keyboard kb)
        {
            bool lmb = mouse != null && mouse.leftButton.isPressed;
            bool lmbDown = mouse != null && mouse.leftButton.wasPressedThisFrame;
            bool lmbUp = mouse != null && mouse.leftButton.wasReleasedThisFrame;
            bool rmb = mouse != null && mouse.rightButton.isPressed;
            bool shift = kb != null && kb.leftShiftKey.isPressed;

            bool liftDown = kb != null && kb.eKey.wasPressedThisFrame;

            if (AimedPickup != null && !Roller.IsCarrying)
            {
                if (IsSculpting) { IsSculpting = false; Flush(); }
                HideBrushCursor();
                AimingCut = false;
                if (planeCut != null) planeCut.Hide();
                ThrowCharge = 0f;
                _lmbDownTime = -1f;
                if (lmbDown) CollectAimedItem();
                return;
            }

            // Planar shave. Only while actually aiming at a sculpture: Shift is also the run key, and a preview
            // that flashed up every time you sprinted across the field would be unreadable noise.
            AimingCut = shift && !Roller.IsCarrying && HasHit && Target != null;
            if (AimingCut)
            {
                // The red scoop sphere is a lie while slicing — LMB cuts, it does not scoop.
                HideBrushCursor();
                if (planeCut != null) planeCut.Show(_cutAngle, SliceLengthPixels());
                if (lmbDown) CommitPlaneCut();
                return; // Shift owns the click while a cut is lined up
            }
            if (planeCut != null) planeCut.Hide();

            // --- the scoop button is still down from a bite: keep biting as the cursor sweeps ---
            // This branch comes before the carrying one deliberately. The first bite fills your hands, so
            // without it the same press that started the scoop would immediately be read as "holding to throw
            // what I just dug out" — the hold has to keep meaning what it meant when it went down.
            if (_scooping)
            {
                if (lmb) { ContinueScoopDrag(); return; }
                _scooping = false;
                _lmbDownTime = -1f;     // this press was a scoop; its release is not a drop or a throw
                ThrowCharge = 0f;
                Flush();
                return;
            }

            // --- carrying: LMB tap lets go where the snow is, hold charges a throw; RMB smooths ---
            if (Roller.IsCarrying)
            {
                bool onSnow = HasHit && Target != null && Target != Roller.Carried;
                // Snow too heavy to pick up cannot be thrown or previewed onto a surface either: it stays on the
                // ground, rolls, and fuses where it sits. One question, asked of the roller, answered the same
                // way by the prompts below.
                bool heavy = Roller.TooHeavyToLift;
                // Patting with a ball in hand is RMB — the same button it is with free hands, no modifier. It
                // used to be Shift+LMB, which the planar shave then took, leaving smoothing reachable only by
                // holding Shift, LMB and RMB together: a bound action nothing could have told you about.
                // RMB does not fight LMB, so letting go and charging a throw keep working while you pat.
                bool sculpting = rmb && Roller.IsCarryingBall && onSnow && !heavy;
                if (lmbDown) _lmbDownTime = Time.time;
                bool charging = lmb && _lmbDownTime >= 0f && Roller.IsCarryingBall && !heavy
                                && Time.time - _lmbDownTime > config.throwTapThreshold;
                ThrowCharge = charging
                    ? Mathf.Min(1f, (Time.time - _lmbDownTime - config.throwTapThreshold) / Mathf.Max(0.05f, Roller.chargeTime))
                    : 0f;

                bool cursorNear = false;
                if (HasGroundHit && reachOrigin != null)
                {
                    Vector3 d = GroundPoint - reachOrigin.position; d.y = 0f;
                    cursorNear = d.magnitude <= config.rollEngageDistance;
                }
                Vector3? preview = (charging || sculpting || heavy) ? null
                                 : onSnow ? Roller.AttachCentre(BrushPoint, BrushNormal) : (Vector3?)null;
                Roller.UpdateCarrying(preview, liftToHand: charging || sculpting,
                                      rollOnGround: heavy || (cursorNear && !charging && !sculpting && !onSnow));

                if (lmbUp && _lmbDownTime >= 0f)
                {
                    float held = Time.time - _lmbDownTime;
                    _lmbDownTime = -1f;
                    ThrowCharge = 0f;
                    if (Roller.IsCarryingBall && !heavy && held > config.throwTapThreshold)
                    {
                        var cam = viewCamera.transform;
                        Roller.Throw(cam.position, cam.forward,
                            Mathf.Min(1f, (held - config.throwTapThreshold) / Mathf.Max(0.05f, Roller.chargeTime)));
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
                if (sculpting) { UpdateBrush(smoothing: true); return; }
                HideBrushCursor();
                return;
            }

            ThrowCharge = 0f;
            _lmbDownTime = -1f;

            // --- free hands, E: pick the whole thing up, if these arms can manage it ---
            // Putting it down again is the carrying branch above: a tap lets go where the snow is, which on
            // another sculpture fuses the two together. That is how a snowman gets assembled.
            if (liftDown && CanLiftAimed)
            {
                Vector3 at = (Vector3)BrushPoint;
                Roller.PickUp(Target, at);
                PulseFree(at, at - Body.position);
                HideBrushCursor();
                return;
            }

            // --- free hands, LMB: scoop a chunk out of the aimed sculpture, or a handful off the ground ---
            if (lmbDown && !shift)
            {
                if (HasHit && Target != null) { BeginScoopDrag(); ScoopChunk(BrushPoint, CurrentRadius()); return; }
                if (HasGroundHit && Roller.CanReachGround(GroundPoint))
                {
                    Roller.ScoopFrom(GroundPoint);
                    HideBrushCursor();
                    return;
                }
            }

            UpdateBrush(smoothing: rmb);
        }

        /// <summary>
        /// One discrete bite at <paramref name="point"/>. The chunk is built from the snow the brush sphere
        /// actually overlaps - so a bite at the edge of a sculpture hands you a half-sphere - and the same kernel
        /// is then removed from every grid under it. The point is passed in rather than read from the aim because
        /// a held scoop takes its bites on a fixed depth plane, not wherever the ray happens to land.
        ///
        /// Colliders are re-cooked here for a single bite but left to <see cref="Flush"/> during a drag: cooking
        /// a 192³ sculpture's chunks ten times a second is the one thing that cannot keep up with a sweep.
        /// </summary>
        void ScoopChunk(float3 point, float radius, bool deferColliders = false)
        {
            var factory = SculptureFactory.Instance;
            if (factory == null) return;
            GatherStrokeTargets(Target, point, radius);

            var chunk = factory.CreateEmptySnowball((Vector3)point, radius);
            foreach (var t in _strokeTargets)
                if (t is SnowSculpture s && s != null && s != chunk.Sculpture)
                    chunk.Sculpture.ExtractFrom(s, point, radius, config.addShoulder);

            float volume = chunk.Sculpture.DensityVolume();
            if (volume <= 1e-5f)
            {
                Destroy(chunk.gameObject);           // nothing but air under the cursor...
                PulseFree((Vector3)point, (Vector3)point - Body.position); // ...but the grab still happened
                return;
            }

            foreach (var t in _strokeTargets)
            {
                if (t is Component c && c == null) continue;
                t.ApplyAdd(point, radius, -255f, config.addShoulder); // full-strength one-shot removal
                t.Remesh();
                if (deferColliders) _dirtyTargets.Add(t);
                else t.RebuildColliders();
            }
            Roller.TakeChunk(chunk, volume);
            CollectNetTargets(chunk.Sculpture);
            SculptureNet.RaiseScooped(new SculptureNet.ScoopInfo
            { point = point, radius = radius, targets = _netTargets, chunk = chunk, resultRadius = chunk.radius });
        }

        // ---------- continuous scoop ----------

        /// <summary>
        /// Remember the depth this scoop started at: the plane through the first contact point, facing the way
        /// the surface did. Every later bite of the same press is centred on that plane, so sweeping the cursor
        /// shaves an even band across the shape instead of boring deeper and deeper into its own crater — the
        /// ray would otherwise land on the inside wall of the hole just dug and take the next bite from there.
        /// </summary>
        void BeginScoopDrag()
        {
            _scooping = true;
            _scoopPlanePoint = (Vector3)BrushPoint;
            _scoopPlaneNormal = ((Vector3)BrushNormal).sqrMagnitude > 1e-6f
                ? ((Vector3)BrushNormal).normalized : Vector3.up;
            _scoopLastBite = _scoopPlanePoint;
            _scoopNextTime = Time.time + Mathf.Max(0f, config.scoopHoldInterval);
        }

        /// <summary>
        /// The scoop button is still down. Keep the snow already in hand where it belongs, draw the bite the
        /// cursor is over at the drag's own depth, and take it once the cursor has actually moved on.
        /// </summary>
        void ContinueScoopDrag()
        {
            // Both hands are busy: one holds what has been dug out so far, the other goes on digging.
            if (Roller.IsCarrying) Roller.UpdateCarrying(null, liftToHand: !Roller.TooHeavyToLift);
            ThrowCharge = 0f;
            CurrentOp = BrushOp.None;

            float radius = CurrentRadius();
            if (!HasHit || Target == null || config.scoopHoldSpacing <= 0f) { HideBrushCursor(); return; }

            // How far the snow under the cursor sits from the plane, along it. Past a radius the shape has
            // fallen away from the depth this scoop is working at and there is nothing there to take.
            float offPlane = Vector3.Dot((Vector3)BrushPoint - _scoopPlanePoint, _scoopPlaneNormal);
            if (offPlane < -radius) { HideBrushCursor(); return; }

            Vector3 at = (Vector3)BrushPoint - _scoopPlaneNormal * offPlane;
            ShowCursor(at, radius, cursorCarveColor);
            DriveScoopHand(at);

            if (Time.time < _scoopNextTime) return;
            float step = radius * config.scoopHoldSpacing;
            if ((at - _scoopLastBite).sqrMagnitude < step * step) return;
            _scoopNextTime = Time.time + Mathf.Max(0f, config.scoopHoldInterval);
            _scoopLastBite = at;
            ScoopChunk(at, radius, deferColliders: true);
        }

        /// <summary>The digging hand stays on the snow it is biting, whichever hand is free.</summary>
        void DriveScoopHand(Vector3 at)
        {
            if (hands == null || !hands.IsReady) return;
            hands.Reach(Roller.IsCarrying ? HandRig.Side.Left : HandRig.Side.Right, at, 1f, at - Body.position);
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

        /// <summary>
        /// The stroke loop. RMB smooths; adding is disabled (LMB scoops instead), so smoothing is the only op
        /// this still runs. The caller decides whether it is smoothing rather than reading the mouse here: with a
        /// ball in hand the same button has to be read alongside the let-go/throw timing, and two places reading
        /// the button for themselves is how the brush and the HUD prompt came to disagree about it.
        /// </summary>
        void UpdateBrush(bool smoothing)
        {
            BrushOp op = smoothing ? BrushOp.Smooth : BrushOp.None;
            CurrentOp = op;

            IBrushTarget target = Target;

            float radius = CurrentRadius();
            // Aiming at a prop still shows the cursor so the brush feels continuous over accessories.
            if (target != null) ShowCursor((Vector3)BrushPoint, radius, smoothing ? cursorAddColor : cursorCarveColor);
            else if (cursor != null) cursor.gameObject.SetActive(false);

            bool pressing = op != BrushOp.None;
            if (pressing && target != null)
            {
                if (!IsSculpting) { IsSculpting = true; _tickAccumulator = 1f / config.ticksPerSecond; } // first tick immediate
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
                }
            }
            else if (IsSculpting && !pressing)
            {
                IsSculpting = false;
                Flush();
            }
        }

        public float CurrentRadius() => config.addRadius * radiusScale;

        /// <summary>
        /// Snow under the reticle that these arms could pick up whole. A loose ball, a snowball somebody left
        /// lying about, a finished sculpture — the same question for all three, asked of the roller so the
        /// prompt and the key can never disagree about the answer.
        /// </summary>
        bool CanLiftAimed => LiftOffered && Roller.CanLift(Target);

        /// <summary>
        /// As above, but without the verdict, so the HUD can say why the key is on screen doing nothing.
        ///
        /// Mid-stroke is deliberately not offered. Weighing a sculpture means a pass over its grid, cached until
        /// the snow changes - and a stroke changes it every tick, so asking while patting would rescan a grown
        /// 192 grid twice a second for nothing. You are not about to pick up what you are patting.
        /// </summary>
        bool LiftOffered => Mode == ToolMode.Hand && !Roller.IsCarrying && !_scooping
                            && CurrentOp == BrushOp.None && !IsSculpting
                            && HasHit && Target != null && AimedProp == null;

        /// <summary>
        /// Chop the aimed sculpture along the previewed plane. The plane is spanned by the camera's forward
        /// direction and the on-screen line, which is why the preview can be drawn as a line at all — see
        /// PlaneCutPreview. Everything on the far side of it is removed and simply gone; this is a cut, not a
        /// scoop, so nothing is handed back to the player.
        /// </summary>
        void CommitPlaneCut()
        {
            var s = Target;
            if (s == null) return;

            Transform cam = viewCamera != null ? viewCamera.transform : transform;
            float a = _cutAngle * Mathf.Deg2Rad;
            Vector3 lineDir = cam.right * Mathf.Cos(a) + cam.up * Mathf.Sin(a);
            Vector3 normal = Vector3.Cross(cam.forward, lineDir).normalized; // points into the doomed half
            if (normal.sqrMagnitude < 1e-6f) return;

            float removed = s.ApplyPlaneCut(BrushPoint, normal, lineDir, config.sliceLength);
            if (removed <= 0f) return;

            s.Remesh();
            s.RebuildColliders();
        }

        /// <summary>
        /// The cut line's length in screen pixels, measured at the depth of the point under the cursor. A fixed
        /// world length does not project to a fixed number of pixels, so the line is drawn true where you are
        /// aiming rather than averaged over a depth range you cannot see.
        /// </summary>
        float SliceLengthPixels()
        {
            if (viewCamera == null || config == null) return 0f;
            Transform cam = viewCamera.transform;
            float depth = Mathf.Max(Vector3.Dot((Vector3)BrushPoint - cam.position, cam.forward), 0.05f);
            float worldHeight = 2f * depth * Mathf.Tan(viewCamera.fieldOfView * 0.5f * Mathf.Deg2Rad);
            if (worldHeight <= 0.0001f) return 0f;
            return config.sliceLength / worldHeight * Screen.height;
        }

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
        void GatherStrokeTargets(IBrushTarget aimed, float radius) => GatherStrokeTargets(aimed, BrushPoint, radius);

        /// <summary>As above, with the kernel somewhere other than the aim point (a held scoop's depth plane).</summary>
        void GatherStrokeTargets(IBrushTarget aimed, float3 at, float radius)
        {
            _strokeTargets.Clear();
            _strokeTargets.Add(aimed);
            int n = Physics.OverlapSphereNonAlloc((Vector3)at, radius, _overlapScratch, sculptMask, QueryTriggerInteraction.Ignore);
            for (int i = 0; i < n; i++)
            {
                var s = _overlapScratch[i].GetComponentInParent<SnowSculpture>();
                if (s == null) continue;
                var ball = s.GetComponent<Snowball>();
                if (ball != null && ball.IsFlying) continue;
                if (Roller != null && s == Roller.Carried) continue;
                if (!_strokeTargets.Contains(s)) _strokeTargets.Add(s);
            }
        }

        /// <summary>Finish a stroke: final remesh + collider cook for every touched grid.</summary>
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
        }

        /// <summary>Put the brush sphere somewhere and colour it. The one place the cursor is positioned.</summary>
        void ShowCursor(Vector3 at, float radius, Color colour)
        {
            if (cursor == null) return;
            cursor.gameObject.SetActive(true);
            cursor.position = at;
            cursor.localScale = Vector3.one * radius * 2f;
            SetCursorColor(colour);
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
            bool shift = kb != null && kb.leftShiftKey.isPressed;
            bool lmbDown = mouse != null && mouse.leftButton.wasPressedThisFrame;

            if (_placer.IsBusy || (Roller.IsCarrying && Roller.TwoHandedCarry)) return;

            // --- a chain is being placed: clicks add points, the strand drapes live to the cursor ---
            if (_placer.ActiveChain != null)
            {
                _placer.UpdateChainTip(Target, HasHit ? BrushPoint : (Vector3?)null);
                if (mouse != null && mouse.rightButton.wasPressedThisFrame) { FinishChainWithFlourish(); return; }
                if (!lmbDown) return;
                if (shift) { FinishChainWithFlourish(); return; }
                if (AimedAnchor != null && AimedAnchor.Chain == _placer.ActiveChain)
                {
                    _placer.UseExistingPoint(AimedAnchor);
                    PulseFree(AimedAnchor.transform.position, -(Vector3)BrushNormal);
                }
                else if (HasHit && _placer.CanExtendOnto(Target))
                {
                    _placer.AppendPoint(Target, BrushPoint);
                    PulseFree(BrushPoint, -(Vector3)BrushNormal);
                }
                return;
            }

            if (!lmbDown) return;
            if (AimedPickup != null) CollectAimedItem();
            else if (AimedProp != null)
            {
                Vector3 at = AimedProp.transform.position; // read before it is unparented and destroyed
                if (_placer.Retrieve(AimedProp)) PulseFree(at, at - Body.position);
            }
            else if (HasHit && Target != null && _placer.HasSelectedItem)
            {
                if (_placer.Selected.Drape != null)
                {
                    _placer.BeginChain(Target, BrushPoint);
                    PulseFree(BrushPoint, -(Vector3)BrushNormal);
                }
                else _placer.BeginPlacement(Target, BrushPoint, BrushNormal,
                    config.accessoryPlaceTime, config.accessoryPlaceRecovery);
            }
        }

        void CollectAimedItem()
        {
            if (AimedPickup == null) return;
            Vector3 at = AimedPickup.transform.position;
            string id = AimedPickup.itemId;
            bool selectCollected = !_placer.HasSelectedItem;
            if (AimedPickup.Collect(_placer.Inventory) <= 0) return;
            if (selectCollected) _placer.Select(_placer.Inventory.FindFirstSlot(id));
            PulseFree(at, at - Body.position);
        }

        /// <summary>End the chain: a one-point chain was a misclick, so drop it rather than leaving a stub.</summary>
        void FinishChainWithFlourish()
        {
            var chain = _placer.ActiveChain;
            if (chain == null) return;
            Vector3 at = chain.transform.position;
            if (chain.PointCount < 2) _placer.CancelChain();
            else _placer.FinishChain();
            PulseFree(at, at - Body.position);
        }
    }
}
