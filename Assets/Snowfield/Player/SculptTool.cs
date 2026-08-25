using System.Collections.Generic;
using Snowfield.Config;
using Snowfield.Field;
using Snowfield.Sculpture;
using Snowfield.Voxel;
using Unity.Mathematics;
using UnityEngine;
using UnityEngine.InputSystem;

namespace Snowfield.Player
{
    /// <summary>
    /// The player's hands. Aims a screen-centre ray and dispatches to the active <see cref="ToolMode"/>:
    ///   Sculpt     — LMB add, RMB carve on sculptures, resting snowballs (converted first) and the ground; scroll = radius
    ///   Empty Hand — LMB smooth on snow; hold LMB to push a snowball (or start one on the ground); RMB picks up anything;
    ///                carrying: LMB set down / attach / stack, hold RMB to throw
    ///   Accessory  — scroll picks, hover ghost, LMB place, RMB retrieve (via <see cref="AccessoryPlacer"/>)
    /// Left Shift cycles modes; 1/2/3 select (locked while hands are full). Remesh on a timer while stroking;
    /// colliders rebuild on release. Lives on the Player (or a child); the HUD is a separate object that reads this.
    /// </summary>
    public class SculptTool : MonoBehaviour
    {
        public enum BrushOp { None, Add, Carve, Smooth }

        public SculptFeelConfig config;
        public Camera viewCamera;
        public LayerMask sculptMask = ~0;
        [Tooltip("How far from the character the brush can reach (the ray itself starts at the camera).")]
        public float maxReach = 4f;
        [Tooltip("Reach is measured from here. Defaults to the SnowCharacter in the scene, else the camera.")]
        public Transform reachOrigin;
        [Tooltip("Visual brush cursor; scaled to brush diameter.")]
        public Transform cursor;
        [Tooltip("Loose field items are small; aim at them with a sphere cast of this radius (m).")]
        public float itemPickRadius = 0.12f;
        [Tooltip("Let Sculpt mode raise/carve the ground (draw in the snow).")]
        public bool allowGroundSculpting = false;

        public ToolMode Mode { get; private set; } = ToolModeInfo.Default;
        public BrushOp CurrentOp { get; private set; } = BrushOp.None;
        public bool IsSculpting { get; private set; }

        // ---- aim results (refreshed every frame) ----
        public SnowSculpture Target { get; set; }
        public SnowTerrain TargetTerrain { get; private set; }
        public SculptureProp AimedProp { get; private set; }
        /// <summary>A loose, resting snowball under the reticle (it is also <see cref="Target"/>).</summary>
        public Snowball AimedSnowball { get; private set; }
        public WorldItem AimedWorldItem { get; private set; }
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
        public AccessoryInventory Inventory => _placer != null ? _placer.Inventory : null;
        /// <summary>0..1 while charging a throw (RMB held with a carried ball); 0 otherwise. Drives the HUD ring.</summary>
        public float ThrowCharge { get; private set; }
        /// <summary>What LMB would do right now (HUD prompt). Recomputed every frame.</summary>
        public CursorAction PrimaryAction { get; private set; }
        /// <summary>What RMB would do right now (HUD prompt). Recomputed every frame.</summary>
        public CursorAction SecondaryAction { get; private set; }

        /// <summary>Radius multipliers on top of config, driven by scroll; one per brush mode. Session-only.</summary>
        public float snowRadiusScale = 1f;
        public float handRadiusScale = 1f;
        public float radiusScale
        {
            get => Mode == ToolMode.EmptyHand ? handRadiusScale : snowRadiusScale;
            set { if (Mode == ToolMode.EmptyHand) handRadiusScale = value; else snowRadiusScale = value; }
        }

        AccessoryPlacer _placer;
        bool _throwArmed;
        float _tickAccumulator;
        float _remeshAccumulator;
        readonly HashSet<IBrushTarget> _dirtyTargets = new HashSet<IBrushTarget>();
        readonly List<IBrushTarget> _strokeTargets = new List<IBrushTarget>();
        readonly Collider[] _overlapScratch = new Collider[32];

        void Awake()
        {
            if (viewCamera == null) viewCamera = Camera.main;
            if (reachOrigin == null)
            {
                var ch = GetComponentInParent<SnowCharacter>();
                if (ch == null) ch = FindAnyObjectByType<SnowCharacter>();
                reachOrigin = ch != null ? ch.transform : viewCamera.transform;
            }
            _placer = GetComponent<AccessoryPlacer>();
            if (_placer == null) _placer = gameObject.AddComponent<AccessoryPlacer>();
            Roller = GetComponent<SnowballRoller>();
            if (Roller == null) Roller = gameObject.AddComponent<SnowballRoller>();
            if (Roller.config == null) Roller.config = config;
        }

