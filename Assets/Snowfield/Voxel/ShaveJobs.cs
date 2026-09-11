using Unity.Burst;
using Unity.Collections;
using Unity.Jobs;
using Unity.Mathematics;

namespace Snowfield.Voxel
{
    /// <summary>Everything a shave stamp needs beyond where it lands. Derived from compaction by the caller and sent over the wire as-is.</summary>
    public struct ShaveParams
    {
        /// <summary>How deep below the surface plane the cut goes (voxels).</summary>
        public float DepthVoxels;
        /// <summary>Ramp under the cut floor where removal fades to nothing (voxels). Soft on fluffy snow, thin on packed.</summary>
        public float SoftVoxels;
        /// <summary>Lateral falloff shoulder: tight (~0.85) cuts terminate sharply, wide (~0.25) melt out.</summary>
        public float Shoulder;
        /// <summary>Boundary noise as a fraction of the radius (shrinks the disc only — never reaches outside the brush).</summary>
        public float NoiseAmplitude;
        public uint Seed;
    }

    /// <summary>
    /// Surface-relative removal: a shallow disc oriented by the surface normal at the hit point, brush-radius
    /// wide and <see cref="Params"/>.DepthVoxels deep. Everything above the cut plane inside the disc is removed
    /// outright, so repeated passes take the local high spots first and the surface converges on the stroke
    /// path rather than the tool shape. Bite decides where material isn't; this decides what the surface is like.
    /// </summary>
    [BurstCompile]
    public struct ShaveJob : IJobParallelFor
    {
        [NativeDisableParallelForRestriction] public NativeArray<byte> Density;
        [NativeDisableParallelForRestriction] public NativeArray<byte> Compaction;
        public VoxelGridInfo Info;
        public int3 AabbMin, AabbExtent;
        public float3 CenterVoxel;
        /// <summary>Unit surface normal in voxel space, pointing out of the snow.</summary>
        public float3 NormalVoxel;
        public float RadiusVoxels;
        public ShaveParams Params;

        public void Execute(int i)
        {
            int3 p = BrushMath.AabbCoord(i, AabbMin, AabbExtent);
            float3 d = (float3)p - CenterVoxel;
            float h = math.dot(d, NormalVoxel);                 // signed height above the cut plane
            float floor = -Params.DepthVoxels;
            if (h < floor - Params.SoftVoxels) return;          // below the ramp: untouched
            float lateral = math.length(d - h * NormalVoxel);
            float r = RadiusVoxels;
            if (Params.NoiseAmplitude > 0f)
                r *= 1f - Params.NoiseAmplitude * BrushMath.Hash01(p, Params.Seed); // ragged edge, strictly inside
            float wl = BrushMath.Falloff(lateral / math.max(r, 0.5f), Params.Shoulder);
            if (wl <= 0f) return;
            float wd = h >= floor ? 1f : math.smoothstep(floor - Params.SoftVoxels, floor, h);
            float remove = 255f * wl * wd;
            if (remove <= 0f) return;
            int idx = Info.Index(p);
            float v = math.max(0f, Density[idx] - remove);
            Density[idx] = (byte)math.round(v);
            if (v < 0.5f) { Density[idx] = 0; Compaction[idx] = 0; }
        }
    }

    /// <summary>
    /// Resample the whole grid about a centre by a uniform linear scale (&lt;1 shrinks toward the centre). The
    /// squeeze: the ball visibly compresses, keeps its shape, and its compaction is blended toward fully packed.
    /// Reads density and compaction snapshots so it is order-independent.
    /// </summary>
    [BurstCompile]
    public struct RescaleJob : IJobParallelFor
    {
        [ReadOnly] public NativeArray<byte> Source;
        [ReadOnly] public NativeArray<byte> SourceCompaction;
        [NativeDisableParallelForRestriction] public NativeArray<byte> Density;
        [NativeDisableParallelForRestriction] public NativeArray<byte> Compaction;
        public VoxelGridInfo Info;
        public float3 CenterVoxel;
        /// <summary>1 / linear scale: sampling further out than the voxel sits pulls the snow inward.</summary>
        public float InvScale;
        /// <summary>0 = leave compaction, 1 = fully packed after this pass.</summary>
        public float CompactionBlend;

        public void Execute(int i)
        {
            int size = Info.size;
            int3 p = new int3(i % size, (i / size) % size, i / (size * size));
            float3 sp = CenterVoxel + ((float3)p - CenterVoxel) * InvScale;
            float d = DensitySampler.Trilinear(Source, Info, sp);
            byte v = (byte)math.round(math.clamp(d, 0f, 255f));
            Density[i] = v;
            if (v == 0) { Compaction[i] = 0; return; }
            float c = DensitySampler.Trilinear(SourceCompaction, Info, sp);
            c = math.lerp(c, 255f, math.saturate(CompactionBlend));
            Compaction[i] = (byte)math.round(math.clamp(c, 0f, 255f));
        }
    }
}
