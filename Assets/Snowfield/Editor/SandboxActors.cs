using Snowfield.Config;
using Snowfield.Player;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

namespace Snowfield.Editor
{
    /// <summary>
    /// Ensures the player rig and HUD exist in the Sandbox scene, grouped by responsibility:
    ///
    ///   Player          SnowCharacter
    ///    ├ CameraRig    FirstPersonCamera (moves Main Camera)
    ///    ├ SculptTool   SculptTool + AccessoryPlacer + SnowballRoller + AccessoryInventory
    ///    └ CarryAnchor  authored hold point for carried objects
    ///   Main Camera     Camera only
    ///   HUD             Canvas + CanvasScaler + ToolHud
    ///
    /// Idempotent, and migrates the older layout where everything hung off the camera.
    /// </summary>
    public static class SandboxActors
    {
        const string ScenePath = "Assets/Scenes/Sandbox.unity";
        const string ConfigPath = "Assets/Settings/SculptFeelConfig.asset";
        const string CursorMatPath = "Assets/Settings/BrushCursor.mat";
        const string SnowMatPath = "Assets/Settings/Snow.mat";
        const string GroundMatPath = "Assets/Settings/Ground.mat";

        [MenuItem("Snowfield/Ensure Sandbox Actors")]
        public static void Run()
        {
            var scene = EditorSceneManager.OpenScene(ScenePath, OpenSceneMode.Single);
            var config = AssetDatabase.LoadAssetAtPath<SculptFeelConfig>(ConfigPath);

            var cam = Camera.main;
            if (cam == null) { Debug.LogError("[Snowfield] Sandbox scene has no MainCamera"); return; }

            // --- migrate: strip behaviour components that used to live on the camera ---
            StripComponent<SculptTool>(cam.gameObject);
            StripComponent<AccessoryPlacer>(cam.gameObject);
            StripComponent<ToolHud>(cam.gameObject);
            StripComponent<OrbitCamera>(cam.gameObject);

            // --- ground: heightmap field replaces the old Plane ---
            var groundGo = GameObject.Find("Ground");
            if (groundGo == null) groundGo = new GameObject("Ground");
            if (groundGo.GetComponent<Snowfield.Field.SnowTerrain>() == null)
            {
                // strip the primitive plane parts
                foreach (var c in new System.Type[] { typeof(MeshCollider), typeof(MeshRenderer), typeof(MeshFilter) })
                {
                    var comp = groundGo.GetComponent(c);
                    if (comp != null) Object.DestroyImmediate(comp);
                }
                groundGo.transform.localScale = Vector3.one;
                var terrain = groundGo.AddComponent<Snowfield.Field.SnowTerrain>();
                terrain.EditorAssign(config, AssetDatabase.LoadAssetAtPath<Material>(GroundMatPath));
                EditorUtility.SetDirty(terrain);
            }
            groundGo.transform.position = new Vector3(-config.terrainFieldSize * 0.5f, 0f, -config.terrainFieldSize * 0.5f);

            // --- sculptures: factory root; the pre-placed starter mound is retired ---
            var starter = GameObject.Find("Sculpture");
            if (starter != null && starter.GetComponent<Snowfield.Sculpture.SculptureSpawner>() != null) Object.DestroyImmediate(starter);
            var sculpturesGo = GameObject.Find("Sculptures");
            if (sculpturesGo == null) sculpturesGo = new GameObject("Sculptures");
            var factory = sculpturesGo.GetComponent<Snowfield.Sculpture.SculptureFactory>();
            if (factory == null) factory = sculpturesGo.AddComponent<Snowfield.Sculpture.SculptureFactory>();
            factory.config = config;
            factory.snowMaterial = AssetDatabase.LoadAssetAtPath<Material>(SnowMatPath);
            factory.container = sculpturesGo.transform;
            EditorUtility.SetDirty(factory);

            // --- player ---
            var player = GameObject.Find("Player");
            if (player == null)
            {
                player = new GameObject("Player");
                player.transform.position = new Vector3(0f, 1.0f, -3.2f);
                var cc = player.AddComponent<CharacterController>();
                cc.height = 1.8f; cc.radius = 0.35f; cc.center = new Vector3(0f, 0.9f, 0f);
                var body = GameObject.CreatePrimitive(PrimitiveType.Capsule);
                body.name = "Body";
                Object.DestroyImmediate(body.GetComponent<Collider>());
                body.transform.SetParent(player.transform, false);
                body.transform.localPosition = new Vector3(0f, 0.9f, 0f);
                body.transform.localScale = new Vector3(0.7f, 0.9f, 0.7f);
            }
            var character = player.GetComponent<SnowCharacter>();
            if (character == null) character = player.AddComponent<SnowCharacter>();
            character.cameraRig = cam.transform;
            character.faceMovementDirection = false; // first person: the rig sets yaw

            // The brush ray starts behind the player; keep the player out of every raycast.
            int ignore = LayerMask.NameToLayer("Ignore Raycast");
            foreach (Transform t in player.GetComponentsInChildren<Transform>(true)) t.gameObject.layer = ignore;

            // --- first-person camera rig (child of player); retire any orbit rig ---
            var oldOrbit = player.transform.Find("OrbitCamera");
            if (oldOrbit != null) Object.DestroyImmediate(oldOrbit.gameObject);
            var rigGo = Child(player.transform, "CameraRig");
            StripComponent<OrbitCamera>(rigGo);
            var rig = rigGo.GetComponent<FirstPersonCamera>();
            if (rig == null) rig = rigGo.AddComponent<FirstPersonCamera>();
            rig.cameraTransform = cam.transform;
            rig.character = character;

            // --- brush cursor ---
            var cursorGo = GameObject.Find("BrushCursor");
            if (cursorGo == null)
            {
                cursorGo = GameObject.CreatePrimitive(PrimitiveType.Sphere);
                cursorGo.name = "BrushCursor";
                Object.DestroyImmediate(cursorGo.GetComponent<Collider>());
                var mat = AssetDatabase.LoadAssetAtPath<Material>(CursorMatPath);
                if (mat == null)
                {
                    mat = new Material(Shader.Find("Universal Render Pipeline/Lit")) { name = "BrushCursor" };
                    mat.SetFloat("_Surface", 1f); // transparent
                    mat.SetFloat("_Blend", 0f);
                    mat.SetOverrideTag("RenderType", "Transparent");
                    mat.renderQueue = (int)UnityEngine.Rendering.RenderQueue.Transparent;
                    mat.SetInt("_SrcBlend", (int)UnityEngine.Rendering.BlendMode.SrcAlpha);
                    mat.SetInt("_DstBlend", (int)UnityEngine.Rendering.BlendMode.OneMinusSrcAlpha);
                    mat.SetInt("_ZWrite", 0);
                    mat.EnableKeyword("_SURFACE_TYPE_TRANSPARENT");
                    mat.SetColor("_BaseColor", new Color(0.4f, 0.7f, 1f, 0.25f));
                    AssetDatabase.CreateAsset(mat, CursorMatPath);
                }
                cursorGo.GetComponent<MeshRenderer>().sharedMaterial = mat;
                cursorGo.GetComponent<MeshRenderer>().shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
                cursorGo.SetActive(false);
            }

            // --- sculpt tool (child of player) ---
            var toolGo = Child(player.transform, "SculptTool");
            var tool = toolGo.GetComponent<SculptTool>();
            if (tool == null) tool = toolGo.AddComponent<SculptTool>();
            tool.config = config;
            tool.viewCamera = cam;
            tool.reachOrigin = player.transform;
            tool.cursor = cursorGo.transform;
            tool.sculptMask = ~LayerMask.GetMask("Ignore Raycast");
            var placer = toolGo.GetComponent<AccessoryPlacer>();
            if (placer == null) placer = toolGo.AddComponent<AccessoryPlacer>();
            var inventory = toolGo.GetComponent<AccessoryInventory>();
            if (inventory == null) inventory = toolGo.AddComponent<AccessoryInventory>();
            var roller = toolGo.GetComponent<SnowballRoller>();
            if (roller == null) roller = toolGo.AddComponent<SnowballRoller>();
            roller.config = config;
            roller.character = character;
            roller.groundMask = ~LayerMask.GetMask("Ignore Raycast");
            var anchorT = player.transform.Find("CarryAnchor");
            if (anchorT == null)
            {
                var anchor = new GameObject("CarryAnchor");
                anchor.transform.SetParent(player.transform, false);
                anchor.transform.localPosition = new Vector3(0f, 2.0f, 0.5f); // author freely in the scene
                anchor.layer = ignore;
                anchorT = anchor.transform;
            }
            roller.carryAnchor = anchorT;
            EditorUtility.SetDirty(roller);

            // --- loose items scattered over the field ---
            var scatterGo = GameObject.Find("FieldScatter");
            if (scatterGo == null) scatterGo = new GameObject("FieldScatter");
            var scatter = scatterGo.GetComponent<Snowfield.Field.FieldScatter>();
            if (scatter == null) scatter = scatterGo.AddComponent<Snowfield.Field.FieldScatter>();
            scatter.groundMask = ~LayerMask.GetMask("Ignore Raycast");
            EditorUtility.SetDirty(scatter);

            // --- snowfall cycle (own root object) ---
            var snowfallGo = GameObject.Find("Snowfall");
            if (snowfallGo == null) snowfallGo = new GameObject("Snowfall");
            var cycle = snowfallGo.GetComponent<Snowfield.Field.SnowfallCycle>();
            if (cycle == null) cycle = snowfallGo.AddComponent<Snowfield.Field.SnowfallCycle>();
            cycle.config = config;
            EditorUtility.SetDirty(cycle);

            // --- HUD (own root object) ---
            var hudGo = GameObject.Find("HUD");
            if (hudGo == null) hudGo = new GameObject("HUD", typeof(RectTransform));
            var hud = hudGo.GetComponent<ToolHud>();
            if (hud == null) hud = hudGo.AddComponent<ToolHud>();
            hud.tool = tool;
            hud.placer = placer;
            hud.RebuildNow();

            foreach (var o in new Object[] { character, rig, tool, placer, hud }) EditorUtility.SetDirty(o);
            EditorSceneManager.MarkSceneDirty(scene);
            EditorSceneManager.SaveScene(scene);
            AssetDatabase.SaveAssets();
            Debug.Log("[Snowfield] Sandbox actors ensured.");
        }

        static GameObject Child(Transform parent, string name)
        {
            var t = parent.Find(name);
            if (t != null) return t.gameObject;
            var go = new GameObject(name);
            go.transform.SetParent(parent, false);
            go.layer = parent.gameObject.layer;
            return go;
        }

        static void StripComponent<T>(GameObject go) where T : Component
        {
            var c = go.GetComponent<T>();
            if (c != null) Object.DestroyImmediate(c);
        }
    }
}
