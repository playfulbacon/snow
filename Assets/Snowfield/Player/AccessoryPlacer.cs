using System.Collections.Generic;
using Snowfield.Sculpture;
using UnityEngine;

namespace Snowfield.Player
{
    /// <summary>
    /// Keeps the selected accessory and commits placements after the hand reaches the clicked surface.
    /// Chained accessories (lights, scarf) instead run a placing session: each click adds a point,
    /// the strand drapes live between them, and Shift+click finishes. Driven by <see cref="SculptTool"/>; owns no
    /// input itself.
    /// </summary>
    [RequireComponent(typeof(PlayerInventory))]
    public class AccessoryPlacer : MonoBehaviour
    {
        int _selectedIndex;
        public int SelectedIndex
        {
            get
            {
                // Once a stack is spent, show the next item instead of an empty slot.
                if (ActiveChain == null && !IsPlacing && Inventory != null && Inventory.CountAt(_selectedIndex) == 0)
                    _selectedIndex = NextOccupied(_selectedIndex - 1, 1);
                return _selectedIndex;
            }
        }
        public AccessoryCatalog.Entry Selected => Inventory != null ? Inventory.ItemAt(SelectedIndex) : null;
        public PlayerInventory Inventory { get; private set; }
        public bool HasSelectedItem => Selected != null;
        public int SlotCount => Inventory != null ? Inventory.SlotCount : 0;

        /// <summary>
        /// One thumbnail per catalog entry, rendered once at startup. Catalog-aligned, not slot-aligned: a slot
        /// holds whatever item was picked up, and two slots can hold the same one, so the bar looks its picture
        /// up by item id rather than by position. Removed as dead code while the bar did not exist yet.
        /// </summary>
        public IReadOnlyList<Texture2D> Thumbnails => _thumbs;
        readonly List<Texture2D> _thumbs = new List<Texture2D>();

        /// <summary>The picture for an item id, or null if it has none.</summary>
        public Texture2D ThumbnailFor(string itemId)
        {
            if (string.IsNullOrEmpty(itemId)) return null;
            var entries = AccessoryCatalog.Entries;
            for (int i = 0; i < entries.Count && i < _thumbs.Count; i++)
                if (entries[i].Id == itemId) return _thumbs[i];
            return null;
        }

        sealed class Placement
        {
            public SnowSculpture sculpture;
            public AccessoryCatalog.Entry item;
            public int slot;
            public Vector3 localPoint, localNormal;
            public float elapsed, duration, recovery;
        }
        Placement _placement;
        float _recovery;
        public bool IsPlacing => _placement != null;
        public bool IsRecovering => _recovery > 0f;
        public bool IsBusy => IsPlacing || IsRecovering;
        public float PlacementProgress => _placement != null ? Mathf.Clamp01(_placement.elapsed / _placement.duration) : 0f;
        public Vector3 PlacementPoint => _placement != null && _placement.sculpture != null
            ? _placement.sculpture.transform.TransformPoint(_placement.localPoint) : Vector3.zero;
        public Vector3 PlacementNormal => _placement != null && _placement.sculpture != null
            ? _placement.sculpture.transform.TransformDirection(_placement.localNormal).normalized : Vector3.up;
        public Quaternion PlacementRotation => Quaternion.FromToRotation(Vector3.up, PlacementNormal);
        public Vector3 PlacementOrigin => PlacementPoint - PlacementNormal * (_placement != null ? _placement.item.Sink : 0f);

        public bool BeginPlacement(SnowSculpture sculpture, Vector3 point, Vector3 normal, float duration, float recovery)
        {
            if (sculpture == null || IsBusy || ActiveChain != null || !HasSelectedItem || Selected.Drape != null) return false;
            _placement = new Placement
            {
                sculpture = sculpture, item = Selected, slot = SelectedIndex,
                localPoint = sculpture.transform.InverseTransformPoint(point),
                localNormal = sculpture.transform.InverseTransformDirection(normal.normalized),
                duration = Mathf.Max(0.01f, duration), recovery = Mathf.Max(0f, recovery),
            };
            return true;
        }

