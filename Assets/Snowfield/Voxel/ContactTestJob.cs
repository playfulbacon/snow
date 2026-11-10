using Unity.Burst;
using Unity.Collections;
using Unity.Jobs;
using Unity.Mathematics;

namespace Snowfield.Voxel
{
    /// <summary>
    /// Does this grid's snow meet another grid's snow anywhere? Walks an AABB of this grid and stops at the
    /// first voxel that is solid here and solid there too, so a yes costs almost nothing and a no costs the
    /// walk. <see cref="ToOtherVoxel"/> maps this grid's voxel coordinates into the other grid's the same way
    /// <see cref="AbsorbJob"/> does, so the two may be posed and sized differently.
    ///
    /// <see cref="ReachVoxels"/> is the gap two surfaces may still have and count as joined. Marching cubes
    /// rounds both surfaces off, so snow a voxel short of touching already reads as touching on screen — and
    /// the player who just packed it on is waiting for one shape, not two that nearly meet. The grids are
    /// uniformly scaled, so an offset in the other grid's voxels is the same distance in metres either way.
    /// </summary>
    [BurstCompile]
    public struct ContactTestJob : IJob
    {
        [ReadOnly] public NativeArray<byte> Density;
        public VoxelGridInfo Info;
        [ReadOnly] public NativeArray<byte> Other;
        public VoxelGridInfo OtherInfo;
        public int3 AabbMin, AabbMax;    // min inclusive, max exclusive, in this grid's voxels
        public float4x4 ToOtherVoxel;
        public float ReachVoxels;
        public NativeArray<bool> Result; // [0] = the two lumps of snow meet

        public void Execute()
        {
            Result[0] = false;
            for (int z = AabbMin.z; z < AabbMax.z; z++)
            for (int y = AabbMin.y; y < AabbMax.y; y++)
            for (int x = AabbMin.x; x < AabbMax.x; x++)
            {
                int3 p = new int3(x, y, z);
                if (Density[Info.Index(p)] < VoxelGridInfo.Iso) continue;
                float3 op = math.transform(ToOtherVoxel, (float3)p);
                if (Solid(op)) { Result[0] = true; return; }
                if (ReachVoxels <= 0f) continue;
                // Nothing at this exact point: look a gap's length out along each axis for the other surface.
                if (Solid(op + new float3(ReachVoxels, 0f, 0f)) || Solid(op - new float3(ReachVoxels, 0f, 0f)) ||
                    Solid(op + new float3(0f, ReachVoxels, 0f)) || Solid(op - new float3(0f, ReachVoxels, 0f)) ||
                    Solid(op + new float3(0f, 0f, ReachVoxels)) || Solid(op - new float3(0f, 0f, ReachVoxels)))
                { Result[0] = true; return; }
            }
        }

        bool Solid(float3 otherVoxel) =>
            DensitySampler.Trilinear(Other, OtherInfo, otherVoxel) >= VoxelGridInfo.Iso;
    }
}
