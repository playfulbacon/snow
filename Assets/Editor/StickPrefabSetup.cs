using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Snowfield.Player;
using Snowfield.Sculpture;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

namespace SnowDays.EditorTools
{
    /// <summary>Bakes the editable stick collection into independent inventory and pickup prefabs.</summary>
    public static class StickPrefabSetup
    {
        public const string ModelPath = "Assets/Props/Sticks.blend";
        public const string Folder = "Assets/Props/Sticks";
        public const string DefinitionFolder = "Assets/Resources/Items/Sticks";

        static readonly string[] Names = { "Twig", "Short", "Long", "Forked", "Crooked", "Branchlet", "Thin", "Broken", "Snowy" };
        static readonly string[] Ids = { "twig", "stick_short", "stick_long", "stick_forked", "stick_crooked", "stick_branchlet", "stick_thin", "stick_broken", "stick_snowy" };
        static readonly string[] Labels = { "Twig", "Short Twig", "Long Stick", "Forked Stick", "Crooked Stick", "Branching Twig", "Thin Switch", "Broken Stick", "Snowy Branch" };
        static readonly string[] MaterialNames = { "Stick_Bark", "Stick_BarkWarm", "Stick_CutWood", "Stick_Frost" };
        static readonly Color[] MaterialColors = { new Color(.29f, .19f, .105f), new Color(.37f, .25f, .145f),
            new Color(.62f, .44f, .25f), new Color(.82f, .90f, .94f) };

        public static void BuildAndPreview() { Run(); Preview(); }

        [MenuItem("Snowfield/Items/Build Stick Prefabs")]
        public static void Run()
        {
            if (EditorApplication.isPlaying) throw new InvalidOperationException("Exit Play Mode before building stick assets");
            var source = AssetDatabase.LoadAssetAtPath<GameObject>(ModelPath);
            if (source == null) throw new InvalidOperationException("Sticks.blend has not imported successfully");
            var filters = source.GetComponentsInChildren<MeshFilter>(true).ToDictionary(f => f.name);
            if (filters.Count != Names.Length) throw new InvalidOperationException("Expected nine source stick objects");
            foreach (string directory in new[] { Folder + "/Prefabs", Folder + "/Pickups", Folder + "/Materials", DefinitionFolder })
                EnsureFolder(directory);
            var materials = EnsureMaterials();
            for (int i = 0; i < Names.Length; i++)
            {
                string name = $"Stick_{i + 1:00}_{Names[i]}";
                if (!filters.TryGetValue(name, out var filter)) throw new InvalidOperationException("Missing source mesh: " + name);
                var root = new GameObject(name);
                try
                {
                    var child = new GameObject("Mesh");
                    child.transform.SetParent(root.transform, false);
                    child.transform.localRotation = filter.transform.rotation;
                    child.transform.localScale = filter.transform.lossyScale;
                    child.AddComponent<MeshFilter>().sharedMesh = filter.sharedMesh;
                    var sourceMaterials = filter.GetComponent<MeshRenderer>().sharedMaterials;
                    var mapped = new Material[filter.sharedMesh.subMeshCount];
                    for (int m = 0; m < mapped.Length; m++)
                    {
                        string sourceName = m < sourceMaterials.Length && sourceMaterials[m] != null ? sourceMaterials[m].name : "";
                        int index = Array.FindLastIndex(MaterialNames, key => sourceName.StartsWith(key, StringComparison.Ordinal));
                        mapped[m] = materials[Mathf.Max(index, 0)];
                    }
                    child.AddComponent<MeshRenderer>().sharedMaterials = mapped;
                    var bounds = BoundsFor(filter.sharedMesh.bounds, child.transform.localToWorldMatrix);
                    child.transform.localPosition = Vector3.down * bounds.min.y;
                    bounds = BoundsFor(filter.sharedMesh.bounds, child.transform.localToWorldMatrix);
                    if (bounds.size.y < .2f || bounds.size.y > .8f)
                        throw new InvalidOperationException("Unexpected imported stick units: " + bounds.size);
                    var collider = root.AddComponent<BoxCollider>();
                    collider.center = bounds.center;
                    collider.size = bounds.size;
                    collider.isTrigger = true;
                    // Grip the lower trunk, independently of how far the upper forks spread out.
                    var grip = new GameObject("Grip").transform;
                    grip.SetParent(root.transform, false);
                    grip.localPosition = new Vector3(0f, Mathf.Min(.08f, bounds.size.y * .2f), 0f);
                    string prefabPath = Folder + "/Prefabs/" + name + ".prefab";
                    var prefab = PrefabUtility.SaveAsPrefabAsset(root, prefabPath);
                    string definitionPath = DefinitionFolder + "/" + name + ".asset";
                    var definition = AssetDatabase.LoadAssetAtPath<AccessoryItemDefinition>(definitionPath);
                    if (definition == null)
                    {
                        definition = ScriptableObject.CreateInstance<AccessoryItemDefinition>();
                        definition.itemId = Ids[i];
                        definition.displayName = Labels[i];
                        definition.stackLimit = 12;
                        definition.sink = Mathf.Min(.06f, bounds.size.y * .15f);
                        definition.groundLift = bounds.max.z + .008f;
                        AssetDatabase.CreateAsset(definition, definitionPath);
                    }
                    definition.prefab = prefab;
                    EditorUtility.SetDirty(definition);
                    BuildPickup(definition);
                }
                finally { UnityEngine.Object.DestroyImmediate(root); }
            }
            AssetDatabase.SaveAssets();
            AccessoryCatalog.ReloadDefinitions();
            Verify();
            Debug.Log("[Sticks] Built nine placement prefabs, pickup prefabs and item definitions");
        }

