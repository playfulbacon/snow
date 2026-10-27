using Unity.Burst;
using Unity.Collections;
using Unity.Jobs;
using Unity.Mathematics;

namespace Snowfield.Voxel
{
    /// <summary>
    /// Chop the grid along a plane: everything on the normal's side is gone, the rest is untouched. Unlike the
    /// brushes this has no radius and no falloff — it is a single committed cut, not a stroke, so it runs over the
    /// whole grid once rather than an AABB under a cursor.
    ///
    /// The feather band is what makes the cut look like a cut. A hard "zero everything above the plane" leaves
    /// marching cubes interpolating between a full voxel and an empty one, so the visible surface lands on a voxel
    /// boundary and the cut face comes out as a staircase at whatever angle you chose. Ramping density to zero
    /// across the last voxel instead puts the iso-surface exactly on the plane, at any angle.
    /// </summary>
    [BurstCompile]
    public struct PlaneCutJob : IJobParallelFor
    {
        [NativeDisableParallelForRestriction] public NativeArray<byte> Density;
        public VoxelGridInfo Info;
        /// <summary>A point on the cut plane, in voxel space.</summary>
        public float3 PointVoxel;
        /// <summary>Unit plane normal in voxel space. Snow on this side is removed.</summary>
        public float3 NormalVoxel;
        /// <summary>Unit direction of the cut line in voxel space. The cut is bounded along this axis.</summary>
        public float3 LineVoxel;
        /// <summary>Half the cut's length along the line, in voxels. Snow past either end is left alone.</summary>
        public float HalfLengthVoxels;
        /// <summary>Width of the ramp below the plane, in voxels. 1 is plenty.</summary>
        public float FeatherVoxels;

        public void Execute(int i)
        {
            int size = Info.size;
            int t = i / size;
            int3 p = new int3(i - t * size, t % size, t / size);
            float h = math.dot((float3)p - PointVoxel, NormalVoxel);

            float feather = math.max(FeatherVoxels, 0.01f);
            if (h <= -feather) return; // behind the plane: kept

            // The cut is a half-space, so it takes everything on the far side of the plane, but only within
            // the span of the line you were shown. Past either end the snow stands.
            float along = math.abs(math.dot((float3)p - PointVoxel, LineVoxel));
            float halfLen = math.max(HalfLengthVoxels, 0f);
            if (along >= halfLen + feather) return;

            // Each bound contributes a ramp; the one that keeps more snow wins, which rounds the corners at
            // the ends of the cut instead of leaving a voxel staircase there.
            float ceilPlane = h >= 0f ? 0f : 255f * (-h / feather);
            float ceilEnd = along <= halfLen ? 0f : 255f * ((along - halfLen) / feather);
            float ceiling = math.max(ceilPlane, ceilEnd);
            if (Density[i] <= ceiling) return;
            Density[i] = (byte)math.round(ceiling);
        }
    }
}
