using System.Collections;
using System.Collections.Generic;
using NUnit.Framework;
using Snowfield.Config;
using Snowfield.Player;
using Snowfield.Sculpture;
using Unity.Mathematics;
using UnityEngine;
using UnityEngine.TestTools;

namespace Snowfield.Net.Tests
{
    /// <summary>
    /// End-to-end sync-layer tests with no netcode: run an op locally (as the tools do), capture the encoded
    /// event off the SculptureNet seam, wipe the world, rebuild it from snapshots (the late-join path), apply
    /// the event, and assert the density/identity outcome matches what the origin computed. This is exactly the
    /// origin → wire → remote pipeline minus the RPC hop, which RelayPatternTests covers separately.
    /// </summary>
    public class WorldSyncTests
    {
        SculptFeelConfig _cfg;
        SculptureFactory _factory;
        GameObject _factoryGo;
        Material _mat;
        SnowWorldSync _sync;
        List<byte[]> _captured;

        [UnitySetUp]
        public IEnumerator SetUp()
        {
            _cfg = ScriptableObject.CreateInstance<SculptFeelConfig>();
            _cfg.gridSize = 48;
            _cfg.voxelSize = 0.04f;
            _cfg.snowballGridSize = 32;
            _cfg.maxGridSize = 96;
            _mat = new Material(Shader.Find("Universal Render Pipeline/Lit"));
            _factoryGo = new GameObject("Factory");
            _factoryGo.SetActive(false);
            _factory = _factoryGo.AddComponent<SculptureFactory>();
            _factory.config = _cfg;
            _factory.snowMaterial = _mat;
            _factoryGo.SetActive(true);
            yield return null; // Awake → Instance

            _sync = new SnowWorldSync();
            _sync.Registry.LocalPrefix = 1;
            _sync.Attach();
            _captured = new List<byte[]>();
            _sync.Send = _captured.Add;
        }

        [UnityTearDown]
        public IEnumerator TearDown()
        {
            _sync?.Detach();
            _sync = null;
            SculptureNet.Suppress = false;
            foreach (var s in Object.FindObjectsByType<SnowSculpture>(FindObjectsSortMode.None))
                Object.DestroyImmediate(s.gameObject);
            Object.Destroy(_factoryGo);
            Object.Destroy(_cfg);
            Object.Destroy(_mat);
            yield return null;
        }

        /// <summary>Detach the origin sync and stand up a fresh one, as if this process were another peer.</summary>
        SnowWorldSync BecomeRemotePeer()
        {
            _sync.WipeWorld();
            _sync.Detach();
            _sync = new SnowWorldSync();
            _sync.Registry.LocalPrefix = 2;
            _sync.Attach();
            _sync.Send = _ => { };
            return _sync;
        }

        static float[] Probe(SnowSculpture s, Vector3 centre)
        {
            var probes = new float[7];
            probes[0] = s.SampleDensityWorld(centre);
            probes[1] = s.SampleDensityWorld(centre + new Vector3(0.1f, 0, 0));
            probes[2] = s.SampleDensityWorld(centre - new Vector3(0.1f, 0, 0));
            probes[3] = s.SampleDensityWorld(centre + new Vector3(0, 0.1f, 0));
            probes[4] = s.SampleDensityWorld(centre - new Vector3(0, 0.1f, 0));
            probes[5] = s.SampleDensityWorld(centre + new Vector3(0, 0, 0.1f));
            probes[6] = s.SampleDensityWorld(centre - new Vector3(0, 0, 0.1f));
            return probes;
        }

        static void AssertProbesEqual(float[] expected, float[] actual)
        {
            for (int i = 0; i < expected.Length; i++)
                Assert.AreEqual(expected[i], actual[i], 0.01f, $"probe {i} diverged");
        }

        [UnityTest]
        public IEnumerator Stroke_RoundTrips_Through_Snapshot_And_Event()
        {
            var s = _factory.CreateMound(new Vector3(10f, 0f, 10f), 0.5f);
            Assert.IsTrue(_sync.Registry.TryGetId(s, out ulong id));
            byte[] snapshot = SnowWorldSync.EncodeSnapshot(id, s);

            Vector3 p = new Vector3(10f, 0.25f, 10f);
            for (int i = 0; i < 3; i++)
                s.ApplySmooth(p, 0.3f, _cfg.smoothStrength, _cfg.smoothShoulder);
            SculptureNet.RaiseStroke(new SculptureNet.StrokeInfo
            { op = 3, point = p, radius = 0.3f, ticks = 3, targets = new List<SnowSculpture> { s } });
            Assert.AreEqual(1, _captured.Count, "one stroke event expected");
            float[] expected = Probe(s, p);

            var remote = BecomeRemotePeer();
            remote.ApplySnapshot(snapshot);
            Assert.IsTrue(remote.Registry.TryGet(id, out var s2), "snapshot did not restore the sculpture id");
            remote.Apply(_captured[0]);
            AssertProbesEqual(expected, Probe(s2, p));
            yield return null;
        }

