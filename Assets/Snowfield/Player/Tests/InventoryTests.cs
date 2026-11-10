using System.Collections;
using System.Collections.Generic;
using NUnit.Framework;
using Snowfield.Config;
using Snowfield.Sculpture;
using UnityEngine;
using UnityEngine.TestTools;
using UnityEngine.UI;

namespace Snowfield.Player.Tests
{
    public class InventoryTests
    {
        readonly List<GameObject> _objects = new List<GameObject>();
        PlayerInventory _inventory;
        AccessoryPlacer _placer;
        SculptFeelConfig _config;

        GameObject MakeObject(string name)
        {
            var go = new GameObject(name);
            _objects.Add(go);
            return go;
        }

        [SetUp]
        public void SetUp()
        {
            _placer = MakeObject("InventoryTestPlayer").AddComponent<AccessoryPlacer>();
            _placer.enabled = false;
            _inventory = _placer.Inventory;
        }

        [TearDown]
        public void TearDown()
        {
            foreach (var go in _objects) if (go != null) Object.DestroyImmediate(go);
            _objects.Clear();
            if (_config != null) Object.DestroyImmediate(_config);
        }

        SnowSculpture MakeSculpture()
        {
            _config = ScriptableObject.CreateInstance<SculptFeelConfig>();
            _config.gridSize = 16;
            var go = MakeObject("InventoryTestSculpture");
            go.SetActive(false);
            var sculpture = go.AddComponent<SnowSculpture>();
            sculpture.EditorAssign(_config, null);
            go.SetActive(true);
            return sculpture;
        }

        ItemPickup MakePickup(string id, int quantity)
        {
            var pickup = ItemPickup.Create(id, quantity, Vector3.zero);
            _objects.Add(pickup.gameObject);
            return pickup;
        }

        [Test]
        public void NewInventory_IsEmpty_AndEachItemHasItsOwnLimit()
        {
            foreach (var entry in AccessoryCatalog.Entries)
            {
                Assert.AreEqual(0, _inventory.Count(entry.Id));
                var inventory = MakeObject("StackLimitTest").AddComponent<PlayerInventory>();
                int quantity = entry.StackLimit * 10 + 1;
                Assert.AreEqual(0, inventory.SlotCount);
                Assert.AreEqual(quantity, inventory.Add(entry.Id, quantity));
                Assert.AreEqual(quantity, inventory.Count(entry.Id));
                Assert.AreEqual(11, inventory.SlotCount);
                for (int slot = 0; slot < inventory.SlotCount; slot++)
                {
                    Assert.AreEqual(entry.Id, inventory.ItemIdAt(slot));
                    Assert.AreEqual(slot == 10 ? 1 : entry.StackLimit, inventory.CountAt(slot));
                }
            }
            Assert.Greater(AccessoryCatalog.Find("twig").StackLimit, AccessoryCatalog.Find("lights").StackLimit);
        }

        [Test]
        public void InvalidOrNegativeTransfers_DoNotChangeInventory()
        {
            Assert.AreEqual(0, _inventory.Add("unknown", 2));
            Assert.AreEqual(0, _inventory.Add("carrot", -2));
            Assert.IsFalse(_inventory.TryConsume("unknown"));
            Assert.IsFalse(_inventory.TryConsume("carrot"));
            Assert.AreEqual(0, _inventory.Count("carrot"));
        }

        [Test]
        public void Pickup_CollectsEntireStack_AndGrowsInventoryBeyondExistingStacks()
        {
            int limit = AccessoryCatalog.Find("carrot").StackLimit;
            int before = limit * 8 - 1;
            _inventory.Add("carrot", before);
            var pickup = MakePickup("carrot", 3);
            Assert.AreEqual(3, pickup.Collect(_inventory));
            Assert.AreEqual(before + 3, _inventory.Count("carrot"));
            Assert.AreEqual(9, _inventory.SlotCount);
            Assert.AreEqual(limit, _inventory.CountAt(7));
            Assert.AreEqual(2, _inventory.CountAt(8));
            Assert.AreEqual(0, pickup.quantity);
            Assert.IsFalse(pickup.gameObject.activeSelf);
        }

        [Test]
        public void DepletedPickup_CannotBeCollectedTwiceInOneFrame()
        {
            var pickup = MakePickup("twig", 2);
            Assert.AreEqual(2, pickup.Collect(_inventory));
            Assert.IsFalse(pickup.gameObject.activeSelf);
            Assert.AreEqual(0, pickup.Collect(_inventory));
            Assert.AreEqual(2, _inventory.Count("twig"));
        }

