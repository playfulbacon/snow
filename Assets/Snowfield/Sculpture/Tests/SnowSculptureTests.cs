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
