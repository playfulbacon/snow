using System.Collections;
using NUnit.Framework;
using Snowfield.Config;
using Unity.Mathematics;
using UnityEngine;
using UnityEngine.TestTools;

namespace Snowfield.Sculpture.Tests
{
    /// <summary>PlayMode: a scripted brush stroke on a real SnowSculpture grows the mesh and updates colliders.</summary>
    public class SnowSculptureTests
    {
        GameObject _go;
        SnowSculpture _s;
        SculptFeelConfig _cfg;

        [UnitySetUp]
        public IEnumerator SetUp()
        {
            _cfg = ScriptableObject.CreateInstance<SculptFeelConfig>();
            _cfg.gridSize = 48; _cfg.voxelSize = 0.04f;
            _go = new GameObject("TestSculpture");
            _go.SetActive(false);
            _s = _go.AddComponent<SnowSculpture>();
            _s.EditorAssign(_cfg, new Material(Shader.Find("Universal Render Pipeline/Lit")));
            _go.SetActive(true); // Awake → Initialise
            yield return null;
        }

        [TearDown]
        public void TearDown()
        {
            Object.Destroy(_go);
            Object.Destroy(_cfg);
        }

        int TotalVertices()
        {
            int n = 0;
            foreach (var mf in _go.GetComponentsInChildren<MeshFilter>()) n += mf.sharedMesh.vertexCount;
            return n;
        }

        [UnityTest]
        public IEnumerator HeldAddBrush_AccumulatesIntoVisibleSnow()
        {
            float extent = _s.Info.WorldExtent;
            float3 centre = new float3(extent * 0.5f, extent * 0.5f, extent * 0.5f);

            _s.Remesh();
            Assert.AreEqual(0, TotalVertices(), "empty grid should have no mesh");

            // One tick at rate 10 cannot cross iso 128 → still nothing visible.
            _s.ApplyAdd(centre, 0.25f, 10f, 0.6f);
            _s.Remesh();
            Assert.AreEqual(0, TotalVertices(), "a single tick should not yet cross the iso-surface");

            // Hold for 20 more ticks → core reaches 210 → surface appears. This is the accumulation feel.
            for (int i = 0; i < 20; i++) _s.ApplyAdd(centre, 0.25f, 10f, 0.6f);
            _s.Remesh();
            int afterHold = TotalVertices();
            Assert.Greater(afterHold, 0, "held brush should produce geometry");

            _s.RebuildColliders();
            yield return new WaitForFixedUpdate();
            Assert.IsTrue(Physics.Raycast(new Vector3(centre.x, extent + 1f, centre.z), Vector3.down, out var hit, extent + 2f), "collider should be hit from above");
            Assert.Less(math.abs(hit.point.y - (centre.y + 0.25f * 0.6f)), 0.15f, "surface should be near the brush core radius");

            // Only chunks around the brush should have been dirtied/remeshed.
            int nonEmptyChunks = 0;
            foreach (var mf in _go.GetComponentsInChildren<MeshFilter>()) if (mf.sharedMesh.vertexCount > 0) nonEmptyChunks++;
            Assert.LessOrEqual(nonEmptyChunks, 8, "a 0.25 m blob should touch at most the 8 chunks around the centre");
        }

