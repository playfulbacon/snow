using Snowfield.Sculpture;
using UnityEngine;

namespace Snowfield.Player
{
    /// <summary>The selected item follows the solved hand, at its real world scale. No HUD or surface ghost.</summary>
    [DefaultExecutionOrder(100)]
    [DisallowMultipleComponent]
    public sealed class HeldAccessoryView : MonoBehaviour
    {
        public SculptTool tool;
        public GameObject Model { get; private set; }
        public string ItemId { get; private set; }
        public Vector3 GripLocal { get; private set; }
        public Vector3 HandGoal { get; private set; }
        public HandRig.Side HandSide { get; private set; }
        Quaternion _gripRotation = Quaternion.identity;

        AccessoryPlacer _placer;
        bool _visible;

        void Awake()
        {
            if (tool == null) tool = GetComponent<SculptTool>();
            _placer = GetComponent<AccessoryPlacer>();
        }

        public Vector3 HoldPosition
        {
            get
            {
                if (tool == null || tool.viewCamera == null) return transform.position;
                var offset = tool.config != null ? tool.config.accessoryHoldOffset : new Vector3(0.30f, -0.28f, 0.65f);
                if (HandSide == HandRig.Side.Left) offset.x = -offset.x;
                return tool.viewCamera.transform.TransformPoint(offset);
            }
        }

        Quaternion HoldRotation => tool.viewCamera.transform.rotation * Quaternion.Euler(
            tool.config != null ? tool.config.accessoryHoldEuler : new Vector3(-15f, 0f, -15f));

        Quaternion ItemRotation => _placer.IsPlacing
            ? Quaternion.Slerp(HoldRotation, _placer.PlacementRotation, Mathf.SmoothStep(0f, 1f, _placer.PlacementProgress))
            : HoldRotation;

        Quaternion PalmFrame(Quaternion itemRotation) => itemRotation * _gripRotation * Quaternion.Euler(
            tool.config != null ? tool.config.accessoryPalmEuler : new Vector3(0f, 0f, 180f));

        void Update() => Tick(Time.deltaTime, tool != null && tool.isActiveAndEnabled && tool.Mode == ToolMode.Accessory);

        /// <summary>Publish the hand's intent before IK. Also used by deterministic playtests without mouse input.</summary>
        public void Tick(float dt, bool itemEquipped)
        {
            if (_placer == null) _placer = GetComponent<AccessoryPlacer>();
            if (tool == null || tool.viewCamera == null || _placer == null) return;
            var roller = tool.Roller;
            bool carrying = roller != null && roller.IsCarrying;
            HandSide = carrying ? HandRig.Side.Left : HandRig.Side.Right;
            if (!itemEquipped || (carrying && roller.TwoHandedCarry))
            {
                _placer.CancelPlacement();
                Hide();
                return;
            }

            if (!_placer.IsRecovering && _placer.Selected != null) EnsureModel(_placer.Selected);
            Vector3 contact = _placer.PlacementOrigin + _placer.PlacementRotation * GripLocal;
            var contactPalm = PalmFrame(_placer.PlacementRotation);
            var placed = _placer.AdvancePlacement(dt);
            var hands = tool.hands;
            if (placed != null && hands != null)
                hands.Pulse(HandSide, contact, tool.config != null ? tool.config.handFollowThrough : 0.18f,
                    contactPalm * Vector3.forward, contactPalm * Vector3.up);

            _visible = _placer.HasSelectedItem && !_placer.IsRecovering;
            if (_visible) EnsureModel(_placer.Selected);
            if (Model != null) Model.SetActive(_visible);
            HandGoal = _placer.IsPlacing
                ? _placer.PlacementOrigin + _placer.PlacementRotation * GripLocal : HoldPosition;
            if (hands != null && (_visible || _placer.IsRecovering))
            {
                var palm = PalmFrame(ItemRotation);
                hands.Reach(HandSide, HandGoal, 1f,
                    palm * Vector3.forward, followBody: !_placer.IsPlacing, palmNormal: palm * Vector3.up);
            }
            UpdatePose();
        }

        void EnsureModel(AccessoryCatalog.Entry item)
        {
            if (Model != null && ItemId == item.Id) return;
            if (Model != null) { Model.SetActive(false); Destroy(Model); }
            ItemId = item.Id;
            Model = item.Build();
            Model.name = "HeldAccessory_" + item.Id;
            Model.transform.SetParent(transform, false);
            AccessoryCatalog.SetLayerRecursive(Model, LayerMask.NameToLayer("Ignore Raycast"));
            AccessoryCatalog.SetColliders(Model, false);
            var authoredGrip = Model.transform.Find("Grip");
            _gripRotation = authoredGrip != null ? authoredGrip.localRotation : Quaternion.identity;
            if (authoredGrip != null)
            {
                GripLocal = authoredGrip.localPosition;
                return;
            }
            // Measure in the model's own space. Do not normalize its size or inherit a stretched bone's scale.
            var bounds = new Bounds(Vector3.zero, Vector3.zero);
            bool first = true;
            foreach (var renderer in Model.GetComponentsInChildren<Renderer>())
            {
                var world = renderer.bounds;
                for (int i = 0; i < 8; i++)
                {
                    var corner = world.center + Vector3.Scale(world.extents,
                        new Vector3((i & 1) == 0 ? -1 : 1, (i & 2) == 0 ? -1 : 1, (i & 4) == 0 ? -1 : 1));
                    var local = Model.transform.InverseTransformPoint(corner);
                    if (first) { bounds = new Bounds(local, Vector3.zero); first = false; }
                    else bounds.Encapsulate(local);
                }
            }
            GripLocal = item.Drape != null ? bounds.center
                : new Vector3(bounds.center.x, bounds.min.y + Mathf.Min(0.08f, bounds.size.y * 0.2f), bounds.center.z);
        }

        // Runs after IK so the grip follows the centre of the palm, not the wrist joint.
        void LateUpdate() => UpdatePose();

        void UpdatePose()
        {
            if (!_visible || Model == null || tool == null || tool.viewCamera == null) return;
            var hands = tool.hands;
            float progress = _placer.IsPlacing ? Mathf.SmoothStep(0f, 1f, _placer.PlacementProgress) : 0f;
            var rotation = ItemRotation;
            Vector3 grip = hands != null && hands.IsReady ? hands.PalmPosition(HandSide)
                : Vector3.Lerp(HoldPosition, HandGoal, progress);
            Model.transform.SetPositionAndRotation(grip - rotation * GripLocal, rotation);
        }

        void Hide() { _visible = false; if (Model != null) Model.SetActive(false); }
        void OnDisable() { if (_placer != null) _placer.CancelPlacement(); Hide(); }
        void OnDestroy() { if (Model != null) Destroy(Model); }
    }
}
