using Snowfield.Config;
using Snowfield.Sculpture;
using Unity.Mathematics;
using UnityEngine;
using UnityEngine.InputSystem;

namespace Snowfield.Player
{
    /// <summary>
    /// The player's hands. Aims a screen-centre ray at sculptures and dispatches to the active <see cref="ToolMode"/>:
    ///   Snow       — LMB add (accumulates), RMB carve, scroll = radius
    ///   Empty Hand — LMB smooth/pat, scroll = radius
    ///   Accessory  — scroll picks, hover ghost, LMB place, RMB remove (via <see cref="AccessoryPlacer"/>)
    /// Left Shift cycles modes; 1/2/3 select. Remesh on a timer while sculpting; colliders rebuild on release.
    /// Lives on the Player (or a child); the HUD is a separate object that reads this component.
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

        public ToolMode Mode { get; private set; } = ToolModeInfo.Default;
        public BrushOp CurrentOp { get; private set; } = BrushOp.None;
        public bool IsSculpting { get; private set; }
        public SnowSculpture Target { get; private set; }
        public SculptureProp AimedProp { get; private set; }
        public float3 BrushPoint { get; private set; }
        public float3 BrushNormal { get; private set; }
        public bool HasHit { get; private set; }
        /// <summary>Aimed at walkable ground (no sculpture) within reach.</summary>
        public bool HasGroundHit { get; private set; }
        public Vector3 GroundPoint { get; private set; }
        public DroppedSnowball AimedSnowball { get; private set; }
        public WorldItem AimedWorldItem { get; private set; }
        public SnowballRoller Roller { get; private set; }
        public AccessoryInventory Inventory => _placer != null ? _placer.Inventory : null;
        [Tooltip("Loose field items are small; aim at them with a sphere cast of this radius (m).")]
        public float itemPickRadius = 0.12f;

        /// <summary>Radius multipliers on top of config, driven by scroll; one per brush mode. Session-only.</summary>
        public float snowRadiusScale = 1f;
        public float handRadiusScale = 1f;
        public float radiusScale
        {
            get => Mode == ToolMode.EmptyHand ? handRadiusScale : snowRadiusScale;
            set { if (Mode == ToolMode.EmptyHand) handRadiusScale = value; else snowRadiusScale = value; }
        }

