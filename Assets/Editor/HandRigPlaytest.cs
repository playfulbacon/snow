using System.IO;
using Snowfield.Player;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;

namespace SnowDays.EditorTools
{
    /// <summary>
    /// One-shot visual check for the stretchy arms, armed by the HANDRIG_PLAYTEST marker (recreate the file to
    /// re-arm; touch a .cs alongside it, markers alone do not force the domain reload). Enters play mode, takes
    /// the ball out of the field and into the character's hands the way the tool does, and captures the reach
    /// from three angles — first person high (ball at the carry anchor), first person low (ball rolling at the
    /// feet), and third person with the body mesh switched back on so the whole stretch is visible.
    ///
    /// The numbers in the log are the real verification: reached/natural per arm is the stretch factor the solver
    /// actually produced, and the hand-to-grip error says whether it got there. Captures are a night scene under
    /// a forced daytime sun, same as SnowDeformPlaytest.
    /// </summary>
    [InitializeOnLoad]
    public static class HandRigPlaytest
    {
        const string MarkerPath = "Assets/Editor/HANDRIG_PLAYTEST.txt";
        const string FlagKey = "HandRigPlaytest.Armed";
        const string PalmFp = "Temp/handrig_palm_fp.png";
        const string PalmThird = "Temp/handrig_palm_third.png";
        const string HugFp = "Temp/handrig_hug_fp.png";
        const string HugThird = "Temp/handrig_hug_third.png";
        const string RollFp = "Temp/handrig_roll_fp.png";
        const string RollThird = "Temp/handrig_roll_third.png";

        static bool s_Ticking;
        static double s_StartedAt;
        static int s_Stage;
        static PlayerController s_Player;
        static SculptTool s_Tool;
        static Animator s_Animator;
        static Camera s_ShotCamera;
        static CursorLockMode s_ObservedLock;
        static double s_StageAt;
        static double s_LiveAt = -1;

        static HandRigPlaytest()
        {
            EditorApplication.playModeStateChanged += OnPlayModeChanged;
            EditorApplication.delayCall += Init;
        }

        static void Init()
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

            if (SessionState.GetBool(FlagKey, false) && EditorApplication.isPlaying) Arm();
        }

        static void OnPlayModeChanged(PlayModeStateChange state)
        {
            if (state == PlayModeStateChange.EnteredPlayMode && SessionState.GetBool(FlagKey, false)) Arm();
        }

        static void Arm()
        {
            if (s_Ticking) return;
            s_Player = Object.FindAnyObjectByType<PlayerController>();
            s_Tool = Object.FindAnyObjectByType<SculptTool>();
            if (s_Player == null || s_Tool == null)
            {
                Debug.LogError("[HandRigPlaytest] No PlayerController/SculptTool in scene.");
                Finish();
                return;
            }
            // The editor only holds a cursor lock while the Game view has focus, and the tool does nothing
            // without one — so front the Game view before anything else.
            EditorApplication.ExecuteMenuItem("Window/General/Game");
            s_Animator = s_Player.GetComponentInChildren<Animator>();
            // Drive it ourselves: no look input, no walk, just the idle pose under the arms.
            s_Player.enabled = false;
            s_Ticking = true;
            s_Stage = 0;
            s_LiveAt = -1;
            s_StartedAt = EditorApplication.timeSinceStartup;
            s_StageAt = 0;
            EditorApplication.update += Tick;
        }

