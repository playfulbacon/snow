using System;
using UnityEngine;

namespace Snowfield.Config
{
    /// <summary>
    /// Whether the game is sitting behind a menu, and the two engine-level consequences of that: the cursor is
    /// free, and (single-player only) time stops.
    ///
    /// It is a static in Config so the things that must stand still can ask without knowing what put the menu on
    /// screen. The player controller in particular has to: it owns Esc and the cursor lock, and left-click
    /// re-locks the cursor — which would snatch the mouse away the moment you clicked a menu button.
    ///
    /// Time is frozen only when <c>freezeTime</c> is asked for. A shared field cannot be paused — the other
    /// players keep sculpting — so in a session the menu opens over a world that is still running, and the
    /// caller is the one who knows which it is.
    /// </summary>
    public static class GamePause
    {
        public static bool IsPaused { get; private set; }

        /// <summary>Raised with the new state, after the cursor and the clock have been dealt with.</summary>
        public static event Action<bool> Changed;

        static bool s_Froze;

        public static void Set(bool paused, bool freezeTime = true)
        {
            if (paused == IsPaused) return;
            IsPaused = paused;

            if (paused)
            {
                s_Froze = freezeTime;
                if (freezeTime) Time.timeScale = 0f;
                Cursor.lockState = CursorLockMode.None;
                Cursor.visible = true;
            }
            else
            {
                // Only undo a freeze we caused: something else may legitimately own timeScale (a cutscene, a test).
                if (s_Froze) Time.timeScale = 1f;
                s_Froze = false;
                Cursor.lockState = CursorLockMode.Locked;
                Cursor.visible = false;
            }

            Changed?.Invoke(paused);
        }

        /// <summary>
        /// A play session must never inherit the last one's pause. With domain reload off the statics survive,
        /// and a timeScale of 0 left behind by a stopped play session is a frozen game with no menu on it.
        /// </summary>
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        static void Boot()
        {
            Changed = null;
            if (IsPaused && s_Froze) Time.timeScale = 1f;
            IsPaused = false;
            s_Froze = false;
        }
    }
}
