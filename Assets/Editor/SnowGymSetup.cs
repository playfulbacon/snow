using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

namespace SnowDays.EditorTools
{
    /// <summary>
    /// Builds the Snow Gym: a bare scene holding nothing but the character, a flat field of snow and the sculpting
    /// kit. No island, no props, no terrain, no night lighting — just the systems under test, so that when
    /// something feels wrong there is nothing else it could be.
    ///
    /// Two things make this possible without rebuilding the world. SnowDeformSystem needs no terrain: with none
    /// active it lays the snow shell flat at y=0 (it says so itself), which is exactly the field we want. And the
    /// character comes from <see cref="PlayerPrefab"/>, so the gym and Main stand the same rig rather than two
    /// hierarchies that drift.
    ///
    /// The kit itself is wired by <see cref="MainSceneSetup.EnsureKit"/>, the same call the Main scene uses.
    ///
    /// Idempotent in the sense that matters: it overwrites the gym scene each time, so re-running it is how you
    /// pick up changes to the kit. Anything you hand-place in the gym is therefore disposable by design.
    /// Run from the menu (Snowfield ▸ Create Snow Gym Scene) or headless:
    /// `Unity -batchmode -nographics -quit -executeMethod SnowDays.EditorTools.SnowGymSetup.Run`
    /// </summary>
    public static class SnowGymSetup
    {
        const string ScenePath = "Assets/Scenes/SnowGym.unity";
        const string SnowMatPath = "Assets/Settings/Snow.mat";

        /// <summary>Side of the ground collider (m). Comfortably wider than SnowDeformSystem's 80 m window.</summary>
        const float GroundSize = 120f;

        [MenuItem("Snowfield/Create Snow Gym Scene")]
        public static void Run()
        {
            // NewScene replaces whatever is open; from the menu that must not silently discard edits.
            if (!Application.isBatchMode && !EditorSceneManager.SaveCurrentModifiedScenesIfUserWantsTo()) return;

            // The gym stands the Game prefab; it does not know how to build one. That prefab is born in Main,
            // out of the rig and HUD already standing there, so Main has to have been set up at least once.
            if (AssetDatabase.LoadAssetAtPath<GameObject>("Assets/Prefabs/Game.prefab") == null)
            {
                Debug.LogError("[SnowGymSetup] Assets/Prefabs/Game.prefab does not exist yet. Run " +
                               "Snowfield ▸ Ensure Main Scene Sculpting first — that is what creates it.");
                if (Application.isBatchMode) EditorApplication.Exit(1);
                return;
            }

            var scene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);

            // Give the scene an identity on disk before anything is loaded beside it: borrowing the player opens
            // Main additively, and an untitled scene is a fragile thing to do that next to.
            if (!EditorSceneManager.SaveScene(scene, ScenePath))
            {
                Debug.LogError($"[SnowGymSetup] Could not create {ScenePath}.");
                if (Application.isBatchMode) EditorApplication.Exit(1);
                return;
            }

            CreateSun();
            CreateGround();
            CreateSnowDeform();

            // Default HUD text: the gym is a daylight scene, so the dark default reads fine. No networking —
            // this bench is for getting the feel right on its own, which is the whole reason it exists.
            // EnsureKit instantiates the Game prefab, so the player arrives with it.
            if (!MainSceneSetup.EnsureKit(scene, networking: false, statusText: null))
            {
                if (Application.isBatchMode) EditorApplication.Exit(1);
                return;
            }

            var player = Object.FindAnyObjectByType<PlayerController>();
            if (player != null)
            {
                player.transform.position = new Vector3(0f, 0.1f, -3f); // off centre, looking at the field
                player.transform.rotation = Quaternion.identity;
            }