        static void Tick()
        {
            if (!EditorApplication.isPlaying) { Finish(); return; }
            // Error Pause freezes the game loop while this editor-side driver keeps running, voiding the test.
            if (EditorApplication.isPaused) EditorApplication.isPaused = false;

            // The tool only works in mouse-look mode. Read what the game loop actually sees before re-asserting:
            // a lock that will not stick means the capture is of an inert tool, and the report should say so.
            s_ObservedLock = Cursor.lockState;
            if (s_ObservedLock != CursorLockMode.Locked) Cursor.lockState = CursorLockMode.Locked;

            double t = EditorApplication.timeSinceStartup - s_StartedAt;
            // The tool swallows the frame the lock is (re)established on, and in the editor that can be several
            // seconds after play starts. Nothing is worth capturing until it reports an action, so wait for one.
            if (s_LiveAt < 0 && s_Tool.PrimaryAction != CursorAction.None) s_LiveAt = t;
            // The editor only holds the lock while the Game view has focus AND Unity is the frontmost app, so keep
            // asking until the tool responds.
            else if (s_LiveAt < 0) EditorApplication.ExecuteMenuItem("Window/General/Game");
            bool live = s_LiveAt >= 0 || t > 25; // hard timeout so a failure still produces evidence

            switch (s_Stage)
            {
                case 0 when t > 1.5:  // let SnowDeform find the player and fill its window
                    Daylight();
                    ScoopBall();
                    Advance(t);
                    break;
                case 1 when live && t - System.Math.Max(s_StageAt, s_LiveAt) > 1.5:  // ball has ridden up to the carry anchor, hands with it
                    Report("palm");   // a scooped handful: one hand
                    Shoot(PalmFp, PalmThird);
                    GrowBall(0.35f);  // past handTwoHandedRadius: the other hand should join
                    Advance(t);
                    break;
                case 2 when t - s_StageAt > 1.5:
                    Report("hug");
                    Shoot(HugFp, HugThird);
                    Pitch(52f);       // look down: the cursor lands near the feet, so the ball drops and rolls
                    Advance(t);
                    break;
                case 3 when t - s_StageAt > 2f:
                    Report("roll");
                    Shoot(RollFp, RollThird);
                    Finish();
                    EditorApplication.ExitPlaymode();
                    break;
            }
        }

        static void Advance(double t)
        {
            s_Stage++;
            s_StageAt = t;
        }

        /// <summary>Both views of the same moment: what the player sees, then the same pose from outside.</summary>
        static void Shoot(string fpPath, string thirdPath)
        {
            s_Player.IsLocal = true;          // hands/feet mesh on, body shadow-only
            Capture(PlayerCamera(), fpPath);
            ThirdPerson();                    // body mesh on, first-person mesh off
            Capture(s_ShotCamera, thirdPath);
        }

        static void Finish()
        {
            if (s_Ticking) EditorApplication.update -= Tick;
            s_Ticking = false;
            SessionState.SetBool(FlagKey, false);
        }

        static Camera PlayerCamera() => s_Player.GetComponentInChildren<Camera>(true);

        static void Pitch(float degrees)
        {
            Transform pivot = s_Player.transform.Find("CameraPivot");
            if (pivot != null) pivot.localRotation = Quaternion.Euler(degrees, 0f, 0f);
        }

        /// <summary>Put snow in the hands the way a ground scoop does, three metres out in front.</summary>
        static void ScoopBall()
        {
            var roller = s_Tool.Roller;
            Transform p = s_Player.transform;
            Vector3 ahead = p.position + p.forward * 1.5f;
            float y = SnowGround.Instance != null && SnowGround.Instance.IsCreated
                ? SnowGround.Instance.SampleHeight(ahead) : p.position.y;
            roller.ScoopFrom(new Vector3(ahead.x, y, ahead.z));
            Debug.Log($"[HandRigPlaytest] scooped: carrying={roller.IsCarrying} r={roller.HoldRadius:F2}");
        }

        /// <summary>Pack the handful up past the two-handed threshold so the other hand has to join in.</summary>
        static void GrowBall(float radius)
        {
            var ball = s_Tool.Roller.Ball;
            if (ball == null) { Debug.LogWarning("[HandRigPlaytest] nothing in hand to grow"); return; }
            ball.Grow(radius);
            ball.Sculpture.Remesh();
            Debug.Log($"[HandRigPlaytest] grew ball to r={ball.radius:F2}");
        }

