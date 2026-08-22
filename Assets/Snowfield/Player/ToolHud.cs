using Snowfield.Sculpture;
using UnityEngine;

namespace Snowfield.Player
{
    /// <summary>
    /// IMGUI overlay: mode boxes along the bottom (with number-key badges), accessory row above them in
    /// accessory mode, status line top-left, and the centre reticle. Pure presentation; reads SculptTool state.
    /// </summary>
    public class ToolHud : MonoBehaviour
    {
        public SculptTool tool;
        public AccessoryPlacer placer;

        [Header("Layout")]
        public float boxWidth = 150f, boxHeight = 44f, boxGap = 28f, bottomMargin = 26f;
        public float badgeSize = 20f;
        public float accessoryBox = 84f, accessoryGap = 16f;

        [Header("Colours")]
        public Color boxBg = new Color(0.08f, 0.1f, 0.14f, 0.72f);
        public Color boxText = new Color(0.85f, 0.88f, 0.95f);
        public Color selectedBg = new Color(0.95f, 0.97f, 1f, 0.95f);
        public Color selectedText = new Color(0.1f, 0.14f, 0.22f);
        public Color badgeBg = new Color(0.2f, 0.45f, 0.8f, 0.95f);

        static Texture2D _white;
        GUIStyle _box, _boxSel, _badge, _status, _accName, _accNameSel;

        void Awake()
        {
            if (tool == null) tool = GetComponent<SculptTool>();
            if (placer == null) placer = GetComponent<AccessoryPlacer>();
        }

        void EnsureStyles()
        {
            if (_white == null)
            {
                _white = new Texture2D(1, 1);
                _white.SetPixel(0, 0, Color.white);
                _white.Apply();
            }
            if (_box != null) return;
            _box = new GUIStyle(GUI.skin.label) { alignment = TextAnchor.MiddleCenter, fontSize = 15, fontStyle = FontStyle.Bold };
            _box.normal.textColor = boxText;
            _boxSel = new GUIStyle(_box);
            _boxSel.normal.textColor = selectedText;
            _badge = new GUIStyle(GUI.skin.label) { alignment = TextAnchor.MiddleCenter, fontSize = 12, fontStyle = FontStyle.Bold };
            _badge.normal.textColor = Color.white;
            _status = new GUIStyle(GUI.skin.label) { fontSize = 13 };
            _status.normal.textColor = new Color(0.1f, 0.1f, 0.15f);
            _accName = new GUIStyle(GUI.skin.label) { alignment = TextAnchor.MiddleCenter, fontSize = 11 };
            _accName.normal.textColor = boxText;
            _accNameSel = new GUIStyle(_accName);
            _accNameSel.normal.textColor = selectedText;
        }

        static void Fill(Rect r, Color c)
        {
            var prev = GUI.color;
            GUI.color = c;
            GUI.DrawTexture(r, _white);
            GUI.color = prev;
        }

        void OnGUI()
        {
            if (tool == null) return;
            EnsureStyles();
            DrawStatus();
            DrawModeBar();
            if (tool.Mode == ToolMode.Accessory && placer != null) DrawAccessoryBar();
            DrawReticle();
        }

        void DrawStatus()
        {
            string line = tool.Mode switch
            {
                ToolMode.Snow => $"radius {tool.CurrentRadius():0.00} m   rate {tool.config.addRatePerTick:0}/tick @ {tool.config.ticksPerSecond:0} Hz",
                ToolMode.EmptyHand => $"radius {tool.CurrentRadius():0.00} m   strength {tool.config.smoothStrength:0.00}",
                ToolMode.Accessory => placer != null ? $"{placer.Selected.DisplayName}" : "",
                _ => "",
            };
            GUI.Label(new Rect(12, 10, 700, 22), $"[{ToolModeInfo.DisplayName(tool.Mode)}]  {line}", _status);
            GUI.Label(new Rect(12, 30, 700, 22), $"{ToolModeInfo.Hint(tool.Mode)}   ·   Shift / 1-3 change mode · WASD move · Tab cursor · +/- zoom", _status);
        }

