using System.IO;
using UnityEditor;
using UnityEngine;

namespace SnowDays.EditorTools
{
    // Compile check for the snow deformation shaders. Import success alone
    // doesn't prove variants compile, so this forces every pass of every
    // shader to build and logs any shader messages. Runs one-shot when the
    // VERIFY_PENDING marker exists (re-arm by recreating it), or headless via
    // -executeMethod SnowDays.EditorTools.SnowDeformVerify.RunBatch.
    [InitializeOnLoad]
    public static class SnowDeformVerify
    {
        private const string MarkerPath = "Assets/SnowDeform/Editor/VERIFY_PENDING.txt";

        private static readonly string[] ShaderPaths =
        {
            "Assets/SnowDeform/Resources/SnowSurface.shader",
            "Assets/SnowDeform/Resources/SnowStamp.shader",
            "Assets/SnowDeform/Resources/SnowMaintenance.shader",
        };

        static SnowDeformVerify()
        {
            EditorApplication.delayCall += TryRun;
        }

        public static void RunBatch()
        {
            if (!Run()) throw new System.Exception("Snow deformation shaders have errors; see log above.");
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
                Debug.LogError("[SnowDeformVerify] FAILED: " + e);
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
            bool ok = true;
            foreach (string path in ShaderPaths)
                ok &= VerifyShader(path);
            if (ok)
                Debug.Log("[SnowDeformVerify] OK: all snow shaders imported and every pass compiled with no errors.");
            return ok;
        }

        private static bool VerifyShader(string path)
        {
            AssetDatabase.ImportAsset(path, ImportAssetOptions.ForceUpdate);
            var shader = AssetDatabase.LoadAssetAtPath<Shader>(path);
            if (shader == null)
            {
                Debug.LogError("[SnowDeformVerify] Shader asset not found at " + path);
                return false;
            }

            if (LogMessages(shader, "import")) return false;

            var mat = new Material(shader);
            try
            {
                int passCount = shader.passCount;
                for (int i = 0; i < passCount; i++)
                    ShaderUtil.CompilePass(mat, i, true);
            }
            finally
            {
                Object.DestroyImmediate(mat);
            }

            if (LogMessages(shader, "variant compile")) return false;

            Debug.Log($"[SnowDeformVerify] {Path.GetFileName(path)}: {shader.passCount} pass(es) compiled clean.");
            return true;
        }

        private static bool LogMessages(Shader shader, string stage)
        {
            bool hasError = ShaderUtil.ShaderHasError(shader);
            var messages = ShaderUtil.GetShaderMessages(shader);
            foreach (var m in messages)
                Debug.LogError($"[SnowDeformVerify] {shader.name} {stage} {m.severity}: {m.message} ({m.file}:{m.line}) [{m.platform}]");
            if (hasError && messages.Length == 0)
                Debug.LogError($"[SnowDeformVerify] {shader.name} {stage}: shader has errors but no messages were retrieved.");
            return hasError;
        }
    }
}
