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

        public void AppendPoint(Vector3 worldPoint)
        {
            if (ActiveChain == null) return;
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
        /// Run the live drape out to the cursor. Pass null when the cursor is off the snow.
        /// The point is smoothed first: the raw raycast hit jitters by a millimetre or two per frame and the strand
        /// would whip with it.
        /// </summary>
        public void UpdateChainTip(Vector3? worldPoint)
        {
            if (ActiveChain == null) return;
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
