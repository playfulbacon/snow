using Unity.Burst;
using Unity.Collections;
using Unity.Jobs;
using Unity.Mathematics;

namespace Snowfield.Voxel
{
    /// <summary>Shared helpers for spherical brush kernels. Pure, Burst-friendly.</summary>
    [BurstCompile]
    public static class BrushMath
    {
        /// <summary>1 at the core, 0 at the edge; flat inside <paramref name="shoulder"/>, smoothstep shoulder outside.</summary>
        public static float Falloff(float normalisedDist, float shoulder)
        {
            if (normalisedDist >= 1f) return 0f;
            if (normalisedDist <= shoulder) return 1f;
            return math.smoothstep(1f, shoulder, normalisedDist);
        }

        public static int3 AabbCoord(int i, int3 min, int3 extent)
        {
            int x = i % extent.x;
            int y = (i / extent.x) % extent.y;
            int z = i / (extent.x * extent.y);
            return min + new int3(x, y, z);
        }
    }

    /// <summary>Raise (or lower, with negative rate) density inside a sphere, rate-capped. Iterates only the brush AABB.</summary>
    [BurstCompile]
    public struct AddBrushJob : IJobParallelFor
    {
        [NativeDisableParallelForRestriction] public NativeArray<byte> Density;
        public VoxelGridInfo Info;
        public int3 AabbMin, AabbExtent;
        public float3 CenterVoxel;
        public float RadiusVoxels;
        public float RatePerTick;   // density units per tick at core; negative = carve
        public float Shoulder;

        public void Execute(int i)
        {
            int3 p = BrushMath.AabbCoord(i, AabbMin, AabbExtent);
            float d = math.distance((float3)p, CenterVoxel) / RadiusVoxels;
            float f = BrushMath.Falloff(d, Shoulder);
            if (f <= 0f) return;
            int idx = Info.Index(p);
            float v = Density[idx] + RatePerTick * f;
            Density[idx] = (byte)math.clamp(v, 0f, 255f);
        }
    }

    /// <summary>
    /// Blur: each voxel in the AABB is lerped toward the mean of its 6-neighbourhood.
    /// Reads from <see cref="Source"/> (a snapshot) and writes to <see cref="Density"/>, so it is order-independent.
    /// </summary>
    [BurstCompile]
    public struct SmoothBrushJob : IJobParallelFor
    {
        [ReadOnly] public NativeArray<byte> Source;
        [NativeDisableParallelForRestriction] public NativeArray<byte> Density;
        public VoxelGridInfo Info;
        public int3 AabbMin, AabbExtent;
        public float3 CenterVoxel;
        public float RadiusVoxels;
        public float Strength;
        public float Shoulder;

        public void Execute(int i)
        {
            int3 p = BrushMath.AabbCoord(i, AabbMin, AabbExtent);
            float d = math.distance((float3)p, CenterVoxel) / RadiusVoxels;
            float f = BrushMath.Falloff(d, Shoulder) * Strength;
            if (f <= 0f) return;

            int idx = Info.Index(p);
            float sum = Source[idx] * 2f; // centre weighted x2
            float w = 2f;
            Accumulate(p + new int3(1, 0, 0), ref sum, ref w);
            Accumulate(p - new int3(1, 0, 0), ref sum, ref w);
            Accumulate(p + new int3(0, 1, 0), ref sum, ref w);
            Accumulate(p - new int3(0, 1, 0), ref sum, ref w);
            Accumulate(p + new int3(0, 0, 1), ref sum, ref w);
            Accumulate(p - new int3(0, 0, 1), ref sum, ref w);

            float mean = sum / w;
            float v = math.lerp(Source[idx], mean, f);
            Density[idx] = (byte)math.clamp(math.round(v), 0f, 255f);
        }

        void Accumulate(int3 q, ref float sum, ref float w)
        {
            if (!Info.InBounds(q)) return; // outside grid = missing sample, so edges do not erode
            sum += Source[Info.Index(q)];
            w += 1f;
        }
    }

    /// <summary>Copy an AABB region of the grid into a same-sized scratch buffer (snapshot for SmoothBrushJob).</summary>
    [BurstCompile]
    public struct CopyRegionJob : IJobParallelFor
    {
        [ReadOnly] public NativeArray<byte> Src;
        [NativeDisableParallelForRestriction] public NativeArray<byte> Dst;
        public VoxelGridInfo Info;
        public int3 AabbMin, AabbExtent;

        public void Execute(int i)
        {
            int3 p = BrushMath.AabbCoord(i, AabbMin, AabbExtent);
            int idx = Info.Index(p);
            Dst[idx] = Src[idx];
        }
    }
}
