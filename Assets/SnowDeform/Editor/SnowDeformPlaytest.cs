using System.IO;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;

namespace SnowDays.EditorTools
{
    // One-shot visual check for snow deformation, armed by the
    // PLAYTEST_PENDING marker: enters play mode, takes over the player and
    // walks an L-shaped path through the snow (the L makes any axis flip in
    // the trample writes obvious), then captures two offscreen frames - a
    // high three-quarter view of the trail and a low close-up of the last
    // prints - and exits play mode.
    [InitializeOnLoad]
    public static class SnowDeformPlaytest
    {
        private const string MarkerPath = "Assets/SnowDeform/Editor/PLAYTEST_PENDING.txt";
        private const string FlagKey = "SnowDeformPlaytest.Armed";
        private const string WidePath = "Temp/snowdeform_wide.png";
        private const string ClosePath = "Temp/snowdeform_close.png";
        private const string TopPath = "Temp/snowdeform_top.png";

        private const float WalkSeconds = 7f;
        private const float WalkSpeed = 2.2f;
        private static double s_StartedAt;
        private static bool s_Ticking;
        private static Transform s_Player;
        private static CharacterController s_Controller;
        private static Animator s_Animator;
        private static Vector3 s_StartPos;
        private static Vector3 s_StartFwd;

        static SnowDeformPlaytest()
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

            var player = Object.FindAnyObjectByType<PlayerController>();
            if (player == null)
            {
                Debug.LogError("[SnowDeformPlaytest] No PlayerController in scene.");
                SessionState.SetBool(FlagKey, false);
                EditorApplication.ExitPlaymode();
                return;
            }
            // Drive the walk ourselves: no input, no cursor lock.
            player.enabled = false;
            s_Player = player.transform;
            s_Controller = player.GetComponent<CharacterController>();
            s_StartPos = s_Player.position;
            s_StartFwd = s_Player.forward;

            // Keep the rig stepping so footfalls stamp: play the walk cycle
            // via animator params, animate even while invisible, and hide
            // the body from the captures (shadow only) so it can't block
            // the close-up.
            s_Animator = player.GetComponentInChildren<Animator>();
            if (s_Animator != null)
                s_Animator.cullingMode = AnimatorCullingMode.AlwaysAnimate;
            foreach (var smr in player.GetComponentsInChildren<SkinnedMeshRenderer>())
                smr.shadowCastingMode = ShadowCastingMode.ShadowsOnly;

            s_Ticking = true;
            s_StartedAt = EditorApplication.timeSinceStartup + 1.0; // let systems bootstrap
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

            // Error Pause (or a stray pause) freezes the game loop while this
            // editor-side driver keeps running, silently voiding the test.
            if (EditorApplication.isPaused)
            {
                Debug.LogWarning("[SnowDeformPlaytest] Play mode was paused (Error Pause?); unpausing.");
                EditorApplication.isPaused = false;
            }

            double elapsed = EditorApplication.timeSinceStartup - s_StartedAt;
            if (elapsed < 0) return;

            if (elapsed < WalkSeconds)
            {
                // Three gaits along one line: walk forward, sprint forward,
                // then backpedal. Params mirror what PlayerController would
                // set with the scene's measured clip speeds (walk fwd
                // 2.5/1.029, run fwd 5.5/1.671, backpedal 1.875/1.533).
                float speed, moveY, norm, rate;
                if (elapsed < 3.0) { speed = 2.5f; moveY = 1f; norm = 1f; rate = 2.43f; }
                else if (elapsed < 5.0) { speed = 5.5f; moveY = 2f; norm = 2f; rate = 3.29f; }
                else { speed = -1.875f; moveY = -1f; norm = 1f; rate = 1.22f; }

                Vector3 move = s_Player.forward * speed + Vector3.down * 5f;
                s_Controller.Move(move * Time.deltaTime);
                if (s_Animator != null)
                {
                    s_Animator.SetFloat("MoveX", 0f);
                    s_Animator.SetFloat("MoveY", moveY);
                    s_Animator.SetFloat("Speed", norm);
                    s_Animator.SetFloat("LocomotionSpeed", rate);
                    s_Animator.SetBool("IsGrounded", true);
                }
                return;
            }

            EditorApplication.update -= Tick;
            s_Ticking = false;
            SessionState.SetBool(FlagKey, false);
            try
            {
                Capture();
            }
            catch (System.Exception e)
            {
                Debug.LogError("[SnowDeformPlaytest] Capture FAILED: " + e);
            }
            EditorApplication.ExitPlaymode();
        }

