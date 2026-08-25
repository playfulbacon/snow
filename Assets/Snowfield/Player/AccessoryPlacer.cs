using System.Collections.Generic;
using Snowfield.Sculpture;
using UnityEngine;

namespace Snowfield.Player
{
    /// <summary>
    /// Accessory mode: keeps the selected accessory, shows a ghost preview on the aimed snow surface, places on
    /// LMB, removes an aimed prop on RMB. Driven by <see cref="SculptTool"/>; owns no input itself.
    /// </summary>
    public class AccessoryPlacer : MonoBehaviour
    {
        public int SelectedIndex { get; private set; }
        public AccessoryCatalog.Entry Selected => AccessoryCatalog.Entries[SelectedIndex];

        GameObject _preview;
        int _previewIndex = -1;

        /// <summary>Thumbnails rendered once at startup; index-aligned with the catalog.</summary>
        public IReadOnlyList<Texture2D> Thumbnails => _thumbs;
        readonly List<Texture2D> _thumbs = new List<Texture2D>();

        void Start() => RenderThumbnails();

        public void Select(int index)
        {
            int n = AccessoryCatalog.Entries.Count;
            SelectedIndex = ((index % n) + n) % n;
        }

        public void Step(int delta) => Select(SelectedIndex + delta);

        /// <summary>Show/hide and position the ghost. Call every frame while in accessory mode.</summary>
        public void UpdatePreview(bool visible, Vector3 point, Vector3 normal)
        {
            if (!visible)
            {
                if (_preview != null) _preview.SetActive(false);
                return;
            }
            if (_preview == null || _previewIndex != SelectedIndex)
            {
                if (_preview != null) Destroy(_preview);
                _preview = Selected.Build();
                _preview.name = "AccessoryPreview";
                AccessoryCatalog.SetColliders(_preview, false);
                AccessoryCatalog.SetLayerRecursive(_preview, LayerMask.NameToLayer("Ignore Raycast"));
                foreach (var r in _preview.GetComponentsInChildren<Renderer>())
                    r.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
                _previewIndex = SelectedIndex;
            }
            _preview.SetActive(true);
            Pose(point, normal, out var pos, out var rot);
            _preview.transform.SetPositionAndRotation(pos, rot);
        }

        public void HidePreview() => UpdatePreview(false, default, default);

        void Pose(Vector3 point, Vector3 normal, out Vector3 pos, out Quaternion rot)
        {
            rot = Quaternion.FromToRotation(Vector3.up, normal);
            pos = point - normal * Selected.Sink;
        }

        /// <summary>Place the selected accessory (unlimited supply).</summary>
        public SculptureProp Place(SnowSculpture sculpture, Vector3 point, Vector3 normal)
        {
            Pose(point, normal, out var pos, out var rot);
            var go = Selected.Build();
            AccessoryCatalog.SetColliders(go, true);
            AccessoryCatalog.AddPickCollider(go, Selected);
            var prop = go.AddComponent<SculptureProp>();
            prop.Attach(sculpture, Selected.Id, pos, rot);
            return prop;
        }

        /// <summary>Take a placed accessory off its sculpture.</summary>
        public void Retrieve(SculptureProp prop)
        {
            if (prop == null) return;
            prop.Remove();
        }

        // ---------- thumbnails ----------

        void RenderThumbnails()
        {
            const int size = 96;
            var camGo = new GameObject("ThumbnailCamera");
            var cam = camGo.AddComponent<Camera>();
            cam.clearFlags = CameraClearFlags.SolidColor;
            cam.backgroundColor = new Color(0f, 0f, 0f, 0f);
            cam.orthographic = true;
            cam.nearClipPlane = 0.01f;
            cam.farClipPlane = 5f;
            cam.enabled = false;
            int layer = LayerMask.NameToLayer("Ignore Raycast");
            cam.cullingMask = 1 << layer;

            var lightGo = new GameObject("ThumbnailLight");
            var light = lightGo.AddComponent<Light>();
            light.type = LightType.Directional;
            light.intensity = 1.2f;
            light.cullingMask = 1 << layer;
            lightGo.transform.rotation = Quaternion.Euler(40f, -30f, 0f);

            var rt = new RenderTexture(size, size, 16, RenderTextureFormat.ARGB32);
            Vector3 far = new Vector3(0f, -500f, 0f); // well away from the field

            foreach (var e in AccessoryCatalog.Entries)
            {
                var go = e.Build();
                AccessoryCatalog.SetColliders(go, false);
                AccessoryCatalog.SetLayerRecursive(go, layer);
                go.transform.position = far;

                // Frame the renderer bounds.
                var b = new Bounds(far, Vector3.zero);
                bool first = true;
                foreach (var r in go.GetComponentsInChildren<Renderer>())
                {
                    if (first) { b = r.bounds; first = false; } else b.Encapsulate(r.bounds);
                }
                float extent = Mathf.Max(b.extents.x, b.extents.y, b.extents.z) * 1.25f + 0.005f;
                cam.orthographicSize = extent;
                camGo.transform.position = b.center + new Vector3(0.6f, 0.5f, -1f).normalized * 2f;
                camGo.transform.LookAt(b.center);

                cam.targetTexture = rt;
                cam.Render();
                var tex = new Texture2D(size, size, TextureFormat.RGBA32, false) { name = "Thumb_" + e.Id };
                var prev = RenderTexture.active;
                RenderTexture.active = rt;
                tex.ReadPixels(new Rect(0, 0, size, size), 0, 0);
                tex.Apply();
                RenderTexture.active = prev;
                _thumbs.Add(tex);
                Destroy(go);
            }

            cam.targetTexture = null;
            Destroy(rt);
            Destroy(camGo);
            Destroy(lightGo);
        }
    }
}
