using System;
using System.Collections.Generic;
using Snowfield.Config;
using Snowfield.Net;
using Snowfield.Player;
using Snowfield.Sculpture;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.SceneManagement;
using UnityEngine.UI;

namespace Snowfield.UI
{
    /// <summary>
    /// The in-game menu: Esc opens it, the cursor comes free, and the game stands still behind it. It rides in
    /// Assets/Prefabs/Game.prefab with the rest of the kit, so a scene that stands the game gets the menu too —
    /// there is nothing per-scene to remember to wire.
    ///
    /// Every page is built at runtime out of the row prefabs under Assets/Prefabs/UI (button, slider, toggle,
    /// dropdown, read-only row) into one <see cref="MenuPanel"/>. That is the whole styling story: edit a row
    /// prefab and every page follows, edit the panel prefab and the box and its rhythm change. Pages are *not*
    /// hand-authored object trees, because half of what is on them depends on the moment — whether you are in a
    /// session, what resolutions this screen has, whether the mic is live — and a hand-built page of those goes
    /// stale the first time it is shown.
    ///
    /// Two deliberate rules about stopping the world:
    /// • In single-player the clock stops (<see cref="GamePause"/>). In a shared field it does not — the other
    ///   players are still sculpting, and a frozen local clock would only desync this client.
    /// • Esc is owned here, not by the player controller. It used to just free the cursor there; that is now what
    ///   opening this does.
    /// </summary>
    public sealed class PauseMenu : MonoBehaviour
    {
        [Header("Scene objects (inside this prefab)")]
        [Tooltip("Everything that is only on screen while paused — the dim and the panel.")]
        public GameObject root;
        public MenuPanel panel;
        public ConfirmDialog dialog;

        [Header("Row prefabs (Assets/Prefabs/UI) — style the menu by editing these")]
        public GameObject buttonPrefab;
        public GameObject sliderPrefab;
        public GameObject togglePrefab;
        public GameObject dropdownPrefab;
        public GameObject rowPrefab;

        [Header("Wired by MainSceneSetup")]
        [Tooltip("The player's camera; the field-of-view setting is applied to it.")]
        public Camera viewCamera;
        [Tooltip("Optional — lets the Display page turn the HUD's dev status lines on and off.")]
        public ToolHud hud;

        [Header("Behaviour")]
        [Tooltip("Scene loaded by Quit to Title. Must be in Build Settings.")]
        public string titleScene = "Start";
        [Tooltip("Save the field before leaving for the title. Quit-to-desktop is covered by SaveLoadManager.")]
        public bool saveOnQuitToTitle = true;
        [Tooltip("Switch the HUD off while the menu is up — a reticle through the middle of a menu reads as a bug.")]
        public bool hideHudWhilePaused = true;

        /// <summary>The pages, in the order they are reached. Root is what Esc opens.</summary>
        enum Page { Root, Audio, Controls, Keys, Display, Multiplayer }

        Page _page = Page.Root;
        readonly List<Vector2Int> _resolutions = new List<Vector2Int>();

        public bool IsOpen => root != null && root.activeSelf;

        void Awake()
        {
            if (root != null) root.SetActive(false);
            GameSettings.Changed += ApplyLive;
        }

        void OnDestroy()
        {
            GameSettings.Changed -= ApplyLive;
        }

        void Start()
        {
            // The view settings have to hold from the first frame, not from the first time the menu is opened.
            ApplyLive();
        }

        void Update()
        {
            var keyboard = Keyboard.current;
            if (keyboard == null || !keyboard.escapeKey.wasPressedThisFrame) return;

            if (dialog != null && dialog.IsOpen) { dialog.Close(); return; }
            if (!IsOpen) { Open(); return; }
            if (_page != Page.Root) { Show(Page.Root); return; }
            Close();
        }

        // ------------------------------------------------------------------ open / close

        public void Open()
        {
            if (IsOpen) return;
            // A session cannot be paused; see the class comment. Status is the only thing that knows.
            GamePause.Set(true, freezeTime: !InSession);
            if (root != null) root.SetActive(true);
            ShowHud(false);
            Show(Page.Root);
        }

        public void Close()
        {
            if (dialog != null) dialog.Close();
            if (root != null) root.SetActive(false);
            ShowHud(true);
            GamePause.Set(false);
            GameSettings.Save();   // one write per visit, rather than one per slider frame
        }

        /// <summary>
        /// The HUD's Canvas rather than its GameObject: disabling the object would make ToolHud rebuild its whole
        /// tree on every resume, and this only needs to stop it drawing.
        /// </summary>
        void ShowHud(bool visible)
        {
            if (!hideHudWhilePaused || hud == null) return;
            var canvas = hud.GetComponent<Canvas>();
            if (canvas != null) canvas.enabled = visible;
        }

        /// <summary>Everything a setting change has to reach that is not the engine's own business.</summary>
        void ApplyLive()
        {
            if (viewCamera != null) viewCamera.fieldOfView = GameSettings.FieldOfView;
            VoiceChat.SetOutputVolume(GameSettings.VoiceVolume);
            if (hud != null && hud.status.show != GameSettings.ShowHudStatus)
            {
                hud.status.show = GameSettings.ShowHudStatus;
                hud.RebuildNow();
            }
        }

