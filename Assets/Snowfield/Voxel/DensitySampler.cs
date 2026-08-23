using Unity.Collections;
using Unity.Mathematics;

namespace Snowfield.Voxel
{
    /// <summary>Trilinear density lookup at a fractional voxel position. Returns 0 outside the grid.</summary>
    public static class DensitySampler
    {
        public static float Trilinear(NativeArray<byte> density, in VoxelGridInfo info, float3 p)
        {
            if (math.any(p < 0f) || math.any(p > info.size - 1)) return 0f;
            p = math.min(p, info.size - 1.001f);
            int3 i0 = (int3)math.floor(p);
            int3 i1 = math.min(i0 + 1, info.size - 1);
            float3 f = p - i0;

            float c000 = density[info.Index(i0.x, i0.y, i0.z)];
            float c100 = density[info.Index(i1.x, i0.y, i0.z)];
            float c010 = density[info.Index(i0.x, i1.y, i0.z)];
            float c110 = density[info.Index(i1.x, i1.y, i0.z)];
            float c001 = density[info.Index(i0.x, i0.y, i1.z)];
            float c101 = density[info.Index(i1.x, i0.y, i1.z)];
            float c011 = density[info.Index(i0.x, i1.y, i1.z)];
            float c111 = density[info.Index(i1.x, i1.y, i1.z)];

            float c00 = math.lerp(c000, c100, f.x);
            float c10 = math.lerp(c010, c110, f.x);
            float c01 = math.lerp(c001, c101, f.x);
            float c11 = math.lerp(c011, c111, f.x);
            return math.lerp(math.lerp(c00, c10, f.y), math.lerp(c01, c11, f.y), f.z);
        }
    }
}