        [Test]
        public void PlacementAndRetrieval_ConserveItems_AndRejectEmptyPlacement()
        {
            var sculpture = MakeSculpture();
            _placer.Select(0);
            Assert.IsNull(_placer.Place(sculpture, Vector3.zero, Vector3.up));
            Assert.AreEqual(0, sculpture.Props.Count);
            _inventory.Add("carrot", 1);
            Assert.IsNull(_placer.Place(null, Vector3.zero, Vector3.up));
            Assert.AreEqual(1, _inventory.Count("carrot"));
            var prop = _placer.Place(sculpture, Vector3.zero, Vector3.up);
            Assert.IsNotNull(prop);
            Assert.AreEqual(0, _inventory.Count("carrot"));
            Assert.IsNull(_placer.Place(sculpture, Vector3.zero, Vector3.up));
            Assert.IsTrue(_placer.Retrieve(prop));
            Assert.IsFalse(_placer.Retrieve(prop));
            Assert.AreEqual(1, _inventory.Count("carrot"));
            Assert.AreEqual(0, sculpture.Props.Count);
        }

        [Test]
        public void Retrieval_CreatesAnotherStack_WhenMatchingStackIsFull()
        {
            var sculpture = MakeSculpture();
            var entry = AccessoryCatalog.Find("twig");
            var prop = AccessoryPlacer.PlaceEntry(sculpture, entry, Vector3.zero, Vector3.up);
            _inventory.Add("twig", entry.StackLimit);
            Assert.IsTrue(_placer.Retrieve(prop));
            Assert.IsTrue(prop.IsRemoved);
            Assert.AreEqual(entry.StackLimit + 1, _inventory.Count("twig"));
            Assert.AreEqual(2, _inventory.SlotCount);
            Assert.AreEqual(1, _inventory.CountAt(1));
        }

        [Test]
        public void ReplayPlacement_DoesNotSpendLocalInventory()
        {
            var sculpture = MakeSculpture();
            var prop = AccessoryPlacer.PlaceEntry(sculpture, AccessoryCatalog.Find("carrot"), Vector3.zero, Vector3.up);
            Assert.IsNotNull(prop);
            Assert.AreEqual(0, _inventory.Count("carrot"));
            Assert.AreEqual(1, sculpture.Props.Count);
        }

        [TestCase("lights")]
        [TestCase("scarf")]
        public void Strand_CancelPreservesItem_FinishSpendsOne_AndRetrievalReturnsOne(string id)
        {
            var sculpture = MakeSculpture();
            int slot = 0;
            _placer.Select(slot);
            _placer.BeginChain(sculpture, Vector3.zero);
            Assert.IsNull(_placer.ActiveChain, "empty slots cannot start strands");
            _inventory.Add(id, 1);
            _inventory.Add("twig", 1);
            _placer.BeginChain(sculpture, Vector3.zero);
            Assert.IsNotNull(_placer.ActiveChain);
            _placer.Select(1);
            Assert.AreEqual(slot, _placer.SelectedIndex, "selection stays pinned during a strand");
            _placer.FinishChain(); // a one-point misclick is cancelled
            Assert.AreEqual(1, _inventory.Count(id));
            Assert.AreEqual(0, sculpture.Props.Count);
            _placer.BeginChain(sculpture, Vector3.zero);
            _placer.AppendPoint(sculpture, Vector3.right * 0.3f);
            _placer.CancelChain();
            Assert.AreEqual(1, _inventory.Count(id));
            _placer.BeginChain(sculpture, Vector3.zero);
            _placer.AppendPoint(sculpture, Vector3.right * 0.3f);
            var chain = _placer.ActiveChain;
            _placer.FinishChain();
            Assert.IsTrue(chain.Frozen);
            Assert.AreEqual(0, _inventory.Count(id));
            Assert.AreEqual(1, sculpture.Props.Count);
            _placer.FinishChain();
            Assert.IsTrue(_placer.Retrieve(chain));
            Assert.AreEqual(1, _inventory.Count(id));
        }

        [Test]
        public void Slots_AcceptItemsInPickupOrder_AndForgetDepletedItems()
        {
            _inventory.Add("scarf", 1);
            _inventory.Add("carrot", 1);
            Assert.AreEqual("scarf", _inventory.ItemIdAt(0));
            Assert.AreEqual("carrot", _inventory.ItemIdAt(1));
            Assert.IsTrue(_inventory.TryConsumeAt(0));
            Assert.IsNull(_inventory.ItemIdAt(0));
            Assert.AreEqual("carrot", _placer.Selected.Id);
            _inventory.Add("lights", 1);
            _placer.Select(0);
            Assert.AreEqual("lights", _inventory.ItemIdAt(0));
            Assert.AreEqual("lights", _placer.Selected.Id);
            Assert.AreEqual("carrot", _inventory.ItemIdAt(1));
        }