        Rect ModeBarRect(int count, out float startX, out float y)
        {
            float total = count * boxWidth + (count - 1) * boxGap;
            startX = (Screen.width - total) * 0.5f;
            y = Screen.height - bottomMargin - boxHeight;
            return new Rect(startX, y, total, boxHeight);
        }

        void DrawModeBar()
        {
            var modes = ToolModeInfo.All;
            ModeBarRect(modes.Length, out float x, out float y);
            for (int i = 0; i < modes.Length; i++)
            {
                bool sel = modes[i] == tool.Mode;
                var r = new Rect(x + i * (boxWidth + boxGap), y, boxWidth, boxHeight);
                Fill(new Rect(r.x - 1, r.y - 1, r.width + 2, r.height + 2), new Color(0, 0, 0, 0.35f));
                Fill(r, sel ? selectedBg : boxBg);
                GUI.Label(r, ToolModeInfo.DisplayName(modes[i]), sel ? _boxSel : _box);

                // number badge above
                var b = new Rect(r.x + (r.width - badgeSize) * 0.5f, r.y - badgeSize - 6f, badgeSize, badgeSize);
                Fill(new Rect(b.x - 1, b.y - 1, b.width + 2, b.height + 2), new Color(0, 0, 0, 0.35f));
                Fill(b, sel ? badgeBg : new Color(0.25f, 0.28f, 0.35f, 0.9f));
                GUI.Label(b, (i + 1).ToString(), _badge);
            }
        }

        void DrawAccessoryBar()
        {
            var entries = AccessoryCatalog.Entries;
            int n = entries.Count;
            float total = n * accessoryBox + (n - 1) * accessoryGap;
            float x0 = (Screen.width - total) * 0.5f;
            ModeBarRect(ToolModeInfo.All.Length, out _, out float modeY);
            float y = modeY - badgeSize - 6f - 18f - accessoryBox; // above the badges
            var thumbs = placer.Thumbnails;

            for (int i = 0; i < n; i++)
            {
                bool sel = i == placer.SelectedIndex;
                var r = new Rect(x0 + i * (accessoryBox + accessoryGap), y, accessoryBox, accessoryBox);
                Fill(new Rect(r.x - 1, r.y - 1, r.width + 2, r.height + 2), new Color(0, 0, 0, 0.35f));
                Fill(r, sel ? selectedBg : boxBg);
                if (i < thumbs.Count && thumbs[i] != null)
                    GUI.DrawTexture(new Rect(r.x + 6, r.y + 4, r.width - 12, r.height - 22), thumbs[i], ScaleMode.ScaleToFit, true);
                GUI.Label(new Rect(r.x, r.yMax - 18, r.width, 16), entries[i].DisplayName, sel ? _accNameSel : _accName);
            }
        }

        void DrawReticle()
        {
            float cx = Screen.width * 0.5f, cy = Screen.height * 0.5f;
            Color col = tool.HasHit ? new Color(0.35f, 0.75f, 1f, 0.95f) : new Color(1f, 1f, 1f, 0.6f);
            if (tool.Mode == ToolMode.Accessory && tool.AimedProp != null) col = new Color(1f, 0.45f, 0.35f, 0.95f);
            const float arm = 9f, gap = 4f, thick = 2f;

            void Bar(float x, float y, float w, float h)
            {
                Fill(new Rect(x - 1, y - 1, w + 2, h + 2), new Color(0, 0, 0, col.a * 0.5f));
                Fill(new Rect(x, y, w, h), col);
            }

            Bar(cx - gap - arm, cy - thick * 0.5f, arm, thick);
            Bar(cx + gap, cy - thick * 0.5f, arm, thick);
            Bar(cx - thick * 0.5f, cy - gap - arm, thick, arm);
            Bar(cx - thick * 0.5f, cy + gap, thick, arm);
            Bar(cx - 1.5f, cy - 1.5f, 3f, 3f);
        }
    }
}
