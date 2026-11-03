using System.IO;
using Snowfield.Config;
using Snowfield.Player;
using Snowfield.Sculpture;
using Snowfield.UI;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.InputSystem.UI;

namespace SnowDays.EditorTools
{
    /// <summary>
    /// Wires the Snowfield sculpting kit into the Main scene around the existing SnowDays player rig, changing
    /// nothing else about the scene. Idempotent; run from the menu or headless:
    /// `Unity -batchmode -nographics -quit -executeMethod SnowDays.EditorTools.MainSceneSetup.Run`
    /// Layout follows the sandbox convention — one responsibility per GameObject:
    ///   Player (SnowDays.PlayerController +HandRig) › SculptTool (+AccessoryPlacer +SnowballRoller) · CarryAnchor · PalmAnchor
    ///   root: Sculptures (SculptureFactory) · SaveLoad (SaveLoadManager) · BrushCursor (inactive) · HUD (ToolHud)
    /// Those four roots and the player come from prefabs under Assets/Prefabs and Assets/Player, created on the
    /// first run from whatever the scene already had, so every scene stands the same kit.
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
        const string KitPrefabFolder = "Assets/Prefabs";
        const string GamePrefabPath = KitPrefabFolder + "/Game.prefab";
        const string GameRootName = "Game";
        const string ReticlePath = "Assets/reticle-2.png";
        const string LeftClickPath = "Assets/left-click.png";
        const string RightClickPath = "Assets/right-click.png";

        [MenuItem("Snowfield/Ensure Main Scene Sculpting")]
        public static void Run()
        {
            var scene = EditorSceneManager.OpenScene(ScenePath, OpenSceneMode.Single);

            // Main is a night scene, so the HUD's default dark status text vanishes against it.
            if (!EnsureKit(scene, networking: true, statusText: new Color(0.85f, 0.88f, 0.95f)))
            {
                if (Application.isBatchMode) EditorApplication.Exit(1);
                return;
            }

            EditorSceneManager.MarkSceneDirty(scene);
            EditorSceneManager.SaveScene(scene);
            Debug.Log("[MainSceneSetup] Sculpting kit ensured in Main scene.");
            if (Application.isBatchMode) EditorApplication.Exit(0);
        }

