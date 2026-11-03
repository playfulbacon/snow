using System;
using System.Collections.Generic;
using Snowfield.Sculpture;
using UnityEngine;
using UnityEngine.UI;

namespace Snowfield.Player
{
    /// <summary>
    /// uGUI overlay for the tool: mode boxes with number badges along the bottom, an accessory row above them,
    /// a status line top-left, and the centre reticle.
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
            public float boxSize = 96f;
            public float gap = 18f;
            [Tooltip("Space between the mode-bar badges and the bottom of the accessory boxes.")]
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

        [Serializable]
        public class ActionArt
        {
            public CursorAction action;
            public Sprite icon;
            public string label;
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
        class AccessoryItem { public RectTransform root; public Image box, boxOutline, countBox, countOutline; public RawImage thumb; public Text label, count; }

        [SerializeField, HideInInspector] RectTransform _accessoryBarRoot, _reticleRoot, _statusRoot, _stackRoot, _hintRoot;
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
            if (!IsBuilt()) _pendingBuild = true;
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
        const int HudSchemaVersion = 2;
        [SerializeField, HideInInspector] int _hudSchemaVersion;

        bool IsBuilt() =>
            _hudSchemaVersion == HudSchemaVersion
            && canvas != null && _accessoryBarRoot != null && _reticleRoot != null && _statusRoot != null
            && _stackRoot != null && _hintRoot != null
            && _accessories.Count == AccessoryCatalog.Entries.Count
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

            // --- accessory bar (bottom-centre, shown while the Tab overlay is open) ---
            _accessoryBarRoot = MakeRect("AccessoryBar", root, new Vector2(0.5f, 0), new Vector2(0.5f, 0), new Vector2(0.5f, 0));
            foreach (var e in AccessoryCatalog.Entries)
            {
                var item = new AccessoryItem();
                item.root = MakeRect(e.DisplayName, _accessoryBarRoot, new Vector2(0, 0), new Vector2(0, 0), new Vector2(0, 0));
                item.boxOutline = Img("Outline", item.root);
                item.box = Img("Box", item.root);
                var thumbGo = new GameObject("Thumbnail", typeof(RectTransform), typeof(RawImage));
                thumbGo.transform.SetParent(item.root, false);
                item.thumb = thumbGo.GetComponent<RawImage>();
                item.thumb.raycastTarget = false;
                item.label = Txt("Label", item.root, TextAnchor.MiddleCenter);
                var countRoot = MakeRect("Count", item.root, new Vector2(1, 1), new Vector2(1, 1), new Vector2(1, 1));
                item.countOutline = Img("Outline", countRoot);
                item.countBox = Img("Box", countRoot);
                item.count = Txt("Label", countRoot, TextAnchor.MiddleCenter);
                item.count.fontStyle = FontStyle.Bold;
                _accessories.Add(item);
            }

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

            // accessory bar
            int k = _accessories.Count;
            float accW = k * accessoryBar.boxSize + (k - 1) * accessoryBar.gap;
            float accY = accessoryBar.bottomMargin;
            _accessoryBarRoot.anchoredPosition = new Vector2(0, accY);
            _accessoryBarRoot.sizeDelta = new Vector2(accW, accessoryBar.boxSize);
            for (int i = 0; i < k; i++)
            {
                var a = _accessories[i];
                a.root.anchoredPosition = new Vector2(i * (accessoryBar.boxSize + accessoryBar.gap), 0);
                a.root.sizeDelta = Vector2.one * accessoryBar.boxSize;
                Stretch(a.boxOutline.rectTransform, -ow);
                Stretch(a.box.rectTransform, 0);
                float p = accessoryBar.thumbnailPadding;
                var tr = a.thumb.rectTransform;
                tr.anchorMin = new Vector2(0, 0); tr.anchorMax = new Vector2(1, 1);
                tr.offsetMin = new Vector2(p, accessoryBar.labelHeight);
                tr.offsetMax = new Vector2(-p, -p);
                var lr = a.label.rectTransform;
                lr.anchorMin = new Vector2(0, 0); lr.anchorMax = new Vector2(1, 0); lr.pivot = new Vector2(0.5f, 0);
                lr.anchoredPosition = Vector2.zero;
                lr.sizeDelta = new Vector2(0, accessoryBar.labelHeight);
                a.label.fontSize = accessoryBar.labelFontSize;
                var cr = (RectTransform)a.countBox.transform.parent;
                cr.anchoredPosition = accessoryBar.countOffset;
                cr.sizeDelta = Vector2.one * accessoryBar.countSize;
                Stretch(a.countOutline.rectTransform, -ow);
                Stretch(a.countBox.rectTransform, 0);
                Stretch(a.count.rectTransform, 0);
                a.count.fontSize = accessoryBar.countFontSize;
            }

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
            bool accVisible = playing ? mode == ToolMode.Accessory : previewAccessoryBar;
            int accIndex = playing && placer != null ? placer.SelectedIndex : previewAccessoryIndex;
            var c = colors;

