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
            _s.StampSphere(Centre, 0.5f, 0.7f);
            _s.Remesh();
        }

        [TearDown]
        public void TearDown()
        {
            if (_go != null) Object.Destroy(_go);
            Object.Destroy(_cfg);
            Object.Destroy(_mat);
            DrapeCurve.GroundSampler = null;
        }

        /// <summary>Centre of the stamped ball, which is also the centre of the grid.</summary>
        Vector3 Centre
        {
            get
            {
                float e = _s.Info.WorldExtent;
                return _go.transform.position + new Vector3(e * 0.5f, e * 0.5f, e * 0.5f);
            }
        }

        /// <summary>Where the ball's surface sits along a direction from its centre, measured by the probe.</summary>
        float IsoRadius(Vector3 dir)
        {
            dir.Normalize();
            Assert.IsTrue(SurfaceProbe.Snap(_s, Centre + dir * 0.9f, dir, 1f, out var hit),
                          "the probe should find the ball along " + dir);
            return Vector3.Distance(hit, Centre);
        }

        static Vector3 OnCircle(float degrees) =>
            new Vector3(Mathf.Cos(degrees * Mathf.Deg2Rad), 0f, Mathf.Sin(degrees * Mathf.Deg2Rad));

        List<Vector3> AnchorsAcrossTheBall()
        {
            Vector3 c = Centre;
            return new List<Vector3>
            {
                c + new Vector3(-0.55f, 0.15f, 0f),
                c + new Vector3(0f, 0.15f, 0.55f),
                c + new Vector3(0.55f, 0.15f, 0f),
            };
        }

        /// <summary>Three clicks on the ball itself, a quarter turn apart — how a scarf actually gets placed.</summary>
        List<Vector3> AnchorsOnTheBall()
        {
            var anchors = new List<Vector3>();
            foreach (float a in new[] { 180f, 90f, 0f })
            {
                Vector3 dir = OnCircle(a);
                anchors.Add(Centre + dir * IsoRadius(dir));
            }
            return anchors;
        }

        [Test]
        public void Snap_LandsOnTheIsoSurface_FromEitherSide()
        {
            // A stamp's shoulder softens its edge, so the iso surface of a 0.5 m stamp sits a little inside 0.5 m.
            // What matters is that the probe reads the same ball whichever way it looks at it.
            float reference = IsoRadius(Vector3.right);
            Assert.That(reference, Is.GreaterThan(0.3f).And.LessThan(0.5f), "a 0.5 m stamp should read as a ball of about that size");

            foreach (float a in new[] { 0f, 55f, 123f, 270f })
            {
                Vector3 dir = OnCircle(a);
                float r = IsoRadius(dir);
                Assert.That(r, Is.EqualTo(reference).Within(0.01f), "the ball should measure the same in every direction");

                // Density at the found point is the iso level, to a hair - that is what "on the surface" means.
                Vector3 surface = Centre + dir * r;
                Assert.That(_s.SampleDensityWorld(surface), Is.EqualTo((float)VoxelGridInfo.Iso).Within(1f));

                // And it is the same surface found from inside, not some other crossing further along.
                Assert.IsTrue(SurfaceProbe.Snap(_s, Centre + dir * (r - 0.1f), dir, 0.3f, out var fromInside));
                Assert.That(Vector3.Distance(fromInside, surface), Is.LessThan(0.005f),
                            "inside and outside should agree on where the surface is");
            }
        }

        [Test]
        public void Curve_WrapsTheBall_InsteadOfCuttingAcrossIt()
        {
            var anchors = AnchorsOnTheBall();
            var surfaces = new List<SnowSculpture> { _s };
            var style = AccessoryCatalog.ScarfStyle;
            float radius = IsoRadius(OnCircle(45f));
            float rest = radius + _cfg.drapeSurfaceOffset + style.Radius; // the band's centreline rides its own width out

            var path = new List<Vector3>();
            var normals = new List<Vector3>();
            DrapeCurve.Build(anchors, style, _cfg, surfaces, path, normals);

            Assert.Greater(path.Count, 8, "a scarf round a 0.5 m ball needs more than a handful of points");
            Assert.AreEqual(path.Count, normals.Count, "every point should carry the surface it lies against");

            for (int i = 0; i < path.Count; i++)
            {
                float d = Vector3.Distance(path[i], Centre);
                Assert.That(d, Is.EqualTo(rest).Within(0.03f),
                            $"point {i} is {d:F3} m from the centre, not resting on the ball at {rest:F3} m");
                // The band lies flat against the snow, so its up vector is the outward radial.
                Vector3 radial = (path[i] - Centre).normalized;
                Assert.Greater(Vector3.Dot(normals[i].normalized, radial), 0.8f,
                               $"point {i}'s normal does not face away from the ball");
            }

            // Going round is longer than cutting across: that is the whole difference being tested.
            float chord = 0f, length = 0f;
            for (int i = 0; i + 1 < anchors.Count; i++) chord += Vector3.Distance(anchors[i], anchors[i + 1]);
            for (int i = 0; i + 1 < path.Count; i++) length += Vector3.Distance(path[i], path[i + 1]);
            Assert.Greater(length, chord * 1.05f, "the strand should bow around the ball, not run straight through it");
            Assert.Less(length, chord * 1.6f, "but it should still be a taut wrap, not a wandering line");
        }

        [Test]
        public void Curve_PassesThroughEveryPointThePlayerClicked()
        {
            var anchors = AnchorsOnTheBall();
            var surfaces = new List<SnowSculpture> { _s };
            var path = new List<Vector3>();
            DrapeCurve.Build(anchors, AccessoryCatalog.ScarfStyle, _cfg, surfaces, path);

            foreach (var anchor in anchors)
            {
                float nearest = float.MaxValue;
                foreach (var p in path) nearest = Mathf.Min(nearest, Vector3.Distance(p, anchor));
                Assert.Less(nearest, 0.1f, "the strand should still run through the clicked point " + anchor);
            }
        }

        [Test]
        public void Curve_IsDeterministic_AndLeavesTheStrandOutsideTheSnow()
        {
            var anchors = AnchorsAcrossTheBall();
            var surfaces = new List<SnowSculpture> { _s };
            var style = AccessoryCatalog.LightsStyle;

            var a = new List<Vector3>();
            var b = new List<Vector3>();
            DrapeCurve.Build(anchors, style, _cfg, surfaces, a);
            DrapeCurve.Build(anchors, style, _cfg, surfaces, b);

            Assert.Greater(a.Count, 2, "the strand should have points between its anchors");
            Assert.AreEqual(a.Count, b.Count, "same input, same point count");
            for (int i = 0; i < a.Count; i++)
                Assert.AreEqual(a[i], b[i], $"point {i} differs between two identical builds");

            // Every point must end up on or outside the surface, or the strand would be buried in the snowman.
            for (int i = 0; i < a.Count; i++)
                Assert.LessOrEqual(_s.SampleDensityWorld(a[i]), VoxelGridInfo.Iso + 1f,
                    $"point {i} ended up inside the snow");
        }

        [Test]
        public void Curve_MovesSmoothlyWhenAnAnchorMoves()
        {
            // The verlet rope that lived here jittered because it carried state; a shape cannot. A hair of anchor
            // movement must only ever produce a hair of strand movement.
            var anchors = AnchorsOnTheBall();
            var surfaces = new List<SnowSculpture> { _s };
            var style = AccessoryCatalog.ScarfStyle;

            var before = new List<Vector3>();
            var after = new List<Vector3>();
            DrapeCurve.Build(anchors, style, _cfg, surfaces, before);
            anchors[anchors.Count - 1] += new Vector3(0.001f, 0f, 0f);
            DrapeCurve.Build(anchors, style, _cfg, surfaces, after);

            Assert.AreEqual(before.Count, after.Count, "a 1 mm nudge should not change the point count");
            for (int i = 0; i < before.Count; i++)
                Assert.Less(Vector3.Distance(before[i], after[i]), 0.02f,
                    $"point {i} jumped for a 1 mm anchor nudge");
        }

        [Test]
        public void PushOut_LiftsAPointBuriedInSnowToTheSurface()
        {
            Vector3 buried = Centre + new Vector3(0.1f, 0f, 0f); // well inside the stamped ball
            Assert.Greater(_s.SampleDensityWorld(buried), VoxelGridInfo.Iso, "test point should start buried");

            Vector3 p = buried;
            SurfaceProbe.PushOut(_s, ref p, 0.01f);
            Assert.LessOrEqual(_s.SampleDensityWorld(p), VoxelGridInfo.Iso + 1f, "point should have been pushed out of the snow");
            Assert.That(Vector3.Distance(p, Centre), Is.EqualTo(IsoRadius(Vector3.right) + 0.01f).Within(0.02f),
                        "and pushed out the near way, not flung somewhere else");
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
