using System;
using System.Collections.Generic;
using Snowfield.Sculpture;
using UnityEngine;
using UnityEngine.UI;
using UnityEngine.EventSystems;
using UnityEngine.InputSystem.UI;

namespace Snowfield.Player
{
    /// <summary>
    /// uGUI inventory grid, contextual prompts, status line and centre reticle.
    ///
    /// Sits on its own "HUD" GameObject with the Canvas, builds the children itself, and runs in edit mode, so the layout can be tuned in the
    /// Scene/Game view without pressing Play. **This component's inspector is the source of truth**: every size,
    /// spacing and colour lives in the settings groups below and is re-applied to the generated objects each
    /// frame. Hand edits to the generated children are overwritten unless <see cref="applyLayoutFromSettings"/>
    /// is switched off. Use the context menu "Rebuild HUD" to regenerate from scratch.
    /// </summary>
    [ExecuteAlways]
    public class ToolHud : MonoBehaviour
    {
        // ------------------------------------------------------------------ settings

        [Serializable]
        public class AccessoryBarSettings
        {
            public float boxSize = 112f;
            [Range(1, 10)] public int columns = 6;
            public float maxHeight = 650f;
            public float gap = 18f;
            [Tooltip("Minimum margin around the inventory panel.")]
            public float bottomMargin = 40f;
            public float labelHeight = 20f;
            public int labelFontSize = 14;
            public float thumbnailPadding = 6f;
            [Header("Count badge")]
            public float countSize = 22f;
            public int countFontSize = 13;
            public Vector2 countOffset = new Vector2(-4f, -4f);
        }

        [Serializable]
        public class ReticleSettings
        {
            public float arm = 10f;
            public float gap = 5f;
            public float thickness = 2f;
            public float dotSize = 4f;
            [Header("Throw charge ring")]
            public float chargeRadius = 22f;
            public float chargeThickness = 3f;
        }

        /// <summary>
        /// The icon for one action, assignable in the inspector. Keyed by the action's *name* as well as its
        /// value: a serialized enum is an int, so inserting a <see cref="CursorAction"/> value used to slide
        /// every stored row onto the action three places along — which is how the lift prompt ended up reading
        /// "Turn the cut". <see cref="EnsurePromptList"/> re-keys off <see cref="key"/>, so a reorder repairs
        /// itself instead of waiting on someone to bump a version constant.
        ///
        /// There is deliberately no label here. What an action is called lives in <see cref="CursorActionInfo"/>
        /// and nowhere else — a serialized override is a second copy that goes stale on its own (this list still
        /// said "Close accessories" long after the overlay became the inventory).
        /// </summary>
        [Serializable]
        public class ActionArt
        {
            public CursorAction action;
            public Sprite icon;
            [HideInInspector] public string key;
        }

        /// <summary>
        /// How one prompt row is drawn, wherever it is drawn. The reticle stack and the corner hints share these
        /// numbers so a key cap is the same key cap in both places — the previous HUD styled three hand-placed
        /// prompt slots separately, which is how one of them ended up with no key picture at all.
        /// </summary>
        [Serializable]
        public class PromptRowSettings
        {
            public float height = 34f;
            public float padding = 8f;
            public float gap = 6f;
            public float minWidth = 140f;
            public int fontSize = 14;
            [Header("Input chip")]
            [Tooltip("Size of the mouse sprites.")]
            public float inputIconSize = 22f;
            [Tooltip("Smallest key cap; wider keys ('WASD') grow past it.")]
            public float keyCapMinWidth = 30f;
            public float keyCapHeight = 22f;
            public int keyCapFontSize = 12;
            [Header("Action icon")]
            public float iconSize = 26f;
        }

        [Serializable]
        public class CursorSettings
        {
            [Tooltip("Sprite drawn at the screen centre. Leave empty to use the procedural cross.")]
            public Sprite defaultIcon;
            public float iconSize = 28f;
            [Header("Input sprites (a key cap with text is drawn when one is missing)")]
            public Sprite leftMouseIcon;
            public Sprite rightMouseIcon;
            public Sprite scrollIcon;
            [Header("Contextual stack, under the reticle")]
            [Tooltip("Top-centre of the stack, relative to the reticle. It grows downward.")]
            public Vector2 stackOffset = new Vector2(0f, -56f);
            [Tooltip("Most rows the stack will draw at once. Extra actions are dropped rather than wrapping off-screen.")]
            public int maxRows = 5;
            public PromptRowSettings row = new PromptRowSettings();
            [Tooltip("One entry per action. Assign an icon; edit the label if the default wording is off.")]
            public List<ActionArt> prompts = new List<ActionArt>();
        }

        /// <summary>
        /// The persistent corner strip: the handful of actions that are always there (the accessory overlay, the
        /// menu, the mic in a session). They used to live only in the dev status line, which ships switched off —
        /// so the Tab overlay, a whole mode of the game, was invisible unless you already knew about it.
        /// </summary>
        [Serializable]
        public class HintSettings
        {
            public bool show = true;
            public Vector2 margin = new Vector2(24f, 24f);
            public PromptRowSettings row = new PromptRowSettings { minWidth = 150f, height = 30f, fontSize = 13 };
            [Tooltip("Opacity of the strip relative to the contextual prompts — these are reminders, not offers.")]
            [Range(0.1f, 1f)] public float opacity = 0.75f;
        }

        [Serializable]
        public class StatusSettings
        {
            [Tooltip("Show the top-left lines: the mode/aim readout, the control hints and the connection state. " +
                     "Off by default — it is dev chrome, not part of the game's look.")]
            public bool show = false;
            public Vector2 margin = new Vector2(14f, 12f);
            public int fontSize = 16;
            public float lineSpacing = 4f;
            public float width = 1100f;
            [Tooltip("Size of the 'Field N' line under the status text.")]
            public int fieldFontSize = 20;
        }