        /// <summary>Called by the held-item view. Cancelling never spends an item; only contact commits it.</summary>
        public SculptureProp AdvancePlacement(float dt)
        {
            dt = Mathf.Max(0f, dt);
            _recovery = Mathf.Max(0f, _recovery - dt);
            if (_placement == null) return null;
            var placement = _placement;
            if (placement.sculpture == null || Inventory.ItemIdAt(placement.slot) != placement.item.Id)
            {
                CancelPlacement();
                return null;
            }
            placement.elapsed += dt;
            if (placement.elapsed < placement.duration) return null;
            var point = PlacementPoint;
            var normal = PlacementNormal;
            _placement = null;
            var prop = PlaceEntry(placement.sculpture, placement.item, point, normal);
            if (prop != null)
            {
                Inventory.TryConsumeAt(placement.slot);
                _recovery = placement.recovery;
            }
            return prop;
        }

        public void CancelPlacement() { _placement = null; _recovery = 0f; }

        void Awake()
        {
            Inventory = GetComponent<PlayerInventory>();
            // RequireComponent does not retrofit components already serialized in older scenes.
            if (Inventory == null) Inventory = gameObject.AddComponent<PlayerInventory>();
        }

        void Start() => RenderThumbnails();

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


        void OnDisable()
        {
            CancelChain();
            CancelPlacement();
        }

        public void Select(int index)
        {
            if (ActiveChain != null || IsBusy) return;
            int n = SlotCount;
            if (n == 0) { _selectedIndex = 0; return; }
            int wrapped = ((index % n) + n) % n;
            _selectedIndex = Inventory.CountAt(wrapped) > 0 ? wrapped : NextOccupied(wrapped - 1, 1);
        }

        public void Step(int delta)
        {
            if (ActiveChain != null || IsBusy) return;
            if (delta == 0) return;
            _selectedIndex = NextOccupied(SelectedIndex, delta > 0 ? 1 : -1);
        }

        int NextOccupied(int from, int direction)
        {
            int n = SlotCount;
            for (int i = 1; i <= n; i++)
            {
                int slot = ((from + i * direction) % n + n) % n;
                if (Inventory.CountAt(slot) > 0) return slot;
            }
            return 0;
        }

        // ---------- chained accessories (lights, scarf) ----------

        /// <summary>The chain being placed, or null. While one exists it owns the clicks.</summary>
        public DrapedAccessory ActiveChain { get; private set; }

        /// <summary>Start a chain of the selected style with its first point.</summary>
        public void BeginChain(SnowSculpture sculpture, Vector3 worldPoint)
        {
            if (ActiveChain != null || IsBusy || sculpture == null || !HasSelectedItem || Selected.Drape == null) return;
            _hasSmoothTip = false;
            var local = sculpture.transform.InverseTransformPoint(worldPoint);
            ActiveChain = DrapedAccessory.Create(sculpture, Selected, new[] { local }, frozen: false);
        }

        /// <summary>
        /// A strand belongs to the sculpture its first point landed on, and every later point has to land there
        /// too. The shape is solved against that one grid, so a point on the snowman next door would be joined to
        /// the rest by a span with nothing under it — a straight line through the air between two sculptures. It
        /// also has nowhere to live: the points are stored in the owner's local space and travel with it.
        /// </summary>
        public bool CanExtendOnto(SnowSculpture target) =>
            ActiveChain != null && target != null && target == ActiveChain.Sculpture;

        public void AppendPoint(SnowSculpture target, Vector3 worldPoint)
        {
            if (!CanExtendOnto(target)) return;
            ActiveChain.AddPointWorld(worldPoint);
        }

        /// <summary>Reuse a point already in this chain — how a scarf closes into a loop.</summary>
        public void UseExistingPoint(DrapeAnchor anchor)
        {
            if (ActiveChain == null || anchor == null || anchor.Chain != ActiveChain) return;
            ActiveChain.AddExistingPoint(anchor.Index);
        }

        Vector3 _smoothTip;
        bool _hasSmoothTip;

