using System.Collections.Generic;
using NUnit.Framework;
using Snowfield.Config;
using Snowfield.Sculpture;
using UnityEngine;

namespace Snowfield.Player.Tests
{
    public class HeldAccessoryTests
    {
        readonly List<GameObject> _objects = new List<GameObject>();
        SculptFeelConfig _config;
        SculptTool _tool;
        AccessoryPlacer _placer;
        HeldAccessoryView _held;
        PlayerInventory Inventory => _placer.Inventory;

        GameObject Make(string name)
        {
            var go = new GameObject(name);
            _objects.Add(go);
            return go;
        }

        [SetUp]
        public void SetUp()
        {
            _config = ScriptableObject.CreateInstance<SculptFeelConfig>();
            _config.gridSize = 16;
            var player = Make("HeldItemTestPlayer");
            player.SetActive(false);
            player.AddComponent<ItemPickupSpawner>().spawnNearbySupplies = false;
            var cameraGo = Make("HeldItemTestCamera");
            cameraGo.transform.SetParent(player.transform, false);
            cameraGo.transform.localPosition = new Vector3(0f, 1.08f, 0f);
            var camera = cameraGo.AddComponent<Camera>();
            camera.enabled = false;
            _tool = player.AddComponent<SculptTool>();
            _tool.config = _config;
            _tool.viewCamera = camera;
            _tool.reachOrigin = player.transform;
            player.SetActive(true);
            _tool.enabled = false; // drive presentation deterministically, without cursor-lock/input requirements
            _held = player.GetComponent<HeldAccessoryView>();
            _held.enabled = false;
            _placer = player.GetComponent<AccessoryPlacer>();
            _tool.SetMode(ToolMode.Accessory);
        }

        [TearDown]
        public void TearDown()
        {
            foreach (var go in _objects) if (go != null) Object.DestroyImmediate(go);
            _objects.Clear();
            Object.DestroyImmediate(_config);
        }

        SnowSculpture Sculpture()
        {
            var go = Make("HeldItemTestSculpture");
            go.SetActive(false);
            var sculpture = go.AddComponent<SnowSculpture>();
            sculpture.EditorAssign(_config, null);
            go.SetActive(true);
            return sculpture;
        }

        [Test]
        public void Holding_StaysAtFrontRight_AndDoesNotFollowTheAimedSurface()
        {
            Inventory.Add("carrot", 1);
            _held.Tick(0f, true);
            Vector3 grip = _held.Model.transform.TransformPoint(_held.GripLocal);
            Assert.Less(Vector3.Distance(_tool.viewCamera.transform.InverseTransformPoint(grip), _config.accessoryHoldOffset), 0.001f);
            Assert.AreEqual(HandRig.Side.Right, _held.HandSide);
            Assert.Less(Vector3.Distance(Vector3.one, _held.Model.transform.lossyScale), 0.0001f);
            _tool.Target = Sculpture();
            _tool.Target.transform.position = new Vector3(1f, 1f, 2f);
            _held.Tick(0f, true);
            Assert.Less(Vector3.Distance(grip, _held.HandGoal), 0.001f);
            Assert.Less(Vector3.Distance(grip, _held.Model.transform.TransformPoint(_held.GripLocal)), 0.001f);
            foreach (var collider in _held.Model.GetComponentsInChildren<Collider>()) Assert.IsFalse(collider.enabled);
            Assert.AreEqual(LayerMask.NameToLayer("Ignore Raycast"), _held.Model.layer);
            Assert.IsNull(GameObject.Find("AccessoryPreview"));
        }

        [Test]
        public void PalmTuning_DoesNotRotateTheHeldItem()
        {
            Inventory.Add("twig", 1);
            _held.Tick(0f, true);
            var rotation = _held.Model.transform.rotation;
            _config.accessoryPalmEuler = new Vector3(25f, -30f, 45f);
            _held.Tick(0f, true);
            Assert.Less(Quaternion.Angle(rotation, _held.Model.transform.rotation), .001f);
        }

