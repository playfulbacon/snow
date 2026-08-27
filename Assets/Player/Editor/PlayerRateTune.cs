using System.IO;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

namespace SnowDays.EditorTools
{
    // One-shot, armed by the RATE_TUNE_PENDING marker: raises the scene
    // PlayerController's serialized m_MaxPlaybackRate to 3.5 (the scene's
    // stored 2 clamps sprint playback to 2x when speed/authored needs ~3.3x,
    // making planted feet slide and footprints misfire).
    [InitializeOnLoad]
    public static class PlayerRateTune
    {
        private const string MarkerPath = "Assets/Player/Editor/RATE_TUNE_PENDING.txt";

        static PlayerRateTune()
        {
            EditorApplication.delayCall += TryRun;
        }

        private static void TryRun()
        {
            if (!File.Exists(MarkerPath)) return;
            if (EditorApplication.isPlayingOrWillChangePlaymode || EditorApplication.isCompiling || EditorApplication.isUpdating)
            {
                EditorApplication.delayCall += TryRun;
                return;
            }

            try
            {
                Run();
            }
            catch (System.Exception e)
            {
                Debug.LogError("[PlayerRateTune] FAILED: " + e);
            }
            finally
            {
                if (!AssetDatabase.DeleteAsset(MarkerPath) && File.Exists(MarkerPath))
                {
                    File.Delete(MarkerPath);
                    File.Delete(MarkerPath + ".meta");
                    AssetDatabase.Refresh();
                }
            }
        }

        private static void Run()
        {
            var pc = Object.FindAnyObjectByType<PlayerController>(FindObjectsInactive.Include);
            if (pc == null)
            {
                Debug.LogError("[PlayerRateTune] No PlayerController in the open scene.");
                return;
            }

            var so = new SerializedObject(pc);
            SerializedProperty prop = so.FindProperty("m_MaxPlaybackRate");
            float old = prop.floatValue;
            prop.floatValue = 3.5f;
            so.ApplyModifiedPropertiesWithoutUndo();
            EditorSceneManager.MarkSceneDirty(pc.gameObject.scene);
            EditorSceneManager.SaveScene(pc.gameObject.scene);
            Debug.Log($"[PlayerRateTune] m_MaxPlaybackRate {old} -> 3.5, scene saved.");
        }
    }
}
