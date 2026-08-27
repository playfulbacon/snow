using System.IO;
using Snowfield.Sculpture;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

namespace Snowfield.Editor
{
    /// <summary>
    /// Renders the Main scene to a PNG without entering play mode, so the look can be checked
    /// from a batchmode run (needs graphics: omit -nographics).
    /// `Unity -batchmode -executeMethod Snowfield.Editor.HeadlessScreenshot.Run -screenshotOut path.png`
    /// The scene's player camera is used as-is; pass -screenshotDemo to also stamp a demo mound at the
    /// player's feet so sculpture rendering shows up in the shot.
    /// </summary>
    public static class HeadlessScreenshot
    {
        const string ScenePath = "Assets/Scenes/Main.unity";

        [MenuItem("Snowfield/Headless Screenshot")]
        public static void Run()
        {
            string outPath = "Screenshots/main.png";
            bool demo = false;
            var args = System.Environment.GetCommandLineArgs();
            for (int i = 0; i < args.Length; i++)
            {
                if (args[i] == "-screenshotOut" && i + 1 < args.Length) outPath = args[i + 1];
                if (args[i] == "-screenshotDemo") demo = true;
            }
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(outPath)));

            EditorSceneManager.OpenScene(ScenePath, OpenSceneMode.Single);

            // Start() does not run in edit mode, so initialise any scene sculptures by hand.
            foreach (var s in Object.FindObjectsByType<SnowSculpture>(FindObjectsSortMode.None))
            {
                s.Initialise(s.Config.gridSize, s.Config.voxelSize);
                var spawner = s.GetComponent<SculptureSpawner>();
                if (spawner != null) spawner.SpawnNow();
            }
            if (demo)
            {
                var factory = Object.FindAnyObjectByType<SculptureFactory>();
                if (factory != null)
                {
                    // Place the mound a couple of metres in front of the camera so it is in the shot.
                    var cam0 = Object.FindAnyObjectByType<Camera>();
                    Vector3 at = cam0 != null
                        ? cam0.transform.position + Vector3.ProjectOnPlane(cam0.transform.forward, Vector3.up).normalized * 2.5f
                            + Vector3.down * 1.5f
                        : Vector3.zero;
                    var mound = factory.CreateAt(at);
                    if (mound.Grid == null) mound.Initialise(factory.config.gridSize, factory.config.voxelSize);
                    mound.StampSphere(at + Vector3.down * 0.2f, 0.8f, 0.7f, clipBelowWorldY: at.y);
                    mound.Remesh();
                }
            }

            var cam = Camera.main;
            if (cam == null) cam = Object.FindAnyObjectByType<Camera>(); // the player camera is untagged
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
            EditorSceneManager.OpenScene(ScenePath, OpenSceneMode.Single);
            if (Application.isBatchMode) EditorApplication.Exit(0);
        }
    }
}