        // ------------------------------------------------------------------ pages

        void Show(Page page)
        {
            _page = page;
            if (panel == null) return;
            panel.Clear();

            switch (page)
            {
                case Page.Audio: BuildAudio(); break;
                case Page.Controls: BuildControls(); break;
                case Page.Keys: BuildKeys(); break;
                case Page.Display: BuildDisplay(); break;
                case Page.Multiplayer: BuildMultiplayer(); break;
                default: BuildRoot(); break;
            }
        }

        void BuildRoot()
        {
            panel.SetTitle(InSession ? "PAUSED — the field keeps moving" : "PAUSED");
            AddButton("RESUME", Close);
            AddButton("AUDIO", () => Show(Page.Audio));
            AddButton("CONTROLS", () => Show(Page.Controls));
            AddButton("DISPLAY", () => Show(Page.Display));
            AddButton("MULTIPLAYER", () => Show(Page.Multiplayer));
            AddButton("SAVE FIELD", SaveField);
            AddButton("QUIT TO TITLE", AskQuitToTitle);
            AddButton("QUIT TO DESKTOP", AskQuitToDesktop);
        }

        void BuildAudio()
        {
            panel.SetTitle("AUDIO");
            AddSlider("MASTER", 0f, 1f, GameSettings.MasterVolume, v => GameSettings.MasterVolume = v);
            AddSlider("MUSIC", 0f, 1f, GameSettings.MusicVolume, v => GameSettings.MusicVolume = v);
            AddSlider("SOUND & AMBIENCE", 0f, 1f, GameSettings.SfxVolume, v => GameSettings.SfxVolume = v);
            AddSlider("VOICE CHAT", 0f, 1f, GameSettings.VoiceVolume, v => GameSettings.VoiceVolume = v);
            AddToggle("MICROPHONE", !VoiceChat.Muted, on =>
            {
                if (on == !VoiceChat.Muted) return;
                VoiceChat.ToggleMute();        // the M key and this switch are the same one state
            });
            AddRow("MIC KEY", InputGlyphInfo.Name(InputGlyph.Mute));
            Back();
        }

        void BuildControls()
        {
            panel.SetTitle("CONTROLS");
            AddSlider("LOOK SENSITIVITY", GameSettings.LookMin, GameSettings.LookMax, GameSettings.LookSensitivity,
                   v => GameSettings.LookSensitivity = v, v => v.ToString("0.00") + "x");
            AddToggle("INVERT LOOK Y", GameSettings.InvertLookY, on => GameSettings.InvertLookY = on);
            AddButton("KEYS", () => Show(Page.Keys));
            Back();
        }

        /// <summary>
        /// What every key does, on a page of its own: the panel grows to its content and the whole scheme is
        /// thirteen lines, which is a taller card than a 1080p screen has room for next to anything else.
        /// </summary>
        void BuildKeys()
        {
            panel.SetTitle("KEYS");
            foreach (var binding in Controls.All) AddRow(binding.Key, binding.What);
            AddButton("BACK", () => Show(Page.Controls));
        }

        void BuildDisplay()
        {
            panel.SetTitle("DISPLAY");
            AddSlider("FIELD OF VIEW", GameSettings.FovMin, GameSettings.FovMax, GameSettings.FieldOfView,
                   v => GameSettings.FieldOfView = v, v => Mathf.RoundToInt(v) + "°");
            AddToggle("FULLSCREEN", GameSettings.Fullscreen, on => GameSettings.Fullscreen = on);
            AddToggle("V-SYNC", GameSettings.VSync, on => GameSettings.VSync = on);

            var quality = QualitySettings.names;
            if (quality.Length > 1)
                AddDropdown("QUALITY", quality, Mathf.Clamp(GameSettings.QualityLevel, 0, quality.Length - 1),
                         i => GameSettings.QualityLevel = i);

            RefreshResolutions();
            if (_resolutions.Count > 1)
            {
                var labels = new List<string>(_resolutions.Count);
                int current = 0;
                for (int i = 0; i < _resolutions.Count; i++)
                {
                    labels.Add(_resolutions[i].x + " × " + _resolutions[i].y);
                    if (_resolutions[i].x == GameSettings.ResolutionWidth
                        && _resolutions[i].y == GameSettings.ResolutionHeight) current = i;
                }
                AddDropdown("RESOLUTION", labels, current,
                         i => GameSettings.SetResolution(_resolutions[i].x, _resolutions[i].y));
            }

            AddToggle("DEV STATUS LINES", GameSettings.ShowHudStatus, on => GameSettings.ShowHudStatus = on);
            Back();
        }

