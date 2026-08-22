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
        public class ModeBarSettings
        {
            public float boxWidth = 170f;
            public float boxHeight = 48f;
            public float gap = 32f;
            public float bottomMargin = 28f;
            public int fontSize = 20;
            [Header("Number badge")]
            public float badgeSize = 22f;
            public float badgeGap = 8f;
            public int badgeFontSize = 14;
        }

        [Serializable]
        public class AccessoryBarSettings
        {
            public float boxSize = 96f;
            public float gap = 18f;
            [Tooltip("Space between the mode-bar badges and the bottom of the accessory boxes.")]
            public float gapAboveModeBar = 22f;
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
        }

        [Serializable]
        public class StatusSettings
        {
            public Vector2 margin = new Vector2(14f, 12f);
            public int fontSize = 16;
            public float lineSpacing = 4f;
            public float width = 1100f;
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
        public ModeBarSettings modeBar = new ModeBarSettings();
        public AccessoryBarSettings accessoryBar = new AccessoryBarSettings();
        public ReticleSettings reticle = new ReticleSettings();
        public StatusSettings status = new StatusSettings();
        public Palette colors = new Palette();

        [Header("Edit-mode preview")]
        public ToolMode previewMode = ToolMode.Snow;
        public bool previewAccessoryBar = true;
        public int previewAccessoryIndex = 0;

        // ------------------------------------------------------------------ generated refs

        [Serializable]
        class ModeItem { public RectTransform root; public Image box, boxOutline, badge, badgeOutline; public Text label, badgeLabel; }

        [Serializable]
        class AccessoryItem { public RectTransform root; public Image box, boxOutline, countBox, countOutline; public RawImage thumb; public Text label, count; }

        [SerializeField, HideInInspector] RectTransform _modeBarRoot, _accessoryBarRoot, _reticleRoot, _statusRoot;
        [SerializeField, HideInInspector] List<ModeItem> _modes = new List<ModeItem>();
        [SerializeField, HideInInspector] List<AccessoryItem> _accessories = new List<AccessoryItem>();
        [SerializeField, HideInInspector] Image[] _reticleBars = new Image[4];
        [SerializeField, HideInInspector] Image[] _reticleShadows = new Image[4];
        [SerializeField, HideInInspector] Image _reticleDot, _reticleDotShadow;
        [SerializeField, HideInInspector] Text _statusLine1, _statusLine2;

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
            _modes.Clear(); _accessories.Clear();
            Build();
            ApplyAll();
        }

        bool IsBuilt() =>
            canvas != null && _modeBarRoot != null && _accessoryBarRoot != null && _reticleRoot != null && _statusRoot != null
            && _modes.Count == ToolModeInfo.All.Length && _accessories.Count == AccessoryCatalog.Entries.Count
            && _statusLine1 != null && _reticleDot != null
            && (_accessories.Count == 0 || _accessories[0].count != null);

        // ------------------------------------------------------------------ build

        void Build()
        {
            _pendingBuild = false;
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
            for (int i = canvas.transform.childCount - 1; i >= 0; i--) Kill(canvas.transform.GetChild(i).gameObject);
            _modes.Clear(); _accessories.Clear();

            var root = canvas.transform;

            // --- status (top-left) ---
            _statusRoot = MakeRect("Status", root, new Vector2(0, 1), new Vector2(0, 1), new Vector2(0, 1));
            _statusLine1 = Txt("Line1", _statusRoot, TextAnchor.UpperLeft);
            _statusLine2 = Txt("Line2", _statusRoot, TextAnchor.UpperLeft);

            // --- mode bar (bottom-centre) ---
            _modeBarRoot = MakeRect("ModeBar", root, new Vector2(0.5f, 0), new Vector2(0.5f, 0), new Vector2(0.5f, 0));
            foreach (var m in ToolModeInfo.All)
            {
                var item = new ModeItem();
                item.root = MakeRect(ToolModeInfo.DisplayName(m).Replace(" ", ""), _modeBarRoot, new Vector2(0, 0), new Vector2(0, 0), new Vector2(0, 0));
                item.boxOutline = Img("Outline", item.root);
                item.box = Img("Box", item.root);
                item.label = Txt("Label", item.root, TextAnchor.MiddleCenter);
                item.label.fontStyle = FontStyle.Bold;
                var badgeRoot = MakeRect("Badge", item.root, new Vector2(0.5f, 1), new Vector2(0.5f, 1), new Vector2(0.5f, 0));
                item.badgeOutline = Img("Outline", badgeRoot);
                item.badge = Img("Box", badgeRoot);
                item.badgeLabel = Txt("Label", badgeRoot, TextAnchor.MiddleCenter);
                item.badgeLabel.fontStyle = FontStyle.Bold;
                _modes.Add(item);
            }

            // --- accessory bar (above mode bar) ---
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

            // mode bar
            int n = _modes.Count;
            float totalW = n * modeBar.boxWidth + (n - 1) * modeBar.gap;
            _modeBarRoot.anchoredPosition = new Vector2(0, modeBar.bottomMargin);
            _modeBarRoot.sizeDelta = new Vector2(totalW, modeBar.boxHeight);
            for (int i = 0; i < n; i++)
            {
                var m = _modes[i];
                m.root.anchoredPosition = new Vector2(i * (modeBar.boxWidth + modeBar.gap), 0);
                m.root.sizeDelta = new Vector2(modeBar.boxWidth, modeBar.boxHeight);
                Stretch(m.boxOutline.rectTransform, -ow);
                Stretch(m.box.rectTransform, 0);
                Stretch(m.label.rectTransform, 0);
                m.label.fontSize = modeBar.fontSize;
                var badgeRoot = (RectTransform)m.badge.transform.parent;
                badgeRoot.anchoredPosition = new Vector2(0, modeBar.badgeGap);
                badgeRoot.sizeDelta = Vector2.one * modeBar.badgeSize;
                Stretch(m.badgeOutline.rectTransform, -ow);
                Stretch(m.badge.rectTransform, 0);
                Stretch(m.badgeLabel.rectTransform, 0);
                m.badgeLabel.fontSize = modeBar.badgeFontSize;
                m.boxOutline.color = m.badgeOutline.color = c.outline;
                m.badgeLabel.color = c.badgeText;
                m.badgeLabel.text = (i + 1).ToString();
                m.label.text = ToolModeInfo.DisplayName(ToolModeInfo.All[i]);
            }

            // accessory bar
            int k = _accessories.Count;
            float accW = k * accessoryBar.boxSize + (k - 1) * accessoryBar.gap;
            float accY = modeBar.bottomMargin + modeBar.boxHeight + modeBar.badgeGap + modeBar.badgeSize + accessoryBar.gapAboveModeBar;
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
                a.label.text = AccessoryCatalog.Entries[i].DisplayName;
                a.boxOutline.color = c.outline;
                var cr = (RectTransform)a.countBox.transform.parent;
                cr.anchoredPosition = accessoryBar.countOffset;
                cr.sizeDelta = Vector2.one * accessoryBar.countSize;
                Stretch(a.countOutline.rectTransform, -ow);
                Stretch(a.countBox.rectTransform, 0);
                Stretch(a.count.rectTransform, 0);
                a.count.fontSize = accessoryBar.countFontSize;
                a.countOutline.color = c.outline;
                a.countBox.color = c.countBg;
                a.count.color = c.countText;
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
        }

        void ApplyState()
        {
            bool playing = Application.isPlaying && tool != null;
            ToolMode mode = playing ? tool.Mode : previewMode;
            bool accVisible = playing ? mode == ToolMode.Accessory : previewAccessoryBar;
            int accIndex = playing && placer != null ? placer.SelectedIndex : previewAccessoryIndex;
            var c = colors;

            for (int i = 0; i < _modes.Count; i++)
            {
                bool sel = ToolModeInfo.All[i] == mode;
                var m = _modes[i];
                m.box.color = sel ? c.selectedBg : c.boxBg;
                m.label.color = sel ? c.selectedText : c.boxText;
                m.badge.color = sel ? c.badgeBg : c.badgeBgIdle;
            }

            _accessoryBarRoot.gameObject.SetActive(accVisible);
            if (accVisible)
            {
                var thumbs = playing && placer != null ? placer.Thumbnails : null;
                var inv = playing && placer != null ? placer.Inventory : null;
                for (int i = 0; i < _accessories.Count; i++)
                {
                    bool sel = i == accIndex;
                    var a = _accessories[i];
                    int n = inv != null ? inv.Count(AccessoryCatalog.Entries[i].Id) : (playing ? 0 : 3);
                    bool empty = n <= 0;
                    Color tint = empty ? c.emptyTint : Color.white;
                    a.box.color = (sel ? c.selectedBg : c.boxBg) * tint;
                    a.label.color = (sel ? c.selectedText : c.boxText) * tint;
                    var tex = thumbs != null && i < thumbs.Count ? thumbs[i] : null;
                    a.thumb.texture = tex;
                    a.thumb.color = (tex != null ? Color.white : c.thumbnailPlaceholder) * tint;
                    a.count.text = n.ToString();
                    a.countBox.color = c.countBg * tint;
                    a.count.color = c.countText * tint;
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

            // status text
            string line = "";
            if (playing && tool.config != null)
            {
                line = mode switch
                {
                    ToolMode.Snow => $"radius {tool.CurrentRadius():0.00} m   rate {tool.config.addRatePerTick:0}/tick @ {tool.config.ticksPerSecond:0} Hz",
                    ToolMode.EmptyHand => tool.Roller != null && tool.Roller.IsEngaged
                        ? $"snowball {tool.Roller.Radius * 2f:0.00} m"
                        : $"radius {tool.CurrentRadius():0.00} m   strength {tool.config.smoothStrength:0.00}",
                    ToolMode.Accessory => placer != null ? placer.Selected.DisplayName : "",
                    _ => "",
                };
            }
            _statusLine1.text = $"[{ToolModeInfo.DisplayName(mode)}]  {line}";
            _statusLine2.text = $"{ToolModeInfo.Hint(mode)}   ·   Shift / 1-3 change mode · WASD move · Tab cursor · +/- zoom";
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

        static void Kill(UnityEngine.Object o)
        {
            if (o == null) return;
            if (Application.isPlaying) Destroy(o); else DestroyImmediate(o);
        }
    }
}