        [Test]
        public void Pickup_TopsUpMatchingStacksBeforeUsingEarlierEmptySlots()
        {
            _inventory.Add("twig", 1);
            _inventory.Add("carrot", 1);
            _inventory.TryConsumeAt(0);
            _inventory.Add("carrot", 2);
            Assert.IsNull(_inventory.ItemIdAt(0));
            Assert.AreEqual(3, _inventory.CountAt(1));
            _inventory.Add("carrot", AccessoryCatalog.Find("carrot").StackLimit);
            Assert.AreEqual(AccessoryCatalog.Find("carrot").StackLimit, _inventory.CountAt(1));
            Assert.AreEqual("carrot", _inventory.ItemIdAt(0));
            Assert.AreEqual(3, _inventory.CountAt(0));
        }

        [Test]
        public void Inventory_GrowsPastTheCatalogSize_WithoutAConfiguredSlotLimit()
        {
            int limit = AccessoryCatalog.Find("carrot").StackLimit;
            int stacks = AccessoryCatalog.Entries.Count + 10;
            int quantity = limit * stacks;
            Assert.AreEqual(quantity, _inventory.Add("carrot", quantity));
            Assert.AreEqual(stacks, _inventory.SlotCount);
            Assert.AreEqual(1, _inventory.Add("twig", 1));
            Assert.AreEqual(stacks + 1, _inventory.SlotCount);
            Assert.AreEqual("twig", _inventory.ItemIdAt(stacks));
            Assert.Greater(_inventory.SpaceFor("carrot"), 0);
        }

        [Test]
        public void Selection_WrapsOccupiedStacks_AndHandlesAnEmptyInventory()
        {
            _placer.Select(0);
            _placer.Step(1);
            _placer.Step(-1);
            Assert.IsNull(_placer.Selected);
            _inventory.Add("twig", 1);
            _inventory.Add("carrot", 1);
            _inventory.Add("scarf", 1);
            _placer.Select(0);
            _inventory.TryConsumeAt(1);
            _placer.Step(1);
            Assert.AreEqual(2, _placer.SelectedIndex);
            _placer.Step(1);
            Assert.AreEqual(0, _placer.SelectedIndex);
            _placer.Step(-1);
            Assert.AreEqual(2, _placer.SelectedIndex);
            _inventory.TryConsumeAt(2);
            Assert.AreEqual("twig", _placer.Selected.Id);
            _inventory.TryConsumeAt(0);
            Assert.IsNull(_placer.Selected);
            _placer.Step(1);
            Assert.IsNull(_placer.Selected);
        }

        [Test]
        public void Placement_SpendsFromSelectedStack_AndLeavesOtherMatchingStacksAlone()
        {
            var sculpture = MakeSculpture();
            int limit = AccessoryCatalog.Find("carrot").StackLimit;
            _inventory.Add("carrot", limit + 1);
            _placer.Select(1);
            var prop = _placer.Place(sculpture, Vector3.zero, Vector3.up);
            Assert.IsNotNull(prop);
            Assert.AreEqual(limit, _inventory.CountAt(0));
            Assert.AreEqual(0, _inventory.CountAt(1));
            Assert.AreEqual("carrot", _placer.Selected.Id);
            Assert.AreEqual(0, _placer.SelectedIndex);
            Assert.IsTrue(_placer.Retrieve(prop));
            Assert.AreEqual("carrot", _inventory.ItemIdAt(1));
            Assert.AreEqual(1, _inventory.CountAt(1));
        }

        [UnityTest]
        public IEnumerator Hud_DoesNotShowAFloatingInventoryPreview()
        {
            var tool = _placer.gameObject.AddComponent<SculptTool>();
            tool.enabled = false;
            tool.GetComponent<ItemPickupSpawner>().spawnNearbySupplies = false;
            var hud = MakeObject("InventoryTestHud").AddComponent<ToolHud>();
            hud.tool = tool;
            hud.placer = _placer;
            _inventory.Add("carrot", 1);
            yield return null;
            Assert.IsNull(hud.transform.Find("SelectedItem"));
            Assert.IsNull(hud.transform.Find("AccessoryBar"));
            var inventory = hud.transform.Find("Inventory");
            Assert.IsNotNull(inventory);
            Assert.IsFalse(inventory.gameObject.activeSelf);
            Assert.IsEmpty(hud.GetComponentsInChildren<RawImage>());
        }