        [Serializable]
        public class Palette
        {
            public Color boxBg = new Color(0.08f, 0.1f, 0.14f, 0.72f);
            public Color boxText = new Color(0.85f, 0.88f, 0.95f);
            public Color selectedBg = new Color(0.95f, 0.97f, 1f, 0.95f);
            public Color selectedText = new Color(0.1f, 0.14f, 0.22f);
            public Color badgeBg = new Color(0.2f, 0.45f, 0.8f, 0.95f);
            public Color badgeBgIdle = new Color(0.25f, 0.28f, 0.35f, 0.9f);
            public Color badgeText = Color.white;
            public Color outline = new Color(0f, 0f, 0f, 0.35f);
            public Color statusText = new Color(0.1f, 0.1f, 0.15f);
            public Color reticleIdle = new Color(1f, 1f, 1f, 0.6f);
            public Color reticleOnSnow = new Color(0.35f, 0.75f, 1f, 0.95f);
            public Color reticleOnProp = new Color(1f, 0.45f, 0.35f, 0.95f);
            public Color chargeRingBg = new Color(1f, 1f, 1f, 0.18f);
            public Color chargeRingFill = new Color(1f, 0.85f, 0.3f, 0.95f);
            [Tooltip("Ring colour once the throw is fully charged.")]
            public Color chargeRingFull = new Color(1f, 0.35f, 0.25f, 1f);
            public Color promptBg = new Color(0.08f, 0.1f, 0.14f, 0.72f);
            public Color promptText = new Color(0.9f, 0.92f, 0.97f);
            public Color promptIconTint = Color.white;
            [Tooltip("Tint for the mouse sprites on a prompt.")]
            public Color inputIconTint = Color.white;
            [Tooltip("Key cap drawn for any input without a sprite (Tab, Esc, M, Shift...).")]
            public Color keyCapBg = new Color(0.86f, 0.89f, 0.95f, 0.92f);
            public Color keyCapText = new Color(0.09f, 0.12f, 0.18f);
            public Color thumbnailPlaceholder = new Color(0.5f, 0.55f, 0.65f, 0.6f);
            public Color countBg = new Color(0.2f, 0.45f, 0.8f, 0.95f);
            public Color countText = Color.white;
            [Tooltip("Tint applied to an accessory box whose inventory count is zero.")]
            public Color emptyTint = new Color(1f, 1f, 1f, 0.35f);
        }

        [Header("Wiring")]
        public SculptTool tool;
        public AccessoryPlacer placer;
        [Tooltip("Canvas the HUD is built under. Defaults to the Canvas on this object (added if missing).")]
        public Canvas canvas;
        public Vector2 referenceResolution = new Vector2(1920f, 1080f);
        public float outlineWidth = 1f;

        [Header("Layout")]
        [Tooltip("Re-apply sizes/positions/colours from the settings below every frame. Turn off to hand-edit the generated objects.")]
        public bool applyLayoutFromSettings = true;
        public AccessoryBarSettings accessoryBar = new AccessoryBarSettings();
        public ReticleSettings reticle = new ReticleSettings();
        public CursorSettings cursor = new CursorSettings();
        public HintSettings hints = new HintSettings();
        public StatusSettings status = new StatusSettings();
        public Palette colors = new Palette();

        [Serializable]
        public class PreviewPrompt
        {
            public CursorAction action = CursorAction.ScoopSnow;
            public InputGlyph input = InputGlyph.LeftMouse;
            public bool hold;
        }

        [Header("Edit-mode preview")]
        public ToolMode previewMode = ToolMode.Hand;
        public bool previewAccessoryBar = true;
        public int previewAccessoryIndex = 0;
        public bool previewChargeRing = false;
        [Tooltip("Rows the stack shows with the game stopped, so the layout can be tuned without pressing Play.")]
        public List<PreviewPrompt> previewActions = new List<PreviewPrompt>
        {
            new PreviewPrompt { action = CursorAction.ScoopSnow, input = InputGlyph.LeftMouse },
            new PreviewPrompt { action = CursorAction.Smooth, input = InputGlyph.RightMouse },
            new PreviewPrompt { action = CursorAction.BrushRadius, input = InputGlyph.Scroll },
        };

        // ------------------------------------------------------------------ generated refs

        [Serializable]
        class AccessoryItem { public RectTransform root; public Image box, boxOutline, countBox, countOutline; public RawImage thumb; public Text label, count; public Button button; public int slot; }

        [SerializeField, HideInInspector] RectTransform _accessoryBarRoot, _reticleRoot, _statusRoot, _stackRoot, _hintRoot;
        [SerializeField, HideInInspector] ScrollRect _inventoryScroll;
        [SerializeField, HideInInspector] GridLayoutGroup _inventoryGrid;
        [SerializeField, HideInInspector] Text _inventoryTitle, _inventoryHint;
        bool _inventoryWasVisible;
        [SerializeField, HideInInspector] List<AccessoryItem> _accessories = new List<AccessoryItem>();
        [SerializeField, HideInInspector] Image[] _reticleBars = new Image[4];
        [SerializeField, HideInInspector] Image[] _reticleShadows = new Image[4];
        [SerializeField, HideInInspector] Image _reticleDot, _reticleDotShadow;
        [SerializeField, HideInInspector] Image _chargeBg, _chargeFill, _cursorIcon;

        /// <summary>
        /// One prompt row: a box, the input chip (a mouse sprite or a key cap, plus an optional modifier cap),
        /// the action's icon and its label. Both the reticle stack and the corner hints are pools of these.
        /// </summary>
        [Serializable]
        class Prompt
        {
            public RectTransform root;
            public Image bg, outline, input, icon, capBg, capOutline, modBg, modOutline;
            public Text cap, mod, label;
        }

        [SerializeField, HideInInspector] List<Prompt> _rows = new List<Prompt>();
        [SerializeField, HideInInspector] List<Prompt> _hintRows = new List<Prompt>();
        /// <summary>Rebuilt each frame from the tool, then rendered. Field rather than local: no per-frame garbage.</summary>
        readonly List<ActionPrompt> _shown = new List<ActionPrompt>();
        readonly List<ActionPrompt> _persistent = new List<ActionPrompt>();
        static Sprite _ringSprite;
        [SerializeField, HideInInspector] Text _statusLine1, _statusLine2, _fieldLine;

        Font _font;
        bool _pendingBuild;

        // ------------------------------------------------------------------ lifecycle

        void Awake() => ResolveRefs();

        void ResolveRefs()
        {
            if (tool == null) tool = FindAnyObjectByType<SculptTool>();
            if (placer == null && tool != null) placer = tool.GetComponent<AccessoryPlacer>();
        }

