using Snowfield.Config;
using Snowfield.Player;
using Snowfield.Sculpture;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

namespace SnowDays.EditorTools
{
    /// <summary>
    /// Wires the Snowfield sculpting kit into the Main scene around the existing SnowDays player rig, changing
    /// nothing else about the scene. Idempotent; run from the menu or headless:
    /// `Unity -batchmode -nographics -quit -executeMethod SnowDays.EditorTools.MainSceneSetup.Run`
    /// Layout follows the sandbox convention — one responsibility per GameObject:
    ///   Player (SnowDays.PlayerController +HandRig) › SculptTool (+AccessoryPlacer +SnowballRoller) · CarryAnchor · PalmAnchor
    ///   root: Sculptures (SculptureFactory) · SaveLoad (SaveLoadManager) · BrushCursor (inactive) · HUD (ToolHud)
    /// SnowDeformSystem / SnowfallSystem / SnowDeformGroundAdapter need no scene objects (runtime bootstraps).
    /// All references are assigned directly (SerializedProperty drops custom ScriptableObject refs in batchmode).
    /// </summary>
    public static class MainSceneSetup
    {
        const string ScenePath = "Assets/Scenes/Main.unity";
        const string ConfigPath = "Assets/Settings/SculptFeelConfig.asset";
        const string SnowMatPath = "Assets/Settings/Snow.mat";
        const string SculptShaderPath = "Assets/SnowDeform/Resources/SnowSculpt.shader";
        const string CursorMatPath = "Assets/Settings/BrushCursor.mat";
        const string ReticlePath = "Assets/reticle-2.png";
        const string LeftClickPath = "Assets/left-click.png";
        const string RightClickPath = "Assets/right-click.png";

        [MenuItem("Snowfield/Ensure Main Scene Sculpting")]
        public static void Run()
        {
            var scene = EditorSceneManager.OpenScene(ScenePath, OpenSceneMode.Single);

            var config = AssetDatabase.LoadAssetAtPath<SculptFeelConfig>(ConfigPath);
            var snowMat = AssetDatabase.LoadAssetAtPath<Material>(SnowMatPath);
            if (config == null || snowMat == null)
            {
                Debug.LogError($"[MainSceneSetup] Missing {ConfigPath} or {SnowMatPath}; aborting.");
                if (Application.isBatchMode) EditorApplication.Exit(1);
                return;
            }

            var player = Object.FindAnyObjectByType<PlayerController>();
            if (player == null)
            {
                Debug.LogError("[MainSceneSetup] No SnowDays.PlayerController in the scene; aborting.");
                if (Application.isBatchMode) EditorApplication.Exit(1);
                return;
            }
            var viewCamera = player.GetComponentInChildren<Camera>(true);

            SyncSnowMaterial(snowMat);

            // The brush ray starts behind the player; keep the whole player out of every sculpt raycast.
            int ignoreRaycast = 2; // built-in layer
            foreach (var t in player.GetComponentsInChildren<Transform>(true))
                t.gameObject.layer = ignoreRaycast;
            int sculptMask = ~(1 << ignoreRaycast);

            // --- Sculptures container + factory ---
            var sculpturesGo = GameObject.Find("Sculptures") ?? new GameObject("Sculptures");
            var factory = sculpturesGo.GetComponent<SculptureFactory>() ?? sculpturesGo.AddComponent<SculptureFactory>();
            factory.config = config;
            factory.snowMaterial = snowMat;
            factory.container = sculpturesGo.transform;

            // --- Save/load ---
            var saveGo = GameObject.Find("SaveLoad") ?? new GameObject("SaveLoad");
            if (saveGo.GetComponent<SaveLoadManager>() == null) saveGo.AddComponent<SaveLoadManager>();

            // --- Brush cursor (inactive sphere, searched with GetComponentsInChildren(true)) ---
            var cursorGo = FindInactiveRoot(scene, "BrushCursor");
            if (cursorGo == null)
            {
                cursorGo = GameObject.CreatePrimitive(PrimitiveType.Sphere);
                cursorGo.name = "BrushCursor";
                Object.DestroyImmediate(cursorGo.GetComponent<Collider>());
            }
            var cursorMr = cursorGo.GetComponent<MeshRenderer>();
            var cursorMat = AssetDatabase.LoadAssetAtPath<Material>(CursorMatPath);
            if (cursorMat != null) cursorMr.sharedMaterial = cursorMat;
            cursorMr.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            cursorMr.receiveShadows = false;
            cursorGo.SetActive(false);

            // --- Carry anchors: yaw-only children of the player root (the pivot pitches, so not under it).
            //     Overhead for snow that needs both arms; down at the right hand for a one-handed handful. ---
            Transform anchor = EnsureAnchor(player.transform, "CarryAnchor", new Vector3(0f, 1.9f, 1f), ignoreRaycast);
            // Palm height is measured off the rig, not the capsule: this character's shoulders sit at ~0.98 m and
            // its eye line at 1.08, so a hold much above this reads as holding the snowball over your own head.
            Transform palm = EnsureAnchor(player.transform, "PalmAnchor", new Vector3(0.35f, 0.78f, 0.5f), ignoreRaycast);

            // --- Sculpt tool ---
            var toolTr = player.transform.Find("SculptTool");
            if (toolTr == null)
            {
                toolTr = new GameObject("SculptTool").transform;
                toolTr.SetParent(player.transform, false);
            }
            toolTr.gameObject.layer = ignoreRaycast;
            var tool = toolTr.GetComponent<SculptTool>() ?? toolTr.gameObject.AddComponent<SculptTool>();
            tool.config = config;
            tool.viewCamera = viewCamera;
            tool.sculptMask = sculptMask;
            tool.reachOrigin = player.transform;
            tool.cursor = cursorGo.transform;
            var placer = toolTr.GetComponent<AccessoryPlacer>() ?? toolTr.gameObject.AddComponent<AccessoryPlacer>();
            var roller = toolTr.GetComponent<SnowballRoller>() ?? toolTr.gameObject.AddComponent<SnowballRoller>();
            roller.config = config;
            roller.character = player.transform;
            roller.carryAnchor = anchor;
            roller.palmAnchor = palm;
            roller.groundMask = sculptMask;
            // Feel values tuned in the sandbox scene (they lived only in that scene file, now deleted).
            roller.carryOffset = new Vector2(0.75f, 1.05f);
            roller.throwSpeedMin = 7f;
            roller.throwSpeedMax = 30f;

            // --- Hands: stretchy arm IK on the humanoid rig, driven by the tool ---
            var rig = player.GetComponent<HandRig>() ?? player.gameObject.AddComponent<HandRig>();
            rig.config = config;
            rig.animator = player.GetComponentInChildren<Animator>(true);
            tool.hands = rig;

            // --- HUD ---
            var hudGo = GameObject.Find("HUD") ?? new GameObject("HUD", typeof(RectTransform));
            var hud = hudGo.GetComponent<ToolHud>() ?? hudGo.AddComponent<ToolHud>();
            hud.tool = tool;
            hud.placer = placer;
            hud.cursor.defaultIcon = AssetDatabase.LoadAssetAtPath<Sprite>(ReticlePath);
            hud.cursor.primaryInputIcon = AssetDatabase.LoadAssetAtPath<Sprite>(LeftClickPath);
            hud.cursor.secondaryInputIcon = AssetDatabase.LoadAssetAtPath<Sprite>(RightClickPath);
            hud.colors.statusText = new Color(0.85f, 0.88f, 0.95f); // Main is a night scene; the default dark text vanishes
            hud.RebuildNow();

            // --- Multiplayer: NetworkManager root + avatar/channel prefabs ---
            NetSceneSetup.Ensure();

            EditorSceneManager.MarkSceneDirty(scene);
            EditorSceneManager.SaveScene(scene);
            Debug.Log("[MainSceneSetup] Sculpting kit ensured in Main scene.");
            if (Application.isBatchMode) EditorApplication.Exit(0);
        }