        /// <summary>
        /// Wire the sculpting kit around a player that is already in the open scene. Shared by the Main scene and
        /// the Snow Gym rather than copied into each: a test bench wired differently from the real scene is worse
        /// than no test bench, because it lies. Callers own opening and saving the scene. False = a required asset
        /// is missing and nothing was changed.
        /// </summary>
        internal static bool EnsureKit(UnityEngine.SceneManagement.Scene scene,
                                       bool networking, Color? statusText)
        {
            var config = AssetDatabase.LoadAssetAtPath<SculptFeelConfig>(ConfigPath);
            var snowMat = AssetDatabase.LoadAssetAtPath<Material>(SnowMatPath);
            if (config == null || snowMat == null)
            {
                Debug.LogError($"[MainSceneSetup] Missing {ConfigPath} or {SnowMatPath}; aborting.");
                return false;
            }
            Transform game = EnsureGame(scene);
            var player = game.GetComponentInChildren<PlayerController>(true)
                      ?? Object.FindAnyObjectByType<PlayerController>();
            if (player == null)
            {
                Debug.LogError("[MainSceneSetup] No SnowDays.PlayerController in the scene and none in "
                               + GamePrefabPath + "; aborting.");
                return false;
            }
            if (player.transform.parent != game) player.transform.SetParent(game, true);
            var viewCamera = player.GetComponentInChildren<Camera>(true);

            SyncSnowMaterial(snowMat);

            // The brush ray starts behind the player; keep the whole player out of every sculpt raycast.
            int ignoreRaycast = 2; // built-in layer
            foreach (var t in player.GetComponentsInChildren<Transform>(true))
                t.gameObject.layer = ignoreRaycast;
            int sculptMask = ~(1 << ignoreRaycast);

            // --- Sculptures container + factory ---
            var sculpturesGo = EnsureUnder(scene, game, "Sculptures", () => new GameObject("Sculptures"));
            var factory = sculpturesGo.GetComponent<SculptureFactory>() ?? sculpturesGo.AddComponent<SculptureFactory>();
            factory.config = config;
            factory.snowMaterial = snowMat;
            factory.container = sculpturesGo.transform;

            // --- Save/load ---
            var saveGo = EnsureUnder(scene, game, "SaveLoad", () => new GameObject("SaveLoad"));
            if (saveGo.GetComponent<SaveLoadManager>() == null) saveGo.AddComponent<SaveLoadManager>();

            // --- Brush cursor (inactive sphere, searched with GetComponentsInChildren(true)) ---
            var cursorGo = EnsureUnder(scene, game, "BrushCursor", () =>
            {
                var go = GameObject.CreatePrimitive(PrimitiveType.Sphere);
                Object.DestroyImmediate(go.GetComponent<Collider>());
                return go;
            });
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
            var hudGo = EnsureUnder(scene, game, "HUD", () => new GameObject("HUD", typeof(RectTransform)));
            var hud = hudGo.GetComponent<ToolHud>() ?? hudGo.AddComponent<ToolHud>();
            hud.tool = tool;
            hud.placer = placer;
            hud.cursor.defaultIcon = AssetDatabase.LoadAssetAtPath<Sprite>(ReticlePath);
            // Mouse buttons get their pictures; every other input is drawn as a key cap with its name in it, so
            // an input with no sprite is still readable rather than silently keyless.
            hud.cursor.leftMouseIcon = AssetDatabase.LoadAssetAtPath<Sprite>(LeftClickPath);
            hud.cursor.rightMouseIcon = AssetDatabase.LoadAssetAtPath<Sprite>(RightClickPath);
            if (statusText.HasValue) hud.colors.statusText = statusText.Value;

            // --- join code, corner of the screen while a session is up ---
            var joinCode = EnsureJoinCode(scene, game);

            // --- pause menu (Esc): settings, multiplayer, quit. Its own canvas above the HUD. ---
            var pauseMenu = EnsurePauseMenu(scene, game, hud, viewCamera);
            var eventSystem = EnsureEventSystem(scene, game);

            // --- planar shave preview ---
            tool.planeCut = EnsurePlaneCut(game);

            // Everything is wired, and because it all hangs off one root those references are internal to the
            // prefab and will actually serialise. Promote before the HUD builds its tree: ToolHud.Build wipes the
            // canvas's children with DestroyImmediate and Unity forbids destroying a child that belongs to a
            // prefab, so the asset must hold the Canvas and the tuned fields and nothing underneath.
            PromoteGame(game.gameObject);

            // Anything this run had to create is, until now, only an override on this scene's instance of that
            // prefab. These belong to the game, not to the scene it is standing in.
            ApplyAdded(pauseMenu);
            ApplyAdded(eventSystem);
            ApplyAdded(joinCode);
            ApplyAdded(tool.planeCut != null ? tool.planeCut.gameObject : null);

            hud.RebuildNow();

            // --- Multiplayer: NetworkManager root + avatar/channel prefabs ---
            if (networking) NetSceneSetup.Ensure();

            return true;
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

        /// <summary>
        /// The one root a scene needs: Assets/Prefabs/Game.prefab, holding everything required to play — the
        /// player, the sculpture factory, save/load, the brush cursor and the HUD. One prefab rather than one per
        /// component: a scene wants "put the game in it", not a shopping list of single-component objects. The
        /// payoff is not just tidiness — every reference between these (HUD to tool, tool to cursor, roller to the
        /// carry anchors) is internal to a single asset, so the wiring is stored rather than rebuilt per scene.
        /// Found in the open scene, else instantiated from the prefab, else created empty for the caller to fill,
        /// which is how the prefab first comes into existence out of whatever the scene already had.
        /// </summary>
        static Transform EnsureGame(UnityEngine.SceneManagement.Scene scene)
        {
            var existing = FindInactiveRoot(scene, GameRootName);
            if (existing != null) return existing.transform;

            var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(GamePrefabPath);
            if (prefab != null)
            {
                var instance = (GameObject)PrefabUtility.InstantiatePrefab(prefab, scene);
                instance.name = GameRootName;
                return instance.transform;
            }
            return new GameObject(GameRootName).transform;
        }

        /// <summary>
        /// A child of the Game root, adopting a stray scene root of the same name if there is one. That adoption
        /// is what migrates the old layout, where these were scene roots: the HUD above all carries layout and
        /// colours set by hand, so it has to move into the prefab rather than be rebuilt from defaults beside it.
        /// Reparented keeping world position, so nothing moves.
        /// </summary>
        static GameObject EnsureUnder(UnityEngine.SceneManagement.Scene scene, Transform game, string name,
                                      System.Func<GameObject> build)
        {
            var child = game.Find(name);
            if (child != null) return child.gameObject;

            var stray = FindInactiveRoot(scene, name);
            var go = stray != null ? stray : build();
            go.name = name;
            go.transform.SetParent(game, true);
            return go;
        }

        /// <summary>Write the Game prefab the first time, and make this scene's root an instance of it.</summary>
        static void PromoteGame(GameObject game)
        {
            if (AssetDatabase.LoadAssetAtPath<GameObject>(GamePrefabPath) != null) return;
            if (!Directory.Exists(KitPrefabFolder))
            {
                Directory.CreateDirectory(KitPrefabFolder);
                AssetDatabase.Refresh();
            }
            PrefabUtility.SaveAsPrefabAssetAndConnect(game, GamePrefabPath, InteractionMode.AutomatedAction,
                                                      out bool ok);
            Debug.Log(ok
                ? "[MainSceneSetup] Created " + GamePrefabPath + "; a scene needs only this prefab to be playable."
                : "[MainSceneSetup] Could not write " + GamePrefabPath + "; the kit stays scene-local.");
        }

        /// <summary>
        /// The session's join code, on its own canvas rather than as a line in the HUD — ToolHud regenerates its
        /// whole canvas, so nothing placed in there by hand survives. Created once and then left alone, so its
        /// position and styling can be changed in the inspector.
        /// </summary>
        /// <summary>
        /// The planar-shave overlay: a line and a wash over the doomed half, on their own canvas so they can be
        /// restyled without ToolHud regenerating them away. Created once and then left alone.
        /// </summary>
        static Snowfield.Player.PlaneCutPreview EnsurePlaneCut(Transform game)
        {
            var existing = game.Find("PlaneCut");
            if (existing != null) return existing.GetComponent<Snowfield.Player.PlaneCutPreview>();

            var go = new GameObject("PlaneCut", typeof(RectTransform), typeof(Canvas),
                                    typeof(UnityEngine.UI.CanvasScaler));
            go.transform.SetParent(game, false);
            var canvas = go.GetComponent<Canvas>();
            canvas.renderMode = RenderMode.ScreenSpaceOverlay;
            canvas.sortingOrder = -1; // under the HUD: this is a hint, not a readout

            var preview = go.AddComponent<Snowfield.Player.PlaneCutPreview>();
            preview.doomed = Rect(go.transform, "Doomed", new Color(1f, 0.25f, 0.2f, 0.22f));
            preview.line = Rect(go.transform, "Line", new Color(1f, 0.95f, 0.9f, 0.9f));
            preview.line.sizeDelta = new Vector2(2000f, 3f);
            go.SetActive(true);
            preview.Hide();
            return preview;
        }

        static RectTransform Rect(Transform parent, string name, Color color)
        {
            var go = new GameObject(name, typeof(RectTransform), typeof(CanvasRenderer), typeof(UnityEngine.UI.Image));
            go.transform.SetParent(parent, false);
            var rt = go.GetComponent<RectTransform>();
            rt.anchorMin = rt.anchorMax = rt.pivot = new Vector2(0.5f, 0.5f);
            rt.anchoredPosition = Vector2.zero;
            var img = go.GetComponent<UnityEngine.UI.Image>();
            img.color = color;
            img.raycastTarget = false;
            return rt;
        }

        static GameObject EnsureJoinCode(UnityEngine.SceneManagement.Scene scene, Transform game)
        {
            var already = game.Find("JoinCode");
            if (already != null) return already.gameObject;

            var go = new GameObject("JoinCode", typeof(RectTransform), typeof(Canvas),
                                    typeof(UnityEngine.UI.CanvasScaler));
            go.transform.SetParent(game, false);
            var canvas = go.GetComponent<Canvas>();
            canvas.renderMode = RenderMode.ScreenSpaceOverlay;
            var scaler = go.GetComponent<UnityEngine.UI.CanvasScaler>();
            scaler.uiScaleMode = UnityEngine.UI.CanvasScaler.ScaleMode.ScaleWithScreenSize;
            scaler.referenceResolution = new Vector2(1920f, 1080f);

            var textGo = new GameObject("Label", typeof(RectTransform), typeof(CanvasRenderer), typeof(UnityEngine.UI.Text));
            textGo.transform.SetParent(go.transform, false);
            var rt = textGo.GetComponent<RectTransform>();
            rt.anchorMin = rt.anchorMax = rt.pivot = new Vector2(0f, 1f); // top-left
            rt.anchoredPosition = new Vector2(24f, -24f);
            rt.sizeDelta = new Vector2(520f, 48f);
            var text = textGo.GetComponent<UnityEngine.UI.Text>();
            text.font = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");
            text.fontSize = 32;
            text.color = new Color(0.85f, 0.88f, 0.95f);
            text.alignment = TextAnchor.UpperLeft;
            text.raycastTarget = false;

            var label = go.AddComponent<Snowfield.Net.JoinCodeLabel>();
            label.label = text;
            textGo.SetActive(false); // nothing to show until there is a session
            return go;
        }

        /// <summary>
        /// The pause menu, from Assets/Prefabs/UI/PauseMenu.prefab (built on the first run, then left alone so it
        /// can be restyled). Only the two things it cannot know about itself are assigned: the camera the
        /// field-of-view setting lands on, and the HUD whose dev status lines the Display page can switch.
        /// </summary>
        static GameObject EnsurePauseMenu(UnityEngine.SceneManagement.Scene scene, Transform game, ToolHud hud,
                                          Camera viewCamera)
        {
            var prefab = PauseMenuPrefabs.EnsureAll();
            var child = game.Find("PauseMenu");
            GameObject go = child != null ? child.gameObject : null;

            // A menu dragged into the scene by hand to try it out is adopted rather than duplicated: two of them
            // would both answer Esc.
            if (go == null)
            {
                go = FindInactiveRoot(scene, "PauseMenu");
                if (go != null) go.transform.SetParent(game, false);
            }
            if (go == null)
            {
                if (prefab == null)
                {
                    Debug.LogWarning("[MainSceneSetup] No pause menu prefab; the scene has no Esc menu.");
                    return null;
                }
                go = (GameObject)PrefabUtility.InstantiatePrefab(prefab);
                go.name = "PauseMenu";
                go.transform.SetParent(game, false);
            }

            var menu = go.GetComponent<PauseMenu>();
            if (menu == null) return go;
            menu.viewCamera = viewCamera;
            menu.hud = hud;
            EditorUtility.SetDirty(menu);
            return go;
        }

        /// <summary>
        /// uGUI does nothing without one of these, and until the pause menu arrived nothing in this scene was
        /// clickable, so there was none. On the new Input System, so the old StandaloneInputModule would read no
        /// mouse at all.
        /// </summary>
        static GameObject EnsureEventSystem(UnityEngine.SceneManagement.Scene scene, Transform game)
        {
            var found = Object.FindAnyObjectByType<EventSystem>(FindObjectsInactive.Include);
            var go = found != null
                ? found.gameObject
                : EnsureUnder(scene, game, "EventSystem", () => new GameObject("EventSystem"));
            // One found loose in the scene belongs in the kit too: it is the game that needs it, not the scene.
            if (go.transform.parent != game) go.transform.SetParent(game, false);
            if (go.GetComponent<EventSystem>() == null) go.AddComponent<EventSystem>();
            if (go.GetComponent<BaseInputModule>() == null) go.AddComponent<InputSystemUIInputModule>();
            return go;
        }

        /// <summary>
        /// An object this setup creates once Game.prefab already exists is not in the kit: it is an <i>added
        /// GameObject override</i> on this scene's instance, so it lives in the scene file, reaches no other
        /// scene, and disappears the moment someone reverts the instance. Promotion only covers the very first
        /// run, when the asset is written out of the whole hierarchy.
        ///
        /// So each one is applied into the asset, which is what makes "a scene needs only this prefab" true for
        /// the things added later — the pause menu and the EventSystem above all, which were added long after the
        /// prefab was born. References they hold to their new siblings are remapped by Unity as part of the
        /// apply, because every one of those siblings is inside the same prefab.
        /// </summary>
        static void ApplyAdded(GameObject go)
        {
            if (go == null) return;
            if (AssetDatabase.LoadAssetAtPath<GameObject>(GamePrefabPath) == null) return; // first run: promoted
            if (!PrefabUtility.IsAddedGameObjectOverride(go)) return;                      // already in the asset
            PrefabUtility.ApplyAddedGameObject(go, GamePrefabPath, InteractionMode.AutomatedAction);
            Debug.Log("[MainSceneSetup] " + go.name + " applied into " + GamePrefabPath
                      + "; it is part of the kit now, not of this scene.");
        }

        static GameObject FindInactiveRoot(UnityEngine.SceneManagement.Scene scene, string name)
        {
            foreach (var root in scene.GetRootGameObjects())
                if (root.name == name) return root;
            return null;
        }
    }
}
