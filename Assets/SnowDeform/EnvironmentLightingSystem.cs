using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.SceneManagement;

namespace SnowDays
{
    /// <summary>
    /// Keeps the skybox ambient probe in sync with environment settings.
    /// Unity 6 does not regenerate it automatically when lighting changes.
    /// Both Terrain/Lit and the deformable snow sample this probe.
    /// </summary>
    public sealed class EnvironmentLightingSystem : MonoBehaviour
    {
        private const double PollInterval = 0.25;
        private static double s_NextPoll;
        private static bool s_HasSnapshot;
        private static int s_SceneHandle;
        private static AmbientMode s_Mode;
        private static Material s_Skybox;
        private static int s_SkyboxCrc;
        private static float s_Intensity;

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        public static void Invalidate()
        {
            s_HasSnapshot = false;
            s_NextPoll = 0;
        }

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        private static void Bootstrap()
        {
            if (FindAnyObjectByType<EnvironmentLightingSystem>() != null) return;
            var go = new GameObject("EnvironmentLighting");
            DontDestroyOnLoad(go);
            go.AddComponent<EnvironmentLightingSystem>();
        }

        private void OnEnable()
        {
            Invalidate();
            SceneManager.sceneLoaded += OnSceneLoaded;
        }

        private void OnDisable()
        {
            SceneManager.sceneLoaded -= OnSceneLoaded;
        }

        private void OnSceneLoaded(Scene scene, LoadSceneMode mode) => Invalidate();

        private void Update() => RefreshIfChanged(Time.realtimeSinceStartupAsDouble);

        // Also driven by the editor so the Lighting window has a live preview.
        // Rate limiting avoids queuing a GPU skybox readback every frame while
        // dragging the intensity slider or animating a skybox material.
        public static bool RefreshIfChanged(double now)
        {
            if (now < s_NextPoll) return false;
            s_NextPoll = now + PollInterval;

            int sceneHandle = SceneManager.GetActiveScene().handle;
            AmbientMode mode = RenderSettings.ambientMode;
            Material skybox = RenderSettings.skybox;
            int skyboxCrc = skybox != null ? skybox.ComputeCRC() : 0;
            float intensity = RenderSettings.ambientIntensity;
            bool changed = !s_HasSnapshot || sceneHandle != s_SceneHandle ||
                mode != s_Mode || skybox != s_Skybox || skyboxCrc != s_SkyboxCrc ||
                intensity != s_Intensity;

            s_HasSnapshot = true;
            s_SceneHandle = sceneHandle;
            s_Mode = mode;
            s_Skybox = skybox;
            s_SkyboxCrc = skyboxCrc;
            s_Intensity = intensity;

            // Flat and gradient ambient colors are already updated by Unity.
            if (!changed || mode != AmbientMode.Skybox || skybox == null) return false;
            DynamicGI.UpdateEnvironment();
            return true;
        }
    }
}