        void OnEnable()
        {
            // Runtime listeners are not serialized with the edit-mode preview's buttons.
            if (Application.isPlaying || !IsBuilt()) _pendingBuild = true;
#if UNITY_EDITOR
            if (!Application.isPlaying) UnityEditor.EditorApplication.delayCall += EditorTick;
#endif
        }

#if UNITY_EDITOR
        void EditorTick()
        {
            if (this == null) return;
            if (_pendingBuild) Build();
            ApplyAll();
        }

        void OnValidate()
        {
            EnsurePromptList();
            if (!Application.isPlaying) UnityEditor.EditorApplication.delayCall += EditorTick;
        }
#endif

        void Update()
        {
            if (_pendingBuild) Build();
            if (Application.isPlaying && tool == null) ResolveRefs();
            ApplyAll();
        }

        [ContextMenu("Rebuild HUD")]
        public void RebuildNow()
        {
            _accessories.Clear();
            _rows.Clear();
            _hintRows.Clear();
            Build();
            ApplyAll();
        }

        /// <summary>How many hint rows the strip can ever need: Tab, the menu, and the mic in a session.</summary>
        const int HintCapacity = 3;

        /// <summary>
        /// Bumped whenever the generated HUD changes shape. The checks below catch a HUD that lost an object,
        /// but not one built by an older version of this file whose object graph happens to still line up —
        /// a serialized scene keeps that one for ever otherwise.
        /// </summary>
        const int HudSchemaVersion = 3;
        [SerializeField, HideInInspector] int _hudSchemaVersion;

        bool IsBuilt() =>
            _hudSchemaVersion == HudSchemaVersion
            && canvas != null && _accessoryBarRoot != null && _reticleRoot != null && _statusRoot != null
            && _stackRoot != null && _hintRoot != null
            && _inventoryScroll != null && _inventoryGrid != null && _inventoryTitle != null && _inventoryHint != null
            && _rows.Count == Mathf.Max(1, cursor.maxRows) && _hintRows.Count == HintCapacity
            && _statusLine1 != null && _reticleDot != null && _chargeFill != null
            && _cursorIcon != null && _fieldLine != null
            && Intact(_rows) && Intact(_hintRows)
            && (_accessories.Count == 0 || _accessories[0].count != null);

        static bool Intact(List<Prompt> pool)
        {
            foreach (var p in pool)
                if (p == null || p.root == null || p.input == null || p.cap == null || p.mod == null) return false;
            return true;
        }

        // ------------------------------------------------------------------ build

        void Build()
        {
            _pendingBuild = false;
            _hudSchemaVersion = HudSchemaVersion;
            _font = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");

            if (canvas == null) canvas = GetComponent<Canvas>();
            if (canvas == null) canvas = gameObject.AddComponent<Canvas>();
            canvas.renderMode = RenderMode.ScreenSpaceOverlay;
            var scaler = canvas.GetComponent<CanvasScaler>();
            if (scaler == null) scaler = canvas.gameObject.AddComponent<CanvasScaler>();
            scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
            scaler.referenceResolution = referenceResolution;
            scaler.matchWidthOrHeight = 0.5f;
            if (canvas.GetComponent<GraphicRaycaster>() == null) canvas.gameObject.AddComponent<GraphicRaycaster>();
            if (Application.isPlaying && FindAnyObjectByType<EventSystem>() == null)
                new GameObject("InventoryEventSystem", typeof(EventSystem), typeof(InputSystemUIInputModule));

            // Wipe previously generated children and rebuild deterministically.
            for (int i = canvas.transform.childCount - 1; i >= 0; i--)
            {
                // Deactivated first: Destroy is deferred to the end of the frame, so a child left enabled draws
                // once more over the HUD that replaced it.
                var child = canvas.transform.GetChild(i).gameObject;
                child.SetActive(false);
                Kill(child);
            }
            _accessories.Clear();
            _rows.Clear();
            _hintRows.Clear();

            var root = canvas.transform;

            // --- status (top-left) ---
            _statusRoot = MakeRect("Status", root, new Vector2(0, 1), new Vector2(0, 1), new Vector2(0, 1));
            _statusLine1 = Txt("Line1", _statusRoot, TextAnchor.UpperLeft);
            _statusLine2 = Txt("Line2", _statusRoot, TextAnchor.UpperLeft);
            _fieldLine = Txt("Field", _statusRoot, TextAnchor.UpperLeft);
            _fieldLine.fontStyle = FontStyle.Bold;

            // --- inventory panel with a clipped, vertically scrollable grid ---
            _accessoryBarRoot = MakeRect("Inventory", root, Vector2.one * 0.5f, Vector2.one * 0.5f, Vector2.one * 0.5f);
            var background = _accessoryBarRoot.gameObject.AddComponent<Image>();
            background.color = new Color(0.045f, 0.065f, 0.10f, 0.97f);
            _inventoryTitle = Txt("Title", _accessoryBarRoot, TextAnchor.MiddleLeft);
            _inventoryTitle.text = "Inventory";
            _inventoryTitle.fontSize = 26;
            _inventoryTitle.fontStyle = FontStyle.Bold;
            _inventoryHint = Txt("Hint", _accessoryBarRoot, TextAnchor.MiddleLeft);
            _inventoryHint.text = "Click to equip · Tab to close · Scroll to browse";
            _inventoryHint.fontSize = 15;
            var viewport = MakeRect("Viewport", _accessoryBarRoot, Vector2.zero, Vector2.one, new Vector2(0, 1));
            viewport.offsetMin = new Vector2(24, 24);
            viewport.offsetMax = new Vector2(-24, -100);
            viewport.gameObject.AddComponent<RectMask2D>();
            var content = MakeRect("Items", viewport, new Vector2(0, 1), Vector2.one, new Vector2(0, 1));
            _inventoryGrid = content.gameObject.AddComponent<GridLayoutGroup>();
            _inventoryGrid.constraint = GridLayoutGroup.Constraint.FixedColumnCount;
            var fitter = content.gameObject.AddComponent<ContentSizeFitter>();
            fitter.verticalFit = ContentSizeFitter.FitMode.PreferredSize;
            _inventoryScroll = _accessoryBarRoot.gameObject.AddComponent<ScrollRect>();
            _inventoryScroll.viewport = viewport;
            _inventoryScroll.content = content;
            _inventoryScroll.horizontal = false;
            _inventoryScroll.movementType = ScrollRect.MovementType.Clamped;
            _inventoryScroll.scrollSensitivity = 36f;
            EnsureInventoryCards(AccessoryCatalog.Entries.Count);

            // --- reticle (centre) ---
            _reticleRoot = MakeRect("Reticle", root, new Vector2(0.5f, 0.5f), new Vector2(0.5f, 0.5f), new Vector2(0.5f, 0.5f));
            string[] names = { "Left", "Right", "Up", "Down" };
            for (int i = 0; i < 4; i++)
            {
                _reticleShadows[i] = Img(names[i] + "Shadow", _reticleRoot);
                _reticleBars[i] = Img(names[i], _reticleRoot);
            }
            _reticleDotShadow = Img("DotShadow", _reticleRoot);
            _reticleDot = Img("Dot", _reticleRoot);
            _chargeBg = Img("ChargeRingBg", _reticleRoot);
            _chargeFill = Img("ChargeRingFill", _reticleRoot);
            _chargeFill.type = Image.Type.Filled;
            _chargeFill.fillMethod = Image.FillMethod.Radial360;
            _chargeFill.fillOrigin = (int)Image.Origin360.Top;
            _chargeFill.fillClockwise = true;
            _cursorIcon = Img("CursorIcon", _reticleRoot);
            _cursorIcon.preserveAspect = true;

            // --- contextual prompt stack (under the reticle, grows down) ---
            _stackRoot = MakeRect("ActionStack", _reticleRoot, new Vector2(0.5f, 0.5f), new Vector2(0.5f, 0.5f), new Vector2(0.5f, 1f));
            for (int i = 0; i < Mathf.Max(1, cursor.maxRows); i++) _rows.Add(BuildPrompt("Row" + i, _stackRoot));

            // --- persistent hints (bottom-right, grows up) ---
            _hintRoot = MakeRect("Hints", root, new Vector2(1, 0), new Vector2(1, 0), new Vector2(1, 0));
            for (int i = 0; i < HintCapacity; i++) _hintRows.Add(BuildPrompt("Hint" + i, _hintRoot));

            EnsurePromptList();
        }

