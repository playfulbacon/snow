using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine.SceneManagement;

namespace SnowDays.EditorTools
{
    [InitializeOnLoad]
    public static class EnvironmentLightingEditor
    {
        static EnvironmentLightingEditor()
        {
            EnvironmentLightingSystem.Invalidate();
            EditorApplication.update += Update;
            EditorApplication.playModeStateChanged += OnPlayModeChanged;
            EditorSceneManager.sceneOpened += OnSceneOpened;
            Lightmapping.bakeCompleted += EnvironmentLightingSystem.Invalidate;
        }

        private static void OnPlayModeChanged(PlayModeStateChange state)
            => EnvironmentLightingSystem.Invalidate();

        private static void OnSceneOpened(Scene scene, OpenSceneMode mode)
            => EnvironmentLightingSystem.Invalidate();

        private static void Update()
        {
            if (EditorApplication.isPlayingOrWillChangePlaymode ||
                EditorApplication.isCompiling || EditorApplication.isUpdating ||
                Lightmapping.isRunning || !SceneManager.GetActiveScene().isLoaded)
                return;

            if (EnvironmentLightingSystem.RefreshIfChanged(EditorApplication.timeSinceStartup))
            {
                EditorApplication.QueuePlayerLoopUpdate();
                SceneView.RepaintAll();
            }
        }
    }
}
