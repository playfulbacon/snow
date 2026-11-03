using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Snowfield.Player;
using Snowfield.Sculpture;
using Unity.Collections;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

namespace SnowDays.EditorTools
{
    /// <summary>Builds individual inventory items from the artist's carrot model collection.</summary>
    public static class CarrotPrefabSetup
    {
        public const string ModelPath = "Assets/Props/Carrots.blend";
        public const string Folder = "Assets/Props/Carrots";
        public const string DefinitionFolder = "Assets/Resources/Items/Carrots";
        const float ModelScale = 0.05f;

        [Serializable] class BuildReport { public List<Variant> variants = new List<Variant>(); }
        [Serializable] class Variant
        {
            public string sourceMesh, itemId, prefab, pickup;
            public Vector3 size;
        }

        [MenuItem("Snowfield/Items/Build Carrot Prefabs")]
        public static void Run()
        {
            if (EditorApplication.isPlaying) throw new InvalidOperationException("Exit Play Mode before building carrot assets");
            var model = AssetDatabase.LoadAssetAtPath<GameObject>(ModelPath);
            if (model == null) throw new InvalidOperationException("Carrots.blend has no imported model");
            var filters = model.GetComponentsInChildren<MeshFilter>(true)
                .Where(f => f.sharedMesh != null).OrderBy(f => f.name, StringComparer.Ordinal).ToArray();
            if (filters.Length == 0) throw new InvalidOperationException("No carrot meshes were imported");
            EnsureFolder(Folder + "/Prefabs");
            EnsureFolder(Folder + "/Pickups");
            EnsureFolder(Folder + "/Materials");
            EnsureFolder(DefinitionFolder);
            var material = AssetDatabase.LoadAssetAtPath<Material>(Folder + "/Materials/Carrot.mat");
            if (material == null)
            {
                material = new Material(Shader.Find("Universal Render Pipeline/Lit")) { name = "Carrot" };
                material.SetColor("_BaseColor", new Color(0.95f, 0.40f, 0.075f));
                material.SetFloat("_Smoothness", 0.22f);
                AssetDatabase.CreateAsset(material, Folder + "/Materials/Carrot.mat");
            }

            var report = new BuildReport();
            for (int i = 0; i < filters.Length; i++)
            {
                var source = filters[i];
                string suffix = source.name.Replace('.', '_');
                string assetName = source.name == "Carrot1" ? "Carrot1" : "Carrot_" + suffix;
                string id = source.name == "Carrot1" ? "carrot" : "carrot_" + suffix.ToLowerInvariant();
                string prefabPath = Folder + "/Prefabs/" + assetName + ".prefab";
                string pickupPath = Folder + "/Pickups/" + assetName + "_Pickup.prefab";
                var root = new GameObject(assetName);
                try
                {
                    var meshObject = new GameObject(source.name);
                    meshObject.transform.SetParent(root.transform, false);
                    meshObject.AddComponent<MeshFilter>().sharedMesh = source.sharedMesh;
                    meshObject.AddComponent<MeshRenderer>().sharedMaterials = Enumerable.Repeat(material, source.sharedMesh.subMeshCount).ToArray();
                    meshObject.transform.localRotation = source.transform.rotation;
                    meshObject.transform.localScale = source.transform.lossyScale * ModelScale;
                    var meshBounds = source.sharedMesh.bounds;
                    var matrix = meshObject.transform.localToWorldMatrix;
                    var bounds = TransformBounds(meshBounds, matrix);
                    // Point the carrot tip out along +Y, with the broad base at the placement origin.
                    if (WiderEndIsTop(source.sharedMesh, matrix, bounds))
                    {
                        meshObject.transform.localRotation = Quaternion.Euler(180f, 0f, 0f) * meshObject.transform.localRotation;
                        bounds = TransformBounds(meshBounds, meshObject.transform.localToWorldMatrix);
                    }
                    meshObject.transform.localPosition = -new Vector3(bounds.center.x, bounds.min.y, bounds.center.z);
                    bounds = TransformBounds(meshBounds, meshObject.transform.localToWorldMatrix);
                    var collider = root.AddComponent<BoxCollider>();
                    collider.center = bounds.center;
                    collider.size = bounds.size;
                    collider.isTrigger = true;
                    var prefab = PrefabUtility.SaveAsPrefabAsset(root, prefabPath);
                    string definitionPath = DefinitionFolder + "/" + assetName + ".asset";
                    var definition = AssetDatabase.LoadAssetAtPath<AccessoryItemDefinition>(definitionPath);
                    if (definition == null)
                    {
                        definition = ScriptableObject.CreateInstance<AccessoryItemDefinition>();
                        definition.itemId = id;
                        definition.displayName = source.name == "Carrot1" ? "Carrot" : "Carrot " + (i + 1);
                        definition.stackLimit = 6;
                        definition.sink = Mathf.Min(0.04f, bounds.size.y * 0.15f);
                        definition.groundLift = bounds.extents.z + 0.005f;
                        AssetDatabase.CreateAsset(definition, definitionPath);
                    }
                    definition.prefab = prefab;
                    EditorUtility.SetDirty(definition);
                    BuildPickupPrefab(definition, pickupPath);
                    report.variants.Add(new Variant { sourceMesh = source.sharedMesh.name, itemId = id,
                        prefab = prefabPath, pickup = pickupPath, size = bounds.size });
                }
                finally { UnityEngine.Object.DestroyImmediate(root); }
            }
            AssetDatabase.SaveAssets();
            AccessoryCatalog.ReloadDefinitions();
            Directory.CreateDirectory("Temp");
            File.WriteAllText("Temp/carrot-prefabs.json", JsonUtility.ToJson(report, true));
            Verify();
            Debug.Log($"[Carrots] Built {report.variants.Count} placement prefabs, pickup prefabs, and item definitions");
        }

