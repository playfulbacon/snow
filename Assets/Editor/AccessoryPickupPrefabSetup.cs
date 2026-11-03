using System;
using System.IO;
using Snowfield.Player;
using Snowfield.Sculpture;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

namespace SnowDays.EditorTools
{
    /// <summary>Visible, level-authorable pickups for the remaining procedural accessories.</summary>
    public static class AccessoryPickupPrefabSetup
    {
        public const string Folder = "Assets/Props/Accessories";
        static readonly string[] Ids = { "scarf", "lights", "button", "pebble" };

        public static void BuildAndPreview() { Run(); Preview(); }

        [MenuItem("Snowfield/Items/Build Accessory Pickup Prefabs")]
        public static void Run()
        {
            if (EditorApplication.isPlaying) throw new InvalidOperationException("Exit Play Mode before building pickup assets");
            foreach (string suffix in new[] { "/Prefabs", "/Pickups", "/Materials" }) EnsureFolder(Folder + suffix);
            foreach (string id in Ids)
            {
                var entry = AccessoryCatalog.Find(id);
                var model = entry.Build();
                try
                {
                    model.name = entry.DisplayName;
                    // Persist the builders' transient materials so editor previews survive a restart.
                    foreach (var renderer in model.GetComponentsInChildren<Renderer>(true))
                    {
                        var materials = renderer.sharedMaterials;
                        for (int i = 0; i < materials.Length; i++)
                        {
                            var source = materials[i];
                            string path = Folder + "/Materials/" + source.name + ".mat";
                            var saved = AssetDatabase.LoadAssetAtPath<Material>(path);
                            if (saved == null)
                            {
                                saved = new Material(source) { name = source.name };
                                AssetDatabase.CreateAsset(saved, path);
                            }
                            materials[i] = saved;
                        }
                        renderer.sharedMaterials = materials;
                    }
                    AccessoryCatalog.MakePickable(model);
                    var prefab = PrefabUtility.SaveAsPrefabAsset(model, Folder + "/Prefabs/" + model.name + ".prefab");
                    BuildPickup(entry, prefab);
                }
                finally { UnityEngine.Object.DestroyImmediate(model); }
            }
            AssetDatabase.SaveAssets();
            Verify();
            Debug.Log("[Accessories] Built scarf, lights, button and pebble preview/pickup prefabs");
        }

        static void BuildPickup(AccessoryCatalog.Entry entry, GameObject prefab)
        {
            var root = new GameObject(entry.DisplayName + "_Pickup");
            try
            {
                var model = (GameObject)PrefabUtility.InstantiatePrefab(prefab);
                model.transform.SetParent(root.transform, false);
                model.transform.localRotation = entry.GroundRest;
                model.transform.localPosition = Vector3.up * entry.GroundLift;
                AccessoryCatalog.SetColliders(model, false);
                var pickup = root.AddComponent<ItemPickup>();
                pickup.itemId = entry.Id;
                pickup.quantity = 1;
                pickup.worldModel = model;
                var bounds = BoundsFor(model);
                var collider = root.AddComponent<BoxCollider>();
                collider.center = bounds.center;
                collider.size = Vector3.Max(bounds.size, new Vector3(.22f, .18f, .22f));
                collider.isTrigger = true;
                PrefabUtility.SaveAsPrefabAsset(root, Folder + "/Pickups/" + root.name + ".prefab");
            }
            finally { UnityEngine.Object.DestroyImmediate(root); }
        }

        static Bounds BoundsFor(GameObject model)
        {
            var renderers = model.GetComponentsInChildren<Renderer>();
            var bounds = renderers[0].bounds;
            foreach (var renderer in renderers) bounds.Encapsulate(renderer.bounds);
            return bounds;
        }

        static void EnsureFolder(string path)
        {
            if (AssetDatabase.IsValidFolder(path)) return;
            string parent = Path.GetDirectoryName(path).Replace('\\', '/');
            EnsureFolder(parent);
            AssetDatabase.CreateFolder(parent, Path.GetFileName(path));
        }

