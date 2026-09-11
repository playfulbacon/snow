using Unity.Burst;
using Unity.Collections;
using Unity.Jobs;
using Unity.Mathematics;

namespace Snowfield.Voxel
{
    /// <summary>
    /// Max-merge another grid's density into this grid over an AABB of this grid.
    /// <see cref="ToSourceVoxel"/> maps this grid's voxel coordinates into the source grid's voxel coordinates
    /// (handles both transforms, offsets and voxel sizes), so the two grids may be rotated and sized differently.
    /// Compaction rides along mass-weighted, and where the two bodies overlap (the sunk-in contact region of a
    /// fuse) it is raised to at least <see cref="WeldCompaction"/>: the weld shell is firmer than either side.
    /// </summary>
    [BurstCompile]
    public struct AbsorbJob : IJobParallelFor
    {
        [NativeDisableParallelForRestriction] public NativeArray<byte> Density;
        [NativeDisableParallelForRestriction] public NativeArray<byte> Compaction;
        public VoxelGridInfo Info;
        [ReadOnly] public NativeArray<byte> Source;
        [ReadOnly] public NativeArray<byte> SourceCompaction;
        public VoxelGridInfo SourceInfo;
        public int3 AabbMin, AabbExtent;
        public float4x4 ToSourceVoxel;
        public float WeldCompaction;

        public void Execute(int i)
        {
            int3 p = BrushMath.AabbCoord(i, AabbMin, AabbExtent);
            float3 sp = math.transform(ToSourceVoxel, (float3)p);
            float d = DensitySampler.Trilinear(Source, SourceInfo, sp);
            if (d <= 0.5f) return;
            int idx = Info.Index(p);
            byte old = Density[idx];
            float sc = DensitySampler.Trilinear(SourceCompaction, SourceInfo, sp);
            // Both bodies substantially solid here: the contact shell. Raised even where nothing is added.
            bool weld = old >= VoxelGridInfo.Iso / 2 && d >= VoxelGridInfo.Iso / 2;
            byte v = (byte)math.round(math.clamp(d, 0f, 255f));
            byte c = Compaction[idx];
            if (v > old)
            {
                Density[idx] = v;
                c = BrushMath.MixCompaction(old, c, v - old, sc);
            }
            if (weld) c = (byte)math.max(c, (byte)math.clamp(WeldCompaction, 0f, 255f));
            Compaction[idx] = c;
        }
    }
}
