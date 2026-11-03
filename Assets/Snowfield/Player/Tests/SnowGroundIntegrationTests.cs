using System.Collections;
using NUnit.Framework;
using Snowfield.Player;
using Snowfield.Sculpture;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.TestTools;

namespace Snowfield.Player.Tests
{
    /// <summary>
    /// End-to-end check of the SnowGround seam in the real Main scene: the SnowDeform adapter registers itself,
    /// the deformation window fills, a StampDepression lands in the surface (freshness and height respond), and
    /// a ground scoop puts a snowball in hand. Loads the full town scene, so it is the slowest test here.
    /// </summary>
    public class SnowGroundIntegrationTests
    {
        /// <summary>The town scene must not leak into later tests (their surface raycasts would hit its terrain).</summary>
        [UnityTearDown]
        public IEnumerator UnloadMain()
        {
            var main = SceneManager.GetSceneByName("Main");
            if (main.IsValid() && main.isLoaded)
            {
                var empty = SceneManager.CreateScene("SnowGroundTestTeardown");
                SceneManager.SetActiveScene(empty);
                yield return SceneManager.UnloadSceneAsync(main);
            }
        }

        [UnityTest]
        public IEnumerator MainScene_ScoopAndStamp_AffectSurfaceSnow()
        {
            SceneManager.LoadScene("Main");
            yield return null;

            // The adapter bootstraps on scene load; the deform window fills once the system finds the player
            // (1 s search cadence + a time-sliced 8-frame fill).
            float deadline = Time.realtimeSinceStartup + 30f;
            while ((SnowGround.Instance == null || !SnowGround.Instance.IsCreated)
                   && Time.realtimeSinceStartup < deadline)
                yield return null;
            Assert.IsNotNull(SnowGround.Instance, "SnowDeformGroundAdapter never registered as SnowGround.Instance");
            Assert.IsTrue(SnowGround.Instance.IsCreated, "SnowDeform window never filled");

            var roller = Object.FindAnyObjectByType<SnowballRoller>();
            Assert.IsNotNull(roller, "SnowballRoller missing from Main scene");
            var placer = roller.GetComponent<AccessoryPlacer>();
            Assert.IsNotNull(placer.Inventory, "Older scene wiring must acquire an inventory automatically");
            foreach (var entry in AccessoryCatalog.Entries)
                Assert.AreEqual(0, placer.Inventory.Count(entry.Id), "Inventory must start empty");

            // Existing scenes need no hand-editing to make the collection loop playable.
            ItemPickup[] pickups;
            deadline = Time.realtimeSinceStartup + 8f;
            do
            {
                yield return null;
                // Levels may also contain authored pickup prefabs; count only the automatic supplies.
                pickups = System.Array.FindAll(Object.FindObjectsByType<ItemPickup>(FindObjectsSortMode.None),
                    p => p.transform.parent != null && p.transform.parent.name == "AccessorySupplies");
            }
            while (pickups.Length < AccessoryCatalog.Entries.Count && Time.realtimeSinceStartup < deadline);
            Assert.AreEqual(AccessoryCatalog.Entries.Count, pickups.Length, "Spawn one finite supply stack per item");
            foreach (var pickup in pickups)
            {
                Assert.IsNotNull(pickup.GetComponent<Collider>(), "World supplies must be targetable");
                Assert.IsNotEmpty(pickup.GetComponentsInChildren<Renderer>(), "World supplies must be visible");
                int quantity = pickup.quantity;
                Assert.AreEqual(quantity, pickup.Collect(placer.Inventory));
                Assert.AreEqual(quantity, placer.Inventory.Count(pickup.itemId));
                Assert.AreEqual(0, pickup.Collect(placer.Inventory), "Depleted supplies cannot refill a slot");
                // The catalog can contain more item types than the inventory has slots.
                for (int i = 0; i < quantity; i++) Assert.IsTrue(placer.Inventory.TryConsume(pickup.itemId));
            }

            Vector3 player = roller.character != null ? roller.character.position : Vector3.zero;
            Vector3 p = player + new Vector3(1.5f, 0f, 1.5f);

            var ground = SnowGround.Instance;
            float before = ground.SampleHeight(p);
            Assert.IsTrue(ground.IsFreshAt(p, 0.02f), "Untouched snow should read fresh");

            // A full-depth stamp tramples to the compressed floor — the only level the (deliberately
            // generous) freshness gate treats as no-longer-fresh.
            ground.StampDepression(p, 0.35f, 1f, 0.6f);
            yield return null; // stamps drain in SnowDeformSystem.LateUpdate the same frame; settle one more
            yield return null;

            float after = ground.SampleHeight(p);
            Assert.Less(after, before - 0.03f,
                $"StampDepression should lower the surface (before {before:F3}, after {after:F3})");
            Assert.IsFalse(ground.IsFreshAt(p, 0.02f), "Fully trampled snow should no longer read fresh");

            // Scoop a handful off bare ground: snow ends up in hand, divot in the field. Divots are shallow
            // by design (balls must keep growing over them), so assert the height drop, not freshness.
            Vector3 scoopAt = player + new Vector3(-1.5f, 0f, 1.5f);
            float scoopBefore = ground.SampleHeight(scoopAt);
            scoopAt.y = scoopBefore;
            Assert.IsFalse(roller.IsCarrying);
            roller.ScoopFrom(scoopAt);
            Assert.IsTrue(roller.IsCarrying, "ScoopFrom should put a snowball in hand");
            Assert.IsTrue(roller.IsCarryingBall);
            yield return null;
            yield return null;
            float scoopAfter = ground.SampleHeight(scoopAt);
            Assert.Less(scoopAfter, scoopBefore - 0.015f,
                $"Scooping should leave a divot in the surface snow (before {scoopBefore:F3}, after {scoopAfter:F3})");
        }
    }
}
