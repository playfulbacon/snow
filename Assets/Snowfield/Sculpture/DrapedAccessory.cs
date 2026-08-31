using System.Collections.Generic;
using Snowfield.Config;
using UnityEngine;

namespace Snowfield.Sculpture
{
    /// <summary>
    /// An accessory made of a chain of points rather than one rigid piece: a string of lights, a scarf.
    /// A <see cref="SculptureProp"/> subclass, so prop registration, removal, saving and the network's remove path
    /// all keep working untouched.
    ///
    /// Only the points are state. The visible strand is re-derived by <see cref="DrapeRope"/> every time — live
    /// while you are placing, once more when the chain is finished — so nothing about the simulation is ever stored
    /// or sent. Points live in the owner sculpture's local space; the prop itself sits on the first point so that
    /// two chains of the same style on one sculpture stay tellable apart (the network keys removal on that).
    /// </summary>
    public class DrapedAccessory : SculptureProp
    {
        readonly List<Vector3> _points = new List<Vector3>();   // sculpture-local
        readonly List<DrapeAnchor> _markers = new List<DrapeAnchor>();
        readonly List<Transform> _bulbs = new List<Transform>();
        readonly List<SnowSculpture> _surfaces = new List<SnowSculpture>();
        readonly List<Vector3> _bulbSpots = new List<Vector3>();
        readonly DrapeRope _rope = new DrapeRope();

        DrapeStyle _style;
        SculptFeelConfig _config;
        Mesh _mesh;
        MeshFilter _filter;
        Transform _strand;
        Vector3? _tipWorld;      // the live segment running to the cursor
        bool _hasTip;

        /// <summary>Points in the owner sculpture's local space. This is the whole persisted/synced state.</summary>
        public IReadOnlyList<Vector3> Points => _points;
        public DrapeStyle Style => _style;
        public bool Frozen { get; private set; }
        public int PointCount => _points.Count;

        /// <summary>
        /// The one construction path — used by placement, save/load and network replay alike.
        /// <paramref name="pointsLocal"/> are in <paramref name="owner"/>'s local space.
        /// </summary>
        public static DrapedAccessory Create(SnowSculpture owner, AccessoryCatalog.Entry entry,
                                             IReadOnlyList<Vector3> pointsLocal, bool frozen)
        {
            if (owner == null || entry == null || entry.Drape == null || pointsLocal == null || pointsLocal.Count == 0)
                return null;

            var go = new GameObject("Drape_" + entry.Id);
            var chain = go.AddComponent<DrapedAccessory>();
            chain._style = entry.Drape;
            chain._config = owner.Config;
            chain._points.AddRange(pointsLocal);
            chain.Attach(owner, entry.Id, owner.transform.TransformPoint(pointsLocal[0]), owner.transform.rotation);
            chain.BuildVisuals();
            chain.RefreshSurfaces();
            if (frozen) chain.Freeze();
            else chain.Rebuild();
            return chain;
        }

        // ---------- editing ----------

        /// <summary>Add a point (world space) to the end of the chain.</summary>
        public void AddPointWorld(Vector3 world)
        {
            if (Frozen || Sculpture == null) return;
            _points.Add(Sculpture.transform.InverseTransformPoint(world));
            AddMarker(_points.Count - 1);
            RefreshSurfaces();
            Rebuild();
        }

        /// <summary>Close the chain back onto one of its own points (a scarf loop).</summary>
        public void AddExistingPoint(int index)
        {
            if (Frozen || index < 0 || index >= _points.Count) return;
            _points.Add(_points[index]);
            AddMarker(_points.Count - 1);
            Rebuild();
        }

        /// <summary>Where the strand currently runs to while you aim; null while there is no live end.</summary>
        public void SetTip(Vector3? world)
        {
            if (Frozen) return;
            _tipWorld = world;
            _hasTip = world.HasValue;
        }

        /// <summary>Re-solve the strand for this frame. Deterministic from the points, so it cannot drift or shake.</summary>
        public void StepLive()
        {
            if (Frozen) return;
            Rebuild();
        }

        /// <summary>Settle the strand and stop simulating; the chain becomes an ordinary placed accessory.</summary>
        public void Freeze()
        {
            _hasTip = false;
            _tipWorld = null;
            RefreshSurfaces();
            Rebuild();          // same solve as the preview, so nothing shifts when you finish
            ClearMarkers();
            Frozen = true;
        }

        // ---------- internals ----------