        AccessoryPlacer _placer;
        float _tickAccumulator;
        float _remeshAccumulator;
        SnowSculpture _dirtySculpture;

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
                case ToolMode.Snow:
                    UpdateBrush(mouse);
                    break;
                case ToolMode.EmptyHand:
                    UpdateEmptyHand(mouse);
                    break;
                case ToolMode.Accessory:
                    UpdateAccessory(mouse);
                    break;
            }

            // timed remesh while a stroke is in progress
            if (_dirtySculpture != null)
            {
                _remeshAccumulator += Time.deltaTime;
                if (_remeshAccumulator >= 1f / config.remeshHz)
                {
                    _remeshAccumulator = 0f;
                    _dirtySculpture.Remesh();
                }
            }
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
            AimedProp = null;
            AimedSnowball = null;
            AimedWorldItem = null;
            var ray = viewCamera.ViewportPointToRay(new Vector3(0.5f, 0.5f, 0f));
            Vector3 origin = reachOrigin != null ? reachOrigin.position + Vector3.up : ray.origin;
            float rayLength = maxReach + Vector3.Distance(ray.origin, origin);

            // Loose items are thin; a fat cast makes them forgiving to aim at.
            if (Physics.SphereCast(ray, itemPickRadius, out var fat, rayLength, sculptMask, QueryTriggerInteraction.Ignore))
            {
                var item = fat.collider.GetComponentInParent<WorldItem>();
                if (item != null && Vector3.Distance(fat.point, origin) <= maxReach) AimedWorldItem = item;
            }

            if (!Physics.Raycast(ray, out var hit, rayLength, sculptMask, QueryTriggerInteraction.Ignore)) return;
            if (Vector3.Distance(hit.point, origin) > maxReach) return;

            AimedSnowball = hit.collider.GetComponentInParent<DroppedSnowball>();
            AimedProp = hit.collider.GetComponentInParent<SculptureProp>();
            var s = hit.collider.GetComponentInParent<SnowSculpture>();
            if (s == null)
            {
                if (AimedSnowball == null && hit.normal.y > 0.6f) { HasGroundHit = true; GroundPoint = hit.point; }
                return;
            }
            Target = s;
            BrushPoint = hit.point;
            BrushNormal = hit.normal;
            HasHit = AimedProp == null; // aiming at a prop is not a snow hit
        }

        // ---------- brush modes ----------

        void UpdateBrush(Mouse mouse, bool allowCarve = true)
        {
            _placer.HidePreview();
            bool lmb = mouse != null && mouse.leftButton.isPressed;
            bool rmb = mouse != null && mouse.rightButton.isPressed && allowCarve;

            BrushOp op = BrushOp.None;
            if (Mode == ToolMode.Snow) op = lmb ? BrushOp.Add : (rmb ? BrushOp.Carve : BrushOp.None);
            else if (Mode == ToolMode.EmptyHand) op = lmb ? BrushOp.Smooth : (rmb ? BrushOp.Carve : BrushOp.None);
            CurrentOp = op;

            float radius = CurrentRadius();
            if (cursor != null)
            {
                // Aiming at a prop still shows the cursor so the brush feels continuous over accessories.
                bool show = Target != null;
                cursor.gameObject.SetActive(show);
                if (show)
                {
                    cursor.position = BrushPoint;
                    cursor.localScale = Vector3.one * radius * 2f;
                }
            }

            bool pressing = op != BrushOp.None;
            if (pressing && Target != null)
            {
                if (!IsSculpting) { IsSculpting = true; _tickAccumulator = 1f / config.ticksPerSecond; } // first tick immediate
                _dirtySculpture = Target;
                _tickAccumulator += Time.deltaTime;
                float tickDt = 1f / config.ticksPerSecond;
                int ticks = 0;
                while (_tickAccumulator >= tickDt && ticks < 8) { _tickAccumulator -= tickDt; ticks++; }
                for (int i = 0; i < ticks; i++) ApplyTick(Target, op, BrushPoint, radius);
            }
            else if (IsSculpting && !pressing)
            {
                IsSculpting = false;
                Flush();
            }
        }

        public float CurrentRadius() =>
            (Mode == ToolMode.EmptyHand ? config.smoothRadius : config.addRadius) * radiusScale;

        public void ApplyTick(SnowSculpture s, BrushOp op, float3 point, float radius)
        {
            switch (op)
            {
                case BrushOp.Add:
                    s.ApplyAdd(point, radius, config.addRatePerTick, config.addShoulder);
                    break;
                case BrushOp.Carve:
                    s.ApplyAdd(point, radius, -config.addRatePerTick, config.addShoulder);
                    break;
                case BrushOp.Smooth:
                    s.ApplySmooth(point, radius, config.smoothStrength, config.smoothShoulder);
                    break;
            }
        }

        /// <summary>Finish a stroke: final remesh + collider cook.</summary>
        public void Flush()
        {
            if (_dirtySculpture == null) return;
            _dirtySculpture.Remesh();
            _dirtySculpture.RebuildColliders();
            _dirtySculpture = null;
            _remeshAccumulator = 0f;
        }

        // ---------- empty hand: smooth, or roll / attach / drop snowballs ----------

        void UpdateEmptyHand(Mouse mouse)
        {
            bool lmb = mouse != null && mouse.leftButton.isPressed;
            bool lmbDown = mouse != null && mouse.leftButton.wasPressedThisFrame;
            bool rmbDown = mouse != null && mouse.rightButton.wasPressedThisFrame;
            _placer.HidePreview();

            // --- pushing: only while the button is held ---
            if (Roller.IsPushing)
            {
                HideBrushCursor();
                if (!lmb) Roller.Release();
                else Roller.UpdatePushing();
                return;
            }

            // --- carrying: preview on snow or ground, LMB to set down / attach ---
            if (Roller.IsCarrying)
            {
                HideBrushCursor();
                bool onSnow = HasHit && Target != null;
                Vector3? preview = onSnow ? Roller.AttachCentre(BrushPoint, BrushNormal)
                                 : HasGroundHit ? Roller.GroundCentre(GroundPoint) : (Vector3?)null;
                Roller.UpdateCarrying(preview);
                if (lmbDown)
                {
                    if (onSnow) Roller.AttachTo(Target, BrushPoint, BrushNormal);
                    else if (HasGroundHit) Roller.PlaceOnGround(GroundPoint);
                }
                return;
            }

            // --- free hands: RMB picks up anything pickable ---
            if (rmbDown)
            {
                if (AimedSnowball != null) Roller.PickUp(AimedSnowball);
                else if (AimedWorldItem != null) _placer.Collect(AimedWorldItem);
                else if (AimedProp != null) _placer.Retrieve(AimedProp);
                return;
            }

            // --- LMB: push a ball, start a ball on bare ground, or smooth snow ---
            if (lmbDown && AimedSnowball != null) { Roller.StartPushing(AimedSnowball); HideBrushCursor(); return; }
            if (lmbDown && !HasHit && HasGroundHit && AimedWorldItem == null) { Roller.StartNew(); HideBrushCursor(); return; }

            UpdateBrush(mouse, allowCarve: false); // smoothing on snow
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