        [UnityTest]
        public IEnumerator Absorb_CopiesSnowInWorldSpace_AndLeavesSourceIntact()
        {
            float extent = _s.Info.WorldExtent;
            // Source: a small offset grid (like a snowball) centred 0.5 m to the side, rotated to prove world-space sampling.
            var srcGo = new GameObject("Source");
            srcGo.SetActive(false);
            var src = srcGo.AddComponent<SnowSculpture>();
            src.EditorAssign(_cfg, new Material(Shader.Find("Universal Render Pipeline/Lit")));
            src.gridSizeOverride = 32;
            float srcExtent = 32 * _cfg.voxelSize;
            src.gridOffset = Vector3.one * (-srcExtent * 0.5f);
            srcGo.transform.position = new Vector3(extent * 0.5f + 0.3f, extent * 0.5f, extent * 0.5f);
            srcGo.transform.rotation = Quaternion.Euler(0f, 37f, 0f);
            srcGo.SetActive(true);
            src.StampSphere(srcGo.transform.position, 0.3f, 0.7f);
            src.Remesh();
            int srcVerts = 0;
            foreach (var mf in srcGo.GetComponentsInChildren<MeshFilter>()) srcVerts += mf.sharedMesh.vertexCount;
            Assert.Greater(srcVerts, 0);

            _s.Absorb(src);
            _s.Remesh();
            yield return null;

            Assert.Greater(TotalVertices(), 0, "target should now contain the sphere");
            // Density at the source centre should be solid in the target, and nothing far away.
            Assert.Greater(_s.SampleDensityWorld(srcGo.transform.position), 200f);
            Assert.AreEqual(0f, _s.SampleDensityWorld(new Vector3(0.2f, 0.2f, 0.2f)), 1e-3f);
            // Source untouched.
            Assert.Greater(src.SampleDensityWorld(srcGo.transform.position), 200f);
            Object.Destroy(srcGo);
        }

        [UnityTest]
        public IEnumerator Regrow_PreservesSnow_AndCoversRequestedBounds()
        {
            var factoryGo = new GameObject("Factory");
            factoryGo.SetActive(false);
            var factory = factoryGo.AddComponent<SculptureFactory>();
            factory.config = _cfg;
            factory.snowMaterial = new Material(Shader.Find("Universal Render Pipeline/Lit"));
            _cfg.maxGridSize = 96;
            factoryGo.SetActive(true);
            yield return null; // Awake → Instance

            var s = factory.CreateAt(new Vector3(10f, 0f, 10f));
            float extent = s.Info.WorldExtent;
            // Snow near the +X wall.
            Vector3 centre = new Vector3(10f + extent * 0.5f - 0.25f, 0.4f, 10f);
            s.StampSphere(centre, 0.3f, 0.7f);
            Assert.Greater(s.SampleDensityWorld(centre), 200f);
            Assert.IsFalse(s.ContainsWorldSphere(centre + Vector3.right * 0.4f, 0.3f, 2f));

            var needed = new Bounds(centre + Vector3.right * 0.6f, Vector3.one * 0.7f);
            var grown = factory.Regrow(s, needed);
            yield return null;

            Assert.AreNotEqual(s, grown, "should have produced a new sculpture");
            Assert.Greater(grown.Info.size, 48, "grid should be larger");
            Assert.Greater(grown.SampleDensityWorld(centre), 200f, "snow must survive the regrow at the same world position");
            Assert.IsTrue(grown.WorldBounds.Contains(needed.min) && grown.WorldBounds.Contains(needed.max), "requested bounds covered");
            Object.Destroy(grown.gameObject);
            Object.Destroy(factoryGo);
        }

        [UnityTest]
        public IEnumerator SmoothBrush_ReducesVertexCountOnNoisyBlob()
        {
            float extent = _s.Info.WorldExtent;
            float3 centre = new float3(extent * 0.5f, extent * 0.5f, extent * 0.5f);
            _s.StampSphere(centre, 0.5f, 0.7f);
            // Roughen with small random dabs.
            var rng = new Unity.Mathematics.Random(1234);
            for (int i = 0; i < 40; i++)
                _s.ApplyAdd(centre + rng.NextFloat3Direction() * 0.5f, 0.06f, 200f, 0.2f);
            _s.Remesh();
            int rough = TotalVertices();

            for (int i = 0; i < 30; i++) _s.ApplySmooth(centre, 0.8f, 0.5f, 0.5f);
            _s.Remesh();
            int smooth = TotalVertices();
            yield return null;
            Assert.Less(smooth, rough, "smoothing should simplify the surface");
        }
    }
}
