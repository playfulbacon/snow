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
    /// Only the points are state. The strand itself is a shape derived from them by <see cref="DrapeCurve"/> — no
    /// simulation, so it never shimmers while you drag the cursor around. Movement is a scripted jiggle on top: a
    /// damped swing played when the chain is placed and whenever something brushes past. The jiggle only offsets
    /// what is drawn, so nothing that gets saved or sent ever moves.
    ///
    /// Points live in the owner sculpture's local space; the prop itself sits on the first point so that two chains
    /// of the same style on one sculpture stay tellable apart (the network keys removal on that).
    /// </summary>
    public class DrapedAccessory : SculptureProp
    {
        /// <summary>Every live chain, so a passer-by can find the ones near it without a scene search.</summary>
        static readonly List<DrapedAccessory> Live = new List<DrapedAccessory>();

        readonly List<Vector3> _points = new List<Vector3>();      // sculpture-local: the whole persisted state
        readonly List<Vector3> _path = new List<Vector3>();        // the resting strand, world space
        readonly List<Vector3> _render = new List<Vector3>();      // _path plus the current jiggle
        readonly List<Vector3> _swingDirs = new List<Vector3>();
        readonly List<DrapeAnchor> _markers = new List<DrapeAnchor>();
        readonly List<Transform> _bulbs = new List<Transform>();
        readonly List<SnowSculpture> _surfaces = new List<SnowSculpture>();
        readonly List<Vector3> _bulbSpots = new List<Vector3>();
        readonly List<Vector3> _anchorScratch = new List<Vector3>();

        DrapeStyle _style;
        SculptFeelConfig _config;
        Mesh _mesh;
        Transform _strand;
        Vector3? _tipWorld;
        bool _hasTip;
        float _swing;          // 1 = just knocked, 0 = at rest
        float _swingPhase;
        Bounds _reach;         // world bounds of the strand, for cheap bump tests

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

        void OnEnable() { if (!Live.Contains(this)) Live.Add(this); }
        void OnDisable() => Live.Remove(this);

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

        /// <summary>Re-lay the strand for this frame. Deterministic from the points, so it cannot drift or shake.</summary>
        public void StepLive()
        {
            if (Frozen) return;
            Rebuild();
        }

        /// <summary>Finish: the chain becomes an ordinary placed accessory, with a little swing as it settles.</summary>
        public void Freeze()
        {
            _hasTip = false;
            _tipWorld = null;
            RefreshSurfaces();
            Rebuild();              // same shape as the preview, so nothing shifts when you finish
            ClearMarkers();
            Frozen = true;
            Jiggle(1f);
        }

        // ---------- jiggle ----------

        /// <summary>Set the strand swinging. <paramref name="strength"/> 0-1; a stronger knock overrides a fading one.</summary>
        public void Jiggle(float strength)
        {
            strength = Mathf.Clamp01(strength);
            if (strength <= _swing) return;   // already swinging harder than this
            _swing = strength;
            _swingPhase = Mathf.Repeat(transform.position.x * 12.9898f + transform.position.z * 78.233f, Mathf.PI * 2f);
        }

        /// <summary>Knock any strand within <paramref name="radius"/> of a world position — a passer-by, a thrown ball.</summary>
        public static void BumpAt(Vector3 world, float radius, float strength)
        {
            for (int i = 0; i < Live.Count; i++)
            {
                var chain = Live[i];
                if (chain == null || !chain.Frozen) continue;
                var reach = chain._reach;
                reach.Expand(radius * 2f);
                if (reach.Contains(world)) chain.Jiggle(strength);
            }
        }

        void Update()
        {
            if (_swing <= 0f || _path.Count < 3) return;   // idle strands cost nothing
            float decay = _config != null ? Mathf.Max(0.05f, _config.drapeJiggleDecay) : 1.1f;
            _swing = Mathf.Max(0f, _swing - Time.deltaTime / decay);
            ApplySwing();
            UploadMesh();
        }

        /// <summary>Offset the drawn strand by a damped standing wave; ends stay put, the middle moves most.</summary>
        void ApplySwing()
        {
            float amp = (_config != null ? _config.drapeJiggleAmplitude : 0.035f) * _swing * _swing;
            float freq = _config != null ? _config.drapeJiggleFrequency : 4.5f;
            float t = Time.time * freq * Mathf.PI * 2f + _swingPhase;
            int last = _path.Count - 1;
            if (last <= 0 || _render.Count != _path.Count || _swingDirs.Count != _path.Count) return;
            for (int i = 0; i <= last; i++)
            {
                float edge = Mathf.Sin(Mathf.PI * (i / (float)last));   // pinned at both anchors
                _render[i] = _path[i] + _swingDirs[i] * (Mathf.Sin(t + i * 0.6f) * amp * edge);
            }
        }

        // ---------- internals ----------

        void BuildVisuals()
        {
            var strand = new GameObject("Strand");
            strand.transform.SetParent(transform, false);
            strand.layer = gameObject.layer;
            _strand = strand.transform;
            _mesh = new Mesh { name = "Drape" };
            _mesh.MarkDynamic();
            strand.AddComponent<MeshFilter>().sharedMesh = _mesh;
            strand.AddComponent<MeshRenderer>().sharedMaterial =
                AccessoryCatalog.Mat("drape_" + prefabId, _style.StrandColor, _style.StrandSmoothness);

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

        void Rebuild()
        {
            _anchorScratch.Clear();
            if (Sculpture != null)
                foreach (var p in _points) _anchorScratch.Add(Sculpture.transform.TransformPoint(p));
            if (_hasTip && _tipWorld.HasValue) _anchorScratch.Add(_tipWorld.Value);

            DrapeCurve.Build(_anchorScratch, _style, _config, _surfaces, _path);
            RefreshSwingBasis();
            _render.Clear();
            _render.AddRange(_path);
            if (_swing > 0f) ApplySwing();
            UploadMesh();
        }

        void UploadMesh()
        {
            DrapeMesh.Build(_mesh, _render, _strand, _style, _surfaces);
            RebuildBulbs();
        }

        /// <summary>A fixed sway direction per point — sideways to the strand, leaning differently along it.</summary>
        void RefreshSwingBasis()
        {
            _swingDirs.Clear();
            _reach = new Bounds(_path.Count > 0 ? _path[0] : transform.position, Vector3.zero);
            for (int i = 0; i < _path.Count; i++)
            {
                _reach.Encapsulate(_path[i]);
                Vector3 tangent = i == 0 ? _path[Mathf.Min(1, _path.Count - 1)] - _path[0]
                                : i == _path.Count - 1 ? _path[i] - _path[i - 1]
                                : _path[i + 1] - _path[i - 1];
                if (tangent.sqrMagnitude < 1e-8f) tangent = Vector3.forward;
                Vector3 side = Vector3.Cross(tangent.normalized, Vector3.up);
                if (side.sqrMagnitude < 1e-6f) side = Vector3.right;
                // lean each point's sway a little differently so the swing reads as loose, not rigid
                float lean = Mathf.Sin(i * 1.7f) * 0.35f;
                _swingDirs.Add((side.normalized + Vector3.up * lean).normalized);
            }
        }

        void RebuildBulbs()
        {
            if (_style.BulbSpacing <= 0.001f || _style.BulbColors == null || _style.BulbColors.Length == 0) return;
            DrapeMesh.SampleAlong(_render, _style.BulbSpacing, _bulbSpots);

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
            if (_points.Count == 0 || Sculpture == null) return;

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
            Live.Remove(this);
            if (_mesh != null) Destroy(_mesh);
        }
    }
}
