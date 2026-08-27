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
    /// so biting the edge of a sculpture yields a half-sphere, not a ball.
    /// </summary>
    [BurstCompile]
    public struct ExtractChunkJob : IJobParallelFor
    {
        [NativeDisableParallelForRestriction] public NativeArray<byte> Density;
        public VoxelGridInfo Info;
        [ReadOnly] public NativeArray<byte> Source;
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
            float src = DensitySampler.Trilinear(Source, SourceInfo, math.transform(ToSourceVoxel, (float3)p));
            if (src <= 0f) return;
            byte v = (byte)math.round(math.min(src, 255f * f));
            int idx = Info.Index(p);
            if (v > Density[idx]) Density[idx] = v;
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
}
