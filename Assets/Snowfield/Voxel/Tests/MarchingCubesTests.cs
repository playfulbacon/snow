using NUnit.Framework;
using Unity.Collections;
using Unity.Jobs;
using Unity.Mathematics;

namespace Snowfield.Voxel.Tests
{
    public class MarchingCubesTests
    {
        VoxelGrid _grid;
        MarchingCubesLookup _lookup;

        [SetUp]
        public void SetUp()
        {
            _grid = new VoxelGrid(32, 0.04f);
            _lookup = MarchingCubesLookup.Create(Allocator.Persistent);
        }

        [TearDown]
        public void TearDown()
        {
            _grid.Dispose();
            _lookup.Dispose();
        }

        void StampSphere(float3 c, float r)
        {
            Assert.IsTrue(_grid.SphereAabb(c, r, out var min, out var max));
            int3 ext = max - min;
            new SphereStampJob
            {
                Density = _grid.Density, Info = _grid.Info, AabbMin = min, AabbExtent = ext,
                CenterVoxel = c, RadiusVoxels = r, Shoulder = 0.6f, ClipBelowY = -1e9f,
            }.Schedule(ext.x * ext.y * ext.z, 64).Complete();
        }

        (NativeList<SnowVertex>, NativeList<int>) MeshChunk(int3 chunk)
        {
            var v = new NativeList<SnowVertex>(Allocator.TempJob);
            var i = new NativeList<int>(Allocator.TempJob);
            new MeshChunkJob { Density = _grid.Density, Info = _grid.Info, ChunkCoord = chunk, Lookup = _lookup, Vertices = v, Indices = i }
                .Schedule().Complete();
            return (v, i);
        }

        [Test]
        public void TriTable_HasExpectedShape()
        {
            Assert.AreEqual(256 * 16, MarchingCubesTables.TriTable.Length);
            Assert.AreEqual(256, MarchingCubesTables.EdgeTable.Length);
            for (int c = 0; c < 256; c++)
            {
                int edges = 0;
                for (int k = 0; k < 16; k++)
                {
                    int e = MarchingCubesTables.TriTable[c * 16 + k];
                    if (e < 0) break;
                    Assert.That(e, Is.InRange(0, 11), $"case {c}");
                    edges |= 1 << e;
                }
                Assert.AreEqual(MarchingCubesTables.EdgeTable[c], edges, $"edge table mismatch for case {c}");
            }
        }

        [Test]
        public void EmptyGrid_ProducesNoGeometry()
        {
            var (v, i) = MeshChunk(new int3(0, 0, 0));
            Assert.AreEqual(0, v.Length);
            Assert.AreEqual(0, i.Length);
            v.Dispose(); i.Dispose();
        }

        [Test]
        public void Sphere_WindingMatchesGradientNormals_AndIsClosed()
        {
            StampSphere(new float3(16, 16, 16), 9f);
            int agree = 0, total = 0;
            var edgeUse = new System.Collections.Generic.Dictionary<(float3, float3), int>();
            for (int ci = 0; ci < _grid.Info.ChunkCount; ci++)
            {
                var (v, idx) = MeshChunk(_grid.Info.ChunkCoord(ci));
                for (int t = 0; t < idx.Length; t += 3)
                {
                    var a = v[idx[t]]; var b = v[idx[t + 1]]; var c = v[idx[t + 2]];
                    // Unity: clockwise = front face. Geometric normal for CW winding in a left-handed frame:
                    float3 geom = math.cross(b.Position - a.Position, c.Position - a.Position);
                    float3 grad = a.Normal + b.Normal + c.Normal;
                    if (math.dot(geom, grad) > 0) agree++;
                    total++;
                    // radial check: normals should point away from the sphere centre
                    float3 centre = new float3(16, 16, 16) * _grid.Info.voxelSize;
                    Assert.Greater(math.dot(a.Normal, a.Position - centre), 0f, "normal points inward");
                }
                v.Dispose(); idx.Dispose();
            }
            Assert.Greater(total, 100, "sphere should produce plenty of triangles");
            Assert.AreEqual(total, agree, "every triangle's winding should agree with its gradient normal");
        }

        [Test]
        public void GridSerializer_RoundTripsSphereExactly()
        {
            StampSphere(new float3(16, 16, 16), 9f);
            var blob = GridSerializer.Encode(_grid.Density);
            Assert.Less(blob.Length, _grid.Density.Length / 4, "RLE should compress a mostly-empty grid hard");
            var restored = new NativeArray<byte>(_grid.Density.Length, Allocator.Temp);
            GridSerializer.Decode(blob, restored);
            for (int i = 0; i < restored.Length; i++)
                if (restored[i] != _grid.Density[i]) Assert.Fail($"voxel {i} differs");
            restored.Dispose();
        }

        [Test]
        public void AddBrush_RaisesDensity_AndMarksDirty()
        {
            float3 c = new float3(8, 8, 8);
            Assert.IsTrue(_grid.SphereAabb(c, 3f, out var min, out var max));
            int3 ext = max - min;
            new AddBrushJob
            {
                Density = _grid.Density, Info = _grid.Info, AabbMin = min, AabbExtent = ext,
                CenterVoxel = c, RadiusVoxels = 3f, RatePerTick = 10f, Shoulder = 0.6f,
            }.Schedule(ext.x * ext.y * ext.z, 64).Complete();
            _grid.MarkDirty(min, max);

            Assert.AreEqual(10, _grid.Density[_grid.Info.Index(8, 8, 8)]);
            Assert.AreEqual(0, _grid.Density[_grid.Info.Index(8, 8, 12)]);
            Assert.IsTrue(_grid.ChunkDirty[0]);
            Assert.IsFalse(_grid.ChunkDirty[_grid.Info.ChunkIndex(new int3(1, 1, 1))]);
        }

        [Test]
        public void SmoothBrush_PreservesUniformRegions()
        {
            StampSphere(new float3(16, 16, 16), 12f);
            var snapshot = new NativeArray<byte>(_grid.Density, Allocator.TempJob);
            float3 c = new float3(16, 16, 16);
            Assert.IsTrue(_grid.SphereAabb(c, 4f, out var min, out var max));
            int3 ext = max - min;
            new SmoothBrushJob
            {
                Source = snapshot, Density = _grid.Density, Info = _grid.Info, AabbMin = min, AabbExtent = ext,
                CenterVoxel = c, RadiusVoxels = 4f, Strength = 1f, Shoulder = 0.5f,
            }.Schedule(ext.x * ext.y * ext.z, 64).Complete();
            snapshot.Dispose();
            Assert.AreEqual(255, _grid.Density[_grid.Info.Index(16, 16, 16)], "solid core should stay solid");
        }
    }
}