        static void EnsureFolder(string path)
        {
            if (AssetDatabase.IsValidFolder(path)) return;
            string parent = Path.GetDirectoryName(path).Replace('\\', '/');
            EnsureFolder(parent);
            AssetDatabase.CreateFolder(parent, Path.GetFileName(path));
        }

        static Bounds TransformBounds(Bounds local, Matrix4x4 matrix)
        {
            var result = new Bounds(matrix.MultiplyPoint3x4(local.min), Vector3.zero);
            for (int i = 0; i < 8; i++)
                result.Encapsulate(matrix.MultiplyPoint3x4(local.center + Vector3.Scale(local.extents,
                    new Vector3((i & 1) == 0 ? -1 : 1, (i & 2) == 0 ? -1 : 1, (i & 4) == 0 ? -1 : 1))));
            return result;
        }

        static bool WiderEndIsTop(Mesh mesh, Matrix4x4 matrix, Bounds bounds)
        {
            using var data = MeshUtility.AcquireReadOnlyMeshData(mesh);
            using var vertices = new NativeArray<Vector3>(mesh.vertexCount, Allocator.Temp);
            data[0].GetVertices(vertices);
            var bands = new Bounds[10];
            var populated = new bool[bands.Length];
            foreach (var vertex in vertices)
            {
                var p = matrix.MultiplyPoint3x4(vertex);
                int band = Mathf.Clamp((int)((p.y - bounds.min.y) / bounds.size.y * bands.Length), 0, bands.Length - 1);
                if (!populated[band]) { bands[band] = new Bounds(p, Vector3.zero); populated[band] = true; }
                else bands[band].Encapsulate(p);
            }
            // Measure thickness about each band's own centre, so bent carrots do not mistake sideways
            // curvature for a broad end. Sum several bands to include sparse low-poly cross sections.
            float lower = 0f, upper = 0f;
            for (int i = 0; i < 3; i++)
            {
                lower += bands[i].size.x + bands[i].size.z;
                upper += bands[9 - i].size.x + bands[9 - i].size.z;
            }
            return upper > lower;
        }