        [UnityTest]
        public IEnumerator Scoop_Recreates_Chunk_With_Same_Id_And_Bite()
        {
            var s = _factory.CreateMound(new Vector3(20f, 0f, 20f), 0.6f);
            Assert.IsTrue(_sync.Registry.TryGetId(s, out ulong id));
            byte[] snapshot = SnowWorldSync.EncodeSnapshot(id, s);

            // Mirror SculptTool.ScoopChunk: extract first, then remove the same kernel (order is load-bearing).
            Vector3 bite = new Vector3(20f, 0.3f, 20f);
            float radius = 0.2f;
            var chunk = _factory.CreateEmptySnowball(bite, radius);
            chunk.Sculpture.ExtractFrom(s, bite, radius, _cfg.addShoulder);
            float volume = chunk.Sculpture.DensityVolume();
            Assert.Greater(volume, 1e-5f, "the bite should have caught snow");
            s.ApplyAdd(bite, radius, -255f, _cfg.addShoulder);
            s.Remesh();
            chunk.radius = 0.11f;
            Assert.IsTrue(_sync.Registry.TryGetId(chunk.Sculpture, out ulong chunkId));
            SculptureNet.RaiseScooped(new SculptureNet.ScoopInfo
            { point = bite, radius = radius, shoulder = _cfg.addShoulder, targets = new List<SnowSculpture> { s }, chunk = chunk, resultRadius = 0.11f });
            Assert.AreEqual(1, _captured.Count);
            float[] expectedHole = Probe(s, bite);

            var remote = BecomeRemotePeer();
            remote.ApplySnapshot(snapshot);
            remote.Apply(_captured[0]);
            Assert.IsTrue(remote.Registry.TryGet(id, out var s2));
            Assert.IsTrue(remote.Registry.TryGet(chunkId, out var chunk2), "scoop should recreate the chunk under its id");
            AssertProbesEqual(expectedHole, Probe(s2, bite));
            Assert.AreEqual(volume, chunk2.DensityVolume(), volume * 0.02f + 1e-4f, "chunk density diverged");
            var ball2 = chunk2.GetComponent<Snowball>();
            Assert.IsNotNull(ball2);
            Assert.AreEqual(0.11f, ball2.radius, 1e-4f);
            yield return null;
        }

        [UnityTest]
        public IEnumerator Fuse_Promotes_And_Migrates_Ids_Identically()
        {
            var target = _factory.CreateSnowball(new Vector3(30f, 0.2f, 30f), 0.2f);
            var source = _factory.CreateSnowball(new Vector3(30.15f, 0.35f, 30f), 0.15f);
            Assert.IsTrue(_sync.Registry.TryGetId(target.Sculpture, out ulong targetId));
            Assert.IsTrue(_sync.Registry.TryGetId(source.Sculpture, out ulong sourceId));
            byte[] snapTarget = SnowWorldSync.EncodeSnapshot(targetId, target.Sculpture);
            byte[] snapSource = SnowWorldSync.EncodeSnapshot(sourceId, source.Sculpture);

            var result = _factory.Fuse(target.Sculpture, source.Sculpture);
            Assert.AreEqual(1, _captured.Count, "fuse should broadcast exactly one event (inner promote/regrow ride along)");
            Assert.IsTrue(_sync.Registry.TryGet(targetId, out var mapped), "target id must survive the promote");
            Assert.AreEqual(result, mapped);
            Assert.IsFalse(_sync.Registry.TryGet(sourceId, out _), "source id must be retired");
            int expectedSize = result.Info.size;
            Vector3 probeAt = new Vector3(30.05f, 0.3f, 30f);
            float[] expected = Probe(result, probeAt);

            var remote = BecomeRemotePeer();
            remote.ApplySnapshot(snapTarget);
            remote.ApplySnapshot(snapSource);
            remote.Apply(_captured[0]);
            Assert.IsTrue(remote.Registry.TryGet(targetId, out var result2));
            Assert.AreEqual(expectedSize, result2.Info.size, "remote promote produced a different grid");
            Assert.IsFalse(remote.Registry.TryGet(sourceId, out _));
            AssertProbesEqual(expected, Probe(result2, probeAt));
            yield return null;
        }

