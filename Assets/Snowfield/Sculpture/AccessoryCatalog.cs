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
            /// <summary>
            /// Set for accessories made of a chain of points (lights, scarf) rather than one rigid piece.
            /// Null = an ordinary prop. <see cref="Build"/> then only supplies the HUD thumbnail / hover sample.
            /// </summary>
            public DrapeStyle Drape;
            public System.Func<GameObject> Build;
        }


        // ---------- draped styles (declared before Entries: static initializers run in order) ----------

        public static readonly DrapeStyle LightsStyle = new DrapeStyle
        {
            Shape = DrapeStyle.Form.Cord,
            Radius = 0.008f,
            Slack = 1.18f,
            SurfaceCling = 0.15f,    // a cord mostly hangs free, but settles onto a surface right beside it
            StrandColor = new Color(0.11f, 0.12f, 0.14f),
            StrandSmoothness = 0.25f,
            TubeSides = 5,
            BulbSpacing = 0.22f,
            BulbRadius = 0.028f,
            BulbColors = new[]
            {
                new Color(1f, 0.22f, 0.2f),    // red
                new Color(1f, 0.78f, 0.25f),   // amber
                new Color(0.3f, 0.85f, 0.4f),  // green
                new Color(0.35f, 0.6f, 1f),    // blue
            },
        };

        public static readonly DrapeStyle ScarfStyle = new DrapeStyle
        {
            Shape = DrapeStyle.Form.Ribbon,
            Radius = 0.07f,          // half-width: a 14 cm band
            Slack = 1.06f,           // wool pulls tighter to the snow than a cord
            SurfaceCling = 0.6f,     // wool lies against the shape instead of hanging off it like a flag
            StrandColor = new Color(0.72f, 0.16f, 0.19f),
            StrandSmoothness = 0.1f,
            FringeLength = 0.12f,
            FringeCount = 5,
        };

        public static readonly IReadOnlyList<Entry> Entries = new List<Entry>
        {
            new Entry { Id = "twig",   DisplayName = "Twig",   Sink = 0.06f,  GroundRest = Quaternion.Euler(90f, 0f, 0f), GroundLift = 0.012f, Build = BuildTwig },
            new Entry { Id = "carrot", DisplayName = "Carrot", Sink = 0.12f,  GroundRest = Quaternion.Euler(90f, 0f, 0f), GroundLift = 0.06f,  Build = BuildCarrot },
            new Entry { Id = "button", DisplayName = "Button", Sink = 0.015f, GroundRest = Quaternion.identity,          GroundLift = 0f,     Build = BuildButton },
            new Entry { Id = "pebble", DisplayName = "Pebble", Sink = 0.015f, GroundRest = Quaternion.identity,          GroundLift = 0f,     Build = BuildPebble },
            new Entry { Id = "lights", DisplayName = "Lights", Sink = 0f,     GroundRest = Quaternion.identity,          GroundLift = 0f,     Drape = LightsStyle, Build = BuildLightsSample },
            new Entry { Id = "scarf",  DisplayName = "Scarf",  Sink = 0f,     GroundRest = Quaternion.identity,          GroundLift = 0f,     Drape = ScarfStyle,  Build = BuildScarfSample },
        };


        public static Entry Find(string id)
        {
            foreach (var e in Entries) if (e.Id == id) return e;
            return null;
        }

        // ---------- materials (cached) ----------

        static readonly Dictionary<string, Material> Mats = new Dictionary<string, Material>();

        /// <summary>Cached URP Lit material. <paramref name="emission"/> makes bulbs read as lit rather than painted.</summary>
        public static Material Mat(string key, Color c, float smooth = 0.2f, Color? emission = null)
        {
            if (Mats.TryGetValue(key, out var m) && m != null) return m;
            m = new Material(Shader.Find("Universal Render Pipeline/Lit")) { name = "Accessory_" + key };
            m.SetColor("_BaseColor", c);
            m.SetFloat("_Smoothness", smooth);
            m.SetFloat("_Metallic", 0f);
            if (emission.HasValue)
            {
                m.EnableKeyword("_EMISSION");
                m.SetColor("_EmissionColor", emission.Value);
            }
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
            // Cone = stacked cylinders tapering; 5 segments over 54 cm.
            const int segs = 5; float len = 0.54f, r0 = 0.084f;
            for (int i = 0; i < segs; i++)
            {
                float t0 = (float)i / segs, t1 = (float)(i + 1) / segs;
                float r = Mathf.Lerp(r0, 0.012f, (t0 + t1) * 0.5f);
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
                new Vector3(0f, 0.024f, 0f), Quaternion.identity,
                new Vector3(0.105f, 0.024f, 0.105f), coal);
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

        // ---------- samples for the HUD thumbnail / hover ghost (the real thing is generated by DrapedAccessory) ----------

        static GameObject BuildLightsSample()
        {
            var cord = Mat("drape_lights", LightsStyle.StrandColor, LightsStyle.StrandSmoothness);
            var root = Root("LightsSample");
            const int spans = 4;
            float width = 0.5f, sag = 0.12f;
            Vector3 Point(int i)
            {
                float t = i / (float)spans;
                return new Vector3((t - 0.5f) * width, Mathf.Sin(t * Mathf.PI) * -sag + sag, 0f);
            }
            for (int i = 0; i < spans; i++)
            {
                Vector3 a = Point(i), b = Point(i + 1);
                Vector3 mid = (a + b) * 0.5f;
                float len = Vector3.Distance(a, b);
                Part(PrimitiveType.Cylinder, root.transform, mid,
                    Quaternion.FromToRotation(Vector3.up, (b - a).normalized),
                    new Vector3(0.008f, len * 0.5f, 0.008f), cord);
            }
            for (int i = 0; i <= spans; i++)
            {
                var c = LightsStyle.BulbColors[i % LightsStyle.BulbColors.Length];
                Part(PrimitiveType.Sphere, root.transform, Point(i) - new Vector3(0f, 0.02f, 0f), Quaternion.identity,
                    Vector3.one * LightsStyle.BulbRadius * 2f,
                    Mat("bulb_" + ColorUtility.ToHtmlStringRGB(c), c, 0.7f, c * 0.8f));
            }
            return root;
        }

        static GameObject BuildScarfSample()
        {
            var wool = Mat("drape_scarf", ScarfStyle.StrandColor, ScarfStyle.StrandSmoothness);
            var root = Root("ScarfSample");
            const int segs = 4;
            float width = 0.5f;
            for (int i = 0; i < segs; i++)
            {
                float t = (i + 0.5f) / segs;
                Part(PrimitiveType.Cube, root.transform,
                    new Vector3((t - 0.5f) * width, Mathf.Sin(t * Mathf.PI) * -0.06f + 0.06f, 0f),
                    Quaternion.Euler(0f, 0f, Mathf.Cos(t * Mathf.PI) * 18f),
                    new Vector3(width / segs * 1.1f, 0.02f, ScarfStyle.Radius * 2f), wool);
            }
            for (int f = 0; f < 3; f++)
            {
                float x = -width * 0.5f;
                Part(PrimitiveType.Cube, root.transform,
                    new Vector3(x - 0.02f, 0.06f - ScarfStyle.FringeLength * 0.5f, (f - 1) * ScarfStyle.Radius * 0.6f),
                    Quaternion.identity, new Vector3(0.012f, ScarfStyle.FringeLength, 0.012f), wool);
            }
            return root;
        }

        /// <summary>
        /// Ready a placed accessory for the cursor: its own part colliders, live but as triggers. The camera ray
        /// (QueryTriggerInteraction.Collide) still hits them precisely, while nothing physical shoves the player.
        /// </summary>
        public static void MakePickable(GameObject go)
        {
            foreach (var c in go.GetComponentsInChildren<Collider>(true))
            {
                var mesh = c as MeshCollider;
                if (mesh != null) mesh.convex = true; // a trigger mesh collider has to be convex
                c.isTrigger = true;
                c.enabled = true;
            }
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