        static void BuildPickupPrefab(AccessoryItemDefinition definition, string path)
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
                collider.size = Vector3.Max(bounds.size, new Vector3(0.22f, 0.18f, 0.22f));
                collider.isTrigger = true;
                PrefabUtility.SaveAsPrefabAsset(root, path);
            }
            finally { UnityEngine.Object.DestroyImmediate(root); }
        }

        [MenuItem("Snowfield/Items/Verify Carrot Prefabs")]
        public static void Verify()
        {
            var definitions = Resources.LoadAll<AccessoryItemDefinition>("Items/Carrots");
            var sources = AssetDatabase.LoadAssetAtPath<GameObject>(ModelPath).GetComponentsInChildren<MeshFilter>(true);
            if (definitions.Length != sources.Length) throw new InvalidOperationException("Every source mesh must have its own definition");
            var meshes = new HashSet<Mesh>();
            var ids = new HashSet<string>();
            foreach (var definition in definitions)
            {
                if (!ids.Add(definition.itemId)) throw new InvalidOperationException("Duplicate carrot item id");
                var mesh = definition.prefab.GetComponentInChildren<MeshFilter>().sharedMesh;
                if (!meshes.Add(mesh) || AssetDatabase.GetAssetPath(mesh) != ModelPath)
                    throw new InvalidOperationException("Each carrot prefab must use a different mesh from Carrots.blend");
                var entry = AccessoryCatalog.Find(definition.itemId);
                if (entry == null || entry.Prefab != definition.prefab) throw new InvalidOperationException("Carrot missing from runtime catalog");
                string pickupPath = Folder + "/Pickups/" + definition.prefab.name + "_Pickup.prefab";
                var pickup = AssetDatabase.LoadAssetAtPath<GameObject>(pickupPath).GetComponent<ItemPickup>();
                if (pickup.itemId != definition.itemId || pickup.worldModel == null || pickup.quantity != 1)
                    throw new InvalidOperationException("Pickup prefab has incomplete item wiring");
                if (definition.prefab.GetComponent<Collider>() == null) throw new InvalidOperationException("Placement prefab needs a retrieval collider");
            }
            Debug.Log($"[Carrots] Verified all {definitions.Length} variants and their pickup/placement wiring");
        }

        [MenuItem("Snowfield/Items/Preview Carrot Prefabs")]
        public static void Preview()
        {
            var scene = EditorSceneManager.NewPreviewScene();
            RenderTexture rt = null;
            Texture2D image = null;
            var previous = RenderTexture.active;
            try
            {
                var root = new GameObject("CarrotPreview");
                UnityEngine.SceneManagement.SceneManager.MoveGameObjectToScene(root, scene);
                var definitions = Resources.LoadAll<AccessoryItemDefinition>("Items/Carrots")
                    .OrderBy(d => d.itemId, StringComparer.Ordinal).ToArray();
                for (int i = 0; i < definitions.Length; i++)
                {
                    var go = UnityEngine.Object.Instantiate(definitions[i].prefab, root.transform);
                    go.transform.localPosition = new Vector3((i % 3 - 1) * 0.65f, 0.7f - i / 3 * 0.8f, 0f);
                    var labelGo = new GameObject("Label");
                    labelGo.transform.SetParent(root.transform, false);
                    labelGo.transform.localPosition = go.transform.localPosition + new Vector3(0f, -0.13f, 0f);
                    var label = labelGo.AddComponent<TextMesh>();
                    label.font = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");
                    labelGo.GetComponent<MeshRenderer>().sharedMaterial = label.font.material;
                    label.text = definitions[i].displayName;
                    label.fontSize = 64;
                    label.characterSize = 0.025f;
                    label.anchor = TextAnchor.MiddleCenter;
                    label.color = new Color(0.92f, 0.94f, 0.98f);
                }
                var cameraGo = new GameObject("Camera");
                cameraGo.transform.SetParent(root.transform, false);
                var camera = cameraGo.AddComponent<Camera>();
                camera.cameraType = CameraType.Preview;
                camera.scene = scene;
                camera.enabled = false;
                camera.clearFlags = CameraClearFlags.SolidColor;
                camera.backgroundColor = new Color(0.055f, 0.075f, 0.105f);
                camera.orthographic = true;
                camera.orthographicSize = 1.25f;
                camera.nearClipPlane = 0.01f;
                camera.farClipPlane = 20f;
                cameraGo.transform.position = new Vector3(0f, 0.15f, -5f);
                var lightGo = new GameObject("Light");
                lightGo.transform.SetParent(root.transform, false);
                var light = lightGo.AddComponent<Light>();
                light.type = LightType.Directional;
                light.intensity = 1.5f;
                lightGo.transform.rotation = Quaternion.Euler(25f, -25f, 0f);
                rt = new RenderTexture(1200, 1200, 24, RenderTextureFormat.ARGB32);
                camera.targetTexture = rt;
                camera.Render();
                RenderTexture.active = rt;
                image = new Texture2D(1200, 1200, TextureFormat.RGB24, false);
                image.ReadPixels(new Rect(0, 0, 1200, 1200), 0, 0);
                image.Apply();
                Directory.CreateDirectory("Screenshots/Items");
                File.WriteAllBytes("Screenshots/Items/carrots.png", image.EncodeToPNG());
            }
            finally
            {
                RenderTexture.active = previous;
                if (image != null) UnityEngine.Object.DestroyImmediate(image);
                if (rt != null) UnityEngine.Object.DestroyImmediate(rt);
                EditorSceneManager.ClosePreviewScene(scene);
            }
        }

        [Serializable] class Inspection { public List<MeshInfo> meshes = new List<MeshInfo>(); }
        [Serializable] class MeshInfo
        {
            public string objectName, meshName;
            public int vertices, subMeshes;
            public Vector3 position, scale, boundsCenter, boundsSize;
            public Quaternion rotation;
            public string[] materials;
            public Color[] colors;
        }

        public static void Inspect()
        {
            var model = AssetDatabase.LoadAssetAtPath<GameObject>(ModelPath);
            if (model == null) throw new InvalidOperationException("Carrots.blend has no imported model");
            var result = new Inspection();
            foreach (var filter in model.GetComponentsInChildren<MeshFilter>(true))
            {
                var mesh = filter.sharedMesh;
                var renderer = filter.GetComponent<MeshRenderer>();
                var mats = renderer != null ? renderer.sharedMaterials : Array.Empty<Material>();
                var info = new MeshInfo
                {
                    objectName = filter.name, meshName = mesh.name,
                    vertices = mesh.vertexCount, subMeshes = mesh.subMeshCount,
                    position = filter.transform.position, rotation = filter.transform.rotation,
                    scale = filter.transform.lossyScale, boundsCenter = mesh.bounds.center, boundsSize = mesh.bounds.size,
                    materials = new string[mats.Length], colors = new Color[mats.Length],
                };
                for (int i = 0; i < mats.Length; i++)
                {
                    var mat = mats[i];
                    info.materials[i] = mat != null ? mat.name + " (" + mat.shader.name + ")" : "null";
                    info.colors[i] = mat != null && mat.HasProperty("_BaseColor") ? mat.GetColor("_BaseColor")
                        : mat != null && mat.HasProperty("_Color") ? mat.GetColor("_Color") : Color.white;
                }
                result.meshes.Add(info);
            }
            Directory.CreateDirectory("Temp");
            File.WriteAllText("Temp/carrot-inspection.json", JsonUtility.ToJson(result, true));
            Debug.Log($"[Carrots] Inspected {result.meshes.Count} mesh objects");
        }
    }
}
