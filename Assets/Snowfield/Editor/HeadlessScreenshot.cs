using System.IO;
using Snowfield.Sculpture;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

namespace Snowfield.Editor
{
    /// <summary>
    /// Renders the Sandbox scene's sculpture to a PNG without entering play mode, so the look can be checked
    /// from a batchmode run (needs graphics: omit -nographics).
    /// `Unity -batchmode -executeMethod Snowfield.Editor.HeadlessScreenshot.Run -screenshotOut path.png`
    /// </summary>
    public static class HeadlessScreenshot
    {
        [MenuItem("Snowfield/Headless Screenshot")]
        public static void Run()
        {
            string outPath = "Screenshots/sandbox.png";
            var args = System.Environment.GetCommandLineArgs();
            for (int i = 0; i < args.Length - 1; i++) if (args[i] == "-screenshotOut") outPath = args[i + 1];
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(outPath)));

            EditorSceneManager.OpenScene("Assets/Scenes/Sandbox.unity", OpenSceneMode.Single);

            // Start() does not run in edit mode, so drive the sculpture by hand.
            foreach (var s in Object.FindObjectsByType<SnowSculpture>(FindObjectsSortMode.None))
            {
                s.Initialise(s.Config.gridSize, s.Config.voxelSize);
                var spawner = s.GetComponent<SculptureSpawner>();
                if (spawner != null) spawner.SpawnNow();
            }

            var cam = Camera.main;
            var rt = new RenderTexture(1280, 720, 24, RenderTextureFormat.ARGB32);
            cam.targetTexture = rt;
            cam.Render();
            var tex = new Texture2D(rt.width, rt.height, TextureFormat.RGB24, false);
            RenderTexture.active = rt;
            tex.ReadPixels(new Rect(0, 0, rt.width, rt.height), 0, 0);
            tex.Apply();
            RenderTexture.active = null;
            cam.targetTexture = null;
            File.WriteAllBytes(outPath, tex.EncodeToPNG());
            Object.DestroyImmediate(tex);
            Object.DestroyImmediate(rt);
            Debug.Log($"[Snowfield] Screenshot written to {Path.GetFullPath(outPath)}");

            // Leave the scene untouched on disk.
            EditorSceneManager.OpenScene("Assets/Scenes/Sandbox.unity", OpenSceneMode.Single);
            if (Application.isBatchMode) EditorApplication.Exit(0);
        }
    }
}