        [MenuItem("Snowfield/Items/Verify Accessory Pickup Prefabs")]
        public static void Verify()
        {
            foreach (string id in Ids)
            {
                var entry = AccessoryCatalog.Find(id);
                var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(Folder + "/Pickups/" + entry.DisplayName + "_Pickup.prefab");
                var pickup = prefab != null ? prefab.GetComponent<ItemPickup>() : null;
                if (pickup == null || pickup.itemId != id || pickup.quantity != 1 || pickup.worldModel == null)
                    throw new InvalidOperationException("Incomplete pickup wiring: " + id);
                var target = prefab.GetComponent<BoxCollider>();
                if (target == null || !target.enabled || !target.isTrigger || target.size.x < .22f || target.size.y < .18f)
                    throw new InvalidOperationException("Missing pickup targeting trigger: " + id);
                var renderers = pickup.worldModel.GetComponentsInChildren<MeshRenderer>();
                if (renderers.Length == 0) throw new InvalidOperationException("Invisible pickup: " + id);
                foreach (var renderer in renderers)
                {
                    if (!AssetDatabase.Contains(renderer.GetComponent<MeshFilter>().sharedMesh))
                        throw new InvalidOperationException("Pickup mesh must persist: " + id);
                    foreach (var material in renderer.sharedMaterials)
                        if (material == null || !AssetDatabase.Contains(material))
                            throw new InvalidOperationException("Pickup material must persist: " + id);
                }
                foreach (var collider in pickup.worldModel.GetComponentsInChildren<Collider>())
                    if (collider.enabled) throw new InvalidOperationException("Pickup visuals must not block movement: " + id);
                if ((id == "lights" || id == "scarf") && entry.Drape == null)
                    throw new InvalidOperationException("Draped pickups must retain their strand placement: " + id);
            }
            Debug.Log("[Accessories] Verified all four visible pickup prefabs");
        }

        [MenuItem("Snowfield/Items/Preview Accessory Pickup Prefabs")]
        public static void Preview()
        {
            var scene = EditorSceneManager.NewPreviewScene();
            RenderTexture rt = null;
            Texture2D image = null;
            var previous = RenderTexture.active;
            try
            {
                var root = new GameObject("PickupPreview");
                UnityEngine.SceneManagement.SceneManager.MoveGameObjectToScene(root, scene);
                for (int i = 0; i < Ids.Length; i++)
                {
                    var entry = AccessoryCatalog.Find(Ids[i]);
                    var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(Folder + "/Pickups/" + entry.DisplayName + "_Pickup.prefab");
                    var go = UnityEngine.Object.Instantiate(prefab, root.transform);
                    go.transform.localRotation = Quaternion.Euler(-50f, -15f, -8f);
                    var bounds = BoundsFor(go);
                    float scale = .6f / Mathf.Max(bounds.size.x, bounds.size.y, bounds.size.z);
                    go.transform.localScale = Vector3.one * scale;
                    bounds = BoundsFor(go);
                    var center = new Vector3(i % 2 == 0 ? -.55f : .55f, i < 2 ? .5f : -.5f, 0f);
                    go.transform.localPosition += center - bounds.center;
                    var labelGo = new GameObject("Label");
                    labelGo.transform.SetParent(root.transform, false);
                    labelGo.transform.localPosition = center + new Vector3(0f, -.39f, -.5f);
                    var label = labelGo.AddComponent<TextMesh>();
                    label.font = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");
                    labelGo.GetComponent<MeshRenderer>().sharedMaterial = label.font.material;
                    label.text = entry.DisplayName;
                    label.fontSize = 64;
                    label.characterSize = .018f;
                    label.anchor = TextAnchor.MiddleCenter;
                    label.color = new Color(.92f, .94f, .98f);
                }
                var cameraGo = new GameObject("Camera");
                cameraGo.transform.SetParent(root.transform, false);
                cameraGo.transform.position = new Vector3(0f, 0f, -5f);
                var camera = cameraGo.AddComponent<Camera>();
                camera.scene = scene;
                camera.cameraType = CameraType.Preview;
                camera.enabled = false;
                camera.clearFlags = CameraClearFlags.SolidColor;
                camera.backgroundColor = new Color(.055f, .075f, .105f);
                camera.orthographic = true;
                camera.orthographicSize = 1.1f;
                camera.nearClipPlane = .01f;
                camera.farClipPlane = 20f;
                var lightGo = new GameObject("Light");
                lightGo.transform.SetParent(root.transform, false);
                lightGo.transform.localRotation = Quaternion.Euler(25f, -25f, 0f);
                var light = lightGo.AddComponent<Light>();
                light.type = LightType.Directional;
                light.intensity = 1.5f;
                rt = new RenderTexture(1000, 1000, 24, RenderTextureFormat.ARGB32);
                camera.targetTexture = rt;
                camera.Render();
                RenderTexture.active = rt;
                image = new Texture2D(1000, 1000, TextureFormat.RGB24, false);
                image.ReadPixels(new Rect(0, 0, 1000, 1000), 0, 0);
                image.Apply();
                Directory.CreateDirectory("Screenshots/Items");
                File.WriteAllBytes("Screenshots/Items/accessory-pickups.png", image.EncodeToPNG());
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