        // ------------------------------------------------------------------ apply

        void ApplyAll()
        {
            if (!IsBuilt()) return;
            if (applyLayoutFromSettings) ApplyLayout();
            ApplyState();
        }

        void ApplyLayout()
        {
            var c = colors;
            float ow = outlineWidth;

            // status
            _statusRoot.anchoredPosition = new Vector2(status.margin.x, -status.margin.y);
            float lineH = status.fontSize + 6f;
            _statusRoot.sizeDelta = new Vector2(status.width, lineH * 2 + status.lineSpacing);
            Place(_statusLine1.rectTransform, new Vector2(0, 1), new Vector2(0, 0), new Vector2(status.width, lineH));
            Place(_statusLine2.rectTransform, new Vector2(0, 1), new Vector2(0, -(lineH + status.lineSpacing)), new Vector2(status.width, lineH));
            _statusLine1.fontSize = _statusLine2.fontSize = status.fontSize;
            _statusLine1.color = _statusLine2.color = c.statusText;
            float fieldH = status.fieldFontSize + 6f;
            Place(_fieldLine.rectTransform, new Vector2(0, 1), new Vector2(0, -(lineH * 2 + status.lineSpacing * 2 + 2f)), new Vector2(status.width, fieldH));
            _fieldLine.fontSize = status.fieldFontSize;
            _fieldLine.color = c.statusText;

            LayoutInventory();

            // reticle
            float arm = reticle.arm, gap = reticle.gap, th = reticle.thickness;
            _reticleRoot.sizeDelta = Vector2.one * ((arm + gap) * 2);
            Vector2[] pos = { new Vector2(-(gap + arm * 0.5f), 0), new Vector2(gap + arm * 0.5f, 0), new Vector2(0, gap + arm * 0.5f), new Vector2(0, -(gap + arm * 0.5f)) };
            Vector2[] size = { new Vector2(arm, th), new Vector2(arm, th), new Vector2(th, arm), new Vector2(th, arm) };
            for (int i = 0; i < 4; i++)
            {
                Centre(_reticleBars[i].rectTransform, pos[i], size[i]);
                Centre(_reticleShadows[i].rectTransform, pos[i], size[i] + Vector2.one * (ow * 2));
            }
            Centre(_reticleDot.rectTransform, Vector2.zero, Vector2.one * reticle.dotSize);
            Centre(_reticleDotShadow.rectTransform, Vector2.zero, Vector2.one * (reticle.dotSize + ow * 2));
            Centre(_chargeBg.rectTransform, Vector2.zero, Vector2.one * reticle.chargeRadius * 2f);
            Centre(_chargeFill.rectTransform, Vector2.zero, Vector2.one * reticle.chargeRadius * 2f);
            Centre(_cursorIcon.rectTransform, Vector2.zero, Vector2.one * cursor.iconSize);
            _stackRoot.anchoredPosition = cursor.stackOffset;
            _hintRoot.anchoredPosition = new Vector2(-hints.margin.x, hints.margin.y);
            _hintRoot.sizeDelta = Vector2.zero;
            foreach (var p in _rows) StylePrompt(p, cursor.row, 1f);
            foreach (var p in _hintRows) StylePrompt(p, hints.row, hints.opacity);
            var ring = RingSprite();
            _chargeBg.sprite = ring; _chargeFill.sprite = ring;
            _chargeBg.color = c.chargeRingBg; _chargeFill.color = c.chargeRingFill;
        }

        /// <summary>An outline/shadow colour that fades with the element it frames, so alpha 0 hides the whole thing.</summary>
        static Color Frame(Color outline, Color body) => new Color(outline.r, outline.g, outline.b, outline.a * body.a);