        /// <summary>True while the hands are busy (pushing or carrying a snowball); modes are locked then.</summary>
        public bool HandsFull => Roller != null && Roller.IsEngaged;

        public void SetMode(ToolMode mode)
        {
            if (mode == Mode) return;
            if (HandsFull) return;
            if (IsSculpting) { IsSculpting = false; Flush(); }
            Mode = mode;
            if (cursor != null) cursor.gameObject.SetActive(false);
            _placer.HidePreview();
        }

        void Update()
        {
            if (config == null || viewCamera == null) return;
            var mouse = Mouse.current;
            var kb = Keyboard.current;

            HandleModeInput(kb);
            HandleScroll(mouse);
            Aim();

            switch (Mode)
            {
                case ToolMode.Sculpt:
                    UpdateSculpt(mouse);
                    break;
                case ToolMode.EmptyHand:
                    UpdateEmptyHand(mouse);
                    break;
                case ToolMode.Accessory:
                    UpdateAccessory(mouse);
                    break;
            }

            ComputeActions();

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

        void ComputeActions()
        {
            CursorAction p = CursorAction.None, s = CursorAction.None;
            bool onSnow = HasHit && Target != null;
            switch (Mode)
            {
                case ToolMode.Sculpt:
                    if (Target != null || (allowGroundSculpting && TargetTerrain != null)) { p = CursorAction.AddSnow; s = CursorAction.Carve; }
                    break;

                case ToolMode.EmptyHand:
                    if (Roller.IsPushing) { /* busy: no prompts while pushing */ }
                    else if (Roller.IsCarrying)
                    {
                        if (Roller.IsCarryingBall) s = CursorAction.Throw;
                        if (ThrowCharge <= 0f)
                        {
                            if (onSnow && Target != Roller.Carried) p = CursorAction.AttachSnowball;
                            else if (HasGroundHit) p = CursorAction.SetDownSnowball;
                        }
                    }
                    else
                    {
                        if (AimedSnowball != null) { if (Roller.CanPush(AimedSnowball)) p = CursorAction.PushSnowball; s = CursorAction.PickUpSnowball; }
                        else if (AimedWorldItem != null) { s = CursorAction.PickUpItem; }
                        else if (AimedProp != null) { s = CursorAction.RetrieveAccessory; if (Target != null) p = CursorAction.Smooth; }
                        else if (onSnow) { p = CursorAction.Smooth; s = CursorAction.PickUpSculpture; }
                        else if (HasGroundHit && Roller.CanReachGround(GroundPoint)) p = CursorAction.StartSnowball;
                    }
                    break;

                case ToolMode.Accessory:
                    if (onSnow && _placer.CanPlaceSelected) p = CursorAction.PlaceAccessory;
                    if (AimedProp != null) s = CursorAction.RetrieveAccessory;
                    else if (AimedWorldItem != null) s = CursorAction.PickUpItem;
                    break;
            }
            PrimaryAction = p;
            SecondaryAction = s;
        }

        // ---------- input ----------

        void HandleModeInput(Keyboard kb)
        {
            if (kb == null) return;
            var all = ToolModeInfo.All;
            if (kb.leftShiftKey.wasPressedThisFrame)
                SetMode(all[(ToolModeInfo.IndexOf(Mode) + 1) % all.Length]);
            if (kb.digit1Key.wasPressedThisFrame || kb.numpad1Key.wasPressedThisFrame) SetMode(all[0]);
            if (kb.digit2Key.wasPressedThisFrame || kb.numpad2Key.wasPressedThisFrame) SetMode(all[1]);
            if (kb.digit3Key.wasPressedThisFrame || kb.numpad3Key.wasPressedThisFrame) SetMode(all[2]);
        }

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
            TargetTerrain = null;
            AimedProp = null;
            AimedSnowball = null;
            AimedWorldItem = null;
            AimedColliderPath = "";
            var ray = viewCamera.ViewportPointToRay(new Vector3(0.5f, 0.5f, 0f));
            Vector3 origin = reachOrigin != null ? reachOrigin.position + Vector3.up : ray.origin;
            float rayLength = maxReach + Vector3.Distance(ray.origin, origin);

            // Loose items are thin; a fat cast makes them forgiving to aim at.
            if (Physics.SphereCast(ray, itemPickRadius, out var fat, rayLength, sculptMask, QueryTriggerInteraction.Collide))
            {
                var item = fat.collider.GetComponentInParent<WorldItem>();
                if (item != null && Vector3.Distance(fat.point, origin) <= maxReach) AimedWorldItem = item;
            }

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
                return;
            }
            var terrain = hit.collider.GetComponentInParent<SnowTerrain>();
            if (terrain != null || hit.normal.y > 0.6f)
            {
                TargetTerrain = terrain;
                HasGroundHit = true;
                GroundPoint = hit.point;
            }
        }

