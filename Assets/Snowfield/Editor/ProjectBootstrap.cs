using System.IO;
using Snowfield.Config;
using Snowfield.Sculpture;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;
using UnityEngine.SceneManagement;

namespace Snowfield.Editor
{
    /// <summary>
    /// One-shot project setup so the repo can be rebuilt from scripts: URP asset + renderer, feel config,
    /// snow material, and the Sandbox scene. Idempotent. Run from the menu or
    /// `Unity -batchmode -executeMethod Snowfield.Editor.ProjectBootstrap.Run`.
    /// </summary>
    public static class ProjectBootstrap
    {
        const string SettingsDir = "Assets/Settings";
        const string ScenesDir = "Assets/Scenes";
        const string RendererPath = SettingsDir + "/URP-Renderer.asset";
        const string PipelinePath = SettingsDir + "/URP-Pipeline.asset";
        const string ConfigPath = SettingsDir + "/SculptFeelConfig.asset";
        const string MaterialPath = SettingsDir + "/Snow.mat";
        const string GroundMaterialPath = SettingsDir + "/Ground.mat";
        const string ScenePath = ScenesDir + "/Sandbox.unity";

        [MenuItem("Snowfield/Bootstrap Project")]
        public static void Run()
        {
            Directory.CreateDirectory(SettingsDir);
            Directory.CreateDirectory(ScenesDir);

            EnsureUrp();
            EnsureAsset<SculptFeelConfig>(ConfigPath);
            EnsureSnowMaterial();
            EnsureGroundMaterial();
            // Save + reload so the scene references persistent assets (fresh instances can serialise as null).
            AssetDatabase.SaveAssets();
            AssetDatabase.Refresh();
            var config = AssetDatabase.LoadAssetAtPath<SculptFeelConfig>(ConfigPath);
            var material = AssetDatabase.LoadAssetAtPath<Material>(MaterialPath);
            var ground = AssetDatabase.LoadAssetAtPath<Material>(GroundMaterialPath);
            EnsureScene(config, material, ground);

            AssetDatabase.SaveAssets();
            AssetDatabase.Refresh();
            Debug.Log("[Snowfield] Bootstrap complete.");
        }

        static UniversalRenderPipelineAsset EnsureUrp()
        {
            var renderer = AssetDatabase.LoadAssetAtPath<UniversalRendererData>(RendererPath);
            if (renderer == null)
            {
                renderer = ScriptableObject.CreateInstance<UniversalRendererData>();
                AssetDatabase.CreateAsset(renderer, RendererPath);
            }
            var pipeline = AssetDatabase.LoadAssetAtPath<UniversalRenderPipelineAsset>(PipelinePath);
            if (pipeline == null)
            {
                pipeline = UniversalRenderPipelineAsset.Create(renderer);
                AssetDatabase.CreateAsset(pipeline, PipelinePath);
            }
            GraphicsSettings.defaultRenderPipeline = pipeline;
            QualitySettings.renderPipeline = pipeline;
            // Soft shadows + a couple of cascades make matte snow read as a surface.
            var so = new SerializedObject(pipeline);
            so.FindProperty("m_SoftShadowsSupported").boolValue = true;
            so.FindProperty("m_ShadowDistance").floatValue = 60f;
            so.FindProperty("m_MainLightShadowmapResolution").intValue = 2048;
            so.ApplyModifiedPropertiesWithoutUndo();
            EditorUtility.SetDirty(pipeline);
            return pipeline;
        }

        static T EnsureAsset<T>(string path) where T : ScriptableObject
        {
            var a = AssetDatabase.LoadAssetAtPath<T>(path);
            if (a == null)
            {
                a = ScriptableObject.CreateInstance<T>();
                AssetDatabase.CreateAsset(a, path);
            }
            return a;
        }

        static Material EnsureSnowMaterial()
        {
            var m = AssetDatabase.LoadAssetAtPath<Material>(MaterialPath);
            if (m == null)
            {
                m = new Material(Shader.Find("Universal Render Pipeline/Lit")) { name = "Snow" };
                m.SetColor("_BaseColor", new Color(0.88f, 0.91f, 0.97f));
                m.SetFloat("_Smoothness", 0.12f);
                m.SetFloat("_Metallic", 0f);
                AssetDatabase.CreateAsset(m, MaterialPath);
            }
            return m;
        }

        static Material EnsureGroundMaterial()
        {
            var m = AssetDatabase.LoadAssetAtPath<Material>(GroundMaterialPath);
            if (m == null)
            {
                m = new Material(Shader.Find("Universal Render Pipeline/Lit")) { name = "Ground" };
                m.SetColor("_BaseColor", new Color(0.78f, 0.82f, 0.9f));
                m.SetFloat("_Smoothness", 0.08f);
                m.SetFloat("_Metallic", 0f);
                AssetDatabase.CreateAsset(m, GroundMaterialPath);
            }
            return m;
        }

        static void EnsureScene(SculptFeelConfig config, Material snow, Material groundMat)
        {
            if (File.Exists(ScenePath)) return; // never clobber a hand-edited scene

            var scene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
            RenderSettings.ambientMode = AmbientMode.Trilight;
            RenderSettings.ambientSkyColor = new Color(0.35f, 0.42f, 0.55f);
            RenderSettings.ambientEquatorColor = new Color(0.32f, 0.34f, 0.4f);
            RenderSettings.ambientGroundColor = new Color(0.22f, 0.22f, 0.26f);
            RenderSettings.fog = true;
            RenderSettings.fogMode = FogMode.ExponentialSquared;
            RenderSettings.fogColor = new Color(0.75f, 0.8f, 0.9f);
            RenderSettings.fogDensity = 0.012f;

            var sun = new GameObject("Sun").AddComponent<Light>();
            sun.type = LightType.Directional;
            sun.color = new Color(1f, 0.96f, 0.9f);
            sun.intensity = 1.0f;
            sun.shadows = LightShadows.Soft;
            sun.transform.rotation = Quaternion.Euler(38f, -35f, 0f);

            var ground = new GameObject("Ground");
            ground.transform.position = new Vector3(-config.terrainFieldSize * 0.5f, 0f, -config.terrainFieldSize * 0.5f);
            var terrain = ground.AddComponent<Snowfield.Field.SnowTerrain>();
            terrain.EditorAssign(config, groundMat);

            var camGo = new GameObject("Main Camera");
            camGo.tag = "MainCamera";
            var cam = camGo.AddComponent<Camera>();
            camGo.AddComponent<UniversalAdditionalCameraData>();
            cam.clearFlags = CameraClearFlags.Skybox;
            camGo.transform.position = new Vector3(0f, 2.2f, -4.5f);
            camGo.transform.LookAt(new Vector3(0f, 0.6f, 0f));

            var sculpturesGo = new GameObject("Sculptures");
            var factory = sculpturesGo.AddComponent<SculptureFactory>();
            factory.config = config; // direct assignment: SerializedProperty drops custom SO refs in batchmode
            factory.snowMaterial = snow;
            factory.container = sculpturesGo.transform;
            EditorUtility.SetDirty(factory);

            EditorSceneManager.SaveScene(scene, ScenePath);
            EditorBuildSettings.scenes = new[] { new EditorBuildSettingsScene(ScenePath, true) };
        }
    }
}