        [UnityTest]
        public IEnumerator CarrotVariants_KeepTheirMeshThroughPickupPlacementAndRetrieval()
        {
            var definitions = Resources.LoadAll<AccessoryItemDefinition>("Items/Carrots");
            Assert.AreEqual(9, definitions.Length, "Every mesh in Carrots.blend should have its own item");
            var meshes = new HashSet<Mesh>();
            var sculpture = MakeSculpture();
            foreach (var definition in definitions)
            {
                var entry = AccessoryCatalog.Find(definition.itemId);
                Assert.IsNotNull(entry);
                Assert.AreSame(definition.prefab, entry.Prefab);
                var sourceMesh = definition.prefab.GetComponentInChildren<MeshFilter>().sharedMesh;
                Assert.IsTrue(meshes.Add(sourceMesh), "Each variant must reference a different source mesh");
#if UNITY_EDITOR
                string path = "Assets/Props/Carrots/Pickups/" + definition.prefab.name + "_Pickup.prefab";
                var pickupPrefab = UnityEditor.AssetDatabase.LoadAssetAtPath<GameObject>(path);
                Assert.IsNotNull(pickupPrefab);
                var go = Object.Instantiate(pickupPrefab);
                _objects.Add(go);
                var pickup = go.GetComponent<ItemPickup>();
#else
                var pickup = MakePickup(definition.itemId, 1);
#endif
                yield return null;
                Assert.AreEqual(1, pickup.GetComponentsInChildren<MeshFilter>().Length, "Authored pickup models must not be duplicated at startup");
                Assert.AreSame(sourceMesh, pickup.GetComponentInChildren<MeshFilter>().sharedMesh);
                Assert.AreEqual(1, pickup.Collect(_inventory));
                _placer.Select(_inventory.FindFirstSlot(definition.itemId));
                var prop = _placer.Place(sculpture, Vector3.zero, Vector3.forward);
                Assert.IsNotNull(prop);
                Assert.AreEqual(definition.itemId, prop.prefabId);
                Assert.AreSame(sourceMesh, prop.GetComponentInChildren<MeshFilter>().sharedMesh);
                Assert.AreEqual(0, _inventory.Count(definition.itemId));
                Assert.AreEqual(definition.itemId, SaveLoadManager.ToRecord(sculpture).props[0].prefabId);
                Assert.IsTrue(_placer.Retrieve(prop));
                Assert.AreEqual(1, _inventory.Count(definition.itemId));
                Assert.IsTrue(_inventory.TryConsume(definition.itemId));
            }
        }