            EditorSceneManager.MarkSceneDirty(scene);
            if (!EditorSceneManager.SaveScene(scene))
            {
                Debug.LogError($"[SnowGymSetup] Failed to save {ScenePath}.");
                if (Application.isBatchMode) EditorApplication.Exit(1);
                return;
            }
            Debug.Log($"[SnowGymSetup] Snow Gym built at {ScenePath}. Press Play: scoop from the ground, roll a " +
                      "ball, drop it to start a sculpture.");
            if (Application.isBatchMode) EditorApplication.Exit(0);
        }

        /// <summary>A plain daylight sun. SnowLook quantizes this into bands, so its angle is a look decision.</summary>
        static void CreateSun()
        {
            var go = new GameObject("Sun");
            go.transform.rotation = Quaternion.Euler(42f, 150f, 0f);
            var light = go.AddComponent<Light>();
            light.type = LightType.Directional;
            light.color = new Color(1f, 0.97f, 0.91f);
            light.intensity = 1.15f;
            light.shadows = LightShadows.Soft;

            // An empty scene has no probe at all, and the snow shader reads ambient for everything facing away
            // from the sun — without this the shadowed side of a sculpture is black.
            RenderSettings.ambientMode = UnityEngine.Rendering.AmbientMode.Trilight;
            RenderSettings.ambientSkyColor = new Color(0.55f, 0.65f, 0.82f);
            RenderSettings.ambientEquatorColor = new Color(0.45f, 0.52f, 0.66f);
            RenderSettings.ambientGroundColor = new Color(0.38f, 0.42f, 0.52f);

            // An empty scene has no skybox at all, which leaves the field floating in void. Judging how snow
            // reads - the whole point of this bench - needs something behind it.
            var sky = AssetDatabase.GetBuiltinExtraResource<Material>("Default-Skybox.mat");
            if (sky != null) RenderSettings.skybox = sky;
        }

        /// <summary>
        /// What the player stands on, and what the aim ray calls "ground". Deliberately invisible: the snow shell
        /// is what you see, and it sits a snow-depth above this, so a renderer here would only ever be a surface
        /// buried just under the snow waiting to z-fight with it. Not on Ignore Raycast — the sculpt ray has to
        /// hit this for scooping and rolling to engage at all.
        /// </summary>
        static void CreateGround()
        {
            var go = new GameObject("Ground");
            var box = go.AddComponent<BoxCollider>();
            box.size = new Vector3(GroundSize, 1f, GroundSize);
            box.center = new Vector3(0f, -0.5f, 0f); // top face exactly at y = 0
        }

        /// <summary>
        /// Place SnowDeformSystem by hand rather than letting it bootstrap, purely so the ground wears the same
        /// diffuse as the sculptures. It resolves that texture from a terrain layer at runtime and there is no
        /// terrain here, which would leave the field flat white while every sculpture on it is textured — and
        /// "the field and the things built on it are one material" is the whole premise of the snow shader.
        /// </summary>
        static void CreateSnowDeform()
        {
            var go = new GameObject("SnowDeform");
            var system = go.AddComponent<SnowDeformSystem>();

            var mat = AssetDatabase.LoadAssetAtPath<Material>(SnowMatPath);
            if (mat == null)
            {
                Debug.LogWarning($"[SnowGymSetup] {SnowMatPath} missing; the gym's ground snow will be untextured.");
                return;
            }
            var tex = mat.GetTexture("_SnowBaseMap") as Texture2D;
            if (tex == null)
            {
                Debug.LogWarning("[SnowGymSetup] Snow.mat has no _SnowBaseMap; the gym's ground snow will be " +
                                 "untextured. Run Snowfield ▸ Ensure Main Scene Sculpting to bake one from the terrain.");
                return;
            }

            // Both fields are private [SerializeField]; a Texture2D is a plain asset reference, so unlike a custom
            // ScriptableObject it survives SerializedProperty assignment in batchmode.
            var so = new SerializedObject(system);
            so.FindProperty("m_SnowTexture").objectReferenceValue = tex;
            so.FindProperty("m_SnowTextureTile").floatValue = mat.GetFloat("_SnowTexTiling");
            so.ApplyModifiedPropertiesWithoutUndo();
        }

    }
}
