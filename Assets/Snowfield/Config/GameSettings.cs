using System;
using UnityEngine;

namespace Snowfield.Config
{
    /// <summary>
    /// Every player-facing setting the pause menu can change, in one place, saved to PlayerPrefs and applied to
    /// the engine. Lives in Snowfield.Config rather than with the menu for one reason: the things that *obey* a
    /// setting are spread across assemblies (the player controller is in Assembly-CSharp, the camera is in the
    /// scene, audio arrives in Phase 2), and all of them can reference Config. Nothing has to know the menu
    /// exists — the menu writes here, everything else reads here.
    ///
    /// Properties clamp, store, apply and raise <see cref="Changed"/>; they do not write to disk on every drag,
    /// because one slider raises hundreds of those. <see cref="Save"/> is called when the menu closes.
    ///
    /// Volumes: master is real (it is <see cref="AudioListener.volume"/>). Music and effects are stored and
    /// published for the Phase 2 audio layers to multiply into their sources — there is no game audio yet, so
    /// those two sliders currently move a number nothing is listening to. Voice is applied to Vivox by
    /// Snowfield.Net, which owns that dependency.
    /// </summary>
    public static class GameSettings
    {
        const string Prefix = "snowfield.";

        /// <summary>Raised after any setting changes, once the new value has already been applied.</summary>
        public static event Action Changed;

        // Defaults are the authored game: full master, a quieter music bed, no inversion, 1x look.
        static float s_Master = 1f;
        static float s_Music = 0.7f;
        static float s_Sfx = 1f;
        static float s_Voice = 1f;
        static float s_Look = 1f;
        static bool s_InvertY;
        static float s_Fov = 60f;
        static bool s_Fullscreen = true;
        static int s_Width;
        static int s_Height;
        static bool s_VSync = true;
        static int s_Quality = -1;
        static bool s_ShowStatus;
        static bool s_Loaded;

        public const float LookMin = 0.2f;
        public const float LookMax = 3f;
        public const float FovMin = 50f;
        public const float FovMax = 100f;

        public static float MasterVolume
        {
            get { Ensure(); return s_Master; }
            set => Set(ref s_Master, Mathf.Clamp01(value));
        }

        public static float MusicVolume
        {
            get { Ensure(); return s_Music; }
            set => Set(ref s_Music, Mathf.Clamp01(value));
        }

        /// <summary>Sound effects and ambience — the fwump of packed snow, wind, footsteps.</summary>
        public static float SfxVolume
        {
            get { Ensure(); return s_Sfx; }
            set => Set(ref s_Sfx, Mathf.Clamp01(value));
        }

        public static float VoiceVolume
        {
            get { Ensure(); return s_Voice; }
            set => Set(ref s_Voice, Mathf.Clamp01(value));
        }

        /// <summary>Multiplier on the controller's authored look speed, so tuning that value still means something.</summary>
        public static float LookSensitivity
        {
            get { Ensure(); return s_Look; }
            set => Set(ref s_Look, Mathf.Clamp(value, LookMin, LookMax));
        }

        public static bool InvertLookY
        {
            get { Ensure(); return s_InvertY; }
            set => Set(ref s_InvertY, value);
        }

        public static float FieldOfView
        {
            get { Ensure(); return s_Fov; }
            set => Set(ref s_Fov, Mathf.Clamp(value, FovMin, FovMax));
        }

        public static bool Fullscreen
        {
            get { Ensure(); return s_Fullscreen; }
            set => Set(ref s_Fullscreen, value);
        }

        public static int ResolutionWidth { get { Ensure(); return s_Width; } }
        public static int ResolutionHeight { get { Ensure(); return s_Height; } }

        public static void SetResolution(int width, int height)
        {
            Ensure();
            if (width <= 0 || height <= 0) return;
            if (s_Width == width && s_Height == height) return;
            s_Width = width;
            s_Height = height;
            Apply();
            Changed?.Invoke();
        }

        public static bool VSync
        {
            get { Ensure(); return s_VSync; }
            set => Set(ref s_VSync, value);
        }

        /// <summary>Index into <see cref="QualitySettings.names"/>; the project's own level until changed.</summary>
        public static int QualityLevel
        {
            get { Ensure(); return s_Quality; }
            set => Set(ref s_Quality, Mathf.Clamp(value, 0, Mathf.Max(0, QualitySettings.names.Length - 1)));
        }

        /// <summary>The HUD's dev status lines. Off by default — chrome for the developer, not for the player.</summary>
        public static bool ShowHudStatus
        {
            get { Ensure(); return s_ShowStatus; }
            set => Set(ref s_ShowStatus, value);
        }

        static void Set<T>(ref T field, T value)
        {
            Ensure();
            if (Equals(field, value)) return;
            field = value;
            Apply();
            Changed?.Invoke();
        }

        static void Ensure()
        {
            if (!s_Loaded) Load();
        }