        [UnityTest]
        public IEnumerator StickVariants_KeepTheirMeshThroughPickupHoldingPlacementAndRetrieval()
        {
            var definitions = Resources.LoadAll<AccessoryItemDefinition>("Items/Sticks");
            Assert.AreEqual(9, definitions.Length);
            var sculpture = MakeSculpture();
            var tool = _placer.gameObject.AddComponent<SculptTool>();
            tool.enabled = false;
            tool.config = _config;
            tool.GetComponent<ItemPickupSpawner>().spawnNearbySupplies = false;
            var camera = MakeObject("StickTestCamera").AddComponent<Camera>();
            camera.enabled = false;
            tool.viewCamera = camera;
            var held = tool.GetComponent<HeldAccessoryView>();
            held.enabled = false;
            var meshes = new HashSet<Mesh>();
            foreach (var definition in definitions)
            {
                var entry = AccessoryCatalog.Find(definition.itemId);
                Assert.AreSame(definition.prefab, entry.Prefab);
                var sourceMesh = definition.prefab.GetComponentInChildren<MeshFilter>().sharedMesh;
                Assert.IsTrue(meshes.Add(sourceMesh), "Each stick should have its own source mesh");
                var renderer = definition.prefab.GetComponentInChildren<MeshRenderer>();
                Assert.AreEqual(sourceMesh.subMeshCount, renderer.sharedMaterials.Length);
                foreach (var material in renderer.sharedMaterials) Assert.IsNotNull(material);
#if UNITY_EDITOR
                Assert.AreEqual("Assets/Props/Sticks.blend", UnityEditor.AssetDatabase.GetAssetPath(sourceMesh));
                string path = "Assets/Props/Sticks/Pickups/" + definition.prefab.name + "_Pickup.prefab";
                var prefab = UnityEditor.AssetDatabase.LoadAssetAtPath<GameObject>(path);
                Assert.IsNotNull(prefab);
                var go = Object.Instantiate(prefab);
                _objects.Add(go);
                var pickup = go.GetComponent<ItemPickup>();
#else
                var pickup = MakePickup(definition.itemId, 1);
#endif
                yield return null;
                Assert.AreEqual(1, pickup.GetComponentsInChildren<MeshFilter>().Length);
                Assert.AreEqual(1, pickup.Collect(_inventory));
                _placer.Select(_inventory.FindFirstSlot(definition.itemId));
                held.Tick(0f, true);
                Assert.AreEqual(definition.itemId, held.ItemId);
                Assert.Less(Vector3.Distance(held.GripLocal, definition.prefab.transform.Find("Grip").localPosition), 0.001f);
                Assert.AreSame(sourceMesh, held.Model.GetComponentInChildren<MeshFilter>().sharedMesh);
                Assert.IsTrue(held.Model.activeInHierarchy);
                var prop = _placer.Place(sculpture, Vector3.zero, Vector3.forward);
                Assert.AreEqual(definition.itemId, prop.prefabId);
                Assert.AreSame(sourceMesh, prop.GetComponentInChildren<MeshFilter>().sharedMesh);
                Assert.AreEqual(0, _inventory.Count(definition.itemId));
                Assert.AreEqual(definition.itemId, SaveLoadManager.ToRecord(sculpture).props[0].prefabId);
                Assert.IsTrue(_placer.Retrieve(prop));
                Assert.AreEqual(1, _inventory.Count(definition.itemId));
                Assert.IsTrue(_inventory.TryConsume(definition.itemId));
            }
        }

        [UnityTest]
        public IEnumerator AccessoryPickupPrefabs_ReuseVisibleModels_AndCollectTheCorrectItems()
        {
            foreach (string id in new[] { "scarf", "lights", "button", "pebble" })
            {
                var entry = AccessoryCatalog.Find(id);
#if UNITY_EDITOR
                string path = "Assets/Props/Accessories/Pickups/" + entry.DisplayName + "_Pickup.prefab";
                var prefab = UnityEditor.AssetDatabase.LoadAssetAtPath<GameObject>(path);
                Assert.IsNotNull(prefab, path);
                var go = Object.Instantiate(prefab);
                _objects.Add(go);
                var pickup = go.GetComponent<ItemPickup>();
                var authoredModel = pickup.worldModel;
                int parts = authoredModel.GetComponentsInChildren<MeshRenderer>().Length;
                Assert.Greater(parts, 0);
#else
                var pickup = MakePickup(id, 1);
#endif
                yield return null;
#if UNITY_EDITOR
                Assert.AreSame(authoredModel, pickup.worldModel);
                Assert.AreEqual(parts, pickup.GetComponentsInChildren<MeshRenderer>().Length,
                    "Startup must reuse the visible prefab model rather than build a duplicate");
#endif
                Assert.AreEqual(id, pickup.itemId);
                foreach (var collider in pickup.worldModel.GetComponentsInChildren<Collider>()) Assert.IsFalse(collider.enabled);
                var target = pickup.GetComponent<BoxCollider>();
                Assert.IsTrue(target.enabled && target.isTrigger);
                Physics.SyncTransforms();
                Assert.IsTrue(target.Raycast(new Ray(target.bounds.center + Vector3.up, Vector3.down), out _, 2f));
                Assert.AreEqual(1, pickup.Collect(_inventory));
                Assert.AreEqual(1, _inventory.Count(id));
                Assert.IsFalse(pickup.gameObject.activeSelf);
                if (id == "scarf" || id == "lights") Assert.IsNotNull(entry.Drape);
                Assert.IsTrue(_inventory.TryConsume(id));
            }
        }

        [UnityTest]
        public IEnumerator ScenePickup_BuildsVisibleModel_AndRaycastTarget()
        {
            var pickup = MakePickup("button", 2);
            yield return null;
            Assert.IsNotEmpty(pickup.GetComponentsInChildren<Renderer>());
            var collider = pickup.GetComponent<BoxCollider>();
            Assert.IsNotNull(collider);
            Assert.IsTrue(collider.isTrigger);
            Physics.SyncTransforms();
            Assert.IsTrue(collider.Raycast(new Ray(collider.bounds.center + Vector3.up, Vector3.down), out _, 2f));
        }
    }
}