        // ---------- sculpt mode: brush on sculptures, resting snowballs, or the ground ----------

        void UpdateSculpt(Mouse mouse)
        {
            // Loose snowballs are sculptures too, so the brush simply strokes Target.
            UpdateBrush(mouse, allowCarve: true, allowTerrain: allowGroundSculpting, showCursor: true);
        }

        /// <summary>Shared stroke loop for Sculpt (add/carve) and Empty Hand (smooth).</summary>
        void UpdateBrush(Mouse mouse, bool allowCarve, bool allowTerrain, bool showCursor)
        {
            _placer.HidePreview();
            bool lmb = mouse != null && mouse.leftButton.isPressed;
            bool rmb = mouse != null && mouse.rightButton.isPressed && allowCarve;

            BrushOp op = BrushOp.None;
            if (Mode == ToolMode.Sculpt) op = lmb ? BrushOp.Add : (rmb ? BrushOp.Carve : BrushOp.None);
            else if (Mode == ToolMode.EmptyHand) op = lmb ? BrushOp.Smooth : BrushOp.None;
            CurrentOp = op;

            IBrushTarget target = Target;
            bool onTerrain = false;
            if (target == null && allowTerrain && TargetTerrain != null) { target = TargetTerrain; onTerrain = true; }

            float radius = CurrentRadius();
            if (cursor != null)
            {
                // Aiming at a prop still shows the cursor so the brush feels continuous over accessories.
                bool show = showCursor && target != null;
                cursor.gameObject.SetActive(show);
                if (show)
                {
                    cursor.position = BrushPoint;
                    cursor.localScale = Vector3.one * radius * 2f;
                }
            }

            bool pressing = op != BrushOp.None;
            if (pressing && target != null)
            {
                if (!IsSculpting) { IsSculpting = true; _tickAccumulator = 1f / config.ticksPerSecond; } // first tick immediate
                // Adding at the wall of a fixed sculpture: grow the grid first so the stroke continues seamlessly.
                if (op == BrushOp.Add && Target != null && Target.GetComponent<Snowball>() == null
                    && SculptureFactory.Instance != null
                    && !Target.ContainsWorldSphere(BrushPoint, radius, config.regrowMarginVoxels))
                {
                    var grown = SculptureFactory.Instance.Regrow(Target,
                        new Bounds(BrushPoint, Vector3.one * (radius * 2f + config.regrowMarginVoxels * config.voxelSize * 2f)));
                    if (grown != Target) { Target = grown; target = grown; }
                }
                GatherStrokeTargets(target, allowTerrain, radius);
                foreach (var t in _strokeTargets) _dirtyTargets.Add(t);
                _tickAccumulator += Time.deltaTime;
                float tickDt = 1f / config.ticksPerSecond;
                int ticks = 0;
                while (_tickAccumulator >= tickDt && ticks < 8) { _tickAccumulator -= tickDt; ticks++; }
                for (int i = 0; i < ticks; i++)
                    foreach (var t in _strokeTargets)
                        ApplyTick(t, t is SnowTerrain, op, BrushPoint, radius);
            }
            else if (IsSculpting && !pressing)
            {
                IsSculpting = false;
                Flush();
            }
        }

        public float CurrentRadius() =>
            (Mode == ToolMode.EmptyHand ? config.smoothRadius : config.addRadius) * radiusScale;