        [UnityTest]
        public IEnumerator Regrow_Replays_To_Identical_Grid()
        {
            var s = _factory.CreateMound(new Vector3(40f, 0f, 40f), 0.5f);
            Assert.IsTrue(_sync.Registry.TryGetId(s, out ulong id));
            byte[] snapshot = SnowWorldSync.EncodeSnapshot(id, s);

            var grown = _factory.Regrow(s, new Bounds(new Vector3(41.2f, 0.3f, 40f), Vector3.one * 0.5f));
            Assert.AreNotEqual(s, grown);
            Assert.AreEqual(1, _captured.Count, "regrow should broadcast one exact-geometry event");
            int expectedSize = grown.Info.size;
            Vector3 expectedPos = grown.transform.position;
            Vector3 probeAt = new Vector3(40f, 0.2f, 40f);
            float[] expected = Probe(grown, probeAt);

            var remote = BecomeRemotePeer();
            remote.ApplySnapshot(snapshot);
            remote.Apply(_captured[0]);
            Assert.IsTrue(remote.Registry.TryGet(id, out var grown2), "id must survive the regrow on the remote too");
            Assert.AreEqual(expectedSize, grown2.Info.size);
            Assert.Less((expectedPos - grown2.transform.position).magnitude, 1e-4f);
            AssertProbesEqual(expected, Probe(grown2, probeAt));
            yield return null;
        }

        [UnityTest]
        public IEnumerator Props_Place_And_Remove_Replay()
        {
            var s = _factory.CreateMound(new Vector3(50f, 0f, 50f), 0.5f);
            Assert.IsTrue(_sync.Registry.TryGetId(s, out ulong id));
            byte[] snapshot = SnowWorldSync.EncodeSnapshot(id, s);

            Vector3 point = new Vector3(50f, 0.4f, 50f);
            Vector3 normal = Vector3.up;
            var entry = AccessoryCatalog.Find("carrot");
            Assert.IsNotNull(entry);
            var prop = AccessoryPlacer.PlaceEntry(s, entry, point, normal);
            Assert.AreEqual(1, _captured.Count, "place should broadcast");
            SculptureNet.RaisePropRemoved(s, "carrot", prop.LocalPos);
            prop.Remove();
            Assert.AreEqual(2, _captured.Count, "remove should broadcast");

            var remote = BecomeRemotePeer();
            remote.ApplySnapshot(snapshot);
            Assert.IsTrue(remote.Registry.TryGet(id, out var s2));
            remote.Apply(_captured[0]);
            Assert.AreEqual(1, s2.Props.Count, "remote should have the placed carrot");
            Assert.AreEqual("carrot", s2.Props[0].prefabId);
            remote.Apply(_captured[1]);
            Assert.AreEqual(0, s2.Props.Count, "remote should have removed it again");
            yield return null;
        }

        [UnityTest]
        public IEnumerator Shave_RoundTrips_With_Identical_Cut()
        {
            var s = _factory.CreateMound(new Vector3(70f, 0f, 70f), 0.5f);
            Assert.IsTrue(_sync.Registry.TryGetId(s, out ulong id));
            byte[] snapshot = SnowWorldSync.EncodeSnapshot(id, s);

            Vector3 top = new Vector3(70f, 0.5f, 70f);
            float pack = SculptFeelConfig.Pack(s.CompactionUnderSurface(top, Vector3.up));
            var stamps = new List<SculptureNet.ShaveStamp>();
            for (int i = 0; i < 3; i++)
            {
                Vector3 p = top + new Vector3(0.03f * i, 0f, 0f);
                stamps.Add(new SculptureNet.ShaveStamp { point = p, normal = Vector3.up, prm = SculptureShave.ParamsFor(_cfg, _cfg.voxelSize, pack, false, p) });
            }
            float slump = _cfg.Slump(pack);
            SculptureShave.ApplyStamps(s, 0.12f, slump, stamps, out _, out _);
            SculptureNet.RaiseShaved(new SculptureNet.ShaveInfo { radius = 0.12f, slump = slump, stamps = stamps, targets = new List<SnowSculpture> { s } });
            Assert.AreEqual(1, _captured.Count, "one shave event expected");
            float[] expected = Probe(s, top - Vector3.up * 0.03f);
            float expectedVolume = s.DensityVolume();

            var remote = BecomeRemotePeer();
            remote.ApplySnapshot(snapshot);
            Assert.IsTrue(remote.Registry.TryGet(id, out var s2));
            remote.Apply(_captured[0]);
            AssertProbesEqual(expected, Probe(s2, top - Vector3.up * 0.03f));
            Assert.AreEqual(expectedVolume, s2.DensityVolume(), expectedVolume * 0.001f, "remote removed exactly the same snow");
            yield return null;
        }

