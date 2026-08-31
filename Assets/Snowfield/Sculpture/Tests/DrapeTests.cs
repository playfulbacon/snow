using System.Collections;
using System.Collections.Generic;
using NUnit.Framework;
using Snowfield.Config;
using Snowfield.Voxel;
using Unity.Mathematics;
using UnityEngine;
using UnityEngine.TestTools;

namespace Snowfield.Sculpture.Tests
{
    /// <summary>The drape is re-derived, never stored — so it has to be deterministic and it has to stay out of the snow.</summary>
    public class DrapeTests
    {
        GameObject _go;
        SnowSculpture _s;
        SculptFeelConfig _cfg;
        Material _mat;

        [UnitySetUp]
        public IEnumerator SetUp()
        {
            _cfg = ScriptableObject.CreateInstance<SculptFeelConfig>();
            _cfg.gridSize = 48; _cfg.voxelSize = 0.04f;
            _mat = new Material(Shader.Find("Universal Render Pipeline/Lit"));
            _go = new GameObject("DrapeTestSculpture");
            _go.SetActive(false);
            _s = _go.AddComponent<SnowSculpture>();
            _s.EditorAssign(_cfg, _mat);
            _go.SetActive(true); // Awake -> Initialise
            yield return null;

            // A ball of snow in the middle for the strand to lie on.
            float extent = _s.Info.WorldExtent;
            _s.StampSphere(new float3(extent * 0.5f, extent * 0.5f, extent * 0.5f), 0.5f, 0.7f);
            _s.Remesh();
        }

        [TearDown]
        public void TearDown()
        {
            if (_go != null) Object.Destroy(_go);
            Object.Destroy(_cfg);
            Object.Destroy(_mat);
            DrapeRope.GroundSampler = null;
        }

        List<Vector3> AnchorsAcrossTheBall()
        {
            float e = _s.Info.WorldExtent;
            Vector3 c = _go.transform.position + new Vector3(e * 0.5f, e * 0.5f, e * 0.5f);
            return new List<Vector3>
            {
                c + new Vector3(-0.55f, 0.15f, 0f),
                c + new Vector3(0f, 0.15f, 0.55f),
                c + new Vector3(0.55f, 0.15f, 0f),
            };
        }

        [Test]
        public void Solve_IsDeterministic_AndLeavesTheStrandOutsideTheSnow()
        {
            var anchors = AnchorsAcrossTheBall();
            var surfaces = new List<SnowSculpture> { _s };
            var style = AccessoryCatalog.LightsStyle;

            var a = new DrapeRope();
            a.Solve(anchors, style, _cfg, surfaces);

            var b = new DrapeRope();
            b.Solve(anchors, style, _cfg, surfaces);

            Assert.Greater(a.Count, 2, "the strand should have particles between its anchors");
            Assert.AreEqual(a.Count, b.Count, "same input, same particle count");
            for (int i = 0; i < a.Count; i++)
                Assert.AreEqual(a.Points[i], b.Points[i], $"particle {i} differs between two identical solves");

            // Every particle must end up on or outside the surface, or the strand would be buried in the snowman.
            for (int i = 0; i < a.Count; i++)
                Assert.LessOrEqual(_s.SampleDensityWorld(a.Points[i]), VoxelGridInfo.Iso + 1f,
                    $"particle {i} ended up inside the snow");
        }

        [Test]
        public void PushOutOf_LiftsAPointBuriedInSnowToTheSurface()
        {
            float e = _s.Info.WorldExtent;
            Vector3 centre = _go.transform.position + new Vector3(e * 0.5f, e * 0.5f, e * 0.5f);
            Vector3 buried = centre + new Vector3(0.1f, 0f, 0f); // well inside the stamped ball
            Assert.Greater(_s.SampleDensityWorld(buried), VoxelGridInfo.Iso, "test point should start buried");

            Vector3 p = buried;
            for (int i = 0; i < 8; i++) DrapeRope.PushOutOf(_s, ref p, 0.01f);
            Assert.LessOrEqual(_s.SampleDensityWorld(p), VoxelGridInfo.Iso + 1f, "point should have been pushed out of the snow");
        }

        [UnityTest]
        public IEnumerator Create_RegistersOneProp_AndBuildsGeometry()
        {
            var anchors = AnchorsAcrossTheBall();
            var local = new List<Vector3>();
            foreach (var w in anchors) local.Add(_go.transform.InverseTransformPoint(w));

            var entry = AccessoryCatalog.Find("lights");
            Assert.IsNotNull(entry, "the lights entry should be in the catalog");
            Assert.IsNotNull(entry.Drape, "lights should be a chained accessory");

            var chain = DrapedAccessory.Create(_s, entry, local, frozen: true);
            yield return null;

            Assert.IsNotNull(chain);
            Assert.AreEqual(3, chain.PointCount);
            Assert.IsTrue(chain.Frozen);
            bool registered = false;
            foreach (var p in _s.Props) if (p == chain) registered = true;
            Assert.IsTrue(registered, "the chain should register as one prop");

            int verts = 0;
            foreach (var mf in chain.GetComponentsInChildren<MeshFilter>())
                if (mf.sharedMesh != null) verts += mf.sharedMesh.vertexCount;
            Assert.Greater(verts, 0, "the strand should have generated geometry");

            // Points survive a save/load style round trip through the record shape.
            var record = SaveLoadManager.ToRecord(_s);
            Assert.AreEqual(1, record.props.Count);
            Assert.AreEqual(3, record.props[0].points.Count);
        }
    }
}
