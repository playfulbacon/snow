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
    /// <summary>PlayMode: compaction, shave, squeeze and breakage on real sculptures through the factory.</summary>
    public class StructureTests
    {
        SculptFeelConfig _cfg;
        GameObject _factoryGo;
        SculptureFactory _factory;

        [UnitySetUp]
        public IEnumerator SetUp()
        {
            _cfg = ScriptableObject.CreateInstance<SculptFeelConfig>();
            _cfg.gridSize = 48; _cfg.voxelSize = 0.03f; _cfg.snowballGridSize = 32; _cfg.maxGridSize = 96;
            _factoryGo = new GameObject("Factory");
            _factoryGo.SetActive(false);
            _factory = _factoryGo.AddComponent<SculptureFactory>();
            _factory.config = _cfg;
            _factory.snowMaterial = new Material(Shader.Find("Universal Render Pipeline/Lit"));
            _factoryGo.SetActive(true);
            yield return null;
        }

        [UnityTearDown]
        public IEnumerator TearDown()
        {
            foreach (var s in Object.FindObjectsByType<SnowSculpture>(FindObjectsSortMode.None))
                Object.DestroyImmediate(s.gameObject);
            Object.Destroy(_factoryGo);
            Object.Destroy(_cfg);
            yield return null;
        }

        [UnityTest]
        public IEnumerator Provenance_ScoopedIsFluffy_RolledIsPacked_FuseWelds()
        {
            var handful = _factory.CreateSnowball(new Vector3(0f, 0.2f, 0f), 0.12f, _cfg.compactionScooped);
            var rolled = _factory.CreateSnowball(new Vector3(0f, 0.2f, 2f), 0.2f);
            Assert.AreEqual(_cfg.compactionScooped, handful.Sculpture.MeanCompaction(), 1f);
            Assert.AreEqual(_cfg.compactionRolled, rolled.Sculpture.MeanCompaction(), 1f);

            // Fuse the handful into the rolled ball: the contact shell is raised to at least the weld value.
            Vector3 rolledPos = rolled.transform.position;
            handful.transform.position = rolledPos + new Vector3(0f, 0.2f + 0.12f * 0.55f, 0f);
            var result = _factory.Fuse(rolled.Sculpture, handful.Sculpture);
            yield return null;
            Vector3 seam = rolledPos + new Vector3(0f, 0.17f, 0f); // where both bodies are solid
            Assert.GreaterOrEqual(result.SampleCompactionWorld(seam), _cfg.compactionWeld - 30f, "weld shell is firm");
        }

        [UnityTest]
        public IEnumerator Squeeze_ShrinksVolume_AndPacks()
        {
            var ball = _factory.CreateSnowball(new Vector3(0f, 0.3f, 0f), 0.2f, _cfg.compactionScooped);
            float before = ball.Sculpture.DensityVolume();
            float linear = Mathf.Pow(1f - _cfg.squeezeShrink, 1f / 3f);
            ball.Sculpture.Squeeze(linear, 1f);
            ball.Sculpture.Remesh();
            yield return null;
            float after = ball.Sculpture.DensityVolume();
            Assert.AreEqual(before * (1f - _cfg.squeezeShrink), after, before * 0.06f, "~1/3 of the volume is gone");
            Assert.GreaterOrEqual(ball.Sculpture.MeanCompaction(), 250f, "fully packed");
            Assert.Greater(ball.Sculpture.SampleDensityWorld(ball.Centre), 200f, "still a solid ball");
        }

        [UnityTest]
        public IEnumerator Shave_TakesALayer_AndReportsWhatItRemoved()
        {
            var s = _factory.CreateMound(new Vector3(5f, 0f, 5f), 0.5f);
            float before = s.DensityVolume();
            // The hit point a raycast would give: the iso-surface on the mound's centre line.
            float surfaceY = 0.5f;
            while (surfaceY > 0f && s.SampleDensityWorld(new Vector3(5f, surfaceY, 5f)) < 128f) surfaceY -= 0.005f;
            Vector3 top = new Vector3(5f, surfaceY, 5f);
            float packBefore = s.CompactionUnderSurface(top, Vector3.up);
            Assert.AreEqual(_cfg.compactionLegacy, packBefore, 2f);
            var prm = SculptureShave.ParamsFor(_cfg, _cfg.voxelSize, 1f, false, top);
            float removed = s.ApplyShave(top, Vector3.up, 0.15f, prm, out var min, out var max);
            s.Remesh();
            yield return null;
            Assert.Greater(removed, 0f);
            Assert.IsTrue(math.all(max > min));
            Assert.AreEqual(before - removed, s.DensityVolume(), before * 0.005f, "the report matches the grid");
            Assert.Less(s.SampleDensityWorld(top - Vector3.up * 0.02f), 128f, "the top skin is gone");
            Assert.Greater(s.SampleDensityWorld(top - Vector3.up * 0.15f), 200f, "the body under it is not");
        }

        [UnityTest]
        public IEnumerator Check_DetachesFloatingSnow_AsABall_ConservingMass()
        {
            var s = _factory.CreateMound(new Vector3(10f, 0f, 10f), 0.4f);
            Vector3 floating = new Vector3(10.4f, 1.0f, 10f);
            s.StampSphere(floating, 0.2f, 0.7f, 220f, float.NegativeInfinity);
            s.Remesh();
            float total = s.DensityVolume();
            Assert.Greater(s.SampleDensityWorld(floating), 200f);

            var result = SculptureStructure.Check(s, int3.zero, new int3(s.Info.size));
            yield return null;
            Assert.AreEqual(1, result.Detached.Count, "the floating lump is an island");
            var ball = result.Detached[0];
            Assert.AreEqual(0f, s.SampleDensityWorld(floating), 1e-3f, "it left the sculpture");
            Assert.Greater(ball.Sculpture.SampleDensityWorld(floating), 200f, "and is in the ball at the same world position");
            Assert.AreEqual(220f, ball.Sculpture.MeanCompaction(), 3f, "with its compaction");
            Assert.AreEqual(total, s.DensityVolume() + ball.Sculpture.DensityVolume() + result.CrumbVolume, total * 0.01f, "nothing created or lost");
            Assert.IsTrue(ball.IsFlying, "it drops");
        }

        [UnityTest]
        public IEnumerator Check_FluffyThinNeckBreaks_PackedHolds()
        {
            // A ball on a thin neck on a mound. Fluffy: the neck is under the limit and the head comes off.
            foreach (bool packed in new[] { false, true })
            {
                var s = _factory.CreateMound(new Vector3(20f, 0f, 20f), 0.4f);
                float c = packed ? 255f : 0f;
                s.FillCompaction((int)c);
                // neck: a 3x3-voxel (9 cm) column of solid voxels from inside the mound top up to the head
                int3 v = (int3)math.round(s.WorldToVoxel(new float3(20f, 0.4f, 20f)));
                for (int y = v.y - 2; y < v.y + 6; y++)
                for (int dz = -1; dz <= 1; dz++)
                for (int dx = -1; dx <= 1; dx++)
                {
                    int idx = s.Info.Index(new int3(v.x + dx, y, v.z + dz));
                    s.Grid.Density[idx] = 255; s.Grid.Compaction[idx] = (byte)c;
                }
                float neckTop = (v.y + 5) * _cfg.voxelSize; // world y of the top neck voxel (sculpture origin y = 0)
                Vector3 head = new Vector3(20f, neckTop + 0.11f, 20f); // overlaps the neck top
                s.StampSphere(head, 0.16f, 0.7f, c, float.NegativeInfinity);
                s.TouchAll();
                s.Remesh();
                var result = SculptureStructure.Check(s, int3.zero, new int3(s.Info.size));
                yield return null;
                if (packed)
                    Assert.AreEqual(0, result.Detached.Count, "packed: a 9 cm neck holds (limit 5 cm)");
                else
                    Assert.AreEqual(1, result.Detached.Count, "fluffy: the neck crumbles and the head thuds off");
                Object.DestroyImmediate(s.gameObject);
                foreach (var b in result.Detached) if (b != null) Object.DestroyImmediate(b.gameObject);
            }
        }

        [UnityTest]
        public IEnumerator Check_TwigArmature_HoldsAFluffyNeck()
        {
            var s = _factory.CreateMound(new Vector3(30f, 0f, 30f), 0.4f);
            s.FillCompaction(0);
            // The twig: stuck in the mound top, pointing up. Beads of fluffy snow along it.
            var entry = AccessoryCatalog.Find("twig");
            var go = entry.Build();
            var prop = go.AddComponent<SculptureProp>();
            prop.Attach(s, "twig", new Vector3(30f, 0.4f - entry.Sink, 30f), Quaternion.identity);
            for (int i = 0; i < 4; i++)
                s.StampSphere(new Vector3(30f, 0.4f + 0.09f * i, 30f), 0.06f, 0.7f, 0f, float.NegativeInfinity);
            s.Remesh();
            float total = s.DensityVolume();
            var result = SculptureStructure.Check(s, int3.zero, new int3(s.Info.size));
            yield return null;
            Assert.AreEqual(0, result.Detached.Count, "held by the twig");
            Assert.AreEqual(total, s.DensityVolume(), total * 0.05f, "and nothing worth mentioning crumbled off it");
        }
    }
}
