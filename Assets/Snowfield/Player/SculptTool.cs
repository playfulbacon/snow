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
    /// The player's hands. One persistent state (Hand) plus a Tab accessory overlay:
    ///   LMB — scoop: on a sculpture, bite out a chunk; on bare ground, a handful. Either way that snow is now in
    ///         your hands (mass continuity). While carrying: a tap lets go where the snow already is (fusing into
    ///         snow, falling otherwise), a hold charges a throw.
    ///   Shift+LMB — smooth/pat snow (works with a ball in hand too)
    ///   A carried ball rides the ground in front of you and rolls (grows) while the cursor points near you.
    ///   Scroll — brush radius (accessory selection while the overlay is open) · Tab — accessory overlay
    /// Remesh on a timer while stroking; colliders rebuild on release. The HUD is a separate object reading this.
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
        [Tooltip("Let the brush raise/carve the ground (draw in the snow).")]
        public bool allowGroundSculpting = false;
        [Tooltip("Holding LMB longer than this starts a throw charge; a shorter tap places/drops the carried object.")]
        public float throwTapThreshold = 0.2f;
        [Tooltip("A carried ball rolls on the ground only while the cursor points within this distance of you; otherwise it is held overhead.")]
        public float rollEngageDistance = 2.2f;
        [Header("Brush cursor colours")]
        [Tooltip("Cursor while smoothing (Shift held).")]
        public Color cursorAddColor = new Color(0.4f, 0.7f, 1f, 0.25f);
        [Tooltip("Default cursor: this sphere is the chunk LMB will scoop out.")]
        public Color cursorCarveColor = new Color(1f, 0.35f, 0.3f, 0.3f);

        public ToolMode Mode { get; private set; } = ToolMode.Hand;
        public BrushOp CurrentOp { get; private set; } = BrushOp.None;
        public bool IsSculpting { get; private set; }

        // ---- aim results (refreshed every frame) ----
        public SnowSculpture Target { get; set; }
        public SnowTerrain TargetTerrain { get; private set; }
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

        public SnowballRoller Roller { get; private set; }
        /// <summary>0..1 while charging a throw (LMB held with a carried ball); 0 otherwise. Drives the HUD ring.</summary>
        public float ThrowCharge { get; private set; }
        /// <summary>What LMB would do right now (HUD prompt). Recomputed every frame.</summary>
        public CursorAction PrimaryAction { get; private set; }
        /// <summary>What RMB would do right now (HUD prompt). Recomputed every frame.</summary>
        public CursorAction SecondaryAction { get; private set; }
        /// <summary>Reserved for a third key; unused in the current scheme.</summary>
        public CursorAction TertiaryAction { get; private set; }

        /// <summary>Radius multiplier on top of config, driven by scroll. Session-only.</summary>
        public float radiusScale = 1f;

        AccessoryPlacer _placer;
        float _lmbDownTime = -1f;
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

        void Update()
        {
            if (config == null || viewCamera == null) return;
            var mouse = Mouse.current;
            var kb = Keyboard.current;

            if (kb != null && kb.tabKey.wasPressedThisFrame)
            {
                Mode = Mode == ToolMode.Accessory ? ToolMode.Hand : ToolMode.Accessory;
                _placer.HidePreview();
                HideBrushCursor();
            }
            HandleScroll(mouse);
            Aim();

            if (Mode == ToolMode.Accessory) UpdateAccessory(mouse, kb);
            else UpdateHand(mouse, kb);

            ComputeActions(kb);

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

        void ComputeActions(Keyboard kb)
        {
            CursorAction p = CursorAction.None, s = CursorAction.None;
            bool onSnow = HasHit && Target != null;
            bool shift = kb != null && kb.leftShiftKey.isPressed;

            if (Mode == ToolMode.Accessory)
            {
                if (onSnow) p = CursorAction.PlaceAccessory;
                if (AimedProp != null) s = CursorAction.RetrieveAccessory;
            }
            else if (Roller.IsCarrying)
            {
                if (shift && onSnow && Roller.IsCarryingBall) p = CursorAction.Smooth;
                else p = ThrowCharge > 0f ? CursorAction.Throw
                       : (onSnow && Target != Roller.Carried) ? CursorAction.AttachSnowball
                       : CursorAction.Drop;
            }
            else if (onSnow)
            {
                p = shift ? CursorAction.Smooth : CursorAction.ScoopSnow;
            }
            else if (HasGroundHit && Roller.CanReachGround(GroundPoint) && !shift)
            {
                p = CursorAction.ScoopSnow;
            }
            PrimaryAction = p;
            SecondaryAction = s;
            TertiaryAction = CursorAction.None;
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
            TargetTerrain = null;
            AimedProp = null;
            AimedSnowball = null;
            AimedColliderPath = "";
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

        // ---------- hand: brush, scoop, carry, roll, throw ----------

        void UpdateHand(Mouse mouse, Keyboard kb)
        {
            bool lmb = mouse != null && mouse.leftButton.isPressed;
            bool lmbDown = mouse != null && mouse.leftButton.wasPressedThisFrame;
            bool lmbUp = mouse != null && mouse.leftButton.wasReleasedThisFrame;
            bool shift = kb != null && kb.leftShiftKey.isPressed;
            _placer.HidePreview();

            // --- carrying: LMB tap lets go where the snow is, hold charges a throw; Shift+LMB still smooths ---
            if (Roller.IsCarrying)
            {
                bool letGo = !shift; // Shift keeps the brush live instead of releasing the snow
                if (letGo && lmbDown) _lmbDownTime = Time.time;
                bool charging = letGo && lmb && _lmbDownTime >= 0f && Roller.IsCarryingBall
                                && Time.time - _lmbDownTime > throwTapThreshold;
                ThrowCharge = charging
                    ? Mathf.Min(1f, (Time.time - _lmbDownTime - throwTapThreshold) / Mathf.Max(0.05f, Roller.chargeTime))
                    : 0f;

                bool onSnow = HasHit && Target != null && Target != Roller.Carried;
                bool sculpting = shift && lmb && Roller.IsCarryingBall; // brushing keeps the snow in hand
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
                        Roller.Throw(viewCamera.transform.position, viewCamera.transform.forward,
                            Mathf.Min(1f, (held - throwTapThreshold) / Mathf.Max(0.05f, Roller.chargeTime)));
                    else if (onSnow) Roller.PlaceWhereItIs(Target);   // exactly where it sits
                    else Roller.DropFalling();                        // let go; gravity takes it
                    return;
                }

                // A ball in hand still leaves you free to smooth; a whole sculpture fills both hands.
                if (sculpting) { UpdateBrush(mouse, true); return; }
                HideBrushCursor();
                return;
            }

            ThrowCharge = 0f;
            _lmbDownTime = -1f;

            // --- free hands, LMB: scoop a chunk out of the aimed sculpture, or a handful off the ground ---
            if (lmbDown && !shift)
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

        /// <summary>One discrete bite: remove a sphere of snow from every grid under it and hand the player that chunk.</summary>
        void ScoopChunk(float radius)
        {
            GatherStrokeTargets(Target, false, radius);
            foreach (var t in _strokeTargets)
            {
                if (t is Component c && c == null) continue;
                t.ApplyAdd(BrushPoint, radius, -255f, config.addShoulder); // full-strength one-shot removal
                t.Remesh();
                t.RebuildColliders();
            }
            Roller.GainChunk(BrushPoint, radius);
        }

        /// <summary>The stroke loop: Shift+LMB smooths. (Adding is disabled; LMB scoops instead.) Multi-target.</summary>
        void UpdateBrush(Mouse mouse, bool shift)
        {
            bool lmb = mouse != null && mouse.leftButton.isPressed;

            BrushOp op = lmb && shift ? BrushOp.Smooth : BrushOp.None;
            CurrentOp = op;

            IBrushTarget target = Target;
            if (target == null && allowGroundSculpting && TargetTerrain != null) target = TargetTerrain;

            float radius = CurrentRadius();
            if (cursor != null)
            {
                // Aiming at a prop still shows the cursor so the brush feels continuous over accessories.
                bool show = target != null;
                cursor.gameObject.SetActive(show);
                if (show)
                {
                    cursor.position = BrushPoint;
                    cursor.localScale = Vector3.one * radius * 2f;
                    SetCursorColor(shift ? cursorAddColor : cursorCarveColor);
                }
            }

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
                GatherStrokeTargets(target, allowGroundSculpting, radius);
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

        public float CurrentRadius() => config.addRadius * radiusScale;

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

            _placer.UpdatePreview(HasHit, BrushPoint, BrushNormal);

            if (mouse == null) return;
            if (mouse.leftButton.wasPressedThisFrame && HasHit && Target != null)
                _placer.Place(Target, BrushPoint, BrushNormal);
            if (mouse.rightButton.wasPressedThisFrame && AimedProp != null)
                _placer.Retrieve(AimedProp);
        }
    }
}
