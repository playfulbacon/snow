using NUnit.Framework;
using Unity.Collections;
using Unity.Jobs;
using Unity.Mathematics;

namespace Snowfield.Voxel.Tests
{
    /// <summary>EditMode: the compaction channel, the shave kernel, the squeeze resample and the structural passes, all on bare grids.</summary>
    public class CompactionTests
    {
        VoxelGrid _grid;

        [SetUp] public void SetUp() => _grid = new VoxelGrid(32, 0.03f);
        [TearDown] public void TearDown() => _grid.Dispose();

        int Idx(int x, int y, int z) => _grid.Info.Index(x, y, z);

        void Stamp(float3 c, float r, float compaction)
        {
            Assert.IsTrue(_grid.SphereAabb(c, r, out var min, out var max));
            int3 ext = max - min;
            new SphereStampJob
            {
                Density = _grid.Density, Compaction = _grid.Compaction, Info = _grid.Info, AabbMin = min, AabbExtent = ext,
                CenterVoxel = c, RadiusVoxels = r, Shoulder = 0.7f, ClipBelowY = -1e9f, StampCompaction = compaction,
            }.Schedule(ext.x * ext.y * ext.z, 64).Complete();
        }

        void FillBox(int3 lo, int3 hi, byte density, byte compaction)
        {
            for (int z = lo.z; z <= hi.z; z++)
            for (int y = lo.y; y <= hi.y; y++)
            for (int x = lo.x; x <= hi.x; x++)
            {
                _grid.Density[Idx(x, y, z)] = density;
                _grid.Compaction[Idx(x, y, z)] = compaction;
            }
        }

        float Mass()
        {
            var r = new NativeArray<float>(1, Allocator.TempJob);
            new DensitySumJob { Density = _grid.Density, Result = r }.Schedule().Complete();
            float m = r[0]; r.Dispose();
            return m;
        }

        [Test]
        public void AddBrush_ArrivingSnow_CarriesArrivalCompaction()
        {
            float3 c = new float3(16, 16, 16);
            Assert.IsTrue(_grid.SphereAabb(c, 4f, out var min, out var max));
            int3 ext = max - min;
            new AddBrushJob
            {
                Density = _grid.Density, Compaction = _grid.Compaction, Info = _grid.Info, AabbMin = min, AabbExtent = ext,
                CenterVoxel = c, RadiusVoxels = 4f, RatePerTick = 200f, Shoulder = 0.6f, ArrivalCompaction = 40f,
            }.Schedule(ext.x * ext.y * ext.z, 64).Complete();
            Assert.AreEqual(200, _grid.Density[Idx(16, 16, 16)]);
            Assert.AreEqual(40, _grid.Compaction[Idx(16, 16, 16)]);
            Assert.AreEqual(0, _grid.Compaction[Idx(16, 16, 24)], "air carries no compaction");
        }

        [Test]
        public void SphereStamp_KeepsExistingCompaction_WhereNothingIsAdded()
        {
            Stamp(new float3(16, 16, 16), 6f, 220f);
            Assert.AreEqual(220, _grid.Compaction[Idx(16, 16, 16)]);
            Stamp(new float3(22, 16, 16), 6f, 40f); // a fluffy stamp overlapping the packed core
            Assert.AreEqual(220, _grid.Compaction[Idx(16, 16, 16)], "a full voxel takes nothing, so keeps its packing");
            Assert.AreEqual(40, _grid.Compaction[Idx(26, 16, 16)], "new snow arrives fluffy");
        }

        [Test]
        public void Shave_RemovesAboveTheCutPlane_AndLeavesTheBodyBelow()
        {
            FillBox(new int3(0, 0, 0), new int3(31, 19, 31), 255, 255); // half-space: solid below y = 20
            float3 c = new float3(16, 19.5f, 16);
            var prm = new ShaveParams { DepthVoxels = 1.5f, SoftVoxels = 0.75f, Shoulder = 0.85f, NoiseAmplitude = 0f, Seed = 1 };
            Assert.IsTrue(_grid.SphereAabb(c, 8f, out var min, out var max));
            int3 ext = max - min;
            float before = Mass();
            new ShaveJob
            {
                Density = _grid.Density, Compaction = _grid.Compaction, Info = _grid.Info, AabbMin = min, AabbExtent = ext,
                CenterVoxel = c, NormalVoxel = new float3(0, 1, 0), RadiusVoxels = 6f, Params = prm,
            }.Schedule(ext.x * ext.y * ext.z, 64).Complete();
            Assert.AreEqual(0, _grid.Density[Idx(16, 19, 16)], "the top layer under the disc is gone");
            Assert.AreEqual(0, _grid.Compaction[Idx(16, 19, 16)], "removed snow leaves no compaction behind");
            Assert.AreEqual(255, _grid.Density[Idx(16, 16, 16)], "below depth + soft ramp: untouched");
            Assert.AreEqual(255, _grid.Density[Idx(16, 19, 26)], "outside the disc laterally: untouched");
            Assert.Less(Mass(), before, "mass was removed");
        }

