using Unity.Burst;
using Unity.Collections;
using Unity.Jobs;
using Unity.Mathematics;

namespace Snowfield.Voxel
{
    /// <summary>
    /// Copy the snow that a brush sphere actually overlaps out of <see cref="Source"/> and into this grid:
    /// density = min(source density, 255 * falloff). Paired with a full-strength negative brush on the source,
    /// this makes the chunk in your hands exactly the snow that left the sculpture — including the empty parts,
    /// so biting the edge of a sculpture yields a half-sphere, not a ball. Compaction comes along with the snow.
    /// </summary>
    [BurstCompile]
    public struct ExtractChunkJob : IJobParallelFor
    {
        [NativeDisableParallelForRestriction] public NativeArray<byte> Density;
        [NativeDisableParallelForRestriction] public NativeArray<byte> Compaction;
        public VoxelGridInfo Info;
        [ReadOnly] public NativeArray<byte> Source;
        [ReadOnly] public NativeArray<byte> SourceCompaction;
        public VoxelGridInfo SourceInfo;
        public int3 AabbMin, AabbExtent;
        /// <summary>This grid's voxel space → the source grid's voxel space.</summary>
        public float4x4 ToSourceVoxel;
        public float3 CenterVoxel;
        public float RadiusVoxels;
        public float Shoulder;

        public void Execute(int i)
        {
            int3 p = BrushMath.AabbCoord(i, AabbMin, AabbExtent);
            float d = math.distance((float3)p, CenterVoxel) / RadiusVoxels;
            float f = BrushMath.Falloff(d, Shoulder);
            if (f <= 0f) return;
            float3 sp = math.transform(ToSourceVoxel, (float3)p);
            float src = DensitySampler.Trilinear(Source, SourceInfo, sp);
            if (src <= 0f) return;
            byte v = (byte)math.round(math.min(src, 255f * f));
            int idx = Info.Index(p);
            byte old = Density[idx];
            if (v <= old) return;
            float sc = DensitySampler.Trilinear(SourceCompaction, SourceInfo, sp);
            Density[idx] = v;
            Compaction[idx] = BrushMath.MixCompaction(old, Compaction[idx], v - old, sc);
        }
    }

    /// <summary>Total density in a grid, as a fraction of full voxels (multiply by voxel volume for cubic metres).</summary>
    [BurstCompile]
    public struct DensitySumJob : IJob
    {
        [ReadOnly] public NativeArray<byte> Density;
        public NativeArray<float> Result;

        public void Execute()
        {
            float sum = 0f;
            for (int i = 0; i < Density.Length; i++) sum += Density[i];
            Result[0] = sum / 255f;
        }
    }

    /// <summary>Mass-weighted mean compaction of a grid (0-255); 0 for an empty grid. Result[0] = mean, Result[1] = mass.</summary>
    [BurstCompile]
    public struct MeanCompactionJob : IJob
    {
        [ReadOnly] public NativeArray<byte> Density;
        [ReadOnly] public NativeArray<byte> Compaction;
        public NativeArray<float> Result;

        public void Execute()
        {
            float mass = 0f, weighted = 0f;
            for (int i = 0; i < Density.Length; i++)
            {
                float d = Density[i];
                if (d <= 0f) continue;
                mass += d;
                weighted += d * Compaction[i];
            }
            Result[0] = mass > 0f ? weighted / mass : 0f;
            Result[1] = mass / 255f;
        }
    }
}