        static Material[] EnsureMaterials()
        {
            var result = new Material[MaterialNames.Length];
            for (int i = 0; i < result.Length; i++)
            {
                string path = Folder + "/Materials/" + MaterialNames[i] + ".mat";
                result[i] = AssetDatabase.LoadAssetAtPath<Material>(path);
                if (result[i] != null) continue;
                result[i] = new Material(Shader.Find("Universal Render Pipeline/Lit")) { name = MaterialNames[i] };
                result[i].SetColor("_BaseColor", MaterialColors[i]);
                result[i].SetFloat("_Smoothness", i == 3 ? .2f : .08f);
                AssetDatabase.CreateAsset(result[i], path);
            }
            return result;
        }

        static void BuildPickup(AccessoryItemDefinition definition)
        {
            var root = new GameObject(definition.prefab.name + "_Pickup");
            try
            {
                var model = (GameObject)PrefabUtility.InstantiatePrefab(definition.prefab);
                model.transform.SetParent(root.transform, false);
                model.transform.localRotation = Quaternion.Euler(definition.groundEulerAngles);
                model.transform.localPosition = Vector3.up * definition.groundLift;
                AccessoryCatalog.SetColliders(model, false);
                var pickup = root.AddComponent<ItemPickup>();
                pickup.itemId = definition.itemId;
                pickup.quantity = 1;
                pickup.worldModel = model;
                var bounds = model.GetComponentInChildren<Renderer>().bounds;
                var collider = root.AddComponent<BoxCollider>();
                collider.center = bounds.center;
                collider.size = Vector3.Max(bounds.size, new Vector3(.22f, .18f, .22f));
                collider.isTrigger = true;
                PrefabUtility.SaveAsPrefabAsset(root, Folder + "/Pickups/" + root.name + ".prefab");
            }
            finally { UnityEngine.Object.DestroyImmediate(root); }
        }

        static Bounds BoundsFor(Bounds bounds, Matrix4x4 matrix)
        {
            var result = new Bounds(matrix.MultiplyPoint3x4(bounds.min), Vector3.zero);
            for (int i = 0; i < 8; i++) result.Encapsulate(matrix.MultiplyPoint3x4(bounds.center + Vector3.Scale(bounds.extents,
                new Vector3((i & 1) == 0 ? -1 : 1, (i & 2) == 0 ? -1 : 1, (i & 4) == 0 ? -1 : 1))));
            return result;
        }

        static void EnsureFolder(string path)
        {
            if (AssetDatabase.IsValidFolder(path)) return;
            string parent = Path.GetDirectoryName(path).Replace('\\', '/');
            EnsureFolder(parent);
            AssetDatabase.CreateFolder(parent, Path.GetFileName(path));
        }