        /// <summary>
        /// Keeps sculpted snow wearing the same diffuse as the ground shell.
        /// SnowDeformSystem resolves that texture from the terrain when it
        /// bootstraps; a material cannot read a runtime binding, so the same
        /// rule runs here and bakes the result into Snow.mat.
        /// </summary>
        static void SyncSnowMaterial(Material mat)
        {
            var shader = AssetDatabase.LoadAssetAtPath<Shader>(SculptShaderPath);
            if (shader == null)
                Debug.LogWarning($"[MainSceneSetup] {SculptShaderPath} missing; leaving {mat.shader.name} on the snow material.");
            else if (mat.shader != shader)
                mat.shader = shader;

            // Same pick as SnowDeformSystem.ApplySnowTexture: a snow-named
            // layer if there is one, else the first layer with a diffuse.
            TerrainLayer best = null;
            foreach (var terrain in Object.FindObjectsByType<Terrain>(FindObjectsSortMode.None))
            {
                TerrainLayer[] layers = terrain.terrainData != null ? terrain.terrainData.terrainLayers : null;
                if (layers == null) continue;
                foreach (var layer in layers)
                {
                    if (layer == null || layer.diffuseTexture == null) continue;
                    if (best == null) best = layer;
                    if (layer.name.ToLowerInvariant().Contains("snow")) { best = layer; break; }
                }
                if (best != null && best.name.ToLowerInvariant().Contains("snow")) break;
            }

            if (best == null)
            {
                Debug.LogWarning("[MainSceneSetup] No terrain layer with a diffuse; snow material keeps its current texture.");
            }
            else
            {
                mat.SetTexture("_SnowBaseMap", best.diffuseTexture);
                mat.SetFloat("_SnowTexTiling", Mathf.Max(best.tileSize.x, 0.01f));
                Debug.Log($"[MainSceneSetup] snow material texture from terrain layer '{best.name}': " +
                    $"{best.diffuseTexture.name} (tile {best.tileSize.x}m)");
            }

            EditorUtility.SetDirty(mat);
            AssetDatabase.SaveAssets();
        }

        /// <summary>Create the anchor at its authored default if missing; never move one that already exists.</summary>
        static Transform EnsureAnchor(Transform parent, string name, Vector3 localPosition, int layer)
        {
            Transform anchor = parent.Find(name);
            if (anchor == null)
            {
                anchor = new GameObject(name).transform;
                anchor.SetParent(parent, false);
                anchor.localPosition = localPosition;
            }
            anchor.gameObject.layer = layer; // created after the subtree layer pass
            return anchor;
        }

        static GameObject FindInactiveRoot(UnityEngine.SceneManagement.Scene scene, string name)
        {
            foreach (var root in scene.GetRootGameObjects())
                if (root.name == name) return root;
            return null;
        }
    }
}
