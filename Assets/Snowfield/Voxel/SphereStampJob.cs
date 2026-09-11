using Unity.Burst;
using Unity.Collections;
using Unity.Jobs;
using Unity.Mathematics;

namespace Snowfield.Voxel
{
    /// <summary>
    /// One-shot: density = max(existing, 255 * falloff) inside a sphere.
    /// Used for the starter mound (hemisphere via ClipBelowY) and snowball growth. Whatever the stamp raises
    /// arrives at <see cref="StampCompaction"/> (rolled snow is born packed; a handful is born fluffy).
    /// </summary>
    [BurstCompile]
    public struct SphereStampJob : IJobParallelFor
    {
        [NativeDisableParallelForRestriction] public NativeArray<byte> Density;
        [NativeDisableParallelForRestriction] public NativeArray<byte> Compaction;
        public VoxelGridInfo Info;
        public int3 AabbMin, AabbExtent;
        public float3 CenterVoxel;
        public float RadiusVoxels;
        public float Shoulder;
        /// <summary>Voxels with y below this are left untouched (use a large negative for none).</summary>
        public float ClipBelowY;
        public float StampCompaction;

        public void Execute(int i)
        {
            int3 p = BrushMath.AabbCoord(i, AabbMin, AabbExtent);
            if (p.y < ClipBelowY) return;
            float d = math.distance((float3)p, CenterVoxel) / RadiusVoxels;
            float f = BrushMath.Falloff(d, Shoulder);
            if (f <= 0f) return;
            int idx = Info.Index(p);
            byte v = (byte)math.round(255f * f);
            byte old = Density[idx];
            if (v <= old) return;
            Density[idx] = v;
            Compaction[idx] = BrushMath.MixCompaction(old, Compaction[idx], v - old, StampCompaction);
        }
    }
}
