using System.Collections;
using System.Collections.Generic;
using NUnit.Framework;
using Snowfield.Config;
using Snowfield.Sculpture;
using UnityEngine;
using UnityEngine.TestTools;

namespace Snowfield.Player.Tests
{
    /// <summary>
    /// A strand belongs to one sculpture. The points are stored in that sculpture's local space and the shape is
    /// solved against that one density grid, so a point dropped on the snowman next door has nowhere to live and
    /// nothing under the span that reaches it.
    /// </summary>
    public class ChainConfinementTests
    {
        SculptFeelConfig _cfg;
        Material _mat;
        readonly List<GameObject> _spawned = new List<GameObject>();
        SnowSculpture _a, _b;
        AccessoryPlacer _placer;

        [UnitySetUp]
        public IEnumerator SetUp()
        {
            _cfg = ScriptableObject.CreateInstance<SculptFeelConfig>();
            _cfg.gridSize = 48; _cfg.voxelSize = 0.04f;
            _mat = new Material(Shader.Find("Universal Render Pipeline/Lit"));

            _a = Ball("ChainTestA", Vector3.zero);
            _b = Ball("ChainTestB", new Vector3(4f, 0f, 0f)); // well clear of A, grids included
            yield return null;

            // Inactive, so Start never fires: the placer renders the HUD thumbnails there and this test has no
            // use for them. Every method under test is called directly.
            var go = new GameObject("ChainTestPlacer");
            _placer = go.AddComponent<AccessoryPlacer>();
            go.SetActive(false); // Awake wires the inventory; Start still never renders thumbnails.
            _spawned.Add(go);
            _placer.Inventory.Add("scarf", 1);
            _placer.Select(_placer.Inventory.FindFirstSlot("scarf"));
            Assert.IsNotNull(_placer.Selected.Drape, "the scarf should be a chained accessory");
        }

        [TearDown]
        public void TearDown()
        {
            foreach (var go in _spawned) if (go != null) Object.Destroy(go);
            _spawned.Clear();
            Object.Destroy(_cfg);
            Object.Destroy(_mat);
            DrapeCurve.GroundSampler = null;
        }

        SnowSculpture Ball(string name, Vector3 at)
        {
            var go = new GameObject(name);
            go.transform.position = at;
            go.SetActive(false);                      // configure before Awake
            var s = go.AddComponent<SnowSculpture>();
            s.EditorAssign(_cfg, _mat);
            go.SetActive(true);
            _spawned.Add(go);
            if (s.Grid == null) s.Initialise(_cfg.gridSize, _cfg.voxelSize);
            s.StampSphere(CentreOf(s), 0.5f, 0.7f);
            s.Remesh();
            return s;
        }

        static Vector3 CentreOf(SnowSculpture s)
        {
            float e = s.Info.WorldExtent;
            return s.transform.position + new Vector3(e * 0.5f, e * 0.5f, e * 0.5f);
        }

        /// <summary>A point on the ball's surface, found the way the player's raycast would find it.</summary>
        static Vector3 SurfaceOf(SnowSculpture s, Vector3 dir)
        {
            dir.Normalize();
            Vector3 centre = CentreOf(s);
            Assert.IsTrue(SurfaceProbe.Snap(s, centre + dir * 0.9f, dir, 1.5f, out var hit),
                          "the probe should find " + s.name + " along " + dir);
            return hit;
        }

        [Test]
        public void Chain_BelongsToTheSculptureItStartedOn()
        {
            _placer.BeginChain(_a, SurfaceOf(_a, Vector3.left));

            Assert.IsNotNull(_placer.ActiveChain, "the first click should start a chain");
            Assert.AreSame(_a, _placer.ActiveChain.Sculpture);
            Assert.IsTrue(_placer.CanExtendOnto(_a));
            Assert.IsFalse(_placer.CanExtendOnto(_b), "the chain does not belong to the other sculpture");
            Assert.IsFalse(_placer.CanExtendOnto(null), "bare ground is not a sculpture");
        }

        [Test]
        public void AppendPoint_OnAnotherSculpture_IsRefused()
        {
            _placer.BeginChain(_a, SurfaceOf(_a, Vector3.left));
            Assert.AreEqual(1, _placer.ActiveChain.PointCount);

            _placer.AppendPoint(_b, SurfaceOf(_b, Vector3.left));
            Assert.AreEqual(1, _placer.ActiveChain.PointCount, "a click on the other sculpture should add nothing");

            _placer.AppendPoint(null, CentreOf(_a) + Vector3.down * 2f);
            Assert.AreEqual(1, _placer.ActiveChain.PointCount, "a click on no sculpture at all should add nothing");

            _placer.AppendPoint(_a, SurfaceOf(_a, Vector3.forward));
            Assert.AreEqual(2, _placer.ActiveChain.PointCount, "a click on its own sculpture should still land");
        }

        [Test]
        public void EveryPoint_StaysInsideTheOwnersGrid()
        {
            _placer.BeginChain(_a, SurfaceOf(_a, Vector3.left));
            _placer.AppendPoint(_a, SurfaceOf(_a, Vector3.forward));
            _placer.AppendPoint(_b, SurfaceOf(_b, Vector3.back));   // refused
            _placer.AppendPoint(_a, SurfaceOf(_a, Vector3.right));

            var chain = _placer.ActiveChain;
            Assert.AreEqual(3, chain.PointCount);
            Bounds grid = _a.WorldBounds;
            foreach (var local in chain.Points)
            {
                Vector3 world = _a.transform.TransformPoint(local);
                Assert.IsTrue(grid.Contains(world), "point " + world + " is outside the owner's grid " + grid);
            }
        }

        [UnityTest]
        public IEnumerator LiveStrand_DoesNotReachForAnotherSculpture()
        {
            _placer.BeginChain(_a, SurfaceOf(_a, Vector3.left));
            _placer.AppendPoint(_a, SurfaceOf(_a, Vector3.forward));

            // Aim at the far sculpture: the strand should reel back to its last point rather than stretch across.
            _placer.UpdateChainTip(_b, SurfaceOf(_b, Vector3.left));
            yield return null;
            _placer.UpdateChainTip(_b, SurfaceOf(_b, Vector3.left));

            Assert.IsFalse(StrandBounds(_placer.ActiveChain).Intersects(_b.WorldBounds),
                           "the live strand reached onto the sculpture the cursor was over");

            // Aim back at its own sculpture and the live end works as before.
            Vector3 own = SurfaceOf(_a, Vector3.right);
            _placer.UpdateChainTip(_a, own);
            yield return null;
            _placer.UpdateChainTip(_a, own);
            Assert.IsTrue(StrandBounds(_placer.ActiveChain).Contains(own)
                          || Vector3.Distance(StrandBounds(_placer.ActiveChain).ClosestPoint(own), own) < 0.1f,
                          "the live end should still follow a cursor on its own sculpture");
        }

        static Bounds StrandBounds(DrapedAccessory chain)
        {
            var renderers = chain.GetComponentsInChildren<Renderer>();
            Assert.Greater(renderers.Length, 0, "the strand should have geometry to measure");
            var b = renderers[0].bounds;
            for (int i = 1; i < renderers.Length; i++) b.Encapsulate(renderers[i].bounds);
            return b;
        }
    }
}
