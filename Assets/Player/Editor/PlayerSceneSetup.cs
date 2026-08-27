using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEditor.Animations;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Rendering.Universal;
using UnityEngine.SceneManagement;

namespace SnowDays.EditorTools
{
    // One-shot scene setup, armed by the SETUP_PENDING marker; re-arm by recreating the marker file.
    [InitializeOnLoad]
    public static class PlayerSceneSetup
    {
        private const string MarkerPath = "Assets/Player/Editor/SETUP_PENDING.txt";
        private const string ScenePath = "Assets/Scenes/Main.unity";
        private const string ModelFbxPath = "Assets/CharacterTest/base_basic_shaded.fbx";
        private const string AnimSetPath = "Assets/ithappy/Creative_Characters_FREE/Animations/Animation_Mesh/Aminset_Basic.fbx";
        private const string ControllerPath = "Assets/Player/FirstPersonPlayer.controller";

        static PlayerSceneSetup()
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
                Debug.Log("[PlayerSceneSetup] Player setup complete. Enter play mode to test (WASD, mouse, LeftShift run, Space jump, Esc frees cursor).");
            }
            catch (System.Exception e)
            {
                Debug.LogError("[PlayerSceneSetup] Setup failed: " + e);
            }
            finally
            {
                if (!AssetDatabase.DeleteAsset(MarkerPath) && File.Exists(MarkerPath))
                {
                    File.Delete(MarkerPath);
                    File.Delete(MarkerPath + ".meta");
                    AssetDatabase.Refresh();
                }
                AssetDatabase.SaveAssets();
            }
        }

        private static void Run()
        {
            MakeHumanoid(ModelFbxPath);
            AnimatorController controller = BuildController(out AuthoredSpeeds authoredSpeeds);

            Scene scene = SceneManager.GetActiveScene();
            if (scene.path != ScenePath)
            {
                if (!EditorSceneManager.SaveCurrentModifiedScenesIfUserWantsTo()) throw new System.Exception("Scene switch cancelled.");
                scene = EditorSceneManager.OpenScene(ScenePath, OpenSceneMode.Single);
            }

            FindPlayerObjects(scene, out GameObject playerRoot, out GameObject model);
            if (model == null) throw new System.Exception($"No instance of {ModelFbxPath} found in {ScenePath}.");

            if (playerRoot == null)
            {
                playerRoot = new GameObject("Player");
                playerRoot.transform.SetPositionAndRotation(
                    model.transform.position,
                    Quaternion.Euler(0f, model.transform.eulerAngles.y, 0f));
            }

            if (model.transform.parent != playerRoot.transform)
            {
                model.name = "Model";
                model.transform.SetParent(playerRoot.transform, true);
            }
            model.transform.localPosition = Vector3.zero;
            model.transform.localRotation = Quaternion.identity;

            float height = SetupCharacterController(playerRoot, model);
            Transform pivot = SetupCameraRig(playerRoot, height);
            Animator animator = SetupAnimator(model, controller);
            FindMeshes(model, out SkinnedMeshRenderer body, out SkinnedMeshRenderer firstPerson);
            CopyMaterialOverrides(body, firstPerson);
            WireController(playerRoot, pivot, animator, body, firstPerson, authoredSpeeds);

            EditorSceneManager.MarkSceneDirty(scene);
            EditorSceneManager.SaveScene(scene);
        }

        private static void MakeHumanoid(string fbxPath)
        {
            var importer = AssetImporter.GetAtPath(fbxPath) as ModelImporter;
            if (importer == null) throw new System.Exception("Missing model at " + fbxPath);
            if (importer.animationType == ModelImporterAnimationType.Human) return;

            importer.animationType = ModelImporterAnimationType.Human;
            importer.avatarSetup = ModelImporterAvatarSetup.CreateFromThisModel;
            importer.SaveAndReimport();
        }

        private struct AuthoredSpeeds
        {
            public float walkForward, walkBackward, walkStrafe;
            public float runForward, runBackward, runStrafe;
        }

        private static AnimatorController BuildController(out AuthoredSpeeds authoredSpeeds)
        {
            Dictionary<string, AnimationClip> clips = AssetDatabase.LoadAllAssetsAtPath(AnimSetPath)
                .OfType<AnimationClip>()
                .Where(c => !c.name.StartsWith("__preview__"))
                .GroupBy(c => c.name)
                .ToDictionary(g => g.Key, g => g.First());

            AnimationClip Clip(string name)
            {
                if (!clips.TryGetValue(name, out AnimationClip clip)) throw new System.Exception($"Clip '{name}' not found in {AnimSetPath}.");
                return clip;
            }

            AssetDatabase.DeleteAsset(ControllerPath);
            AnimatorController controller = AnimatorController.CreateAnimatorControllerAtPath(ControllerPath);
            controller.AddParameter("MoveX", AnimatorControllerParameterType.Float);
            controller.AddParameter("MoveY", AnimatorControllerParameterType.Float);
            controller.AddParameter("Speed", AnimatorControllerParameterType.Float);
            controller.AddParameter("LocomotionSpeed", AnimatorControllerParameterType.Float);
            controller.AddParameter("IsGrounded", AnimatorControllerParameterType.Bool);
            controller.AddParameter("Jump", AnimatorControllerParameterType.Trigger);

            AnimatorControllerParameter[] parameters = controller.parameters;
            parameters.First(p => p.name == "IsGrounded").defaultBool = true;
            // Default 1 keeps non-local players animating at authored rate; the owner drives it per frame.
            parameters.First(p => p.name == "LocomotionSpeed").defaultFloat = 1f;
            controller.parameters = parameters;

            float PlanarSpeed(AnimationClip c)
            {
                Vector3 v = c.averageSpeed;
                return new Vector2(v.x, v.z).magnitude;
            }
            authoredSpeeds = new AuthoredSpeeds
            {
                walkForward = PlanarSpeed(Clip("Walk_Forward")),
                walkBackward = PlanarSpeed(Clip("Run_Backward")),
                walkStrafe = PlanarSpeed(Clip("Run_Left")),
                runForward = PlanarSpeed(Clip("Run_Forward")),
                runBackward = PlanarSpeed(Clip("Run_Backward")),
                runStrafe = PlanarSpeed(Clip("Run_Left")),
            };

            AnimatorStateMachine sm = controller.layers[0].stateMachine;

            var tree = new BlendTree
            {
                name = "Locomotion",
                blendType = BlendTreeType.FreeformDirectional2D,
                blendParameter = "MoveX",
                blendParameterY = "MoveY",
                hideFlags = HideFlags.HideInHierarchy,
            };
            AssetDatabase.AddObjectToAsset(tree, controller);
            // Walk-ring back/side slots use run-gait clips: their authored speeds are near travel speed,
            // so playback scaling stays ~1x instead of triple-speeding the 0.8 m/s shuffle clips.
            tree.AddChild(Clip("Idle_Breathing"), Vector2.zero);
            tree.AddChild(Clip("Walk_Forward"), new Vector2(0f, 1f));
            tree.AddChild(Clip("Run_Backward"), new Vector2(0f, -1f));
            tree.AddChild(Clip("Run_Left"), new Vector2(-1f, 0f));
            tree.AddChild(Clip("Run_Right"), new Vector2(1f, 0f));
            tree.AddChild(Clip("Run_Forward"), new Vector2(0f, 2f));
            tree.AddChild(Clip("Run_Backward"), new Vector2(0f, -2f));
            tree.AddChild(Clip("Run_Left"), new Vector2(-2f, 0f));
            tree.AddChild(Clip("Run_Right"), new Vector2(2f, 0f));

            AnimatorState locomotion = sm.AddState("Locomotion", new Vector3(280f, 120f));
            locomotion.motion = tree;
            locomotion.speedParameter = "LocomotionSpeed";
            locomotion.speedParameterActive = true;
            AnimatorState jumpStart = sm.AddState("Jump_Start", new Vector3(560f, 40f));
            jumpStart.motion = Clip("Jump_Start");
            AnimatorState jumpLoop = sm.AddState("Jump_Loop", new Vector3(560f, 120f));
            jumpLoop.motion = Clip("Jump_Loop");
            AnimatorState jumpEnd = sm.AddState("Jump_End", new Vector3(560f, 200f));
            jumpEnd.motion = Clip("Jump_End");
            sm.defaultState = locomotion;

            AnimatorStateTransition T(AnimatorState from, AnimatorState to, float duration, float exitTime = -1f)
            {
                AnimatorStateTransition t = from.AddTransition(to);
                t.hasFixedDuration = true;
                t.duration = duration;
                t.hasExitTime = exitTime >= 0f;
                if (exitTime >= 0f) t.exitTime = exitTime;
                return t;
            }

            // Jump transition is added before the fall transition so the trigger wins when both fire.
            T(locomotion, jumpStart, 0.05f).AddCondition(AnimatorConditionMode.If, 0f, "Jump");
            T(locomotion, jumpLoop, 0.2f).AddCondition(AnimatorConditionMode.IfNot, 0f, "IsGrounded");
            T(jumpStart, jumpLoop, 0.15f, 0.8f);
            // Re-jump from the air state consumes coyote/bunny-hop triggers so they can't fire late.
            T(jumpLoop, jumpStart, 0.1f).AddCondition(AnimatorConditionMode.If, 0f, "Jump");
            // Moving landings bypass the planted-feet Jump_End clip so the run doesn't ice-skate.
            AnimatorStateTransition landMoving = T(jumpLoop, locomotion, 0.2f);
            landMoving.AddCondition(AnimatorConditionMode.If, 0f, "IsGrounded");
            landMoving.AddCondition(AnimatorConditionMode.Greater, 0.5f, "Speed");
            T(jumpLoop, jumpEnd, 0.1f).AddCondition(AnimatorConditionMode.If, 0f, "IsGrounded");
            T(jumpEnd, jumpStart, 0.05f).AddCondition(AnimatorConditionMode.If, 0f, "Jump");
            T(jumpEnd, jumpLoop, 0.15f).AddCondition(AnimatorConditionMode.IfNot, 0f, "IsGrounded");
            T(jumpEnd, locomotion, 0.25f, 0.25f);

            AssetDatabase.SaveAssets();
            return controller;
        }

        private static void FindPlayerObjects(Scene scene, out GameObject playerRoot, out GameObject model)
        {
            GameObject[] roots = scene.GetRootGameObjects();
            GameObject foundModel = roots.FirstOrDefault(g => PrefabUtility.GetPrefabAssetPathOfNearestInstanceRoot(g) == ModelFbxPath);
            model = foundModel;
            playerRoot = roots.FirstOrDefault(g => g.GetComponent<PlayerController>() != null)
                ?? roots.FirstOrDefault(g => g.name == "Player" && g != foundModel);

            if (model == null && playerRoot != null)
            {
                model = playerRoot.GetComponentsInChildren<Transform>(true)
                    .Select(t => t.gameObject)
                    .FirstOrDefault(g => PrefabUtility.IsAnyPrefabInstanceRoot(g)
                        && PrefabUtility.GetPrefabAssetPathOfNearestInstanceRoot(g) == ModelFbxPath);
            }
        }

        private static float SetupCharacterController(GameObject playerRoot, GameObject model)
        {
            Renderer[] renderers = model.GetComponentsInChildren<Renderer>(true);
            if (renderers.Length == 0) throw new System.Exception("Player model has no renderers.");
            Bounds bounds = renderers[0].bounds;
            foreach (Renderer r in renderers) bounds.Encapsulate(r.bounds);

            float height = Mathf.Clamp(bounds.size.y, 0.5f, 3f);
            CharacterController cc = playerRoot.GetComponent<CharacterController>();
            if (cc == null) cc = playerRoot.AddComponent<CharacterController>();
            cc.height = height;
            cc.radius = Mathf.Clamp(height * 0.2f, 0.15f, 0.5f);
            cc.center = new Vector3(0f, height * 0.5f + 0.02f, 0f);
            cc.slopeLimit = 50f;
            cc.stepOffset = Mathf.Min(0.4f, height * 0.25f);
            cc.skinWidth = 0.03f;
            return height;
        }

        private static Transform SetupCameraRig(GameObject playerRoot, float height)
        {
            Transform pivot = playerRoot.transform.Find("CameraPivot");
            if (pivot == null)
            {
                pivot = new GameObject("CameraPivot").transform;
                pivot.SetParent(playerRoot.transform, false);
            }
            pivot.localPosition = new Vector3(0f, height * 0.9f, 0f);
            pivot.localRotation = Quaternion.identity;

            Transform camTransform = pivot.Find("PlayerCamera");
            if (camTransform == null)
            {
                camTransform = new GameObject("PlayerCamera").transform;
                camTransform.SetParent(pivot, false);
            }

            Camera cam = camTransform.GetComponent<Camera>();
            if (cam == null) cam = camTransform.gameObject.AddComponent<Camera>();
            cam.tag = "MainCamera";
            cam.nearClipPlane = 0.05f;
            cam.farClipPlane = 2000f;
            cam.fieldOfView = 70f;
            cam.GetUniversalAdditionalCameraData().renderPostProcessing = true;

            if (Object.FindObjectsByType<AudioListener>(FindObjectsInactive.Include, FindObjectsSortMode.None).Length == 0)
                camTransform.gameObject.AddComponent<AudioListener>();

            return pivot;
        }

        private static Animator SetupAnimator(GameObject model, AnimatorController controller)
        {
            Animator animator = model.GetComponent<Animator>();
            if (animator == null) animator = model.AddComponent<Animator>();
            animator.runtimeAnimatorController = controller;
            animator.applyRootMotion = false;
            animator.cullingMode = AnimatorCullingMode.AlwaysAnimate;

            if (animator.avatar == null)
                animator.avatar = AssetDatabase.LoadAllAssetsAtPath(ModelFbxPath).OfType<Avatar>().FirstOrDefault();
            if (animator.avatar == null || !animator.avatar.isHuman)
                Debug.LogWarning("[PlayerSceneSetup] Humanoid avatar missing or invalid on " + ModelFbxPath);
            return animator;
        }

        private static void FindMeshes(GameObject model, out SkinnedMeshRenderer body, out SkinnedMeshRenderer firstPerson)
        {
            SkinnedMeshRenderer[] meshes = model.GetComponentsInChildren<SkinnedMeshRenderer>(true);
            firstPerson = meshes.FirstOrDefault(m => m.name.ToLowerInvariant().Contains("firstperson"));
            SkinnedMeshRenderer fp = firstPerson;
            body = meshes.Where(m => m != fp)
                .OrderByDescending(m => m.sharedMaterials.Length)
                .FirstOrDefault();
            if (body == null || firstPerson == null)
                Debug.LogWarning($"[PlayerSceneSetup] Expected 2 skinned meshes under the player model, found {meshes.Length}.");
        }

        // The scene overrides the body's materials; carry matching overrides onto the first-person mesh by source-material name.
        private static void CopyMaterialOverrides(SkinnedMeshRenderer body, SkinnedMeshRenderer firstPerson)
        {
            if (body == null || firstPerson == null) return;
            var bodySource = PrefabUtility.GetCorrespondingObjectFromSource(body);
            var fpSource = PrefabUtility.GetCorrespondingObjectFromSource(firstPerson);
            if (bodySource == null || fpSource == null) return;

            var overridesByName = new Dictionary<string, Material>();
            Material[] srcMats = bodySource.sharedMaterials;
            Material[] curMats = body.sharedMaterials;
            for (int i = 0; i < srcMats.Length && i < curMats.Length; i++)
                if (srcMats[i] != null && curMats[i] != null && srcMats[i] != curMats[i])
                    overridesByName[srcMats[i].name] = curMats[i];
            if (overridesByName.Count == 0) return;

            Material[] fpSrc = fpSource.sharedMaterials;
            Material[] fpCur = firstPerson.sharedMaterials;
            bool changed = false;
            for (int i = 0; i < fpSrc.Length && i < fpCur.Length; i++)
            {
                if (fpSrc[i] != null && fpCur[i] == fpSrc[i] && overridesByName.TryGetValue(fpSrc[i].name, out Material mat))
                {
                    fpCur[i] = mat;
                    changed = true;
                }
            }
            if (changed) firstPerson.sharedMaterials = fpCur;
        }

        private static void WireController(GameObject playerRoot, Transform pivot, Animator animator, SkinnedMeshRenderer body, SkinnedMeshRenderer firstPerson, AuthoredSpeeds authoredSpeeds)
        {
            PlayerController pc = playerRoot.GetComponent<PlayerController>();
            if (pc == null) pc = playerRoot.AddComponent<PlayerController>();

            var so = new SerializedObject(pc);
            so.FindProperty("m_CameraPivot").objectReferenceValue = pivot;
            so.FindProperty("m_Animator").objectReferenceValue = animator;
            so.FindProperty("m_BodyMesh").objectReferenceValue = body;
            so.FindProperty("m_FirstPersonMesh").objectReferenceValue = firstPerson;

            // Retargeted root motion scales with the destination avatar's humanScale.
            float humanScale = animator != null && animator.avatar != null && animator.avatar.isHuman && animator.humanScale > 0.01f
                ? animator.humanScale
                : 1f;
            void SetSpeed(string path, float value)
            {
                if (value > 0.05f) so.FindProperty(path).floatValue = value * humanScale;
            }
            SetSpeed("m_WalkClipSpeeds.forward", authoredSpeeds.walkForward);
            SetSpeed("m_WalkClipSpeeds.backward", authoredSpeeds.walkBackward);
            SetSpeed("m_WalkClipSpeeds.strafe", authoredSpeeds.walkStrafe);
            SetSpeed("m_RunClipSpeeds.forward", authoredSpeeds.runForward);
            SetSpeed("m_RunClipSpeeds.backward", authoredSpeeds.runBackward);
            SetSpeed("m_RunClipSpeeds.strafe", authoredSpeeds.runStrafe);

            so.ApplyModifiedPropertiesWithoutUndo();
        }
    }
}