        /// <summary>
        /// Run the live drape out to the cursor. <paramref name="target"/> is the sculpture under it; pass null
        /// for either when the cursor is off the snow. Aiming at a sculpture the strand does not belong to counts
        /// as off: the strand reels back to its last point, which is the feedback that the click will not land.
        /// The point is smoothed first: the raw raycast hit jitters by a millimetre or two per frame and the strand
        /// would whip with it.
        /// </summary>
        public void UpdateChainTip(SnowSculpture target, Vector3? worldPoint)
        {
            if (ActiveChain == null) return;
            if (!CanExtendOnto(target)) worldPoint = null;
            if (!worldPoint.HasValue)
            {
                _hasSmoothTip = false;
                ActiveChain.SetTip(null);
            }
            else
            {
                var cfg = ActiveChain.Sculpture != null ? ActiveChain.Sculpture.Config : null;
                float follow = cfg != null ? cfg.drapeTipFollow : 18f;
                _smoothTip = _hasSmoothTip
                    ? Vector3.Lerp(_smoothTip, worldPoint.Value, 1f - Mathf.Exp(-follow * Time.deltaTime))
                    : worldPoint.Value;
                _hasSmoothTip = true;
                ActiveChain.SetTip(_smoothTip);
            }
            ActiveChain.StepLive();
        }

        /// <summary>Settle the strand and hand it over as an ordinary placed accessory.</summary>
        public void FinishChain()
        {
            var chain = ActiveChain;
            ActiveChain = null;
            if (chain == null) return;
            // The live chain holds this item until it is committed. Cancellation never spends it.
            if (chain.PointCount < 2 || chain.Sculpture == null || Selected?.Id != chain.prefabId
                || !Inventory.TryConsumeAt(SelectedIndex))
            {
                chain.Remove();
                return;
            }
            chain.Freeze();
            if (chain.Sculpture != null)
                SculptureNet.RaiseDrapePlaced(chain.Sculpture, chain.prefabId, chain.Points);
        }

        /// <summary>Throw away a chain that never got a second point (or was abandoned).</summary>
        public void CancelChain()
        {
            if (ActiveChain == null) return;
            var chain = ActiveChain;
            ActiveChain = null;
            chain.Remove();
        }

        static void PoseFor(AccessoryCatalog.Entry entry, Vector3 point, Vector3 normal, out Vector3 pos, out Quaternion rot)
        {
            rot = Quaternion.FromToRotation(Vector3.up, normal);
            pos = point - normal * entry.Sink;
        }

        /// <summary>Spend one item when placing a rigid accessory. Replay/load uses PlaceEntry directly.</summary>
        public SculptureProp Place(SnowSculpture sculpture, Vector3 point, Vector3 normal)
        {
            if (sculpture == null || IsBusy || ActiveChain != null || !HasSelectedItem || Selected.Drape != null) return null;
            var prop = PlaceEntry(sculpture, Selected, point, normal);
            if (prop != null) Inventory.TryConsumeAt(SelectedIndex);
            return prop;
        }

        /// <summary>The fully parameterized twin of <see cref="Place"/> — also what a remote place replays.</summary>
        public static SculptureProp PlaceEntry(SnowSculpture sculpture, AccessoryCatalog.Entry entry, Vector3 point, Vector3 normal)
        {
            if (sculpture == null || entry == null || entry.Drape != null) return null;
            PoseFor(entry, point, normal, out var pos, out var rot);
            var go = entry.Build();
            AccessoryCatalog.MakePickable(go);
            var prop = go.AddComponent<SculptureProp>();
            prop.Attach(sculpture, entry.Id, pos, rot);
            SculptureNet.RaisePropPlaced(sculpture, entry.Id, point, normal);
            return prop;
        }

        /// <summary>Take a placed accessory off its sculpture.</summary>
        public bool CanRetrieve(SculptureProp prop) => prop != null && !prop.IsRemoved
            && ActiveChain == null && !IsBusy && Inventory != null && Inventory.SpaceFor(prop.prefabId) > 0
            && (!(prop is DrapedAccessory chain) || chain.Frozen);

        public bool Retrieve(SculptureProp prop)
        {
            if (!CanRetrieve(prop)) return false;
            Inventory.Add(prop.prefabId, 1);
            if (prop.Sculpture != null)
                SculptureNet.RaisePropRemoved(prop.Sculpture, prop.prefabId, prop.LocalPos);
            prop.Remove();
            return true;
        }

    }
}