        [UnityTest]
        public IEnumerator Snapshot_Carries_Compaction()
        {
            var ball = _factory.CreateSnowball(new Vector3(75f, 0.2f, 75f), 0.2f, _cfg.compactionScooped);
            Assert.IsTrue(_sync.Registry.TryGetId(ball.Sculpture, out ulong id));
            byte[] snapshot = SnowWorldSync.EncodeSnapshot(id, ball.Sculpture);
            var remote = BecomeRemotePeer();
            remote.ApplySnapshot(snapshot);
            Assert.IsTrue(remote.Registry.TryGet(id, out var s2));
            Assert.AreEqual(_cfg.compactionScooped, s2.MeanCompaction(), 1f);
            yield return null;
        }

        [UnityTest]
        public IEnumerator Squeeze_RoundTrips()
        {
            var ball = _factory.CreateSnowball(new Vector3(80f, 0.2f, 80f), 0.2f, _cfg.compactionScooped);
            Assert.IsTrue(_sync.Registry.TryGetId(ball.Sculpture, out ulong id));
            byte[] snapshot = SnowWorldSync.EncodeSnapshot(id, ball.Sculpture);

            float linear = 0.87f;
            ball.Sculpture.Squeeze(linear, 1f);
            ball.radius *= linear;
            SculptureNet.RaiseSqueezed(ball, linear, 1f);
            Assert.AreEqual(1, _captured.Count);
            float expectedVolume = ball.Sculpture.DensityVolume();
            float expectedRadius = ball.radius;

            var remote = BecomeRemotePeer();
            remote.ApplySnapshot(snapshot);
            remote.Apply(_captured[0]);
            Assert.IsTrue(remote.Registry.TryGet(id, out var s2));
            Assert.AreEqual(expectedVolume, s2.DensityVolume(), expectedVolume * 0.001f);
            Assert.AreEqual(expectedRadius, s2.GetComponent<Snowball>().radius, 1e-4f);
            Assert.GreaterOrEqual(s2.MeanCompaction(), 250f);
            yield return null;
        }

        [UnityTest]
        public IEnumerator Detached_Island_Replays_With_Same_Id_And_Voxels()
        {
            var s = _factory.CreateMound(new Vector3(90f, 0f, 90f), 0.4f);
            Vector3 floating = new Vector3(90.4f, 1.0f, 90f);
            s.StampSphere(floating, 0.2f, 0.7f, 220f, float.NegativeInfinity);
            Assert.IsTrue(_sync.Registry.TryGetId(s, out ulong id));
            byte[] snapshot = SnowWorldSync.EncodeSnapshot(id, s);

            var result = SculptureStructure.Check(s, int3.zero, new int3(s.Info.size));
            Assert.AreEqual(1, result.Detached.Count);
            var island = result.Detached[0];
            Assert.IsTrue(_sync.Registry.TryGetId(island.Sculpture, out ulong islandId));
            Assert.GreaterOrEqual(_captured.Count, 1, "a detach event (plus any thin crumbles)");
            float expectedSourceVolume = s.DensityVolume();
            float expectedIslandVolume = island.Sculpture.DensityVolume();
            Vector3 islandPos = island.transform.position;

            var remote = BecomeRemotePeer();
            remote.ApplySnapshot(snapshot);
            foreach (var evt in _captured) remote.Apply(evt);
            Assert.IsTrue(remote.Registry.TryGet(id, out var s2));
            Assert.IsTrue(remote.Registry.TryGet(islandId, out var island2), "the island ball exists under its id");
            Assert.AreEqual(expectedSourceVolume, s2.DensityVolume(), expectedSourceVolume * 0.001f, "source lost exactly the island");
            Assert.AreEqual(expectedIslandVolume, island2.DensityVolume(), expectedIslandVolume * 0.001f);
            Assert.Less((islandPos - island2.transform.position).magnitude, 1e-3f);
            Assert.AreEqual(0f, s2.SampleDensityWorld(floating), 1e-3f);
            yield return null;
        }

        [UnityTest]
        public IEnumerator Suppressed_Replay_Does_Not_Rebroadcast()
        {
            var s = _factory.CreateMound(new Vector3(60f, 0f, 60f), 0.5f);
            Assert.IsTrue(_sync.Registry.TryGetId(s, out ulong id));
            Vector3 p = new Vector3(60f, 0.25f, 60f);
            SculptureNet.RaiseStroke(new SculptureNet.StrokeInfo
            { op = 3, point = p, radius = 0.3f, ticks = 2, targets = new List<SnowSculpture> { s } });
            Assert.AreEqual(1, _captured.Count);

            byte[] evt = _captured[0];
            _captured.Clear();
            _sync.Apply(evt); // a "remote" event arriving at this peer
            Assert.AreEqual(0, _captured.Count, "applying a remote event must not send anything");
            yield return null;
        }
    }
}
