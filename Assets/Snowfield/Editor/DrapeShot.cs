using System.Collections.Generic;
using System.IO;
using Snowfield.Config;
using Snowfield.Sculpture;
using UnityEditor;
using UnityEngine;

namespace Snowfield.Editor
{
    /// <summary>
    /// Look at a draped strand without entering play mode: builds a throwaway snowman off in the middle of nowhere,
    /// wraps a scarf round its neck and a light cord over its body, and renders it from three angles to
    /// <c>Temp/drape_*.png</c>.
    ///
    /// The drape is the one accessory whose shape is computed rather than placed, so "do the tests pass" and "does it
    /// look right" are different questions and this answers the second one. Menu: Snowfield ▸ Drape Shot; headless:
    /// NetOps <c>invoke</c> with <c>Snowfield.Editor.DrapeShot.Run</c>.
    ///
    /// Everything it creates is <see cref="HideFlags.HideAndDontSave"/> and destroyed again, so it leaves the scene
    /// you have open alone — it never opens or saves one.
    /// </summary>
    public static class DrapeShot
    {
        const string OutDir = "Temp";
        static readonly Vector3 Somewhere = new Vector3(1000f, 0f, 1000f);

        [MenuItem("Snowfield/Drape Shot")]
        public static void Run()
        {
            var cfg = AssetDatabase.LoadAssetAtPath<SculptFeelConfig>("Assets/Settings/SculptFeelConfig.asset");
            var mat = AssetDatabase.LoadAssetAtPath<Material>("Assets/Settings/Snow.mat");
            if (cfg == null || mat == null)
            {
                Debug.LogError("DrapeShot: need Assets/Settings/SculptFeelConfig.asset and Assets/Settings/Snow.mat");
                return;
            }
            Directory.CreateDirectory(Path.GetFullPath(OutDir));

            var temp = new List<GameObject>();
            try
            {
                var sculpture = BuildSnowman(cfg, mat, temp, out Vector3 centre, out float neckY, out float bodyY);
                int scarf = Drape(sculpture, "scarf", Ring(sculpture, centre, neckY, 0.6f, 5), temp);
                int lights = Drape(sculpture, "lights", Ring(sculpture, centre, bodyY, 0.95f, 4), temp);
                Debug.Log($"DrapeShot: scarf {scarf} points, lights {lights} points");

                var light = new GameObject("DrapeShotLight") { hideFlags = HideFlags.HideAndDontSave };
                light.transform.position = centre + Vector3.up * 3f;
                light.transform.rotation = Quaternion.Euler(45f, -35f, 0f);
                var dir = light.AddComponent<Light>();
                dir.type = LightType.Directional;
                dir.intensity = 1.5f;
                dir.color = new Color(1f, 0.97f, 0.9f);
                temp.Add(light);

                Vector3 look = centre + Vector3.up * (neckY - centre.y + 0.05f);
                Shoot("drape_front", look, new Vector3(0f, 0.25f, -1f), 2.3f, temp);
                Shoot("drape_quarter", look, new Vector3(-0.8f, 0.35f, -0.8f), 2.3f, temp);
                Shoot("drape_above", look, new Vector3(0f, 1.1f, -0.5f), 2.1f, temp);
            }
            finally
            {
                for (int i = temp.Count - 1; i >= 0; i--) if (temp[i] != null) Object.DestroyImmediate(temp[i]);
            }
        }

        /// <summary>A body and a head, stamped into one grid — the shape a scarf is supposed to wrap.</summary>
        static SnowSculpture BuildSnowman(SculptFeelConfig cfg, Material mat, List<GameObject> temp,
                                          out Vector3 centre, out float neckY, out float bodyY)
        {
            var go = new GameObject("DrapeShotSnowman") { hideFlags = HideFlags.HideAndDontSave };
            go.transform.position = Somewhere;
            go.SetActive(false);                      // configure before Awake, or it disables itself
            var s = go.AddComponent<SnowSculpture>();
            s.EditorAssign(cfg, mat);
            go.SetActive(true);
            temp.Add(go);
            if (s.Grid == null) s.Initialise(cfg.gridSize, cfg.voxelSize);

            float e = s.Info.WorldExtent;
            centre = Somewhere + new Vector3(e * 0.5f, 0f, e * 0.5f);
            bodyY = 0.5f;
            float headY = 1.1f;
            neckY = 0.92f;
            s.StampSphere(centre + Vector3.up * bodyY, 0.45f, 0.7f);
            s.StampSphere(centre + Vector3.up * headY, 0.28f, 0.7f);
            s.Remesh();
            return s;
        }

        /// <summary>
        /// Clicks spread round the snowman at one height, each one put on the surface the way the player's raycast
        /// would put it. <paramref name="turns"/> of 1 would close the ring; 0.6 leaves the ends loose.
        /// </summary>
        static List<Vector3> Ring(SnowSculpture s, Vector3 centre, float y, float turns, int count)
        {
            var points = new List<Vector3>();
            Vector3 axisPoint = centre + Vector3.up * y;
            for (int i = 0; i < count; i++)
            {
                float a = (i / (float)(count - 1) * turns + 0.1f) * Mathf.PI * 2f;
                Vector3 outward = new Vector3(Mathf.Cos(a), 0f, Mathf.Sin(a));
                if (SurfaceProbe.Snap(s, axisPoint + outward * 1.2f, outward, 1.4f, out var hit)) points.Add(hit);
            }
            return points;
        }

        static int Drape(SnowSculpture s, string id, List<Vector3> worldPoints, List<GameObject> temp)
        {
            var entry = AccessoryCatalog.Find(id);
            if (entry == null || worldPoints.Count < 2) return 0;
            var local = new List<Vector3>();
            foreach (var w in worldPoints) local.Add(s.transform.InverseTransformPoint(w));
            var chain = DrapedAccessory.Create(s, entry, local, frozen: true);
            if (chain == null) return 0;
            foreach (var t in chain.GetComponentsInChildren<Transform>(true)) t.gameObject.hideFlags = HideFlags.HideAndDontSave;
            temp.Add(chain.gameObject);
            return chain.PointCount;
        }

        static void Shoot(string name, Vector3 look, Vector3 from, float distance, List<GameObject> temp)
        {
            var go = new GameObject("DrapeShotCamera") { hideFlags = HideFlags.HideAndDontSave };
            temp.Add(go);
            go.transform.position = look + from.normalized * distance;
            go.transform.LookAt(look);
            var cam = go.AddComponent<Camera>();
            cam.clearFlags = CameraClearFlags.SolidColor;
            cam.backgroundColor = new Color(0.42f, 0.5f, 0.62f);
            cam.fieldOfView = 45f;
            cam.nearClipPlane = 0.05f;
            cam.farClipPlane = 20f;

            var rt = new RenderTexture(1280, 720, 24, RenderTextureFormat.ARGB32);
            cam.targetTexture = rt;
            cam.Render();
            var tex = new Texture2D(rt.width, rt.height, TextureFormat.RGB24, false);
            var prev = RenderTexture.active;
            RenderTexture.active = rt;
            tex.ReadPixels(new Rect(0, 0, rt.width, rt.height), 0, 0);
            tex.Apply();
            RenderTexture.active = prev;
            cam.targetTexture = null;

            string path = Path.Combine(OutDir, name + ".png");
            File.WriteAllBytes(path, tex.EncodeToPNG());
            Object.DestroyImmediate(tex);
            Object.DestroyImmediate(rt);
            Debug.Log("DrapeShot: wrote " + Path.GetFullPath(path));
        }
    }
}