        void ApplyState()
        {
            bool playing = Application.isPlaying && tool != null;
            ToolMode mode = playing ? tool.Mode : previewMode;
            bool accVisible = playing ? tool.InventoryOpen : previewAccessoryBar;
            int accIndex = playing && placer != null ? placer.SelectedIndex : previewAccessoryIndex;
            var c = colors;

            _accessoryBarRoot.gameObject.SetActive(accVisible);
            _reticleRoot.gameObject.SetActive(!accVisible);
            if (accVisible)
            {
                var inv = playing && placer != null ? placer.Inventory : null;
                int occupied = 0;
                if (inv != null)
                {
                    for (int slot = 0; slot < inv.SlotCount; slot++)
                        if (inv.CountAt(slot) > 0) occupied++;
                }
                else occupied = playing ? 0 : AccessoryCatalog.Entries.Count;
                EnsureInventoryCards(occupied);
                int card = 0;
                for (int slot = 0; slot < (inv != null ? inv.SlotCount : occupied); slot++)
                {
                    var entry = inv != null ? inv.ItemAt(slot) : AccessoryCatalog.Entries[slot];
                    if (entry == null) continue;
                    DrawInventoryCard(_accessories[card++], entry, slot, inv != null ? inv.CountAt(slot) : 0,
                        mode == ToolMode.Accessory && slot == accIndex);
                }
                for (int i = card; i < _accessories.Count; i++) _accessories[i].root.gameObject.SetActive(false);
                LayoutInventory();
                if (!_inventoryWasVisible) _inventoryScroll.verticalNormalizedPosition = 1f;
            }
            _inventoryWasVisible = accVisible;

            // reticle colour
            Color rc = c.reticleIdle;
            if (playing)
            {
                if (mode == ToolMode.Accessory && tool.AimedProp != null) rc = c.reticleOnProp;
                else if (tool.HasHit) rc = c.reticleOnSnow;
            }
            Color shadow = new Color(0, 0, 0, rc.a * 0.5f);
            for (int i = 0; i < 4; i++) { _reticleBars[i].color = rc; _reticleShadows[i].color = shadow; }
            _reticleDot.color = rc; _reticleDotShadow.color = shadow;

            // centre cursor: sprite if assigned, else the procedural cross
            bool useIcon = cursor.defaultIcon != null;
            _cursorIcon.gameObject.SetActive(useIcon);
            if (useIcon) { _cursorIcon.sprite = cursor.defaultIcon; _cursorIcon.color = rc; }
            for (int i = 0; i < 4; i++) { _reticleBars[i].gameObject.SetActive(!useIcon); _reticleShadows[i].gameObject.SetActive(!useIcon); }
            _reticleDot.gameObject.SetActive(!useIcon); _reticleDotShadow.gameObject.SetActive(!useIcon);

            // --- action prompts: one stack of rows, one row per thing you can do right now ---
            _shown.Clear();
            if (playing)
            {
                var offered = tool.Actions;
                for (int i = 0; i < offered.Count; i++) _shown.Add(offered[i]);
            }
            else if (previewActions != null)
            {
                foreach (var p in previewActions)
                    if (p != null) _shown.Add(new ActionPrompt(p.action, p.input, p.hold));
            }
            DrawStack(_rows, _shown, cursor.row, 1f, downward: true);

            // --- persistent hints: the actions that are always there, so they are never a secret ---
            // With the cursor free (menu open) none of them are live, and the rule here is that a prompt on
            // screen is an action you can take — so the strip goes away rather than promising a dead key.
            _persistent.Clear();
            if (hints.show && (!playing || tool.InventoryOpen || Cursor.lockState == CursorLockMode.Locked))
            {
                _persistent.Add(new ActionPrompt(ToolModeInfo.TabAction(playing ? tool.InventoryOpen : previewAccessoryBar, mode), InputGlyph.Tab));
                _persistent.Add(new ActionPrompt(CursorAction.OpenMenu, InputGlyph.Escape));
                if (!playing || NetStatus.InSession)
                    _persistent.Add(new ActionPrompt(NetStatus.MicMuted ? CursorAction.UnmuteMic : CursorAction.MuteMic,
                                                     InputGlyph.Mute));
            }
            DrawStack(_hintRows, _persistent, hints.row, hints.opacity, downward: false);

            // throw charge ring
            float charge = playing ? tool.ThrowCharge : 0f;
            bool showRing = charge > 0f || (!playing && previewChargeRing);
            _chargeBg.gameObject.SetActive(showRing);
            _chargeFill.gameObject.SetActive(showRing);
            _chargeFill.fillAmount = playing ? charge : 0.65f;
            _chargeFill.color = charge >= 0.999f ? c.chargeRingFull : c.chargeRingFill;

            // status text (hidden unless status.show — see StatusSettings)
            _statusRoot.gameObject.SetActive(status.show);
            if (!status.show) return;

            string line = "";
            if (playing && tool.config != null)
            {
                line = mode == ToolMode.Accessory
                    ? (placer == null ? ""
                       : placer.ActiveChain != null
                         ? $"{placer.Selected.DisplayName} — {placer.ActiveChain.PointCount} point{(placer.ActiveChain.PointCount == 1 ? "" : "s")}"
                         : placer.Selected != null ? placer.Selected.DisplayName : "Empty hands")
                    : tool.Roller != null && tool.Roller.IsEngaged
                        ? (tool.Roller.Ball != null ? $"snowball {tool.Roller.Radius * 2f:0.00} m" : "carrying sculpture")
                        : $"radius {tool.CurrentRadius():0.00} m   rate {tool.config.addRatePerTick:0}/tick @ {tool.config.ticksPerSecond:0} Hz";
            }
            if (playing)
            {
                string aim = tool.AimedSnowball != null ? "loose snowball"
                           : tool.Target != null ? (tool.Target.GetComponent<Snowball>() != null ? "fixed snowball" : "sculpture")
                           : tool.HasGroundHit ? "ground" : "nothing";
                line += $"   ·   aim: {aim}{(tool.AimedProp != null ? " (prop)" : "")} [{tool.AimedColliderPath}]";
            }
            _statusLine1.text = $"[{ToolModeInfo.DisplayName(mode)}]  {line}";
            _fieldLine.text = NetStatus.Line; // written by the net layer; empty in single-player
            // Movement only. The sculpting controls are the prompt stack, and the second hand-written copy of
            // them that used to live on this line is what drifted away from the code.
            _statusLine2.text = Controls.MovementLine;
        }