        void BuildVisuals()
        {
            var strand = new GameObject("Strand");
            strand.transform.SetParent(transform, false);
            strand.layer = gameObject.layer;
            _strand = strand.transform;
            _filter = strand.AddComponent<MeshFilter>();
            var mr = strand.AddComponent<MeshRenderer>();
            mr.sharedMaterial = AccessoryCatalog.Mat("drape_" + prefabId, _style.StrandColor, _style.StrandSmoothness);
            _mesh = new Mesh { name = "Drape" };
            _mesh.MarkDynamic();
            _filter.sharedMesh = _mesh;

            for (int i = 0; i < _points.Count; i++) AddMarker(i);
        }

        /// <summary>Marker size for a style: a marker, not a bead — a scarf's half-width would be enormous.</summary>
        public static float MarkerRadius(DrapeStyle style) => Mathf.Clamp(style.Radius * 1.5f, 0.02f, 0.05f);
        public static Material MarkerMaterial() => AccessoryCatalog.Mat("drape_anchor", new Color(1f, 0.85f, 0.3f, 1f), 0.4f);

        /// <summary>A lone point, for the hover preview before a chain exists.</summary>
        public static GameObject BuildPointMarker(DrapeStyle style)
        {
            var go = GameObject.CreatePrimitive(PrimitiveType.Sphere);
            go.name = "DrapePointPreview";
            go.transform.localScale = Vector3.one * MarkerRadius(style) * 2f;
            go.GetComponent<MeshRenderer>().sharedMaterial = MarkerMaterial();
            return go;
        }

        void AddMarker(int index)
        {
            if (Sculpture == null) return;
            _markers.Add(DrapeAnchor.Create(this, index, Sculpture.transform.TransformPoint(_points[index]),
                                            MarkerRadius(_style), MarkerMaterial()));
        }

        void ClearMarkers()
        {
            foreach (var m in _markers) if (m != null) Destroy(m.gameObject);
            _markers.Clear();
        }

        readonly List<Vector3> _anchorScratch = new List<Vector3>();

        void Rebuild()
        {
            _anchorScratch.Clear();
            if (Sculpture != null)
                foreach (var p in _points) _anchorScratch.Add(Sculpture.transform.TransformPoint(p));
            if (_hasTip && _tipWorld.HasValue) _anchorScratch.Add(_tipWorld.Value);

            _rope.Solve(_anchorScratch, _style, _config, _surfaces);
            DrapeMesh.Build(_mesh, _rope.Points, _strand, _style, _surfaces);
            RebuildBulbs();
        }

        void RebuildBulbs()
        {
            if (_style.BulbSpacing <= 0.001f || _style.BulbColors == null || _style.BulbColors.Length == 0) return;
            DrapeMesh.SampleAlong(_rope.Points, _style.BulbSpacing, _bulbSpots);

            while (_bulbs.Count < _bulbSpots.Count)
            {
                var go = GameObject.CreatePrimitive(PrimitiveType.Sphere);
                go.name = "Bulb" + _bulbs.Count;
                var col = go.GetComponent<Collider>();
                if (col != null) Destroy(col);
                go.transform.SetParent(_strand, false);
                go.GetComponent<MeshRenderer>().shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
                _bulbs.Add(go.transform);
            }
            for (int i = 0; i < _bulbs.Count; i++)
            {
                bool used = i < _bulbSpots.Count;
                _bulbs[i].gameObject.SetActive(used);
                if (!used) continue;
                _bulbs[i].position = _bulbSpots[i];
                _bulbs[i].localScale = Vector3.one * _style.BulbRadius * 2f;
                var c = _style.BulbColors[i % _style.BulbColors.Length];
                _bulbs[i].GetComponent<MeshRenderer>().sharedMaterial =
                    AccessoryCatalog.Mat("bulb_" + ColorUtility.ToHtmlStringRGB(c), c, 0.7f, c * 0.8f);
            }
        }

        /// <summary>Which sculptures the strand can rest on: the owner plus anything its points reach into.</summary>
        void RefreshSurfaces()
        {
            _surfaces.Clear();
            if (Sculpture != null) _surfaces.Add(Sculpture);
            if (_points.Count == 0) return;

            var all = FindObjectsByType<SnowSculpture>(FindObjectsSortMode.None);
            foreach (var s in all)
            {
                if (s == null || s == Sculpture || _surfaces.Contains(s)) continue;
                var b = s.WorldBounds;
                foreach (var p in _points)
                {
                    if (!b.Contains(Sculpture.transform.TransformPoint(p))) continue;
                    _surfaces.Add(s);
                    break;
                }
            }
        }

        void OnDestroy()
        {
            if (_mesh != null) Destroy(_mesh);
        }
    }
}