        [MenuItem("Snowfield/Items/Verify Stick Prefabs")]
        public static void Verify()
        {
            var definitions = Resources.LoadAll<AccessoryItemDefinition>("Items/Sticks");
            if (definitions.Length != Names.Length) throw new InvalidOperationException("Every stick needs its own definition");
            var meshes = new HashSet<Mesh>();
            foreach (var id in Ids)
            {
                var definition = definitions.Single(d => d.itemId == id);
                var mesh = definition.prefab.GetComponentInChildren<MeshFilter>().sharedMesh;
                if (!meshes.Add(mesh) || AssetDatabase.GetAssetPath(mesh) != ModelPath)
                    throw new InvalidOperationException("Each stick must use its own source mesh");
                if (AccessoryCatalog.Find(id)?.Prefab != definition.prefab)
                    throw new InvalidOperationException("Stick is missing from the runtime catalog: " + id);
                var pickup = AssetDatabase.LoadAssetAtPath<GameObject>(Folder + "/Pickups/" + definition.prefab.name + "_Pickup.prefab");
                if (pickup == null || pickup.GetComponent<ItemPickup>().worldModel == null || pickup.GetComponent<ItemPickup>().itemId != id)
                    throw new InvalidOperationException("Invalid stick pickup: " + id);
                if (definition.prefab.GetComponent<Collider>() == null || definition.stackLimit < 1)
                    throw new InvalidOperationException("Invalid stick collider or stack limit: " + id);
                if (definition.prefab.transform.Find("Grip") == null)
                    throw new InvalidOperationException("Stick needs an authored lower-trunk grip: " + id);
                var renderer = definition.prefab.GetComponentInChildren<MeshRenderer>();
                if (renderer.sharedMaterials.Length != mesh.subMeshCount || renderer.sharedMaterials.Any(m => m == null))
                    throw new InvalidOperationException("Missing bark/cut-end material: " + id);
            }
            Debug.Log("[Sticks] Verified all nine source meshes, catalog entries and pickup prefabs");
        }

        [MenuItem("Snowfield/Items/Preview Stick Prefabs")]
        public static void Preview()
        {
            var scene = EditorSceneManager.NewPreviewScene();
            RenderTexture rt = null;
            Texture2D image = null;
            var previous = RenderTexture.active;
            try
            {
                var root = new GameObject("StickPreview");
                UnityEngine.SceneManagement.SceneManager.MoveGameObjectToScene(root, scene);
                for (int i = 0; i < Ids.Length; i++)
                {
                    var definition = Resources.LoadAll<AccessoryItemDefinition>("Items/Sticks").Single(d => d.itemId == Ids[i]);
                    var go = UnityEngine.Object.Instantiate(definition.prefab, root.transform);
                    go.transform.localPosition = new Vector3((i % 3 - 1) * .9f, 1f - i / 3 * 1.05f, 0f);
                    go.transform.localRotation = Quaternion.Euler(0f, -12f, -4f);
                    var labelGo = new GameObject("Label");
                    labelGo.transform.SetParent(root.transform, false);
                    labelGo.transform.localPosition = go.transform.localPosition + new Vector3(0f, -.12f, 0f);
                    var label = labelGo.AddComponent<TextMesh>();
                    label.font = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");
                    labelGo.GetComponent<MeshRenderer>().sharedMaterial = label.font.material;
                    label.text = Labels[i];
                    label.anchor = TextAnchor.MiddleCenter;
                    label.fontSize = 64;
                    label.characterSize = .013f;
                    label.color = new Color(.92f, .94f, .98f);
                }
                var cameraGo = new GameObject("Camera");
                cameraGo.transform.SetParent(root.transform, false);
                var camera = cameraGo.AddComponent<Camera>();
                camera.scene = scene;
                camera.cameraType = CameraType.Preview;
                camera.enabled = false;
                camera.clearFlags = CameraClearFlags.SolidColor;
                camera.backgroundColor = new Color(.055f, .075f, .105f);
                camera.orthographic = true;
                camera.orthographicSize = 1.65f;
                camera.nearClipPlane = .01f;
                camera.farClipPlane = 20f;
                cameraGo.transform.position = new Vector3(0f, .32f, -5f);
                var lightGo = new GameObject("Light");
                lightGo.transform.SetParent(root.transform, false);
                lightGo.transform.localRotation = Quaternion.Euler(25f, -25f, 0f);
                var light = lightGo.AddComponent<Light>();
                light.type = LightType.Directional;
                light.intensity = 1.5f;
                rt = new RenderTexture(1200, 1200, 24, RenderTextureFormat.ARGB32);
                camera.targetTexture = rt;
                camera.Render();
                RenderTexture.active = rt;
                image = new Texture2D(1200, 1200, TextureFormat.RGB24, false);
                image.ReadPixels(new Rect(0, 0, 1200, 1200), 0, 0);
                image.Apply();
                Directory.CreateDirectory("Screenshots/Items");
                File.WriteAllBytes("Screenshots/Items/sticks.png", image.EncodeToPNG());
            }
            finally
            {
                RenderTexture.active = previous;
                if (image != null) UnityEngine.Object.DestroyImmediate(image);
                if (rt != null) UnityEngine.Object.DestroyImmediate(rt);
                EditorSceneManager.ClosePreviewScene(scene);
            }
        }
    }
}