            _accessoryBarRoot.gameObject.SetActive(accVisible);
            if (accVisible)
            {
                // The bar is the inventory now, not the catalog: one box per occupied slot, in slot order, and
                // the same item can hold two slots. So the box count varies at runtime while the pool does not,
                // and the bar has to place and size itself here rather than in the structural pass. Out of play
                // there is no inventory, so the preview falls back to showing the catalog — which is what the
                // bar is for in the inspector.
                var inv = playing && placer != null ? placer.Inventory : null;
                int shown = inv != null ? Mathf.Min(inv.SlotCount, _accessories.Count) : _accessories.Count;
                float bs = accessoryBar.boxSize, gp = accessoryBar.gap;
                _accessoryBarRoot.sizeDelta = new Vector2(shown > 0 ? shown * bs + (shown - 1) * gp : 0f, bs);

                for (int i = 0; i < _accessories.Count; i++)
                {
                    var a = _accessories[i];
                    bool used = i < shown;
                    a.root.gameObject.SetActive(used);
                    if (!used) continue;

                    a.root.anchoredPosition = new Vector2(i * (bs + gp), 0);

                    var entry = inv != null ? inv.ItemAt(i)
                              : i < AccessoryCatalog.Entries.Count ? AccessoryCatalog.Entries[i] : null;
                    bool sel = i == accIndex;
                    a.box.color = sel ? c.selectedBg : c.boxBg;
                    a.label.color = sel ? c.selectedText : c.boxText;
                    a.label.text = entry != null ? entry.DisplayName : "";

                    var tex = entry != null && placer != null ? placer.ThumbnailFor(entry.Id) : null;
                    a.thumb.texture = tex;
                    a.thumb.color = tex != null ? Color.white : c.thumbnailPlaceholder;

                    // The badge used to be hidden outright, because the supply was unlimited. It is not any
                    // more — a slot holds a finite stack, so the number is half the point of the bar.
                    a.countBox.color = c.countBg;
                    a.count.color = c.countText;
                    a.count.text = inv != null ? inv.CountAt(i).ToString() : "";
                    ((RectTransform)a.countBox.transform.parent).gameObject.SetActive(inv != null);

                    a.boxOutline.color = Frame(c.outline, a.box.color);
                    a.countOutline.color = Frame(c.outline, a.countBox.color);
                }
            }

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
            if (hints.show && (!playing || Cursor.lockState == CursorLockMode.Locked))
            {
                _persistent.Add(new ActionPrompt(ToolModeInfo.TabAction(mode), InputGlyph.Tab));
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
                         : placer.Selected.DisplayName)
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
                pool[i].root.anchoredPosition = new Vector2(0f, downward ? -y : y);
                pool[i].root.sizeDelta = new Vector2(width, s.height);
            }
        }

        /// <summary>Fill one row and report the width its content wants.</summary>
        float ApplyPrompt(Prompt p, ActionPrompt item, PromptRowSettings s, float opacity)
        {
            var entry = FindPrompt(item.Action);
            string label = entry != null && !string.IsNullOrEmpty(entry.label)
                ? entry.label : CursorActionInfo.DefaultLabel(item.Action);
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

        /// <summary>Bump whenever CursorAction values are added/removed/reordered: stored rows are keyed by the enum's int.</summary>
        const int PromptSchemaVersion = 5;
        [SerializeField, HideInInspector] int _promptSchemaVersion;

        /// <summary>Make sure the inspector list has one row per action, with default labels filled in.</summary>
        void EnsurePromptList()
        {
            if (_promptSchemaVersion != PromptSchemaVersion)
            {
                cursor.prompts.Clear(); // enum ints shifted: stored labels/icons would attach to the wrong actions
                _promptSchemaVersion = PromptSchemaVersion;
            }
            cursor.prompts.RemoveAll(e => !Enum.IsDefined(typeof(CursorAction), e.action) || e.action == CursorAction.None);
            foreach (CursorAction a in Enum.GetValues(typeof(CursorAction)))
            {
                if (a == CursorAction.None) continue;
                var e = FindPrompt(a);
                if (e == null) { e = new ActionArt { action = a }; cursor.prompts.Add(e); }
                if (string.IsNullOrEmpty(e.label)) e.label = CursorActionInfo.DefaultLabel(a);
            }
        }

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