        void EnsureInventoryCards(int count)
        {
            while (_accessories.Count < count)
            {
                var item = new AccessoryItem();
                item.root = MakeRect("Item", _inventoryScroll.content, Vector2.zero, Vector2.zero, new Vector2(0, 1));
                item.boxOutline = Img("Outline", item.root);
                item.box = Img("Box", item.root);
                item.box.raycastTarget = true;
                item.button = item.root.gameObject.AddComponent<Button>();
                item.button.targetGraphic = item.box;
                item.button.navigation = new Navigation { mode = Navigation.Mode.None };
                item.button.onClick.AddListener(() => { if (tool != null) tool.EquipInventorySlot(item.slot); });
                var thumbGo = new GameObject("Thumbnail", typeof(RectTransform), typeof(RawImage));
                thumbGo.transform.SetParent(item.root, false);
                item.thumb = thumbGo.GetComponent<RawImage>();
                item.thumb.raycastTarget = false;
                item.label = Txt("Label", item.root, TextAnchor.MiddleCenter);
                item.label.horizontalOverflow = HorizontalWrapMode.Wrap;
                item.label.verticalOverflow = VerticalWrapMode.Truncate;
                item.label.resizeTextForBestFit = true;
                item.label.resizeTextMinSize = 10;
                var countRoot = MakeRect("Count", item.root, Vector2.one, Vector2.one, Vector2.one);
                item.countOutline = Img("Outline", countRoot);
                item.countBox = Img("Box", countRoot);
                item.count = Txt("Label", countRoot, TextAnchor.MiddleCenter);
                item.count.fontStyle = FontStyle.Bold;
                _accessories.Add(item);
            }
        }

        void LayoutInventory()
        {
            var area = ((RectTransform)canvas.transform).rect.size;
            float bs = Mathf.Max(64f, accessoryBar.boxSize), gp = Mathf.Max(0f, accessoryBar.gap);
            float available = Mathf.Max(bs, area.x - 2f * Mathf.Max(16f, accessoryBar.bottomMargin) - 48f);
            int columns = Mathf.Clamp(Mathf.FloorToInt((available + gp) / (bs + gp)), 1, Mathf.Max(1, accessoryBar.columns));
            float height = Mathf.Min(Mathf.Max(240f, accessoryBar.maxHeight), Mathf.Max(240f, area.y - 80f));
            _accessoryBarRoot.anchoredPosition = Vector2.zero;
            _accessoryBarRoot.sizeDelta = new Vector2(columns * (bs + gp) - gp + 48f, height);
            _inventoryGrid.constraintCount = columns;
            _inventoryGrid.cellSize = Vector2.one * bs;
            _inventoryGrid.spacing = Vector2.one * gp;
            Place(_inventoryTitle.rectTransform, new Vector2(0, 1), new Vector2(24, -16), new Vector2(600, 36));
            Place(_inventoryHint.rectTransform, new Vector2(0, 1), new Vector2(24, -56),
                new Vector2(_accessoryBarRoot.sizeDelta.x - 48f, 36));
            _inventoryTitle.color = colors.boxText;
            _inventoryHint.color = colors.boxText;
            _inventoryHint.horizontalOverflow = HorizontalWrapMode.Wrap;
            foreach (var a in _accessories)
            {
                Stretch(a.boxOutline.rectTransform, -outlineWidth);
                Stretch(a.box.rectTransform, 0);
                float padding = accessoryBar.thumbnailPadding;
                var tr = a.thumb.rectTransform;
                tr.anchorMin = Vector2.zero; tr.anchorMax = Vector2.one;
                tr.offsetMin = Vector2.one * padding;
                tr.offsetMax = Vector2.one * -padding;
                var lr = a.label.rectTransform;
                lr.anchorMin = Vector2.zero; lr.anchorMax = new Vector2(1, 0); lr.pivot = new Vector2(0.5f, 0);
                lr.anchoredPosition = new Vector2(0, 4);
                lr.sizeDelta = new Vector2(-8, accessoryBar.labelHeight + 6f);
                a.label.fontSize = accessoryBar.labelFontSize;
                a.label.resizeTextMaxSize = accessoryBar.labelFontSize;
                var cr = (RectTransform)a.countBox.transform.parent;
                cr.anchoredPosition = accessoryBar.countOffset;
                cr.sizeDelta = Vector2.one * accessoryBar.countSize;
                Stretch(a.countOutline.rectTransform, -outlineWidth);
                Stretch(a.countBox.rectTransform, 0);
                Stretch(a.count.rectTransform, 0);
                a.count.fontSize = accessoryBar.countFontSize;
            }
        }

        void DrawInventoryCard(AccessoryItem a, AccessoryCatalog.Entry entry, int slot, int quantity, bool selected)
        {
            a.root.gameObject.SetActive(true);
            a.slot = slot;
            a.root.name = entry.DisplayName;
            a.box.color = selected ? colors.selectedBg : colors.boxBg;
            a.label.color = selected ? colors.selectedText : colors.boxText;
            a.label.gameObject.SetActive(false);
            a.label.text = "";
            var tex = entry != null && placer != null ? placer.ThumbnailFor(entry.Id) : null;
            a.thumb.texture = tex;
            a.thumb.gameObject.SetActive(tex != null);
            a.thumb.color = Color.white;
            a.countBox.color = colors.countBg;
            a.count.color = colors.countText;
            a.count.text = quantity.ToString();
            a.countBox.transform.parent.gameObject.SetActive(quantity > 0);
            a.boxOutline.color = Frame(colors.outline, a.box.color);
            a.countOutline.color = Frame(colors.outline, a.countBox.color);
        }

        // ------------------------------------------------------------------ helpers

        static RectTransform MakeRect(string name, Transform parent, Vector2 anchorMin, Vector2 anchorMax, Vector2 pivot)
        {
            var go = new GameObject(name, typeof(RectTransform));
            go.transform.SetParent(parent, false);
            var rt = go.GetComponent<RectTransform>();
            rt.anchorMin = anchorMin; rt.anchorMax = anchorMax; rt.pivot = pivot;
            return rt;
        }

        static Image Img(string name, Transform parent)
        {
            var go = new GameObject(name, typeof(RectTransform), typeof(Image));
            go.transform.SetParent(parent, false);
            var img = go.GetComponent<Image>();
            img.raycastTarget = false;
            return img;
        }

