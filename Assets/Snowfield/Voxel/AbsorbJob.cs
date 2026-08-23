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
    /// </summary>
    [BurstCompile]
    public struct AbsorbJob : IJobParallelFor
    {
        [NativeDisableParallelForRestriction] public NativeArray<byte> Density;
        public VoxelGridInfo Info;
        [ReadOnly] public NativeArray<byte> Source;
        public VoxelGridInfo SourceInfo;
        public int3 AabbMin, AabbExtent;
        public float4x4 ToSourceVoxel;

        public void Execute(int i)
        {
            int3 p = BrushMath.AabbCoord(i, AabbMin, AabbExtent);
            float3 sp = math.transform(ToSourceVoxel, (float3)p);
            float d = DensitySampler.Trilinear(Source, SourceInfo, sp);
            if (d <= 0.5f) return;
            int idx = Info.Index(p);
            byte v = (byte)math.round(math.clamp(d, 0f, 255f));
            if (v > Density[idx]) Density[idx] = v;
        }
    }
}