        [Test]
        public void Shave_FluffyParams_CutDeeper()
        {
            FillBox(new int3(0, 0, 0), new int3(31, 19, 31), 255, 40);
            float3 c = new float3(16, 19.5f, 16);
            var prm = new ShaveParams { DepthVoxels = 4.5f, SoftVoxels = 2.25f, Shoulder = 0.25f, NoiseAmplitude = 0.3f, Seed = 7 };
            Assert.IsTrue(_grid.SphereAabb(c, 10f, out var min, out var max));
            int3 ext = max - min;
            new ShaveJob
            {
                Density = _grid.Density, Compaction = _grid.Compaction, Info = _grid.Info, AabbMin = min, AabbExtent = ext,
                CenterVoxel = c, NormalVoxel = new float3(0, 1, 0), RadiusVoxels = 6f, Params = prm,
            }.Schedule(ext.x * ext.y * ext.z, 64).Complete();
            Assert.AreEqual(0, _grid.Density[Idx(16, 16, 16)], "a fluffy gouge reaches 3x deeper");
            Assert.AreEqual(255, _grid.Density[Idx(16, 12, 16)], "but still stops");
            Assert.AreEqual(255, _grid.Density[Idx(16, 19, 27)], "noise only shrinks the disc: nothing outside the brush is touched");
        }

        [Test]
        public void Rescale_ShrinksVolume_AndPacks()
        {
            Stamp(new float3(16, 16, 16), 8f, 40f);
            float before = Mass();
            var src = new NativeArray<byte>(_grid.Density, Allocator.TempJob);
            var srcC = new NativeArray<byte>(_grid.Compaction, Allocator.TempJob);
            new RescaleJob
            {
                Source = src, SourceCompaction = srcC, Density = _grid.Density, Compaction = _grid.Compaction,
                Info = _grid.Info, CenterVoxel = new float3(16, 16, 16), InvScale = 1f / 0.8f, CompactionBlend = 1f,
            }.Schedule(_grid.Info.VoxelCount, 256).Complete();
            src.Dispose(); srcC.Dispose();
            float after = Mass();
            Assert.AreEqual(before * 0.512f, after, before * 0.08f, "linear 0.8 → volume 0.512");
            Assert.AreEqual(255, _grid.Compaction[Idx(16, 16, 16)], "fully packed after a full blend");
            Assert.Greater(_grid.Density[Idx(16, 16, 16)], 250, "the core is still solid");
        }

        (NativeArray<byte> mask, float removed, int count) RunThinness(int3 min, int3 max, float rFluffy, float rPacked, NativeArray<SupportSegment> segs, int segCount, float supportR)
        {
            int3 ext = max - min;
            var mask = new NativeArray<byte>(ext.x * ext.y * ext.z, Allocator.TempJob);
            var res = new NativeArray<float>(2, Allocator.TempJob);
            new ThinnessJob
            {
                Density = _grid.Density, Compaction = _grid.Compaction, Info = _grid.Info,
                RegionMin = min, RegionExtent = ext, ThinRadiusFluffy = rFluffy, ThinRadiusPacked = rPacked,
                Segments = segs, SegmentCount = segCount, SupportRadiusVoxels = supportR, Mask = mask, Result = res,
            }.Schedule().Complete();
            float removed = res[0]; int count = (int)res[1];
            res.Dispose();
            return (mask, removed, count);
        }

        int MaskAt(NativeArray<byte> mask, int3 min, int3 max, int3 p)
        {
            int3 ext = max - min, l = p - min;
            return mask[l.x + l.y * ext.x + l.z * ext.x * ext.y];
        }