        Text Txt(string name, Transform parent, TextAnchor anchor)
        {
            if (_font == null) _font = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");
            var go = new GameObject(name, typeof(RectTransform), typeof(Text));
            go.transform.SetParent(parent, false);
            var t = go.GetComponent<Text>();
            t.font = _font;
            t.alignment = anchor;
            t.raycastTarget = false;
            t.horizontalOverflow = HorizontalWrapMode.Overflow;
            t.verticalOverflow = VerticalWrapMode.Overflow;
            return t;
        }

        /// <summary>Fill the parent, inset by <paramref name="inset"/> (negative = outset).</summary>
        static void Stretch(RectTransform rt, float inset)
        {
            rt.anchorMin = Vector2.zero; rt.anchorMax = Vector2.one;
            rt.offsetMin = new Vector2(inset, inset); rt.offsetMax = new Vector2(-inset, -inset);
        }

        static void Place(RectTransform rt, Vector2 anchor, Vector2 pos, Vector2 size)
        {
            rt.anchorMin = rt.anchorMax = anchor; rt.pivot = anchor;
            rt.anchoredPosition = pos; rt.sizeDelta = size;
        }

        static void Centre(RectTransform rt, Vector2 pos, Vector2 size)
        {
            rt.anchorMin = rt.anchorMax = rt.pivot = new Vector2(0.5f, 0.5f);
            rt.anchoredPosition = pos; rt.sizeDelta = size;
        }

        Prompt BuildPrompt(string name, Transform parent)
        {
            var p = new Prompt();
            p.root = MakeRect(name, parent, new Vector2(0.5f, 1f), new Vector2(0.5f, 1f), new Vector2(0.5f, 1f));
            p.outline = Img("Outline", p.root);
            p.bg = Img("Box", p.root);
            var mod = MakeRect("Modifier", p.root, new Vector2(0, 0.5f), new Vector2(0, 0.5f), new Vector2(0, 0.5f));
            p.modOutline = Img("Outline", mod);
            p.modBg = Img("Cap", mod);
            p.mod = Txt("Text", mod, TextAnchor.MiddleCenter);
            var cap = MakeRect("KeyCap", p.root, new Vector2(0, 0.5f), new Vector2(0, 0.5f), new Vector2(0, 0.5f));
            p.capOutline = Img("Outline", cap);
            p.capBg = Img("Cap", cap);
            p.cap = Txt("Text", cap, TextAnchor.MiddleCenter);
            p.input = Img("Input", p.root);
            p.input.preserveAspect = true;
            p.icon = Img("Icon", p.root);
            p.icon.preserveAspect = true;
            p.label = Txt("Label", p.root, TextAnchor.MiddleLeft);
            return p;
        }

        /// <summary>The part of a row that never depends on what is in it. Size and position come later.</summary>
        void StylePrompt(Prompt p, PromptRowSettings s, float opacity)
        {
            Stretch(p.outline.rectTransform, -outlineWidth);
            Stretch(p.bg.rectTransform, 0);
            var inr = p.input.rectTransform;
            inr.anchorMin = inr.anchorMax = new Vector2(0, 0.5f); inr.pivot = new Vector2(0, 0.5f);
            inr.sizeDelta = Vector2.one * s.inputIconSize;
            var ir = p.icon.rectTransform;
            ir.anchorMin = ir.anchorMax = new Vector2(0, 0.5f); ir.pivot = new Vector2(0, 0.5f);
            ir.sizeDelta = Vector2.one * s.iconSize;
            var lr = p.label.rectTransform;
            lr.anchorMin = new Vector2(0, 0); lr.anchorMax = new Vector2(1, 1); lr.pivot = new Vector2(0.5f, 0.5f);
            lr.offsetMax = new Vector2(-s.padding, 0);
            p.label.fontSize = s.fontSize;

            var bg = Fade(colors.promptBg, opacity);
            p.bg.color = bg;
            p.outline.color = Frame(colors.outline, bg);
            p.label.color = Fade(colors.promptText, opacity);
            var cap = Fade(colors.keyCapBg, opacity);
            StyleCap(p.capBg, p.capOutline, p.cap, cap, s, opacity);
            StyleCap(p.modBg, p.modOutline, p.mod, cap, s, opacity);
        }

        void StyleCap(Image box, Image outline, Text text, Color cap, PromptRowSettings s, float opacity)
        {
            box.color = cap;
            Stretch(box.rectTransform, 0);
            outline.color = Frame(colors.outline, cap);
            Stretch(outline.rectTransform, -outlineWidth);
            text.color = Fade(colors.keyCapText, opacity);
            text.fontSize = s.keyCapFontSize;
            text.fontStyle = FontStyle.Bold;
            Stretch(text.rectTransform, 0);
        }

        static Color Fade(Color c, float a) => new Color(c.r, c.g, c.b, c.a * a);

        /// <summary>
        /// Draw a list of prompts into a pool of rows: fill the ones that are needed, hide the rest, give them all
        /// one width so the key chips line up into a column, and stack them.
        /// </summary>
        void DrawStack(List<Prompt> pool, List<ActionPrompt> items, PromptRowSettings s, float opacity, bool downward)
        {
            int row = 0;
            float width = s.minWidth;
            for (int i = 0; i < items.Count && row < pool.Count; i++)
            {
                if (!items[i].IsSomething) continue;   // never costs a row: a blank gap would read as a lost prompt
                pool[row].root.gameObject.SetActive(true);
                width = Mathf.Max(width, ApplyPrompt(pool[row], items[i], s, opacity));
                row++;
            }
            int filled = row;
            for (; row < pool.Count; row++) pool[row].root.gameObject.SetActive(false);

            // Second pass: every visible row takes the widest row's width, so the chips share a left edge.
            for (int i = 0; i < filled; i++)
            {
                float y = (s.height + s.gap) * i;
                pool[i].root.pivot = downward ? new Vector2(0.5f, 1f) : new Vector2(1f, 0f);
                pool[i].root.anchoredPosition = new Vector2(0f, downward ? -y : y);
                pool[i].root.sizeDelta = new Vector2(width, s.height);
            }
        }

