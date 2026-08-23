using NUnit.Framework;
using Unity.Collections;
using Unity.Jobs;
using Unity.Mathematics;

namespace Snowfield.Field.Tests
{
    public class TerrainJobTests
    {
        const int Samples = 65; // 64 cells
        NativeArray<float> _h;

        [SetUp] public void SetUp() => _h = new NativeArray<float>(Samples * Samples, Allocator.Persistent, NativeArrayOptions.ClearMemory);
        [TearDown] public void TearDown() => _h.Dispose();

        static void Aabb(float2 c, float r, out int2 min, out int2 max)
        {
            min = math.clamp((int2)math.floor(c - r), 0, Samples);
            max = math.clamp((int2)math.ceil(c + r) + 1, 0, Samples);
        }

        float H(int x, int z) => _h[x + z * Samples];

        [Test]
        public void Brush_RaisesAndCarves_WithinClamp()
        {
            float2 c = new float2(32, 32);
            Aabb(c, 6f, out var min, out var max);
            int2 ext = max - min;
            for (int i = 0; i < 50; i++)
                new HeightBrushJob { Height = _h, Samples = Samples, AabbMin = min, AabbExtent = ext, Center = c, RadiusSamples = 6f, Amount = 0.05f, Shoulder = 0.6f, MinH = -0.6f, MaxH = 0.8f }
                    .Schedule(ext.x * ext.y, 64).Complete();
            Assert.AreEqual(0.8f, H(32, 32), 1e-4f, "raise should clamp at MaxH");
            Assert.AreEqual(0f, H(32, 45), 1e-6f, "outside the radius is untouched");

            for (int i = 0; i < 100; i++)
                new HeightBrushJob { Height = _h, Samples = Samples, AabbMin = min, AabbExtent = ext, Center = c, RadiusSamples = 6f, Amount = -0.05f, Shoulder = 0.6f, MinH = -0.6f, MaxH = 0.8f }
                    .Schedule(ext.x * ext.y, 64).Complete();
            Assert.AreEqual(-0.6f, H(32, 32), 1e-4f, "carve should clamp at MinH");
        }

        [Test]
        public void Stamp_PacksDownToCap_ButLeavesCarvedGroundAlone()
        {
            float2 c = new float2(20, 20);
            Aabb(c, 3f, out var min, out var max);
            int2 ext = max - min;
            for (int i = 0; i < 20; i++)
                new HeightStampJob { Height = _h, Samples = Samples, AabbMin = min, AabbExtent = ext, Center = c, RadiusSamples = 3f, Depth = 0.02f, Shoulder = 0.5f, PathDepthCap = 0.1f }
                    .Schedule(ext.x * ext.y, 64).Complete();
            Assert.AreEqual(-0.1f, H(20, 20), 1e-4f, "repeated footprints pack down to the cap");

            // carve deeper, then stamp again: must not rise back to the cap
            _h[20 + 20 * Samples] = -0.4f;
            new HeightStampJob { Height = _h, Samples = Samples, AabbMin = min, AabbExtent = ext, Center = c, RadiusSamples = 3f, Depth = 0.02f, Shoulder = 0.5f, PathDepthCap = 0.1f }
                .Schedule(ext.x * ext.y, 64).Complete();
            Assert.AreEqual(-0.4f, H(20, 20), 1e-4f, "stamp never raises carved ground");
        }

        [Test]
        public void Smooth_LeavesFlatGroundFlat()
        {
            float2 c = new float2(32, 32);
            Aabb(c, 5f, out var min, out var max);
            int2 ext = max - min;
            var snap = new NativeArray<float>(_h, Allocator.TempJob);
            new HeightSmoothJob { Source = snap, Height = _h, Samples = Samples, AabbMin = min, AabbExtent = ext, Center = c, RadiusSamples = 5f, Strength = 1f, Shoulder = 0.5f }
                .Schedule(ext.x * ext.y, 64).Complete();
            snap.Dispose();
            for (int i = 0; i < _h.Length; i++) Assert.AreEqual(0f, _h[i], 1e-6f);
        }

        [Test]
        public void MeshJob_ChunkSeamsShareHeightsAndNormals()
        {
            // bump straddling the seam between chunk (0,0) and (1,0) with 32-cell chunks
            float2 c = new float2(32, 16);
            Aabb(c, 6f, out var min, out var max);
            int2 ext = max - min;
            new HeightBrushJob { Height = _h, Samples = Samples, AabbMin = min, AabbExtent = ext, Center = c, RadiusSamples = 6f, Amount = 0.3f, Shoulder = 0.4f, MinH = -1f, MaxH = 1f }
                .Schedule(ext.x * ext.y, 64).Complete();

            var v0 = new NativeList<TerrainVertex>(Allocator.TempJob); var i0 = new NativeList<int>(Allocator.TempJob);
            var v1 = new NativeList<TerrainVertex>(Allocator.TempJob); var i1 = new NativeList<int>(Allocator.TempJob);
            new TerrainMeshJob { Height = _h, Samples = Samples, ChunkCells = 32, ChunkCoord = new int2(0, 0), CellSize = 0.05f, FieldSize = 3.2f, Vertices = v0, Indices = i0 }.Schedule().Complete();
            new TerrainMeshJob { Height = _h, Samples = Samples, ChunkCells = 32, ChunkCoord = new int2(1, 0), CellSize = 0.05f, FieldSize = 3.2f, Vertices = v1, Indices = i1 }.Schedule().Complete();

            Assert.AreEqual(33 * 33, v0.Length);
            Assert.AreEqual(32 * 32 * 6, i0.Length);
            int n = 33;
            for (int z = 0; z < n; z++)
            {
                var a = v0[32 + z * n]; // right edge of chunk 0
                var b = v1[0 + z * n];  // left edge of chunk 1
                Assert.AreEqual(a.Position.y, b.Position.y, 1e-6f, $"seam height row {z}");
                Assert.Less(math.distance(a.Normal, b.Normal), 1e-5f, $"seam normal row {z}");
                Assert.Greater(a.Normal.y, 0f, "normals face up");
            }
            v0.Dispose(); i0.Dispose(); v1.Dispose(); i1.Dispose();
        }
    }
}