        [Test]
        public void Thinness_PackedSlabHolds_FluffySlabBreaks_SheetAlwaysBreaks()
        {
            // A 6-voxel slab and a 1-voxel sheet, both packed.
            FillBox(new int3(4, 8, 4), new int3(27, 13, 27), 255, 255);
            FillBox(new int3(4, 20, 4), new int3(27, 20, 27), 255, 255);
            var segs = new NativeArray<SupportSegment>(1, Allocator.TempJob);
            int3 min = new int3(0), max = new int3(32);
            var (mask, _, count) = RunThinness(min, max, rFluffy: 3f, rPacked: 1f, segs, 0, 0f);
            Assert.AreEqual(0, MaskAt(mask, min, max, new int3(16, 10, 16)), "packed 6-voxel slab core holds");
            Assert.AreEqual(0, MaskAt(mask, min, max, new int3(16, 8, 16)), "packed slab face holds");
            Assert.AreEqual(1, MaskAt(mask, min, max, new int3(16, 20, 16)), "a 1-voxel sheet has no interior: it breaks");
            Assert.AreEqual(0, MaskAt(mask, min, max, new int3(4, 8, 4)), "slab corners survive (one step from an interior voxel)");
            mask.Dispose();

            // The same slab as powder: 6 voxels is under the fluffy limit, so it goes.
            FillBox(new int3(4, 8, 4), new int3(27, 13, 27), 255, 0);
            var (mask2, _, _) = RunThinness(min, max, rFluffy: 3f, rPacked: 1f, segs, 0, 0f);
            Assert.AreEqual(1, MaskAt(mask2, min, max, new int3(16, 10, 16)), "fluffy 6-voxel slab breaks: packing unlocks the material");
            mask2.Dispose();
            segs.Dispose();
        }

        [Test]
        public void Thinness_TwigSupport_ExemptsSnowAroundIt()
        {
            FillBox(new int3(4, 20, 4), new int3(27, 20, 27), 255, 0); // a fluffy sheet
            var segs = new NativeArray<SupportSegment>(1, Allocator.TempJob);
            segs[0] = new SupportSegment { A = new float3(8, 20, 16), B = new float3(24, 20, 16) };
            int3 min = new int3(0), max = new int3(32);
            var (mask, _, _) = RunThinness(min, max, 3f, 1f, segs, 1, 2.5f);
            Assert.AreEqual(0, MaskAt(mask, min, max, new int3(16, 20, 16)), "on the twig: held");
            Assert.AreEqual(0, MaskAt(mask, min, max, new int3(16, 20, 18)), "within support radius: held");
            Assert.AreEqual(1, MaskAt(mask, min, max, new int3(16, 20, 26)), "far from the twig: breaks");
            mask.Dispose(); segs.Dispose();
        }

        (NativeArray<int> label, NativeList<IslandInfo> islands) RunConnectivity(NativeArray<SupportSegment> segs, int segCount, float supportR)
        {
            var label = new NativeArray<int>(_grid.Info.VoxelCount, Allocator.TempJob);
            var islands = new NativeList<IslandInfo>(4, Allocator.TempJob);
            var groundY = new NativeArray<float>(1, Allocator.TempJob); // floor mode
            new ConnectivityJob
            {
                Density = _grid.Density, Info = _grid.Info, Threshold = 64, GroundY = groundY, SeedDepthVoxels = 2f,
                Segments = segs, SegmentCount = segCount, SupportRadiusVoxels = supportR, Label = label, Islands = islands,
            }.Schedule().Complete();
            groundY.Dispose();
            return (label, islands);
        }

        [Test]
        public void Connectivity_FloatingCubeIsAnIsland_GroundedSlabIsNot()
        {
            FillBox(new int3(0, 0, 0), new int3(31, 3, 31), 255, 200);   // floor slab
            FillBox(new int3(10, 20, 10), new int3(14, 24, 14), 255, 200); // floating 5³ cube
            var segs = new NativeArray<SupportSegment>(1, Allocator.TempJob);
            var (label, islands) = RunConnectivity(segs, 0, 0f);
            Assert.AreEqual(1, islands.Length, "one island");
            Assert.AreEqual(1, label[Idx(16, 2, 16)], "slab is grounded");
            Assert.AreEqual(2, label[Idx(12, 22, 12)], "cube is island 2");
            Assert.AreEqual(125f, islands[0].Mass, 0.01f);
            Assert.AreEqual(new int3(10, 20, 10), islands[0].Min);
            Assert.AreEqual(new int3(14, 24, 14), islands[0].Max);
            label.Dispose(); islands.Dispose(); segs.Dispose();
        }

        [Test]
        public void Connectivity_TwigBridgesAGap()
        {
            FillBox(new int3(0, 0, 0), new int3(31, 3, 31), 255, 200);
            FillBox(new int3(10, 10, 10), new int3(14, 14, 14), 255, 200); // cube 6 voxels above the slab
            var segs = new NativeArray<SupportSegment>(1, Allocator.TempJob);
            segs[0] = new SupportSegment { A = new float3(12, 2, 12), B = new float3(12, 12, 12) };
            var (label, islands) = RunConnectivity(segs, 1, 1.5f);
            Assert.AreEqual(0, islands.Length, "the twig carries the flood across the gap");
            Assert.AreEqual(1, label[Idx(12, 12, 12)]);
            label.Dispose(); islands.Dispose(); segs.Dispose();
        }
    }
}
