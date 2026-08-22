using Snowfield.Config;
using Snowfield.Sculpture;
using Unity.Mathematics;
using UnityEngine;
using UnityEngine.InputSystem;

namespace Snowfield.Player
{
    /// <summary>
    /// Screen-centre raycast onto sculptures → brush cursor. LMB hold = add (accumulates), RMB hold = smooth/pat,
    /// Ctrl+LMB = carve. Scroll = radius. Remesh on a timer while sculpting; colliders rebuild on release.
    /// </summary>
    public class SculptTool : MonoBehaviour
    {
        public enum Mode { Add, Smooth, Carve }

        public SculptFeelConfig config;
        public Camera viewCamera;
        public LayerMask sculptMask = ~0;
        [Tooltip("How far from the character the brush can reach (the ray itself starts at the camera).")]
        public float maxReach = 4f;
        [Tooltip("Reach is measured from here. Defaults to the SnowCharacter in the scene, else the camera.")]
        public Transform reachOrigin;
        [Tooltip("Visual cursor; scaled to brush diameter.")]
        public Transform cursor;

        public Mode CurrentMode { get; private set; } = Mode.Add;
        public bool IsSculpting { get; private set; }
        public SnowSculpture Target { get; private set; }
        public float3 BrushPoint { get; private set; }
        public bool HasHit { get; private set; }

        /// <summary>Radius multiplier on top of config, driven by scroll. Persisted per session only.</summary>
        public float radiusScale = 1f;

        float _tickAccumulator;
        float _remeshAccumulator;
        SnowSculpture _dirtySculpture;

        void Awake()
        {
            if (viewCamera == null) viewCamera = Camera.main;
            if (reachOrigin == null)
            {
                var ch = FindAnyObjectByType<SnowCharacter>();
                reachOrigin = ch != null ? ch.transform : viewCamera.transform;
            }
        }

        void Update()
        {
            if (config == null || viewCamera == null) return;
            var mouse = Mouse.current;
            var kb = Keyboard.current;

            // --- radius control ---
            if (mouse != null)
            {
                // Scroll magnitude varies per mouse/driver, so step per notch direction rather than by amount.
                float scroll = mouse.scroll.ReadValue().y;
                if (math.abs(scroll) > 0.01f)
                    radiusScale = math.clamp(radiusScale * (scroll > 0 ? 1.15f : 1f / 1.15f), 0.25f, 4f);
            }

            // --- aim ---
            HasHit = false;
            Target = null;
            var ray = viewCamera.ViewportPointToRay(new Vector3(0.5f, 0.5f, 0f));
            Vector3 origin = reachOrigin != null ? reachOrigin.position + Vector3.up : ray.origin;
            float rayLength = maxReach + Vector3.Distance(ray.origin, origin);
            if (Physics.Raycast(ray, out var hit, rayLength, sculptMask, QueryTriggerInteraction.Ignore))
            {
                var s = hit.collider.GetComponentInParent<SnowSculpture>();
                if (s != null && Vector3.Distance(hit.point, origin) <= maxReach)
                {
                    Target = s;
                    HasHit = true;
                    BrushPoint = hit.point;
                }
            }

            bool lmb = mouse != null && mouse.leftButton.isPressed;
            bool rmb = mouse != null && mouse.rightButton.isPressed;
            bool ctrl = kb != null && (kb.leftCtrlKey.isPressed || kb.rightCtrlKey.isPressed);
            bool pressing = lmb || rmb;
            Mode mode = rmb ? Mode.Smooth : (ctrl ? Mode.Carve : Mode.Add);
            CurrentMode = mode;

            float radius = CurrentRadius(mode);
            if (cursor != null)
            {
                cursor.gameObject.SetActive(HasHit);
                if (HasHit)
                {
                    cursor.position = BrushPoint;
                    cursor.localScale = Vector3.one * radius * 2f;
                }
            }

            // --- apply ---
            if (pressing && HasHit)
            {
                if (!IsSculpting) { IsSculpting = true; _tickAccumulator = 1f / config.ticksPerSecond; } // first tick immediate
                _dirtySculpture = Target;
                _tickAccumulator += Time.deltaTime;
                float tickDt = 1f / config.ticksPerSecond;
                int ticks = 0;
                while (_tickAccumulator >= tickDt && ticks < 8) { _tickAccumulator -= tickDt; ticks++; }
                for (int i = 0; i < ticks; i++) ApplyTick(Target, mode, BrushPoint, radius);
            }
            else if (IsSculpting && !pressing)
            {
                IsSculpting = false;
                Flush();
            }

            // --- timed remesh while sculpting ---
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

        public float CurrentRadius(Mode mode) =>
            (mode == Mode.Smooth ? config.smoothRadius : config.addRadius) * radiusScale;

        public void ApplyTick(SnowSculpture s, Mode mode, float3 point, float radius)
        {
            switch (mode)
            {
                case Mode.Add:
                    s.ApplyAdd(point, radius, config.addRatePerTick, config.addShoulder);
                    break;
                case Mode.Carve:
                    s.ApplyAdd(point, radius, -config.addRatePerTick, config.addShoulder);
                    break;
                case Mode.Smooth:
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

        void OnGUI()
        {
            if (config == null) return;
            var style = new GUIStyle(GUI.skin.label) { fontSize = 13 };
            style.normal.textColor = new Color(0.1f, 0.1f, 0.15f);
            GUI.Label(new Rect(12, 10, 600, 22), $"[{CurrentMode}]  radius {CurrentRadius(CurrentMode):0.00} m   rate {config.addRatePerTick:0}/tick @ {config.ticksPerSecond:0} Hz", style);
            GUI.Label(new Rect(12, 30, 600, 22), "LMB add · RMB smooth · Ctrl+LMB carve · scroll radius · WASD move · Tab cursor · +/- zoom", style);
            DrawReticle();
        }

        static Texture2D _white;

        /// <summary>Screen-centre reticle: a cross with a gap, plus a dot. Blue-ish when over snow, grey otherwise.</summary>
        void DrawReticle()
        {
            if (_white == null)
            {
                _white = new Texture2D(1, 1);
                _white.SetPixel(0, 0, Color.white);
                _white.Apply();
            }
            float cx = Screen.width * 0.5f, cy = Screen.height * 0.5f;
            Color col = HasHit ? new Color(0.35f, 0.75f, 1f, 0.95f) : new Color(1f, 1f, 1f, 0.6f);
            const float arm = 9f, gap = 4f, thick = 2f;

            void Bar(float x, float y, float w, float h, Color c)
            {
                var prev = GUI.color;
                GUI.color = new Color(0, 0, 0, c.a * 0.5f);           // soft shadow for readability on white snow
                GUI.DrawTexture(new Rect(x - 1, y - 1, w + 2, h + 2), _white);
                GUI.color = c;
                GUI.DrawTexture(new Rect(x, y, w, h), _white);
                GUI.color = prev;
            }

            Bar(cx - gap - arm, cy - thick * 0.5f, arm, thick, col);   // left
            Bar(cx + gap,       cy - thick * 0.5f, arm, thick, col);   // right
            Bar(cx - thick * 0.5f, cy - gap - arm, thick, arm, col);   // up
            Bar(cx - thick * 0.5f, cy + gap,       thick, arm, col);   // down
            Bar(cx - 1.5f, cy - 1.5f, 3f, 3f, col);                    // centre dot
        }
    }
}
