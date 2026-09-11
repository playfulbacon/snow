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

        /// <summary>
        /// Compaction of a voxel after <paramref name="added"/> density arrives at <paramref name="arrival"/> compaction
        /// on top of <paramref name="existing"/> density at <paramref name="compaction"/>: mass-weighted mixing, so
        /// packed snow buried under powder stays mostly packed and a handful of powder on a packed ball reads fluffy.
        /// </summary>
        public static byte MixCompaction(float existing, float compaction, float added, float arrival)
        {
            float total = existing + added;
            if (total <= 0f) return 0;
            if (existing <= 0f) return (byte)math.clamp(math.round(arrival), 0f, 255f);
            return (byte)math.clamp(math.round((existing * compaction + added * arrival) / total), 0f, 255f);
        }

        /// <summary>Deterministic per-voxel white noise in [0,1): the same on every peer for the same seed.</summary>
        public static float Hash01(int3 p, uint seed)
        {
            uint h = math.hash(new uint4((uint)p.x, (uint)p.y, (uint)p.z, seed));
            return (h & 0xFFFFFF) / 16777216f;
        }
    }

    /// <summary>
    /// Raise (or lower, with negative rate) density inside a sphere, rate-capped. Iterates only the brush AABB.
    /// Arriving snow carries <see cref="ArrivalCompaction"/>; removal leaves the compaction of what stays.
    /// </summary>
    [BurstCompile]
    public struct AddBrushJob : IJobParallelFor
    {
        [NativeDisableParallelForRestriction] public NativeArray<byte> Density;
        [NativeDisableParallelForRestriction] public NativeArray<byte> Compaction;
        public VoxelGridInfo Info;
        public int3 AabbMin, AabbExtent;
        public float3 CenterVoxel;
        public float RadiusVoxels;
        public float RatePerTick;   // density units per tick at core; negative = carve
        public float Shoulder;
        public float ArrivalCompaction;

        public void Execute(int i)
        {
            int3 p = BrushMath.AabbCoord(i, AabbMin, AabbExtent);
            float d = math.distance((float3)p, CenterVoxel) / RadiusVoxels;
            float f = BrushMath.Falloff(d, Shoulder);
            if (f <= 0f) return;
            int idx = Info.Index(p);
            float old = Density[idx];
            float v = math.clamp(old + RatePerTick * f, 0f, 255f);
            Density[idx] = (byte)v;
            if (v <= 0f) Compaction[idx] = 0;
            else if (v > old) Compaction[idx] = BrushMath.MixCompaction(old, Compaction[idx], v - old, ArrivalCompaction);
        }
    }

    /// <summary>
    /// Blur: each voxel in the AABB is lerped toward the weighted mean of its (2k+1)³ neighbourhood.
    /// Reads from <see cref="Source"/> (a snapshot) and writes to <see cref="Density"/>, so it is order-independent.
    /// This is the pat: it also blurs compaction, adds <see cref="CompactionPerTick"/> to snow it touches, and
    /// shrinks the density by <see cref="ShrinkFraction"/> per full compaction span gained — packing = same snow, smaller.
    /// </summary>
    [BurstCompile]
    public struct SmoothBrushJob : IJobParallelFor
    {
        [ReadOnly] public NativeArray<byte> Source;
        [ReadOnly] public NativeArray<byte> SourceCompaction;
        [NativeDisableParallelForRestriction] public NativeArray<byte> Density;
        [NativeDisableParallelForRestriction] public NativeArray<byte> Compaction;
        public VoxelGridInfo Info;
        public int3 AabbMin, AabbExtent;
        public float3 CenterVoxel;
        public float RadiusVoxels;
        public float Strength;
        public float Shoulder;
        /// <summary>Kernel half-width in voxels (1 = 3³ box, 2 = 5³). Wider merges beads into tubes in fewer strokes.</summary>
        public int KernelRadius;
        public float CompactionPerTick;
        public float ShrinkFraction;

        public void Execute(int i)
        {
            int3 p = BrushMath.AabbCoord(i, AabbMin, AabbExtent);
            float d = math.distance((float3)p, CenterVoxel) / RadiusVoxels;
            float f = BrushMath.Falloff(d, Shoulder) * Strength;
            if (f <= 0f) return;

            int idx = Info.Index(p);
            int k = math.max(1, KernelRadius);
            float sum = Source[idx] * 2f, w = 2f;          // centre weighted x2
            float csum = 0f, cw = 0f;
            for (int dz = -k; dz <= k; dz++)
            for (int dy = -k; dy <= k; dy++)
            for (int dx = -k; dx <= k; dx++)
            {
                if (dx == 0 && dy == 0 && dz == 0) continue;
                int3 q = p + new int3(dx, dy, dz);
                if (!Info.InBounds(q)) continue;         // outside grid = missing sample, so edges do not erode
                float dist = math.sqrt((float)(dx * dx + dy * dy + dz * dz));
                float wq = math.max(0f, 1f - (dist - 1f) / (k + 0.5f)); // 1 for face neighbours, tapering out
                if (wq <= 0f) continue;
                int qi = Info.Index(q);
                float sq = Source[qi];
                sum += sq * wq; w += wq;
                if (sq > 0f) { csum += SourceCompaction[qi] * wq * sq; cw += wq * sq; } // compaction is mass-weighted
            }

            float mean = sum / w;
            float v = math.lerp(Source[idx], mean, f);
            if (v <= 0.5f) { Density[idx] = 0; Compaction[idx] = 0; return; }

            float c0 = Source[idx] > 0 ? SourceCompaction[idx] : (cw > 0f ? csum / cw : 0f);
            float cmean = cw > 0f ? csum / cw : c0;
            float c = math.lerp(c0, cmean, f) + CompactionPerTick * f;
            c = math.clamp(c, 0f, 255f);
            float gain = math.max(0f, c - c0);
            v *= 1f - ShrinkFraction * gain / 255f;
            Density[idx] = (byte)math.clamp(math.round(v), 0f, 255f);
            Compaction[idx] = (byte)math.round(c);
        }
    }

    /// <summary>Copy an AABB region of the grid into a same-sized scratch buffer (snapshot for SmoothBrushJob).</summary>
    [BurstCompile]
    public struct CopyRegionJob : IJobParallelFor
    {
        [ReadOnly] public NativeArray<byte> Src;
        [NativeDisableParallelForRestriction] public NativeArray<byte> Dst;
        [ReadOnly] public NativeArray<byte> SrcB;
        [NativeDisableParallelForRestriction] public NativeArray<byte> DstB;
        public VoxelGridInfo Info;
        public int3 AabbMin, AabbExtent;

        public void Execute(int i)
        {
            int3 p = BrushMath.AabbCoord(i, AabbMin, AabbExtent);
            int idx = Info.Index(p);
            Dst[idx] = Src[idx];
            DstB[idx] = SrcB[idx];
        }
    }

    /// <summary>Total density (in full-voxel units) over an AABB. Run before and after a stamp to measure what it removed.</summary>
    [BurstCompile]
    public struct RegionMassJob : IJob
    {
        [ReadOnly] public NativeArray<byte> Density;
        public VoxelGridInfo Info;
        public int3 AabbMin, AabbExtent;
        public NativeArray<float> Result;

        public void Execute()
        {
            float sum = 0f;
            int n = AabbExtent.x * AabbExtent.y * AabbExtent.z;
            for (int i = 0; i < n; i++) sum += Density[Info.Index(BrushMath.AabbCoord(i, AabbMin, AabbExtent))];
            Result[0] = sum / 255f;
        }
    }
}
