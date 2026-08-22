using System.Collections.Generic;
using UnityEngine;

namespace Snowfield.Sculpture
{
    /// <summary>
    /// The accessories a player can stick into snow. Built procedurally from primitives for now so there are no
    /// prefab assets to maintain; swap <see cref="Build"/> for prefab instantiation later without touching callers.
    /// Every accessory is authored with +Y pointing *out of* the snow surface and its origin at the snow contact point.
    /// </summary>
    public static class AccessoryCatalog
    {
        public sealed class Entry
        {
            public string Id;
            public string DisplayName;
            /// <summary>How far (m) to push the origin below the surface so it reads as stuck in, not floating.</summary>
            public float Sink;
            /// <summary>Rotation that lays the item on flat ground (authored +Y = out of snow).</summary>
            public Quaternion GroundRest;
            /// <summary>Height above ground for the origin when resting, so it does not clip.</summary>
            public float GroundLift;
            public System.Func<GameObject> Build;
        }

        public static readonly IReadOnlyList<Entry> Entries = new List<Entry>
        {
            new Entry { Id = "twig",   DisplayName = "Twig",   Sink = 0.06f,  GroundRest = Quaternion.Euler(90f, 0f, 0f), GroundLift = 0.012f, Build = BuildTwig },
            new Entry { Id = "carrot", DisplayName = "Carrot", Sink = 0.05f,  GroundRest = Quaternion.Euler(90f, 0f, 0f), GroundLift = 0.02f,  Build = BuildCarrot },
            new Entry { Id = "button", DisplayName = "Button", Sink = 0.01f,  GroundRest = Quaternion.identity,          GroundLift = 0f,     Build = BuildButton },
            new Entry { Id = "pebble", DisplayName = "Pebble", Sink = 0.015f, GroundRest = Quaternion.identity,          GroundLift = 0f,     Build = BuildPebble },
        };

        public static Entry Find(string id)
        {
            foreach (var e in Entries) if (e.Id == id) return e;
            return null;
        }

        // ---------- materials (cached) ----------

        static readonly Dictionary<string, Material> Mats = new Dictionary<string, Material>();

        static Material Mat(string key, Color c, float smooth = 0.2f)
        {
            if (Mats.TryGetValue(key, out var m) && m != null) return m;
            m = new Material(Shader.Find("Universal Render Pipeline/Lit")) { name = "Accessory_" + key };
            m.SetColor("_BaseColor", c);
            m.SetFloat("_Smoothness", smooth);
            m.SetFloat("_Metallic", 0f);
            Mats[key] = m;
            return m;
        }

        // ---------- builders ----------

        static GameObject Part(PrimitiveType type, Transform parent, Vector3 localPos, Quaternion localRot, Vector3 scale, Material mat)
        {
            var go = GameObject.CreatePrimitive(type);
            go.transform.SetParent(parent, false);
            go.transform.localPosition = localPos;
            go.transform.localRotation = localRot;
            go.transform.localScale = scale;
            go.GetComponent<MeshRenderer>().sharedMaterial = mat;
            return go;
        }

        static GameObject Root(string name)
        {
            var root = new GameObject(name);
            return root;
        }

        static GameObject BuildTwig()
        {
            var bark = Mat("bark", new Color(0.32f, 0.22f, 0.13f), 0.1f);
            var root = Root("Twig");
            // main stick: 45 cm long, 2 cm thick, leaning slightly so a pair reads as arms
            float len = 0.45f;
            Part(PrimitiveType.Cylinder, root.transform,
                new Vector3(0f, len * 0.5f, 0f), Quaternion.Euler(0f, 0f, 8f),
                new Vector3(0.02f, len * 0.5f, 0.02f), bark);
            // a small fork near the tip
            Part(PrimitiveType.Cylinder, root.transform,
                new Vector3(0.03f, len * 0.78f, 0f), Quaternion.Euler(0f, 0f, -40f),
                new Vector3(0.013f, 0.07f, 0.013f), bark);
            // fingers at the tip
            Part(PrimitiveType.Cylinder, root.transform,
                new Vector3(-0.045f, len * 0.95f, 0.01f), Quaternion.Euler(15f, 0f, 35f),
                new Vector3(0.01f, 0.05f, 0.01f), bark);
            return root;
        }

        static GameObject BuildCarrot()
        {
            var orange = Mat("carrot", new Color(0.95f, 0.48f, 0.1f), 0.35f);
            var root = Root("Carrot");
            // Cone = stacked cylinders tapering; 5 segments over 18 cm.
            const int segs = 5; float len = 0.18f, r0 = 0.028f;
            for (int i = 0; i < segs; i++)
            {
                float t0 = (float)i / segs, t1 = (float)(i + 1) / segs;
                float r = Mathf.Lerp(r0, 0.004f, (t0 + t1) * 0.5f);
                float segLen = len / segs;
                Part(PrimitiveType.Cylinder, root.transform,
                    new Vector3(0f, (t0 + 0.5f / segs) * len, 0f), Quaternion.identity,
                    new Vector3(r, segLen * 0.5f, r), orange);
            }
            return root;
        }

        static GameObject BuildButton()
        {
            var coal = Mat("coal", new Color(0.08f, 0.08f, 0.09f), 0.45f);
            var root = Root("Button");
            Part(PrimitiveType.Cylinder, root.transform,
                new Vector3(0f, 0.008f, 0f), Quaternion.identity,
                new Vector3(0.035f, 0.008f, 0.035f), coal);
            return root;
        }

        static GameObject BuildPebble()
        {
            var stone = Mat("stone", new Color(0.22f, 0.21f, 0.2f), 0.25f);
            var root = Root("Pebble");
            Part(PrimitiveType.Sphere, root.transform,
                new Vector3(0f, 0.012f, 0f), Quaternion.identity,
                new Vector3(0.03f, 0.024f, 0.028f), stone);
            return root;
        }

        /// <summary>Strip colliders (previews) or keep them (placed props) after building.</summary>
        public static void SetColliders(GameObject go, bool enabled)
        {
            foreach (var c in go.GetComponentsInChildren<Collider>(true)) c.enabled = enabled;
        }

        public static void SetLayerRecursive(GameObject go, int layer)
        {
            foreach (var t in go.GetComponentsInChildren<Transform>(true)) t.gameObject.layer = layer;
        }
    }
}