        private static void Capture()
        {
            // Daylight override (play-mode only, never saved): the scene is
            // night+fog, so captures force a daytime sun and flat cool
            // ambient to show the banded lighting and sun-gated sparkle.
            Light sun = null;
            foreach (var l in Object.FindObjectsByType<Light>(FindObjectsSortMode.None))
                if (l.type == LightType.Directional && (sun == null || l.intensity > sun.intensity)) sun = l;
            if (sun != null)
            {
                sun.intensity = 1.25f;
                sun.color = new Color(1f, 0.96f, 0.88f);
                sun.transform.rotation = Quaternion.Euler(45f, -30f, 0f);
            }
            RenderSettings.ambientMode = UnityEngine.Rendering.AmbientMode.Flat;
            RenderSettings.ambientLight = new Color(0.38f, 0.45f, 0.58f);

            var snow = Object.FindAnyObjectByType<SnowDeformSystem>();
            Debug.Log("[SnowDeformPlaytest] SnowDeformSystem present: " + (snow != null));
            if (snow != null)
            {
                Debug.Log($"[SnowDeformPlaytest] sys enabled={snow.isActiveAndEnabled} instance={SnowDeformSystem.Instance != null}");
                bool ok = snow.TryGetGroundHeight(s_Player.position.x, s_Player.position.z, out float gh);
                Debug.Log($"[SnowDeformPlaytest] groundSample ok={ok} h={gh:F2} playerY={s_Player.position.y:F2}");
                var rend = snow.GetComponentInChildren<MeshRenderer>(true);
                if (rend != null)
                {
                    var mesh = rend.GetComponent<MeshFilter>().sharedMesh;
                    Debug.Log($"[SnowDeformPlaytest] renderer enabled={rend.enabled} pos={rend.transform.position} " +
                        $"boundsCenter={rend.bounds.center} verts={(mesh != null ? mesh.vertexCount : -1)} " +
                        $"shader={rend.sharedMaterial.shader.name}");
                }
                else
                {
                    Debug.Log("[SnowDeformPlaytest] no MeshRenderer child under SnowDeformSystem");
                }
                CountTrampledTexels();
            }

            Camera cam = Camera.main;
            if (cam == null) throw new System.Exception("No main camera in play mode.");
            cam.transform.SetParent(null, true);

            // Wide: high three-quarter view back along the whole L.
            Vector3 mid = (s_StartPos + s_Player.position) * 0.5f;
            cam.transform.position = s_Player.position + s_Player.forward * 4f + Vector3.up * 6f;
            cam.transform.LookAt(mid + Vector3.up * 0.2f);
            CaptureTo(cam, WidePath);

            // Close: low and off to the side of the freshest prints, looking
            // back down the trail (the capsule sits dead center otherwise).
            cam.transform.position = s_Player.position + s_Player.forward * 2.2f
                + s_Player.right * 1.4f + Vector3.up * 1.1f;
            cam.transform.LookAt(s_Player.position - s_Player.forward * 1.5f + Vector3.up * 0.1f);
            CaptureTo(cam, ClosePath);

            // Straight down: shows the snow shell disc (or its absence)
            // against the bare terrain beyond its 36m extent.
            cam.transform.position = s_Player.position + Vector3.up * 50f;
            cam.transform.rotation = Quaternion.LookRotation(Vector3.down, s_Player.forward);
            CaptureTo(cam, TopPath);
        }

        // Reads back the center of the trample RT and counts texels with any
        // trampling - proves whether footstep stamps reached the GPU at all.
        private static void CountTrampledTexels()
        {
            var rt = Shader.GetGlobalTexture("_SnowTrampleTex") as RenderTexture;
            if (rt == null)
            {
                Debug.Log("[SnowDeformPlaytest] no global _SnowTrampleTex bound");
                return;
            }
            const int size = 512;
            int x0 = (rt.width - size) / 2, y0 = (rt.height - size) / 2;
            RenderTexture prev = RenderTexture.active;
            RenderTexture.active = rt;
            var tex = new Texture2D(size, size, TextureFormat.RGBAFloat, false);
            tex.ReadPixels(new Rect(x0, y0, size, size), 0, 0);
            tex.Apply();
            RenderTexture.active = prev;
            var px = tex.GetPixels();
            int trampled = 0;
            float peak = 0f;
            foreach (var c in px)
            {
                if (c.r > 0.05f) trampled++;
                if (c.r > peak) peak = c.r;
            }
            Object.DestroyImmediate(tex);
            Debug.Log($"[SnowDeformPlaytest] trample RT center {size}x{size}: {trampled} texels > 0.05, peak {peak:F2}");
        }

        private static void CaptureTo(Camera cam, string path)
        {
            const int w = 1280, h = 720;
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

            Directory.CreateDirectory(Path.GetDirectoryName(path));
            File.WriteAllBytes(path, tex.EncodeToPNG());
            Object.DestroyImmediate(tex);
            rt.Release();
            Object.DestroyImmediate(rt);
            Debug.Log("[SnowDeformPlaytest] Wrote frame to " + path);
        }
    }
}