        void BuildMultiplayer()
        {
            panel.SetTitle("MULTIPLAYER");
            AddRow("CONNECTION", NetBootstrap.Status);
            if (!string.IsNullOrEmpty(NetBootstrap.JoinCode))
            {
                AddRow("JOIN CODE", JoinCodeFormat.Pretty(NetBootstrap.JoinCode));
                // Reading six characters off a screen to someone is the worst part of inviting a friend.
                AddButton("COPY THE CODE", () => GUIUtility.systemCopyBuffer = NetBootstrap.JoinCode);
            }

            var net = FindAnyObjectByType<NetBootstrap>();
            if (net == null)
            {
                AddRow("", "No NetworkManager in this scene.");
            }
            else if (InSession)
            {
                AddButton("LEAVE THE SHARED FIELD", () => dialog?.Ask(
                    "Leave the shared field?",
                    "You go back to sculpting alone. The field you are standing in stays as it is; your own field "
                    + "comes back next launch.",
                    "LEAVE", () => { net.Toggle(); Show(Page.Multiplayer); }));
            }
            else
            {
                AddButton("JOIN THE SHARED FIELD", () => { net.Toggle(); Show(Page.Multiplayer); });
            }
            AddRow("SHORTCUT", InputGlyphInfo.Name(InputGlyph.SessionChord));
            Back();
        }

        // ------------------------------------------------------------------ actions

        void SaveField()
        {
            var save = SaveLoadManager.Instance;
            if (save == null)
            {
                Debug.LogWarning("[PauseMenu] No SaveLoadManager in the scene — nothing saved.");
                return;
            }
            save.SaveAll();
        }

        void AskQuitToTitle()
        {
            if (dialog == null) { QuitToTitle(); return; }
            dialog.Ask("Quit to the title screen?",
                       saveOnQuitToTitle
                           ? "The field is saved first, and will be here when you come back."
                           : "Anything unsaved in this field is lost.",
                       "QUIT TO TITLE", QuitToTitle);
        }

        void AskQuitToDesktop()
        {
            if (dialog == null) { QuitToDesktop(); return; }
            dialog.Ask("Quit the game?",
                       "The field is saved on the way out.",
                       "QUIT", QuitToDesktop);
        }

        void QuitToTitle()
        {
            // Leaving for the title is not a quit, so nothing autosaves on the way: do it here or the field is
            // gone. A session has to be let go of too, or the lobby keeps this player in it.
            if (saveOnQuitToTitle) SaveField();
            var net = FindAnyObjectByType<NetBootstrap>();
            if (net != null && InSession) _ = net.DisconnectAsync();

            GameSettings.Save();
            GamePause.Set(false);   // restores timeScale before the next scene starts running
            SceneManager.LoadScene(titleScene);
        }

        void QuitToDesktop()
        {
            GameSettings.Save();
            GamePause.Set(false);
#if UNITY_EDITOR
            UnityEditor.EditorApplication.isPlaying = false;
#else
            Application.Quit();
#endif
        }

        // ------------------------------------------------------------------ row helpers

        void AddButton(string label, Action onClick)
        {
            var go = panel.Add<Transform>(buttonPrefab);
            if (go == null) return;
            var text = go.GetComponentInChildren<Text>(true);
            if (text != null) text.text = label;
            var button = go.GetComponent<Button>() ?? go.GetComponentInChildren<Button>(true);
            if (button == null) return;
            button.onClick.RemoveAllListeners();
            button.onClick.AddListener(() => onClick?.Invoke());
        }

        void AddSlider(string label, float min, float max, float current, Action<float> set,
                    Func<float, string> format = null)
        {
            panel.Add<MenuSlider>(sliderPrefab)?.Bind(label, min, max, current, set, format);
        }

        void AddToggle(string label, bool current, Action<bool> set)
        {
            panel.Add<MenuToggle>(togglePrefab)?.Bind(label, current, set);
        }

        void AddDropdown(string label, IList<string> options, int current, Action<int> set)
        {
            panel.Add<MenuDropdown>(dropdownPrefab)?.Bind(label, options, current, set);
        }

        void AddRow(string label, string value)
        {
            panel.Add<MenuRow>(rowPrefab)?.Bind(label, value);
        }

        void Back() => AddButton("BACK", () => Show(Page.Root));

        // ------------------------------------------------------------------ odds and ends

        static bool InSession => NetBootstrap.Online;

        /// <summary>
        /// Distinct screen sizes this display offers, largest first. Refresh rates are collapsed away: the game
        /// does not care, and three copies of the same size in a dropdown looks like a bug.
        /// </summary>
        void RefreshResolutions()
        {
            _resolutions.Clear();
            foreach (var res in Screen.resolutions)
            {
                var size = new Vector2Int(res.width, res.height);
                if (!_resolutions.Contains(size)) _resolutions.Add(size);
            }
            _resolutions.Sort((a, b) => (b.x * b.y).CompareTo(a.x * a.y));
            var native = new Vector2Int(Screen.width, Screen.height);
            if (!_resolutions.Contains(native)) _resolutions.Insert(0, native);
        }

        // The keys page is printed from Snowfield.Player.Controls — the same table the on-screen prompts are
        // drawn from — rather than a list kept here. The list that used to be here had drifted: it still
        // promised Shift + left mouse smoothed, months after that became the planar shave and smoothing moved
        // to the right button. A printed page rather than rebinding is still the call: this is a one-month game
        // with a fixed scheme, and a rebinding UI is a week of work the sculpting brush wants.
    }
}
