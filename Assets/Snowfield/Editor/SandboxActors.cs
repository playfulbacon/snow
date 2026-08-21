using Snowfield.Config;
using Snowfield.Player;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

namespace Snowfield.Editor
{
    /// <summary>
    /// Adds the player rig (character, orbit camera, sculpt tool, cursor) to the Sandbox scene if missing.
    /// Separate from ProjectBootstrap.EnsureScene so it can be re-run against a hand-edited scene.
    /// </summary>
    public static class SandboxActors
    {
        const string ScenePath = "Assets/Scenes/Sandbox.unity";
        const string ConfigPath = "Assets/Settings/SculptFeelConfig.asset";
        const string CursorMatPath = "Assets/Settings/BrushCursor.mat";

        [MenuItem("Snowfield/Ensure Sandbox Actors")]
        public static void Run()
        {
            var scene = EditorSceneManager.OpenScene(ScenePath, OpenSceneMode.Single);
            var config = AssetDatabase.LoadAssetAtPath<SculptFeelConfig>(ConfigPath);

            var cam = Camera.main;
            if (cam == null) { Debug.LogError("[Snowfield] Sandbox scene has no MainCamera"); return; }

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
                var ch = player.AddComponent<SnowCharacter>();
                ch.cameraRig = cam.transform;
            }
            // The brush ray starts behind the player; keep the player out of every raycast.
            int ignore = LayerMask.NameToLayer("Ignore Raycast");
            player.layer = ignore;
            foreach (Transform t in player.GetComponentsInChildren<Transform>(true)) t.gameObject.layer = ignore;

            var orbit = cam.GetComponent<OrbitCamera>();
            if (orbit == null) orbit = cam.gameObject.AddComponent<OrbitCamera>();
            orbit.target = player.transform;
            orbit.collisionMask = ~LayerMask.GetMask("Ignore Raycast");

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

            var tool = cam.GetComponent<SculptTool>();
            if (tool == null) tool = cam.gameObject.AddComponent<SculptTool>();
            tool.config = config;
            tool.viewCamera = cam;
            tool.cursor = cursorGo.transform;
            tool.sculptMask = ~LayerMask.GetMask("Ignore Raycast");

            EditorUtility.SetDirty(tool);
            EditorUtility.SetDirty(orbit);
            EditorSceneManager.MarkSceneDirty(scene);
            EditorSceneManager.SaveScene(scene);
            AssetDatabase.SaveAssets();
            Debug.Log("[Snowfield] Sandbox actors ensured.");
        }
    }
}