        /// <summary>What the solver actually did, per arm — the part of this test that does not depend on pixels.</summary>
        static void Report(string label)
        {
            var rig = s_Player.GetComponent<HandRig>();
            Debug.Log($"[HandRigPlaytest] {label}: lockSeenByGame={s_ObservedLock} ready={(rig != null && rig.IsReady)} " +
                      $"carrying={s_Tool.Roller.IsCarrying} r={s_Tool.Roller.HoldRadius:F2} action={s_Tool.PrimaryAction}");
            if (rig == null || !rig.IsReady || s_Animator == null) return;

            // Where the held snow actually lands on the player's own screen: 0..1 across the frame, so anything
            // outside [0,1] is snow the owner cannot see.
            Camera cam = PlayerCamera();
            if (cam != null && s_Tool.Roller.IsCarrying)
            {
                Vector3 v = cam.WorldToViewportPoint(s_Tool.Roller.HoldCentre);
                Debug.Log($"[HandRigPlaytest] {label} on screen: x={v.x:F2} y={v.y:F2} depth={v.z:F2}m " +
                          $"({(v.z > 0f && v.x > 0f && v.x < 1f && v.y > 0f && v.y < 1f ? "VISIBLE" : "off frame")})");
            }
            Measure(label, "L", HumanBodyBones.LeftUpperArm, HumanBodyBones.LeftLowerArm, HumanBodyBones.LeftHand);
            Measure(label, "R", HumanBodyBones.RightUpperArm, HumanBodyBones.RightLowerArm, HumanBodyBones.RightHand);
        }

        static void Measure(string label, string side, HumanBodyBones upper, HumanBodyBones lower, HumanBodyBones hand)
        {
            Transform u = s_Animator.GetBoneTransform(upper);
            Transform l = s_Animator.GetBoneTransform(lower);
            Transform h = s_Animator.GetBoneTransform(hand);
            if (u == null || l == null || h == null) return;
            float span = Vector3.Distance(u.position, l.position) + Vector3.Distance(l.position, h.position);
            float toHold = Vector3.Distance(h.position, s_Tool.Roller.HoldCentre);
            float feet = s_Player.transform.position.y;
            Debug.Log($"[HandRigPlaytest] {label} {side}: armSpan={span:F3}m shoulder->hand={Vector3.Distance(u.position, h.position):F3}m " +
                      $"hand->holdCentre={toHold:F3}m (ball r={s_Tool.Roller.HoldRadius:F2}) " +
                      $"shoulderY={u.position.y - feet:F2} ballY={s_Tool.Roller.HoldCentre.y - feet:F2} above feet");
        }

        /// <summary>Show the body mesh (it is shadow-only for its owner) and frame the character from the front.</summary>
        static void ThirdPerson()
        {
            s_Player.IsLocal = false; // play-mode only; never saved
            if (s_ShotCamera == null)
            {
                var go = new GameObject("HandRigShotCamera");
                s_ShotCamera = go.AddComponent<Camera>();
                s_ShotCamera.CopyFrom(PlayerCamera());
                s_ShotCamera.enabled = false; // only ever rendered through a render request
            }
            Transform p = s_Player.transform;
            Vector3 focus = p.position + Vector3.up * 1.3f;
            s_ShotCamera.transform.position = focus + p.forward * 3.2f + p.right * 1.6f + Vector3.up * 0.5f;
            s_ShotCamera.transform.LookAt(focus);
        }

        /// <summary>Main is a night scene; force a daytime sun so the captures show shape rather than fog.</summary>
        static void Daylight()
        {
            Light sun = null;
            foreach (var l in Object.FindObjectsByType<Light>(FindObjectsSortMode.None))
                if (l.type == LightType.Directional && (sun == null || l.intensity > sun.intensity)) sun = l;
            if (sun != null)
            {
                sun.intensity = 1.25f;
                sun.color = new Color(1f, 0.96f, 0.88f);
                sun.transform.rotation = Quaternion.Euler(45f, -30f, 0f);
            }
            RenderSettings.ambientMode = AmbientMode.Flat;
            RenderSettings.ambientLight = new Color(0.38f, 0.45f, 0.58f);
        }

        static void Capture(Camera cam, string path)
        {
            if (cam == null) { Debug.LogError("[HandRigPlaytest] no camera for " + path); return; }
            const int w = 1280, h = 720;
            var rt = new RenderTexture(w, h, 24, RenderTextureFormat.ARGB32);
            var request = new RenderPipeline.StandardRequest { destination = rt };
            if (!RenderPipeline.SupportsRenderRequest(cam, request))
            {
                Debug.LogError("[HandRigPlaytest] StandardRequest unsupported.");
                return;
            }
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
            Debug.Log("[HandRigPlaytest] Wrote " + path);
        }
    }
}
