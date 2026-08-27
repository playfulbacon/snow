using System.IO;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;

namespace SnowDays.EditorTools
{
    // One-shot visual check, armed by the PLAYTEST_PENDING marker: enters
    // play mode, lets the snowfall bootstrap and run for a few seconds,
    // renders the main camera offscreen to a PNG, then exits play mode.
    // Offscreen StandardRequest renders scene geometry (snow included) but
    // may cull fullscreen passes, so this proves the snow - not the post.
    [InitializeOnLoad]
    public static class SnowfallPlaytest
    {
        private const string MarkerPath = "Assets/Snowfall/Editor/PLAYTEST_PENDING.txt";
        private const string FlagKey = "SnowfallPlaytest.Armed";
        private const string OutputPath = "Temp/snowfall_frame.png";

        private static double s_CaptureAt;
        private static bool s_Ticking;

        static SnowfallPlaytest()
        {
            EditorApplication.playModeStateChanged += OnPlayModeChanged;
            EditorApplication.delayCall += Init;
        }

        private static void Init()
        {
            if (File.Exists(MarkerPath))
            {
                if (EditorApplication.isCompiling || EditorApplication.isUpdating)
                {
                    EditorApplication.delayCall += Init;
                    return;
                }
                if (!AssetDatabase.DeleteAsset(MarkerPath) && File.Exists(MarkerPath))
                {
                    File.Delete(MarkerPath);
                    File.Delete(MarkerPath + ".meta");
                }
                SessionState.SetBool(FlagKey, true);
                if (EditorApplication.isPlaying) Arm();
                else EditorApplication.EnterPlaymode();
                return;
            }

            // Domain-reload-into-play path: the cctor re-ran, pick up the flag.
            if (SessionState.GetBool(FlagKey, false) && EditorApplication.isPlaying)
                Arm();
        }

        private static void OnPlayModeChanged(PlayModeStateChange state)
        {
            if (state == PlayModeStateChange.EnteredPlayMode && SessionState.GetBool(FlagKey, false))
                Arm();
        }

        private static void Arm()
        {
            if (s_Ticking) return;
            s_Ticking = true;
            s_CaptureAt = EditorApplication.timeSinceStartup + 3.0;
            EditorApplication.update += Tick;
        }

        private static void Tick()
        {
            if (!EditorApplication.isPlaying)
            {
                EditorApplication.update -= Tick;
                s_Ticking = false;
                SessionState.SetBool(FlagKey, false);
                return;
            }
            if (EditorApplication.timeSinceStartup < s_CaptureAt) return;

            EditorApplication.update -= Tick;
            s_Ticking = false;
            SessionState.SetBool(FlagKey, false);
            try
            {
                Capture();
            }
            catch (System.Exception e)
            {
                Debug.LogError("[SnowfallPlaytest] Capture FAILED: " + e);
            }
            EditorApplication.ExitPlaymode();
        }

        private static void Capture()
        {
            Camera cam = Camera.main;
            if (cam == null) throw new System.Exception("No main camera in play mode.");

            var snow = Object.FindAnyObjectByType<SnowfallSystem>();
            Debug.Log("[SnowfallPlaytest] SnowfallSystem present: " + (snow != null));

            // Match the GameCubeLook 480p internal render so captures show
            // the pixel flakes at their real chunkiness.
            const int w = 854, h = 480;
            var rt = new RenderTexture(w, h, 24, RenderTextureFormat.ARGB32);
            var request = new RenderPipeline.StandardRequest();
            if (!RenderPipeline.SupportsRenderRequest(cam, request))
                throw new System.Exception("StandardRequest unsupported.");
            request.destination = rt;
            RenderPipeline.SubmitRenderRequest(cam, request);

            RenderTexture prev = RenderTexture.active;
            RenderTexture.active = rt;
            var tex = new Texture2D(w, h, TextureFormat.RGBA32, false);
            tex.ReadPixels(new Rect(0, 0, w, h), 0, 0);
            tex.Apply();
            RenderTexture.active = prev;

            Directory.CreateDirectory(Path.GetDirectoryName(OutputPath));
            File.WriteAllBytes(OutputPath, tex.EncodeToPNG());
            Object.DestroyImmediate(tex);
            rt.Release();
            Object.DestroyImmediate(rt);
            Debug.Log("[SnowfallPlaytest] Wrote frame to " + OutputPath);
        }
    }
}