        public void ApplyTick(IBrushTarget t, bool terrain, BrushOp op, float3 point, float radius)
        {
            float addRate = terrain ? config.terrainAddPerTick : config.addRatePerTick;
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

        /// <summary>Finish a stroke: final remesh + collider cook.</summary>
        /// <summary>Everything the brush sphere touches: the aimed target plus any other grids under the kernel.</summary>
        void GatherStrokeTargets(IBrushTarget aimed, bool allowTerrain, float radius)
        {
            _strokeTargets.Clear();
            _strokeTargets.Add(aimed);
            int n = Physics.OverlapSphereNonAlloc(BrushPoint, radius, _overlapScratch, sculptMask, QueryTriggerInteraction.Ignore);
            for (int i = 0; i < n; i++)
            {
                var s = _overlapScratch[i].GetComponentInParent<SnowSculpture>();
                if (s != null)
                {
                    var ball = s.GetComponent<Snowball>();
                    if (ball != null && ball.IsFlying) continue;
                    if (Roller != null && s == Roller.Carried) continue;
                    if (!_strokeTargets.Contains(s)) _strokeTargets.Add(s);
                    continue;
                }
                if (allowTerrain)
                {
                    var terrain = _overlapScratch[i].GetComponentInParent<SnowTerrain>();
                    if (terrain != null && !_strokeTargets.Contains(terrain)) _strokeTargets.Add(terrain);
                }
            }
        }

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

        // ---------- empty hand: smooth, push / carry / stack / throw snowballs, pick things up ----------

        void UpdateEmptyHand(Mouse mouse)
        {
            bool lmb = mouse != null && mouse.leftButton.isPressed;
            bool lmbDown = mouse != null && mouse.leftButton.wasPressedThisFrame;
            bool rmbDown = mouse != null && mouse.rightButton.wasPressedThisFrame;
            _placer.HidePreview();

            // --- pushing: only while the button is held; the ball stays where it is on release ---
            if (Roller.IsPushing)
            {
                HideBrushCursor();
                if (!lmb) Roller.Release();
                else Roller.UpdatePushing();
                return;
            }

            // --- carrying: preview on snow / ground / another ball, LMB to act, hold RMB to charge a throw ---
            if (Roller.IsCarrying)
            {
                HideBrushCursor();
                bool rmbHeld = mouse != null && mouse.rightButton.isPressed;
                bool rmbUp = mouse != null && mouse.rightButton.wasReleasedThisFrame;
                if (!rmbHeld) _throwArmed = true; // the pick-up press must be released before a charge can start

                bool charging = _throwArmed && rmbHeld && Roller.IsCarryingBall;
                if (charging) ThrowCharge = Mathf.Min(1f, ThrowCharge + Time.deltaTime / Mathf.Max(0.05f, Roller.chargeTime));

                bool onSnow = HasHit && Target != null;
                Vector3? preview = charging ? null
                                 : onSnow ? Roller.AttachCentre(BrushPoint, BrushNormal)
                                 : HasGroundHit ? Roller.GroundCentre(GroundPoint) : (Vector3?)null;
                Roller.UpdateCarrying(preview);

                if (rmbUp && _throwArmed && ThrowCharge > 0f && Roller.IsCarryingBall)
                {
                    Roller.Throw(viewCamera.transform.position, viewCamera.transform.forward, ThrowCharge);
                    ThrowCharge = 0f;
                    return;
                }
                if (!charging) ThrowCharge = 0f;

                if (lmbDown && !charging)
                {
                    if (onSnow && Target != Roller.Carried) Roller.AttachTo(Target, BrushPoint, BrushNormal);
                    else if (HasGroundHit) Roller.PlaceOnGround(GroundPoint);
                }
                return;
            }
            ThrowCharge = 0f;

            // --- free hands: RMB picks up anything pickable ---
            if (rmbDown)
            {
                if (AimedSnowball != null) { Roller.PickUp(AimedSnowball.Sculpture, BrushPoint); _throwArmed = false; }
                else if (AimedWorldItem != null) _placer.Collect(AimedWorldItem);
                else if (AimedProp != null) _placer.Retrieve(AimedProp);
                else if (HasHit && Target != null) { Roller.PickUp(Target, BrushPoint); _throwArmed = false; }
                return;
            }

            // --- LMB: push a ball (within reach), start a ball on bare ground, or smooth snow ---
            if (lmbDown && AimedSnowball != null)
            {
                if (Roller.CanPush(AimedSnowball)) { Roller.StartPushing(AimedSnowball); HideBrushCursor(); }
                return;
            }
            if (lmbDown && HasGroundHit && AimedWorldItem == null)
            {
                if (Roller.CanReachGround(GroundPoint)) { Roller.StartNew(GroundPoint); HideBrushCursor(); }
                return;
            }

            UpdateBrush(mouse, allowCarve: false, allowTerrain: false, showCursor: true); // smoothing on sculpture snow only
        }

        void HideBrushCursor()
        {
            CurrentOp = BrushOp.None;
            if (cursor != null) cursor.gameObject.SetActive(false);
        }

        // ---------- accessory mode ----------

        void UpdateAccessory(Mouse mouse)
        {
            CurrentOp = BrushOp.None;
            if (cursor != null) cursor.gameObject.SetActive(false);

            _placer.UpdatePreview(HasHit && _placer.CanPlaceSelected, BrushPoint, BrushNormal);

            if (mouse == null) return;
            if (mouse.leftButton.wasPressedThisFrame && HasHit && Target != null)
                _placer.Place(Target, BrushPoint, BrushNormal);
            if (mouse.rightButton.wasPressedThisFrame)
            {
                if (AimedProp != null) _placer.Retrieve(AimedProp);
                else if (AimedWorldItem != null) _placer.Collect(AimedWorldItem);
            }
        }
    }
}
