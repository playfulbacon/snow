using System.IO;
using UnityEditor;
using UnityEngine;

namespace SnowDays.EditorTools
{
    // Compile check for the snowfall shader. Import success alone doesn't
    // prove variants compile, so this forces pass 0 to build and logs any
    // shader messages. Runs one-shot when the VERIFY_PENDING marker exists
    // (re-arm by recreating it), or headless via -executeMethod
    // SnowDays.EditorTools.SnowfallVerify.RunBatch.
    [InitializeOnLoad]
    public static class SnowfallVerify
    {
        private const string MarkerPath = "Assets/Snowfall/Editor/VERIFY_PENDING.txt";
        private const string ShaderPath = "Assets/Snowfall/Resources/RetroSnow.shader";

        static SnowfallVerify()
        {
            EditorApplication.delayCall += TryRun;
        }

        // Entry point for -executeMethod; throws on failure so the batch run
        // exits nonzero.
        public static void RunBatch()
        {
            if (!Run()) throw new System.Exception("RetroSnow shader has errors; see log above.");
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
                Debug.LogError("[SnowfallVerify] FAILED: " + e);
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

        private static bool Run()
        {
            AssetDatabase.ImportAsset(ShaderPath, ImportAssetOptions.ForceUpdate);
            var shader = AssetDatabase.LoadAssetAtPath<Shader>(ShaderPath);
            if (shader == null)
            {
                Debug.LogError("[SnowfallVerify] Shader asset not found at " + ShaderPath);
                return false;
            }

            if (LogMessages(shader, "import")) return false;

            var mat = new Material(shader);
            try
            {
                ShaderUtil.CompilePass(mat, 0, true);
            }
            finally
            {
                Object.DestroyImmediate(mat);
            }

            if (LogMessages(shader, "variant compile")) return false;

            Debug.Log("[SnowfallVerify] OK: RetroSnow imported and pass 0 compiled with no errors.");
            return true;
        }

        private static bool LogMessages(Shader shader, string stage)
        {
            bool hasError = ShaderUtil.ShaderHasError(shader);
            var messages = ShaderUtil.GetShaderMessages(shader);
            foreach (var m in messages)
                Debug.LogError($"[SnowfallVerify] {stage} {m.severity}: {m.message} ({m.file}:{m.line}) [{m.platform}]");
            if (hasError && messages.Length == 0)
                Debug.LogError($"[SnowfallVerify] {stage}: shader has errors but no messages were retrieved.");
            return hasError;
        }
    }
}