        /// <summary>
        /// Read the stored values, falling back to whatever the project is already set to — so a player who has
        /// never opened the menu gets the authored build, not a second opinion about resolution and quality.
        /// </summary>
        public static void Load()
        {
            s_Loaded = true; // set first: the fallbacks below would otherwise recurse through Ensure
            s_Master = PlayerPrefs.GetFloat(Prefix + "vol.master", s_Master);
            s_Music = PlayerPrefs.GetFloat(Prefix + "vol.music", s_Music);
            s_Sfx = PlayerPrefs.GetFloat(Prefix + "vol.sfx", s_Sfx);
            s_Voice = PlayerPrefs.GetFloat(Prefix + "vol.voice", s_Voice);
            s_Look = Mathf.Clamp(PlayerPrefs.GetFloat(Prefix + "look.sensitivity", s_Look), LookMin, LookMax);
            s_InvertY = PlayerPrefs.GetInt(Prefix + "look.invertY", 0) == 1;
            s_Fov = Mathf.Clamp(PlayerPrefs.GetFloat(Prefix + "view.fov", s_Fov), FovMin, FovMax);
            s_Fullscreen = PlayerPrefs.GetInt(Prefix + "screen.fullscreen", Screen.fullScreen ? 1 : 0) == 1;
            s_Width = PlayerPrefs.GetInt(Prefix + "screen.width", Screen.width);
            s_Height = PlayerPrefs.GetInt(Prefix + "screen.height", Screen.height);
            s_VSync = PlayerPrefs.GetInt(Prefix + "screen.vsync", QualitySettings.vSyncCount > 0 ? 1 : 0) == 1;
            s_Quality = PlayerPrefs.GetInt(Prefix + "screen.quality", QualitySettings.GetQualityLevel());
            s_ShowStatus = PlayerPrefs.GetInt(Prefix + "hud.status", 0) == 1;
        }

        public static void Save()
        {
            Ensure();
            PlayerPrefs.SetFloat(Prefix + "vol.master", s_Master);
            PlayerPrefs.SetFloat(Prefix + "vol.music", s_Music);
            PlayerPrefs.SetFloat(Prefix + "vol.sfx", s_Sfx);
            PlayerPrefs.SetFloat(Prefix + "vol.voice", s_Voice);
            PlayerPrefs.SetFloat(Prefix + "look.sensitivity", s_Look);
            PlayerPrefs.SetInt(Prefix + "look.invertY", s_InvertY ? 1 : 0);
            PlayerPrefs.SetFloat(Prefix + "view.fov", s_Fov);
            PlayerPrefs.SetInt(Prefix + "screen.fullscreen", s_Fullscreen ? 1 : 0);
            PlayerPrefs.SetInt(Prefix + "screen.width", s_Width);
            PlayerPrefs.SetInt(Prefix + "screen.height", s_Height);
            PlayerPrefs.SetInt(Prefix + "screen.vsync", s_VSync ? 1 : 0);
            PlayerPrefs.SetInt(Prefix + "screen.quality", s_Quality);
            PlayerPrefs.SetInt(Prefix + "hud.status", s_ShowStatus ? 1 : 0);
            PlayerPrefs.Save();
        }

        /// <summary>
        /// Push the values the engine itself owns, and only those it is safe to push from where we are. Outside
        /// play mode, v-sync and the quality level are the <i>project's</i> settings assets — writing them from a
        /// test or an edit-mode script is how a repository ends up with settings the editor rewrote on its own.
        /// Screen resolution is left alone in the whole editor: SetResolution there resizes the Game view and it
        /// sticks, which is a nasty thing to do to someone who was only dragging a slider.
        /// </summary>
        public static void Apply()
        {
            Ensure();
            AudioListener.volume = s_Master;
            if (!Application.isPlaying) return;
            QualitySettings.vSyncCount = s_VSync ? 1 : 0;
            if (s_Quality >= 0 && s_Quality < QualitySettings.names.Length
                && s_Quality != QualitySettings.GetQualityLevel())
                QualitySettings.SetQualityLevel(s_Quality);

            if (Application.isEditor) return;
            var mode = s_Fullscreen ? FullScreenMode.FullScreenWindow : FullScreenMode.Windowed;
            if (s_Width > 0 && s_Height > 0
                && (Screen.width != s_Width || Screen.height != s_Height || Screen.fullScreenMode != mode))
                Screen.SetResolution(s_Width, s_Height, mode);
        }

        /// <summary>
        /// Loaded and applied before the first scene runs, so settings hold on a cold boot without the menu ever
        /// being opened. Also clears <see cref="Changed"/>: with domain reload off, subscribers from the previous
        /// play session would still be attached to destroyed objects.
        /// </summary>
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        static void Boot()
        {
            Changed = null;
            s_Loaded = false;
            Load();
            Apply();
        }
    }
}
