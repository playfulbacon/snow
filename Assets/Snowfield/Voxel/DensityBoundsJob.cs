using Unity.Burst;
using Unity.Collections;
using Unity.Jobs;
using Unity.Mathematics;

namespace Snowfield.Voxel
{
    /// <summary>Voxel-space AABB of all non-empty density. Result: [min, max] inclusive; min > max means empty.</summary>
    [BurstCompile]
    public struct DensityBoundsJob : IJob
    {
        [ReadOnly] public NativeArray<byte> Density;
        public VoxelGridInfo Info;
        public NativeArray<int3> Result; // [0] = min, [1] = max

        public void Execute()
        {
            int3 min = new int3(int.MaxValue), max = new int3(int.MinValue);
            int size = Info.size;
            int i = 0;
            for (int z = 0; z < size; z++)
            for (int y = 0; y < size; y++)
            for (int x = 0; x < size; x++, i++)
            {
                if (Density[i] == 0) continue;
                min = math.min(min, new int3(x, y, z));
                max = math.max(max, new int3(x, y, z));
            }
            Result[0] = min;
            Result[1] = max;
        }
    }
}