        [Test]
        public void Placement_MovesTheHeldItem_AndCommitsExactlyOnceAtContact()
        {
            Inventory.Add("carrot", 2);
            _held.Tick(0f, true);
            var sculpture = Sculpture();
            var point = new Vector3(0f, 1f, 1.5f);
            var normal = Vector3.back;
            Vector3 startGrip = _held.Model.transform.TransformPoint(_held.GripLocal);
            Assert.IsTrue(_placer.BeginPlacement(sculpture, point, normal, 0.3f, 0.4f));
            Assert.IsFalse(_placer.BeginPlacement(sculpture, point, normal, 0.3f, 0.4f));
            _held.Tick(0.15f, true);
            Assert.AreEqual(2, Inventory.Count("carrot"));
            Assert.AreEqual(0, sculpture.Props.Count);
            Assert.IsTrue(_held.Model.activeSelf);
            Assert.Greater(Vector3.Distance(startGrip, _held.Model.transform.TransformPoint(_held.GripLocal)), 0.05f);
            _held.Tick(0.16f, true);
            Assert.AreEqual(1, sculpture.Props.Count);
            Assert.AreEqual(1, Inventory.Count("carrot"));
            var prop = sculpture.Props[0];
            Assert.Less(Vector3.Distance(prop.transform.position, point - normal * AccessoryCatalog.Find("carrot").Sink), 0.001f);
            Assert.Greater(Vector3.Dot(prop.transform.up, normal), 0.999f);
            Assert.IsFalse(_held.Model.activeSelf, "the return stroke must not carry a second item to the surface");
            _held.Tick(0.5f, true);
            Assert.IsTrue(_held.Model.activeSelf);
            Assert.Less(Vector3.Distance(_held.Model.transform.TransformPoint(_held.GripLocal), _held.HoldPosition), 0.001f);
            _held.Tick(0.5f, true);
            Assert.AreEqual(1, sculpture.Props.Count);
            Assert.AreEqual(1, Inventory.Count("carrot"));
        }

        [Test]
        public void CancellingOrLosingTheSurface_DoesNotConsumeAnItem()
        {
            Inventory.Add("carrot", 1);
            _held.Tick(0f, true);
            var sculpture = Sculpture();
            _placer.BeginPlacement(sculpture, Vector3.forward, Vector3.back, 0.3f, 0.4f);
            _held.Tick(0.1f, true);
            _tool.SetMode(ToolMode.Hand);
            _held.Tick(1f, false);
            Assert.AreEqual(1, Inventory.Count("carrot"));
            Assert.AreEqual(0, sculpture.Props.Count);
            Assert.IsFalse(_held.Model.activeSelf);
            Assert.IsFalse(_placer.IsBusy);
            _tool.SetMode(ToolMode.Accessory);
            _held.Tick(0f, true);
            _placer.BeginPlacement(sculpture, Vector3.forward, Vector3.back, 0.3f, 0.4f);
            Object.DestroyImmediate(sculpture.gameObject);
            _held.Tick(1f, true);
            Assert.AreEqual(1, Inventory.Count("carrot"));
            Assert.IsFalse(_placer.IsBusy);
            Assert.IsTrue(_held.Model.activeSelf);
        }

        [Test]
        public void Selection_IsLockedThroughPlacement_ThenReturnsWithTheNextItem()
        {
            Inventory.Add("carrot", 1);
            Inventory.Add("twig", 1);
            _held.Tick(0f, true);
            _placer.BeginPlacement(Sculpture(), Vector3.forward, Vector3.back, 0.3f, 0.4f);
            _placer.Select(1);
            _placer.Step(1);
            Assert.AreEqual("carrot", _placer.Selected.Id);
            _held.Tick(0.31f, true);
            Assert.AreEqual(0, Inventory.Count("carrot"));
            Assert.IsFalse(_held.Model.activeSelf);
            _held.Tick(0.5f, true);
            Assert.AreEqual("twig", _held.ItemId);
            Assert.IsTrue(_held.Model.activeSelf);
            Assert.AreEqual(1, Inventory.Count("twig"));
        }
    }
}