        /// <summary>Fill one row and report the width its content wants.</summary>
        float ApplyPrompt(Prompt p, ActionPrompt item, PromptRowSettings s, float opacity)
        {
            var entry = FindPrompt(item.Action);
            string label = CursorActionInfo.DefaultLabel(item.Action);
            // The one row whose text is worth building per frame: "Pick up Pebble x3" beats "Pick up item",
            // and the count is what the pickup will actually give you, not what is lying there.
            if (item.Action == CursorAction.CollectItem && tool != null && tool.AimedPickup != null
                && placer != null && placer.Inventory != null)
            {
                var pickup = tool.AimedPickup;
                int amount = Mathf.Min(pickup.quantity, placer.Inventory.SpaceFor(pickup.itemId));
                label = $"Pick up {pickup.Item?.DisplayName} ×{amount}";
            }
            var icon = entry != null ? entry.icon : null;

            // A hold reads as a modifier on the input, not as words inside the label: "Hold [LMB] Throw".
            string modifier = InputGlyphInfo.Modifier(item.Input);
            if (item.Hold) modifier = modifier.Length == 0 ? "Hold" : modifier + " + hold";

            // Row: [modifier cap] [mouse sprite | key cap] [action icon] [label], each part skipped when absent.
            float pad = s.padding;
            float x = pad;
            x += Cap(p.mod, p.modBg, modifier, s, x);
            var sprite = InputGlyphInfo.Art(item.Input) switch
            {
                GlyphArt.LeftMouse => cursor.leftMouseIcon,
                GlyphArt.RightMouse => cursor.rightMouseIcon,
                GlyphArt.Scroll => cursor.scrollIcon,
                _ => null,
            };
            p.input.gameObject.SetActive(sprite != null);
            if (sprite != null)
            {
                p.input.sprite = sprite;
                p.input.color = Fade(colors.inputIconTint, opacity);
                p.input.rectTransform.anchoredPosition = new Vector2(x, 0);
                x += s.inputIconSize + pad;
                SetActive(p.cap, false);
            }
            else
            {
                // No sprite for this input — the key cap carries its name. This is the fallback that was missing:
                // an input without a picture used to be drawn as a label with no key beside it at all.
                x += Cap(p.cap, p.capBg, InputGlyphInfo.Face(item.Input), s, x);
            }

            p.icon.gameObject.SetActive(icon != null);
            if (icon != null)
            {
                p.icon.sprite = icon;
                p.icon.color = Fade(colors.promptIconTint, opacity);
                p.icon.rectTransform.anchoredPosition = new Vector2(x, 0);
                x += s.iconSize + pad;
            }

            p.label.text = label;
            p.label.rectTransform.offsetMin = new Vector2(x, 0);
            return x + p.label.preferredWidth + pad;
        }

        /// <summary>Place a key cap at <paramref name="x"/> and report the width it consumed (0 when empty).</summary>
        float Cap(Text text, Image box, string face, PromptRowSettings s, float x)
        {
            bool show = !string.IsNullOrEmpty(face);
            SetActive(text, show);
            if (!show) return 0f;
            text.text = face;
            float w = Mathf.Max(s.keyCapMinWidth, text.preferredWidth + s.padding * 1.5f);
            var rt = (RectTransform)box.transform.parent;
            rt.anchoredPosition = new Vector2(x, 0);
            rt.sizeDelta = new Vector2(w, s.keyCapHeight);
            return w + s.padding;
        }

        /// <summary>A cap is a text inside a box object; showing or hiding one means the whole box.</summary>
        static void SetActive(Text capText, bool on)
        {
            if (capText != null) capText.transform.parent.gameObject.SetActive(on);
        }

        ActionArt FindPrompt(CursorAction a)
        {
            foreach (var e in cursor.prompts) if (e.action == a) return e;
            return null;
        }

        /// <summary>
        /// One row per action, re-keyed by name first. A row saved before the enum changed carries the name it
        /// was authored against, so it goes back onto the action it was meant for however the ints have moved;
        /// a row from before the names were stored is only worth keeping if it actually holds an icon, since
        /// nothing else on it can be trusted.
        /// </summary>
        void EnsurePromptList()
        {
            cursor.prompts.RemoveAll(e => e == null
                || (string.IsNullOrEmpty(e.key) && e.icon == null)
                || (!string.IsNullOrEmpty(e.key) && Named(e.key) == CursorAction.None));
            foreach (var e in cursor.prompts)
            {
                if (string.IsNullOrEmpty(e.key)) e.key = e.action.ToString();
                else e.action = Named(e.key);
            }
            cursor.prompts.RemoveAll(e => e.action == CursorAction.None
                                          || !Enum.IsDefined(typeof(CursorAction), e.action));
            foreach (CursorAction a in Enum.GetValues(typeof(CursorAction)))
            {
                if (a == CursorAction.None) continue;
                if (FindPrompt(a) == null) cursor.prompts.Add(new ActionArt { action = a, key = a.ToString() });
            }
        }

        /// <summary>The action a stored key names, or None if it names nothing this build has.</summary>
        static CursorAction Named(string key) =>
            Enum.TryParse(key, out CursorAction a) && Enum.IsDefined(typeof(CursorAction), a) ? a : CursorAction.None;

        /// <summary>Procedural ring sprite (transparent centre) so the charge indicator reads as a dial, not a square.</summary>
        Sprite RingSprite()
        {
            if (_ringSprite != null) return _ringSprite;
            const int size = 64;
            var tex = new Texture2D(size, size, TextureFormat.RGBA32, false) { name = "HudRing", filterMode = FilterMode.Bilinear, hideFlags = HideFlags.DontSave };
            float outer = size * 0.5f, inner = outer - Mathf.Max(1f, reticle.chargeThickness / reticle.chargeRadius * outer);
            var px = new Color[size * size];
            for (int y = 0; y < size; y++)
            for (int x = 0; x < size; x++)
            {
                float d = Vector2.Distance(new Vector2(x + 0.5f, y + 0.5f), new Vector2(outer, outer));
                float a = Mathf.Clamp01(outer - d) * Mathf.Clamp01(d - inner + 1f);
                px[y * size + x] = new Color(1f, 1f, 1f, a);
            }
            tex.SetPixels(px); tex.Apply();
            _ringSprite = Sprite.Create(tex, new Rect(0, 0, size, size), new Vector2(0.5f, 0.5f), 100f);
            _ringSprite.hideFlags = HideFlags.DontSave;
            return _ringSprite;
        }

        static void Kill(UnityEngine.Object o)
        {
            if (o == null) return;
            if (Application.isPlaying) Destroy(o); else DestroyImmediate(o);
        }
    }
}
